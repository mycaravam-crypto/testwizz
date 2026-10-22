using System.Globalization;
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
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var settings = config.Ai;
        var output = context.Options.TryGetValue(OutOption, out var outText) && !string.IsNullOrWhiteSpace(outText)
            ? Path.GetFullPath(outText, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        try
        {
            // Checked again when writing; failing here avoids a slow model call that could not be saved.
            ProposalBundle.EnsureOutsideRepository(root, output);
        }
        catch (ProposalException ex)
        {
            throw new CliException(ex.Code, ex.Message);
        }

        if (context.Has(SinceOption))
        {
            var since = context.Options[SinceOption];
            IReadOnlyList<string> targets;
            try
            {
                targets = ChangedCode.Methods(root, since ?? "");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new CliException("git.diff", ex.Message);
            }

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
                var selected = context.Options.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                {
                    ["--for"] = targets[0],
                };
                context = context with { Options = selected };
            }
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
