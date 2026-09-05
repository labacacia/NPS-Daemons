[English Version](./README.md) | 中文版

# `npsd` —— NPS Daemon（Layer 1，主机本地）

> NPS 主机本地 daemon 的参考实现。监听套件统一端口 `17433`，
> 持有主机的 root Ed25519 keypair，按需为本机 agent 签发 sub-NID，
> 并为 ephemeral agent 提供持久 per-NID inbox 与带签名的 NDP 存活广播。
> 完整六 daemon 拓扑见
> [`docs/daemons/architecture.cn.md`](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.cn.md)。

## 这个二进制做什么

- **统一监听**：默认在 `127.0.0.1:17433` 同时承载 HTTP 与原生 NCP。连接层仅消费精确的 `NPS/1.0\n` 原生前导，HTTP 字节不被消费并原样交给 Kestrel。可用 `NPSD_HOST` / `NPSD_PORT` 覆盖。
- **原生 NCP local-dev profile**：执行有界 preamble + Hello/Caps 协商，约束协商后的编码与 payload 上限，ACK 并缓存 canonical AnchorFrame；已准入连接发生协议错误时先发 ErrorFrame 再关闭。明文只适用于本机 profile；公网 TLS/mTLS 终止仍由 `nps-ingress` 负责。
- **Root keypair**：首次启动生成 Ed25519 root keypair；落盘到 `${NPSD_DATA_DIR:-~/.local/share/npsd}/root.ed25519.pkcs8`，POSIX 文件权限 `0600`（满足 NPS-Node Profile L1 合规用例 `TC-N1-NIP-01`）。
- **Sub-NID 签发与续租**：基于主机 root NID 生成、持久化并续租短期 sub-NID。承载 IdentFrame 由 root key 签名。npsd 生成的 agent 私钥以主机 root 派生密钥做 AES-256-GCM 加密后落盘；BYO 私钥永不进入 npsd。SQLite 存于 `${NPSD_DATA_DIR}/sub-nids.sqlite`。
- **NDP 在线状态**：为每个有效的 npsd 托管 agent 周期性向本地 registry POST 发布者签名的 `AnnounceFrame`。帧携带 `activation_mode="ephemeral"`、跨重启单调递增的 `graph_seq`、受限 TTL/heartbeat，并在优雅停机时发送 TTL 0。BYO-key agent 不会自动广播，因为 npsd 无权代其签名。
- **Per-NID inbox**：按 sub-NID 分桶的持久 SQLite 队列，支持 long-poll、显式持久 ack、depth、priority、绝对 TTL 和原子的 per-NID 深度上限。未投递 payload 在 daemon 重启后仍保留于 `${NPSD_DATA_DIR}/inbox.sqlite`。
- **`GET /.nwm`** —— daemon 自身的 Neural Web Manifest，声明上面这些路由。
- **健康探针**：`GET /health` 暴露 npsd 诊断信息；Docker 调用内建 `npsd --healthcheck` 检查 `GET /healthz`，不依赖镜像中的额外工具。

## alpha.4 已落地的部分

- NCP 原生模式连接前导字节（`NPS/1.0\n`）runtime —— NPS-RFC-0001 Phase 2。
- Sub-NID 签发：`npsd` 为本机 agent 签发子 NID。
- Per-NID inbox 队列：ephemeral agent 通过 npsd 拉取并显式确认消息；未投递记录跨重启保留。

## alpha.19 技债收口候选

- 原生 NCP 已从“仅库层 helper”变成 npsd 的真实 wire path；HTTP 控制 API 与原生会话共用配置的统一端口。
- inbox 已改为持久 SQLite 队列。宿主重启测试验证 ActionFrame 逐字节恢复、ack 持久化、绝对 TTL 清理、优先级顺序和 FIFO pull/ack 排空。
- 托管 sub-NID 现可在限定的到期前窗口原位续租：NID、公钥、capabilities、scope、metadata 保持不变，serial 与有效期原子轮换；撤销、过期或过早续租均 fail closed。
- 有效托管 agent 现向 `nps-registry` 发出签名的 ephemeral AnnounceFrame；广播私钥和 per-NID graph sequence 均跨重启保持，优雅停机发送 TTL 0。BYO key 因 npsd 不持有私钥而明确不自动广播。
- 升级说明：本 slice 之前签发的 sub-NID 在 npsd 中没有可恢复私钥，会归入 caller-managed/legacy 计数；需要自动广播时应一次性重新签发。
- 明确不宣称 resident/hybrid push：L1 允许拒绝该可选路径，本 profile 只声明 `ephemeral` HTTP pull + 显式 ack。
- 真实 socket 测试覆盖 HTTP/native 共存、Hello/Caps、Anchor ACK/缓存与 digest 拒绝、版本/编码不兼容 ErrorFrame、loopback 默认值及 RFC-0001 静默关闭时限。
- [`conformance/NPS-NODE-L1-MANIFEST.json`](./conformance/NPS-NODE-L1-MANIFEST.json) 记录精确 NCP、NDP、NWP 用例证据。其余 NIP/NWP 与进程级用例未收口前，不宣称完整 Node L1 认证。

## alpha.11+ 还没做的部分

按 `docs/daemons/architecture.md` 的逐 daemon 阶段表跟踪：

- resident/hybrid push（L1 可选且本 profile 不声明；留给后续 L2+ 设计）。
- 完整 NPS-Node L1 认证；本次只验证清单中声明的 NCP/NDP/NWP 子集。

## 快速开始

### 本地

```bash
dotnet run --project tools/daemons/npsd/Npsd.csproj
# → npsd starting; root NID host fingerprint = <16-hex>; bind = 127.0.0.1:17433

curl -s http://127.0.0.1:17433/health | jq
curl -s http://127.0.0.1:17433/.nwm   | jq
```

### Docker

```bash
docker build -f tools/daemons/npsd/Dockerfile -t labacacia/npsd:1.0.0-alpha.18 .
docker run --rm -p 17433:17433 \
  -v npsd-data:/data \
  labacacia/npsd:1.0.0-alpha.18
```

## API

接口均返回 JSON，错误统一为 `{error, status, message}`，遵循 NPS 错误码命名空间。

### Sub-NID

| 方法 | 路径 | 用途 |
|------|------|------|
| `POST` | `/v1/agents` | 签发一个新的 sub-NID。Body：`{identifier?, capabilities[], scope?, agent_pub_key?, metadata?}`。返回 `{frame: IdentFrame, minted_private_key?}`。`agent_pub_key` 缺省时 npsd 会 mint Ed25519 keypair、**仅一次**返回私钥（`ed25519-raw:{base64url}`），同时为签名广播保留 AES-256-GCM 加密副本。|
| `GET`  | `/v1/agents` | 列出已签发的 sub-NID（新到旧）。Query：`?limit=N&offset=M`。|
| `GET`  | `/v1/agents/{nid}` | 返回某 NID 的持久化记录。 |
| `POST` | `/v1/agents/{nid}/renew` | 原位续租满足窗口要求的有效 sub-NID，返回 `{frame: IdentFrame}`。NID/key/capabilities/scope/metadata 不变，serial 与有效期轮换。|
| `POST` | `/v1/agents/{nid}/revoke` | 标记某 NID 已吊销。Body：`{reason?}`（如 `"key_compromise"`）。|

### Inbox

| 方法 | 路径 | 用途 |
|------|------|------|
| `POST` | `/v1/inbox/{nid}` | 投递消息到 `{nid}`。Body 是原始 payload。Header：`Content-Type`（原样存储）、`X-Nps-Inbox-Priority`（int，默认 0；越大越先消费）、`X-Nps-Inbox-Ttl-Seconds`（int，默认 600）。返回 `{message_id, enqueued_at, expires_at}`。收件人不在本机 → `404`；已 revoke → `401` + `NIP-CERT-REVOKED`；inbox 满 → `429`；payload 超 cap → `413`。|
| `GET`  | `/v1/inbox/{nid}` | Long-poll 取消息。Query：`?wait=N`（秒，clamp 到 `NPSD_MAX_INBOX_WAIT_SECONDS`）、`?batch=B`（默认 16）。返回 `{nid, count, messages: [{message_id, enqueued_at, expires_at, priority, content_type, payload_b64}]}`。超时返回空数组。|
| `DELETE` | `/v1/inbox/{nid}/{message_id}` | Ack 一条消息（从队列删除）。幂等 —— 二次调用返回 `404`。|
| `GET` | `/v1/inbox/{nid}/depth` | 该 NID 当前的待消费数量。 |

### Daemon

| 方法 | 路径 | 用途 |
|------|------|------|
| `GET` | `/health` | 返回 `{status, daemon, version, layer, role, port, host_nid, host_nid_fpr, ndp_announcements, sub_nids, ...}`。|
| `GET` | `/healthz` | 内建容器健康探针使用的最小 liveness 端点。|
| `GET` | `/.nwm` | Daemon 自身的 Neural Web Manifest（memory-node 形态、anonymous-auth、路由表）。|

## 配置（环境变量）

| 变量 | 默认 | 用途 |
|------|------|------|
| `NPSD_PORT` | `17433` | 绑定端口。 |
| `NPSD_HOST` | `127.0.0.1` | 绑定地址。仅在隔离网络命名空间里设 `0.0.0.0`，不要直接对公网暴露（公网 ingress 用 `nps-ingress`）。|
| `NPSD_NCP_PREAMBLE_TIMEOUT_MS` | `10000` | 原生 preamble 读取时限；不完整或不匹配的 NPS preamble 静默关闭。 |
| `NPSD_NCP_HELLO_TIMEOUT_MS` | `5000` | 合法 preamble 后完整 HelloFrame 的读取时限。 |
| `NPSD_NCP_MAX_HELLO_PAYLOAD_BYTES` | `65535` | 初始 HelloFrame payload 的分配上限。 |
| `NPSD_NCP_ENABLE_MSGPACK` | `true` | 原生会话是否声明/允许 Tier-2 MsgPack；Tier-1 JSON 始终支持。 |
| `NPSD_DATA_DIR` | `~/.local/share/npsd` | 持久化状态（root keypair 文件、`sub-nids.sqlite` 和持久 `inbox.sqlite`）。|
| `NPSD_HOST_NID_PREFIX` | `urn:nps:host:{HostFingerprint}` | sub-NID 派生的 NID 前缀。仅在主机已被上游 CA 用其他 NID 注册时覆盖。|
| `NPSD_SUB_NID_VALIDITY_DAYS` | `7` | 签发 sub-NID 的默认有效期。 |
| `NPSD_SUB_NID_RENEWAL_WINDOW_DAYS` | `1` | 有效 sub-NID 可开始续租的到期前窗口。 |
| `NPSD_NDP_ANNOUNCE_ENABLED` | `true` | 为有效的 npsd 托管 agent 广播签名 ephemeral AnnounceFrame。 |
| `NPSD_NDP_REGISTRY_URL` | `http://127.0.0.1:17436` | 本地 NDP registry 基础 URL；不可用时重试，不影响本地服务。 |
| `NPSD_NDP_ANNOUNCE_TTL_SECONDS` | `60` | 广播 TTL；clamp 到 NDP ephemeral 上限 60 秒。 |
| `NPSD_NDP_ANNOUNCE_INTERVAL_SECONDS` | `20` | 请求的存活广播间隔；有效值不超过 TTL 的一半。 |
| `NPSD_NDP_ADVERTISE_HOST` | `NPSD_HOST` | NDP 中发布的 hostname/address；绑定 `0.0.0.0` 时必须显式设置。 |
| `NPSD_MAX_INBOX_DEPTH_PER_NID` | `1024` | 单 NID 最大待处理消息数；超出时投递返回 `429`。|
| `NPSD_MAX_INBOX_MESSAGE_BYTES` | `65536` | 单条 payload 上限（与 NCP 默认帧大小对齐）。|
| `NPSD_MAX_INBOX_WAIT_SECONDS` | `30` | Long-poll 最大等待秒数；超出值会被 clamp。|

## 规范参考

- [NPS-Node Profile](https://github.com/labacacia/NPS-Release/blob/main/spec/services/NPS-Node-Profile.cn.md) —— 本 daemon 对照构建的合规规范。
- [NPS-Node-L1 合规](https://github.com/labacacia/NPS-Release/blob/main/spec/services/conformance/NPS-Node-L1.cn.md) —— 21 个 `TC-N1-*` 用例。
- [Daemon 架构](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.cn.md) —— 六 daemon、三层参考部署。
- [NPS-1 NCP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-1-NCP.cn.md) —— 线层。
- [NPS-3 NIP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-3-NIP.cn.md) —— root keypair / IdentFrame 语义。

## License

Apache-2.0，见仓库根的 `LICENSE`。
