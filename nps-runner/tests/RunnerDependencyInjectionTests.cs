// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class RunnerDependencyInjectionTests
{
    [Fact]
    public void Production_service_graph_is_constructible()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"nps-runner-di-{Guid.NewGuid():N}");
        var options = new RunnerOptions
        {
            StatePath = Path.Combine(root, "leases.db"),
            LogDir = Path.Combine(root, "logs"),
        };
        try
        {
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton(options)
                .AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                .AddSingleton<SpawnSpecRemoteClient>()
                .AddSingleton<NpsdClient>()
                .AddSingleton<SpawnSpecResolver>()
                .AddSingleton(_ => new LeaseStore(options.StatePath, options.InstanceId))
                .AddSingleton<LeaseRenewalLoop>()
                .AddSingleton<WorkerManager>()
                .AddSingleton<InboxWatcher>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            Assert.NotNull(provider.GetRequiredService<InboxWatcher>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
