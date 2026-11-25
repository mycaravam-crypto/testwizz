using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using TestWizz.Core.Context;

namespace TestWizz.DotNet;

/// <summary>
/// What a test in the test project can and cannot use of the type under test, so the model is guided to public
/// behavior instead of private state (#92). The validation compilation is one loose assembly in which every
/// <c>internal</c> member is visible; here <c>internal</c> counts as accessible only when the code under test grants
/// the test assembly access with <c>InternalsVisibleTo</c> (attribute or project item). Private and protected members,
/// and setters or init accessors that are not accessible, are listed as off limits.
/// </summary>
public static class MemberAccess
{
    private const int MaxLines = 40;

    private static readonly SymbolDisplayFormat Format = new(
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <param name="Accessible">Members a test may call or read, with read-only notes for inaccessible setters.</param>
    /// <param name="Inaccessible">Members a test must not use; it should test through public behavior instead.</param>
    public sealed record Report(string TypeName, bool InternalsVisible, IReadOnlyList<string> Accessible, IReadOnlyList<string> Inaccessible);

    public static Report? Describe(RoslynContextProvider provider, ContextDocument context, XUnitConventions conventions)
    {
        var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(context.Target.Id, provider.Compilation);
        if ((symbol as INamedTypeSymbol ?? symbol?.ContainingType) is not { } type)
        {
            return null;
        }

        var internals = InternalsVisible(provider, context.Project, conventions.TestProject);
        var accessible = new List<string>();
        var inaccessible = new List<string>();
        foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared && m is IFieldSymbol or IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } or IEventSymbol))
        {
            var text = Describe(member);
            if (!IsAccessible(member.DeclaredAccessibility, internals))
            {
                inaccessible.Add($"{Name(member.DeclaredAccessibility)} {text}");
            }
            else if (member is IPropertySymbol property)
            {
                var setter = property.SetMethod;
                var note = setter is null ? "read-only"
                    : !IsAccessible(setter.DeclaredAccessibility, internals) ? $"read-only from tests ({Name(setter.DeclaredAccessibility)} {(setter.IsInitOnly ? "init" : "set")})"
                    : setter.IsInitOnly ? "init-only: set it in an object initializer"
                    : null;
                accessible.Add($"{text}{(note is null ? "" : $" — {note}")}");
            }
            else if (member is IFieldSymbol { IsReadOnly: true } or IFieldSymbol { IsConst: true })
            {
                accessible.Add($"{text} — read-only");
            }
            else
            {
                accessible.Add(text);
            }
        }

        return new Report(type.Name, internals, [.. accessible.Take(MaxLines)], [.. inaccessible.Take(MaxLines)]);
    }

    /// <summary>Prompt lines for the report; empty when the type could not be analyzed.</summary>
    public static IReadOnlyList<string> PromptLines(Report? report)
    {
        if (report is null)
        {
            return [];
        }

        var lines = new List<string>
        {
            $"## What tests can use of {report.TypeName}",
            report.InternalsVisible
                ? "The test project can see internal members (InternalsVisibleTo)."
                : "The test project cannot see internal members (no InternalsVisibleTo for it).",
        };
        lines.AddRange(report.Accessible.Select(a => "- " + a));
        if (report.Inaccessible.Count > 0)
        {
            lines.Add("Not accessible from tests (never use them, nor reflection to reach them; test through the accessible members):");
            lines.AddRange(report.Inaccessible.Select(a => "- " + a));
        }

        return lines;
    }

    private static string Describe(ISymbol member) => member switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } constructor =>
            $"constructor {constructor.ContainingType.Name}({string.Join(", ", constructor.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))})",
        _ => (member.IsStatic ? "static " : "") + member.ToDisplayString(Format),
    };

    private static bool IsAccessible(Accessibility accessibility, bool internals) => accessibility switch
    {
        Accessibility.Public => true,
        Accessibility.Internal or Accessibility.ProtectedOrInternal => internals,
        _ => false,
    };

    private static string Name(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "public",
    };

    /// <summary>
    /// True when the code under test grants the test assembly access: an <c>[assembly: InternalsVisibleTo("…")]</c> in
    /// the repository, or an <c>&lt;InternalsVisibleTo Include="…" /&gt;</c> item in the target's project file
    /// (<c>$(AssemblyName)</c> and <c>$(MSBuildProjectName)</c> are expanded). The test assembly is named after its project file.
    /// </summary>
    internal static bool InternalsVisible(RoslynContextProvider provider, string? targetProject, string? testProject)
    {
        if (testProject is null)
        {
            return false;
        }

        var testAssembly = Path.GetFileNameWithoutExtension(testProject);
        static string AssemblyOf(string friend) => friend.Split(',')[0].Trim();
        if (provider.Compilation.Assembly.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "InternalsVisibleToAttribute" or "InternalsVisibleTo"
                && a.ConstructorArguments is [{ Value: string friend }]
                && AssemblyOf(friend).Equals(testAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Attributes on an unresolvable attribute type are not bound; fall back to the syntax.
        if (provider.Compilation.SyntaxTrees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax>())
                .Any(a => a.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.AttributeListSyntax { Target.Identifier.Text: "assembly" }
                    && a.Name.ToString().EndsWith("InternalsVisibleTo", StringComparison.Ordinal)
                    && a.ArgumentList?.Arguments.FirstOrDefault()?.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax { Token.ValueText: var text }
                    && AssemblyOf(text).Equals(testAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (targetProject is null)
        {
            return false;
        }

        var projectFile = Path.Combine(provider.Root, targetProject.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            return File.Exists(projectFile) && XDocument.Load(projectFile).Descendants()
                .Where(e => e.Name.LocalName == "InternalsVisibleTo")
                .Select(e => (string?)e.Attribute("Include") ?? "")
                .Select(i => i.Replace("$(AssemblyName)", projectName, StringComparison.Ordinal).Replace("$(MSBuildProjectName)", projectName, StringComparison.Ordinal))
                .Any(i => AssemblyOf(i).Equals(testAssembly, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
