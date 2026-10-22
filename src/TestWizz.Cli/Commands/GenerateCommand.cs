using System.Globalization;
using System.Text;
using TestWizz.AI;
using TestWizz.Core.Configuration;
using TestWizz.Core.Context;
using TestWizz.Core.Generation;
using TestWizz.Core.Planning;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

public static class GenerateCommand
{
    private const string AiPlanOption = "--ai-plan";

    public static CommandDefinition Definition { get; } = new(
        "generate",
        "Plan scenarios and let the local model write xUnit tests for a C# method. Prints the proposal; nothing is written or run.",
        "testwizz generate [path] --for <method> [--ai-plan] [--max-chars <n>]",
        Options:
        [
            .. ContextCommand.TargetOptions,
            new(AiPlanOption, "Also let the model propose extra scenarios before writing tests (same as plan --ai)."),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var root = context.PathArgument();
        var settings = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName)).Ai;
        var document = ContextCommand.Load(context, Definition.Usage);
        var provider = RoslynContextProvider.Load(root);
        var extractor = new CSharpScenarioExtractor();

        GenerationResult result;
        try
        {
            var extraction = extractor.Extract(document);
            using var client = ModelClient.Create(settings, context.GetEnvironmentVariable);
            var enrichment = context.Has(AiPlanOption)
                ? ScenarioEnricher.EnrichAsync(client, document, extraction, context.CancellationToken).GetAwaiter().GetResult()
                : null;
            var plan = ScenarioPlanner.Create(document, extractor.Version, extraction, enrichment);
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

        context.Output.Result("generate", GenerationJson.ToJson(result), ToText(result), result.IsValid ? "ok" : "failed");
        return result.IsValid ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static string ToText(GenerationResult r)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Proposed tests for {r.TargetSignature}\n");
        text.Append(CultureInfo.InvariantCulture, $"Model: {r.Generator.Provider} {r.Generator.Model} · prompt {r.Generator.PromptTemplate}\n");
        text.Append(CultureInfo.InvariantCulture, $"Test project: {(string?)r.Conventions["testProject"] ?? "(none found)"} · mocking: {(string?)r.Conventions["mocking"] ?? "none"} · assertions: {(string?)r.Conventions["assertions"]}\n");
        foreach (var file in r.Files)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n--- {file.Path} ---\n{file.Content}");
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
        text.Append(". Nothing was written to disk and nothing was built or run.\n");
        return text.ToString();
    }
}
