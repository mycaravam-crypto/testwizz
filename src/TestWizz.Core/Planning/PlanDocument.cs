using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TestWizz.Core.Context;

namespace TestWizz.Core.Planning;

public enum ScenarioCategory
{
    HappyPath,
    Boundary,
    Negative,
    Exception,
    StateTransition,
    Regression,
}

/// <summary>A fact found in the source or context that a scenario can cite, e.g. a guard clause or a doc comment.</summary>
/// <param name="Kind"><c>throw</c>, <c>guard</c>, <c>comparison</c>, <c>branch</c>, <c>switch-case</c>, <c>return</c>, <c>call</c>, <c>cancellation</c>, <c>doc</c> or <c>related-test</c>.</param>
/// <param name="Line">Line in the source file, or null for evidence not tied to a line.</param>
public sealed record Evidence(string Id, string Kind, int? Line, string Text);

/// <param name="Source"><c>deterministic</c> (derived from code structure) or <c>ai</c> (proposed by the model).</param>
/// <param name="ReviewReason">Why a human must confirm the expectation; null when <see cref="NeedsReview"/> is false.</param>
/// <param name="PossibleDuplicateOf">Existing tests that look like they already cover this scenario (name heuristics).</param>
public sealed record Scenario(
    string Id,
    ScenarioCategory Category,
    string Title,
    string Inputs,
    string ExpectedBehavior,
    IReadOnlyList<string> Evidence,
    string Source,
    bool NeedsReview,
    string? ReviewReason,
    IReadOnlyList<string> PossibleDuplicateOf);

public sealed record PlanFinding(string Code, string Message);

/// <param name="Provider">Null when the plan was made without AI.</param>
public sealed record PlanGenerator(
    string ExtractorVersion,
    string? Provider,
    string? Model,
    string? ModelDigest,
    string? PromptTemplate,
    string? PromptSha256);

/// <summary><c>testwizz.plan/v1</c>: evidence-linked test scenarios for one target, made before any test code.</summary>
public sealed record PlanDocument(
    string Stack,
    string TargetId,
    string TargetSignature,
    string SourceFile,
    string SourceSha256,
    string ContextSha256,
    PlanGenerator Generator,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<Scenario> Scenarios,
    IReadOnlyList<PlanFinding> Findings)
{
    public const string Schema = "testwizz.plan/v1";
}

/// <summary>Deterministic extraction for one stack.</summary>
public interface IScenarioExtractor
{
    string Version { get; }

    ExtractionResult Extract(ContextDocument context);
}

public sealed record ExtractionResult(
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<Scenario> Scenarios,
    IReadOnlyList<PlanFinding> Findings);

public static class ScenarioCategories
{
    private static readonly Dictionary<ScenarioCategory, string> Names = new()
    {
        [ScenarioCategory.HappyPath] = "happy-path",
        [ScenarioCategory.Boundary] = "boundary",
        [ScenarioCategory.Negative] = "negative",
        [ScenarioCategory.Exception] = "exception",
        [ScenarioCategory.StateTransition] = "state-transition",
        [ScenarioCategory.Regression] = "regression",
    };

    public static IReadOnlyCollection<string> All => Names.Values;

    public static string Name(ScenarioCategory category) => Names[category];

    public static bool TryParse(string name, out ScenarioCategory category)
    {
        foreach (var (key, value) in Names)
        {
            if (value == name)
            {
                category = key;
                return true;
            }
        }

        category = default;
        return false;
    }
}

public static class PlanJson
{
    /// <summary>Hash of the compact context JSON, so a plan can be tied to the exact context it was made from.</summary>
    public static string ContextHash(ContextDocument context) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ContextJson.ToJson(context).ToJsonString())));

    public static JsonObject ToJson(PlanDocument plan) => new()
    {
        ["schema"] = PlanDocument.Schema,
        ["stack"] = plan.Stack,
        ["target"] = new JsonObject { ["id"] = plan.TargetId, ["signature"] = plan.TargetSignature },
        ["source"] = new JsonObject { ["file"] = plan.SourceFile, ["sha256"] = plan.SourceSha256 },
        ["contextSha256"] = plan.ContextSha256,
        ["generator"] = new JsonObject
        {
            ["extractorVersion"] = plan.Generator.ExtractorVersion,
            ["provider"] = plan.Generator.Provider,
            ["model"] = plan.Generator.Model,
            ["modelDigest"] = plan.Generator.ModelDigest,
            ["promptTemplate"] = plan.Generator.PromptTemplate,
            ["promptSha256"] = plan.Generator.PromptSha256,
        },
        ["evidence"] = new JsonArray([.. plan.Evidence.Select(e => (JsonNode)new JsonObject
        {
            ["id"] = e.Id,
            ["kind"] = e.Kind,
            ["line"] = e.Line,
            ["text"] = e.Text,
        })]),
        ["scenarios"] = new JsonArray([.. plan.Scenarios.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id,
            ["category"] = ScenarioCategories.Name(s.Category),
            ["title"] = s.Title,
            ["inputs"] = s.Inputs,
            ["expectedBehavior"] = s.ExpectedBehavior,
            ["evidence"] = new JsonArray([.. s.Evidence.Select(e => (JsonNode)e)]),
            ["source"] = s.Source,
            ["needsReview"] = s.NeedsReview,
            ["reviewReason"] = s.ReviewReason,
            ["possibleDuplicateOf"] = new JsonArray([.. s.PossibleDuplicateOf.Select(d => (JsonNode)d)]),
        })]),
        ["findings"] = new JsonArray([.. plan.Findings.Select(f => (JsonNode)new JsonObject { ["code"] = f.Code, ["message"] = f.Message })]),
    };

    public static string ToMarkdown(PlanDocument plan)
    {
        var md = new StringBuilder();
        md.Append("# Test plan: `").Append(plan.TargetSignature).Append("`\n\n");
        md.Append("Source: `").Append(plan.SourceFile).Append("` · generator: ").Append(plan.Generator.Provider is null ? "deterministic" : $"deterministic + {plan.Generator.Provider} `{plan.Generator.Model}`").Append("\n\n");
        md.Append("| ID | Category | Scenario | Expected | Evidence | Review |\n|----|----------|----------|----------|----------|--------|\n");
        foreach (var s in plan.Scenarios)
        {
            var review = s.NeedsReview ? "needs review" : "ok";
            if (s.PossibleDuplicateOf.Count > 0)
            {
                review += $"; maybe covered by {string.Join(", ", s.PossibleDuplicateOf)}";
            }

            md.Append("| ").Append(s.Id)
                .Append(" | ").Append(ScenarioCategories.Name(s.Category))
                .Append(" | ").Append(Cell(s.Title)).Append("<br>").Append(Cell(s.Inputs))
                .Append(" | ").Append(Cell(s.ExpectedBehavior))
                .Append(" | ").Append(string.Join(", ", s.Evidence))
                .Append(" | ").Append(Cell(review)).Append(" |\n");
        }

        md.Append("\n## Evidence\n\n");
        foreach (var e in plan.Evidence)
        {
            md.Append("- **").Append(e.Id).Append("** ").Append(e.Kind).Append(e.Line is { } line ? $" (line {line})" : "").Append(": `").Append(e.Text.Replace("`", "'", StringComparison.Ordinal)).Append("`\n");
        }

        if (plan.Findings.Count > 0)
        {
            md.Append("\n## Findings\n\n");
            foreach (var f in plan.Findings)
            {
                md.Append("- ").Append(f.Code).Append(": ").Append(f.Message).Append('\n');
            }
        }

        return md.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
}
