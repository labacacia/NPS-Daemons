// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// nps-runner — NPS task scheduler / FaaS runtime.
//
// Startup sequence:
//   1. Read RunnerOptions from environment variables.
//   2. Self-register with local npsd (POST /v1/agents, idempotent).
//   3. Long-poll the runner's inbox for JSON spawn-spec messages.
//   4. For each message: spawn a subprocess, capture stdout/stderr to a log
//      file, enforce idle + max-runtime limits, ack the inbox message on exit,
//      and optionally POST a completion notification to the caller's reply_to NID.
//
// Configuration (all via environment variables — see RunnerOptions):
//   NPSD_URL                           default: http://127.0.0.1:17433
//   NPS_RUNNER_POLL_INTERVAL_MS        default: 1000
//   NPS_RUNNER_MAX_CONCURRENT_WORKERS  default: 8
//   NPS_RUNNER_LOG_DIR                 default: /tmp/nps-runner-logs
//   NPS_RUNNER_AGENT_ID                default: nps-runner

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
    .AddSingleton<LeaseStore>()
    .AddSingleton<WorkerManager>()
    .AddHostedService<InboxWatcher>();

await builder.Build().RunAsync();
