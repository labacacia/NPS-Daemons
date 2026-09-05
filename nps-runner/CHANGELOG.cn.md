[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— `nps-runner`

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。Tag 跟随 NPS 套件统一 SemVer。

---

## [未发布]

### 修复

- 将 runner 的机器可读 conformance manifest 打包到 standalone 测试输出，
  使同一能力契约可在 monorepo 与物化布局中执行。
- 用持久化 SQLite store 替换进程内任务租约与终态去重 map。指向同一状态文件的
  进程现在会原子 claim、跨重启保留终态节点、回收过期租约，并以每次启动的新
  process-instance identity 围栏旧进程或重启前进程。
- claim 冲突的 inbox 消息保持未 ack；先原子提交终态再 ack；租约丢失时取消
  worker，且不写终态、不 ack、不发送完成通知。确定性测试覆盖完整旧属主路径。
- 新增机器可读 Node L3 implementation manifest：三个严格 case 已验证、五个为
  部分验证，TaskFrame DAG/Saga 部署 case 明确未执行，因此不声明完整 L3 认证。
- 为该 manifest 增加可运行门禁元数据和精确十项 case 覆盖检查。
- 公开 resolver 的依赖注入构造器以修复生产启动；同时修复两条 Docker 构建路径，
  改用基础镜像自带的非 root `app` 账户、复制完整独立源码树，并配置持久化状态卷。
- 修正仍把当前 runner 描述为骨架的陈旧文本，同时把 alpha.3/alpha.4 记录保留为
  明确历史。

## [1.0.0-alpha.18] —— 2026-08-15

### 变更

- 将包元数据、runtime banner、publish-overlay SDK 引用与同步 daemon 列车对齐到 alpha.18 协议/SDK 候选版。

## [1.0.0-alpha.17] —— 2026-08-02

### 变更

- 对齐 package metadata、运行时版本 banner 与 publish overlay SDK 依赖，准备服务端能力对齐版本的 alpha.17 daemon 候选。
- 完成 CR-0007 runtime gate：加入 fail-closed portable OCI SpawnSpec 校验、
  inline / HTTPS / NWP 引用解析、可配置 OCI runtime 执行、确定性的超时优先级，
  并保留旧版直接子进程兼容路径。
- 加入 daemon 自有的 NOP 0.9 一致性覆盖，验证租约、SpawnSpec 解析、
  worker 生命周期与去重语义。
- 加固远程 SpawnSpec 拉取的 SSRF 与 DNS rebinding 防护：拒绝非公网 DNS
  结果，仅连接已校验地址并保留 TLS 主机名校验，逐跳重验重定向，并限制
  重定向次数、响应大小与请求时间。
- Worker 丢失进程内租约后不再写入终态或 ack inbox，使消息可由后续本地
  claim 处理。

## [1.0.0-alpha.16] —— 2026-07-23

### 变更

- 套件级 alpha.16 同步：在 alpha.15 已发布后，对齐包元数据、当前 README / 版本 banner、分发源树以及 release-prep 说明。
- 承载源事实树中的 nps-ingress 与 nps-runner 分发测试隔离修复。

## [1.0.0-alpha.15] —— 2026-06-28

### 变更

- 套件级 alpha.15 同步：对齐包元数据、当前 README / 版本 banner、分发源树以及 release-prep 说明到 NPS-Dev。
- 承载源事实树中的 NCP Tier-3 BinaryVector、入站 NWP Bridge server 加固、NIP canonical trust/revoke，以及 NDP discovery canonical-form 对齐。

## [1.0.0-alpha.14] —— 2026-06-26

- 套件版本同步到 1.0.0-alpha.14。

## [1.0.0-alpha.14] —— 2026-06-13

### 新增

- **NPS-CR-0007 任务 claim 决策逻辑**（`LeaseStore.cs`）。Inbox 调度在 spawn
  前按 `task_id` 获取进程内租约；本地冲突返回 `NOP-CLAIM-CONFLICT`。租约限制在
  `[10, 600]s`、在本进程内过期，携带
  `dedup_key = sha256(task_id ‖ dag_hash)`，并在 worker 完成时释放。历史更正：
  该内存实现当时不能协调多个副本，也不能在进程崩溃后保留租约或终态去重；
  持久化/共享语义仍是后续工作（CR-0007 §10 OQ-1）。

## [1.0.0-alpha.7] —— 2026-05-18

### 跟随套件

- 跟随 NPS 套件 `v1.0.0-alpha.7`。项目版本、publish-overlay 版本以及
  所有 `LabAcacia.NPS.*` PackageReference 已统一对齐到 alpha.7 发布线。

---

## [1.0.0-alpha.6] —— 2026-05-12

### 跟随套件

- 跟随 NPS 套件 `v1.0.0-alpha.6`。项目版本、publish-overlay 版本以及
  所有 `LabAcacia.NPS.*` PackageReference 已统一对齐到 alpha.6 发布线。

---

## [1.0.0-alpha.5] —— 2026-05-01

### 新增

- **Inbox watcher + worker spawn** —— 完整 L3 FaaS runtime，替换
  alpha.3/alpha.4 的心跳骨架：
  - 启动时向本机 `npsd` 自注册（`POST /v1/agents`，幂等 —— 409 返回
    现有 NID）；失败时以指数退避最多重试 20 次。
  - 以可配置间隔（`NPS_RUNNER_POLL_INTERVAL_MS`，默认 1 秒）对
    runner inbox 做 long-poll（`GET /v1/inbox/{nid}?wait=N&batch=B`）。
  - 反序列化 JSON spawn-spec 消息，字段详见 README。
  - 以指定 `command` / `args` / `env` / `work_dir` 拉起 worker 子进程。
  - `stdout` + `stderr` 捕获至
    `NPS_RUNNER_LOG_DIR/{task_id}.log`，带 `[stdout]`/`[stderr]` 前缀。
  - 监控循环（5 秒 tick）强制执行 `idle_timeout_seconds`（自上次输出
    以来的静默时间）和 `max_runtime_seconds`（硬性墙钟上限，默认 4 小时）。
  - Worker 退出时：ack inbox 消息；若设置了 `reply_to`，向该 NID POST
    JSON 完成通知。
  - 并发上限可配置（`NPS_RUNNER_MAX_CONCURRENT_WORKERS`，默认 8）——
    到达上限时收到的消息保持未 ack，下次轮询重新出现。

---

## [1.0.0-alpha.4] —— 2026-04-30

### 跟随套件的协议变更

- `LabAcacia.NPS.*` NuGet 依赖升至 `v1.0.0-alpha.4`。自 alpha.3 无功能
  变更 —— `nps-runner` 仍是 Generic Host + 30 秒心跳的骨架。
- Inbox 监听、`spawn_spec_ref` 解析、worker 子进程生命周期仍推迟到
  L3 阶段（alpha.5+）。

---

## [1.0.0-alpha.3] —— 2026-04-26

### 新增

- 首次发布。NPS 套件的 Layer-1 任务调度 / FaaS runtime。
- Phase 1 骨架：Generic Host 脚手架 + 30 秒心跳，让部署面先稳定，
  运维可以把它接进 systemd / docker compose 而不必等完整实现。
- 多阶段 Docker 镜像（非 root `npsrunner` 用户，不暴露端口 ——
  从同主机的 `npsd` 拉任务）。

### 推迟到 alpha.5+（L3 阶段）

- Inbox 监听：轮询本机 `npsd` 找发往 ephemeral 模式 NID 的消息。
- `spawn_spec_ref` 解析：拿到 ephemeral NID 后，从源 Anchor / Memory
  Node 取回 spawn spec。
- worker 子进程生命周期：按 spawn spec 拉起、监控、完成或空闲超时后回收。

---

[1.0.0-alpha.5]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
