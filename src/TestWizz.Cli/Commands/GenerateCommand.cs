using System.Globalization;
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
            List<string> targets;
            try
            {
                targets = ChangedCode.Ranked(root, since ?? "").Where(m => m.Risk >= minRisk).Select(m => m.Target).ToList();
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
                    ["risk"] = new JsonArray([.. ChangedCode.Ranked(root, since ?? "").Where(m => m.Risk >= minRisk).Select(m => (JsonNode?)new JsonObject { ["target"] = m.Target, ["score"] = m.Risk, ["reasons"] = new JsonArray([.. m.Reasons.Select(r => (JsonNode?)JsonValue.Create(r))]) })]),
                    ["targets"] = new JsonArray([.. targets.Select(t => (JsonNode?)JsonValue.Create(t))]),
                };
                var markdown = new StringBuilder("# Changed C# targets\n\n| Target |\n|---|\n");
                foreach (var target in targets)
                    markdown.Append("| `").Append(target.Replace("|", "\\\\|", StringComparison.Ordinal)).Append("` |\n");
                context.Output.Result("generate", report, markdown.ToString());
                return ExitCodes.Success;
            }
            if (context.Has(AllChangedOption))
                return RunBatch(context, targets, since ?? "", minRisk);

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

        var document = ContextCommand.Load(context, Definition.Usage);
        var provider = RoslynContextProvider.Load(root);
        var extractor = new CSharpScenarioExtractor();

        GenerationResult result;
        PlanDocument plan;
        try
        {
            var extraction = extractor.Extract(document);
            using var client = ModelClient.Create(settings, context.GetEnvironmentVariable);
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

        var json = proposal.Manifest.DeepClone().AsObject();
        json["path"] = proposal.Directory;
        context.Output.Result("generate", json, ToText(result, proposal), result.IsValid ? "ok" : "failed");
        return result.IsValid ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static int RunBatch(CommandContext context, IReadOnlyList<string> targets, string since, int minRisk)
    {
        if (targets.Count > MaxBatchTargets)
            throw new CliException("target.too_many", $"Changed methods: {targets.Count}; maximum is {MaxBatchTargets}. Narrow the Git comparison.");
        var rows = new JsonArray();
        var markdown = new StringBuilder("# TestWizz changed-code generation\n\n");
        markdown.Append("| Target | Status | Proposal |\n|---|---|---|\n");
        var failed = 0;
        foreach (var target in targets)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var options = context.Options
                .Where(option => option.Key is not (AllChangedOption or SinceOption))
                .ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal);
            options["--for"] = target;
            var inner = context with
            {
                Options = options,
                Output = new CliOutput(stdout, context.Output.Stderr, OutputFormat.Json),
            };
            string status;
            string? proposalPath = null;
            string? errorCode = null;
            try
            {
                var exitCode = Run(inner);
                var response = JsonNode.Parse(stdout.ToString())!;
                proposalPath = (string?)response["result"]?["path"];
                status = exitCode == ExitCodes.Success ? "ok" : "failed";
                if (exitCode != ExitCodes.Success) failed++;
            }
            catch (CliException ex)
            {
                status = "error";
                errorCode = ex.Code;
                failed++;
            }

            rows.Add(new JsonObject
            {
                ["target"] = target,
                ["risk"] = ChangedCode.Ranked(context.PathArgument(), since).First(m => m.Target == target).Risk,
                ["status"] = status,
                ["proposal"] = proposalPath,
                ["errorCode"] = errorCode,
            });
            markdown.Append("| `").Append(target.Replace("|", "\\|", StringComparison.Ordinal))
                .Append("` | ").Append(status).Append(" | ")
                .Append(proposalPath is null ? "—" : "`" + proposalPath.Replace("|", "\\|", StringComparison.Ordinal) + "`")
                .Append(" |\n");
        }
        markdown.Append(CultureInfo.InvariantCulture, $"\n{targets.Count} target(s), {failed} failed. No repository files were modified.\n");
        context.Output.Result("generate", new JsonObject
        {
            ["schema"] = "testwizz.generate-batch/v1",
            ["base"] = since,
            ["total"] = targets.Count,
            ["failed"] = failed,
            ["minRisk"] = minRisk,
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
