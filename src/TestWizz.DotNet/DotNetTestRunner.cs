using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;

namespace TestWizz.DotNet;

/// <param name="Image">Container image; null picks <c>mcr.microsoft.com/dotnet/sdk:&lt;version&gt;</c> from the target framework.</param>
public sealed record RunOptions(string? Image, TimeSpan Timeout, ContainerLimits Limits);

public sealed record RunOutput(RunResult Result, IReadOnlyDictionary<string, string> Logs);

/// <summary>
/// Builds and runs a proposal's xUnit tests in a container, never on the host. A throw-away copy of the repository is
/// made in the system temp folder. Phase 1 (<c>dotnet restore</c>, with network) sees only project and build files;
/// the sources and the proposed tests are added afterwards, so repository and model-written code only ever runs in
/// phases 2 and 3 (<c>dotnet build</c>, <c>dotnet test</c>) with <c>--network none</c>.
/// </summary>
public sealed partial class DotNetTestRunner(IContainerRuntime runtime)
{
    private const string Scratch = ".testwizz-run";
    private const string TrxName = "testwizz.trx";
    private const int MaxRepositoryFiles = 200_000;

    private static readonly HashSet<string> RestoreFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "nuget.config", "packages.lock.json", "Directory.Build.rsp",
    };

    private static readonly string[] RestoreExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx", ".slnf"];

    private static readonly Dictionary<string, string> Environment = new(StringComparer.Ordinal)
    {
        ["HOME"] = $"{ContainerSpec.ContainerWorkspace}/{Scratch}/home",
        ["DOTNET_CLI_HOME"] = $"{ContainerSpec.ContainerWorkspace}/{Scratch}/home",
        ["NUGET_PACKAGES"] = $"{ContainerSpec.ContainerWorkspace}/{Scratch}/nuget",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
        ["MSBUILDDISABLENODEREUSE"] = "1",
    };

    public static string DefaultImage(string? targetFramework)
    {
        var versions = (targetFramework ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => NetVersion().Match(f))
            .Where(m => m.Success)
            .Select(m => new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
        if (versions.Count == 0)
        {
            throw new ProposalException("run.unknown_framework", $"Cannot pick an SDK image for target framework '{targetFramework}'. Pass --image.");
        }

        var newest = versions.Max()!;
        return $"mcr.microsoft.com/dotnet/sdk:{newest.Major}.{newest.Minor}";
    }

    public async Task<RunOutput> RunAsync(string root, LoadedProposal proposal, RunOptions options, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        var conventions = proposal.Conventions;
        var testProject = (string?)conventions["testProject"]
            ?? throw new ProposalException("run.no_test_project", "The proposal has no test project to build.");
        var filter = $"FullyQualifiedName~{(string?)conventions["namespace"]}.{(string?)conventions["className"]}.";
        var image = options.Image ?? DefaultImage((string?)conventions["targetFramework"]);

        foreach (var file in proposal.Files)
        {
            if (PathGuard.Exists(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar))))
            {
                throw new ProposalException("run.file_exists", $"{file.Path} now exists in the repository; generate the proposal again.");
            }
        }

        var stale = ProposalReader.StaleSources(root, proposal);
        var workspace = Directory.CreateTempSubdirectory("testwizz-run-").FullName;
        var runId = Guid.NewGuid().ToString("N")[..12];
        var deadline = DateTime.UtcNow + options.Timeout;
        var phases = new List<RunPhase>();
        var logs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var files = RepositoryFiles.Files(root, "*", MaxRepositoryFiles)
                .Where(f => !f.Split('/').Any(s => s is "bin" or "obj" or ".git" or Scratch))
                .ToList();
            var restoreFiles = files.Where(IsRestoreFile).ToList();
            Copy(root, workspace, restoreFiles);
            Directory.CreateDirectory(Path.Combine(workspace, Scratch, "home"));

            var restore = await Phase("restore", network: true, ["dotnet", "restore", testProject]).ConfigureAwait(false);
            if (restore.TimedOut || restore.ExitCode != 0)
            {
                return Done(restore.TimedOut ? RunStatus.Timeout : RunStatus.RestoreFailed, [], []);
            }

            Copy(root, workspace, files.Except(restoreFiles, StringComparer.Ordinal));
            foreach (var file in proposal.Files)
            {
                var target = Path.Combine(workspace, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Content);
            }

            var build = await Phase("build", network: false, ["dotnet", "build", testProject, "--no-restore", "-nologo"]).ConfigureAwait(false);
            if (build.TimedOut)
            {
                return Done(RunStatus.Timeout, [], []);
            }

            if (build.ExitCode != 0)
            {
                return Done(RunStatus.CompileError, ParseBuildErrors(logs["build.log"], proposal), []);
            }

            var test = await Phase("test", network: false,
            [
                "dotnet", "test", testProject, "--no-build", "--filter", filter,
                "--logger", $"trx;LogFileName={TrxName}", "--results-directory", $"{ContainerSpec.ContainerWorkspace}/{Scratch}/results",
            ]).ConfigureAwait(false);

            var trxPath = Path.Combine(workspace, Scratch, "results", TrxName);
            var trx = File.Exists(trxPath) ? File.ReadAllText(trxPath) : null;
            if (trx is not null)
            {
                logs["results.trx"] = trx;
            }

            var tests = WithScenarios(trx is null ? [] : ParseTrx(trx), proposal);
            if (test.TimedOut)
            {
                return Done(RunStatus.Timeout, [], tests);
            }

            var passed = test.ExitCode == 0 && tests.Count > 0 && tests.All(t => t.Outcome == "passed");
            return Done(passed ? RunStatus.Passed : RunStatus.Failed, [], tests);
        }
        finally
        {
            TryDelete(workspace);
        }

        async Task<RunPhase> Phase(string name, bool network, IReadOnlyList<string> command)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                var skipped = new RunPhase(name, network, -1, 0, TimedOut: true);
                phases.Add(skipped);
                return skipped;
            }

            var spec = new ContainerSpec($"testwizz-{runId}-{name}", image, workspace, command, network, options.Limits, Environment);
            var result = await runtime.RunAsync(spec, remaining, cancellationToken).ConfigureAwait(false);
            logs[$"{name}.log"] = $"$ {string.Join(' ', command)}\n{result.Output}";
            var phase = new RunPhase(name, network, result.ExitCode, (long)result.Duration.TotalMilliseconds, result.TimedOut);
            phases.Add(phase);
            return phase;
        }

        RunOutput Done(RunStatus status, IReadOnlyList<CompileError> errors, IReadOnlyList<TestOutcome> tests) =>
            new(new RunResult(proposal.Id, status, runtime.Name, image, phases, stale, errors, tests), logs);
    }

    private static bool IsRestoreFile(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return RestoreFileNames.Contains(name) || RestoreExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Copies repository files, skipping anything that resolves outside the repository through a link.</summary>
    private static void Copy(string root, string workspace, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var source = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!PathGuard.IsSafeRelative(file) || !File.Exists(source) || !PathGuard.IsWithin(root, source))
            {
                continue;
            }

            var target = Path.Combine(workspace, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    /// <summary>MSBuild <c>file(line,col): error CODE: message [project]</c> lines, de-duplicated.</summary>
    public static IReadOnlyList<CompileError> ParseBuildErrors(string output, LoadedProposal proposal)
    {
        var proposed = proposal.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var errors = new List<CompileError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in BuildError().Matches(output))
        {
            var file = match.Groups["file"].Value.Trim().Replace('\\', '/');
            var prefix = ContainerSpec.ContainerWorkspace + "/";
            file = file.StartsWith(prefix, StringComparison.Ordinal) ? file[prefix.Length..] : file;
            var error = new CompileError(
                file,
                int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture),
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim(),
                proposed.Contains(file));
            if (seen.Add($"{error.File}:{error.Line}:{error.Column}:{error.Code}:{error.Message}"))
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    /// <summary>Reads VSTest TRX: one entry per executed test case (theory rows are separate entries).</summary>
    public static IReadOnlyList<TestOutcome> ParseTrx(string trx)
    {
        var document = XDocument.Parse(trx);
        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var methods = document.Descendants(ns + "UnitTest").ToDictionary(
            u => (string?)u.Attribute("id") ?? "",
            u => (string?)u.Element(ns + "TestMethod")?.Attribute("name") ?? "",
            StringComparer.Ordinal);

        return
        [
            .. document.Descendants(ns + "UnitTestResult")
                .Select(r =>
                {
                    var name = (string?)r.Attribute("testName") ?? "";
                    var method = methods.GetValueOrDefault((string?)r.Attribute("testId") ?? "") is { Length: > 0 } m ? m : MethodFromName(name);
                    var error = r.Element(ns + "Output")?.Element(ns + "ErrorInfo");
                    return new TestOutcome(
                        name,
                        method,
                        Outcome((string?)r.Attribute("outcome")),
                        TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var duration) ? (long)duration.TotalMilliseconds : 0,
                        Trim((string?)error?.Element(ns + "Message")),
                        Trim((string?)error?.Element(ns + "StackTrace")),
                        []);
                })
                .OrderBy(t => t.Name, StringComparer.Ordinal),
        ];
    }

    private static List<TestOutcome> WithScenarios(IReadOnlyList<TestOutcome> results, LoadedProposal proposal)
    {
        var scenarios = proposal.TestScenarios;
        var outcomes = results.Select(t => t with { Scenarios = scenarios.GetValueOrDefault(t.Method) ?? [] }).ToList();
        foreach (var (method, ids) in scenarios.Where(s => !results.Any(r => r.Method == s.Key)))
        {
            outcomes.Add(new TestOutcome(method, method, "not-run", 0, "The test was not discovered or did not run.", null, ids));
        }

        return outcomes;
    }

    private static string MethodFromName(string name)
    {
        var bare = name.Split('(')[0];
        return bare[(bare.LastIndexOf('.') + 1)..];
    }

    private static string Outcome(string? trx) => trx switch
    {
        "Passed" => "passed",
        "Failed" or "Error" or "Timeout" or "Aborted" => "failed",
        "NotExecuted" => "skipped",
        _ => "not-run",
    };

    private static string? Trim(string? text) => text is null ? null : text.Length <= 4000 ? text.Trim() : text[..4000].Trim() + " …";

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the system temp folder; it holds only a copy of the repository.
        }
    }

    [GeneratedRegex(@"^net(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NetVersion();

    [GeneratedRegex(@"^\s*(?<file>[^\r\n(]+?)\((?<line>\d+),(?<column>\d+)\): error (?<code>[A-Z]+\d+): (?<message>.*?)(?: \[[^\]\r\n]+\])?\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BuildError();
}
