English | [中文版](./CHANGELOG.cn.md)

# Changelog — `nps-runner`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Tags follow the umbrella SemVer of the NPS suite.

---

## [1.0.0-alpha.19] — 2026-09-05

### Fixed

- Package the machine-readable runner conformance manifests into the standalone
  test output so the same capability contract runs in monorepo and materialized layouts.
- Replaced the process-local task lease and terminal-dedup map with a persistent
  SQLite store. Processes sharing the same state file now make atomic claims,
  preserve terminal nodes across restart, reclaim expired leases, and fence a
  stale/restarted process with a fresh process-instance identity.
- Leave claim-conflicted inbox messages unacked, atomically commit terminal state
  before ack, and cancel a worker on lease loss without terminal write, ack, or
  completion notification. Deterministic tests cover the full stale-owner path.
- Added a machine-readable Node L3 implementation manifest: three strict cases
  are verified, five are partial, and the TaskFrame DAG/Saga deployment cases are
  explicitly unexecuted, so full L3 certification is not claimed.
- Added runnable gate metadata and exact ten-case coverage checks to that manifest.
- Fixed production startup by exposing the resolver's dependency-injection
  constructor, and fixed both Docker build paths to use the base image's non-root
  `app` account, copy the complete standalone source tree, and provision the
  persistent state volume.
- Corrected stale skeleton-era current-status text while preserving historical
  alpha.3/alpha.4 records as history.

## [1.0.0-alpha.18] — 2026-08-15

### Changed

- Align package metadata, runtime banners, publish-overlay SDK references, and the synchronized daemon train with the alpha.18 protocol/SDK candidate.

## [1.0.0-alpha.17] — 2026-08-02

### Changed

- Prepare the alpha.17 daemon candidate by aligning package metadata, runtime banners, and publish-overlay SDK dependencies with the server-surface parity release.
- Complete the CR-0007 runtime gate with fail-closed portable OCI SpawnSpec
  validation, inline/HTTPS/NWP reference resolution, configurable OCI runtime
  execution, deterministic timeout precedence, and legacy subprocess
  compatibility.
- Add daemon-owned NOP 0.9 conformance coverage for leases, SpawnSpec parsing,
  worker lifecycle, and deduplication semantics.
- Harden remote SpawnSpec retrieval against SSRF and DNS rebinding: reject
  non-public DNS answers, connect only to a validated address while preserving
  TLS hostname verification, revalidate every redirect, and bound redirects,
  response size, and request time.
- Stop terminal reporting and inbox acknowledgement when a worker loses its
  process-local lease, leaving the message available for a later local claim.

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

### Added

- **NPS-CR-0007 task-claim decision logic** (`LeaseStore.cs`). Inbox dispatch
  claims a process-local lease per `task_id` before spawning; local contention
  returns `NOP-CLAIM-CONFLICT`. Leases are clamped to `[10, 600]s`, expire within
  that process, carry `dedup_key = sha256(task_id ‖ dag_hash)`, and release on
  worker completion. Historical correction: this in-memory implementation did
  not coordinate replicas or retain leases/terminal dedup across a process crash.
  Those durable/shared semantics remained follow-up work (CR-0007 §10 OQ-1).

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

- **Inbox watcher + worker spawn** — full L3 FaaS runtime replacing the
  alpha.3/alpha.4 heartbeat skeleton:
  - Self-registers with local `npsd` on startup (`POST /v1/agents`,
    idempotent — 409 returns existing NID); retries with exponential
    backoff up to 20 attempts.
  - Long-polls the runner's inbox (`GET /v1/inbox/{nid}?wait=N&batch=B`)
    at a configurable interval (`NPS_RUNNER_POLL_INTERVAL_MS`, default 1 s).
  - Deserialises JSON spawn-spec messages; see README for full field list.
  - Spawns worker subprocesses with the given `command` / `args` /
    `env` / `work_dir`.
  - Captures `stdout` + `stderr` to `NPS_RUNNER_LOG_DIR/{task_id}.log`
    with `[stdout]`/`[stderr]` prefixes.
  - Monitor loop (5 s tick) enforces `idle_timeout_seconds` (silence
    since last output) and `max_runtime_seconds` (hard wall-clock limit,
    default 4 h).
  - On worker exit: acks the inbox message; if `reply_to` is set, POSTs
    a JSON completion notification to that NID.
  - Concurrency cap (`NPS_RUNNER_MAX_CONCURRENT_WORKERS`, default 8) —
    messages arriving when at capacity stay unacked and reappear next poll.

---

## [1.0.0-alpha.4] — 2026-04-30

### Tracking the suite

- Bumps `LabAcacia.NPS.*` NuGet dependencies to `v1.0.0-alpha.4`.
  No functional changes since alpha.3 — `nps-runner` remains the
  Generic Host + 30-second heartbeat skeleton.
- Inbox watcher, `spawn_spec_ref` resolver, and worker subprocess
  lifecycle remain deferred to the L3 stage (alpha.5+).

---

## [1.0.0-alpha.3] — 2026-04-26

### Added

- First release. Layer-1 task scheduler / FaaS runtime for the NPS suite.
- Phase 1 skeleton: Generic Host scaffolding with a 30-second heartbeat,
  so the deployment surface is stable and operators can wire it into
  systemd / docker compose without waiting for the full implementation.
- Multi-stage Docker image (non-root `npsrunner` user, no exposed ports — pulls work from the colocated `npsd`).

### Deferred to alpha.5+ (L3 stage)

- Inbox watcher polling local `npsd` for messages addressed to
  ephemeral-mode NIDs.
- `spawn_spec_ref` resolver — given an ephemeral NID, fetch the
  spawn spec from the originating Anchor / Memory Node.
- Worker subprocess lifecycle: launch, monitor, kill on completion or
  on idle timeout per the spawn spec.

---

[1.0.0-alpha.5]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
