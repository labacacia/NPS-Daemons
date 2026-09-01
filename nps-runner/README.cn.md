[English Version](./README.md) | 中文版

# `nps-runner` —— NPS Daemon（任务调度器 / FaaS runtime）

> 监控本机 [`npsd`](../npsd/) inbox 中的 JSON spawn-spec 消息，按需拉起
> worker 子进程，并管理其完整生命周期（stdout/stderr 捕获、空闲/最大运行
> 超时、并发上限、完成通知）。完整六-daemon 拓扑见
> [`docs/daemons/architecture.cn.md`](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.cn.md)。

## 状态 —— alpha.18 候选

Inbox 监听、CR-0007 任务租约、portable OCI SpawnSpec 执行与旧版直接子进程
兼容路径均已实现。具体部署的 Layer-3 认证仍需单独执行一致性验证。

## 快速上手

```bash
# 从 monorepo 根目录
dotnet run --project tools/daemons/nps-runner/NpsRunner.csproj
# 日志: "nps-runner ready — NID=<nid>  npsd=http://127.0.0.1:17433 ..."
```

### Docker

```bash
docker build -f tools/daemons/nps-runner/Dockerfile -t labacacia/nps-runner:1.0.0-alpha.18 .
docker run --rm \
  -e NPSD_URL=http://127.0.0.1:17433 \
  -e NPS_RUNNER_LOG_DIR=/var/log/nps-runner \
  labacacia/nps-runner:1.0.0-alpha.18
```

## 配置

所有配置均通过环境变量设置。

| 变量 | 默认值 | 用途 |
|---|---|---|
| `NPSD_URL` | `http://127.0.0.1:17433` | npsd 基础 URL |
| `NPS_RUNNER_AGENT_ID` | `nps-runner` | 自注册时使用的 sub-NID 标识符 |
| `NPS_RUNNER_POLL_INTERVAL_MS` | `1000` | Inbox 轮询间隔（同时设定长轮询 `wait` 窗口） |
| `NPS_RUNNER_MAX_CONCURRENT_WORKERS` | `8` | 同时运行的 worker 进程上限 |
| `NPS_RUNNER_LOG_DIR` | `/tmp/nps-runner-logs` | 每个 worker 的 `{task_id}.log` 目录 |
| `NPS_RUNNER_OCI_RUNTIME` | `docker` | 执行 portable SpawnSpec 的 OCI 兼容 CLI |
| `NPS_REGISTRY_URL` | `http://127.0.0.1:17436` | 解析 `nwp://` SpawnSpec 引用的 NDP registry |

## 工作原理

### 1. 自注册

启动时，nps-runner 向本机 npsd 调用 `POST /v1/agents`，传入
`identifier = NPS_RUNNER_AGENT_ID` 和 `capabilities = ["spawn"]`。
该调用是幂等的 —— 返回 409 时直接读取现有 NID。
分配的 NID 在启动日志中输出，向该 NID 发送任务消息即可。

### 2. Spawn-spec 消息格式

向 runner 的 inbox 投递消息：

```
POST /v1/inbox/{runner-nid}
Content-Type: application/json
X-Nps-Inbox-Ttl-Seconds: 3600
```

Portable OCI 消息体：

```json
{
  "task_id": "abc123",
  "reply_to": "<完成通知目标 NID>",
  "image": "ghcr.io/example/worker@sha256:0123456789abcdef",
  "command": ["worker", "--task", "abc123"],
  "env": {
    "NPS_TASK_ID": "abc123"
  },
  "resource_limits": {
    "cpu": "500m",
    "memory": "512Mi",
    "cgn_budget": 10000
  },
  "idle_timeout_seconds": 600,
  "max_runtime_seconds": 3600
}
```

| 字段 | 类型 | 必填 | 描述 |
|---|---|---|---|
| `task_id` | string | 否 | 调用方自定义 ID；缺省时自动生成（UUID） |
| `reply_to` | string | 否 | worker 退出后接收完成通知的 NID |
| `image` | string | **是** | OCI 镜像引用；建议使用 digest 固定版本 |
| `command` | string[] | 否 | 追加在镜像之后的 OCI entrypoint / 参数覆盖 |
| `env` | object | 否 | 传入容器的环境变量 |
| `resource_limits` | object | 否 | 可选的 `cpu`、`memory` 与 `cgn_budget` 限制 |
| `idle_timeout_seconds` | int | 否 | stdout/stderr 无输出超过 N 秒后 kill worker |
| `max_runtime_seconds` | int | 否 | 硬性墙钟上限；默认上限为 4 小时 |

Inbox 消息体也可以只包含 `spawn_spec_ref`。支持 inline base64url 文档
（`spawnspec:<payload>`）、HTTPS URL，或经 `NPS_REGISTRY_URL` 解析的
`nwp://` URL。解析后的文档上限为 64 KiB，并使用同一套 fail-closed
校验器。远程拉取必须使用 HTTPS，拒绝 user-info 与非公网 DNS 结果，仅连接
已校验地址并保留 TLS 主机名校验；每次重定向都会重新校验，最多五跳。

alpha.17 之前的直接子进程格式继续作为兼容路径：

```json
{
  "task_id": "abc123",
  "command": "claude",
  "args": ["remote-control", "--name", "worker-abc"],
  "work_dir": "/home/wind/project",
  "env": {"CLAUDE_CODE_SANDBOXED": "1"},
  "max_runtime_seconds": 3600
}
```

在该旧格式中，`command` 是必填字符串，`args` 是字符串数组，
`work_dir` 缺省为 runner 当前工作目录。

### 3. Worker 生命周期

1. 收到消息 → 解析引用并校验 spawn spec → 检查并发上限。
2. 达到上限：消息保持未 ack 状态，下次轮询周期重新出现。
3. 否则：获取任务租约并启动 OCI 容器或旧版直接子进程。
4. stdout + stderr 捕获至 `NPS_RUNNER_LOG_DIR/{task_id}.log`。
5. 监控循环（5 秒 tick）续租，并检查空闲超时和最大运行时限。
6. 退出（任何原因）：ack inbox 消息；若设置了 `reply_to`，向该 NID 投递完成通知（JSON）。

### 4. 完成通知

worker 退出且设置了 `reply_to` 时，nps-runner 投递：

```json
{
  "task_id": "abc123",
  "exit_code": 0,
  "killed_reason": null,
  "log_path": "/tmp/nps-runner-logs/abc123.log",
  "started_at": "2026-05-03T10:00:00.000Z",
  "finished_at": "2026-05-03T10:05:00.000Z"
}
```

`killed_reason` 取值：`"idle_timeout"`、`"max_runtime"`、`"shutdown"`、
`"exception"`，或 `null`（正常退出）。

## 为什么独立 daemon（而不是 `npsd` 的一部分）

详见 [架构 §1](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.cn.md)：协议层与 worker
调度器在资源画像、故障隔离、信任边界三方面差异巨大 —— worker crash 不该
把 NCP 层带下去；调度器要执行用户提供的命令，协议层不该有这个权限面。

## 验证

独立 daemon bundle 在 `spec/conformance/nop` 下携带共用 NOP fixtures。
运行：

```bash
dotnet test nps-runner/tests/NpsRunner.Tests.csproj -c Release
```

## 许可证

Apache-2.0 —— 见仓库根 `LICENSE`。
