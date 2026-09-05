English | [中文版](./CHANGELOG.cn.md)

# Changelog — `nps-ingress`

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Tags follow the umbrella SemVer of the NPS suite.

---

## [Unreleased] — alpha.19 debt closure

### Changed

- Reconciled current runtime claims with the native ingress already present in source: TLS 1.3,
  ALPN `nps/1.0`, NIP mTLS trust validation, inline certificate/`IdentFrame` session-NID binding,
  backend prefix replay, and response drain after client half-close.
- Replaced the stale planned-milestones `/health` payload with a machine-readable capability
  snapshot that distinguishes implemented, configuration-present, uncertified, and unsupported boundaries.
- Corrected the bilingual daemon README, architecture/status tables, package descriptions, and
  container ports. Unsupported capabilities remain explicitly unclaimed.
- Added deterministic tests for health claim truth and both bidirectional-proxy completion paths.
- Added real-socket executable evidence for the complete `TC-N2-Tls-01..04` family, including
  TLS 1.3/ALPN negotiation, mandatory client certificates, trusted NIP certificate-to-session
  binding, and peer-visible `NCP-NID-MISMATCH` rejection.
- Closed a TLS 1.3 fail-open edge where OpenSSL could finish the handshake without presenting a
  client certificate, and emit a structured `ErrorFrame` before closing NID-mismatched sessions.
- Published a role-scoped evidence manifest without claiming the topology, Bridge, HA, Registry,
  admission-control, or full NPS-Node-L2 families.
- Fixed the normal interactive native handshake path: ingress now relays backend Caps before
  waiting for the post-Caps IdentFrame, then validates the certificate NID before forwarding that
  Ident. Invalid preambles, non-Hello first frames, oversized frames, and slow incomplete
  handshakes close before backend admission.
- Added a mechanically tested current-contract disposition for the historical rate-limit,
  NeuronHub auth, CGN debit, reputation, Anchor middleware, and DDoS roadmap items. These are not
  advertised as RFC-0006 transport capabilities.
- Repaired both Docker build paths for the current .NET base images and standalone source layout:
  use the built-in non-root `app` identity, copy all runtime source files, and replace the absent
  `wget` dependency with the binary's built-in `--healthcheck` probe.

## [1.0.0-alpha.18] — 2026-08-15

### Changed

- Align package metadata, runtime banners, publish-overlay SDK references, and the synchronized daemon train with the alpha.18 protocol/SDK candidate.

## [1.0.0-alpha.17] — 2026-08-02

### Changed

- Prepare the alpha.17 daemon candidate by aligning package metadata, runtime banners, and publish-overlay SDK dependencies with the server-surface parity release.

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

- **L2 native-mode TLS terminator** (`NcpTlsListener`, NPS-RFC-0006 §6). A `SslStream` listener
  on the configured TLS port negotiates ALPN **`nps/1.0`** over TLS 1.3 with **mutual TLS**: the
  client certificate is validated to the configured trust anchors and its NID extracted and
  bound to the session (`NipMtlsValidator`, reusing `NipX509Verifier`); the terminated NCP byte
  stream is proxied to the local backend (npsd). The IdentFrame-NID cross-check
  (`CheckSessionNidBinding`) enforces `NCP-NID-MISMATCH` (NPS-RFC-0006 §6.3). Runs alongside the
  HTTP `/health` listener; stays idle until `NPSINGRESS_CERT_PATH` is set. New env config
  (`IngressOptions`): `NPSINGRESS_TLS_PORT`, `NPSINGRESS_BACKEND_{HOST,PORT}`,
  `NPSINGRESS_CERT_PATH`, `NPSINGRESS_TRUST_ANCHORS_DIR`, `NPSINGRESS_REQUIRE_CLIENT_CERT`.
  Unit tests: `tests/NipMtlsValidatorTests.cs` (5 cases). Follow-up: IdentFrame parse to wire the
  session NID cross-check inline; full TC-N2-* L2 conformance; rate limiting / CGN debit.

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

### Changed

- Bumps `LabAcacia.NPS.NWP.Anchor` dependency to `1.0.0-alpha.5`.
  Renames the wire field `estimated_npt` → `cgn_est` in topology events
  to match the Cognon Budget spec (NPS-5 §4.3 / NPS-AaaS §2.3).
- No changes to the ingress daemon's own code; the API surface and routing
  behaviour are identical to alpha.4.

---

## [1.0.0-alpha.4] — 2026-04-30

### Tracking the suite

- Bumps `LabAcacia.NPS.*` NuGet dependencies to `v1.0.0-alpha.4`,
  including the new `LabAcacia.NPS.NWP.Anchor` package which now ships
  **NPS-CR-0002** topology query types (`topology.snapshot` /
  `topology.stream`). The ingress daemon does **not** wire these yet —
  Anchor middleware integration remains the alpha.4 → alpha.5 work.
- No functional changes in the ingress daemon itself since alpha.3 — still the
  `:8080` HTTP listener + `/health` skeleton documenting the planned
  TLS / rate-limit / auth / CGN-debit / reputation-lookup path.

---

## [1.0.0-alpha.3] — 2026-04-26

### Added

- First release. Layer-2 public Internet ingress for the NPS suite.
- Phase 1 skeleton: HTTP listener on `:8080` and `/health` documenting
  the planned milestones, so operators can put nginx/Caddy/Traefik in
  front of it during alpha.3 and only flip behavior at alpha.4 → alpha.5.
- Multi-stage Docker image (non-root `npsing` user, exposes `:8080`).

### Deferred to alpha.4 / alpha.5

- TLS termination (alpha.3 ships plain HTTP — terminate upstream).
- Rate limiting (per-NID + per-customer + per-route).
- NeuronHub-customer authentication and per-customer CGN debit triggering.
- NPS-RFC-0004 reputation lookup before routing.
- `LabAcacia.NPS.NWP.Anchor` Anchor Node middleware wiring (NPS-CR-0001).
- DDoS defence (slow-loris timeout, request-rate caps, fail2ban hooks).

---

[1.0.0-alpha.5]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://github.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
