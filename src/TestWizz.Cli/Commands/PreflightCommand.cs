using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

/// <param name="Status"><c>ok</c>, <c>failed</c> or <c>skipped</c>.</param>
/// <param name="Code">Stable failure code, e.g. <c>preflight.runtime_unavailable</c> or <c>restore.tls_untrusted</c>.</param>
/// <param name="Detail">Redacted, bounded text; never raw container output.</param>
internal sealed record PreflightStage(string Name, string Status, string? Code, string Detail);

internal sealed record PreflightReport(string Runtime, string? Image, string ImageSource, IReadOnlyList<PreflightStage> Stages)
{
    public bool Ok => Stages.All(s => s.Status != "failed");
}

/// <summary>
/// Optional checks before <c>run</c> or <c>bootstrap</c>, one stage at a time so a failure names where it happened:
/// <list type="number">
/// <item><c>runtime</c>: Docker/Podman answers.</item>
/// <item><c>workspace</c>: the temp folder is writable and the repository's paths can be copied side by side.</item>
/// <item><c>image</c>: the effective SDK image starts (<c>dotnet --version</c>, network off).</item>
/// <item><c>nuget</c>: from inside that image, a throw-away project restores one package from the feeds of the root <c>nuget.config</c> with TLS validation.</item>
/// </list>
/// Containers use the same hardening as <c>run</c>; only the <c>nuget</c> stage has a network, and its workspace holds
/// nothing but a copy of the root <c>nuget.config</c>. No repository code runs. No certificate settings are required.
/// </summary>
internal static class ExecutionPreflight
{
    private const string ProbeProject = "testwizz-preflight.csproj";

    // Restores one floating package exactly the way a run restores (same NuGet stack, real exit code, full NU1301
    // detail). 'dotnet package search' is not used: it exits 0 when a feed fails and hides the TLS error.
    private const string ProbeProjectContent = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>netstandard2.0</TargetFramework>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="xunit.v3.assert" Version="*" />
          </ItemGroup>
        </Project>
        """;
    private static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<string, string> Environment = new(StringComparer.Ordinal)
    {
        ["HOME"] = $"{ContainerSpec.ContainerWorkspace}/.home",
        ["DOTNET_CLI_HOME"] = $"{ContainerSpec.ContainerWorkspace}/.home",
        ["NUGET_PACKAGES"] = $"{ContainerSpec.ContainerWorkspace}/.home/packages",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
    };

    public static async Task<PreflightReport> RunAsync(
        string root, IContainerRuntime runtime, string? image, string imageSource, bool checkNetwork,
        bool? caseSensitiveWorkspace, CancellationToken cancellationToken)
    {
        var stages = new List<PreflightStage>();

        var problem = await runtime.CheckAsync(cancellationToken).ConfigureAwait(false);
        stages.Add(problem is null
            ? new("runtime", "ok", null, $"{runtime.Name} is available.")
            : new("runtime", "failed", "preflight.runtime_unavailable", BatchFailure.SafeMessage(problem)));

        stages.Add(Workspace(root, caseSensitiveWorkspace));

        if (image is null)
        {
            stages.Add(new("image", "failed", "run.unknown_framework",
                "No target framework found to pick an SDK image. Set run.image or pass --image."));
        }
        else if (problem is not null)
        {
            stages.Add(new("image", "skipped", null, "The container runtime is not available."));
        }
        else
        {
            stages.Add(await Image(runtime, image, cancellationToken).ConfigureAwait(false));
        }

        if (!checkNetwork)
        {
            stages.Add(new("nuget", "skipped", null, "Skipped (--skip-network)."));
        }
        else if (stages[^1] is not { Name: "image", Status: "ok" })
        {
            stages.Add(new("nuget", "skipped", null, "The SDK image is not usable."));
        }
        else
        {
            stages.Add(await NuGet(root, runtime, image!, cancellationToken).ConfigureAwait(false));
        }

        return new PreflightReport(runtime.Name, image, imageSource, stages);
    }

    private static PreflightStage Workspace(string root, bool? caseSensitiveWorkspace)
    {
        string probe;
        try
        {
            probe = Directory.CreateTempSubdirectory("testwizz-preflight-").FullName;
            Directory.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("workspace", "failed", "preflight.temp_unwritable",
                BatchFailure.SafeMessage($"Cannot create a temporary workspace in {Path.GetTempPath()}: {ex.Message}"));
        }

        var caseSensitive = caseSensitiveWorkspace ?? WorkspacePaths.DestinationIsCaseSensitive;
        var files = DotNetTestRunner.WorkspaceFiles(root);
        var semantics = caseSensitive ? "case-sensitive" : "case-insensitive";
        try
        {
            var copied = WorkspacePaths.Checked(files, caseSensitive);
            return new("workspace", "ok", null,
                $"{copied.Count.ToString(CultureInfo.InvariantCulture)} files can be copied; the temp folder is {semantics}.");
        }
        catch (ProposalException ex)
        {
            return new("workspace", "failed", ex.Code, BatchFailure.SafeMessage(ex.Message));
        }
    }

    private static async Task<PreflightStage> Image(IContainerRuntime runtime, string image, CancellationToken cancellationToken)
    {
        var (result, output) = await Container(runtime, "image", image, network: false, ["dotnet", "--version"], null, cancellationToken)
            .ConfigureAwait(false);
        if (result.TimedOut)
        {
            return new("image", "failed", "preflight.timeout", $"'{image}' did not start within {StageTimeout.TotalMinutes:0} minutes.");
        }

        if (result.ExitCode == 0)
        {
            var version = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            return new("image", "ok", null, BatchFailure.SafeMessage($"{image} starts; .NET SDK {version}."));
        }

        return RestoreDiagnostics.Diagnose(output) is { Code: "restore.image_unavailable" } diagnosis
            ? new("image", "failed", "preflight.image_unavailable", diagnosis.Message)
            : new("image", "failed", "preflight.image_failed",
                BatchFailure.SafeMessage($"'dotnet --version' failed in {image} (exit {result.ExitCode}): {Last(output)}"));
    }

    private static async Task<PreflightStage> NuGet(string root, IContainerRuntime runtime, string image, CancellationToken cancellationToken)
    {
        var config = Directory.EnumerateFiles(root)
            .FirstOrDefault(f => Path.GetFileName(f).Equals("nuget.config", StringComparison.OrdinalIgnoreCase));
        var (result, output) = await Container(runtime, "nuget", image, network: true,
            ["dotnet", "restore", ProbeProject], config, cancellationToken).ConfigureAwait(false);
        var sources = config is null ? "the default feed (no nuget.config at the repository root)" : "the feeds of nuget.config";
        if (result.TimedOut)
        {
            return new("nuget", "failed", "preflight.timeout", $"NuGet did not answer within {StageTimeout.TotalMinutes:0} minutes.");
        }

        if (result.ExitCode == 0)
        {
            return new("nuget", "ok", null, $"A package was restored from {sources} inside the SDK image with TLS validation.");
        }

        if (RestoreDiagnostics.Diagnose(output) is { } diagnosis)
        {
            return new("nuget", "failed", diagnosis.Code, diagnosis.Message);
        }

        return new("nuget", "failed", "preflight.nuget_failed",
            BatchFailure.SafeMessage($"Restoring a probe package from {sources} failed (exit {result.ExitCode}): {Last(output)}"));
    }

    private static async Task<(ContainerResult Result, string Output)> Container(
        IContainerRuntime runtime, string stage, string image, bool network, IReadOnlyList<string> command,
        string? nugetConfig, CancellationToken cancellationToken)
    {
        var workspace = Directory.CreateTempSubdirectory("testwizz-preflight-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, ".home"));
            File.WriteAllText(Path.Combine(workspace, ProbeProject), ProbeProjectContent);
            if (nugetConfig is not null)
            {
                File.Copy(nugetConfig, Path.Combine(workspace, Path.GetFileName(nugetConfig)));
            }

            var spec = new ContainerSpec($"testwizz-preflight-{Guid.NewGuid().ToString("N")[..12]}-{stage}", image, workspace,
                command, network, ContainerLimits.Default, Environment);
            var result = await runtime.RunAsync(spec, StageTimeout, cancellationToken).ConfigureAwait(false);
            return (result, result.Output);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the system temp folder; it holds at most a copy of nuget.config.
            }
        }
    }

    private static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";
}

public static class PreflightCommand
{
    private const string ImageOption = "--image";
    private const string SkipNetworkOption = "--skip-network";

    public static CommandDefinition Definition { get; } = new(
        "preflight",
        "Optional checks before run/bootstrap: container runtime, workspace paths, the effective SDK image and NuGet feed access (TLS) from inside it. Runs no repository code.",
        "testwizz preflight [path] [--image <image>] [--skip-network]",
        Options:
        [
            new(ImageOption, "SDK image to check; overrides run.image (default: from the repository's newest target framework).", "image"),
            new(SkipNetworkOption, "Do not check NuGet feed access (no container gets a network)."),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var cliImage = context.Options.TryGetValue(ImageOption, out var imageText) && !string.IsNullOrWhiteSpace(imageText) ? imageText.Trim() : null;
        if (cliImage is not null && ContainerImage.Problem(cliImage) is { } imageProblem)
        {
            throw new CliException("usage", $"{ImageOption}: {imageProblem}");
        }

        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var (image, source) = cliImage is not null ? (cliImage, "cli")
            : config.Run.Image is { } configured ? (configured, "config")
            : (DotNetTestRunner.DefaultImageForRepository(root), "target-framework");

        PreflightReport report;
        try
        {
            report = ExecutionPreflight.RunAsync(root, context.Runtime(config.Run.Runtime), image, source,
                checkNetwork: !context.Has(SkipNetworkOption), caseSensitiveWorkspace: null, context.CancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new CliException("cancelled", "The preflight was cancelled; its containers were removed.");
        }

        context.Output.Result("preflight", ToJson(report), ToText(report), report.Ok ? "ok" : "failed");
        return report.Ok ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    internal static JsonObject ToJson(PreflightReport report) => new()
    {
        ["ok"] = report.Ok,
        ["runtime"] = report.Runtime,
        ["image"] = report.Image,
        ["imageSource"] = report.ImageSource,
        ["stages"] = new JsonArray([.. report.Stages.Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.Name,
            ["status"] = s.Status,
            ["code"] = s.Code,
            ["detail"] = s.Detail,
        })]),
    };

    private static string ToText(PreflightReport report)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Preflight: {report.Runtime}, image {report.Image ?? "(none)"} ({report.ImageSource})\n");
        foreach (var stage in report.Stages)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {stage.Status,-8} {stage.Name,-10} {(stage.Code is null ? "" : stage.Code + ": ")}{stage.Detail}\n");
        }

        text.Append(report.Ok ? "\nReady for isolated runs.\n" : "\nNot ready: fix the failed stage(s) above.\n");
        return text.ToString();
    }
}
