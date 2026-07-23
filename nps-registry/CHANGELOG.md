English | [中文版](./CHANGELOG.cn.md)

# Changelog — `nps-registry`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Tags follow the umbrella SemVer of the NPS suite.

---

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

- Suite-wide version sync to 1.0.0-alpha.14 (L2 cross-machine NDP registry).

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

### Tracking the suite

- Tracks NPS suite `v1.0.0-alpha.5`.  No registry-specific changes —
  the skeleton HTTP listener and `/health` surface are identical to
  alpha.4.  `LabAcacia.NPS.NDP` NuGet dependency bumped to
  `v1.0.0-alpha.5`, picking up DNS TXT fallback resolution
  (`ResolveViaDns`) and NWP error-code constants.

---

## [1.0.0-alpha.4] — 2026-04-30

### Added

- **SQLite-backed real NDP registry** — `SqliteNdpRegistry` replaces
  the alpha.3 stub. Implements the full NDP `Resolve` / `Graph` /
  `Announce` URL surface against a real persistence store at
  `${NPSREG_DATA_DIR:-/data}/registry.sqlite`:
  - `POST /v1/announce` — accept `AnnounceFrame` and persist binding
    `(NID → endpoint, TTL, signature)`.
  - `GET /v1/resolve?nid=<nid>` — resolve NID to endpoint with TTL
    eviction (lazy purge on read).
  - `GET /v1/graph?nid=<nid>&depth=<N>` — depth-limited BFS traversal
    (default cap 5 per NDP spec) with cycle detection.
- 10 integration tests under `NPS.Tests/Daemons/NpsRegistry/` covering
  registration, resolution, graph traversal, TTL eviction, concurrent
  writes, and oversized announce rejection.

### Tracking the suite

- Bumps `LabAcacia.NPS.*` NuGet dependencies to `v1.0.0-alpha.4`.

### Deferred to alpha.5+

- Cross-machine federation / gossip for L2 HA-cluster mode.
- Optional Postgres backend for cluster-grade deployments.
- Graph-traversal optimisation beyond BFS + cycle detection (e.g.
  query result caching, parallel traversal).

---

## [1.0.0-alpha.3] — 2026-04-26

### Added

- First release. Layer-2 cross-machine NDP discovery registry for the NPS suite.
- Phase 1 skeleton: HTTP listener on the NDP optional-dedicated port `17436`;
  `Resolve` / `Graph` / `Announce` URLs return `NDP-REGISTRY-UNAVAILABLE`
  (HTTP 503) so consumers can wire and gracefully fall back. `/health`
  returns 200 once the listener is up.
- Multi-stage Docker image (non-root `npsreg` user, exposes `:17436`).

### Deferred to alpha.4

- SQLite-backed real registration table.
- Announce signature verification + TTL-based eviction.
- Graph-traversal optimisation (BFS + cycle detection).
- Optional Postgres backend for cluster-grade deployments.

---

[1.0.0-alpha.5]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
