[English Version](./README.md) | 中文版

# `nps-registry` —— NPS Daemon（第二层，跨机 NDP 发现）

> 跨机 NDP 发现注册中心的参考实现。中心节点，响应 NDP `Resolve` / `Graph`
> 查询，汇总多机注册信息。每机 [`npsd`](../npsd/) 只知道本机 session；
> 跨机查询走这里。完整六-daemon 拓扑见
> [`docs/daemons/architecture.cn.md`](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.cn.md)。

## 状态

**真实 SQLite 后端注册中心。** Announce、Resolve、Graph 端点已全部实现。
公告以 TTL lazy expiry 方式持久化（无需后台定时器），每次 Announce 或
eviction 均自增单调 per-cluster graph 序号。文件存储或内存存储通过 env 选择。

**多 Anchor 高可用（[NPS-CR-0009](https://github.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0009-multi-anchor-ha.md)，NDP §9）。**
`AnnounceFrame.cluster_epoch`（uint64，缺省视为 `1`）已接受并持久化；
解析 `cluster_anchor` NID 时返回 epoch **最高**的存活 Anchor；两个存活
Anchor 持平 epoch 属脑裂故障，报 `NDP-CLUSTER-SPLIT`（`NPS-CLIENT-CONFLICT`，
HTTP 409）。`(cluster_anchor, cluster_epoch, active_nid)` 三元组按 cluster
单调记录，可与 federated peer 交换：peer 给出更高 epoch 时采纳，更低时永不回退。

## 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| `POST` | `/v1/announce` | 注册或刷新节点公告。接受 NDP `AnnounceBody`，返回已存储的 entry（含 registry 实际保留的 `cluster_epoch`）。TTL 取公告中的值（未设置则为 300 秒）。按 NDP §9 处理 `ndp-forwarded-by`：成环返回 `NDP-FEDERATION-LOOP` / 409，第 4 跳静默丢弃。|
| `GET` | `/v1/resolve?target=<nwp-url>` | 将 `nwp://` target 解析为当前端点。未知或已过期返回 `404`。|
| `GET` | `/v1/cluster/resolve?cluster_anchor=<nid>` | **NPS-CR-0009。** 解析 cluster 的当前 active Anchor —— `cluster_epoch` 最高的存活成员。无存活 Anchor 返回 `404`；两个存活 Anchor 并列最高 epoch 返回 `409` + `NDP-CLUSTER-SPLIT`。|
| `GET` | `/v1/federation/clusters` | 本 registry 向 federated peer 传播的 `(cluster_anchor, cluster_epoch, active_nid)` 三元组。|
| `POST` | `/v1/federation/cluster` | 接收 peer 传来的三元组。更高 epoch → `applied`；持平/更低 → `ignored`；持平 epoch 但 active Anchor 不同 → `409` + `NDP-CLUSTER-SPLIT`。要求 `public-federated` profile（NDP §7.6），否则 `403` + `NDP-ANNOUNCE-PROFILE-VIOLATION`。|
| `GET` | `/v1/graph` | 以 GraphFrame 返回所有存活（未过期）公告（节点带 `cluster_anchor`），附带 `seq` 单调计数器供客户端做变更检测。|
| `GET` | `/health` | 存活探针。返回 `status`、`storage`、`profile` 以及 entry / cluster 数量。|

## 快速上手

```bash
# 内存模式（默认，无需文件）
NPSREGISTRY_PORT=17436 dotnet run --project tools/daemons/nps-registry/NpsRegistry.csproj

# 文件模式（重启后持久化）
NPSREGISTRY_SQLITE_PATH=/data/registry.db \
NPSREGISTRY_PORT=17436 \
  dotnet run --project tools/daemons/nps-registry/NpsRegistry.csproj

curl -s http://localhost:17436/health | jq
curl -s http://localhost:17436/v1/graph | jq
```

### Docker

```bash
docker build -f tools/daemons/nps-registry/Dockerfile -t labacacia/nps-registry:1.0.0-alpha.18 .
docker run --rm -p 17436:17436 \
  -v /data:/data \
  -e NPSREGISTRY_SQLITE_PATH=/data/registry.db \
  labacacia/nps-registry:1.0.0-alpha.18
```

## 配置（环境变量）

| 变量 | 默认值 | 用途 |
|-----|------|-----|
| `NPSREGISTRY_PORT` | `17436` | bind TCP 端口。NDP 可选独立端口，见 [NPS-4](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-4-NDP.cn.md)。|
| `NPSREGISTRY_HOST` | `0.0.0.0` | bind 地址。registry 故意面向网络。|
| `NPSREGISTRY_SQLITE_PATH` | *(内存)* | SQLite 数据库文件路径。未设置 → 临时内存存储。|
| `NPSREGISTRY_NID` | `urn:nps:agent:nps-registry:local` | 本 registry 自身 NID。用于 NDP §9 federation 的 `ndp-forwarded-by` 成环检测。|
| `NPSREGISTRY_PROFILE` | `local-dev` | NDP §7.3 registry 安全 profile：`local-dev` / `org-private` / `public-federated`。仅 `public-federated` 接受 federation 导入（§7.6）。|

## 规范引用

- [NPS-4 NDP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-4-NDP.cn.md) —— 本 daemon 实现的注册中心面所属的发现协议。
- [Daemon 架构 §④](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.cn.md#-nps-registry--discovery-注册中心-l2-阶段可选托管) —— 为什么 registry 独立 daemon 而不是 `npsd` 的一部分。

## 许可证

Apache-2.0 —— 见仓库根 `LICENSE`。
