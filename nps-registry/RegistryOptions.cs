// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Daemon.Registry;

/// <summary>
/// Runtime configuration for the <c>nps-registry</c> daemon. Read from the environment by
/// <see cref="FromEnvironment"/> in production; constructed directly in tests.
/// </summary>
public sealed record RegistryOptions
{
    /// <summary>Bind address. <c>0.0.0.0</c> listens on every interface.</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>TCP port. NDP optional-dedicated registry port.</summary>
    public int Port { get; init; } = 17436;

    /// <summary>SQLite file path; <c>null</c>/empty selects an ephemeral in-memory store.</summary>
    public string? SqlitePath { get; init; }

    /// <summary>
    /// This registry's own NID. Used for NDP §9 federation loop detection — a forwarded
    /// AnnounceFrame whose <c>ndp-forwarded-by</c> list already contains this NID is a loop.
    /// </summary>
    public string Nid { get; init; } = "urn:nps:agent:nps-registry:local";

    /// <summary>
    /// NDP §7.3 registry security profile: <c>local-dev</c> / <c>org-private</c> /
    /// <c>public-federated</c>. Federation (§7.6) is permitted only in <c>public-federated</c>.
    /// </summary>
    public string Profile { get; init; } = ProfileLocalDev;

    /// <summary>NDP §7.3 <c>local-dev</c> profile — single host, no federation.</summary>
    public const string ProfileLocalDev = "local-dev";

    /// <summary>NDP §7.3 <c>org-private</c> profile — single organisation, no federation.</summary>
    public const string ProfileOrgPrivate = "org-private";

    /// <summary>NDP §7.3 <c>public-federated</c> profile — federation permitted (§7.6).</summary>
    public const string ProfilePublicFederated = "public-federated";

    /// <summary>True when this registry may import cluster state from federated peers (NDP §7.6).</summary>
    public bool FederationEnabled =>
        string.Equals(Profile, ProfilePublicFederated, StringComparison.Ordinal);

    /// <summary>Builds options from <c>NPSREGISTRY_*</c> environment variables.</summary>
    public static RegistryOptions FromEnvironment()
    {
        var defaults = new RegistryOptions();
        return defaults with
        {
            Host       = Environment.GetEnvironmentVariable("NPSREGISTRY_HOST") ?? defaults.Host,
            Port       = int.TryParse(Environment.GetEnvironmentVariable("NPSREGISTRY_PORT"), out var p)
                            ? p : defaults.Port,
            SqlitePath = Environment.GetEnvironmentVariable("NPSREGISTRY_SQLITE_PATH"),
            Nid        = Environment.GetEnvironmentVariable("NPSREGISTRY_NID") ?? defaults.Nid,
            Profile    = Environment.GetEnvironmentVariable("NPSREGISTRY_PROFILE") ?? defaults.Profile,
        };
    }
}
