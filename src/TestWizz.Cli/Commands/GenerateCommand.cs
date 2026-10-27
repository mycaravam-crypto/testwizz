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
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var root = context.PathArgument();
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

    private sealed record BatchItem(string Target, string Status, string? Proposal, string? ErrorCode, string DebugText, double Seconds);

    private static int RunBatch(CommandContext context, List<ChangedCode.RankedMethod> ranked, string since, int minRisk)
    {
        if (ranked.Count > MaxBatchTargets)
            throw new CliException("target.too_many", $"Changed methods: {ranked.Count}; maximum is {MaxBatchTargets}. Narrow the Git comparison.");
        var limit = 4;
        if (context.Options.TryGetValue(MaxWorkersOption, out var text)
            && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 8))
            throw new CliException("usage", "--max-workers must be between 1 and 8.");
        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        using var model = ModelClient.Create(config.Ai, context.GetEnvironmentVariable);
        var provider = RoslynContextProvider.Load(root);
        var results = new BatchItem[ranked.Count];
        var workers = 1;
        double priorThroughput = 0;
        var position = 0;
        while (position < ranked.Count)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(workers, ranked.Count - position);
            var start = position;
            var stopwatch = Stopwatch.StartNew();
            // Execute independent LLM requests concurrently, but share the read-only Roslyn compilation.
            // Each worker keeps console output private until its complete result has been collected.
            var tasks = Enumerable.Range(start, count).Select(index => Task.Run(() =>
            {
                var target = ranked[index].Target;
                using var stdout = new StringWriter(CultureInfo.InvariantCulture);
                using var stderr = new StringWriter(CultureInfo.InvariantCulture);
                var options = context.Options
                    .Where(option => option.Key is not (AllChangedOption or SinceOption or MaxWorkersOption))
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
                    return new BatchItem(target, exit == ExitCodes.Success ? "ok" : "failed",
                        (string?)response["result"]?["path"], null, stderr.ToString(), timer.Elapsed.TotalSeconds);
                }
                catch (CliException ex)
                {
                    return new BatchItem(target, "error", null, ex.Code, stderr.ToString(), timer.Elapsed.TotalSeconds);
                }
            }, context.CancellationToken)).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            stopwatch.Stop();
            for (var i = 0; i < tasks.Length; i++)
            {
                var item = tasks[i].Result;
                results[start + i] = item;
                if (item.DebugText.Length > 0)
                    context.Output.Stderr.Write(item.DebugText);
                context.Output.Stderr.WriteLine($"testwizz batch: {start + i + 1}/{ranked.Count} {item.Target}: {item.Status} ({item.Seconds:F1}s)");
            }
            position += count;
            var throughput = count / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            context.Output.Stderr.WriteLine($"testwizz batch: workers={workers}, throughput={throughput:F3} targets/s");
            // Additive ramp-up with regression backoff: do not increase concurrency when
            // throughput is below 90% of the preceding wave. No assumption about GPU size.
            if (workers > 1 && priorThroughput > 0 && throughput < priorThroughput * 0.9)
                workers = Math.Max(1, workers - 1);
            else if (workers < limit && position < ranked.Count)
                workers++;
            priorThroughput = throughput;
        }
        var rows = new JsonArray();
        var markdown = new StringBuilder("# TestWizz changed-code generation\n\n| Target | Status | Proposal |\n|---|---|---|\n");
        var failed = 0;
        for (var i = 0; i < results.Length; i++)
        {
            var item = results[i];
            if (item.Status != "ok") failed++;
            rows.Add(new JsonObject
            {
                ["target"] = item.Target,
                ["risk"] = ranked[i].Risk,
                ["status"] = item.Status,
                ["proposal"] = item.Proposal,
                ["errorCode"] = item.ErrorCode,
                ["seconds"] = item.Seconds,
            });
            markdown.Append("| `").Append(item.Target.Replace("|", "\\|", StringComparison.Ordinal))
                .Append("` | ").Append(item.Status).Append(" | ")
                .Append(item.Proposal is null ? "—" : "`" + item.Proposal.Replace("|", "\\|", StringComparison.Ordinal) + "`")
                .Append(" |\n");
        }
        markdown.Append(CultureInfo.InvariantCulture, $"\n{results.Length} target(s), {failed} failed. No repository files were modified.\n");
        context.Output.Result("generate", new JsonObject
        {
            ["schema"] = "testwizz.generate-batch/v1",
            ["base"] = since,
            ["total"] = results.Length,
            ["failed"] = failed,
            ["minRisk"] = minRisk,
            ["maxWorkers"] = limit,
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
