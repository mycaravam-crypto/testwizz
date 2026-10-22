using System.Text.Json.Nodes;

namespace TestWizz.Core.Generation;

/// <param name="Path">Repository-relative path the file is proposed for. Chosen by TestWizz, never by the model.</param>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>A generated test, the plan scenarios it claims to cover and what the stack's analyzer found in its body.</summary>
/// <param name="Skipped">The test is skipped (attribute or runtime skip).</param>
/// <param name="Assertions">Assertions found in the test, classified by the stack's analyzer.</param>
public sealed record GeneratedTest(string Name, int Line, IReadOnlyList<string> Scenarios, bool Skipped, AssertionSummary Assertions);

/// <param name="Meaningful">Assertions that check a computed value, an exception or an interaction.</param>
/// <param name="Weak">Assertions that only check for null or non-null.</param>
/// <param name="PlaceholderLines">Lines with assertions that can never fail (e.g. <c>Assert.True(true)</c>) or unfinished bodies.</param>
public sealed record AssertionSummary(int Meaningful, int Weak, IReadOnlyList<int> PlaceholderLines)
{
    public static AssertionSummary None { get; } = new(0, 0, []);
}

/// <param name="Severity"><c>error</c> makes the result invalid (exit code 1); <c>warning</c> needs attention.</param>
public sealed record GenerationFinding(string Severity, string Code, string Message, int? Line = null)
{
    public static GenerationFinding Error(string code, string message, int? line = null) => new("error", code, message, line);

    public static GenerationFinding Warning(string code, string message, int? line = null) => new("warning", code, message, line);
}

public sealed record GenerationProvenance(
    string Provider,
    string Model,
    string? ModelDigest,
    string PromptTemplate,
    string PromptSha256);

/// <summary><c>testwizz.generation/v1</c>: proposed test source for one target plus everything needed to review it.</summary>
/// <param name="Conventions">Stack-specific convention report (test project, frameworks, naming).</param>
public sealed record GenerationResult(
    string Stack,
    string TargetId,
    string TargetSignature,
    string ContextSha256,
    GenerationProvenance Generator,
    JsonObject Conventions,
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<GeneratedTest> Tests,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<GenerationFinding> Findings)
{
    public const string Schema = "testwizz.generation/v1";

    public bool IsValid => Findings.All(f => f.Severity != "error");
}

public static class GenerationJson
{
    public static JsonObject ToJson(GenerationResult r) => new()
    {
        ["schema"] = GenerationResult.Schema,
        ["stack"] = r.Stack,
        ["target"] = new JsonObject { ["id"] = r.TargetId, ["signature"] = r.TargetSignature },
        ["contextSha256"] = r.ContextSha256,
        ["valid"] = r.IsValid,
        ["generator"] = new JsonObject
        {
            ["provider"] = r.Generator.Provider,
            ["model"] = r.Generator.Model,
            ["modelDigest"] = r.Generator.ModelDigest,
            ["promptTemplate"] = r.Generator.PromptTemplate,
            ["promptSha256"] = r.Generator.PromptSha256,
        },
        ["conventions"] = r.Conventions.DeepClone(),
        ["files"] = new JsonArray([.. r.Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["content"] = f.Content })]),
        ["tests"] = new JsonArray([.. r.Tests.Select(t => (JsonNode)ToJson(t))]),
        ["assumptions"] = new JsonArray([.. r.Assumptions.Select(a => (JsonNode)a)]),
        ["findings"] = new JsonArray([.. r.Findings.Select(f => (JsonNode)ToJson(f))]),
    };

    public static JsonObject ToJson(GeneratedTest t) => new()
    {
        ["name"] = t.Name,
        ["line"] = t.Line,
        ["scenarios"] = new JsonArray([.. t.Scenarios.Select(s => (JsonNode)s)]),
        ["skipped"] = t.Skipped,
        ["assertions"] = new JsonObject
        {
            ["meaningful"] = t.Assertions.Meaningful,
            ["weak"] = t.Assertions.Weak,
            ["placeholderLines"] = new JsonArray([.. t.Assertions.PlaceholderLines.Select(l => (JsonNode)l)]),
        },
    };

    public static JsonObject ToJson(GenerationFinding f) => new()
    {
        ["severity"] = f.Severity,
        ["code"] = f.Code,
        ["message"] = f.Message,
        ["line"] = f.Line,
    };
}
