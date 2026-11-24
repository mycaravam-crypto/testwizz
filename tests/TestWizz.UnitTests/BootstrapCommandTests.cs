using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using TestWizz.Cli;
using TestWizz.Core.Running;

namespace TestWizz.UnitTests;

/// <summary>End-to-end bootstrap runs against a one-method repository and a fake model server (generation only).</summary>
public sealed class BootstrapCommandTests : IAsyncLifetime
{
    private const string Target = "M:App.Maths.One";

    private const string ValidTests = """
        namespace App.Tests;

        public class MathsTests
        {
            [Fact]
            [Trait("Scenario", "S1")]
            public void One_ReturnsOne()
            {
                Assert.Equal(1, new Maths().One());
            }
        }
        """;

    private readonly string _repo = Directory.CreateTempSubdirectory("testwizz-bootstrap-repo-").FullName;
    private readonly string _output = Directory.CreateTempSubdirectory("testwizz-bootstrap-out-").FullName;
    private FakeModelServer _server = null!;
    private int _chatRequests;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeModelServer.StartAsync();
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/App/Maths.cs", "namespace App;\n\npublic class Maths\n{\n    public int One() => 1;\n}\n");
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
            ["ai"] = new JsonObject { ["endpoint"] = _server.BaseUri.ToString(), ["model"] = "m" },
            ["output"] = _output,
        }.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        TestTempDirectories.Delete(_repo);
        TestTempDirectories.Delete(_output);
    }

    private string Checkpoint => Path.Combine(_output, "bootstrap-checkpoint.json");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Reply(string code) => new JsonObject { ["code"] = code, ["assumptions"] = new JsonArray() }.ToJsonString();

    /// <summary>Each chat request gets the next response: an HTTP status (int) or test code (string); the last one repeats.</summary>
    private void Model(params object[] responses)
    {
        _server.AsOllama("m", Reply(ValidTests));
        _server.On("POST /api/chat", async http =>
        {
            var index = Interlocked.Increment(ref _chatRequests) - 1;
            var response = responses[Math.Min(index, responses.Length - 1)];
            if (response is int status)
            {
                http.Response.StatusCode = status;
                return;
            }

            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply((string)response) },
            }.ToJsonString(), http.RequestAborted);
        });
    }

    private (int ExitCode, string Stdout, string Stderr) Bootstrap(CancellationToken cancellation, params string[] extra)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _repo, name => name == "TESTWIZZ_RETRY_DELAY_MS" ? "0" : null)
            .Run(["bootstrap", "--all", "--generate-only", "--format", "json", .. extra], cancellation);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private (int ExitCode, string Stdout, string Stderr) Bootstrap(params string[] extra) =>
        Bootstrap(TestContext.Current.CancellationToken, extra);

    private JsonObject CheckpointRow() =>
        JsonNode.Parse(File.ReadAllText(Checkpoint))!["items"]!.AsArray().Single()!.AsObject();

    private static IEnumerable<JsonObject> Events(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith('{'))
            .Select(line => JsonNode.Parse(line)!.AsObject());

    [Fact]
    public void ValidationFailure_ReportsReasonOnStderrAndPersistsItInCheckpoint()
    {
        Model(ValidTests.Replace("new Maths().One()", "new Maths().Two()", StringComparison.Ordinal));

        var (exit, stdout, stderr) = Bootstrap();

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        var result = JsonNode.Parse(stdout)!["result"]!;
        var item = result["items"]!.AsArray().Single()!;
        Assert.Equal("failed", (string?)item["generation"]);
        Assert.Equal("validation.unknown-member", (string?)item["failure"]!["code"]);
        Assert.Contains("Two", (string?)item["error"], StringComparison.Ordinal);
        var proposal = (string)item["proposal"]!;
        Assert.Contains($"generation failed for {Target}: validation.unknown-member:", stderr, StringComparison.Ordinal);
        Assert.Contains("proposal " + proposal, stderr, StringComparison.Ordinal);
        var row = CheckpointRow();
        Assert.Equal("validation.unknown-member", (string?)row["failure"]!["code"]);
        Assert.StartsWith("validation.unknown-member: ", (string?)row["error"], StringComparison.Ordinal);
        Assert.Equal(1, (int)result["generation"]!["failed"]!);
        Assert.Equal(0, (int)result["generation"]!["valid"]!);
        // Validation failures are never retried: one model call.
        Assert.Equal(1, _chatRequests);
        Assert.Null(row["attempts"]);
    }

    [Fact]
    public void ExistingCheckpoint_IsNeverReplacedWithoutResumeOrRestart()
    {
        Model(ValidTests);
        Assert.Equal(ExitCodes.Success, Bootstrap().ExitCode);
        var before = File.ReadAllBytes(Checkpoint);

        var (exit, stdout, _) = Bootstrap();

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("bootstrap.checkpoint_exists", (string?)JsonNode.Parse(stdout)!["error"]!["code"]);
        Assert.Equal(before, File.ReadAllBytes(Checkpoint));
        Assert.Equal(1, _chatRequests);

        var combined = Bootstrap("--resume", "--restart");
        Assert.Equal(ExitCodes.Error, combined.ExitCode);
        Assert.Equal("usage", (string?)JsonNode.Parse(combined.Stdout)!["error"]!["code"]);

        Assert.Equal(ExitCodes.Success, Bootstrap("--resume").ExitCode);
        Assert.Equal(1, _chatRequests);
        Assert.Equal(ExitCodes.Success, Bootstrap("--restart").ExitCode);
        Assert.Equal(2, _chatRequests);
    }

    [Fact]
    public void SeparateOutputDirectory_StartsFreshWithoutTouchingTheExistingCheckpoint()
    {
        Model(ValidTests);
        Assert.Equal(ExitCodes.Success, Bootstrap().ExitCode);
        var before = File.ReadAllBytes(Checkpoint);
        var other = Directory.CreateTempSubdirectory("testwizz-bootstrap-other-").FullName;
        try
        {
            Assert.Equal(ExitCodes.Success, Bootstrap("--out", other).ExitCode);
            Assert.True(File.Exists(Path.Combine(other, "bootstrap-checkpoint.json")));
            Assert.Equal(before, File.ReadAllBytes(Checkpoint));
        }
        finally
        {
            TestTempDirectories.Delete(other);
        }
    }

    [Fact]
    public void Resume_CountsRecoveredProposalsAsValidGenerations()
    {
        Model(ValidTests);
        Assert.Equal(ExitCodes.Success, Bootstrap().ExitCode);
        // A proposal generated but not yet finalized (e.g. interrupted before execution) is recovered, not regenerated.
        var state = JsonNode.Parse(File.ReadAllText(Checkpoint))!.AsObject();
        state["items"]![0]!["execution"] = "pending";
        File.WriteAllText(Checkpoint, state.ToJsonString());

        var (exit, stdout, stderr) = Bootstrap("--resume", "--progress-jsonl");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, _chatRequests);
        var result = JsonNode.Parse(stdout)!["result"]!;
        Assert.Equal(1, (int)result["recovered"]!);
        Assert.Equal(1, (int)result["generation"]!["valid"]!);
        Assert.Equal(1, (int)result["generation"]!["recovered"]!);
        Assert.Equal(0, (int)result["generation"]!["remaining"]!);
        Assert.Equal(1, (int)result["execution"]!["notRun"]!);
        var discovered = Events(stderr).First(e => (string?)e["phase"] == "discovered");
        Assert.Equal(1, (int)discovered["completed"]!);
        Assert.Equal(1, (int)discovered["generation"]!["valid"]!);
        Assert.Equal(1, (int)discovered["generation"]!["recovered"]!);
    }

    [Fact]
    public void TransientModelFailure_IsRetriedAndAttemptsAreRetained()
    {
        Model(503, ValidTests);

        var (exit, stdout, stderr) = Bootstrap("--progress-jsonl");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(2, _chatRequests);
        var row = CheckpointRow();
        Assert.Equal("ok", (string?)row["generation"]);
        Assert.Null(row["failure"]);
        var attempt = Assert.Single(row["attempts"]!.AsArray())!;
        Assert.Equal("ai.http_error", (string?)attempt["code"]);
        Assert.Equal(2, (int)Events(stderr).Single(e => (string?)e["phase"] == "generated")["attempts"]!);
        Assert.Equal("ok", (string?)JsonNode.Parse(stdout)!["status"]);
    }

    [Fact]
    public void RetriesAreBounded()
    {
        Model(503);

        var (exit, stdout, stderr) = Bootstrap("--retries", "1");

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(2, _chatRequests);
        var item = JsonNode.Parse(stdout)!["result"]!["items"]![0]!;
        Assert.Equal("error", (string?)item["generation"]);
        Assert.Equal("ai.http_error", (string?)item["failure"]!["code"]);
        Assert.Contains($"generation failed for {Target}: ai.http_error: Server returned HTTP 503.", stderr, StringComparison.Ordinal);
        Assert.Single(CheckpointRow()["attempts"]!.AsArray());
    }

    [Fact]
    public void NonRecoverableModelFailure_IsNotRetried()
    {
        Model(401);

        var (exit, _, stderr) = Bootstrap();

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(1, _chatRequests);
        Assert.Equal("ai.auth_failed", (string?)CheckpointRow()["failure"]!["code"]);
        Assert.Contains("ai.auth_failed", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRetryCount_IsUsageError()
    {
        var (exit, stdout, _) = Bootstrap("--retries", "6");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("usage", (string?)JsonNode.Parse(stdout)!["error"]!["code"]);
    }

    [Fact]
    public void CancellationDuringRetryBackoff_StopsPromptlyAndResumeRecovers()
    {
        Model(503);
        using var cancellation = new CancellationTokenSource();
        _server.On("POST /api/chat", http =>
        {
            Interlocked.Increment(ref _chatRequests);
            http.Response.StatusCode = 503;
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            return Task.CompletedTask;
        });
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // A 60 s back-off would block for a minute if the wait ignored cancellation.
        var exit = new CliApp(stdout, stderr, _repo, name => name == "TESTWIZZ_RETRY_DELAY_MS" ? "60000" : null)
            .Run(["bootstrap", "--all", "--generate-only", "--format", "json"], cancellation.Token);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
        Assert.Equal("cancelled", (string?)JsonNode.Parse(stdout.ToString())!["error"]!["code"]);
        Assert.Equal(1, _chatRequests);
        Assert.True(File.Exists(Checkpoint));

        Model(ValidTests);
        var resumed = Bootstrap("--resume");
        Assert.Equal(ExitCodes.Success, resumed.ExitCode);
        Assert.Equal("ok", (string?)CheckpointRow()["generation"]);
    }

    [Fact]
    public void Concurrency_IsAdaptiveByDefaultAndFixedWhenMaxWorkersIsExplicit()
    {
        Model(ValidTests);

        var adaptive = JsonNode.Parse(Bootstrap().Stdout)!["result"]!;
        var other = Directory.CreateTempSubdirectory("testwizz-bootstrap-fixed-").FullName;
        try
        {
            var fixedRun = JsonNode.Parse(Bootstrap("--max-workers", "2", "--out", other).Stdout)!["result"]!;

            Assert.True((bool)adaptive["generationWorkersAdaptive"]!);
            Assert.Equal(4, (int)adaptive["generationWorkersMax"]!);
            Assert.False((bool)fixedRun["generationWorkersAdaptive"]!);
            Assert.Equal(2, (int)fixedRun["generationWorkersMax"]!);
        }
        finally
        {
            TestTempDirectories.Delete(other);
        }
    }

    [Fact]
    public void Verification_OverlapsGenerationAndTargetsShareOneRestore()
    {
        Write("src/App/Maths.cs", "namespace App;\n\npublic class Maths\n{\n    public int One() => 1;\n\n    public int Two() => 2;\n}\n");
        // The SDK image for the containers follows the test project's target framework.
        var project = Path.Combine(_repo, "tests", "App.Tests", "App.Tests.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("<ItemGroup>",
            "<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n  <ItemGroup>", StringComparison.Ordinal));
        const string validTwo = """
            namespace App.Tests;

            public class MathsTests
            {
                [Fact]
                [Trait("Scenario", "S1")]
                public void Two_ReturnsTwo()
                {
                    Assert.Equal(2, new Maths().Two());
                }
            }
            """;
        using var containerStarted = new ManualResetEventSlim();
        var runtime = new FakeRuntime
        {
            Respond = spec =>
            {
                containerStarted.Set();
                if (FakeRuntime.Phase(spec) == "test")
                    FakeRuntime.WriteTrx(spec, $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testId="a" testName="App.Tests.MathsTests.One_ReturnsOne" duration="00:00:00.01" outcome="Passed" /><UnitTestResult testId="b" testName="App.Tests.MathsTests.Two_ReturnsTwo" duration="00:00:00.01" outcome="Passed" /></Results><TestDefinitions><UnitTest name="App.Tests.MathsTests.One_ReturnsOne" id="a"><TestMethod className="App.Tests.MathsTests" name="One_ReturnsOne" /></UnitTest><UnitTest name="App.Tests.MathsTests.Two_ReturnsTwo" id="b"><TestMethod className="App.Tests.MathsTests" name="Two_ReturnsTwo" /></UnitTest></TestDefinitions></TestRun>""");
                return new ContainerResult(0, "ok", TimeSpan.Zero, false);
            },
        };
        var overlapped = false;
        _server.AsOllama("m", Reply(ValidTests));
        _server.On("POST /api/chat", async http =>
        {
            var index = Interlocked.Increment(ref _chatRequests) - 1;
            // The second target's model response waits until the first target's verification has started a
            // container. Without overlapping stages this would only time out.
            if (index == 1) overlapped = await Task.Run(() => containerStarted.Wait(TimeSpan.FromSeconds(20)), http.RequestAborted);
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply(index == 0 ? ValidTests : validTwo) },
            }.ToJsonString(), http.RequestAborted);
        });

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _repo, name => name == "TESTWIZZ_RETRY_DELAY_MS" ? "0" : null, _ => runtime)
            .Run(["bootstrap", "--all", "--max-workers", "1", "--progress-jsonl", "--format", "json"], TestContext.Current.CancellationToken);

        Assert.True(exit == ExitCodes.Success, stdout.ToString() + stderr);
        Assert.True(overlapped);
        var result = JsonNode.Parse(stdout.ToString())!["result"]!;
        Assert.Equal(2, (int)result["execution"]!["passed"]!);
        // One restore for both targets; each still builds and tests in its own workspace.
        Assert.Equal(["restore", "build", "test", "build", "test"], runtime.Runs.Select(r => FakeRuntime.Phase(r.Spec)));
        Assert.Equal(2, runtime.Runs.Select(r => r.Spec.Workspace).Skip(1).Distinct().Count());
        // Generation and verification events interleave; each phase's own counter still only grows.
        var events = Events(stderr.ToString()).ToList();
        foreach (var phase in new[] { "generated", "finalized" })
        {
            var completed = events.Where(e => (string?)e["phase"] == phase).Select(e => (int)e["completed"]!).ToList();
            Assert.Equal([1, 2], completed);
        }

        var items = JsonNode.Parse(File.ReadAllText(Checkpoint))!["items"]!.AsArray();
        Assert.All(items, row => Assert.Equal("passed", (string?)row!["execution"]));
    }

    [Fact]
    public void Help_DocumentsRestartAndRetries()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        new CliApp(stdout, stderr, _repo).Run(["bootstrap", "--help"], TestContext.Current.CancellationToken);

        var help = stdout.ToString();
        Assert.Contains("--restart", help, StringComparison.Ordinal);
        Assert.Contains("--retries", help, StringComparison.Ordinal);
        Assert.Contains("--resume | --restart", help, StringComparison.Ordinal);
    }
}
