[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— `nps-runner`

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。Tag 跟随 NPS 套件统一 SemVer。

---

## [1.0.0-alpha.17] —— 未发布

### 变更

- 对齐 package metadata、运行时版本 banner 与 publish overlay SDK 依赖，准备服务端能力对齐版本的 alpha.17 daemon 候选。

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
