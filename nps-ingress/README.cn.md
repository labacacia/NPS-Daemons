[English Version](./README.md) | 中文版

# `nps-ingress` —— NPS Daemon（第二层，Internet 入站）

> 公网 NPS Internet 入站的参考实现。把公网上来的 NPS-over-TLS 流量翻译成
> 本机协议帧；处理 TLS termination、限速、NeuronHub 用户鉴权、CGN 扣款触发、
> [NPS-RFC-0004](https://github.com/labacacia/NPS-Release/blob/main/spec/rfcs/NPS-RFC-0004-nid-reputation-log.cn.md) 声誉检查、
> DDoS 防护。完整六-daemon 拓扑见
> [`docs/daemons/architecture.cn.md`](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.cn.md)。

## 状态 —— v1.0.0-alpha.17 包，alpha.16 release 边界

**已发布 OSS baseline。** 公网 HTTP 监听 + `/health` 端点（端点本身记录后续里程碑）。
alpha.16 release 文档在 SDK/spec 层面对齐 native NCP TLS/mTLS 合约；daemon
端点接线，以及限速、鉴权、CGN 扣款、声誉查询、
[NPS-CR-0001](https://github.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0001-anchor-bridge-split.md) Anchor Node 中间件
接入仍是后续工作。

骨架自 alpha.3 起存在，目的是让部署面（进程名、NuGet 包 id、Docker image
tag）从 daemon 生态的起点就稳定下来。

## 命名说明

这是**进程**名 `nps-ingress`。*规范层*的"集群控制平面、把 NPS 帧路由到
NOP"角色已在 NWP 规范中由 [NPS-CR-0001](https://github.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0001-anchor-bridge-split.md)
重命名为 **Anchor Node**。`nps-ingress` 进程 MAY 通过 `NPS.NWP.Anchor`
承载 Anchor Node 中间件；该接入故意延后，让 OSS baseline 保持最小。

## 快速上手

```bash
NPSINGRESS_PORT=8080 dotnet run --project tools/daemons/nps-ingress/NpsIngress.csproj
curl -s http://localhost:8080/health | jq
```

### Docker

```bash
docker build -f tools/daemons/nps-ingress/Dockerfile -t labacacia/nps-ingress:1.0.0-alpha.17 .
docker run --rm -p 8080:8080 labacacia/nps-ingress:1.0.0-alpha.17
```

## 配置（环境变量）

| 变量 | 默认值 | 用途 |
|-----|------|-----|
| `NPSINGRESS_PORT` | `8080` | bind TCP 端口。生产部署在 `:443` 终止 TLS；本 Phase-1 骨架只监听 HTTP。|
| `NPSINGRESS_HOST` | `0.0.0.0` | bind 地址。默认对公网开放 —— `nps-ingress` 与 `npsd` 不同，故意面向 Internet。|

## 许可证

Apache-2.0 —— 见仓库根 `LICENSE`。
