using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Cli.Commands;
using TestWizz.Core.Running;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

/// <summary>Execution preflight (#96): each stage reports separately, isolation is kept and output is redacted.</summary>
public sealed class PreflightTests : IDisposable
{
    private const string Tls = """
        /work/testwizz-preflight.csproj : error NU1301: Unable to load the service index for source https://build:s3cr3t@feed.example.com/v3/index.json.
        /work/testwizz-preflight.csproj : error NU1301:   The SSL connection could not be established, see inner exception.
        /work/testwizz-preflight.csproj : error NU1301:   The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot
        """;

    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly FakeRuntime _runtime = new();
    private readonly List<(string Stage, bool Network, string[] Files)> _containers = [];

    public PreflightTests() => _runtime.Respond = spec =>
    {
        _containers.Add((FakeRuntime.Phase(spec), spec.Network,
            [.. Directory.EnumerateFiles(spec.Workspace, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(spec.Workspace, f).Replace('\\', '/'))]));
        return new ContainerResult(0, "10.0.100", TimeSpan.Zero, false);
    };

    public void Dispose() => TestTempDirectories.Delete(_fixture);

    private Task<PreflightReport> Run(string? image = "mcr.microsoft.com/dotnet/sdk:10.0", bool network = true, bool caseSensitive = true) =>
        ExecutionPreflight.RunAsync(_fixture, _runtime, image, "config", network, caseSensitive, TestContext.Current.CancellationToken);

    private static PreflightStage Stage(PreflightReport report, string name) => report.Stages.Single(s => s.Name == name);

    [Fact]
    public async Task HealthyEnvironment_PassesEveryStage()
    {
        var report = await Run();

        Assert.True(report.Ok);
        Assert.Equal(["runtime", "workspace", "image", "nuget"], report.Stages.Select(s => s.Name));
        Assert.All(report.Stages, s => Assert.Equal("ok", s.Status));
        Assert.Contains("10.0.100", Stage(report, "image").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheNuGetStageHasANetworkAndItSeesNoRepositoryCode()
    {
        File.WriteAllText(Path.Combine(_fixture, "nuget.config"), "<configuration />");

        await Run();

        var image = _containers.Single(c => c.Stage == "image");
        var nuget = _containers.Single(c => c.Stage == "nuget");
        Assert.False(image.Network);
        Assert.Equal(["testwizz-preflight.csproj"], image.Files);
        Assert.True(nuget.Network);
        Assert.Equal(["nuget.config", "testwizz-preflight.csproj"], nuget.Files.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SkipNetwork_RunsNoNetworkedContainer()
    {
        var report = await Run(network: false);

        Assert.True(report.Ok);
        Assert.Equal("skipped", Stage(report, "nuget").Status);
        Assert.All(_containers, c => Assert.False(c.Network));
    }

    [Fact]
    public async Task UnavailableRuntime_FailsItsStageAndSkipsContainers()
    {
        _runtime.Unavailable = "'docker version' failed: Cannot connect to the Docker daemon";

        var report = await Run();

        Assert.False(report.Ok);
        Assert.Equal("preflight.runtime_unavailable", Stage(report, "runtime").Code);
        Assert.Equal("ok", Stage(report, "workspace").Status);
        Assert.Equal("skipped", Stage(report, "image").Status);
        Assert.Equal("skipped", Stage(report, "nuget").Status);
        Assert.Empty(_containers);
    }

    [Fact]
    public async Task PathCollision_FailsTheWorkspaceStageWithBothPaths()
    {
        if (!WorkspacePaths.ProbeCaseSensitive(_fixture))
        {
            Assert.Skip("Needs a case-sensitive file system to create both spellings.");
        }

        Directory.CreateDirectory(Path.Combine(_fixture, "web"));
        File.WriteAllText(Path.Combine(_fixture, "web", "_supplyPartList.ts"), "a");
        File.WriteAllText(Path.Combine(_fixture, "web", "_SupplyPartList.ts"), "b");

        var report = await Run(caseSensitive: false);

        var workspace = Stage(report, "workspace");
        Assert.Equal("run.path_collision", workspace.Code);
        Assert.Contains("web/_SupplyPartList.ts", workspace.Detail, StringComparison.Ordinal);
        Assert.Contains("ignores letter case", workspace.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingImage_IsAnImageStageFailureAndNuGetIsSkipped()
    {
        _runtime.Respond = _ => new ContainerResult(125, "Unable to find image 'testwizz-sdk:10.0-corporate' locally\npull access denied", TimeSpan.Zero, false);

        var report = await Run("testwizz-sdk:10.0-corporate");

        Assert.Equal("preflight.image_unavailable", Stage(report, "image").Code);
        Assert.Equal("skipped", Stage(report, "nuget").Status);
    }

    [Fact]
    public async Task UntrustedFeedCertificate_IsDiagnosedWithoutEchoingCredentials()
    {
        _runtime.Respond = spec => FakeRuntime.Phase(spec) == "nuget"
            ? new ContainerResult(1, Tls, TimeSpan.Zero, false)
            : new ContainerResult(0, "10.0.100", TimeSpan.Zero, false);

        var report = await Run();

        Assert.False(report.Ok);
        Assert.Equal("restore.tls_untrusted", Stage(report, "nuget").Code);
        Assert.DoesNotContain("s3cr3t", PreflightCommand.ToJson(report).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownNuGetFailure_IsRedacted()
    {
        _runtime.Respond = spec => FakeRuntime.Phase(spec) == "nuget"
            ? new ContainerResult(1, "error: feed https://build:s3cr3t@feed.example.com/v3/index.json said no; token=abc123", TimeSpan.Zero, false)
            : new ContainerResult(0, "10.0.100", TimeSpan.Zero, false);

        var report = await Run();

        var nuget = Stage(report, "nuget");
        Assert.Equal("preflight.nuget_failed", nuget.Code);
        Assert.DoesNotContain("s3cr3t", nuget.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", nuget.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoImage_IsAnImageStageFailure()
    {
        var report = await Run(image: null);

        Assert.Equal("run.unknown_framework", Stage(report, "image").Code);
        Assert.Equal("skipped", Stage(report, "nuget").Status);
        Assert.Empty(_containers);
    }

    [Fact]
    public void RepositoryDefaultImage_FollowsTheNewestTargetFramework() =>
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", DotNetTestRunner.DefaultImageForRepository(_fixture));

    private (int ExitCode, JsonNode Json) Cli(params string[] extra)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _fixture, _ => null, _ => _runtime)
            .Run(["preflight", "--format", "json", "--skip-network", .. extra], TestContext.Current.CancellationToken);
        return (exit, JsonNode.Parse(stdout.ToString())!);
    }

    [Fact]
    public void Command_UsesConfiguredImageAndLetsTheCommandLineOverrideIt()
    {
        File.WriteAllText(Path.Combine(_fixture, "testwizz.json"),
            """{ "version": 1, "run": { "image": "testwizz-sdk:10.0-corporate" } }""");

        var (exit, json) = Cli();
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("testwizz-sdk:10.0-corporate", (string?)json["result"]!["image"]);
        Assert.Equal("config", (string?)json["result"]!["imageSource"]);

        (_, json) = Cli("--image", "mcr.microsoft.com/dotnet/sdk:10.0");
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", (string?)json["result"]!["image"]);
        Assert.Equal("cli", (string?)json["result"]!["imageSource"]);
    }

    [Fact]
    public void Command_WithoutImageSettings_UsesTheTargetFramework()
    {
        File.WriteAllText(Path.Combine(_fixture, "testwizz.json"), """{ "version": 1 }""");

        var (_, json) = Cli();

        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", (string?)json["result"]!["image"]);
        Assert.Equal("target-framework", (string?)json["result"]!["imageSource"]);
    }

    [Fact]
    public void Command_FailedStage_ExitsWithValidationFailure()
    {
        File.WriteAllText(Path.Combine(_fixture, "testwizz.json"), """{ "version": 1 }""");
        _runtime.Unavailable = "not running";

        var (exit, json) = Cli();

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.False((bool)json["result"]!["ok"]!);
    }

    [Fact]
    public void Command_InvalidImage_IsAUsageError()
    {
        File.WriteAllText(Path.Combine(_fixture, "testwizz.json"), """{ "version": 1 }""");

        var (exit, json) = Cli("--image", "--privileged");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("usage", (string?)json["error"]!["code"]);
        Assert.Empty(_containers);
    }
}
