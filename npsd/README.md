English | [中文版](./README.cn.md)

# `npsd` — NPS Daemon (Layer 1, host-local)

> Reference implementation of the host-local NPS daemon. Listens on the
> unified suite port `17433`, holds the host's root Ed25519 keypair,
> issues sub-NIDs for local agents on demand, and exposes a per-NID
> inbox queue for resident agent push delivery. See
> [`docs/daemons/architecture.md`](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md)
> for the broader six-daemon topology.

## What this binary does

- **Bind** `127.0.0.1:17433` by default (loopback only — public ingress is `nps-ingress`'s job). Override with `NPSD_HOST` / `NPSD_PORT`.
- **Root keypair**: generates an Ed25519 root keypair on first start; persists to `${NPSD_DATA_DIR:-~/.local/share/npsd}/root.ed25519.pkcs8` with POSIX mode `0600` (NPS-Node Profile L1 conformance test `TC-N1-NIP-01`).
- **Sub-NID issuance**: mint and persist sub-NIDs derived from the host root NID. Carrier IdentFrames are signed with the root key. SQLite-backed at `${NPSD_DATA_DIR}/sub-nids.sqlite`.
- **Per-NID inbox**: short-term in-memory queue per sub-NID with long-poll, ack, depth, priority, TTL, and per-NID depth caps.
- **`GET /.nwm`** — daemon-self Neural Web Manifest declaring the routes above.
- **`GET /health`** — Docker `HEALTHCHECK` / systemd liveness target.

## What landed in alpha.4

- NCP native-mode connection preamble (`NPS/1.0\n`) runtime — NPS-RFC-0001 Phase 2.
- Sub-NID issuance: `npsd` signs child NIDs for local agents.
- Per-NID inbox queue: resident agents receive messages via `npsd`'s in-memory inbox.

## What is NOT yet implemented (alpha.11+)

Tracked in `docs/daemons/architecture.md` under the per-daemon phasing table:

- Push delivery to resident agents (inbox → agent socket).
- AnnounceFrame emission to the local NDP registry.
- Sub-NID renewal — currently revoke + reissue.

## Quick start

### Local

```bash
dotnet run --project tools/daemons/npsd/Npsd.csproj
# → npsd starting; root NID host fingerprint = <16-hex>; bind = 127.0.0.1:17433

curl -s http://127.0.0.1:17433/health | jq
curl -s http://127.0.0.1:17433/.nwm   | jq
```

### Docker

```bash
docker build -f tools/daemons/npsd/Dockerfile -t labacacia/npsd:1.0.0-alpha.16 .
docker run --rm -p 17433:17433 \
  -v npsd-data:/data \
  labacacia/npsd:1.0.0-alpha.16
```

## API

All endpoints return JSON unless noted. Errors carry `{error, status, message}` per the NPS error-code namespace.

### Sub-NIDs

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/v1/agents` | Issue a new sub-NID. Body: `{identifier?, capabilities[], scope?, agent_pub_key?, metadata?}`. Returns `{frame: IdentFrame, minted_private_key?}`. If `agent_pub_key` is omitted, npsd mints an Ed25519 keypair and returns the private half **once** as `ed25519-raw:{base64url}`. |
| `GET`  | `/v1/agents` | List issued sub-NIDs (newest first). Query: `?limit=N&offset=M`. |
| `GET`  | `/v1/agents/{nid}` | Return the persisted record for a NID. |
| `POST` | `/v1/agents/{nid}/revoke` | Mark the NID revoked. Body: `{reason?}` (e.g. `"key_compromise"`). |

### Inbox

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/v1/inbox/{nid}` | Deposit a message addressed to `{nid}`. Body is the raw payload. Headers: `Content-Type` (stored verbatim), `X-Nps-Inbox-Priority` (int, default 0; higher drains first), `X-Nps-Inbox-Ttl-Seconds` (int, default 600). Returns `{message_id, enqueued_at, expires_at}`. `404` if recipient not on this host; `401` with `NIP-CERT-REVOKED` if revoked; `429` if inbox full; `413` if payload exceeds the per-message cap. |
| `GET`  | `/v1/inbox/{nid}` | Long-poll for messages. Query: `?wait=N` (seconds, clamped to `NPSD_MAX_INBOX_WAIT_SECONDS`), `?batch=B` (max messages returned, default 16). Returns `{nid, count, messages: [{message_id, enqueued_at, expires_at, priority, content_type, payload_b64}]}`. Empty array on timeout. |
| `DELETE` | `/v1/inbox/{nid}/{message_id}` | Ack a message, removing it from the queue. Idempotent — second call returns `404`. |
| `GET` | `/v1/inbox/{nid}/depth` | Current pending count for the NID. |

### Daemon

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/health` | Returns `{status, daemon, version, layer, role, port, host_nid, host_nid_fpr, ...}`. |
| `GET` | `/.nwm` | Daemon-self Neural Web Manifest (memory-node shape, anonymous-auth, route catalog). |

## Configuration (env vars)

| Variable | Default | Purpose |
|----------|---------|---------|
| `NPSD_PORT` | `17433` | TCP port to bind. |
| `NPSD_HOST` | `127.0.0.1` | Bind address. Use `0.0.0.0` only inside an isolated network namespace — never expose `npsd` directly to the public internet (use `nps-ingress`). |
| `NPSD_DATA_DIR` | `~/.local/share/npsd` | Persistent state (root keypair file + sub-NID SQLite). |
| `NPSD_HOST_NID_PREFIX` | `urn:nps:host:{HostFingerprint}` | NID prefix used when minting sub-NIDs. Override only if the host has been registered with an upstream CA under a different NID. |
| `NPSD_SUB_NID_VALIDITY_DAYS` | `7` | Default validity window for issued sub-NIDs. |
| `NPSD_MAX_INBOX_DEPTH_PER_NID` | `1024` | Max pending messages per NID before deposits get `429`. |
| `NPSD_MAX_INBOX_MESSAGE_BYTES` | `65536` | Per-message payload cap (matches NCP default frame size). |
| `NPSD_MAX_INBOX_WAIT_SECONDS` | `30` | Maximum long-poll wait time. Larger values get clamped. |

## Spec references

- [NPS-Node Profile](https://github.com/labacacia/NPS-Release/blob/main/spec/services/NPS-Node-Profile.md) — the compliance specification this daemon targets.
- [NPS-Node-L1 conformance suite](https://github.com/labacacia/NPS-Release/blob/main/spec/services/conformance/NPS-Node-L1.md) — the 21 `TC-N1-*` cases this daemon is being built to pass.
- [Daemon architecture](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md) — the six-daemon, three-layer reference deployment.
- [NPS-1 NCP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-1-NCP.md) — the wire layer.
- [NPS-3 NIP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-3-NIP.md) — root keypair / IdentFrame semantics.

## License

Apache-2.0 — see `LICENSE` at the repository root.
