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
    private const string CheckpointFile = "bootstrap-checkpoint.json";
    private const string CheckpointSchema = "testwizz.bootstrap-checkpoint/v1";
    private const int DefaultRetries = 2;
    private const int MaxRetries = 5;

    /// <summary>Base delay before the first retry; doubles per attempt and is capped at 30 seconds.</summary>
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Overrides the base retry delay in milliseconds (0-60000); intended for tests and slow model servers.</summary>
    internal const string RetryDelayVariable = "TESTWIZZ_RETRY_DELAY_MS";

    public static CommandDefinition Definition { get; } = new(
        "bootstrap",
        "Discover all eligible C# methods, generate test proposals and optionally run them in isolated containers. Never applies changes.",
        "testwizz bootstrap [path] --all [--review] [--generate-only] [--resume | --restart] [--retries <n>] [--max-workers <n>] [--verify-workers <n>] [--out <dir>]",
        Options:
        [
            new("--all", "Discover and process all eligible C# methods."),
            new("--review", "Keep proposals for explicit review (default; no files are applied)."),
            new("--generate-only", "Skip container execution; only generate proposals."),
            new("--resume", "Resume matching sources; reuse completed and generated-but-not-yet-tested proposals."),
            new("--restart", "Replace an existing checkpoint in the output directory and start over (a run without --resume or --restart never replaces one)."),
            new("--retries", "Retries per target for transient model failures such as timeouts, connection errors or HTTP 429/5xx (0-5; default 2). Validation failures are never retried.", "n"),
            new("--out", "Proposal directory outside the repository.", "dir"),
            new("--debug", "Print each generated test source to stderr (may contain sensitive source code)."),
            new("--max-workers", "Model concurrency (1-8); default adaptive 1..4, explicit N uses N.", "n"),
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
        var retries = DefaultRetries;
        if (context.Options.TryGetValue("--retries", out var retryText)
            && (!int.TryParse(retryText, NumberStyles.None, CultureInfo.InvariantCulture, out retries) || retries is < 0 or > MaxRetries))
            throw new CliException("usage", $"--retries must be between 0 and {MaxRetries}.");
        if (!context.Has("--all"))
            throw new CliException("usage", "bootstrap currently requires --all.");
        var resume = context.Has("--resume");
        var restart = context.Has("--restart");
        if (resume && restart)
            throw new CliException("usage", "--resume and --restart cannot be combined.");
        var retryDelay = RetryDelay(context);
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
        var outputDirectory = context.Options.TryGetValue("--out", out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        ProposalBundle.EnsureOutsideRepository(root, outputDirectory);
        var checkpoint = Path.Combine(outputDirectory, CheckpointFile);
        // A checkpoint is the only record of an interrupted run; never replace it by accident.
        if (!resume && !restart && File.Exists(checkpoint))
            throw new CliException("bootstrap.checkpoint_exists",
                $"A bootstrap checkpoint already exists in {outputDirectory}. Use --resume to continue it, " +
                "--restart to discard it and start over, or choose another --out directory.");
        using var model = ModelClient.Create(config.Ai, context.GetEnvironmentVariable);
        Directory.CreateDirectory(outputDirectory);
        progress.Stage("fingerprint", 0, detail: "Checking source freshness");
        var fingerprint = Fingerprint(root, config, targets, context.Has("--generate-only"), includedProjects, provider.SourceHashes, context.CancellationToken);
        progress.Pulse("Source fingerprint ready");
        JsonArray saved;
        try
        {
            saved = resume
                ? BatchResume.Read(checkpoint, CheckpointSchema, fingerprint) : new JsonArray();
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
                pair.Key, "ok", (string?)pair.Value["proposal"], null, "", 0, []), StringComparer.Ordinal);
        var checkpointItems = new Dictionary<string, JsonObject>(valid, StringComparer.Ordinal);
        void SaveState()
        {
            var data = new JsonArray([.. checkpointItems.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (JsonNode?)pair.Value.DeepClone())]);
            BatchResume.Save(checkpoint, CheckpointSchema, fingerprint, data);
        }
        if (!resume) SaveState();
        var rows = new JsonArray();
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var text = new StringBuilder("# Initial test baseline\n\n");
        text.Append("| Target | Generation | Execution | Proposal |\n|---|---|---|---|\n");
        // Generation counters: restored proposals (completed or recovered) count as valid generations.
        var counts = new GenerationCounts(targets.Length, completed.Count, recovered.Count);
        var execution = new ExecutionCounts();
        progress.Stage("generate", targets.Length, counts.Done, counts.Detail());
        // Report restored work before newly finalized work; JSONL progress must be monotonic.
        var restoredCount = 0;
        foreach (var target in targets)
            if (completed.TryGetValue(target, out var done))
            {
                execution.Record((string?)done["execution"] ?? "not-run");
                ProgressEvent(context, "resumed", targets.Length, ++restoredCount,
                    target, "ok", clock.Elapsed, counts, execution: execution);
            }
        ProgressEvent(context, "discovered", targets.Length, counts.Done, null, null, clock.Elapsed, counts);
        var pending = targets.Where(target => !valid.ContainsKey(target)).ToArray();
        // Written by the coordinator while verification workers read it: generation and verification overlap.
        var generatedResults = new System.Collections.Concurrent.ConcurrentDictionary<string, BootstrapItem>(recovered, StringComparer.Ordinal);
        // Full worker utilization from the first target, with no head-of-line blocking
        // when one inference is slower than the rest. Only the coordinator writes state.
        var preparationSeconds = clock.Elapsed.TotalSeconds;
        var generationClock = Stopwatch.StartNew();
        // Without an explicit --max-workers, start at one request and adapt on rolling throughput with
        // hysteresis; an explicit value is used as given (and is always the upper bound).
        var fixedWorkers = context.Has("--max-workers");
        var adaptive = new AdaptiveWorkerLimit(maxWorkers);
        if (pending.Length > 0)
            ProgressEvent(context, "generation-started", targets.Length, counts.Done,
                pending[0], null, clock.Elapsed, counts);
        // The runtime CLI can be expensive (Docker/Podman version performs a daemon handshake): check it once per
        // batch, the first time a target needs it. Every test still gets a dedicated, disposable workspace and
        // uniquely named containers; no shared writable test/build directories.
        var runtimeCheck = new Lazy<Task<(IContainerRuntime? Runtime, string? Problem, string Name)>>(async () =>
        {
            var candidate = context.Runtime(config.Run.Runtime);
            var problem = await candidate.CheckAsync(context.CancellationToken).ConfigureAwait(false);
            return (problem is null ? candidate : null, problem, candidate.Name);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        // Targets with identical restore inputs share one restore; its packages are mounted read-only.
        using var restoreCache = new RestoreCache();

        BootstrapVerified VerifyOne(string target)
        {
            var item = generatedResults[target];
            var generation = item.Generation;
            var outcome = "not-run";
            var proposal = item.Proposal;
            var failure = item.Failure;
            using var runDiagnostics = new StringWriter(CultureInfo.InvariantCulture);
            if (generation == "ok" && proposal is not null && !context.Has("--generate-only"))
            {
                var (checkedRuntime, runtimeProblem, runtimeName) = runtimeCheck.Value.GetAwaiter().GetResult();
                if (runtimeProblem is not null)
                {
                    failure = BatchFailure.Create("run.no_container_runtime", $"No usable container runtime: {runtimeProblem} " +
                        $"TestWizz never runs generated tests on the host; start {runtimeName} or " +
                        "set run.runtime in testwizz.json.");
                    outcome = "error";
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
                            SharedRestoreCache = restoreCache,
                        };
                        var runExit = RunCommand.Definition.Handler(runContext);
                        outcome = runExit == ExitCodes.Success ? "passed" : "failed";
                        if (outcome == "failed") failure = RunFailure(runOutput.ToString());
                    }
                    catch (Exception ex) when (ex is CliException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                    {
                        failure = ex is CliException cli ? BatchFailure.From(cli) : BatchFailure.Create("run.error", ex.Message);
                        outcome = "error";
                    }
                }
            }
            else if (generation == "error") outcome = "error";
            return new BootstrapVerified(target, generation, outcome, proposal, failure,
                runDiagnostics.ToString(), item.Attempts);
        }

        var verifiedResults = new Dictionary<string, BootstrapVerified>(StringComparer.Ordinal);
        var verificationClock = new Stopwatch();
        var generated = 0;
        var generationFinished = false;
        void FinishGeneration()
        {
            generationFinished = true;
            generationClock.Stop();
            if (pending.Length > 0)
                progress.Pulse($"{(counts.Generated + counts.Failed) / Math.Max(0.001, generationClock.Elapsed.TotalSeconds):F2} targets/s · " +
                    (fixedWorkers ? $"{maxWorkers} workers" : $"adaptive {adaptive.Current} of {maxWorkers} workers") + $" · {counts.Detail()}");
            // Targets verified while generation was still running are already counted.
            progress.Stage(context.Has("--generate-only") ? "review" : "verify", targets.Length,
                completed.Count + verifiedResults.Count, context.Has("--generate-only")
                    ? "Finalizing proposals" : "Running isolated tests");
        }
        if (pending.Length == 0) FinishGeneration();

        // Each target is verified as soon as its generation finishes, while other targets are still being
        // generated: model requests and container runs overlap instead of running as two separate phases.
        // Recovered proposals (generated in an earlier run) are verified straight away.
        BatchWorkQueue.RunPipelineAsync(
            pending,
            [.. targets.Where(recovered.ContainsKey)],
            maxWorkers,
            (target, _) => Task.FromResult(GenerateTarget(context, provider, model, target, retries, retryDelay)),
            (target, item, active) =>
            {
                generatedResults[item.Target] = item;
                var manifest = item.Proposal is null ? null
                    : BatchResume.ShaFile(Path.Combine(item.Proposal, ProposalBundle.ManifestFile));
                checkpointItems[item.Target] = CheckpointRow(item.Target, item.Generation,
                    item.Generation == "ok" ? context.Has("--generate-only") ? "not-run" : "pending" : "error",
                    item.Proposal, manifest, item.Failure, item.Attempts);
                // Persist each finished LLM request before the next one is dispatched.
                SaveState();
                counts.Record(item.Generation == "ok");
                if (!fixedWorkers) adaptive.Completed(generationClock.Elapsed);
                if (item.Diagnostics.Length > 0) progress.Diagnostics(item.Diagnostics);
                if (item.Failure is { } failure) ReportFailure(progress, "generation", item.Target, failure, item.Proposal);
                progress.Update(counts.Done, item.Target, failed: counts.Failed, active: active);
                ProgressEvent(context, "generated", targets.Length, counts.Done, item.Target,
                    item.Generation, clock.Elapsed, counts, item.Failure, item.Proposal, item.Attempts.Count + 1);
                if (++generated == pending.Length) FinishGeneration();
                return true;
            },
            verifyWorkers,
            (target, _) =>
            {
                lock (verificationClock)
                    if (!verificationClock.IsRunning) verificationClock.Start();
                return Task.FromResult(VerifyOne(target));
            },
            (target, result, active) =>
            {
                verifiedResults[target] = result;
                if (result.Diagnostics.Length > 0) progress.Diagnostics(result.Diagnostics);
                if (result.Generation == "ok") execution.Record(result.Execution);
                // Generation failures were already reported when they happened.
                if (result.Generation == "ok" && result.Execution is "failed" or "error" && result.Failure is { } failure)
                    ReportFailure(progress, "execution", target, failure, result.Proposal);
                var finalized = CheckpointRow(target, result.Generation, result.Execution, result.Proposal,
                    result.Proposal is null ? null
                        : BatchResume.ShaFile(Path.Combine(result.Proposal, ProposalBundle.ManifestFile)),
                    result.Failure, result.Attempts);
                checkpointItems[target] = finalized;
                // Only the coordinator writes checkpoints, even when containers run in parallel.
                SaveState();
                var failures = verifiedResults.Values.Count(row => row.Generation != "ok" ||
                    row.Execution is not ("passed" or "not-run"));
                var done = completed.Count + verifiedResults.Count;
                if (generationFinished) progress.Update(done, target, failed: failures, active: active);
                ProgressEvent(context, "finalized", targets.Length, done, target,
                    result.Generation == "ok" && result.Execution is "passed" or "not-run" ? "ok" : "failed",
                    clock.Elapsed, counts, result.Failure, result.Proposal, execution: execution);
            },
            (generating, verifying) => progress.Pulse(generationFinished
                ? $"Waiting for {verifying} isolated test run(s)"
                : $"Waiting for {generating} model request(s)" +
                  (verifying > 0 ? $" and {verifying} isolated test run(s)" : "") + $" · {counts.Detail()}",
                generationFinished ? verifying : generating),
            context.CancellationToken,
            () => fixedWorkers ? maxWorkers : adaptive.Current).GetAwaiter().GetResult();
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
        text.Append(CultureInfo.InvariantCulture, $"\n{targets.Length} targets; {succeeded} successful, {failed} failed, {skipped} skipped. ");
        text.Append(CultureInfo.InvariantCulture, $"Generation: {counts.Valid} valid ({counts.Recovered} recovered, {counts.Completed} already complete), {counts.Failed} failed. ");
        text.Append(CultureInfo.InvariantCulture, $"Execution: {execution.Passed} passed, {execution.Failed} failed, {execution.Errors} errors, {execution.NotRun} not run. ");
        text.Append("Proposals remain outside the repository; nothing was applied.\n");
        ProgressEvent(context, "finished", targets.Length, targets.Length, null, failed == 0 ? "ok" : "failed", clock.Elapsed, counts, execution: execution);
        progress.Finish(targets.Length, failed, skipped, $"{succeeded} successful · {execution.Passed} passed · {execution.Failed + execution.Errors} execution issue(s)");
        context.Output.Result("bootstrap", new JsonObject
        {
            ["schema"] = "testwizz.bootstrap/v1",
            ["total"] = targets.Length,
            ["eligible"] = targets.Length,
            ["resumed"] = completed.Count,
            ["recovered"] = recovered.Count,
            ["generationWorkersMax"] = maxWorkers,
            ["generationWorkersAdaptive"] = !fixedWorkers,
            ["retries"] = retries,
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
            ["generation"] = counts.ToJson(),
            ["execution"] = execution.ToJson(),
            ["checkpoint"] = checkpoint,
            ["items"] = rows,
        }, text.ToString(), failed == 0 && skipped == 0 ? "ok" : "failed");
        return failed == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    /// <summary>
    /// One checkpoint row. <c>error</c> keeps the legacy <c>code: message</c> string; <c>failure</c> and
    /// <c>attempts</c> are additive, so older checkpoints stay readable and newer ones resume on older readers.
    /// </summary>
    private static JsonObject CheckpointRow(string target, string generation, string execution, string? proposal,
        string? artifactSha256, BatchFailure? failure, List<BatchFailure> attempts)
    {
        var row = new JsonObject
        {
            ["target"] = target,
            ["generation"] = generation,
            ["execution"] = execution,
            ["proposal"] = proposal,
            ["artifactSha256"] = artifactSha256,
            ["error"] = failure?.ToString(),
            ["failure"] = failure?.ToJson(),
        };
        if (attempts.Count > 0)
            row["attempts"] = new JsonArray([.. attempts.Select(a => (JsonNode?)a.ToJson())]);
        return row;
    }

    /// <summary>One sanitized line per failure on stderr; JSONL mode carries the same data in its events instead.</summary>
    private static void ReportFailure(BatchProgress progress, string phase, string target, BatchFailure failure, string? proposal) =>
        progress.Failure($"{phase} failed for {target}: {failure}" + (proposal is null ? "" : $" · proposal {proposal}"));

    /// <summary>The reason a failed <c>run</c> reported; unreadable output is itself an explicit failure reason.</summary>
    private static BatchFailure RunFailure(string json)
    {
        try
        {
            return BatchFailure.FromRun(JsonNode.Parse(json));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return BatchFailure.Create("run.output_invalid", $"run did not produce a JSON result ({ex.Message}).");
        }
    }

    private static TimeSpan RetryDelay(CommandContext context)
    {
        var text = context.GetEnvironmentVariable(RetryDelayVariable);
        if (string.IsNullOrWhiteSpace(text)) return DefaultRetryDelay;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds > 60000)
            throw new CliException("usage", $"{RetryDelayVariable} must be between 0 and 60000.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>Generation outcomes of the run, including proposals restored from the checkpoint.</summary>
    private sealed class GenerationCounts(int total, int completed, int recovered)
    {
        public int Total { get; } = total;
        public int Completed { get; } = completed;
        public int Recovered { get; } = recovered;
        public int Generated { get; private set; }
        public int Failed { get; private set; }
        public int Valid => Completed + Recovered + Generated;
        public int Done => Valid + Failed;
        public int Remaining => Math.Max(0, Total - Done);

        public void Record(bool ok)
        {
            if (ok) Generated++;
            else Failed++;
        }

        public string Detail() =>
            $"{Valid} valid ({Completed + Recovered} restored) · {Failed} failed · {Remaining} remaining";

        public JsonObject ToJson() => new()
        {
            ["valid"] = Valid,
            ["generated"] = Generated,
            ["recovered"] = Recovered,
            ["completed"] = Completed,
            ["failed"] = Failed,
            ["remaining"] = Remaining,
        };
    }

    private sealed class ExecutionCounts
    {
        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public int Errors { get; private set; }
        public int NotRun { get; private set; }

        public void Record(string outcome)
        {
            switch (outcome)
            {
                case "passed": Passed++; break;
                case "failed": Failed++; break;
                case "error": Errors++; break;
                default: NotRun++; break;
            }
        }

        public JsonObject ToJson() => new()
        {
            ["passed"] = Passed,
            ["failed"] = Failed,
            ["error"] = Errors,
            ["notRun"] = NotRun,
        };
    }

    private static void ProgressEvent(CommandContext context, string phase, int total, int completed,
        string? target, string? status, TimeSpan elapsed, GenerationCounts counts, BatchFailure? failure = null,
        string? proposal = null, int? attempts = null, ExecutionCounts? execution = null)
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
            ["generation"] = counts.ToJson(),
        };
        if (execution is not null) payload["execution"] = execution.ToJson();
        if (failure is not null)
        {
            payload["error"] = failure.ToJson();
            payload["proposal"] = proposal;
        }
        if (attempts is not null) payload["attempts"] = attempts;
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

    /// <param name="Attempts">Failures of earlier attempts that were retried (empty when the first attempt counted).</param>
    private sealed record BootstrapItem(string Target, string Generation, string? Proposal, BatchFailure? Failure,
        string Diagnostics, double Seconds, List<BatchFailure> Attempts);

    private sealed record BootstrapVerified(string Target, string Generation, string Execution,
        string? Proposal, BatchFailure? Failure, string Diagnostics, List<BatchFailure> Attempts);

    /// <summary>
    /// Generates one target. Transient model failures are retried up to <paramref name="retries"/> times with
    /// exponential back-off; a cancelled wait stops immediately. Every attempt runs the full validation in
    /// <c>generate</c>, so a retry can never turn an invalid proposal into an accepted one.
    /// </summary>
    private static BootstrapItem GenerateTarget(CommandContext context, RoslynContextProvider provider, ModelClient model,
        string target, int retries, TimeSpan retryDelay)
    {
        var timer = Stopwatch.StartNew();
        var attempts = new List<BatchFailure>();
        var diagnostics = new StringBuilder();
        for (var attempt = 1; ; attempt++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var item = GenerateOnce(context, provider, model, target, diagnostics);
            if (item.Failure is not { } failure || item.Generation != "error"
                || attempt > retries || !BatchFailure.IsRecoverable(failure.Code, failure.Message))
                return item with { Diagnostics = diagnostics.ToString(), Seconds = timer.Elapsed.TotalSeconds, Attempts = attempts };
            attempts.Add(failure);
            var delay = TimeSpan.FromMilliseconds(Math.Min(30000, retryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
            if (context.CancellationToken.WaitHandle.WaitOne(delay))
                context.CancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static BootstrapItem GenerateOnce(CommandContext context, RoslynContextProvider provider, ModelClient model, string target, StringBuilder log)
    {
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
                exit == ExitCodes.Success ? null : BatchFailure.FromGeneration(response["result"]), "", 0, []);
        }
        catch (Exception ex) when (ex is CliException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            var failure = ex is CliException cli ? BatchFailure.From(cli) : BatchFailure.Create("generate.error", ex.Message);
            return new BootstrapItem(target, "error", null, failure, "", 0, []);
        }
        finally
        {
            log.Append(diagnostics);
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
