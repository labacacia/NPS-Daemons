[English Version](./README.md) | 中文版

# `nps-ingress` —— NPS Daemon（第二层，Internet 入站）

> 公网 NPS Internet 入站的参考实现。终止公网 native NCP-over-TLS 流量，
> 将客户端 NIP 证书 NID 绑定到 NCP 会话，再把已验证字节流代理到本机后端。
> 完整六-daemon 拓扑见
> [`docs/daemons/architecture.cn.md`](../docs/architecture.cn.md)。

## 状态 —— v1.0.0-alpha.18

**native transport 边界已实现。** daemon 提供：

- HTTP `/health` 可观测端点；
- TLS 1.3 native NCP 监听器，ALPN 为 `nps/1.0`，配置 PKCS#12 服务端证书后启用；
- 配置可选、默认开启的双向 TLS：验证 NIP 证书链，内联校验
  `IdentFrame`/证书 NID，并以 `NCP-NID-MISMATCH` 拒绝不一致；
- 全双工握手 mediation：允许后端先返回 Caps、客户端再发送 Ident；不一致的 Ident
  不会转发给后端，并在客户端 half-close 后排空响应；
- 对握手时间和 frame 大小设硬边界，无效 preamble / 非 Hello 首帧在连接后端前拒绝。

完整的 `TC-N2-Tls-01..04` family 现已在真实 TLS socket 上执行；其角色范围证据
记录在
[`conformance/NPS-NODE-L2-TLS-EVIDENCE.json`](./conformance/NPS-NODE-L2-TLS-EVIDENCE.json)。
这**不是完整的 NPS-Node-L2 认证声明**：拓扑、Bridge、HA 与 Registry family
适用于其他 IUT 角色。历史路线图中的限速、NeuronHub 鉴权、CGN 扣款、声誉、
Anchor 中间件和 DDoS 项已在
[`conformance/NPS-INGRESS-ADMISSION-DISPOSITION.json`](./conformance/NPS-INGRESS-ADMISSION-DISPOSITION.json)
中逐项处置：它们属于产品、Anchor/AaaS、可选组合或部署控制，不是本 transport-IUT
宣传的能力。`/health` 会明确报告该边界与 disposition artifact。

确保 `PATH` 中有 OpenSSL 3 或更新版本，然后运行 ingress 证据套件：

```bash
dotnet test tools/daemons/nps-ingress/tests/NpsIngress.Tests.csproj
```

## 命名说明

这是**进程**名 `nps-ingress`。规范层的“把 NPS 帧路由进 NOP 的集群控制平面”
角色已由 [NPS-CR-0001](https://gitee.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0001-anchor-bridge-split.md)
重命名为 **Anchor Node**。该进程 MAY 承载 `NPS.NWP.Anchor` 中间件；
alpha.18 尚未实现该接入。

## 快速上手

```bash
NPSINGRESS_PORT=8080 dotnet run --project tools/daemons/nps-ingress/NpsIngress.csproj
curl -s http://localhost:8080/health | jq
```

要启用 native NCP-over-TLS termination，请提供 PKCS#12 服务端证书和客户端信任锚：

```bash
NPSINGRESS_CERT_PATH=/run/secrets/ingress.pfx \
NPSINGRESS_TRUST_ANCHORS_DIR=/run/secrets/client-cas \
dotnet run --project tools/daemons/nps-ingress/NpsIngress.csproj
```

### Docker

```bash
docker build -f tools/daemons/nps-ingress/Dockerfile -t labacacia/nps-ingress:1.0.0-alpha.18 .
docker run --rm -p 8080:8080 -p 17443:17443 \
  -v "$PWD/secrets:/run/secrets:ro" \
  -e NPSINGRESS_CERT_PATH=/run/secrets/ingress.pfx \
  -e NPSINGRESS_TRUST_ANCHORS_DIR=/run/secrets/client-cas \
  labacacia/nps-ingress:1.0.0-alpha.18
```

## 配置（环境变量）

| 变量 | 默认值 | 用途 |
|-----|------|-----|
| `NPSINGRESS_PORT` | `8080` | HTTP 健康检查/可观测端口。|
| `NPSINGRESS_HOST` | `0.0.0.0` | HTTP bind 地址（`0.0.0.0` 或 loopback 行为）。|
| `NPSINGRESS_TLS_PORT` | `17443` | Native NCP-over-TLS 端口；设为 `0` 时关闭。|
| `NPSINGRESS_BACKEND_HOST` | `127.0.0.1` | 本机 NCP 后端 host。|
| `NPSINGRESS_BACKEND_PORT` | `17433` | 本机 NCP 后端端口。|
| `NPSINGRESS_CERT_PATH` | 未设置 | PKCS#12（`.pfx`）服务端证书；未设置时 native listener 不启动。|
| `NPSINGRESS_CERT_PASSWORD` | 未设置 | 可选 PKCS#12 密码。|
| `NPSINGRESS_TRUST_ANCHORS_DIR` | 未设置 | PEM/DER 客户端信任锚目录；默认开启 mTLS 时应配置。|
| `NPSINGRESS_REQUIRE_CLIENT_CERT` | `true` | 要求并验证客户端证书，随后强制内联 session-NID 绑定。|
| `NPSINGRESS_MAX_HANDSHAKE_FRAME_BYTES` | `1048576` | identity admission 前检查的单个 frame payload 最大字节数。|
| `NPSINGRESS_HANDSHAKE_TIMEOUT_MS` | `10000` | preamble/Hello/Ident admission 最大时间；必须为正数。|

## 许可证

Apache-2.0 —— 见仓库根 `LICENSE`。
