[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— `npsd`

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。Tag 跟随 NPS 套件统一 SemVer。

---

## [未发布] —— alpha.19 技债收口

### 新增

- 新增连接层单端口分流：现有 HTTP 控制 API 与原生 NCP 共用配置的 `17433` 端点，且不会互相消费字节。
- 新增 npsd local-dev 原生 NCP 会话路径：有界 RFC-0001 preamble/Hello 读取、确定性 Hello/Caps 协商、协商后帧上限、canonical AnchorFrame ACK/缓存、协议 ErrorFrame 后关闭及准入前静默关闭。
- 新增真实 socket daemon 测试与严格 Node L1 实现清单。NCP 2 项 verified、2 项 partial；明确不宣称完整 L1 认证。
- 将进程内 inbox 替换为持久 SQLite 状态；未投递 ActionFrame、优先级顺序、绝对 TTL 与 ack 均跨宿主重启保留，并明确拒绝本 L1 profile 的 resident/hybrid push 可选能力。
- 为 npsd 托管 agent 新增发布者签名的 ephemeral AnnounceFrame 广播，包括持久单调 graph sequence、受限 heartbeat/TTL、优雅下线信号、registry 不可用时的重试隔离，以及可拒绝签名正文篡改的合规测试。
- 新增有界 sub-NID 续租，以原子方式替换 serial/有效期，并对过早、撤销、过期和并发变更 fail closed。托管 agent 私钥仅以绑定主机 root 的 AES-256-GCM envelope 保留，使广播签名可跨重启连续；BYO key 始终不进入 npsd，也不会自动广播。
- 修复两条 Docker 构建路径：使用 .NET 基础镜像自带的非 root `app` 身份，把此前漏掉的 observability 项目/源码纳入 monorepo/overlay context，让 standalone materialization 携带 native/conformance 源码，并用 npsd 内建 `--healthcheck` 替换镜像中不存在的 `wget` 探针。

## [1.0.0-alpha.18] —— 2026-08-15

### 变更

- 将包元数据、runtime banner、publish-overlay SDK 引用与同步 daemon 列车对齐到 alpha.18 协议/SDK 候选版。

## [1.0.0-alpha.17] —— 2026-08-02

### 变更

- 对齐 package metadata、运行时版本 banner 与 publish overlay SDK 依赖，准备服务端能力对齐版本的 alpha.17 daemon 候选。
- 升级 `Microsoft.Data.Sqlite` 并固定 `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12，移除存在漏洞的 SQLite runtime bundle。

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

- **`AnchorNodeMiddleware`** —— `topology:read` 能力门控（NPS-CR-0001）：
  不带 `topology:read` 的调用方访问 `topology.snapshot` /
  `topology.stream` 端点时收到 HTTP 403 /
  `NWP-UNAUTHORIZED-CAPABILITY`。每个拓扑事件 payload 新增
  `cgn_est` 整型字段。
- **`NPS-SERVER-UNSUPPORTED`（HTTP 501）** —— 未知保留 NWP 帧类型现在
  返回 `501 Not Implemented` + 错误码 `NWP-RESERVED-TYPE-UNSUPPORTED`，
  不再返回通用 4xx。
- **AssuranceLevel 空字符串修复** —— `LabAcacia.NPS.NIP`
  `1.0.0-alpha.5` 将空的 `assurance_level` wire 值等同于字段缺失
  （`anonymous`）。
- **NDP DNS TXT 回退解析** —— `LabAcacia.NPS.NDP` `1.0.0-alpha.5` 新增
  `ResolveViaDns`，注入式 `DnsTxtLookup` 接口，默认使用系统 DNS 解析器。
- 17 个集成测试覆盖更新后的拓扑行为和 501 响应映射。

---

## [1.0.0-alpha.4] —— 2026-04-30

### 新增

- **Sub-NID 签发（L1+）** —— `POST/GET /v1/agents`、
  `GET/POST /v1/agents/{nid}`、`POST /v1/agents/{nid}/revoke`。
  IdentFrame 由主机 root key 签名（`LabAcacia.NPS.NIP` 的
  `NipSigner` 对 canonical JSON 做 Ed25519 签名）。SQLite 存
  `${NPSD_DATA_DIR}/sub-nids.sqlite`。调用方可以 BYO `agent_pub_key`
  （npsd 看不到私钥），或不传（npsd 生成一对 Ed25519，仅一次返回私钥）。
- **Per-NID inbox（L1+）** —— `POST /v1/inbox/{nid}`、
  `GET /v1/inbox/{nid}?wait=N&batch=B`（long-poll）、
  `DELETE /v1/inbox/{nid}/{message_id}`（ack）、
  `GET /v1/inbox/{nid}/depth`。内存队列；支持优先级、TTL、
  per-NID 深度上限、payload 大小上限，以及 404/403/413/429
  的友好错误映射。
- 通过 `NPSD_HOST_NID_PREFIX`、`NPSD_SUB_NID_VALIDITY_DAYS`、
  `NPSD_MAX_INBOX_DEPTH_PER_NID`、`NPSD_MAX_INBOX_MESSAGE_BYTES`、
  `NPSD_MAX_INBOX_WAIT_SECONDS` 调节运行参数。
- 17 个集成测试位于 `NPS.Tests/Daemons/Npsd/`，覆盖 issue、list、get、
  revoke、inbox 投递、long-poll、ack、depth、priority 排序、
  oversize-payload 拒绝、已 revoke NID 投递拒绝。
- daemon 自身 `GET /.nwm` 现在带路由表（route catalog），描述 sub-NID
  + inbox 入口。

### 跟随套件的协议变更

- `LabAcacia.NPS.*` NuGet 依赖升至 `v1.0.0-alpha.4`，库层带来
  **NPS-RFC-0001 Phase 2**（NCP preamble 帮助函数）。线缆传输仍为
  HTTP-only；`npsd` 自身的原生 preamble 路由仍是后续添加项。

### 推迟到 alpha.5+

- `npsd` 自身的 NCP 原生模式 wire transport（库层已有 preamble
  runtime，`npsd` 在线缆上仍是 HTTP-only）。
- Inbox 持久化（LMDB / SQLite）—— alpha.4 的 inbox 仍是内存。
- 向本地 NDP registry 广播 AnnounceFrame。
- Sub-NID 续签（现在只能 revoke + reissue）。

---

## [1.0.0-alpha.3] —— 2026-04-26

### 新增

- 首次发布。NPS 套件的 Layer-1 主机本地 daemon。
- 默认绑定 `127.0.0.1:17433`；可用 `NPSD_HOST` / `NPSD_PORT` 覆盖。
- 首次启动生成 root Ed25519 keypair，落盘到
  `${NPSD_DATA_DIR:-~/.local/share/npsd}/root.ed25519.pkcs8`，
  POSIX 文件权限 `0600` —— 满足 NPS-Node Profile L1 合规用例
  `TC-N1-NIP-01 — Root keypair generation and permission`。
- 暴露 `GET /health`（Docker `HEALTHCHECK` / systemd 活性探针目标）。
- 暴露 `GET /.nwm`，返回 daemon 自身的最小 Neural Web Manifest
  （memory-node 形态、anonymous-auth、无 actions）。
- 多阶段 Docker 镜像（非 root `npsd` 用户、`/data` 卷）。

### 推迟到 alpha.4 / alpha.5

- NCP 原生模式 wire transport（alpha.3 仅 HTTP；NPS-RFC-0001 原生
  preamble 在 alpha.4 落地）。
- Per-NID inbox 持久化 + 向常驻 agent 推送。
- 给本地 agent 签发 sub-NID。
- 向本地 NDP registry 广播 AnnounceFrame。

---

[1.0.0-alpha.5]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
