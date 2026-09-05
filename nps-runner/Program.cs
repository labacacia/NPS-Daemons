// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// nps-runner — NPS task scheduler / FaaS runtime.
//
// Startup sequence:
//   1. Read RunnerOptions from environment variables.
//   2. Self-register with local npsd (POST /v1/agents, idempotent).
//   3. Long-poll the runner's inbox for JSON spawn-spec messages.
//   4. For each message: resolve a portable OCI SpawnSpec (or legacy direct
//      subprocess), execute it, capture stdout/stderr, enforce lifecycle bounds,
//      ack on terminal completion, and optionally notify the reply_to NID.
//
// Configuration (all via environment variables — see RunnerOptions):
//   NPSD_URL                           default: http://127.0.0.1:17433
//   NPS_RUNNER_POLL_INTERVAL_MS        default: 1000
//   NPS_RUNNER_MAX_CONCURRENT_WORKERS  default: 8
//   NPS_RUNNER_LOG_DIR                 default: /tmp/nps-runner-logs
//   NPS_RUNNER_AGENT_ID                default: nps-runner
//   NPS_RUNNER_OCI_RUNTIME              default: docker
//   NPS_REGISTRY_URL                    default: http://127.0.0.1:17436
//   NPS_RUNNER_STATE_PATH               default: /tmp/nps-runner-state/leases.db

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.Daemon.Runner;

var opts = RunnerOptions.FromEnvironment();

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddSingleton(opts)
    .AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
    .AddSingleton<SpawnSpecRemoteClient>()
    .AddSingleton<NpsdClient>()
    .AddSingleton<SpawnSpecResolver>()
    .AddSingleton(_ => new LeaseStore(opts.StatePath, opts.InstanceId))
    .AddSingleton<LeaseRenewalLoop>()
    .AddSingleton<WorkerManager>()
    .AddHostedService<InboxWatcher>();

await builder.Build().RunAsync();
