// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using NPS.Daemon.Runner;
using Xunit;

namespace NPS.Daemon.Runner.Tests;

public sealed class SpawnSpecResolverTests
{
    private const string PortableSpec = """
        {
          "image": "registry.example.test/worker:1.0",
          "command": ["/app/worker", "--serve"],
          "env": {"MODE": "test"},
          "resource_limits": {
            "cpu": "500m",
            "memory": "512Mi",
            "cgn_budget": 50000
          },
          "idle_timeout_seconds": 30,
          "max_runtime_seconds": 300
        }
        """;

    [Fact]
    public async Task Inline_reference_resolves_and_envelope_identity_wins()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(PortableSpec))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var envelope = $$"""
            {
              "task_id": "task-inline",
              "reply_to": "urn:nps:reply",
              "spawn_spec_ref": "spawnspec:{{encoded}}"
            }
            """;
        var resolver = CreateResolver(new RejectingHandler());

        var result = await resolver.ResolveEnvelopeAsync(envelope, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("task-inline", result.Spec!.TaskId);
        Assert.Equal("urn:nps:reply", result.Spec.ReplyTo);
        Assert.Equal("registry.example.test/worker:1.0", result.Spec.Image);
        Assert.Equal(["/app/worker", "--serve"], result.Spec.ContainerCommand);
        Assert.Equal("500m", result.Spec.ResourceLimits!.Cpu);
    }

    [Fact]
    public async Task Https_reference_fetches_and_validates_the_portable_spec()
    {
        var handler = new RoutingHandler(request =>
        {
            Assert.Equal("https://specs.example.test/worker.json", request.RequestUri!.AbsoluteUri);
            return Json(PortableSpec);
        });
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveEnvelopeAsync(
            """{"task_id":"task-https","spawn_spec_ref":"https://specs.example.test/worker.json"}""",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("task-https", result.Spec!.TaskId);
        Assert.Equal("registry.example.test/worker:1.0", result.Spec.Image);
    }

    [Fact]
    public async Task Nwp_reference_resolves_through_registry_then_fetches_over_https()
    {
        var requests = new List<Uri>();
        var handler = new RoutingHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "127.0.0.1")
            {
                return Json("""
                    {
                      "target": "nwp://spawn.example.test/agents/demo",
                      "resolved": {
                        "host": "specs.example.test",
                        "port": 443,
                        "ttl": 60
                      }
                    }
                    """);
            }
            return Json(PortableSpec);
        });
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveEnvelopeAsync(
            """{"spawn_spec_ref":"nwp://spawn.example.test/agents/demo"}""",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, requests.Count);
        Assert.Equal("http", requests[0].Scheme);
        Assert.Equal("/v1/resolve", requests[0].AbsolutePath);
        Assert.Contains("target=nwp%3A%2F%2Fspawn.example.test%2Fagents%2Fdemo", requests[0].Query);
        Assert.Equal("https://specs.example.test/agents/demo", requests[1].AbsoluteUri);
    }

    [Fact]
    public async Task Plain_http_and_oversized_content_fail_closed()
    {
        var resolver = CreateResolver(new RoutingHandler(_ => Json(
            new string('x', SpawnSpecResolver.MaxContentBytes + 1))));

        var plain = await resolver.ResolveEnvelopeAsync(
            """{"spawn_spec_ref":"http://specs.example.test/worker.json"}""",
            CancellationToken.None);
        var oversized = await resolver.ResolveEnvelopeAsync(
            """{"spawn_spec_ref":"https://specs.example.test/worker.json"}""",
            CancellationToken.None);

        Assert.False(plain.Success);
        Assert.Equal(SpawnSpecParser.InvalidCode, plain.ErrorCode);
        Assert.False(oversized.Success);
        Assert.Equal(SpawnSpecParser.InvalidCode, oversized.ErrorCode);
    }

    [Theory]
    [InlineData("https://127.0.0.1/worker.json")]
    [InlineData("https://10.0.0.8/worker.json")]
    [InlineData("https://[::1]/worker.json")]
    [InlineData("https://localhost/worker.json")]
    [InlineData("https://user:secret@specs.example.test/worker.json")]
    public async Task Unsafe_https_destination_shapes_fail_before_the_request(string reference)
    {
        var requests = 0;
        var resolver = CreateResolver(new RoutingHandler(_ =>
        {
            requests++;
            return Json(PortableSpec);
        }));

        var result = await resolver.ResolveEnvelopeAsync(
            $$"""{"spawn_spec_ref":"{{reference}}"}""",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SpawnSpecParser.InvalidCode, result.ErrorCode);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Redirect_destination_is_revalidated()
    {
        var requests = 0;
        var resolver = CreateResolver(new RoutingHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://127.0.0.1/worker.json") },
            };
        }));

        var result = await resolver.ResolveEnvelopeAsync(
            """{"spawn_spec_ref":"https://specs.example.test/worker.json"}""",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SpawnSpecParser.InvalidCode, result.ErrorCode);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    public void Remote_transport_classifies_public_addresses(string value, bool expected)
    {
        Assert.Equal(
            expected,
            SpawnSpecRemoteClient.IsPublicAddress(IPAddress.Parse(value)));
    }

    private static SpawnSpecResolver CreateResolver(HttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            new RunnerOptions { RegistryUrl = "http://127.0.0.1:17436" });

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RoutingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = route(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Inline resolution must not use HTTP.");
    }
}
