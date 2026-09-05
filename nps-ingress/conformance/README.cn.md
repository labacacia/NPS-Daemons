[English Version](./README.md) | 中文版

# `nps-ingress` transport admission 证据

[`NPS-NODE-L2-TLS-EVIDENCE.json`](./NPS-NODE-L2-TLS-EVIDENCE.json) 记录
`nps-ingress` transport-IUT 角色的全有或全无 `TC-N2-Tls-01..04` family。
测试让 daemon 与 OpenSSL 3 穿过真实 TLS 1.3 socket，并使用一次性 Ed25519
NIP 客户端证书链。

执行证据门禁：

```bash
dotnet test tools/daemons/nps-ingress/tests/NpsIngress.Tests.csproj \
  -c Release --filter FullyQualifiedName~IngressTlsConformanceTests
```

manifest coverage 测试要求恰好四个用例全部通过，并把每个 case 映射到带同名
`ConformanceId` trait 的测试方法。Topology、Bridge、Multi-Anchor 与 Registry HA
family 不适用于纯 transport IUT；这里不会把它们伪装成通过，也不会产生非法的
partial-`na` family。

该文件是可执行证据，不是已签名的完整 `NPS-Node-L2` 自认证；它不声明
NPS-RFC-0006 TLS 边界之外的 admission controls。

[`NPS-INGRESS-ADMISSION-DISPOSITION.json`](./NPS-INGRESS-ADMISSION-DISPOSITION.json)
把当前契约边界与 alpha.3 历史路线图分开记录，并以可机械校验的方式处置限速、
NeuronHub 客户鉴权、CGN 扣款、声誉策略、Anchor 中间件及广义 DDoS 策略，不把它们
宣传为已实现的 transport capability。当前可执行的本地加固只覆盖握手时间、frame
大小和后端准入前 framing 检查。
