using System.Text.Json.Nodes;

namespace TestWizz.Core.Generation;

/// <param name="Path">Repository-relative path the file is proposed for. Chosen by TestWizz, never by the model.</param>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>A generated test and the plan scenarios it claims to cover.</summary>
public sealed record GeneratedTest(string Name, int Line, IReadOnlyList<string> Scenarios);

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
        ["tests"] = new JsonArray([.. r.Tests.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["line"] = t.Line,
            ["scenarios"] = new JsonArray([.. t.Scenarios.Select(s => (JsonNode)s)]),
        })]),
        ["assumptions"] = new JsonArray([.. r.Assumptions.Select(a => (JsonNode)a)]),
        ["findings"] = new JsonArray([.. r.Findings.Select(f => (JsonNode)new JsonObject
        {
            ["severity"] = f.Severity,
            ["code"] = f.Code,
            ["message"] = f.Message,
            ["line"] = f.Line,
        })]),
    };
}
