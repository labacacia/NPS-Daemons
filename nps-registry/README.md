English | [中文版](./README.cn.md)

# `nps-registry` — NPS Daemon (Layer 2, cross-machine NDP discovery)

> Reference implementation of the cross-machine NDP discovery registry.
> Centralised endpoint that responds to NDP `Resolve` / `Graph` queries
> and aggregates registrations from multiple machines. Per-host
> [`npsd`](../npsd/) only knows local sessions; cross-machine queries
> go here. See
> [`docs/daemons/architecture.md`](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md)
> for the broader six-daemon topology.

## Status — alpha.4

**Real SQLite-backed registry.** Announce, Resolve, and Graph endpoints
are fully implemented. Announcements are persisted with TTL-based lazy
expiry (no background timer needed); a monotonic per-cluster graph
sequence counter bumps on every Announce or eviction. File-backed or
in-memory storage is selected via env.

L2 cross-machine federation (HA cluster mode / gossip) is queued for
alpha.11+.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/announce` | Register or refresh a node announcement. Accepts an NDP `AnnounceBody`; returns the stored entry. TTL defaults to the announced value (or 300 s if unset). |
| `GET` | `/v1/resolve?nid=<nid>` | Resolve a single NID to its current announcement. Returns `404` if unknown or expired. |
| `GET` | `/v1/graph` | Return all live (non-expired) announcements as a JSON array, plus a `seq` monotonic counter for client-side change detection. |
| `GET` | `/health` | Liveness probe. Returns `status`, `storage`, and current graph `seq`. |

## Quick start

```bash
# In-memory (default, no file needed)
NPSREGISTRY_PORT=17436 dotnet run --project tools/daemons/nps-registry/NpsRegistry.csproj

# File-backed (persists across restarts)
NPSREGISTRY_SQLITE_PATH=/data/registry.db \
NPSREGISTRY_PORT=17436 \
  dotnet run --project tools/daemons/nps-registry/NpsRegistry.csproj

curl -s http://localhost:17436/health | jq
curl -s http://localhost:17436/v1/graph | jq
```

### Docker

```bash
docker build -f tools/daemons/nps-registry/Dockerfile -t labacacia/nps-registry:1.0.0-alpha.17 .
docker run --rm -p 17436:17436 \
  -v /data:/data \
  -e NPSREGISTRY_SQLITE_PATH=/data/registry.db \
  labacacia/nps-registry:1.0.0-alpha.17
```

## Configuration (env vars)

| Variable | Default | Purpose |
|----------|---------|---------|
| `NPSREGISTRY_PORT` | `17436` | TCP port to bind. NDP optional-dedicated per [NPS-4](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-4-NDP.md). |
| `NPSREGISTRY_HOST` | `0.0.0.0` | Bind address. A registry is intentionally network-facing. |
| `NPSREGISTRY_SQLITE_PATH` | *(in-memory)* | Path to the SQLite database file. Unset → ephemeral in-memory store. |

## Spec references

- [NPS-4 NDP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-4-NDP.md) — the discovery protocol whose registry surface this daemon implements.
- [Daemon architecture §④](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md#-nps-registry--discovery-registry-l2-stage-optionally-hosted) — why the registry is its own daemon rather than part of `npsd`.

## License

Apache-2.0 — see `LICENSE` at the repository root.
