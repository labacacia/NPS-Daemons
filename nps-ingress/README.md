English | [中文版](./README.cn.md)

# `nps-ingress` — NPS Daemon (Layer 2, Internet ingress)

> Reference implementation of the public-facing NPS Internet ingress.
> Translates NPS-over-TLS traffic from the public internet into local
> frames; handles TLS termination, rate limiting, NeuronHub-customer
> authentication, CGN debit triggering, [NPS-RFC-0004](https://github.com/labacacia/NPS-Release/blob/main/spec/rfcs/NPS-RFC-0004-nid-reputation-log.md)
> reputation checks, and DDoS defense. See
> [`docs/daemons/architecture.md`](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md)
> for the broader six-daemon topology.

## Status — v1.0.0-alpha.18 package, alpha.16 release boundary

**Published OSS baseline.** Public-facing HTTP listener with a `/health`
endpoint that documents the planned milestones. The alpha.16 release docs
align the native NCP TLS/mTLS contract at the SDK/spec layer; daemon endpoint
wiring plus rate limit, auth, CGN debit, reputation lookup, and Anchor Node
middleware wiring per [NPS-CR-0001](https://github.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0001-anchor-bridge-split.md)
remain follow-up work.

This skeleton has been in place since alpha.3 so that the deployment surface (process
name, NuGet package id, Docker image tag) is stable from the start of
the daemon ecosystem.

## Naming note

This is the **process** called `nps-ingress`. The *spec-level* role
of "cluster control plane that routes NPS frames into NOP" has been
renamed **Anchor Node** in the NWP specification by
[NPS-CR-0001](https://github.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0001-anchor-bridge-split.md). The
`nps-ingress` process MAY host an Anchor Node middleware via
`NPS.NWP.Anchor`; that wiring is intentionally deferred so this OSS
baseline stays minimal.

## Quick start

```bash
NPSINGRESS_PORT=8080 dotnet run --project tools/daemons/nps-ingress/NpsIngress.csproj
curl -s http://localhost:8080/health | jq
```

### Docker

```bash
docker build -f tools/daemons/nps-ingress/Dockerfile -t labacacia/nps-ingress:1.0.0-alpha.18 .
docker run --rm -p 8080:8080 labacacia/nps-ingress:1.0.0-alpha.18
```

## Configuration (env vars)

| Variable | Default | Purpose |
|----------|---------|---------|
| `NPSINGRESS_PORT` | `8080` | TCP port to bind. Production deployments terminate TLS on `:443`; this Phase-1 skeleton only listens HTTP. |
| `NPSINGRESS_HOST` | `0.0.0.0` | Bind address. The default is public — `nps-ingress`, unlike `npsd`, is intentionally Internet-facing. |

## License

Apache-2.0 — see `LICENSE` at the repository root.
