using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestWizz.Core.Generation;

namespace TestWizz.DotNet;

/// <summary>
/// Static checks on generated xUnit code, without building or running it: C# syntax, the expected test class, a
/// <c>[Trait("Scenario", "Sn")]</c> link on every test, plan coverage, and calls to members or constructors of
/// repository types that do not exist. Package types (xUnit, mocking, assertions) are not resolvable offline, so only
/// repository types are checked; extension methods of the detected mocking and assertion libraries are allowed.
/// </summary>
public static class XUnitValidator
{
    private const int MaxSyntaxErrors = 10;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly string[] TestAttributes = ["Fact", "Theory", "Xunit.Fact", "Xunit.Theory", "FactAttribute", "TheoryAttribute"];

    private static readonly Dictionary<string, string[]> ExtensionMembers = new(StringComparer.Ordinal)
    {
        ["NSubstitute"] =
        [
            "Received", "DidNotReceive", "ReceivedWithAnyArgs", "DidNotReceiveWithAnyArgs", "Returns", "ReturnsForAnyArgs",
            "ReturnsNull", "ReturnsNullForAnyArgs", "Throws", "ThrowsAsync", "ThrowsForAnyArgs", "ThrowsAsyncForAnyArgs",
            "When", "WhenForAnyArgs", "Configure", "ClearReceivedCalls", "ReceivedCalls",
        ],
        ["FakeItEasy"] = [],
        ["Moq"] = [],
    };

    /// <summary>Diagnostics that mean the code uses an API the repository type does not have.</summary>
    private static readonly Dictionary<string, string> ApiDiagnostics = new(StringComparer.Ordinal)
    {
        ["CS1061"] = "unknown-member",
        ["CS0117"] = "unknown-member",
        ["CS1729"] = "constructor-mismatch",
        ["CS7036"] = "signature-mismatch",
        ["CS1501"] = "signature-mismatch",
        ["CS1739"] = "signature-mismatch",
        ["CS0122"] = "inaccessible-member",
        ["CS0200"] = "read-only-member",
    };

    public static (IReadOnlyList<GeneratedTest> Tests, IReadOnlyList<GenerationFinding> Findings) Validate(
        RoslynContextProvider provider,
        string code,
        string path,
        XUnitConventions conventions,
        IReadOnlyCollection<string> scenarioIds)
    {
        var findings = new List<GenerationFinding>();
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions, path: path);
        var root = tree.GetRoot();

        foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(MaxSyntaxErrors))
        {
            findings.Add(GenerationFinding.Error("syntax-error", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture), Line(error.Location)));
        }

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        if (!classes.Any(c => c.Identifier.Text == conventions.ClassName))
        {
            findings.Add(GenerationFinding.Warning("unexpected-class-name", $"Expected a test class named {conventions.ClassName}."));
        }

        var tests = new List<GeneratedTest>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTest))
        {
            var line = Line(method.Identifier.GetLocation());
            var linked = ScenarioTraits(method).ToList();
            if (linked.Count == 0)
            {
                findings.Add(GenerationFinding.Error("unlinked-test", $"{method.Identifier.Text} has no [Trait(\"Scenario\", \"S<n>\")].", line));
            }

            foreach (var unknown in linked.Where(id => !scenarioIds.Contains(id)))
            {
                findings.Add(GenerationFinding.Error("unknown-scenario", $"{method.Identifier.Text} links to {unknown}, which is not in the plan.", line));
            }

            tests.Add(new GeneratedTest(method.Identifier.Text, line, linked));
        }

        if (tests.Count == 0)
        {
            findings.Add(GenerationFinding.Error("no-tests", "The generated code contains no [Fact] or [Theory] methods."));
        }

        var covered = tests.SelectMany(t => t.Scenarios).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in scenarioIds.Where(id => !covered.Contains(id)))
        {
            findings.Add(GenerationFinding.Warning("scenario-not-covered", $"No generated test covers {missing}."));
        }

        findings.AddRange(ApiFindings(provider, tree, conventions));
        return (tests, findings);
    }

    private static IEnumerable<GenerationFinding> ApiFindings(RoslynContextProvider provider, SyntaxTree tree, XUnitConventions conventions)
    {
        var support = PackageStubs.For(conventions.MockingLibrary)
            .Append(conventions.XunitGlobalUsing ? "global using Xunit;" : "")
            .Select(text => CSharpSyntaxTree.ParseText(text, ParseOptions));
        var compilation = provider.Compilation.AddSyntaxTrees(support.Prepend(tree));
        var model = compilation.GetSemanticModel(tree);
        var allowed = new HashSet<string>(
            conventions.MockingLibrary is { } mocking && ExtensionMembers.TryGetValue(mocking, out var members) ? members : [],
            StringComparer.Ordinal);
        var seen = new HashSet<(string, int?)>();

        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (!ApiDiagnostics.TryGetValue(diagnostic.Id, out var code))
            {
                continue;
            }

            var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null || !InvolvesRepositoryType(provider, model, node))
            {
                continue;
            }

            var memberName = (node.FirstAncestorOrSelf<MemberAccessExpressionSyntax>()?.Name ?? node as SimpleNameSyntax)?.Identifier.Text;
            if (memberName is not null && (allowed.Contains(memberName) || (conventions.AssertionLibrary is not null && memberName.StartsWith("Should", StringComparison.Ordinal))))
            {
                continue;
            }

            if (code == "signature-mismatch" && node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is { } call
                && model.GetSymbolInfo(call) is { Symbol: null, CandidateSymbols.IsEmpty: true })
            {
                code = "unknown-member";
            }

            var line = Line(diagnostic.Location);
            if (seen.Add((diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), line)))
            {
                yield return GenerationFinding.Error(code, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), line);
            }
        }
    }

    /// <summary>True when the failing member access, call or construction targets a type declared in the repository.</summary>
    private static bool InvolvesRepositoryType(RoslynContextProvider provider, SemanticModel model, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<BaseObjectCreationExpressionSyntax>() is { } creation
            && (creation is not ImplicitObjectCreationExpressionSyntax || creation.ArgumentList is not null)
            && provider.IsRepositoryType(model.GetTypeInfo(creation).Type))
        {
            return true;
        }

        if (node.FirstAncestorOrSelf<MemberAccessExpressionSyntax>() is { } access)
        {
            var receiver = model.GetTypeInfo(access.Expression).Type
                ?? (model.GetSymbolInfo(access.Expression).Symbol as ITypeSymbol);
            if (provider.IsRepositoryType(receiver))
            {
                return true;
            }
        }

        if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is { } invocation)
        {
            var symbols = model.GetSymbolInfo(invocation);
            var candidate = symbols.Symbol ?? symbols.CandidateSymbols.FirstOrDefault();
            return provider.IsRepositoryType(candidate?.ContainingType);
        }

        return provider.IsRepositoryType(model.GetSymbolInfo(node).Symbol?.ContainingType)
            || provider.IsRepositoryType(model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault()?.ContainingType);
    }

    private static bool IsTest(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Any(a => TestAttributes.Contains(a.Name.ToString(), StringComparer.Ordinal));

    private static IEnumerable<string> ScenarioTraits(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => a.Name.ToString() is "Trait" or "Xunit.Trait" or "TraitAttribute" && a.ArgumentList?.Arguments.Count == 2)
            .Where(a => Literal(a.ArgumentList!.Arguments[0].Expression) == "Scenario")
            .Select(a => Literal(a.ArgumentList!.Arguments[1].Expression))
            .OfType<string>()
            .SelectMany(v => v.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));

    private static string? Literal(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;

    private static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;
}
