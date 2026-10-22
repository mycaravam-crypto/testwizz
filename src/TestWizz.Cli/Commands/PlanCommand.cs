using TestWizz.AI;
using TestWizz.Core.Configuration;
using TestWizz.Core.Context;
using TestWizz.Core.Planning;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

public static class PlanCommand
{
    private const string AiOption = "--ai";

    public static CommandDefinition Definition { get; } = new(
        "plan",
        "Plan evidence-linked test scenarios for a C# method (testwizz.plan/v1). Add --ai to let the local model propose more.",
        "testwizz plan [path] --for <target> [--ai] [--max-chars <n>]",
        Options:
        [
            .. ContextCommand.TargetOptions,
            new(AiOption, $"Ask the model configured in {TestWizzConfig.FileName} for additional scenarios (all marked for review)."),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var document = ContextCommand.Load(context, Definition.Usage);
        var extractor = new CSharpScenarioExtractor();
        ExtractionResult extraction;
        try
        {
            extraction = extractor.Extract(document);
        }
        catch (ContextException ex)
        {
            throw ContextCommand.ToCliException(ex);
        }

        Enrichment? enrichment = null;
        if (context.Has(AiOption))
        {
            var settings = ConfigCommand.Load(Path.Combine(context.PathArgument(), TestWizzConfig.FileName)).Ai;
            try
            {
                using var client = ModelClient.Create(settings, context.GetEnvironmentVariable);
                enrichment = ScenarioEnricher.EnrichAsync(client, document, extraction, context.CancellationToken).GetAwaiter().GetResult();
            }
            catch (ModelClientException ex)
            {
                throw new CliException(
                    $"ai.{ex.Code}",
                    $"{ex.Message} Run 'testwizz ai check' for details, or plan without --ai.",
                    ex.IsConfigurationError ? ExitCodes.Error : ExitCodes.ValidationFailed);
            }
        }

        var plan = ScenarioPlanner.Create(document, extractor.Version, extraction, enrichment);
        context.Output.Result("plan", PlanJson.ToJson(plan), PlanJson.ToMarkdown(plan));
        return ExitCodes.Success;
    }
}
