using System.Diagnostics;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;
using TestWizz.DotNet;
using TestWizz.AI;

namespace TestWizz.Cli.Commands;

/// <summary>Initial, review-first generation and execution of every eligible C# method.</summary>
public static class BootstrapCommand
{
    public static CommandDefinition Definition { get; } = new(
        "bootstrap",
        "Discover all eligible C# methods, generate test proposals and optionally run them in isolated containers. Never applies changes.",
        "testwizz bootstrap [path] --all [--review] [--generate-only] [--resume] [--max-workers <n>] [--verify-workers <n>] [--out <dir>]",
        Options:
        [
            new("--all", "Discover and process all eligible C# methods."),
            new("--review", "Keep proposals for explicit review (default; no files are applied)."),
            new("--generate-only", "Skip container execution; only generate proposals."),
            new("--resume", "Resume matching sources; reuse completed and generated-but-not-yet-tested proposals."),
            new("--out", "Proposal directory outside the repository.", "dir"),
            new("--debug", "Print each generated test source to stderr (may contain sensitive source code)."),
            new("--max-workers", "Maximum concurrent model requests (1-8; default 4).", "n"),
            new("--verify-workers", "Maximum parallel isolated container test runs (1-4; default 1).", "n"),
            new("--progress-jsonl", "Emit structured bootstrap progress events on stderr."),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var clock = Stopwatch.StartNew();
        using var progress = BatchProgress.For(context, "bootstrap", quiet: context.Has("--progress-jsonl"));
        progress.Stage("scan", 0, detail: "Loading C# sources");
        var maxWorkers = 4;
        if (context.Options.TryGetValue("--max-workers", out var workerText)
            && (!int.TryParse(workerText, NumberStyles.None, CultureInfo.InvariantCulture, out maxWorkers) || maxWorkers is < 1 or > 8))
            throw new CliException("usage", "--max-workers must be between 1 and 8.");
        var verifyWorkers = 1;
        if (context.Options.TryGetValue("--verify-workers", out var verifyText) &&
            (!int.TryParse(verifyText, NumberStyles.None, CultureInfo.InvariantCulture, out verifyWorkers) ||
             verifyWorkers is < 1 or > 4))
            throw new CliException("usage", "--verify-workers must be between 1 and 4.");
        if (!context.Has("--all"))
            throw new CliException("usage", "bootstrap currently requires --all.");
        var root = context.PathArgument();
        // Read the DocWizz scope before loading Roslyn. Excluded project sources must never be parsed.
        var includedProjects = DocWizzProjectScope.Read(root);
        progress.Pulse(includedProjects is null
            ? "Loading C# sources" : $"Loading {includedProjects.Count} scoped projects");
        context.CancellationToken.ThrowIfCancellationRequested();
        var includeFile = includedProjects is null ? null : ScopedSourceFilter(root, includedProjects);
        var provider = includeFile is null
            ? RoslynContextProvider.Load(root, context.CancellationToken)
            : RoslynContextProvider.Load(root, includeFile, context.CancellationToken);
        progress.Stage("discover", 0, detail: "Scanning source files");
        var discoveryClock = Stopwatch.StartNew();
        var lastDiscoveryReport = TimeSpan.Zero;
        var discoveryTotal = -1;
        var targets = provider.DiscoverMethods((done, total) =>
            {
                if (total != discoveryTotal)
                {
                    discoveryTotal = total;
                    progress.Stage("discover", total, done, "Scanning source files");
                }
                if (discoveryClock.Elapsed - lastDiscoveryReport < TimeSpan.FromSeconds(2) && done != total) return;
                lastDiscoveryReport = discoveryClock.Elapsed;
                progress.Update(done, "Scanning source files");
            }, context.CancellationToken).ToArray();
        context.CancellationToken.ThrowIfCancellationRequested();
        progress.Stage("configure", 0, detail: "Loading configuration");
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        using var model = ModelClient.Create(config.Ai, context.GetEnvironmentVariable);
        var outputDirectory = context.Options.TryGetValue("--out", out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        ProposalBundle.EnsureOutsideRepository(root, outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var checkpoint = Path.Combine(outputDirectory, "bootstrap-checkpoint.json");
        progress.Stage("fingerprint", 0, detail: "Checking source freshness");
        var fingerprint = Fingerprint(root, config, targets, context.Has("--generate-only"), includedProjects, provider.SourceHashes, context.CancellationToken);
        progress.Pulse("Source fingerprint ready");
        const string checkpointSchema = "testwizz.bootstrap-checkpoint/v1";
        JsonArray saved;
        try
        {
            saved = context.Has("--resume")
                ? BatchResume.Read(checkpoint, checkpointSchema, fingerprint) : new JsonArray();
        }
        catch (CliException ex) when (ex.Code.StartsWith("resume.", StringComparison.Ordinal))
        {
            // Preserve the bootstrap CLI's existing error codes for downstream scripts.
            throw new CliException("bootstrap." + ex.Code["resume.".Length..], ex.Message);
        }
        // Only reuse artifacts whose manifest digest and output confinement still match.
        // A pending/failed execution with a valid generated proposal resumes at the runner,
        // not at the model; a failed generation is retried.
        var valid = saved.OfType<JsonObject>()
            .Where(row => (string?)row["target"] is { Length: > 0 } &&
                (string?)row["generation"] == "ok" &&
                (string?)row["proposal"] is { } proposal &&
                BatchResume.IntactProposal(root, outputDirectory, proposal, (string?)row["artifactSha256"]))
            .ToDictionary(row => (string)row["target"]!, row => row, StringComparer.Ordinal);
        var completed = valid
            .Where(pair => (string?)pair.Value["execution"] == "passed" ||
                context.Has("--generate-only") && (string?)pair.Value["execution"] == "not-run")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var recovered = valid
            .Where(pair => !completed.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => new BootstrapItem(
                pair.Key, "ok", (string?)pair.Value["proposal"], null, "", 0), StringComparer.Ordinal);
        var checkpointItems = new Dictionary<string, JsonObject>(valid, StringComparer.Ordinal);
        void SaveState()
        {
            var data = new JsonArray([.. checkpointItems.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (JsonNode?)pair.Value.DeepClone())]);
            BatchResume.Save(checkpoint, checkpointSchema, fingerprint, data);
        }
        if (!context.Has("--resume")) SaveState();
        var rows = new JsonArray();
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var text = new StringBuilder("# Initial test baseline\n\n");
        text.Append("| Target | Generation | Execution | Proposal |\n|---|---|---|---|\n");
        progress.Stage("generate", targets.Length, valid.Count,
            $"{targets.Length - valid.Count} pending · {valid.Count} restored");
        ProgressEvent(context, "discovered", targets.Length, completed.Count, null, null, clock.Elapsed);
        var pending = targets.Where(target => !valid.ContainsKey(target)).ToArray();
        var generatedResults = new Dictionary<string, BootstrapItem>(recovered, StringComparer.Ordinal);
        // Full worker utilization from the first target, with no head-of-line blocking
        // when one inference is slower than the rest. Only the coordinator writes state.
        var preparationSeconds = clock.Elapsed.TotalSeconds;
        var generationClock = Stopwatch.StartNew();
        var generatedCount = 0;
        if (pending.Length > 0)
            ProgressEvent(context, "generation-started", targets.Length, completed.Count,
                pending[0], null, clock.Elapsed);
        BatchWorkQueue.RunAsync(
            pending,
            maxWorkers,
            (target, _) => Task.FromResult(GenerateTarget(context, provider, model, target)),
            (target, item, active) =>
            {
                generatedResults[item.Target] = item;
                var manifest = item.Proposal is null ? null
                    : BatchResume.ShaFile(Path.Combine(item.Proposal, ProposalBundle.ManifestFile));
                checkpointItems[item.Target] = new JsonObject
                {
                    ["target"] = item.Target,
                    ["generation"] = item.Generation,
                    ["execution"] = item.Generation == "ok"
                        ? context.Has("--generate-only") ? "not-run" : "pending" : "error",
                    ["proposal"] = item.Proposal,
                    ["artifactSha256"] = manifest,
                    ["error"] = item.Error,
                };
                SaveState();
                generatedCount++;
                if (item.Diagnostics.Length > 0) progress.Diagnostics(item.Diagnostics);
                var done = valid.Count + generatedCount;
                var failureCount = checkpointItems.Values.Count(
                    row => (string?)row["generation"] is "failed" or "error");
                progress.Update(done, item.Target, failed: failureCount, active: active);
                ProgressEvent(context, "generated", targets.Length, done, item.Target,
                    item.Generation, clock.Elapsed);
            },
            null,
            (done, active) => progress.Pulse($"Waiting for {active} model request(s)", active),
            context.CancellationToken).GetAwaiter().GetResult();
        generationClock.Stop();
        if (pending.Length > 0)
            progress.Pulse($"{generatedCount / Math.Max(0.001, generationClock.Elapsed.TotalSeconds):F2} targets/s · {maxWorkers} workers");

        progress.Stage(context.Has("--generate-only") ? "review" : "verify", targets.Length,
            completed.Count, context.Has("--generate-only")
                ? "Finalizing proposals" : "Running isolated tests");

        var remainingForVerification = targets.Where(target => !completed.ContainsKey(target)).ToArray();
        // The runtime CLI can be expensive (Docker/Podman version performs a daemon handshake).
        // Check once for this batch. Every test still gets a dedicated, disposable workspace
        // and uniquely named containers; no shared writable test/build directories.
        IContainerRuntime? checkedRuntime = null;
        string? runtimeProblem = null;
        if (!context.Has("--generate-only") && remainingForVerification.Any(target =>
            generatedResults[target].Generation == "ok" && generatedResults[target].Proposal is not null))
        {
            var candidate = context.Runtime(config.Run.Runtime);
            runtimeProblem = candidate.CheckAsync(context.CancellationToken).GetAwaiter().GetResult();
            if (runtimeProblem is null) checkedRuntime = candidate;
        }

        BootstrapVerified VerifyOne(string target)
        {
            var item = generatedResults[target];
            var generation = item.Generation;
            var execution = "not-run";
            var proposal = item.Proposal;
            var error = item.Error;
            using var runDiagnostics = new StringWriter(CultureInfo.InvariantCulture);
            if (generation == "ok" && proposal is not null && !context.Has("--generate-only"))
            {
                if (runtimeProblem is not null)
                {
                    error = "run.no_container_runtime: " + runtimeProblem;
                    execution = "error";
                }
                else
                {
                    try
                    {
                        using var runOutput = new StringWriter(CultureInfo.InvariantCulture);
                        var runContext = context with
                        {
                            Command = RunCommand.Definition,
                            Options = RunOptions(context, proposal),
                            Output = new CliOutput(runOutput, runDiagnostics, OutputFormat.Json),
                            SharedRuntime = checkedRuntime,
                        };
                        var runExit = RunCommand.Definition.Handler(runContext);
                        execution = runExit == ExitCodes.Success ? "passed" : "failed";
                    }
                    catch (Exception ex) when (ex is CliException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                    {
                        error = ex is CliException cli ? cli.Code + ": " + cli.Message : ex.Message;
                        execution = "error";
                    }
                }
            }
            else if (generation == "error") execution = "error";
            return new BootstrapVerified(target, generation, execution, proposal, error,
                runDiagnostics.ToString());
        }

        var verifiedResults = new Dictionary<string, BootstrapVerified>(StringComparer.Ordinal);
        var verificationClock = Stopwatch.StartNew();
        BatchWorkQueue.RunAsync(
            remainingForVerification,
            verifyWorkers,
            (target, _) => Task.FromResult(VerifyOne(target)),
            (target, result, active) =>
            {
                verifiedResults[target] = result;
                if (result.Diagnostics.Length > 0) progress.Diagnostics(result.Diagnostics);
                var finalized = new JsonObject
                {
                    ["target"] = target,
                    ["generation"] = result.Generation,
                    ["execution"] = result.Execution,
                    ["proposal"] = result.Proposal,
                    ["artifactSha256"] = result.Proposal is null ? null
                        : BatchResume.ShaFile(Path.Combine(result.Proposal, ProposalBundle.ManifestFile)),
                    ["error"] = result.Error,
                };
                checkpointItems[target] = finalized;
                // Only the coordinator writes checkpoints, even when containers run in parallel.
                SaveState();
                var failures = verifiedResults.Values.Count(row => row.Generation != "ok" ||
                    row.Execution is not ("passed" or "not-run"));
                var done = completed.Count + verifiedResults.Count;
                progress.Update(done, target, failed: failures, active: active);
                ProgressEvent(context, "finalized", targets.Length, done, target,
                    result.Generation == "ok" && result.Execution is "passed" or "not-run" ? "ok" : "failed",
                    clock.Elapsed);
            },
            null,
            (done, active) => progress.Pulse($"Waiting for {active} isolated test run(s)", active),
            context.CancellationToken).GetAwaiter().GetResult();
        verificationClock.Stop();

        // Aggregate in source order, independent of concurrent completion order.
        foreach (var target in targets)
        {
            if (completed.TryGetValue(target, out var previous))
            {
                var restored = previous.DeepClone().AsObject();
                rows.Add(restored);
                succeeded++;
                AppendRow(text, target, (string?)restored["generation"] ?? "ok",
                    (string?)restored["execution"] ?? "not-run", (string?)restored["proposal"]);
                ProgressEvent(context, "resumed", targets.Length, completed.Count, target, "ok", clock.Elapsed);
                continue;
            }
            var result = verifiedResults[target];
            if (result.Generation == "ok" && result.Execution is "passed" or "not-run") succeeded++;
            else failed++;
            rows.Add(checkpointItems[target].DeepClone());
            AppendRow(text, target, result.Generation, result.Execution, result.Proposal);
        }

        if (targets.Length == 0)
            progress.Note("No eligible C# methods discovered.");
        text.Append(CultureInfo.InvariantCulture, $"\n{targets.Length} targets; {succeeded} successful, {failed} failed, {skipped} skipped. Proposals remain outside the repository; nothing was applied.\n");
        ProgressEvent(context, "finished", targets.Length, targets.Length, null, failed == 0 ? "ok" : "failed", clock.Elapsed);
        progress.Finish(targets.Length, failed, skipped, $"{succeeded} successful");
        context.Output.Result("bootstrap", new JsonObject
        {
            ["schema"] = "testwizz.bootstrap/v1",
            ["total"] = targets.Length,
            ["eligible"] = targets.Length,
            ["resumed"] = completed.Count,
            ["generationWorkersMax"] = maxWorkers,
            ["verificationWorkersMax"] = verifyWorkers,
            ["timings"] = new JsonObject
            {
                ["preparationSeconds"] = Math.Round(preparationSeconds, 1),
                ["generationSeconds"] = Math.Round(generationClock.Elapsed.TotalSeconds, 1),
                ["verificationSeconds"] = Math.Round(verificationClock.Elapsed.TotalSeconds, 1),
                ["totalSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
            },
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["items"] = rows,
        }, text.ToString(), failed == 0 && skipped == 0 ? "ok" : "failed");
        return failed == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static void ProgressEvent(CommandContext context, string phase, int total, int completed,
        string? target, string? status, TimeSpan elapsed)
    {
        if (!context.Has("--progress-jsonl")) return;
        var payload = new JsonObject
        {
            ["schema"] = "testwizz.bootstrap-progress/v1",
            ["phase"] = phase,
            ["total"] = total,
            ["completed"] = completed,
            ["percent"] = total == 0 ? 100 : Math.Min(100, completed * 100 / total),
            ["target"] = target,
            ["status"] = status,
            ["elapsedSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
        };
        context.Output.Stderr.WriteLine(payload.ToJsonString());
    }

    // Nearest .csproj wins, including when project directories are nested. Cache each
    // directory's owning project so large repositories do not repeatedly scan manifests.
    private static Func<string, bool> ScopedSourceFilter(string root, IReadOnlySet<string> projects)
    {
        var owners = new Dictionary<string, string?>(StringComparer.Ordinal);
        var rootFull = Path.GetFullPath(root);
        string? Owner(string directory)
        {
            if (owners.TryGetValue(directory, out var cached)) return cached;
            string? result = null;
            var relative = Path.GetRelativePath(rootFull, directory);
            if (relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal)
                && !relative.StartsWith("..\\", StringComparison.Ordinal))
            {
                result = Directory.EnumerateFiles(directory, "*.csproj")
                    .Select(project => Path.GetRelativePath(rootFull, project).Replace('\\', '/'))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (result is null && !Path.GetFullPath(directory).Equals(rootFull, StringComparison.OrdinalIgnoreCase))
                    result = Owner(Path.GetDirectoryName(directory)!);
            }
            owners[directory] = result;
            return result;
        }
        return relative => projects.Contains(Owner(Path.GetDirectoryName(Path.Combine(rootFull, relative))!) ?? "");
    }

    private sealed record BootstrapItem(string Target, string Generation, string? Proposal, string? Error, string Diagnostics, double Seconds);
    private sealed record BootstrapVerified(string Target, string Generation, string Execution,
        string? Proposal, string? Error, string Diagnostics);

    private static BootstrapItem GenerateTarget(CommandContext context, RoslynContextProvider provider, ModelClient model, string target)
    {
        var timer = Stopwatch.StartNew();
        using var generated = new StringWriter(CultureInfo.InvariantCulture);
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var options = new Dictionary<string, string?>(StringComparer.Ordinal) { ["--for"] = target };
        if (context.Has("--debug")) options["--debug"] = null;
        if (context.Options.TryGetValue("--out", out var output)) options["--out"] = output;
        var inner = context with
        {
            Command = GenerateCommand.Definition,
            Options = options,
            Output = new CliOutput(generated, diagnostics, OutputFormat.Json),
            SharedProvider = provider,
            SharedModel = model,
        };
        try
        {
            var exit = GenerateCommand.Definition.Handler(inner);
            var response = JsonNode.Parse(generated.ToString())!;
            var proposal = (string?)response["result"]?["path"];
            if (exit == ExitCodes.Success && proposal is null)
                throw new CliException("bootstrap.no_proposal", "Generation succeeded without a proposal path.");
            return new BootstrapItem(target, exit == ExitCodes.Success ? "ok" : "failed", proposal,
                null, diagnostics.ToString(), timer.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is CliException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            var message = ex is CliException cli ? cli.Code + ": " + cli.Message : ex.Message;
            return new BootstrapItem(target, "error", null, message, diagnostics.ToString(), timer.Elapsed.TotalSeconds);
        }
    }

    private static void AppendRow(StringBuilder text, string target, string generation, string execution, string? proposal)
    {
        text.Append("| `").Append(target.Replace("|", "\\|", StringComparison.Ordinal))
            .Append("` | ").Append(generation).Append(" | ").Append(execution).Append(" | ")
            .Append(proposal is null ? "—" : "`" + proposal.Replace("|", "\\|", StringComparison.Ordinal) + "`")
            .Append("|\n");
    }

    private static string Fingerprint(string root, TestWizzConfig config, IReadOnlyList<string> targets, bool generateOnly, IReadOnlySet<string>? includedProjects, IReadOnlyList<(string Path, string Sha256)> sources, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add(Path.GetFullPath(root));
        Add(CliApp.Version);
        Add(BatchResume.PublicConfigFingerprint(config));
        Add(generateOnly.ToString());
        foreach (var project in (includedProjects ?? new HashSet<string>(StringComparer.Ordinal)).Order(StringComparer.Ordinal)) Add(project);
        foreach (var target in targets) Add(target);
        foreach (var (path, sha256) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(path);
            Add(sha256);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Dictionary<string, string?> RunOptions(CommandContext context, string proposal)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal) { ["--proposal"] = proposal };
        if (context.Options.TryGetValue("--out", out var outputDirectory))
            options["--out"] = outputDirectory;
        return options;
    }

}
