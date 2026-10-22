using System.Text.Json.Nodes;

namespace TestWizz.Core.Context;

/// <summary>
/// <c>testwizz.context/v1</c>: everything TestWizz knows about one test target, shared by all stacks and providers.
/// Values a provider cannot determine are listed in <see cref="Unavailable"/> instead of being guessed.
/// </summary>
public sealed record ContextDocument(
    string Stack,
    Provenance Provenance,
    string? Revision,
    TargetInfo Target,
    SourceInfo Source,
    string? Project,
    IReadOnlyList<MemberInfo> Members,
    IReadOnlyList<MemberInfo> Constructors,
    IReadOnlyList<DependencyInfo> Dependencies,
    IReadOnlyList<ThrowInfo> Throws,
    IReadOnlyList<RelatedTestInfo> RelatedTests,
    IReadOnlyList<UnavailableField> Unavailable,
    IReadOnlyList<ContextDiagnostic> Diagnostics)
{
    public const string Schema = "testwizz.context/v1";
}

public sealed record Provenance(string Provider, string ProviderVersion, string ToolVersion);

/// <param name="Kind"><c>method</c> or <c>type</c>.</param>
/// <param name="Id">Stable symbol id (for .NET the documentation comment id, e.g. <c>M:Shop.Calculator.Add(System.Int32,System.Int32)</c>).</param>
public sealed record TargetInfo(
    string Kind,
    string Id,
    string Name,
    string? ContainingType,
    string? Namespace,
    string Signature,
    string Accessibility,
    IReadOnlyList<string> Modifiers,
    string? ReturnType,
    IReadOnlyList<ParameterInfo> Parameters);

public sealed record ParameterInfo(string Name, string Type, string? Modifier, string? DefaultValue);

/// <param name="File">Repository-relative path with forward slashes.</param>
/// <param name="Sha256">Hash of the whole file, used later to detect stale proposals.</param>
public sealed record SourceInfo(
    string File,
    int StartLine,
    int EndLine,
    string Sha256,
    string Excerpt,
    bool Truncated,
    string? Documentation);

public sealed record MemberInfo(string Id, string Signature, string File, int Line);

/// <param name="Role"><c>constructor</c> (injected; a mock candidate), <c>parameter</c> or <c>return</c>.</param>
/// <param name="DeclaredIn">Location in this repository, or null for external types.</param>
/// <param name="Members">Member signatures (no bodies) when declared in this repository.</param>
public sealed record DependencyInfo(
    string Role,
    string ParameterName,
    string Type,
    string? Kind,
    SourceLocation? DeclaredIn,
    IReadOnlyList<string> Members);

public sealed record SourceLocation(string File, int Line);

public sealed record ThrowInfo(string Type, int Line, string Statement);

/// <summary>A test that mentions the target. This is a relationship found by name, <b>not</b> measured coverage.</summary>
public sealed record RelatedTestInfo(string Name, string File, int Line, string MatchedOn);

public sealed record UnavailableField(string Field, string Reason);

public sealed record ContextDiagnostic(string Code, string Message);

/// <summary>The target could not be resolved. <see cref="Candidates"/> lists ids the user could pass instead.</summary>
public sealed class ContextException(string code, string message, IReadOnlyList<string>? candidates = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyList<string> Candidates { get; } = candidates ?? [];
}

public static class ContextJson
{
    public static JsonObject ToJson(ContextDocument c) => new()
    {
        ["schema"] = ContextDocument.Schema,
        ["stack"] = c.Stack,
        ["provenance"] = new JsonObject
        {
            ["provider"] = c.Provenance.Provider,
            ["providerVersion"] = c.Provenance.ProviderVersion,
            ["toolVersion"] = c.Provenance.ToolVersion,
        },
        ["revision"] = c.Revision,
        ["target"] = new JsonObject
        {
            ["kind"] = c.Target.Kind,
            ["id"] = c.Target.Id,
            ["name"] = c.Target.Name,
            ["containingType"] = c.Target.ContainingType,
            ["namespace"] = c.Target.Namespace,
            ["signature"] = c.Target.Signature,
            ["accessibility"] = c.Target.Accessibility,
            ["modifiers"] = Array(c.Target.Modifiers, m => m),
            ["returnType"] = c.Target.ReturnType,
            ["parameters"] = Array(c.Target.Parameters, p => new JsonObject
            {
                ["name"] = p.Name,
                ["type"] = p.Type,
                ["modifier"] = p.Modifier,
                ["defaultValue"] = p.DefaultValue,
            }),
        },
        ["source"] = new JsonObject
        {
            ["file"] = c.Source.File,
            ["startLine"] = c.Source.StartLine,
            ["endLine"] = c.Source.EndLine,
            ["sha256"] = c.Source.Sha256,
            ["truncated"] = c.Source.Truncated,
            ["documentation"] = c.Source.Documentation,
            ["excerpt"] = c.Source.Excerpt,
        },
        ["project"] = c.Project,
        ["members"] = Array(c.Members, Member),
        ["constructors"] = Array(c.Constructors, Member),
        ["dependencies"] = Array(c.Dependencies, d => new JsonObject
        {
            ["role"] = d.Role,
            ["parameterName"] = d.ParameterName,
            ["type"] = d.Type,
            ["kind"] = d.Kind,
            ["declaredIn"] = d.DeclaredIn is { } l ? new JsonObject { ["file"] = l.File, ["line"] = l.Line } : null,
            ["members"] = Array(d.Members, m => m),
        }),
        ["throws"] = Array(c.Throws, t => new JsonObject
        {
            ["type"] = t.Type,
            ["line"] = t.Line,
            ["statement"] = t.Statement,
        }),
        ["relatedTests"] = Array(c.RelatedTests, t => new JsonObject
        {
            ["name"] = t.Name,
            ["file"] = t.File,
            ["line"] = t.Line,
            ["matchedOn"] = t.MatchedOn,
            ["relationship"] = "name-reference (not coverage)",
        }),
        ["unavailable"] = Array(c.Unavailable, u => new JsonObject { ["field"] = u.Field, ["reason"] = u.Reason }),
        ["diagnostics"] = Array(c.Diagnostics, d => new JsonObject { ["code"] = d.Code, ["message"] = d.Message }),
    };

    private static JsonObject Member(MemberInfo m) => new()
    {
        ["id"] = m.Id,
        ["signature"] = m.Signature,
        ["file"] = m.File,
        ["line"] = m.Line,
    };

    private static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonNode?> map)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(map(item));
        }

        return array;
    }
}
