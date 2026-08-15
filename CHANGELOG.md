English | [中文版](./CHANGELOG.cn.md)

# Changelog — NPS Daemons (bundle)

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Until NPS reaches v1.0 stable, every repository in the suite is synchronized to the same pre-release version tag.

---

## [1.0.0-alpha.18] — 2026-08-15

### Changed

- Align the four public daemon images, package metadata, and deployment examples with the alpha.18 synchronized candidate.

## [1.0.0-alpha.17] — 2026-08-02

### Changed

- Align all four bundled daemon images, package metadata, and operational examples with the alpha.17 server-surface parity candidate.
- Preserve standalone test access in the runner and ingress publish overlays, and remove the vulnerable bundled SQLite runtime from `npsd` and `nps-registry`.

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

- Bundle image tags bumped to 1.0.0-alpha.14.

## [1.0.0-alpha.7] — 2026-05-18

### Changed

- Aligns all daemon image tags, project versions, publish-overlay versions,
  and `LabAcacia.NPS.*` PackageReferences to `1.0.0-alpha.7`.

---

## [1.0.0-alpha.6] — 2026-05-12

### Changed

- Aligns all daemon image tags, project versions, publish-overlay versions,
  and `LabAcacia.NPS.*` PackageReferences to `1.0.0-alpha.6`.

---

## [1.0.0-alpha.5] — 2026-05-03

### Added

- **Native OS packages**: `.deb` (Ubuntu/Debian amd64), `.rpm` (Fedora/RHEL x86_64), and
  `.msi` (Windows x64) installers are now published as GitHub Release assets alongside the
  Docker images. Each package installs as a self-contained binary with no .NET runtime
  dependency. Linux packages register a systemd service unit; the Windows MSI registers a
  Windows service under a `NT SERVICE\<daemon>` virtual account. Install instructions:
  see the [NPS Wiki — Operator Quickstart Bundle](https://github.com/labacacia/NPS-Release/wiki/Operator-Quickstart-Bundle#option-b-native-packages-systemd--windows-service).

### Changed

- **`npsd`** — `AnchorNodeMiddleware` gains `topology:read` capability
  gating and `cgn_est` per-event field; HTTP 501 +
  `NWP-RESERVED-TYPE-UNSUPPORTED` for unknown reserved NWP frame types.
  See [`npsd/CHANGELOG.md`](./npsd/CHANGELOG.md) for details.
- **`nps-ingress`** — bumps `LabAcacia.NPS.NWP.Anchor` to
  `1.0.0-alpha.5` (wire field rename `estimated_npt → cgn_est`).
- **`nps-runner`** — inbox watcher + worker spawn fully implemented (L3
  FaaS runtime); replaces the alpha.3/alpha.4 heartbeat skeleton.
  See [`nps-runner/CHANGELOG.md`](./nps-runner/CHANGELOG.md) for details.
- **`nps-registry`** / **`nps-cloud-ca`** — tracking suite version, no
  functional changes.
- **`docker-compose.yml`** — all image tags bumped from
  `1.0.0-alpha.4` → `1.0.0-alpha.5`.

See [NPS-Release CHANGELOG](https://github.com/labacacia/NPS-Release/blob/main/CHANGELOG.md)
for the full suite-level rollup.

---

## [1.0.0-alpha.4] — 2026-04-30

### Added

- **`npsd`** — Sub-NID issuance + per-NID inbox queue land at the L1+
  layer. `POST/GET /v1/agents`, `GET/POST /v1/agents/{nid}`,
  `POST /v1/agents/{nid}/revoke` mint IdentFrames signed by the host
  root key (SQLite-backed at `${NPSD_DATA_DIR}/sub-nids.sqlite`).
  `POST /v1/inbox/{nid}`, `GET /v1/inbox/{nid}?wait=N&batch=B`
  (long-poll), `DELETE /v1/inbox/{nid}/{message_id}` (ack),
  `GET /v1/inbox/{nid}/depth` deliver an in-memory queue with
  priority, TTL, per-NID depth caps, and payload size caps. 17
  integration tests under `NPS.Tests/Daemons/Npsd/`.
- **`nps-registry`** — `SqliteNdpRegistry` replaces the alpha.3 stub.
  Real `Resolve` / `Graph` / `Announce` backed by a SQLite store with
  TTL-based lazy purge. 10 integration tests covering registration,
  resolution, graph traversal, TTL eviction, and concurrent writes.

### Changed

- All four daemons' `*.csproj` files bumped to `1.0.0-alpha.4` and
  reference the matching
  [`LabAcacia.NPS.*`](https://www.nuget.org/profiles/LabAcacia/) NuGet
  package versions.
- Top-level `docker-compose.yml` `image:` tags pinned to
  `labacacia/<daemon>:1.0.0-alpha.4`.

### Tracking the suite

This release rolls up suite-wide protocol changes that landed in NPS
`v1.0.0-alpha.4`:

- **NPS-RFC-0001 Phase 2** — NCP connection preamble runtime helpers
  (preamble parser + writer; `npsd` keeps HTTP-only on the wire — full
  native preamble routing lands later).
- **NPS-RFC-0002 Phase A/B** — X.509 NID certificates + ACME `agent-01`
  in `LabAcacia.NPS.NIP` (`nps-cloud-ca` and `nip-ca-server` will pick
  this up downstream).
- **NPS-CR-0002** — Anchor Node `topology.snapshot` /
  `topology.stream` (`nps-ingress` will wire this once the Anchor
  middleware lands).

Still phase-1 skeletons in this release: `nps-runner`, `nps-ingress`
(no functional change since alpha.3).

See [`NPS-Release/CHANGELOG.md`](https://github.com/labacacia/NPS-Release/blob/main/CHANGELOG.md)
for the full suite-level rollup.

---

## [1.0.0-alpha.3] — 2026-04-26

### Added

- First independent release as a standalone repository at
  [`labacacia/nps-daemons`](https://github.com/labacacia/nps-daemons)
  ([Gitee mirror](https://gitee.com/labacacia/nps-daemons)). Up to and
  including v1.0.0-alpha.2 these daemons did not exist yet; they were
  introduced as a group in dev as the alpha.3 deployment surface and
  have been promoted to their own repo on first release.
- **`npsd`** (Layer 1, host-local NCP wire + state host) — L1 minimum:
  binds `127.0.0.1:17433`, generates a root Ed25519 keypair on first
  start (PKCS#8, file mode `0600` — satisfies NPS-Node Profile L1
  conformance test `TC-N1-NIP-01`), serves `GET /health` and a
  daemon-self `GET /.nwm`.
- **`nps-runner`** (Layer 1, task scheduler / FaaS runtime) — Phase 1
  skeleton: Generic Host scaffolding with a 30-second heartbeat. Inbox
  watcher + `spawn_spec_ref` resolver + worker lifecycle land at L3
  stage (alpha.5+).
- **`nps-ingress`** (Layer 2, public Internet ingress) — Phase 1
  skeleton: HTTP listener on `:8080` + `/health` documenting planned
  milestones. TLS termination, rate-limit, NeuronHub auth, CGN debit,
  NPS-RFC-0004 reputation lookup, and NPS-CR-0001 Anchor Node
  middleware wiring land alpha.4 → alpha.5.
- **`nps-registry`** (Layer 2, cross-machine NDP discovery) — Phase 1
  skeleton: HTTP listener on the NDP optional-dedicated port `17436`;
  `Resolve` / `Graph` / `Announce` URL surface returns
  `NDP-REGISTRY-UNAVAILABLE` (HTTP 503) so consumers can wire and
  gracefully fall back. SQLite-backed real registration lands alpha.4.
- Bundle README (EN + CN), top-level `docker-compose.yml` to spin up
  all four daemons at once, repo-level `LICENSE` + `NOTICE`, and
  `docs/architecture.{md,cn.md}` carrying the full three-layer
  topology (including the two private trust-anchor daemons that live
  in `innolotus/nps-cloud-ca` and `innolotus/nps-ledger`).

### Changed

- All four daemon `*.csproj` files switched from in-tree
  `<ProjectReference>` to published
  [`LabAcacia.NPS.*`](https://www.nuget.org/profiles/LabAcacia/) NuGet
  packages at v1.0.0-alpha.3. The publish repo is now self-contained
  and builds without the development monorepo.
- Dockerfile context changed from monorepo-relative (`../../..`) to
  repo-root, so `docker build .` works directly inside each daemon
  subdirectory of the publish repo.

### Tracking the suite

This release rolls up suite-wide protocol changes that landed in NPS
`v1.0.0-alpha.3`:

- **RFC-0001** — NCP connection preamble (preamble runtime in `npsd`
  itself lands at alpha.4 — Phase 1 here is library-only).
- **RFC-0003** — Agent identity assurance levels (touches NIP and the
  `npsd` self-`/.nwm` `min_assurance_level` field).
- **RFC-0004** — NID reputation log (Phase 1; entry shape only —
  `nps-ledger`'s in-memory honouring lives in `innolotus/nps-ledger`,
  not this repo).
- **CR-0001** — Anchor + Bridge Node split (`nps-ingress` is wired
  against the new `LabAcacia.NPS.NWP.Anchor` package).

See [`NPS-Release/CHANGELOG.md`](https://github.com/labacacia/NPS-Release/blob/main/CHANGELOG.md)
for the full suite-level rollup.

---

[1.0.0-alpha.5]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
