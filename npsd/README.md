English | [中文版](./README.cn.md)

# `npsd` — NPS Daemon (Layer 1, host-local)

> Reference implementation of the host-local NPS daemon. Listens on the
> unified suite port `17433`, holds the host's root Ed25519 keypair,
> issues sub-NIDs for local agents on demand, and exposes a per-NID
> durable inbox queue and signed NDP liveness for ephemeral agents. See
> [`docs/daemons/architecture.md`](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md)
> for the broader six-daemon topology.

## What this binary does

- **Unified bind**: serves HTTP and native NCP on `127.0.0.1:17433` by default. A connection-layer router consumes only the exact `NPS/1.0\n` native preamble and leaves HTTP bytes untouched for Kestrel. Override with `NPSD_HOST` / `NPSD_PORT`.
- **Native NCP local-dev profile**: performs bounded preamble + Hello/Caps negotiation, enforces the negotiated encoding and payload ceiling, ACKs and caches canonical AnchorFrames, and emits an ErrorFrame before closing on admitted protocol violations. Plaintext is restricted to the host-local profile; public TLS/mTLS termination remains `nps-ingress`'s responsibility.
- **Root keypair**: generates an Ed25519 root keypair on first start; persists to `${NPSD_DATA_DIR:-~/.local/share/npsd}/root.ed25519.pkcs8` with POSIX mode `0600` (NPS-Node Profile L1 conformance test `TC-N1-NIP-01`).
- **Sub-NID issuance and renewal**: mint, persist, and renew short-lived sub-NIDs derived from the host root NID. Carrier IdentFrames are signed with the root key. npsd-minted agent keys are encrypted at rest under host-root-derived AES-256-GCM key material; BYO private keys never enter npsd. SQLite-backed at `${NPSD_DATA_DIR}/sub-nids.sqlite`.
- **NDP presence**: periodically POST a publisher-signed `AnnounceFrame` to the local registry for every active npsd-managed agent. Frames carry `activation_mode="ephemeral"`, persistent monotonic `graph_seq`, bounded TTL/heartbeat, and a graceful TTL-0 offline signal. BYO-key agents are not auto-announced because npsd cannot legitimately sign as them.
- **Per-NID inbox**: durable SQLite queue per sub-NID with long-poll, explicit durable ack, depth, priority, absolute TTL, and atomic per-NID depth caps. Undelivered payloads survive daemon restart in `${NPSD_DATA_DIR}/inbox.sqlite`.
- **`GET /.nwm`** — daemon-self Neural Web Manifest declaring the routes above.
- **Health probes**: `GET /health` exposes npsd-shaped diagnostics; Docker invokes the built-in `npsd --healthcheck`, which checks `GET /healthz` without relying on extra image tools.

## What landed in alpha.4

- NCP native-mode connection preamble (`NPS/1.0\n`) runtime — NPS-RFC-0001 Phase 2.
- Sub-NID issuance: `npsd` signs child NIDs for local agents.
- Per-NID inbox queue: ephemeral agents pull and explicitly acknowledge messages through npsd; undelivered rows survive restart.

## Alpha.19 debt-closure candidate

- Native NCP is now a real npsd wire path rather than a library-only helper. HTTP control APIs and native sessions coexist on the configured unified port.
- The inbox is now a durable SQLite queue. Host-restart tests prove bit-identical ActionFrame recovery, persistent acknowledgement, absolute-TTL purge, priority order and FIFO pull/ack drain.
- Managed sub-NIDs now renew in place during a bounded pre-expiry window, preserving NID, key, capabilities, scope and metadata while atomically rotating serial and validity timestamps. Revoked, expired and too-early credentials fail closed.
- Active managed agents emit signed ephemeral AnnounceFrames to `nps-registry`; announcement keys and per-NID graph sequences survive restart, and graceful shutdown emits TTL 0. Caller-managed/BYO keys remain explicit opt-out because npsd does not possess their private half.
- Upgrade note: sub-NIDs created before this slice have no recoverable private key in npsd and are reported with caller-managed/legacy agents; reissue them once if automatic announcements are required.
- Resident/hybrid push is deliberately not claimed: L1 permits that path to be declined, and this profile advertises only `ephemeral` HTTP pull with explicit acknowledgement.
- Real-socket tests cover HTTP/native coexistence, Hello/Caps, Anchor ACK/cache and digest rejection, incompatible-version and unsupported-encoding ErrorFrames, loopback defaults, and the RFC-0001 silent-close deadline.
- [`conformance/NPS-NODE-L1-MANIFEST.json`](./conformance/NPS-NODE-L1-MANIFEST.json) records the exact NCP, NDP and NWP case evidence. Full Node L1 certification is not claimed while the remaining NIP/NWP and process-level cases are open.

## What is NOT yet implemented (alpha.11+)

Tracked in `docs/daemons/architecture.md` under the per-daemon phasing table:

- Resident/hybrid push delivery (optional at L1 and not advertised by this profile; belongs to a future L2+ design).
- Full NPS-Node L1 certification; this slice verifies only the declared NCP/NDP/NWP subset.

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
docker build -f tools/daemons/npsd/Dockerfile -t labacacia/npsd:1.0.0-alpha.18 .
docker run --rm -p 17433:17433 \
  -v npsd-data:/data \
  labacacia/npsd:1.0.0-alpha.18
```

## API

All endpoints return JSON unless noted. Errors carry `{error, status, message}` per the NPS error-code namespace.

### Sub-NIDs

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/v1/agents` | Issue a new sub-NID. Body: `{identifier?, capabilities[], scope?, agent_pub_key?, metadata?}`. Returns `{frame: IdentFrame, minted_private_key?}`. If `agent_pub_key` is omitted, npsd mints an Ed25519 keypair, returns the private half **once** as `ed25519-raw:{base64url}`, and retains an AES-256-GCM-encrypted copy for signed announcements. |
| `GET`  | `/v1/agents` | List issued sub-NIDs (newest first). Query: `?limit=N&offset=M`. |
| `GET`  | `/v1/agents/{nid}` | Return the persisted record for a NID. |
| `POST` | `/v1/agents/{nid}/renew` | Renew an eligible active sub-NID in place. Returns `{frame: IdentFrame}`. The NID/key/capabilities/scope/metadata remain unchanged; serial and validity rotate. |
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
| `GET` | `/health` | Returns `{status, daemon, version, layer, role, port, host_nid, host_nid_fpr, ndp_announcements, sub_nids, ...}`. |
| `GET` | `/healthz` | Minimal liveness endpoint used by the built-in container health probe. |
| `GET` | `/.nwm` | Daemon-self Neural Web Manifest (memory-node shape, anonymous-auth, route catalog). |

## Configuration (env vars)

| Variable | Default | Purpose |
|----------|---------|---------|
| `NPSD_PORT` | `17433` | TCP port to bind. |
| `NPSD_HOST` | `127.0.0.1` | Bind address. Use `0.0.0.0` only inside an isolated network namespace — never expose `npsd` directly to the public internet (use `nps-ingress`). |
| `NPSD_NCP_PREAMBLE_TIMEOUT_MS` | `10000` | Native preamble read deadline; incomplete/mismatched NPS preambles close silently. |
| `NPSD_NCP_HELLO_TIMEOUT_MS` | `5000` | Complete HelloFrame read deadline after a valid preamble. |
| `NPSD_NCP_MAX_HELLO_PAYLOAD_BYTES` | `65535` | Allocation ceiling for the initial HelloFrame payload. |
| `NPSD_NCP_ENABLE_MSGPACK` | `true` | Advertise/allow Tier-2 MsgPack in native session negotiation; Tier-1 JSON remains supported. |
| `NPSD_DATA_DIR` | `~/.local/share/npsd` | Persistent state (root keypair file, `sub-nids.sqlite`, and durable `inbox.sqlite`). |
| `NPSD_HOST_NID_PREFIX` | `urn:nps:host:{HostFingerprint}` | NID prefix used when minting sub-NIDs. Override only if the host has been registered with an upstream CA under a different NID. |
| `NPSD_SUB_NID_VALIDITY_DAYS` | `7` | Default validity window for issued sub-NIDs. |
| `NPSD_SUB_NID_RENEWAL_WINDOW_DAYS` | `1` | Pre-expiry window in which an active sub-NID may renew. |
| `NPSD_NDP_ANNOUNCE_ENABLED` | `true` | Emit signed ephemeral AnnounceFrames for active npsd-managed agents. |
| `NPSD_NDP_REGISTRY_URL` | `http://127.0.0.1:17436` | Base URL of the local NDP registry. Registry failure is retried and does not take down local service. |
| `NPSD_NDP_ANNOUNCE_TTL_SECONDS` | `60` | Announcement TTL; clamped to the NDP ephemeral maximum of 60 seconds. |
| `NPSD_NDP_ANNOUNCE_INTERVAL_SECONDS` | `20` | Requested liveness interval; effective value is capped at half the TTL. |
| `NPSD_NDP_ADVERTISE_HOST` | `NPSD_HOST` | Hostname/address published in NDP. Set explicitly when binding `0.0.0.0`. |
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
