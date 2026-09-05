English | [中文版](./README.cn.md)

# `nps-ingress` — NPS Daemon (Layer 2, Internet ingress)

> Reference implementation of the public-facing NPS Internet ingress.
> Terminates native NCP-over-TLS traffic from the public Internet, binds the
> client NIP certificate NID to the NCP session, and proxies the verified byte
> stream to a local backend. See
> [`docs/daemons/architecture.md`](../docs/architecture.md)
> for the broader six-daemon topology.

## Status — v1.0.0-alpha.18

**Implemented native-transport boundary.** The daemon provides:

- an HTTP `/health` observability endpoint;
- a TLS 1.3 native NCP listener with ALPN `nps/1.0`, enabled when a PKCS#12
  server certificate is configured;
- optional-by-configuration, default-on mutual TLS with NIP certificate chain
  validation, inline `IdentFrame`/certificate NID binding, and
  `NCP-NID-MISMATCH` rejection;
- full-duplex handshake mediation that lets the backend return Caps before the
  client sends Ident, rejects a mismatched Ident before forwarding it, and
  drains the response after a client half-close;
- bounded handshake time and frame size, with invalid preamble/non-Hello
  traffic rejected before a backend connection is opened.

The complete `TC-N2-Tls-01..04` family now executes against real TLS sockets;
its role-scoped evidence is recorded in
[`conformance/NPS-NODE-L2-TLS-EVIDENCE.json`](./conformance/NPS-NODE-L2-TLS-EVIDENCE.json).
This is **not a complete NPS-Node-L2 certification claim**: the topology,
Bridge, HA, and Registry families apply to other IUT roles. The historical
rate-limit, NeuronHub authentication, CGN debit, reputation, Anchor middleware,
and DDoS roadmap items are dispositioned in
[`conformance/NPS-INGRESS-ADMISSION-DISPOSITION.json`](./conformance/NPS-INGRESS-ADMISSION-DISPOSITION.json):
they are product, Anchor/AaaS, optional-composition, or deployment controls, not
capabilities advertised by this transport-IUT. `/health` reports this boundary
and the disposition artifact explicitly.

Run the ingress evidence suite with OpenSSL 3 or newer available on `PATH`:

```bash
dotnet test tools/daemons/nps-ingress/tests/NpsIngress.Tests.csproj
```

## Naming note

This is the **process** called `nps-ingress`. The *spec-level* role of
"cluster control plane that routes NPS frames into NOP" was renamed
**Anchor Node** by
[NPS-CR-0001](https://github.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0001-anchor-bridge-split.md).
The process MAY host `NPS.NWP.Anchor` middleware; alpha.18 does not implement
that wiring.

## Quick start

```bash
NPSINGRESS_PORT=8080 dotnet run --project tools/daemons/nps-ingress/NpsIngress.csproj
curl -s http://localhost:8080/health | jq
```

To enable native NCP-over-TLS termination, provide a PKCS#12 server
certificate and client trust anchors:

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

## Configuration (environment variables)

| Variable | Default | Purpose |
|----------|---------|---------|
| `NPSINGRESS_PORT` | `8080` | HTTP health/observability port. |
| `NPSINGRESS_HOST` | `0.0.0.0` | HTTP bind address (`0.0.0.0` or loopback behavior). |
| `NPSINGRESS_TLS_PORT` | `17443` | Native NCP-over-TLS port; `0` disables it. |
| `NPSINGRESS_BACKEND_HOST` | `127.0.0.1` | Local NCP backend host. |
| `NPSINGRESS_BACKEND_PORT` | `17433` | Local NCP backend port. |
| `NPSINGRESS_CERT_PATH` | unset | PKCS#12 (`.pfx`) server certificate. The native listener stays disabled while unset. |
| `NPSINGRESS_CERT_PASSWORD` | unset | Optional PKCS#12 password. |
| `NPSINGRESS_TRUST_ANCHORS_DIR` | unset | Directory containing PEM/DER client trust anchors. Required for useful default-on mTLS admission. |
| `NPSINGRESS_REQUIRE_CLIENT_CERT` | `true` | Require and validate a client certificate, then enforce inline session-NID binding. |
| `NPSINGRESS_MAX_HANDSHAKE_FRAME_BYTES` | `1048576` | Maximum payload size of one frame inspected before identity admission. |
| `NPSINGRESS_HANDSHAKE_TIMEOUT_MS` | `10000` | Maximum preamble/Hello/Ident admission time; must be positive. |

## License

Apache-2.0 — see `LICENSE` at the repository root.
