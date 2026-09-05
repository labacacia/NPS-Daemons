English | [中文版](./README.cn.md)

# `nps-registry` — NPS Daemon (Layer 2, cross-machine NDP discovery)

> Reference implementation of the cross-machine NDP discovery registry.
> Centralised endpoint that responds to NDP `Resolve` / `Graph` queries
> and aggregates registrations from multiple machines. Per-host
> [`npsd`](../npsd/) only knows local sessions; cross-machine queries
> go here. See
> [`docs/daemons/architecture.md`](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.md)
> for the broader six-daemon topology.

## Status

**Real SQLite-backed registry.** Announce, Resolve, and Graph endpoints
are fully implemented. Announcements are persisted with TTL-based lazy
expiry (no background timer needed); a monotonic per-cluster graph
sequence counter bumps on every Announce or eviction. File-backed or
in-memory storage is selected via env.

**Multi-Anchor HA ([NPS-CR-0009](https://github.com/labacacia/NPS-Release/blob/main/spec/cr/NPS-CR-0009-multi-anchor-ha.md), NDP §9).**
`AnnounceFrame.cluster_epoch` (uint64, absent ⇒ `1`) is accepted and persisted;
resolving a `cluster_anchor` NID returns the live Anchor holding the **highest**
epoch; an equal-epoch tie between two live Anchors is a split-brain fault
reported as `NDP-CLUSTER-SPLIT` (`NPS-CLIENT-CONFLICT`, HTTP 409). The
`(cluster_anchor, cluster_epoch, active_nid)` tuple is tracked monotonically per
cluster and can be exchanged with federated peers — a higher epoch from a peer
wins, a lower one is never applied.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/announce` | Register or refresh a node announcement. Accepts an NDP `AnnounceBody`; returns the stored entry, including the `cluster_epoch` the registry kept. TTL defaults to the announced value (or 300 s if unset). Honours `ndp-forwarded-by` (NDP §9): a loop returns `NDP-FEDERATION-LOOP` / 409, a 4th hop is silently dropped. |
| `GET` | `/v1/resolve?target=<nwp-url>` | Resolve a `nwp://` target to its current endpoint. Returns `404` if unknown or expired. |
| `GET` | `/v1/cluster/resolve?cluster_anchor=<nid>` | **NPS-CR-0009.** Resolve a cluster to its current active Anchor — the live member with the highest `cluster_epoch`. `404` when the cluster has no live Anchor; `409` + `NDP-CLUSTER-SPLIT` when two live Anchors share the top epoch. |
| `GET` | `/v1/federation/clusters` | The `(cluster_anchor, cluster_epoch, active_nid)` tuples this registry propagates to federated peers. |
| `POST` | `/v1/federation/cluster` | Ingest such a tuple from a peer. Higher epoch → `applied`; equal/lower → `ignored`; equal epoch naming a different Anchor → `409` + `NDP-CLUSTER-SPLIT`. Requires the `public-federated` profile (NDP §7.6), otherwise `403` + `NDP-ANNOUNCE-PROFILE-VIOLATION`. |
| `GET` | `/v1/graph` | Return all live (non-expired) announcements as a GraphFrame (nodes carry `cluster_anchor`), plus a `seq` monotonic counter for client-side change detection. |
| `GET` | `/health` | Liveness probe. Returns `status`, `storage`, `profile`, entry and cluster counts. |

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
docker build -f tools/daemons/nps-registry/Dockerfile -t labacacia/nps-registry:1.0.0-alpha.19 .
docker run --rm -p 17436:17436 \
  -v /data:/data \
  -e NPSREGISTRY_SQLITE_PATH=/data/registry.db \
  labacacia/nps-registry:1.0.0-alpha.19
```

## Configuration (env vars)

| Variable | Default | Purpose |
|----------|---------|---------|
| `NPSREGISTRY_PORT` | `17436` | TCP port to bind. NDP optional-dedicated per [NPS-4](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-4-NDP.md). |
| `NPSREGISTRY_HOST` | `0.0.0.0` | Bind address. A registry is intentionally network-facing. |
| `NPSREGISTRY_SQLITE_PATH` | *(in-memory)* | Path to the SQLite database file. Unset → ephemeral in-memory store. |
| `NPSREGISTRY_NID` | `urn:nps:agent:nps-registry:local` | This registry's own NID. Used for NDP §9 federation loop detection against `ndp-forwarded-by`. |
| `NPSREGISTRY_PROFILE` | `local-dev` | NDP §7.3 registry security profile: `local-dev` / `org-private` / `public-federated`. Federation ingest (§7.6) is accepted only under `public-federated`. |

## Spec references

- [NPS-4 NDP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-4-NDP.md) — the discovery protocol whose registry surface this daemon implements.
- [Daemon architecture §④](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.md#-nps-registry--discovery-registry-l2-stage-optionally-hosted) — why the registry is its own daemon rather than part of `npsd`.

## License

Apache-2.0 — see `LICENSE` at the repository root.
