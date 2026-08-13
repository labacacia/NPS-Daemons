// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Runner;

internal sealed record RunnerOptions
{
    public string NpsdUrl { get; init; } = "http://127.0.0.1:17433";
    public int PollIntervalMs { get; init; } = 1000;
    public int MaxConcurrentWorkers { get; init; } = 8;
    public string LogDir { get; init; } = "/tmp/nps-runner-logs";
    public string AgentId { get; init; } = "nps-runner";
    public string OciRuntime { get; init; } = "docker";
    public string RegistryUrl { get; init; } = "http://127.0.0.1:17436";

    public static RunnerOptions FromEnvironment() => new()
    {
        NpsdUrl = Env("NPSD_URL", "http://127.0.0.1:17433"),
        PollIntervalMs = IntEnv("NPS_RUNNER_POLL_INTERVAL_MS", 1000),
        MaxConcurrentWorkers = IntEnv("NPS_RUNNER_MAX_CONCURRENT_WORKERS", 8),
        LogDir = Env("NPS_RUNNER_LOG_DIR", "/tmp/nps-runner-logs"),
        AgentId = Env("NPS_RUNNER_AGENT_ID", "nps-runner"),
        OciRuntime = Env("NPS_RUNNER_OCI_RUNTIME", "docker"),
        RegistryUrl = Env("NPS_REGISTRY_URL", "http://127.0.0.1:17436"),
    };

    static string Env(string key, string def) => System.Environment.GetEnvironmentVariable(key) ?? def;
    static int IntEnv(string key, int def) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(key), out var v) ? v : def;
}
