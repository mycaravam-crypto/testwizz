using TestWizz.Core.Context;

namespace TestWizz.Core.Planning;

/// <summary>AI-proposed scenarios that already passed validation, plus what produced them.</summary>
public sealed record Enrichment(
    IReadOnlyList<Scenario> Scenarios,
    IReadOnlyList<PlanFinding> Findings,
    string Provider,
    string Model,
    string? ModelDigest,
    string PromptTemplate,
    string PromptSha256);

public static class ScenarioPlanner
{
    /// <summary>Combines deterministic and (optional) AI scenarios into one plan. Scenario ids are renumbered S1..Sn.</summary>
    public static PlanDocument Create(ContextDocument context, string extractorVersion, ExtractionResult extraction, Enrichment? enrichment = null)
    {
        var scenarios = extraction.Scenarios.Concat(enrichment?.Scenarios ?? [])
            .Select((s, i) => s with { Id = $"S{i + 1}" })
            .ToList();

        return new PlanDocument(
            context.Stack,
            context.Target.Id,
            context.Target.Signature,
            context.Source.File,
            context.Source.Sha256,
            PlanJson.ContextHash(context),
            new PlanGenerator(
                extractorVersion,
                enrichment?.Provider,
                enrichment?.Model,
                enrichment?.ModelDigest,
                enrichment?.PromptTemplate,
                enrichment?.PromptSha256),
            extraction.Evidence,
            scenarios,
            [.. extraction.Findings, .. enrichment?.Findings ?? []]);
    }
}
