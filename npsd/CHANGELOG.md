English | [中文版](./CHANGELOG.cn.md)

# Changelog — `npsd`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Tags follow the umbrella SemVer of the NPS suite.

---

## [1.0.0-alpha.18] — 2026-08-15

### Changed

- Align package metadata, runtime banners, publish-overlay SDK references, and the synchronized daemon train with the alpha.18 protocol/SDK candidate.

## [1.0.0-alpha.17] — 2026-08-02

### Changed

- Prepare the alpha.17 daemon candidate by aligning package metadata, runtime banners, and publish-overlay SDK dependencies with the server-surface parity release.
- Upgrade `Microsoft.Data.Sqlite` and pin `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 to remove the vulnerable bundled SQLite runtime.

## [1.0.0-alpha.16] — 2026-07-23

### Changed

- Suite-wide alpha.16 sync: aligned package metadata, current README/version banners, distribution source trees, and release-prep notes after alpha.15 was already published.
- Carries the nps-ingress and nps-runner distribution test-isolation fix from the source-of-truth tree.

## [1.0.0-alpha.15] — 2026-06-28

### Changed

- Suite-wide alpha.15 sync: aligned package metadata, current README/version banners, distribution source trees, and release-prep notes with NPS-Dev.
- Carries the NCP Tier-3 BinaryVector, inbound NWP Bridge server hardening, NIP canonical trust/revoke, and NDP discovery canonical-form alignment delivered by the source-of-truth tree.

## [1.0.0-alpha.14] — 2026-06-26

- Suite-wide version sync to 1.0.0-alpha.14.

## [1.0.0-alpha.14] — 2026-06-13

- Suite-wide version sync to 1.0.0-alpha.14 (L1 protocol host).

## [1.0.0-alpha.7] — 2026-05-18

### Tracking the suite

- Tracks NPS suite `v1.0.0-alpha.7`. Project version, publish-overlay
  version, and all `LabAcacia.NPS.*` PackageReferences are aligned to the
  alpha.7 release train.

---

## [1.0.0-alpha.6] — 2026-05-12

### Tracking the suite

- Tracks NPS suite `v1.0.0-alpha.6`. Project version, publish-overlay
  version, and all `LabAcacia.NPS.*` PackageReferences are aligned to the
  alpha.6 release train.

---

## [1.0.0-alpha.5] — 2026-05-01

### Added

- **`AnchorNodeMiddleware`** — `topology:read` capability gating (NPS-CR-0001):
  callers without `topology:read` receive HTTP 403 /
  `NWP-UNAUTHORIZED-CAPABILITY` on `topology.snapshot` and
  `topology.stream` endpoints.  `cgn_est` integer field added to every
  topology event payload.
- **`NPS-SERVER-UNSUPPORTED` (HTTP 501)** — unknown reserved NWP frame types
  now return `501 Not Implemented` with error code
  `NWP-RESERVED-TYPE-UNSUPPORTED` instead of a generic 4xx.
- **AssuranceLevel empty-string fix** — `LabAcacia.NPS.NIP` `1.0.0-alpha.5`
  treats an empty `assurance_level` wire value identically to a missing
  field (`anonymous`).
- **NDP DNS TXT fallback** — `LabAcacia.NPS.NDP` `1.0.0-alpha.5` adds
  `ResolveViaDns` with an injectable `DnsTxtLookup` interface; the system
  DNS resolver is used by default.
- 17 integration tests covering the updated topology behaviour and 501
  response mapping.

---

## [1.0.0-alpha.4] — 2026-04-30

### Added

- **Sub-NID issuance (L1+)** — `POST/GET /v1/agents`,
  `GET/POST /v1/agents/{nid}`, `POST /v1/agents/{nid}/revoke`. Mints
  IdentFrames signed by the host root key (`LabAcacia.NPS.NIP`'s
  `NipSigner` over canonical JSON). SQLite-backed at
  `${NPSD_DATA_DIR}/sub-nids.sqlite`. Caller can BYO `agent_pub_key`
  (npsd never sees the private half) or omit it (npsd mints an Ed25519
  keypair and returns the private key once).
- **Per-NID inbox (L1+)** — `POST /v1/inbox/{nid}`,
  `GET /v1/inbox/{nid}?wait=N&batch=B` (long-poll),
  `DELETE /v1/inbox/{nid}/{message_id}` (ack), `GET /v1/inbox/{nid}/depth`.
  In-memory queue; supports priority, TTL, per-NID depth caps, payload
  size caps, and ergonomic 404/403/413/429 error mapping.
- New configurable knobs via `NPSD_HOST_NID_PREFIX`,
  `NPSD_SUB_NID_VALIDITY_DAYS`, `NPSD_MAX_INBOX_DEPTH_PER_NID`,
  `NPSD_MAX_INBOX_MESSAGE_BYTES`, `NPSD_MAX_INBOX_WAIT_SECONDS`.
- 17 integration tests under `NPS.Tests/Daemons/Npsd/` covering issue,
  list, get, revoke, inbox deposit, long-poll, ack, depth, priority
  ordering, oversize-payload rejection, and revoked-NID inbox refusal.

### Tracking the suite

- Bumps `LabAcacia.NPS.*` NuGet dependencies to `v1.0.0-alpha.4`,
  picking up **NPS-RFC-0001 Phase 2** (NCP preamble helpers) at the
  library layer. Wire transport remains HTTP-only; native-mode preamble
  routing in `npsd` itself is still a future addition.

### Deferred to alpha.5+

- NCP native-mode wire transport in `npsd` (preamble runtime is
  available in the library, but `npsd` keeps HTTP-only on the wire).
- Inbox persistence (LMDB / SQLite); current alpha.4 inbox is
  in-memory.
- AnnounceFrame emission to the local NDP registry.
- Sub-NID renewal (currently you revoke + reissue).

---

## [1.0.0-alpha.3] — 2026-04-26

### Added

- First release. Layer-1 host-local daemon for the NPS suite.
- Binds `127.0.0.1:17433` by default; override with `NPSD_HOST` / `NPSD_PORT`.
- Generates a root Ed25519 keypair on first start; persists to
  `${NPSD_DATA_DIR:-~/.local/share/npsd}/root.ed25519.pkcs8` with POSIX
  mode `0600` — satisfies NPS-Node Profile L1 conformance test
  `TC-N1-NIP-01 — Root keypair generation and permission`.
- Serves `GET /health` (Docker `HEALTHCHECK` / systemd liveness target).
- Serves `GET /.nwm` returning the daemon's own minimal Neural Web
  Manifest (Memory-node shape, anonymous-auth, no actions).
- Multi-stage Docker image (non-root `npsd` user, `/data` volume).

### Deferred to alpha.4 / alpha.5

- NCP native-mode wire transport (HTTP-only at alpha.3; native preamble runtime per NPS-RFC-0001 lands alpha.4).
- Per-NID inbox queue persistence + push to resident agents.
- Sub-NID issuance for local agents.
- AnnounceFrame emission to the local NDP registry.

---

[1.0.0-alpha.5]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
