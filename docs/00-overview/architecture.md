# Architecture

## Purpose and lifecycle

Public distribution of the npsd, ingress, registry, and runner services; projected from NPS-Dev.

## Boundaries

npsd/: host identity and state.

nps-ingress/: TLS ingress.

nps-registry/: discovery.

nps-runner/: task execution.

docs/: operations.

Keep responsibilities and dependency direction explicit. Do not add layers or cross-repository sources of truth without a concrete requirement. Record architecture-significant decisions under `docs/04-decisions/`.
