# Security Overview

Services process untrusted traffic and hold keys, sessions, certificates, tasks, and persistence state. Review mTLS, admission, databases, execution, containers, and secrets.

## Review rules

- Validate external input and fail closed.
- Keep secrets out of source, fixtures, logs, and errors.
- Bound network, parser, file, memory, and concurrency work where applicable.
- Pin and review automation dependencies.
- Add focused tests for changed security behavior.
- Follow [SECURITY.md](../../SECURITY.md) for reports.
