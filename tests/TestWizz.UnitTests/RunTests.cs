using System.Diagnostics;
using System.Text.Json.Nodes;
using TestWizz.AI;
using TestWizz.Cli;
using TestWizz.Core.Configuration;
using TestWizz.Core.Generation;
using TestWizz.Core.Planning;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

internal static class Bundles
{
    /// <summary>Validates <paramref name="code"/> like <c>generate</c> would and writes it as a proposal bundle.</summary>
    public static string Create(string repo, string proposals, string code, string target = "Calculator.Divide")
    {
        var provider = RoslynContextProvider.Load(repo);
        var context = provider.GetContext(target, "test");
        var extractor = new CSharpScenarioExtractor();
        var plan = ScenarioPlanner.Create(context, extractor.Version, extractor.Extract(context));
        var conventions = XUnitConventions.Detect(repo, context);
        code = code.ReplaceLineEndings("\n").TrimEnd() + "\n";
        var result = XUnitGenerator.Assemble(provider, context, plan, conventions, code, [], new GenerationProvenance("ollama", "m", null, "xunit/v1", "x"));
        return ProposalBundle.Write(repo, proposals, context, plan, result).Directory;
    }

    public static string Trx(params (string Method, string Outcome, string? Message)[] results)
    {
        var definitions = string.Concat(results.Select((r, i) =>
            $"""<UnitTest name="Shop.Tests.CalculatorDivideTests.{r.Method}" id="id{i}"><TestMethod className="Shop.Tests.CalculatorDivideTests" name="{r.Method}" /></UnitTest>"""));
        var outcomes = string.Concat(results.Select((r, i) =>
            $"""<UnitTestResult testId="id{i}" testName="Shop.Tests.CalculatorDivideTests.{r.Method}" duration="00:00:00.0120000" outcome="{r.Outcome}">{(r.Message is null ? "" : $"<Output><ErrorInfo><Message>{r.Message}</Message><StackTrace>at X</StackTrace></ErrorInfo></Output>")}</UnitTestResult>"""));
        return $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{outcomes}</Results><TestDefinitions>{definitions}</TestDefinitions></TestRun>""";
    }
}

/// <summary>Scripted container runtime: records every container and what its workspace contained.</summary>
internal sealed class FakeRuntime : IContainerRuntime
{
    public string Name => "docker";

    public string? Unavailable { get; set; }

    public List<(ContainerSpec Spec, string[] Files)> Runs { get; } = [];

    public Func<ContainerSpec, ContainerResult> Respond { get; set; } = _ => new ContainerResult(0, "ok", TimeSpan.FromSeconds(1), false);

    public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(Unavailable);

    public Task<ContainerResult> RunAsync(ContainerSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Runs.Add((spec, [.. Directory.EnumerateFiles(spec.Workspace, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(spec.Workspace, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)]));
        return Task.FromResult(Respond(spec));
    }

    public static string Phase(ContainerSpec spec) => spec.Name[(spec.Name.LastIndexOf('-') + 1)..];

    /// <summary>Writes a TRX where the real test phase would.</summary>
    public static void WriteTrx(ContainerSpec spec, string trx)
    {
        var path = Path.Combine(spec.Workspace, ".testwizz-run", "results", "testwizz.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, trx);
    }
}

public sealed class RunCommandTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly string _proposals = Directory.CreateTempSubdirectory("testwizz-proposals-").FullName;
    private readonly FakeRuntime _runtime = new();

    public RunCommandTests() =>
        File.WriteAllText(Path.Combine(_fixture, "testwizz.json"), new JsonObject { ["version"] = 1, ["output"] = _proposals }.ToJsonString());

    public void Dispose()
    {
        Directory.Delete(_fixture, recursive: true);
        Directory.Delete(_proposals, recursive: true);
    }

    private (int ExitCode, JsonNode Json) Run(string bundle, params string[] extra)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _fixture, _ => null, _ => _runtime)
            .Run(["run", "--proposal", Path.GetFileName(bundle), "--format", "json", .. extra], TestContext.Current.CancellationToken);
        return (exit, JsonNode.Parse(stdout.ToString())!);
    }

    private static readonly string[] AllPassed = ["Divide_ByOne_ReturnsDividend", "Divide_ByZero_Throws", "Divide_ReturnsQuotient"];

    private void Passing() => _runtime.Respond = spec =>
    {
        if (FakeRuntime.Phase(spec) == "test")
        {
            FakeRuntime.WriteTrx(spec, Bundles.Trx([.. AllPassed.Select(m => (m, "Passed", (string?)null))]));
        }

        return new ContainerResult(0, "ok", TimeSpan.FromSeconds(1), false);
    };

    private string[] Snapshot() =>
        [.. Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p => $"{p}:{File.ReadAllText(p)}")];

    [Fact]
    public void PassingRun_IsolatesPhasesAndRecordsResult()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        Passing();
        var before = Snapshot();

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Success, exit);
        var result = json["result"]!;
        Assert.Equal("passed", (string)result["status"]!);
        Assert.Equal(3, (int)result["summary"]!["passed"]!);
        Assert.Equal(["S1"], result["tests"]!.AsArray().Single(t => (string)t!["method"]! == "Divide_ReturnsQuotient")!["scenarios"]!.AsArray().Select(s => (string)s!));

        var (restore, restoreFiles) = _runtime.Runs[0];
        var (build, buildFiles) = _runtime.Runs[1];
        var (test, _) = _runtime.Runs[2];
        Assert.Equal(["restore", "build", "test"], _runtime.Runs.Select(r => FakeRuntime.Phase(r.Spec)));
        Assert.True(restore.Network);
        Assert.False(build.Network);
        Assert.False(test.Network);
        Assert.DoesNotContain(restoreFiles, f => f.EndsWith(".cs", StringComparison.Ordinal));
        Assert.Contains("tests/Shop.Tests/Shop.Tests.csproj", restoreFiles);
        Assert.Contains("tests/Shop.Tests/CalculatorDivideTests.cs", buildFiles);
        Assert.Contains("src/Shop/Calculator.cs", buildFiles);
        Assert.Contains("FullyQualifiedName~Shop.Tests.CalculatorDivideTests.", test.Command);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", restore.Image);
        Assert.All(_runtime.Runs, r => Assert.NotEqual(_fixture, r.Spec.Workspace));
        Assert.All(_runtime.Runs, r => Assert.False(Directory.Exists(r.Spec.Workspace)));

        Assert.Equal(before, Snapshot());
        var run = Path.Combine(bundle, "run");
        Assert.Equal("passed", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(run, "result.json")))!["status"]!);
        Assert.True(File.Exists(Path.Combine(run, "build.log")));
        Assert.True(File.Exists(Path.Combine(run, "results.trx")));
    }

    [Fact]
    public void FailingTest_IsReportedWithMessage()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Respond = spec =>
        {
            if (FakeRuntime.Phase(spec) != "test")
            {
                return new ContainerResult(0, "ok", TimeSpan.Zero, false);
            }

            FakeRuntime.WriteTrx(spec, Bundles.Trx(("Divide_ReturnsQuotient", "Failed", "Assert.Equal() Failure: Values differ"), ("Divide_ByZero_Throws", "Passed", null)));
            return new ContainerResult(1, "failed", TimeSpan.Zero, false);
        };

        var (exit, json) = Run(bundle, "--no-repair");

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        var result = json["result"]!;
        Assert.Equal("failed", (string)result["status"]!);
        var tests = result["tests"]!.AsArray();
        Assert.Equal("Assert.Equal() Failure: Values differ", (string)tests.Single(t => (string)t!["outcome"]! == "failed")!["message"]!);
        Assert.Equal("Divide_ByOne_ReturnsDividend", (string)tests.Single(t => (string)t!["outcome"]! == "not-run")!["method"]!);
    }

    [Fact]
    public void BuildErrors_AreParsedAndAttributed()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Respond = spec => FakeRuntime.Phase(spec) == "build"
            ? new ContainerResult(1, """
                /work/tests/Shop.Tests/CalculatorDivideTests.cs(11,46): error CS1503: Argument 2: cannot convert from 'string' to 'int' [/work/tests/Shop.Tests/Shop.Tests.csproj]
                /work/src/Shop/Calculator.cs(3,1): error CS0246: The type or namespace name 'Missing' could not be found [/work/src/Shop/Shop.csproj]
                /work/tests/Shop.Tests/CalculatorDivideTests.cs(11,46): error CS1503: Argument 2: cannot convert from 'string' to 'int' [/work/tests/Shop.Tests/Shop.Tests.csproj]
                """, TimeSpan.Zero, false)
            : new ContainerResult(0, "ok", TimeSpan.Zero, false);

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal("compile-error", (string)json["result"]!["status"]!);
        var errors = json["result"]!["compileErrors"]!.AsArray();
        Assert.Equal(2, errors.Count);
        Assert.Equal("tests/Shop.Tests/CalculatorDivideTests.cs", (string)errors[0]!["file"]!);
        Assert.Equal(11, (int)errors[0]!["line"]!);
        Assert.Equal("CS1503", (string)errors[0]!["code"]!);
        Assert.True((bool)errors[0]!["inProposal"]!);
        Assert.False((bool)errors[1]!["inProposal"]!);
        Assert.Equal(2, _runtime.Runs.Count);
    }

    [Fact]
    public void RestoreFailure_IsAnEnvironmentError()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Respond = _ => new ContainerResult(1, "error NU1301: Unable to load the service index", TimeSpan.Zero, false);

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("restore-failed", (string)json["result"]!["status"]!);
        Assert.Single(_runtime.Runs);
    }

    [Fact]
    public void Timeout_StopsTheRun()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Respond = spec => FakeRuntime.Phase(spec) == "test"
            ? new ContainerResult(-1, "Stopped after 30 seconds.", TimeSpan.FromSeconds(30), true)
            : new ContainerResult(0, "ok", TimeSpan.Zero, false);

        var (exit, json) = Run(bundle, "--timeout", "30");

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal("timeout", (string)json["result"]!["status"]!);
        Assert.True((bool)json["result"]!["phases"]![2]!["timedOut"]!);
    }

    [Fact]
    public void MissingRuntime_FailsClosed()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Unavailable = "'docker' is not installed or not on PATH.";

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("run.no_container_runtime", (string)json["error"]!["code"]!);
        Assert.Contains("never runs generated tests on the host", (string)json["error"]!["message"]!, StringComparison.Ordinal);
        Assert.Empty(_runtime.Runs);
    }

    [Fact]
    public void StaleSourcesAndEditedFiles_AreReported()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        File.AppendAllText(Path.Combine(_fixture, "src", "Shop", "Calculator.cs"), "\n// changed\n");
        File.AppendAllText(Path.Combine(bundle, "files", "tests", "Shop.Tests", "CalculatorDivideTests.cs"), "\n// tweaked\n");
        Passing();

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(["src/Shop/Calculator.cs"], json["result"]!["staleSources"]!.AsArray().Select(s => (string)s!));
        Assert.Equal(["tests/Shop.Tests/CalculatorDivideTests.cs"], json["result"]!["edited"]!.AsArray().Select(s => (string)s!));
    }

    [Fact]
    public void ProposedFileNowInRepository_IsRefused()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        File.WriteAllText(Path.Combine(_fixture, "tests", "Shop.Tests", "CalculatorDivideTests.cs"), "");

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("run.file_exists", (string)json["error"]!["code"]!);
        Assert.Empty(_runtime.Runs);
    }

    [Fact]
    public void TamperedManifestPath_IsRefused()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        var manifestPath = Path.Combine(bundle, "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"path\": \"tests/Shop.Tests/CalculatorDivideTests.cs\"", "\"path\": \"../escape.cs\"", StringComparison.Ordinal));

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("proposal.unsafe_path", (string)json["error"]!["code"]!);
    }

    [Fact]
    public void UnknownProposal_IsNotFound()
    {
        var (exit, json) = Run(Path.Combine(_proposals, "nope"));

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("proposal.not_found", (string)json["error"]!["code"]!);
    }

    [Fact]
    public void Cancellation_CleansUpWorkspace()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        using var cancellation = new CancellationTokenSource();
        string? workspace = null;
        _runtime.Respond = spec =>
        {
            workspace = spec.Workspace;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        using var stdout = new StringWriter();

        var exit = new CliApp(stdout, new StringWriter(), _fixture, _ => null, _ => _runtime)
            .Run(["run", "--proposal", Path.GetFileName(bundle), "--format", "json"], cancellation.Token);

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("cancelled", (string)JsonNode.Parse(stdout.ToString())!["error"]!["code"]!);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void ContainerArguments_AreLockedDown()
    {
        var spec = new ContainerSpec("testwizz-x-test", "img", "/tmp/ws", ["dotnet", "test"], Network: false, ContainerLimits.Default, new Dictionary<string, string> { ["A"] = "1" });

        var args = CliContainerRuntime.RunArguments(spec, ContainerRuntime.Docker, "1000:1000");

        Assert.Equal(
            ["run", "--rm", "--name", "testwizz-x-test", "--network", "none", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--read-only",
             "--tmpfs", "/tmp:rw,exec,size=1g", "--cpus", "2", "--memory", "4096m", "--pids-limit", "1024", "--volume", "/tmp/ws:/work", "--workdir", "/work",
             "--user", "1000:1000", "--env", "A=1", "img", "dotnet", "test"],
            args);
        Assert.DoesNotContain("--user", CliContainerRuntime.RunArguments(spec, ContainerRuntime.Podman, "1000:1000"));
        Assert.Contains("bridge", CliContainerRuntime.RunArguments(spec with { Network = true }, ContainerRuntime.Docker, null));
    }

    [Theory]
    [InlineData("net10.0", "mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("net8.0;net10.0", "mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("net9.0-windows", "mcr.microsoft.com/dotnet/sdk:9.0")]
    public void DefaultImage_FollowsTargetFramework(string framework, string image) => Assert.Equal(image, DotNetTestRunner.DefaultImage(framework));

    [Fact]
    public void DefaultImage_RejectsUnknownFramework() =>
        Assert.Equal("run.unknown_framework", Assert.Throws<ProposalException>(() => DotNetTestRunner.DefaultImage("netstandard2.0")).Code);
}

/// <summary>
/// Real containers. Enabled with TESTWIZZ_DOCKER_TESTS=1 (CI sets it); TESTWIZZ_DOCKER_IMAGE overrides the SDK image,
/// e.g. one that trusts a corporate TLS proxy.
/// </summary>
public sealed class DockerRunTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly string _proposals = Directory.CreateTempSubdirectory("testwizz-proposals-").FullName;
    private readonly string? _image = Environment.GetEnvironmentVariable("TESTWIZZ_DOCKER_IMAGE");

    public void Dispose()
    {
        Directory.Delete(_fixture, recursive: true);
        Directory.Delete(_proposals, recursive: true);
    }

    private static void RequireDocker() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TESTWIZZ_DOCKER_TESTS") == "1", "Set TESTWIZZ_DOCKER_TESTS=1 to run container tests.");

    private async Task<RunResult> RunAsync(string code, int timeoutSeconds = 300)
    {
        var proposal = ProposalReader.Load(Bundles.Create(_fixture, _proposals, code));
        var runner = new DotNetTestRunner(new CliContainerRuntime(ContainerRuntime.Docker));
        var output = await runner.RunAsync(_fixture, proposal, new RunOptions(_image, TimeSpan.FromSeconds(timeoutSeconds), ContainerLimits.Default), TestContext.Current.CancellationToken);
        return output.Result;
    }

    private static string Body(string statements) => $$"""
        namespace Shop.Tests;

        public class CalculatorDivideTests
        {
            [Fact]
            [Trait("Scenario", "S1")]
            public async Task Divide_Probe()
            {
                {{statements}}
                await Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task PassingFailingAndBrokenProposals()
    {
        RequireDocker();
        var before = Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

        var passing = await RunAsync(CannedTests.Divide);
        Assert.Equal(RunStatus.Passed, passing.Status);
        Assert.Equal(3, passing.Passed);

        var failing = await RunAsync(CannedTests.Divide.Replace("Assert.Equal(4,", "Assert.Equal(5,", StringComparison.Ordinal));
        Assert.Equal(RunStatus.Failed, failing.Status);
        var failed = Assert.Single(failing.Tests, t => t.Outcome == "failed");
        Assert.Equal("Divide_ReturnsQuotient", failed.Method);
        Assert.Equal(["S1"], failed.Scenarios);
        Assert.Contains("Expected: 5", failed.Message, StringComparison.Ordinal);

        var broken = await RunAsync(CannedTests.Divide.Replace("Divide(8, 2)", "Divide(8, \"2\")", StringComparison.Ordinal));
        Assert.Equal(RunStatus.CompileError, broken.Status);
        var error = Assert.Single(broken.CompileErrors);
        Assert.Equal("CS1503", error.Code);
        Assert.True(error.InProposal);
        Assert.Equal(11, error.Line);

        Assert.Equal(before, Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList());
        Assert.Empty(LeftoverContainers());
    }

    [Fact]
    public async Task TestPhase_HasNoNetwork()
    {
        RequireDocker();

        var result = await RunAsync(Body("""
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://1.1.1.1/"));
            await Assert.ThrowsAnyAsync<Exception>(() => System.Net.Dns.GetHostAddressesAsync("api.nuget.org"));
            """));

        Assert.True(result.Status == RunStatus.Passed, string.Join("\n", result.Tests.Select(t => $"{t.Outcome} {t.Message}")));
    }

    [Fact]
    public async Task BrokenProposal_IsRepairedAndPassesInARealContainer()
    {
        RequireDocker();
        await using var server = await FakeModelServer.StartAsync();
        server.AsOllamaSequence("qwen2.5-coder:14b", new JsonObject
        {
            ["code"] = CannedTests.Divide,
            ["changes"] = new JsonArray("Passed the divisor as an int."),
            ["suspectedBugs"] = new JsonArray(),
        }.ToJsonString());
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide.Replace("Divide(8, 2)", "Divide(8, \"2\")", StringComparison.Ordinal));
        var loop = new RepairLoop(
            new DotNetTestRunner(new CliContainerRuntime(ContainerRuntime.Docker)),
            () => ModelClient.Create(TestSettings.Ollama(server.BaseUri), _ => null),
            maxAttempts: 2);

        var result = await loop.RunAsync(_fixture, bundle, new RunOptions(_image, TimeSpan.FromSeconds(300), ContainerLimits.Default), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Passed, result.Final.Result.Status);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(3, result.Final.Result.Passed);
    }

    [Fact]
    public async Task HangingTest_TimesOutAndContainerIsRemoved()
    {
        RequireDocker();

        var result = await RunAsync(Body("await Task.Delay(TimeSpan.FromMinutes(10));"), timeoutSeconds: 90);

        Assert.Equal(RunStatus.Timeout, result.Status);
        Assert.True(result.Phases[^1].TimedOut);
        Assert.Empty(LeftoverContainers());
    }

    private static string[] LeftoverContainers()
    {
        using var process = Process.Start(new ProcessStartInfo("docker", ["ps", "-a", "--filter", "name=testwizz-", "--format", "{{.Names}}"]) { RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
