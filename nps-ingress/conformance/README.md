English | [中文版](./README.cn.md)

# `nps-ingress` transport admission evidence

[`NPS-NODE-L2-TLS-EVIDENCE.json`](./NPS-NODE-L2-TLS-EVIDENCE.json) records the
all-or-nothing `TC-N2-Tls-01..04` family for the `nps-ingress` transport-IUT
role. The tests cross real TLS 1.3 sockets between the daemon and OpenSSL 3,
using a disposable Ed25519 NIP client certificate chain.

Run the evidence gate with:

```bash
dotnet test tools/daemons/nps-ingress/tests/NpsIngress.Tests.csproj \
  -c Release --filter FullyQualifiedName~IngressTlsConformanceTests
```

The manifest coverage test requires exactly four passing cases and maps each
case to a test method carrying the same `ConformanceId` trait. Topology,
Bridge, Multi-Anchor and Registry HA families are not applicable to this
transport-only IUT role; they are not recorded as fake passes or partial `na`
families.

This artifact is executable evidence, not a signed full
`NPS-Node-L2` self-attestation. It does not claim admission controls beyond the
NPS-RFC-0006 TLS boundary.

[`NPS-INGRESS-ADMISSION-DISPOSITION.json`](./NPS-INGRESS-ADMISSION-DISPOSITION.json)
records the current-contract boundary separately from the historical alpha.3
roadmap. It mechanically classifies rate limiting, NeuronHub customer auth,
CGN debit, reputation policy, Anchor middleware, and broad DDoS policy without
advertising them as implemented transport capabilities. The executable local
hardening boundary is limited to handshake time, frame-size, and pre-admission
framing checks.
