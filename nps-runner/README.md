English | [中文版](./README.cn.md)

# `nps-runner` — NPS Daemon (task scheduler / FaaS runtime)

> Watches the local [`npsd`](../npsd/) inbox for JSON spawn-spec messages,
> spawns worker subprocesses on demand, and manages their full lifecycle
> (stdout/stderr capture, idle + max-runtime timeouts, concurrency cap,
> completion notifications).  See
> [`docs/daemons/architecture.md`](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.md)
> for the broader six-daemon topology.

## Status — alpha.18 source / alpha.19 debt-closure candidate

Inbox watching, portable OCI SpawnSpec execution, reference resolution,
periodic lease renewal, lease-loss worker cancellation, and legacy
direct-subprocess compatibility are implemented. Task leases and terminal
deduplication use a durable SQLite state file. Runner processes configured with
the same file coordinate claims, fence restarted processes with a fresh instance
identity, reclaim expired leases, and preserve terminal dedup across restart.
Full Layer-3 TaskFrame DAG/Saga certification is not claimed; the case-by-case
boundary is recorded in
[`conformance/NPS-NODE-L3-MANIFEST.json`](./conformance/NPS-NODE-L3-MANIFEST.json)
and the capability contract is
[`conformance/NPS-RUNNER-CAPABILITIES.json`](./conformance/NPS-RUNNER-CAPABILITIES.json).

## Quick start

```bash
# From the monorepo root
dotnet run --project tools/daemons/nps-runner/NpsRunner.csproj
# Logs: "nps-runner ready — NID=<nid>  npsd=http://127.0.0.1:17433 ..."
```

### Docker

```bash
docker build -f tools/daemons/nps-runner/Dockerfile -t labacacia/nps-runner:1.0.0-alpha.19 .
docker run --rm \
  -e NPSD_URL=http://127.0.0.1:17433 \
  -e NPS_RUNNER_LOG_DIR=/var/log/nps-runner \
  -v nps-runner-state:/var/lib/nps-runner \
  labacacia/nps-runner:1.0.0-alpha.19
```

## Configuration

All configuration is via environment variables.

| Variable | Default | Purpose |
|---|---|---|
| `NPSD_URL` | `http://127.0.0.1:17433` | npsd base URL |
| `NPS_RUNNER_AGENT_ID` | `nps-runner` | Identifier used when self-registering the runner's sub-NID |
| `NPS_RUNNER_POLL_INTERVAL_MS` | `1000` | Inbox poll interval (also sets the long-poll `wait` window) |
| `NPS_RUNNER_MAX_CONCURRENT_WORKERS` | `8` | Cap on simultaneously running worker processes |
| `NPS_RUNNER_LOG_DIR` | `/tmp/nps-runner-logs` | Directory for per-worker `{task_id}.log` files |
| `NPS_RUNNER_STATE_PATH` | `/tmp/nps-runner-state/leases.db` (image: `/var/lib/nps-runner/leases.db`) | Persistent SQLite lease and terminal-dedup database; replicas coordinate only when configured with the same correctly locked storage |
| `NPS_RUNNER_OCI_RUNTIME` | `docker` | OCI-compatible CLI used for portable SpawnSpecs |
| `NPS_REGISTRY_URL` | `http://127.0.0.1:17436` | NDP registry used to resolve `nwp://` SpawnSpec references |

## How it works

### 1. Self-registration

On startup nps-runner calls `POST /v1/agents` on the local npsd with
`identifier = NPS_RUNNER_AGENT_ID` and `capabilities = ["spawn"]`.  The call is
idempotent — a 409 conflict just returns the existing NID.  The assigned NID is
logged at boot; send task messages to that NID.

### 2. Spawn-spec message format

Deposit a message to the runner's inbox:

```
POST /v1/inbox/{runner-nid}
Content-Type: application/json
X-Nps-Inbox-Ttl-Seconds: 3600
```

Portable OCI body:

```json
{
  "task_id": "abc123",
  "reply_to": "<nid-to-notify-on-completion>",
  "image": "ghcr.io/example/worker@sha256:0123456789abcdef",
  "command": ["worker", "--task", "abc123"],
  "env": {
    "NPS_TASK_ID": "abc123"
  },
  "resource_limits": {
    "cpu": "500m",
    "memory": "512Mi",
    "cgn_budget": 10000
  },
  "idle_timeout_seconds": 600,
  "max_runtime_seconds": 3600
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `task_id` | string | no | Caller-supplied ID; auto-generated (UUID) if absent |
| `reply_to` | string | no | NID that receives a completion notification on worker exit |
| `image` | string | **yes** | OCI image reference; a digest-pinned reference is recommended |
| `command` | string[] | no | OCI entrypoint/argument override appended after the image |
| `env` | object | no | Environment variables passed to the container |
| `resource_limits` | object | no | Optional `cpu`, `memory`, and `cgn_budget` limits |
| `idle_timeout_seconds` | int | no | Kill worker after N seconds of no stdout/stderr output |
| `max_runtime_seconds` | int | no | Hard wall-clock limit; default ceiling is 4 h |

The inbox body may instead contain a `spawn_spec_ref`. Supported references are
an inline base64url document (`spawnspec:<payload>`), an HTTPS URL, or an
`nwp://` URL resolved through `NPS_REGISTRY_URL`. Resolved documents are limited
to 64 KiB and are validated with the same fail-closed parser. Remote retrieval
requires HTTPS, rejects user-info and non-public DNS answers, connects only to
a validated address while preserving TLS hostname verification, and rechecks
every redirect with a five-hop limit.

The pre-alpha.17 direct-subprocess body remains available for compatibility:

```json
{
  "task_id": "abc123",
  "command": "claude",
  "args": ["remote-control", "--name", "worker-abc"],
  "work_dir": "/home/wind/project",
  "env": {"CLAUDE_CODE_SANDBOXED": "1"},
  "max_runtime_seconds": 3600
}
```

In this legacy shape, `command` is a required string, `args` is a string array,
and `work_dir` defaults to the runner's current working directory.

### 3. Worker lifecycle

1. Message arrives → resolve and validate the spawn spec → atomically claim its durable task lease.
2. A live claim conflict stays unacked. A durable terminal duplicate is acked without execution.
3. After a granted or expired-lease claim, acquire a worker slot and start the OCI container or legacy subprocess. If at cap, release the owned lease and leave the message unacked.
4. stdout + stderr are captured to `NPS_RUNNER_LOG_DIR/{task_id}.log`.
5. A renewal loop refreshes the durable lease at half its bounded window;
   the worker monitor separately checks idle and max-runtime deadlines every 5 s.
6. On terminal exit, atomically record durable dedup state and release the lease;
   only then ack the inbox message and, when `reply_to` is set, POST a completion
   notification (JSON, `Content-Type: application/json`) to that NID.
7. If lease ownership is lost or expires, cancel the stale worker and do not
   record terminal state, ack, or notify.

For restart recovery, `NPS_RUNNER_STATE_PATH` must live on persistent storage.
Every coordinating runner process must point to the same SQLite file. Cross-host
use additionally requires storage with correct SQLite locking and durability;
no generic network-filesystem guarantee is claimed.

### 4. Completion notification

When a worker exits and `reply_to` is set, nps-runner deposits:

```json
{
  "task_id": "abc123",
  "exit_code": 0,
  "killed_reason": null,
  "log_path": "/tmp/nps-runner-logs/abc123.log",
  "started_at": "2026-05-03T10:00:00.000Z",
  "finished_at": "2026-05-03T10:05:00.000Z"
}
```

`killed_reason` is one of `"idle_timeout"`, `"max_runtime"`, `"shutdown"`,
`"exception"`, or `null` (clean exit).

## Why a separate daemon (and not part of `npsd`)

See [architecture §1](https://github.com/labacacia/NPS-Daemons/blob/main/docs/architecture.md): resource profile,
failure isolation, and trust boundary all differ significantly between the
protocol layer and the worker scheduler — a worker crash must not take the NCP
layer down, and the scheduler runs user-supplied commands that the protocol layer
must not have a permission surface for.

## Verification

From the monorepo root, run:

```bash
dotnet test tools/daemons/nps-runner/tests/NpsRunner.Tests.csproj -c Release
```

The standalone daemon bundle uses
`dotnet test nps-runner/tests/NpsRunner.Tests.csproj -c Release` and includes
the shared NOP fixtures under `spec/conformance/nop`.

## License

Apache-2.0 — see `LICENSE` at the repository root.
