English | [中文版](./README.cn.md)

# `nps-runner` — NPS Daemon (task scheduler / FaaS runtime)

> Watches the local [`npsd`](../npsd/) inbox for JSON spawn-spec messages,
> spawns worker subprocesses on demand, and manages their full lifecycle
> (stdout/stderr capture, idle + max-runtime timeouts, concurrency cap,
> completion notifications).  See
> [`docs/daemons/architecture.md`](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md)
> for the broader six-daemon topology.

## Status — alpha.6

Inbox watcher + worker spawn fully implemented.

## Quick start

```bash
# From the monorepo root
dotnet run --project tools/daemons/nps-runner/NpsRunner.csproj
# Logs: "nps-runner ready — NID=<nid>  npsd=http://127.0.0.1:17433 ..."
```

### Docker

```bash
docker build -f tools/daemons/nps-runner/Dockerfile -t labacacia/nps-runner:1.0.0-alpha.17 .
docker run --rm \
  -e NPSD_URL=http://127.0.0.1:17433 \
  -e NPS_RUNNER_LOG_DIR=/var/log/nps-runner \
  labacacia/nps-runner:1.0.0-alpha.17
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

Body (spawn spec, all fields except `command` are optional):

```json
{
  "task_id": "abc123",
  "reply_to": "<nid-to-notify-on-completion>",
  "command": "claude",
  "args": ["remote-control", "--name", "worker-abc", "--port", "1380", "--permission-mode", "bypassPermissions"],
  "work_dir": "/home/wind/project",
  "env": {
    "ANTHROPIC_API_KEY": "...",
    "CLAUDE_CODE_SANDBOXED": "1"
  },
  "idle_timeout_seconds": 600,
  "max_runtime_seconds": 3600
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `task_id` | string | no | Caller-supplied ID; auto-generated (UUID) if absent |
| `reply_to` | string | no | NID that receives a completion notification on worker exit |
| `command` | string | **yes** | Executable name or absolute path |
| `args` | string[] | no | Positional arguments |
| `work_dir` | string | no | Working directory; defaults to nps-runner's CWD |
| `env` | object | no | Extra env vars merged on top of the inherited environment |
| `idle_timeout_seconds` | int | no | Kill worker after N seconds of no stdout/stderr output |
| `max_runtime_seconds` | int | no | Hard wall-clock limit; default ceiling is 4 h |

### 3. Worker lifecycle

1. Message arrives → deserialize spawn spec → check concurrency cap.
2. If at cap: message stays unacked and reappears next poll cycle.
3. Otherwise: spawn subprocess with the given command / args / env / work_dir.
4. stdout + stderr are captured to `NPS_RUNNER_LOG_DIR/{task_id}.log`.
5. Monitor loop (5 s tick) checks idle timeout and max-runtime deadline.
6. On exit (any cause): ack the inbox message; if `reply_to` is set, POST a
   completion notification (JSON, `Content-Type: application/json`) to that NID.

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

See [architecture §1](https://github.com/labacacia/nps-daemons/blob/main/docs/architecture.md): resource profile,
failure isolation, and trust boundary all differ significantly between the
protocol layer and the worker scheduler — a worker crash must not take the NCP
layer down, and the scheduler runs user-supplied commands that the protocol layer
must not have a permission surface for.

## License

Apache-2.0 — see `LICENSE` at the repository root.
