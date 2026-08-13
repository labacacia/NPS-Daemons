[English Version](./README.md) | 中文版

# `npsd` —— NPS Daemon（Layer 1，主机本地）

> NPS 主机本地 daemon 的参考实现。监听套件统一端口 `17433`，
> 持有主机的 root Ed25519 keypair，按需为本机 agent 签发 sub-NID，
> 并提供 per-NID inbox 队列用于 resident agent 推送送达。
> 完整六 daemon 拓扑见
> [`docs/daemons/architecture.cn.md`](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.cn.md)。

## 这个二进制做什么

- **绑定**：默认 `127.0.0.1:17433`（只在 loopback —— 公网 ingress 是 `nps-ingress` 的活）。可用 `NPSD_HOST` / `NPSD_PORT` 覆盖。
- **Root keypair**：首次启动生成 Ed25519 root keypair；落盘到 `${NPSD_DATA_DIR:-~/.local/share/npsd}/root.ed25519.pkcs8`，POSIX 文件权限 `0600`（满足 NPS-Node Profile L1 合规用例 `TC-N1-NIP-01`）。
- **Sub-NID 签发**：基于主机 root NID 生成、持久化 sub-NID。承载 IdentFrame 由 root key 签名。SQLite 存于 `${NPSD_DATA_DIR}/sub-nids.sqlite`。
- **Per-NID inbox**：按 sub-NID 分桶的短期内存队列，支持 long-poll、ack、depth、priority、TTL、per-NID 深度上限。
- **`GET /.nwm`** —— daemon 自身的 Neural Web Manifest，声明上面这些路由。
- **`GET /health`** —— Docker `HEALTHCHECK` / systemd 活性探针目标。

## alpha.4 已落地的部分

- NCP 原生模式连接前导字节（`NPS/1.0\n`）runtime —— NPS-RFC-0001 Phase 2。
- Sub-NID 签发：`npsd` 为本机 agent 签发子 NID。
- Per-NID inbox 队列：resident agent 通过 `npsd` 的内存 inbox 接收消息。

## alpha.11+ 还没做的部分

按 `docs/daemons/architecture.md` 的逐 daemon 阶段表跟踪：

- 主动推送到 resident agent（inbox → agent socket）。
- 向本地 NDP registry 广播 AnnounceFrame。
- Sub-NID 续签 —— 当前只能 revoke + reissue。
- Sub-NID 续签（alpha.4）—— 现在只能 revoke + reissue。

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
| `POST` | `/v1/agents` | 签发一个新的 sub-NID。Body：`{identifier?, capabilities[], scope?, agent_pub_key?, metadata?}`。返回 `{frame: IdentFrame, minted_private_key?}`。`agent_pub_key` 缺省时 npsd 会 mint Ed25519 keypair 并**仅一次**返回私钥（`ed25519-raw:{base64url}`）。|
| `GET`  | `/v1/agents` | 列出已签发的 sub-NID（新到旧）。Query：`?limit=N&offset=M`。|
| `GET`  | `/v1/agents/{nid}` | 返回某 NID 的持久化记录。 |
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
| `GET` | `/health` | 返回 `{status, daemon, version, layer, role, port, host_nid, host_nid_fpr, ...}`。|
| `GET` | `/.nwm` | Daemon 自身的 Neural Web Manifest（memory-node 形态、anonymous-auth、路由表）。|

## 配置（环境变量）

| 变量 | 默认 | 用途 |
|------|------|------|
| `NPSD_PORT` | `17433` | 绑定端口。 |
| `NPSD_HOST` | `127.0.0.1` | 绑定地址。仅在隔离网络命名空间里设 `0.0.0.0`，不要直接对公网暴露（公网 ingress 用 `nps-ingress`）。|
| `NPSD_DATA_DIR` | `~/.local/share/npsd` | 持久化状态（root keypair 文件 + sub-NID SQLite）。|
| `NPSD_HOST_NID_PREFIX` | `urn:nps:host:{HostFingerprint}` | sub-NID 派生的 NID 前缀。仅在主机已被上游 CA 用其他 NID 注册时覆盖。|
| `NPSD_SUB_NID_VALIDITY_DAYS` | `7` | 签发 sub-NID 的默认有效期。 |
| `NPSD_MAX_INBOX_DEPTH_PER_NID` | `1024` | 单 NID 最大待处理消息数；超出时投递返回 `429`。|
| `NPSD_MAX_INBOX_MESSAGE_BYTES` | `65536` | 单条 payload 上限（与 NCP 默认帧大小对齐）。|
| `NPSD_MAX_INBOX_WAIT_SECONDS` | `30` | Long-poll 最大等待秒数；超出值会被 clamp。|

## 规范参考

- [NPS-Node Profile](https://github.com/labacacia/NPS-Release/blob/main/spec/services/NPS-Node-Profile.cn.md) —— 本 daemon 对照构建的合规规范。
- [NPS-Node-L1 合规](https://github.com/labacacia/NPS-Release/blob/main/spec/services/conformance/NPS-Node-L1.cn.md) —— 21 个 `TC-N1-*` 用例。
- [Daemon 架构](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.cn.md) —— 六 daemon、三层参考部署。
- [NPS-1 NCP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-1-NCP.cn.md) —— 线层。
- [NPS-3 NIP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-3-NIP.cn.md) —— root keypair / IdentFrame 语义。

## License

Apache-2.0，见仓库根的 `LICENSE`。
