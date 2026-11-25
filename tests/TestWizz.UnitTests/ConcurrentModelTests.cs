using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TestWizz.AI;
using TestWizz.Cli;

namespace TestWizz.UnitTests;

/// <summary>
/// Concurrent model requests (#97): one shared <see cref="ModelClient"/> keeps every request and response isolated,
/// also when answers arrive out of order, some requests fail and others are cancelled. Failures are reported per
/// request with their own cause; nothing here assumes the model got worse under load.
/// </summary>
public sealed partial class ConcurrentModelClientTests : IAsyncLifetime
{
    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["echo"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("echo"),
        ["additionalProperties"] = false,
    };

    private FakeModelServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await FakeModelServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private static JsonChatRequest Request(string marker) => new("system", $"marker={marker}", "echo", Schema);

    private static string Marker(string body) => MarkerPattern().Match(body).Groups[1].Value;

    /// <summary>Answers each chat with its own marker after a delay chosen so later requests tend to finish first.</summary>
    private void EchoServer(Func<string, Task<object>>? behavior = null)
    {
        _server.AsOllama("m", "{}");
        _server.On("POST /api/chat", async http =>
        {
            var marker = Marker(FakeModelServer.Body(http));
            var outcome = behavior is null ? (object)marker : await behavior(marker);
            if (outcome is int status)
            {
                http.Response.StatusCode = status;
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5 * (30 - (Math.Abs(marker.GetHashCode(StringComparison.Ordinal)) % 30))), http.RequestAborted);
            http.Response.ContentType = "application/json";
            var content = outcome as string == "invalid-json" ? "{ not json"
                : outcome as string == "wrong-schema" ? """{"other":1}"""
                : new JsonObject { ["echo"] = (string)outcome }.ToJsonString();
            await http.Response.WriteAsync(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            }.ToJsonString(), http.RequestAborted);
        });
    }

    [Fact]
    public async Task InterleavedResponses_ReachTheirOwnCallers()
    {
        EchoServer();
        using var client = ModelClient.Create(TestSettings.Ollama(_server.BaseUri, "m"), _ => null);
        var markers = Enumerable.Range(0, 24).Select(i => $"req-{i:D2}-{Guid.NewGuid():N}").ToList();

        var responses = await Task.WhenAll(markers.Select(m => client.CompleteJsonAsync(Request(m), TestContext.Current.CancellationToken)));

        Assert.Equal(markers, responses.Select(r => (string)r.Content["echo"]!));
        Assert.Equal(markers.Count, _server.Requests.Count(r => r.Path == "/api/chat"));
    }

    [Fact]
    public async Task CancellingOneRequest_LeavesTheOthersIntact()
    {
        using var release = new SemaphoreSlim(0);
        EchoServer(async marker =>
        {
            if (marker == "slow")
            {
                await release.WaitAsync(TimeSpan.FromSeconds(20));
            }

            return marker;
        });
        using var client = ModelClient.Create(TestSettings.Ollama(_server.BaseUri, "m"), _ => null);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var slow = client.CompleteJsonAsync(Request("slow"), cancel.Token);
        var others = Enumerable.Range(0, 8).Select(i => client.CompleteJsonAsync(Request($"fast-{i}"), TestContext.Current.CancellationToken)).ToList();
        await cancel.CancelAsync();
        var failure = await Assert.ThrowsAsync<ModelClientException>(() => slow);
        var completed = await Task.WhenAll(others);
        release.Release();

        Assert.Equal(ModelFailure.Cancelled, failure.Failure);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"fast-{i}"), completed.Select(r => (string)r.Content["echo"]!));
    }

    [Fact]
    public async Task TimeoutOfOneRequest_IsReportedAsATimeoutOnlyForIt()
    {
        EchoServer(async marker =>
        {
            if (marker == "hang")
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            return marker;
        });
        using var client = ModelClient.Create(TestSettings.Ollama(_server.BaseUri, "m", timeoutSeconds: 1), _ => null);

        var hang = client.CompleteJsonAsync(Request("hang"), TestContext.Current.CancellationToken);
        var fine = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.CompleteJsonAsync(Request($"ok-{i}"), TestContext.Current.CancellationToken)));

        Assert.Equal(ModelFailure.Timeout, (await Assert.ThrowsAsync<ModelClientException>(() => hang)).Failure);
        Assert.All(fine, r => Assert.StartsWith("ok-", (string)r.Content["echo"]!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MixedFailures_AreAttributedToTheRequestThatFailed()
    {
        EchoServer(marker => Task.FromResult<object>(marker switch
        {
            "server-error" => 500,
            "busy" => 503,
            "invalid-json" or "wrong-schema" => marker,
            _ => marker,
        }));
        using var client = ModelClient.Create(TestSettings.Ollama(_server.BaseUri, "m"), _ => null);
        var markers = new[] { "ok-1", "server-error", "ok-2", "invalid-json", "busy", "wrong-schema", "ok-3" };

        var outcomes = await Task.WhenAll(markers.Select(async m =>
        {
            try
            {
                return (m, (string)(await client.CompleteJsonAsync(Request(m), TestContext.Current.CancellationToken)).Content["echo"]!);
            }
            catch (ModelClientException ex)
            {
                return (m, $"{ex.Failure}: {ex.Message}");
            }
        }));
        var byMarker = outcomes.ToDictionary(o => o.m, o => o.Item2, StringComparer.Ordinal);

        Assert.Equal("ok-1", byMarker["ok-1"]);
        Assert.Equal("ok-2", byMarker["ok-2"]);
        Assert.Equal("ok-3", byMarker["ok-3"]);
        Assert.Equal("HttpError: Server returned HTTP 500.", byMarker["server-error"]);
        Assert.Equal("HttpError: Server returned HTTP 503.", byMarker["busy"]);
        Assert.Equal("InvalidResponse: Model output is not valid JSON.", byMarker["invalid-json"]);
        Assert.StartsWith("InvalidResponse: Model output does not match the schema", byMarker["wrong-schema"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentDigestLookups_AreCoalescedIntoOneListing()
    {
        _server.AsOllama("m", "{}");
        using var client = ModelClient.Create(TestSettings.Ollama(_server.BaseUri, "m"), _ => null);

        var digests = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => client.ModelDigestAsync(TestContext.Current.CancellationToken)));

        Assert.Single(digests.Distinct());
        Assert.Single(_server.Requests, r => r.Path == "/api/tags");
    }

    [GeneratedRegex(@"marker=([A-Za-z0-9-]+)")]
    private static partial Regex MarkerPattern();
}

/// <summary>
/// The same controlled bootstrap workload at 1, 2 and 4 workers: a deterministic model that answers each request only
/// from that request, out of order. Every worker count must produce identical, valid proposals for every target.
/// </summary>
public sealed partial class ConcurrentBootstrapTests : IAsyncLifetime
{
    private const int Methods = 8;

    private readonly string _repo = Directory.CreateTempSubdirectory("testwizz-concurrency-repo-").FullName;
    private readonly List<string> _outputs = [];
    private FakeModelServer _server = null!;
    private int _inFlight;
    private int _maxInFlight;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeModelServer.StartAsync();
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/App/Maths.cs", "namespace App;\n\npublic class Maths\n{\n"
            + string.Concat(Enumerable.Range(1, Methods).Select(i => $"    public int M{i}() => {i};\n")) + "}\n");
        Write("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <ProjectReference Include="../../src/App/App.csproj" />
                <Using Include="Xunit" />
              </ItemGroup>
            </Project>
            """);
        _server.AsOllama("m", "{}");
        _server.On("POST /api/chat", async http =>
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, now);
            try
            {
                var user = (string)JsonNode.Parse(FakeModelServer.Body(http))!["messages"]![1]!["content"]!;
                var testClass = TestClass().Match(user).Groups[1].Value;
                var method = MethodName().Match(user).Groups[1].Value;
                var number = int.Parse(method[1..], System.Globalization.CultureInfo.InvariantCulture);
                // Higher-numbered targets answer sooner, so responses interleave whenever workers overlap.
                await Task.Delay(TimeSpan.FromMilliseconds(15 * (Methods + 1 - number)), http.RequestAborted);
                var code = $$"""
                    namespace App.Tests;

                    public class {{testClass}}
                    {
                        [Fact]
                        [Trait("Scenario", "S1")]
                        public void {{method}}_ReturnsItsNumber()
                        {
                            Assert.Equal({{number}}, new Maths().{{method}}());
                        }
                    }
                    """;
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = new JsonObject { ["code"] = code, ["assumptions"] = new JsonArray() }.ToJsonString(),
                    },
                }.ToJsonString(), http.RequestAborted);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        TestTempDirectories.Delete(_repo);
        foreach (var output in _outputs)
        {
            TestTempDirectories.Delete(output);
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (var current = Volatile.Read(ref target); value > current; current = Volatile.Read(ref target))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Target id → generated test source, for one bootstrap run with <paramref name="workers"/> workers.</summary>
    private SortedDictionary<string, string> Run(int workers)
    {
        var output = Directory.CreateTempSubdirectory($"testwizz-concurrency-{workers}-").FullName;
        _outputs.Add(output);
        File.WriteAllText(Path.Combine(_repo, "testwizz.json"), new JsonObject
        {
            ["version"] = 1,
            ["ai"] = new JsonObject { ["endpoint"] = _server.BaseUri.ToString(), ["model"] = "m", ["maxGenerationRepairs"] = 0 },
            ["output"] = output,
        }.ToJsonString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = new CliApp(stdout, stderr, _repo, _ => null)
            .Run(["bootstrap", "--all", "--generate-only", "--max-workers", workers.ToString(System.Globalization.CultureInfo.InvariantCulture), "--format", "json"],
                TestContext.Current.CancellationToken);

        Assert.True(exit == ExitCodes.Success, $"{workers} worker(s): {stdout}\n{stderr}");
        var items = JsonNode.Parse(stdout.ToString())!["result"]!["items"]!.AsArray();
        Assert.Equal(Methods, items.Count);
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            Assert.Equal("ok", (string?)item!["generation"]);
            var proposal = (string)item["proposal"]!;
            sources[(string)item["target"]!] = File.ReadAllText(Directory.EnumerateFiles(proposal, "*.cs", SearchOption.AllDirectories).Single());
        }

        return sources;
    }

    [Fact]
    public void OneTwoAndFourWorkers_ProduceIdenticalProposals()
    {
        var sequential = Run(1);
        Assert.Equal(1, _maxInFlight);

        foreach (var workers in new[] { 2, 4 })
        {
            Volatile.Write(ref _maxInFlight, 0);
            var parallel = Run(workers);

            Assert.True(_maxInFlight > 1, $"{workers} workers never overlapped (max in flight {_maxInFlight}); the comparison would not test concurrency.");
            Assert.Equal(sequential, parallel);
        }

        // Each proposal was built only from its own request: it calls the method of its own target.
        foreach (var (target, source) in sequential)
        {
            var method = target[(target.LastIndexOf('.') + 1)..];
            Assert.Contains($"new Maths().{method}()", source, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"Test class: (\w+)")]
    private static partial Regex TestClass();

    [GeneratedRegex(@"Method: public int (M\d+)\(\)")]
    private static partial Regex MethodName();
}
