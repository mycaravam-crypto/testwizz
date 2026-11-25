using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TestWizz.Cli;

namespace TestWizz.UnitTests;

/// <summary>
/// Controlled bootstrap runs (Epic #104): a deterministic target limit (#98), an opt-in failure-rate circuit breaker
/// (#99) and aggregate metrics (#100), on a six-method repository with a fake model.
/// </summary>
public sealed partial class BootstrapControlTests : IAsyncLifetime
{
    private const int Methods = 6;

    private readonly string _repo = Directory.CreateTempSubdirectory("testwizz-control-repo-").FullName;
    private readonly string _output = Directory.CreateTempSubdirectory("testwizz-control-out-").FullName;
    private FakeModelServer _server = null!;
    private int _chats;

    /// <summary>Methods whose generated test is invalid (calls a method that does not exist).</summary>
    private HashSet<int> _invalid = [];

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
        Write("testwizz.json", new JsonObject
        {
            ["version"] = 1,
            ["ai"] = new JsonObject { ["endpoint"] = _server.BaseUri.ToString(), ["model"] = "m", ["maxGenerationRepairs"] = 0 },
            ["output"] = _output,
        }.ToJsonString());
        _server.AsOllama("m", "{}");
        _server.On("POST /api/chat", async http =>
        {
            Interlocked.Increment(ref _chats);
            var user = (string)JsonNode.Parse(FakeModelServer.Body(http))!["messages"]![1]!["content"]!;
            var method = MethodName().Match(user).Groups[1].Value;
            var number = int.Parse(method[1..], System.Globalization.CultureInfo.InvariantCulture);
            var called = _invalid.Contains(number) ? "Missing" : method;
            var code = $$"""
                namespace App.Tests;

                public class {{TestClass().Match(user).Groups[1].Value}}
                {
                    [Fact]
                    [Trait("Scenario", "S1")]
                    public void {{method}}_ReturnsItsNumber()
                    {
                        Assert.Equal({{number}}, new Maths().{{called}}());
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
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        TestTempDirectories.Delete(_repo);
        TestTempDirectories.Delete(_output);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (int ExitCode, JsonNode Json, string Stdout, string Stderr) Bootstrap(params string[] extra)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _repo, _ => null)
            .Run(["bootstrap", "--all", "--generate-only", "--max-workers", "1", "--format", "json", .. extra], TestContext.Current.CancellationToken);
        return (exit, JsonNode.Parse(stdout.ToString())!, stdout.ToString(), stderr.ToString());
    }

    private static string[] Targets(JsonNode json) => [.. json["result"]!["items"]!.AsArray().Select(i => (string)i!["target"]!)];

    private JsonArray CheckpointItems() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_output, "bootstrap-checkpoint.json")))!["items"]!.AsArray();

    // ---- #98 target limit ----

    [Fact]
    public void Limit_SelectsTheFirstTargetsInDiscoveryOrderAndReportsTheSelection()
    {
        var (exit, json, _, _) = Bootstrap("--limit", "3");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(["M:App.Maths.M1", "M:App.Maths.M2", "M:App.Maths.M3"], Targets(json));
        var result = json["result"]!;
        Assert.Equal(3, (int)result["total"]!);
        Assert.Equal(Methods, (int)result["eligible"]!);
        Assert.Equal(Methods, (int)result["selection"]!["discovered"]!);
        Assert.Equal(3, (int)result["selection"]!["selected"]!);
        Assert.Equal(3, (int)result["selection"]!["limit"]!);
        Assert.Equal(3, _chats);
    }

    [Fact]
    public void NoLimit_ProcessesEveryTarget()
    {
        var (_, json, _, _) = Bootstrap();

        Assert.Equal(Methods, Targets(json).Length);
        Assert.Null(json["result"]!["selection"]!["limit"]);
    }

    [Fact]
    public void Resume_WithTheSameLimitReusesWorkAndADifferentLimitIsRefused()
    {
        Assert.Equal(ExitCodes.Success, Bootstrap("--limit", "2").ExitCode);
        var after = _chats;

        var resumed = Bootstrap("--limit", "2", "--resume");
        Assert.Equal(ExitCodes.Success, resumed.ExitCode);
        Assert.Equal(after, _chats);
        Assert.Equal(2, (int)resumed.Json["result"]!["resumed"]!);

        var other = Bootstrap("--limit", "4", "--resume");
        Assert.NotEqual(ExitCodes.Success, other.ExitCode);
        Assert.StartsWith("bootstrap.", (string?)other.Json["error"]!["code"], StringComparison.Ordinal);
        Assert.Equal(after, _chats);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("three")]
    [InlineData("100001")]
    public void InvalidLimit_IsAUsageError(string value)
    {
        var (exit, json, _, _) = Bootstrap("--limit", value);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("usage", (string?)json["error"]!["code"]);
        Assert.Equal(0, _chats);
    }

    // ---- #99 circuit breaker ----

    [Fact]
    public void FailureRateAboveTheThreshold_StopsSchedulingAndResumeContinues()
    {
        _invalid = [.. Enumerable.Range(1, Methods)];

        var (exit, json, _, stderr) = Bootstrap("--max-failure-rate", "50", "--failure-sample", "2");

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(2, _chats);
        var breaker = json["result"]!["circuitBreaker"]!;
        Assert.True((bool)breaker["tripped"]!);
        Assert.Equal(100.0, (double)breaker["failureRatePercent"]!);
        Assert.Equal(4, (int)breaker["notScheduled"]!);
        Assert.Equal(4, (int)json["result"]!["skipped"]!);
        Assert.Equal(4, json["result"]!["items"]!.AsArray().Count(i => (string?)i!["generation"] == "skipped"));
        Assert.Contains("bootstrap stopped:", stderr, StringComparison.Ordinal);
        // Only finished work is checkpointed; the skipped targets are simply not there yet.
        Assert.Equal(2, CheckpointItems().Count);

        _invalid = [];
        var resumed = Bootstrap("--resume");
        Assert.Equal(ExitCodes.Success, resumed.ExitCode);
        Assert.Equal(Methods, (int)resumed.Json["result"]!["succeeded"]!);
        Assert.Equal(2 + Methods, _chats);
    }

    [Fact]
    public void FailureRateBelowTheThreshold_DoesNotStop()
    {
        _invalid = [2];

        var (exit, json, _, _) = Bootstrap("--max-failure-rate", "50", "--failure-sample", "2");

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.False((bool)json["result"]!["circuitBreaker"]!["tripped"]!);
        Assert.Equal(0, (int)json["result"]!["skipped"]!);
        Assert.Equal(Methods, _chats);
    }

    [Fact]
    public void WithoutTheOption_AFailingRunIsNeverStopped()
    {
        _invalid = [.. Enumerable.Range(1, Methods)];

        var (_, json, _, _) = Bootstrap();

        Assert.Null(json["result"]!["circuitBreaker"]);
        Assert.Equal(Methods, _chats);
        Assert.Equal(Methods, (int)json["result"]!["failed"]!);
    }

    [Theory]
    [InlineData("--max-failure-rate", "100")]
    [InlineData("--max-failure-rate", "-5")]
    [InlineData("--failure-sample", "3")]
    public void InvalidBreakerOptions_AreUsageErrors(string option, string value)
    {
        var (exit, json, _, _) = Bootstrap(option, value);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("usage", (string?)json["error"]!["code"]);
    }

    // ---- #100 metrics ----

    [Fact]
    public void Metrics_CountFailuresByCodeAndThroughputOfThisRun()
    {
        _invalid = [2, 5];

        var (exit, json, stdout, _) = Bootstrap();

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        var metrics = json["result"]!["metrics"]!;
        Assert.Equal(Methods, (int)metrics["generationFinished"]!);
        Assert.Equal(4, (int)metrics["generationValid"]!);
        Assert.Equal(2, (int)metrics["generationFailed"]!);
        Assert.Equal(0.667, (double)metrics["generationSuccessRate"]!);
        Assert.True((double)metrics["validProposalsPerMinute"]! > 0);
        Assert.Equal(2, (int)metrics["failuresByCode"]!["generation"]!["validation.unknown-member"]!);
        Assert.Empty(metrics["failuresByCode"]!["execution"]!.AsObject());
        // Codes only: no failure messages (which may quote source) in the aggregate.
        Assert.DoesNotContain("Missing", metrics.ToJsonString(), StringComparison.Ordinal);
        // stdout stays one JSON document.
        Assert.Equal(json.ToJsonString(), JsonNode.Parse(stdout)!.ToJsonString());
    }

    [Fact]
    public void Metrics_ExcludeRestoredResultsOnResume()
    {
        Assert.Equal(ExitCodes.Success, Bootstrap().ExitCode);

        var resumed = Bootstrap("--resume");

        var metrics = resumed.Json["result"]!["metrics"]!;
        Assert.Equal(0, (int)metrics["generationFinished"]!);
        Assert.Equal(Methods, (int)metrics["restoredExcluded"]!);
        Assert.Null(metrics["generationSuccessRate"]);
        Assert.Null(metrics["validProposalsPerMinute"]);
    }

    [Fact]
    public void TextSummary_ShowsMetricsAndSelection()
    {
        _invalid = [1];
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        new CliApp(stdout, stderr, _repo, _ => null)
            .Run(["bootstrap", "--all", "--generate-only", "--max-workers", "1", "--limit", "4"], TestContext.Current.CancellationToken);

        var text = stdout.ToString();
        Assert.Contains("Selection: 4 of 6 discovered targets (--limit 4).", text, StringComparison.Ordinal);
        Assert.Contains("Metrics (this run, 0 restored excluded): 75% generation success", text, StringComparison.Ordinal);
        Assert.Contains("Generation failures by code: validation.unknown-member ×1.", text, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"Test class: (\w+)")]
    private static partial Regex TestClass();

    [GeneratedRegex(@"Method: public int (M\d+)\(\)")]
    private static partial Regex MethodName();
}
