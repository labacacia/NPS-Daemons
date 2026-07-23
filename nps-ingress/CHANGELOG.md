English | [中文版](./CHANGELOG.cn.md)

# Changelog — `nps-ingress`

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
