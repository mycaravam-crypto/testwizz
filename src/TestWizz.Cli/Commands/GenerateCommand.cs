using System.Globalization;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text;
using TestWizz.AI;
using TestWizz.Core.Configuration;
using TestWizz.Core.Context;
using TestWizz.Core.Generation;
using TestWizz.Core.Planning;
using TestWizz.Core.Proposals;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

public static class GenerateCommand
{
    private const string AiPlanOption = "--ai-plan";
    private const string OutOption = "--out";
    private const string SinceOption = "--since";
    private const string ListChangedOption = "--list-changed";
    private const string AllChangedOption = "--all-changed";
    private const string MinRiskOption = "--min-risk";
    private const string DebugOption = "--debug";
    private const string MaxWorkersOption = "--max-workers";
    private const string ContinueOnErrorOption = "--continue-on-error";
    private const string ResumeOption = "--resume";
    private const int MaxBatchTargets = 20;

    public static CommandDefinition Definition { get; } = new(
        "generate",
        "Plan scenarios, let the local model write xUnit tests for a C# method and save them as a proposal bundle outside the repository. Nothing in the repository is changed, built or run.",
        "testwizz generate [path] (--for <method> | --since <git-ref>) [--out <dir>] [--ai-plan] [--max-chars <n>]",
        Options:
        [
            .. ContextCommand.TargetOptions,
            new(OutOption, "Directory for proposal bundles (default: 'output' in testwizz.json). Must be outside the repository.", "dir"),
            new(AiPlanOption, "Also let the model propose extra scenarios before writing tests (same as plan --ai)."),
            new(SinceOption, "Find C# methods changed since this Git ref; generate only when exactly one matches.", "ref"),
            new(ListChangedOption, "List changed C# methods without AI calls; suitable for CI."),
            new(AllChangedOption, "Generate proposals for up to 20 changed C# methods and produce one CI summary."),
            new(MinRiskOption, "Minimum deterministic method-risk score (0-100).", "score"),
            new(DebugOption, "Print each generated test source to stderr (may contain sensitive source code)."),
            new(MaxWorkersOption, "Maximum adaptive LLM workers for --all-changed (1-8; default 4).", "n"),
            new(ContinueOnErrorOption, "Continue --all-changed after a failed target (default: stop after the first failed wave)."),
            new(ResumeOption, "Resume a matching changed-method batch without repeating verified proposals."),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var root = context.PathArgument();
        if (context.Has(ResumeOption) && (!context.Has(SinceOption) || !context.Has(AllChangedOption)))
            throw new CliException("usage", "--resume requires --since <ref> --all-changed.");
        if ((context.Has(ListChangedOption) || context.Has(AllChangedOption)) && !context.Has(SinceOption))
            throw new CliException("usage", "--list-changed and --all-changed require --since.");
        if (context.Has(AllChangedOption) && (context.Has(ListChangedOption) || context.Has("--for")))
            throw new CliException("usage", "--all-changed cannot be combined with --list-changed or --for.");
        if (context.Has(MinRiskOption) && !context.Has(SinceOption))
            throw new CliException("usage", "--min-risk requires --since.");
        var minRisk = 0;
        if (context.Has(MinRiskOption) && (!int.TryParse(context.Options[MinRiskOption], NumberStyles.None, CultureInfo.InvariantCulture, out minRisk) || minRisk is < 0 or > 100))
            throw new CliException("usage", "--min-risk must be between 0 and 100.");
        if (context.Has(SinceOption))
        {
            var since = context.Options[SinceOption];
            List<ChangedCode.RankedMethod> ranked;
            List<string> targets;
            try
            {
                ranked = ChangedCode.Ranked(root, since ?? "").Where(m => m.Risk >= minRisk).ToList();
                targets = ranked.Select(m => m.Target).ToList();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new CliException("git.diff", ex.Message);
            }

            if (context.Has(ListChangedOption))
            {
                var report = new JsonObject
                {
                    ["schema"] = "testwizz.changed-targets/v1",
                    ["base"] = since,
                    ["count"] = targets.Count,
                    ["risk"] = new JsonArray([.. ranked.Select(m => (JsonNode?)new JsonObject { ["target"] = m.Target, ["score"] = m.Risk, ["reasons"] = new JsonArray([.. m.Reasons.Select(r => (JsonNode?)JsonValue.Create(r))]) })]),
                    ["targets"] = new JsonArray([.. targets.Select(t => (JsonNode?)JsonValue.Create(t))]),
                };
                var markdown = new StringBuilder("# Changed C# targets\n\n| Target |\n|---|\n");
                foreach (var target in targets)
                    markdown.Append("| `").Append(target.Replace("|", "\\\\|", StringComparison.Ordinal)).Append("` |\n");
                context.Output.Result("generate", report, markdown.ToString());
                return ExitCodes.Success;
            }
            if (context.Has(AllChangedOption))
                return RunBatch(context, ranked, since ?? "", minRisk);

            if (targets.Count == 0)
            {
                throw new CliException("target.not_found", "No changed C# methods found for --since.");
            }

            if (targets.Count > 1 && !context.Options.ContainsKey("--for"))
            {
                throw new CliException("target.ambiguous", "Multiple methods changed; select one with --for: " + string.Join(", ", targets));
            }

            if (context.Options.TryGetValue("--for", out var requested) && !targets.Contains(requested, StringComparer.Ordinal))
            {
                throw new CliException("target.not_changed", $"--for '{requested}' is not among methods changed since '{since}'.");
            }

            if (!context.Has("--for"))
            {
                var selected = context.Options.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                selected["--for"] = targets[0];
                context = context with { Options = selected };
            }
        }

        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var settings = config.Ai;
        var output = context.Options.TryGetValue(OutOption, out var outText) && !string.IsNullOrWhiteSpace(outText)
            ? Path.GetFullPath(outText, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        try
        {
            ProposalBundle.EnsureOutsideRepository(root, output);
        }
        catch (ProposalException ex)
        {
            throw new CliException(ex.Code, ex.Message);
        }

        var provider = context.SharedProvider ?? RoslynContextProvider.Load(root);
        ContextDocument document;
        try
        {
            document = context.SharedProvider is null
                ? ContextCommand.Load(context, Definition.Usage)
                : provider.GetContext(context.Options["--for"]!, CliApp.Version);
        }
        catch (ContextException ex)
        {
            throw ContextCommand.ToCliException(ex);
        }
        var extractor = new CSharpScenarioExtractor();

        GenerationResult result;
        PlanDocument plan;
        try
        {
            var extraction = extractor.Extract(document);
            using var ownedClient = context.SharedModel is null ? ModelClient.Create(settings, context.GetEnvironmentVariable) : null;
            var client = context.SharedModel ?? ownedClient!;
            var enrichment = context.Has(AiPlanOption)
                ? ScenarioEnricher.EnrichAsync(client, document, extraction, context.CancellationToken).GetAwaiter().GetResult()
                : null;
            plan = ScenarioPlanner.Create(document, extractor.Version, extraction, enrichment);
            var conventions = XUnitConventions.Detect(root, document);
            if (conventions.Findings.FirstOrDefault(f => f.Severity == "error") is { } blocking)
            {
                throw new CliException(blocking.Code, blocking.Message);
            }

            result = XUnitGenerator.GenerateAsync(client, provider, document, plan, conventions, context.CancellationToken).GetAwaiter().GetResult();
        }
        catch (ContextException ex)
        {
            throw ContextCommand.ToCliException(ex);
        }
        catch (ModelClientException ex)
        {
            throw new CliException(
                $"ai.{ex.Code}",
                $"{ex.Message} Run 'testwizz ai check' for details.",
                ex.IsConfigurationError ? ExitCodes.Error : ExitCodes.ValidationFailed);
        }

        Proposal proposal;
        try
        {
            proposal = ProposalBundle.Write(root, output, document, plan, result);
        }
        catch (ProposalException ex)
        {
            throw new CliException(ex.Code, ex.Message);
        }

        if (context.Has(DebugOption))
        {
            foreach (var file in result.Files)
            {
                context.Output.Stderr.WriteLine($"[testwizz debug] BEGIN generated test: {file.Path}");
                context.Output.Stderr.Write(file.Content);
                if (!file.Content.EndsWith('\n')) context.Output.Stderr.WriteLine();
                context.Output.Stderr.WriteLine($"[testwizz debug] END generated test: {file.Path}");
            }
        }

        var json = proposal.Manifest.DeepClone().AsObject();
        json["path"] = proposal.Directory;
        context.Output.Result("generate", json, ToText(result, proposal), result.IsValid ? "ok" : "failed");
        return result.IsValid ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private sealed record BatchItem(string Target, string Status, string? Proposal,
        string? ErrorCode, string DebugText, double Seconds, string? ArtifactSha256);

    private static int RunBatch(CommandContext context, List<ChangedCode.RankedMethod> ranked, string since, int minRisk)
    {
        if (ranked.Count > MaxBatchTargets)
            throw new CliException("target.too_many",
                $"Changed methods: {ranked.Count}; maximum is {MaxBatchTargets}. Narrow the Git comparison.");
        var limit = 4;
        if (context.Options.TryGetValue(MaxWorkersOption, out var text)
            && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 8))
            throw new CliException("usage", "--max-workers must be between 1 and 8.");
        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var output = context.Options.TryGetValue(OutOption, out var outText) && !string.IsNullOrWhiteSpace(outText)
            ? Path.GetFullPath(outText, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        try { ProposalBundle.EnsureOutsideRepository(root, output); }
        catch (ProposalException ex) { throw new CliException(ex.Code, ex.Message); }
        Directory.CreateDirectory(output);
        using var progress = BatchProgress.For(context, "generate");
        progress.Stage("prepare", 0, detail: "Loading changed C# targets");
        using var model = ModelClient.Create(config.Ai, context.GetEnvironmentVariable);
        var provider = RoslynContextProvider.Load(root);
        var fingerprintParts = new List<string>
        {
            "testwizz.generate-batch/v2", CliApp.Version, Path.GetFullPath(root),
            Path.GetFullPath(output), since, minRisk.ToString(CultureInfo.InvariantCulture),
            BatchResume.PublicConfigFingerprint(config), context.Has(AiPlanOption).ToString(),
            context.Options.GetValueOrDefault("--max-chars") ?? "",
        };
        fingerprintParts.AddRange(ranked.Select(row => row.Target + ":" + row.Risk.ToString(CultureInfo.InvariantCulture)));
        fingerprintParts.AddRange(provider.SourceHashes.Select(row => row.Path + ":" + row.Sha256));
        var fingerprint = BatchResume.Fingerprint(fingerprintParts);
        var checkpointFile = Path.Combine(output, "generate-checkpoint.json");
        const string schema = "testwizz.generate-checkpoint/v1";
        var saved = context.Has(ResumeOption)
            ? BatchResume.Read(checkpointFile, schema, fingerprint) : new JsonArray();
        var eligible = ranked.Select(row => row.Target).ToHashSet(StringComparer.Ordinal);
        var results = new Dictionary<string, BatchItem>(StringComparer.Ordinal);
        foreach (var row in saved.OfType<JsonObject>())
        {
            var target = (string?)row["target"];
            var proposal = (string?)row["proposal"];
            var digest = (string?)row["artifactSha256"];
            if (target is null || !eligible.Contains(target) ||
                (string?)row["status"] != "ok" ||
                !BatchResume.IntactProposal(root, output, proposal, digest))
                continue;
            results[target] = new BatchItem(target, "resumed", proposal, null, "", 0, digest);
        }
        void Save()
        {
            var entries = new JsonArray([.. results.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (JsonNode?)new JsonObject
                {
                    ["target"] = pair.Key,
                    ["status"] = pair.Value.Status == "resumed" ? "ok" : pair.Value.Status,
                    ["proposal"] = pair.Value.Proposal,
                    ["artifactSha256"] = pair.Value.ArtifactSha256,
                    ["errorCode"] = pair.Value.ErrorCode,
                    ["seconds"] = pair.Value.Seconds,
                })]);
            BatchResume.Save(checkpointFile, schema, fingerprint, entries);
        }
        if (!context.Has(ResumeOption)) Save();
        var pending = ranked.Where(row => !results.ContainsKey(row.Target)).ToArray();
        progress.Stage("generate", ranked.Count, results.Count,
            $"{pending.Length} pending · {results.Count} restored");
        var continueOnError = context.Has(ContinueOnErrorOption);
        var aborted = false;
        var workers = 1;
        double priorThroughput = 0;
        var position = 0;
        while (position < pending.Length)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(workers, pending.Length - position);
            var start = position;
            var stopwatch = Stopwatch.StartNew();
            progress.Update(results.Count, pending[start].Target, active: count);
            var tasks = Enumerable.Range(start, count).Select(index => Task.Run(() =>
            {
                var target = pending[index].Target;
                using var stdout = new StringWriter(CultureInfo.InvariantCulture);
                using var stderr = new StringWriter(CultureInfo.InvariantCulture);
                var options = context.Options
                    .Where(option => option.Key is not
                        (AllChangedOption or SinceOption or MinRiskOption or MaxWorkersOption or ContinueOnErrorOption or ResumeOption))
                    .ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal);
                options["--for"] = target;
                var inner = context with
                {
                    Options = options,
                    Output = new CliOutput(stdout, stderr, OutputFormat.Json),
                    SharedProvider = provider,
                    SharedModel = model,
                };
                var timer = Stopwatch.StartNew();
                try
                {
                    var exit = Run(inner);
                    var response = JsonNode.Parse(stdout.ToString())!;
                    var proposal = (string?)response["result"]?["path"];
                    var digest = proposal is null ? null
                        : BatchResume.ShaFile(Path.Combine(proposal, ProposalBundle.ManifestFile));
                    if (exit == ExitCodes.Success && (proposal is null || digest is null))
                        throw new CliException("batch.proposal_missing", "Generated bundle manifest is missing.");
                    return new BatchItem(target, exit == ExitCodes.Success ? "ok" : "failed",
                        proposal, null, stderr.ToString(), timer.Elapsed.TotalSeconds, digest);
                }
                catch (CliException ex)
                {
                    return new BatchItem(target, "error", null, ex.Code, stderr.ToString(),
                        timer.Elapsed.TotalSeconds, null);
                }
            }, context.CancellationToken)).ToList();
            var failedWave = false;
            while (tasks.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var completion = Task.WhenAny(tasks);
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(15), context.CancellationToken);
                if (Task.WhenAny(completion, heartbeat).GetAwaiter().GetResult() == heartbeat)
                {
                    progress.Pulse($"Waiting for {tasks.Count} model request(s)", tasks.Count);
                    continue;
                }
                var task = completion.GetAwaiter().GetResult();
                tasks.Remove(task);
                var item = task.GetAwaiter().GetResult();
                results[item.Target] = item;
                // A successful completion is durable even if another worker is cancelled.
                Save();
                failedWave |= item.Status != "ok";
                if (item.DebugText.Length > 0) progress.Diagnostics(item.DebugText);
                progress.Update(results.Count, item.Target,
                    failed: results.Values.Count(entry => entry.Status is "failed" or "error"), active: tasks.Count);
            }
            stopwatch.Stop();
            position += count;
            if (!continueOnError && failedWave)
            {
                aborted = position < pending.Length;
                progress.Note($"Stopped after failed wave; {pending.Length - position} target(s) not started. " +
                    "Use --continue-on-error to process all.");
                break;
            }
            var throughput = count / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            progress.Pulse($"{throughput:F2} targets/s · {workers} worker(s)");
            if (workers > 1 && priorThroughput > 0 && throughput < priorThroughput * 0.9)
                workers = Math.Max(1, workers - 1);
            else if (workers < limit && position < pending.Length)
                workers++;
            priorThroughput = throughput;
        }
        var rows = new JsonArray();
        var markdown = new StringBuilder("# TestWizz changed-code generation\n\n| Target | Status | Proposal |\n|---|---|---|\n");
        var failed = 0;
        foreach (var target in ranked)
        {
            if (!results.TryGetValue(target.Target, out var item)) continue;
            if (item.Status is not ("ok" or "resumed")) failed++;
            rows.Add(new JsonObject
            {
                ["target"] = item.Target,
                ["risk"] = target.Risk,
                ["status"] = item.Status,
                ["proposal"] = item.Proposal,
                ["errorCode"] = item.ErrorCode,
                ["seconds"] = item.Seconds,
            });
            markdown.Append("| ").Append(item.Target.Replace("|", "\\|", StringComparison.Ordinal))
                .Append(" | ").Append(item.Status).Append(" | ")
                .Append((item.Proposal ?? "—").Replace("|", "\\|", StringComparison.Ordinal))
                .Append(" |\n");
        }
        markdown.Append(CultureInfo.InvariantCulture,
            $"\n{results.Count} of {ranked.Count} target(s) processed, {failed} failed; " +
            $"{ranked.Count - results.Count} not started. No repository files were modified.\n");
        progress.Finish(results.Count, failed, detail: $"{ranked.Count - results.Count} not started");
        context.Output.Result("generate", new JsonObject
        {
            ["schema"] = "testwizz.generate-batch/v1",
            ["base"] = since,
            ["total"] = ranked.Count,
            ["processed"] = results.Count,
            ["resumed"] = results.Values.Count(item => item.Status == "resumed"),
            ["notStarted"] = ranked.Count - results.Count,
            ["aborted"] = aborted,
            ["failed"] = failed,
            ["minRisk"] = minRisk,
            ["maxWorkers"] = limit,
            ["checkpoint"] = checkpointFile,
            ["items"] = rows,
        }, markdown.ToString(), failed > 0 ? "failed" : "ok");
        return failed > 0 ? ExitCodes.ValidationFailed : ExitCodes.Success;
    }

    private static string ToText(GenerationResult r, Proposal proposal)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Proposal {proposal.Id} for {r.TargetSignature}\n");
        text.Append(CultureInfo.InvariantCulture, $"Model: {r.Generator.Provider} {r.Generator.Model} · prompt {r.Generator.PromptTemplate}\n");
        text.Append(CultureInfo.InvariantCulture, $"Test project: {(string?)r.Conventions["testProject"] ?? "(none found)"} · mocking: {(string?)r.Conventions["mocking"] ?? "none"} · assertions: {(string?)r.Conventions["assertions"]}\n");
        foreach (var file in r.Files)
        {
            text.Append(CultureInfo.InvariantCulture, $"Proposed file: {file.Path}\n");
        }

        text.Append("\nTests:\n");
        foreach (var test in r.Tests)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {test.Name} (line {test.Line}) -> {(test.Scenarios.Count == 0 ? "no scenario" : string.Join(", ", test.Scenarios))}\n");
        }

        if (r.Assumptions.Count > 0)
        {
            text.Append("\nAssumptions:\n");
            foreach (var assumption in r.Assumptions)
            {
                text.Append(CultureInfo.InvariantCulture, $"  - {assumption}\n");
            }
        }

        if (r.Findings.Count > 0)
        {
            text.Append("\nFindings:\n");
            foreach (var f in r.Findings)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {f.Severity,-7} {f.Code}{(f.Line is { } line ? $" (line {line})" : "")}: {f.Message}\n");
            }
        }

        text.Append(r.IsValid ? "\nResult: OK" : "\nResult: FAILED (see errors above)");
        text.Append(". The repository was not changed and nothing was built or run.\n");
        text.Append(CultureInfo.InvariantCulture, $"Bundle: {proposal.Directory}\n");
        text.Append(CultureInfo.InvariantCulture, $"Review {Path.Combine(proposal.Directory, "findings.md")} and {Path.Combine(proposal.Directory, "changes.patch")}.\n");
        return text.ToString();
    }
}
