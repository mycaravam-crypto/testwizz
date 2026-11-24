using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Cli.Commands;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

/// <summary>Workspace path collisions (#88) are found before anything is copied.</summary>
public class WorkspacePathsTests
{
    [Fact]
    public void DuplicateEnumeration_IsCopiedOnceAndIsNotACollision()
    {
        var plan = WorkspacePaths.Plan(["src/a.cs", "src/_supplyPartList.ts", "src/a.cs"], caseSensitive: false);

        Assert.Equal(["src/a.cs", "src/_supplyPartList.ts"], plan.Files);
        Assert.Equal(1, plan.DuplicatesRemoved);
        Assert.Empty(plan.Collisions);
    }

    [Fact]
    public void CaseOnlyPaths_CollideOnCaseInsensitiveDestinations()
    {
        var plan = WorkspacePaths.Plan(["web/_supplyPartList.ts", "web/_SupplyPartList.ts"], caseSensitive: false);

        var collision = Assert.Single(plan.Collisions);
        Assert.Equal(new PathCollision("web/_supplyPartList.ts", "web/_SupplyPartList.ts", "case"), collision);
    }

    [Fact]
    public void CaseOnlyPaths_AreDistinctOnCaseSensitiveDestinations()
    {
        var plan = WorkspacePaths.Plan(["web/_supplyPartList.ts", "web/_SupplyPartList.ts"], caseSensitive: true);

        Assert.Empty(plan.Collisions);
        Assert.Equal(2, plan.Files.Count);
    }

    [Fact]
    public void FolderNamesDifferingInCase_MergeWithoutCollision()
    {
        var plan = WorkspacePaths.Plan(["Src/A.cs", "src/B.cs"], caseSensitive: false);

        Assert.Empty(plan.Collisions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileNamedLikeAFolder_Collides(bool caseSensitive)
    {
        var plan = WorkspacePaths.Plan(["docs", "docs/readme.md"], caseSensitive);

        Assert.Equal(new PathCollision("docs", "docs/readme.md", "file-directory"), Assert.Single(plan.Collisions));
    }

    [Fact]
    public void Checked_ReportsBothPathsAndNeverPicksOne()
    {
        var ex = Assert.Throws<ProposalException>(() =>
            WorkspacePaths.Checked(["web/_supplyPartList.ts", "web/_SupplyPartList.ts", "src/a.cs"], caseSensitive: false));

        Assert.Equal("run.path_collision", ex.Code);
        Assert.Contains("web/_supplyPartList.ts", ex.Message, StringComparison.Ordinal);
        Assert.Contains("web/_SupplyPartList.ts", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was overwritten", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_LeavesNothingBehindAndMatchesTheHost()
    {
        var directory = Directory.CreateTempSubdirectory("testwizz-probe-").FullName;
        try
        {
            var sensitive = WorkspacePaths.ProbeCaseSensitive(directory);

            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            if (OperatingSystem.IsWindows())
            {
                Assert.False(sensitive);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Probe_OfAMissingFolder_AssumesTheStricterCaseInsensitiveSemantics() =>
        Assert.False(WorkspacePaths.ProbeCaseSensitive(Path.Combine(Path.GetTempPath(), $"testwizz-missing-{Guid.NewGuid():N}")));
}

/// <summary>The runner refuses colliding repositories before restore and leaves no workspace behind.</summary>
public sealed class WorkspaceCollisionRunTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly string _proposals = Directory.CreateTempSubdirectory("testwizz-proposals-").FullName;
    private readonly FakeRuntime _runtime = new();

    public void Dispose()
    {
        TestTempDirectories.Delete(_fixture);
        TestTempDirectories.Delete(_proposals);
    }

    [Fact]
    public async Task WindowsCaseOnlyCollision_FailsBeforeAnyContainerRuns()
    {
        if (!WorkspacePaths.ProbeCaseSensitive(_fixture))
        {
            Assert.Skip("Needs a case-sensitive file system to create both spellings.");
        }

        var web = Directory.CreateDirectory(Path.Combine(_fixture, "web")).FullName;
        File.WriteAllText(Path.Combine(web, "_supplyPartList.ts"), "export const a = 1;");
        File.WriteAllText(Path.Combine(web, "_SupplyPartList.ts"), "export const b = 2;");
        var proposal = ProposalReader.Load(Bundles.Create(_fixture, _proposals, CannedTests.Divide));
        var runner = new DotNetTestRunner(_runtime) { CaseSensitiveWorkspace = false };

        var ex = await Assert.ThrowsAsync<ProposalException>(() => runner.RunAsync(_fixture, proposal,
            new RunOptions(null, TimeSpan.FromMinutes(1), ContainerLimits.Default), TestContext.Current.CancellationToken));

        Assert.Equal("run.path_collision", ex.Code);
        Assert.Contains("web/_supplyPartList.ts", ex.Message, StringComparison.Ordinal);
        Assert.Contains("web/_SupplyPartList.ts", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_runtime.Runs);
        Assert.Equal("export const a = 1;", File.ReadAllText(Path.Combine(web, "_supplyPartList.ts")));
    }

    [Fact]
    public async Task SameRepository_RunsOnCaseSensitiveDestination()
    {
        if (!WorkspacePaths.ProbeCaseSensitive(_fixture))
        {
            Assert.Skip("Needs a case-sensitive file system to create both spellings.");
        }

        var web = Directory.CreateDirectory(Path.Combine(_fixture, "web")).FullName;
        File.WriteAllText(Path.Combine(web, "_supplyPartList.ts"), "a");
        File.WriteAllText(Path.Combine(web, "_SupplyPartList.ts"), "b");
        var proposal = ProposalReader.Load(Bundles.Create(_fixture, _proposals, CannedTests.Divide));
        var runner = new DotNetTestRunner(_runtime) { CaseSensitiveWorkspace = true };

        await runner.RunAsync(_fixture, proposal, new RunOptions(null, TimeSpan.FromMinutes(1), ContainerLimits.Default),
            TestContext.Current.CancellationToken);

        Assert.Contains("web/_SupplyPartList.ts", _runtime.Runs[1].Files);
        Assert.Contains("web/_supplyPartList.ts", _runtime.Runs[1].Files);
    }
}

/// <summary>Optional custom SDK image (#87): configuration, precedence and validation.</summary>
public class RunImageConfigTests
{
    [Fact]
    public void DefaultConfig_HasNoImageAndWritesNone()
    {
        Assert.Null(TestWizzConfig.Default.Run.Image);
        Assert.False(ConfigJson.ToJson(TestWizzConfig.Default)["run"]!.AsObject().ContainsKey("image"));
    }

    [Theory]
    [InlineData("testwizz-sdk:10.0-corporate")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("registry.example.com:5000/team/testwizz-sdk:10.0")]
    [InlineData("localhost/testwizz-sdk")]
    [InlineData("mcr.microsoft.com/dotnet/sdk@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ValidImage_IsParsedAndRoundTrips(string image)
    {
        var result = ConfigJson.Parse($$"""{ "version": 1, "run": { "image": "{{image}}" } }""");

        Assert.Empty(result.Errors);
        Assert.Equal(image, result.Config!.Run.Image);
        Assert.Equal(result.Config, ConfigJson.Parse(ConfigJson.ToJson(result.Config).ToJsonString()).Config);
    }

    [Fact]
    public void NullImage_KeepsTheDefault()
    {
        var result = ConfigJson.Parse("""{ "version": 1, "run": { "image": null } }""");

        Assert.Equal(TestWizzConfig.Default, result.Config);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"--privileged\"")]
    [InlineData("\"-v=/:/host\"")]
    [InlineData("\"sdk 10.0\"")]
    [InlineData("\"Upper/Case:1\"")]
    [InlineData("\"sdk:10.0\\n--rm\"")]
    [InlineData("42")]
    public void InvalidImage_IsReportedWithItsPath(string value)
    {
        var result = ConfigJson.Parse($$"""{ "version": 1, "run": { "image": {{value}} } }""");

        Assert.Null(result.Config);
        Assert.Contains(result.Errors, e => e.Path == "$.run.image");
    }

    [Fact]
    public void YamlImage_IsRead()
    {
        var json = TestWizzYaml.ToJson("""
            version: 1
            run:
              runtime: docker
              image: testwizz-sdk:10.0-corporate
              maxRepairAttempts: 2
            """);

        Assert.Equal("testwizz-sdk:10.0-corporate", ConfigJson.Parse(json).Config!.Run.Image);
    }
}

public sealed class RunImagePrecedenceTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly string _proposals = Directory.CreateTempSubdirectory("testwizz-proposals-").FullName;
    private readonly FakeRuntime _runtime = new();

    public void Dispose()
    {
        TestTempDirectories.Delete(_fixture);
        TestTempDirectories.Delete(_proposals);
    }

    private void Config(string? image)
    {
        var run = new JsonObject { ["maxRepairAttempts"] = 0 };
        if (image is not null)
        {
            run["image"] = image;
        }

        File.WriteAllText(Path.Combine(_fixture, "testwizz.json"),
            new JsonObject { ["version"] = 1, ["output"] = _proposals, ["run"] = run }.ToJsonString());
    }

    private (int ExitCode, JsonNode Json) Run(string bundle, params string[] extra)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _fixture, _ => null, _ => _runtime)
            .Run(["run", "--proposal", Path.GetFileName(bundle), "--format", "json", .. extra], TestContext.Current.CancellationToken);
        return (exit, JsonNode.Parse(stdout.ToString())!);
    }

    [Fact]
    public void WithoutImage_TheFrameworkDefaultIsUsed()
    {
        Config(null);
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);

        Run(bundle);

        Assert.All(_runtime.Runs, r => Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", r.Spec.Image));
    }

    [Fact]
    public void ConfiguredImage_IsUsedForRestoreBuildAndTest()
    {
        Config("testwizz-sdk:10.0-corporate");
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);

        var (_, json) = Run(bundle);

        Assert.Equal(["restore", "build", "test"], _runtime.Runs.Select(r => FakeRuntime.Phase(r.Spec)));
        Assert.All(_runtime.Runs, r => Assert.Equal("testwizz-sdk:10.0-corporate", r.Spec.Image));
        Assert.Equal("testwizz-sdk:10.0-corporate", (string?)json["result"]!["image"]);
    }

    [Fact]
    public void CommandLineImage_TakesPrecedence()
    {
        Config("testwizz-sdk:10.0-corporate");
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);

        Run(bundle, "--image", "mcr.microsoft.com/dotnet/sdk:10.0");

        Assert.All(_runtime.Runs, r => Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", r.Spec.Image));
    }

    [Fact]
    public void InvalidCommandLineImage_IsAUsageErrorAndNothingRuns()
    {
        Config(null);
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);

        var (exit, json) = Run(bundle, "--image", "--privileged");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("usage", (string?)json["error"]!["code"]);
        Assert.Empty(_runtime.Runs);
    }

    [Fact]
    public void UntrustedCertificateDuringRestore_IsDiagnosedWithoutEchoingTheLog()
    {
        Config("testwizz-sdk:10.0-corporate");
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Respond = _ => new ContainerResult(1,
            "error NU1301: Unable to load the service index for source https://build:s3cr3t-token@feed.example.com/v3/index.json.\n" +
            "  The SSL connection could not be established, see inner exception.\n  The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot",
            TimeSpan.Zero, false);

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Error, exit);
        var diagnosis = json["result"]!["diagnosis"]!;
        Assert.Equal("restore.tls_untrusted", (string?)diagnosis["code"]);
        Assert.DoesNotContain("s3cr3t", json["result"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Single(_runtime.Runs);
        Assert.Equal("run.restore.tls_untrusted", BatchFailure.FromRun(json).Code);
    }
}

public class RestoreDiagnosticsTests
{
    [Theory]
    [InlineData("Unable to find image 'testwizz-sdk:10.0' locally\npull access denied for testwizz-sdk", "restore.image_unavailable")]
    [InlineData("The SSL connection could not be established ... UntrustedRoot", "restore.tls_untrusted")]
    [InlineData("curl: (60) SSL certificate problem: self-signed certificate in certificate chain", "restore.tls_untrusted")]
    [InlineData("error NU1301: Response status code does not indicate success: 401 (Unauthorized).", "restore.feed_auth")]
    [InlineData("error NU1301: Name or service not known (api.nuget.org:443)", "restore.feed_unreachable")]
    [InlineData("error NU1101: Unable to find package Contoso.Internal.", "restore.package_not_found")]
    public void KnownCauses_AreRecognized(string log, string code) =>
        Assert.Equal(code, RestoreDiagnostics.Diagnose(log)?.Code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("error MSB4019: The imported project was not found.")]
    public void UnknownCauses_AreNotGuessed(string? log) => Assert.Null(RestoreDiagnostics.Diagnose(log));
}
