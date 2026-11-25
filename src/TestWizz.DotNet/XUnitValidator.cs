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
/// The validation compilation carries the test project's global usings, so reduced (<c>value.Ext()</c>) and static
/// (<c>Extensions.Ext(value)</c>) extension calls bind exactly as in the real build, including overloads, generic
/// inference, optional and named parameters and receiver conversions. A call that only fails because the extension's
/// namespace is not imported is reported as <c>missing-using</c>; an incompatible receiver as <c>receiver-mismatch</c>.
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
        ["CS0272"] = "read-only-member",
        ["CS0154"] = "read-only-member",
        ["CS0271"] = "inaccessible-member",
        ["CS1929"] = "receiver-mismatch",
        ["CS1503"] = "argument-mismatch",
    };

    public static (IReadOnlyList<GeneratedTest> Tests, IReadOnlyList<GenerationFinding> Findings) Validate(
        RoslynContextProvider provider,
        string code,
        string path,
        XUnitConventions conventions,
        IReadOnlyCollection<string> scenarioIds,
        bool internalsVisible = true)
    {
        var findings = new List<GenerationFinding>();
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions, path: path);
        var root = tree.GetRoot();

        foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(MaxSyntaxErrors))
        {
            findings.Add(GenerationFinding.Error("syntax-error", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture), Line(error.Location)));
        }

        if (ModelSource.StrayBackslashLines(code) is { Count: > 0 } stray)
        {
            findings.Add(GenerationFinding.Error("malformed-source",
                $"Backslashes outside string literals on line(s) {string.Join(", ", stray.Take(10))}: the model output looks double-escaped or malformed. It was not changed, because decoding it would not produce valid C#.",
                stray[0]));
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
                findings.Add(GenerationFinding.Error("unknown-scenario",
                    $"{method.Identifier.Text} links to {unknown}, which is not in the plan (valid: {string.Join(", ", scenarioIds.Order(StringComparer.Ordinal))}).", line));
            }

            foreach (var opaque in OpaqueScenarioTraits(method))
            {
                findings.Add(GenerationFinding.Error("unverifiable-scenario-link",
                    $"{method.Identifier.Text} has [Trait(\"Scenario\", {opaque})]; scenario ids must be string literals so the link can be checked.", line));
            }

            tests.Add(new GeneratedTest(method.Identifier.Text, line, linked, IsSkipped(method), Assertions(method)));
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

        findings.AddRange(ApiFindings(provider, tree, conventions, internalsVisible));
        findings.AddRange(EncapsulationBypasses(root));
        return (tests, findings);
    }

    private static IEnumerable<GenerationFinding> ApiFindings(RoslynContextProvider provider, SyntaxTree tree, XUnitConventions conventions, bool internalsVisible)
    {
        // The test project's own <Using> items are global usings of the real build; without them an extension method
        // in an imported namespace looks missing, and for an enum receiver Roslyn then binds System.Enum.GetName(...)
        // and reports a misleading "No overload ... takes 0 arguments" (CS1501).
        var globalUsings = conventions.GlobalUsings
            .Append(conventions.XunitGlobalUsing ? "global using Xunit;" : "")
            .Distinct(StringComparer.Ordinal);
        var support = PackageStubs.For(conventions.MockingLibrary)
            .Select(text => CSharpSyntaxTree.ParseText(text, ParseOptions))
            .Append(GlobalUsingTree(globalUsings));
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
            if (node.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
            {
                continue;
            }

            if (code is "unknown-member" or "signature-mismatch" or "receiver-mismatch"
                && MissingExtensionImport(provider, compilation, model, node) is { } missing)
            {
                if (seen.Add((missing, Line(diagnostic.Location))))
                {
                    yield return GenerationFinding.Error("missing-using", missing, Line(diagnostic.Location));
                }

                continue;
            }

            if (code == "receiver-mismatch"
                ? !IsRepositoryExtensionName(provider, compilation, node)
                : !InvolvesRepositoryType(provider, model, node))
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

        foreach (var finding in GeneratedHelperChecks.Check(provider, compilation, model, tree))
        {
            yield return finding;
        }

        foreach (var finding in AssertionApiFindings(provider, model, tree, conventions))
        {
            yield return finding;
        }

        // The validation compilation is one assembly, so Roslyn never reports internal members as inaccessible.
        if (!internalsVisible)
        {
            foreach (var finding in InternalUses(provider, model, tree, seen))
            {
                yield return finding;
            }
        }
    }

    /// <summary>
    /// <c>Assert.X(...)</c> calls checked by name against the detected xUnit version (#93). <c>Assert</c> that binds to a
    /// type declared in the repository or in the generated file (a custom helper) is not xUnit's and is not checked;
    /// the permissive validation stub of <c>Xunit.Assert</c> does not count as such a declaration.
    /// </summary>
    private static IEnumerable<GenerationFinding> AssertionApiFindings(RoslynContextProvider provider, SemanticModel model, SyntaxTree tree, XUnitConventions conventions)
    {
        if (XUnitAssertApi.MajorVersion(conventions.XunitPackage) is not { } major)
        {
            yield break;
        }

        foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access
                || access.Expression.ToString() is not ("Assert" or "Xunit.Assert" or "global::Xunit.Assert")
                || model.GetSymbolInfo(access.Expression).Symbol is INamedTypeSymbol custom
                    && (provider.IsRepositoryType(custom) || custom.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == tree)))
            {
                continue;
            }

            if (XUnitAssertApi.Problem(access.Name.Identifier.Text, major) is { } problem)
            {
                yield return GenerationFinding.Error("unsupported-assertion",
                    problem + $" (checked by name against {conventions.XunitPackage}; packages are not compiled offline)", Line(invocation.GetLocation()));
            }
        }
    }

    /// <summary>Uses of internal repository members or types, which a test assembly without InternalsVisibleTo cannot see.</summary>
    private static IEnumerable<GenerationFinding> InternalUses(RoslynContextProvider provider, SemanticModel model, SyntaxTree tree, HashSet<(string, int?)> seen)
    {
        foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (model.GetSymbolInfo(name).Symbol is not { } symbol)
            {
                continue;
            }

            var declared = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol.OriginalDefinition;
            var owner = declared as INamedTypeSymbol ?? declared.ContainingType;
            if (!provider.IsRepositoryType(owner) || declared is ILocalSymbol or IParameterSymbol or ITypeParameterSymbol)
            {
                continue;
            }

            var hidden = new[] { declared }.Concat(Containers(declared))
                .FirstOrDefault(s => s.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedAndInternal);
            if (hidden is null)
            {
                continue;
            }

            var message = $"'{hidden.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}' is internal and the test project has no InternalsVisibleTo for it; test through public members.";
            var line = Line(name.GetLocation());
            if (seen.Add((message, line)))
            {
                yield return GenerationFinding.Error("inaccessible-member", message, line);
            }
        }

        static IEnumerable<ISymbol> Containers(ISymbol symbol)
        {
            for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
            {
                yield return type;
            }
        }
    }

    /// <summary>
    /// Reflection and accessor tricks that reach private state instead of testing behavior: <c>BindingFlags.NonPublic</c>,
    /// <c>[UnsafeAccessor]</c>, <c>PrivateObject</c>/<c>PrivateType</c>, <c>Activator.CreateInstance(…, nonPublic: true)</c>
    /// and <c>InternalsVisibleTo</c> declared in the test file itself.
    /// </summary>
    private static IEnumerable<GenerationFinding> EncapsulationBypasses(SyntaxNode root)
    {
        var seen = new HashSet<int>();
        foreach (var node in root.DescendantNodes())
        {
            var reason = node switch
            {
                MemberAccessExpressionSyntax { Name.Identifier.Text: "NonPublic" } access when access.Expression.ToString().EndsWith("BindingFlags", StringComparison.Ordinal)
                    => "BindingFlags.NonPublic reflection",
                AttributeSyntax attribute when attribute.Name.ToString().EndsWith("UnsafeAccessor", StringComparison.Ordinal)
                    => "[UnsafeAccessor]",
                AttributeSyntax attribute when attribute.Name.ToString().EndsWith("InternalsVisibleTo", StringComparison.Ordinal)
                    => "InternalsVisibleTo in the test file",
                IdentifierNameSyntax { Identifier.Text: "PrivateObject" or "PrivateType" } id => id.Identifier.Text,
                ArgumentSyntax { NameColon.Name.Identifier.Text: "nonPublic" } => "Activator.CreateInstance(nonPublic: true)",
                _ => null,
            };
            var line = Line(node.GetLocation());
            if (reason is not null && seen.Add(line))
            {
                yield return GenerationFinding.Error("encapsulation-bypass",
                    $"{reason} reaches non-public members; test the public behavior instead, or report the scenario as untestable.", line);
            }
        }
    }

    /// <summary>
    /// One syntax tree holding only well-formed <c>global using</c> directives. Each directive is parsed on its own and
    /// kept only when it is exactly one global using directive with no other members, attributes, trivia-hidden code
    /// or diagnostics, so repository-derived text can never add declarations to the validation compilation. The tree
    /// is built from the parsed nodes, not by concatenating text. The compilation is only analyzed, never emitted.
    /// </summary>
    internal static SyntaxTree GlobalUsingTree(IEnumerable<string> directives)
    {
        var usings = new List<UsingDirectiveSyntax>();
        foreach (var directive in directives)
        {
            if (string.IsNullOrWhiteSpace(directive) || directive.Length > 512)
            {
                continue;
            }

            var unit = SyntaxFactory.ParseCompilationUnit(directive, options: ParseOptions);
            if (unit.ContainsDiagnostics || unit.Members.Count != 0 || unit.AttributeLists.Count != 0
                || unit.Externs.Count != 0 || unit.Usings is not [{ GlobalKeyword.RawKind: (int)SyntaxKind.GlobalKeyword } single]
                || single.ToFullString().Trim() != directive.Trim()
                || single.DescendantTrivia().Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
            {
                continue;
            }

            usings.Add(single.WithoutTrivia());
        }

        return CSharpSyntaxTree.Create(SyntaxFactory.CompilationUnit().WithUsings(SyntaxFactory.List(usings)).NormalizeWhitespace(),
            ParseOptions, path: "<testwizz-global-usings>");
    }

    /// <summary>
    /// For a failing <c>receiver.Name(...)</c> call: a repository extension method named <c>Name</c> that accepts the
    /// receiver but lives in a namespace the test file does not import. The call is still invalid (the real build fails
    /// too), but the finding names the missing <c>using</c> instead of Roslyn's fallback diagnostic, which for an enum
    /// receiver is the unrelated <c>System.Enum.GetName</c> overload error.
    /// </summary>
    private static string? MissingExtensionImport(RoslynContextProvider provider, Compilation compilation, SemanticModel model, SyntaxNode node)
    {
        if (node.FirstAncestorOrSelf<MemberAccessExpressionSyntax>() is not { Parent: InvocationExpressionSyntax } access
            || model.GetTypeInfo(access.Expression).Type is not { TypeKind: not TypeKind.Error } receiver)
        {
            return null;
        }

        var name = access.Name.Identifier.Text;
        var position = access.SpanStart;
        var visible = model.LookupSymbols(position, receiver, name, includeReducedExtensionMethods: true)
            .OfType<IMethodSymbol>()
            .Select(m => (m.ReducedFrom ?? m).OriginalDefinition)
            .ToHashSet(SymbolEqualityComparer.Default);
        var namespaces = compilation.GetSymbolsWithName(name, SymbolFilter.Member)
            .OfType<IMethodSymbol>()
            .Where(m => m.IsExtensionMethod && provider.IsRepositoryType(m.ContainingType)
                && !visible.Contains(m.OriginalDefinition)
                && model.IsAccessible(position, m)
                && m.ReduceExtensionMethod(receiver) is not null)
            .Select(m => m.ContainingNamespace)
            .Where(ns => ns is { IsGlobalNamespace: false })
            .Select(ns => ns.ToDisplayString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (namespaces.Count == 0)
        {
            return null;
        }

        var receiverName = receiver.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return $"'{name}' is an extension method for {receiverName} in namespace {string.Join(" or ", namespaces.Select(ns => $"'{ns}'"))}, "
            + $"which the test file does not import; add 'using {namespaces[0]};'.";
    }

    /// <summary>
    /// CS1929 is reported on the receiver and exposes no candidate symbols: true when the member accessed on it names a
    /// repository extension method, i.e. the receiver is incompatible with a repository API.
    /// </summary>
    private static bool IsRepositoryExtensionName(RoslynContextProvider provider, Compilation compilation, SyntaxNode receiver) =>
        receiver.FirstAncestorOrSelf<MemberAccessExpressionSyntax>() is { } access
        && compilation.GetSymbolsWithName(access.Name.Identifier.Text, SymbolFilter.Member)
            .OfType<IMethodSymbol>()
            .Any(m => m.IsExtensionMethod && provider.IsRepositoryType(m.ContainingType));

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

    private static bool IsSkipped(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => TestAttributes.Contains(a.Name.ToString(), StringComparer.Ordinal))
            .Any(a => a.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Skip") == true)
        || method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => Callee(i) is ("Assert", { } name) && name.StartsWith("Skip", StringComparison.Ordinal));

    /// <summary>
    /// Classifies the assertions in a test body: xUnit <c>Assert.*</c>, fluent <c>Should*</c> chains, mock verification
    /// (NSubstitute <c>Received</c>, Moq <c>Verify</c>, FakeItEasy <c>MustHaveHappened</c>) and local helpers named
    /// <c>Assert*</c>/<c>Verify*</c>. Assertions on constants and <c>NotImplementedException</c> are placeholders.
    /// </summary>
    private static AssertionSummary Assertions(MethodDeclarationSyntax method) => Analyze(method).Summary;

    /// <summary>xUnit assertions that pin an exact value, exception type or identity; weaker ones only constrain it.</summary>
    private static readonly HashSet<string> StrongAsserts = new(StringComparer.Ordinal)
    {
        "Equal", "StrictEqual", "Equivalent", "Same", "Throws", "ThrowsAsync", "Raises", "RaisesAsync", "Collection", "PropertyChanged",
    };

    /// <summary>Per-test facts the repair rules compare before and after a model repair.</summary>
    /// <param name="Strong">Assertions that pin an exact value, exception type or interaction.</param>
    /// <param name="Texts">Normalized source of every assertion, to spot changed expectations.</param>
    internal sealed record TestShape(string Name, IReadOnlyList<string> Scenarios, bool Skipped, AssertionSummary Assertions, int Strong, IReadOnlyList<string> Texts);

    /// <summary>Test methods of <paramref name="code"/> by name (first declaration wins).</summary>
    internal static IReadOnlyDictionary<string, TestShape> Shapes(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        var shapes = new Dictionary<string, TestShape>(StringComparer.Ordinal);
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTest))
        {
            var analysis = Analyze(method);
            shapes.TryAdd(method.Identifier.Text, new TestShape(
                method.Identifier.Text, [.. ScenarioTraits(method).Order(StringComparer.Ordinal)], IsSkipped(method), analysis.Summary, analysis.Strong, analysis.Texts));
        }

        return shapes;
    }

    private static (AssertionSummary Summary, int Strong, List<string> Texts) Analyze(MethodDeclarationSyntax method)
    {
        int meaningful = 0, weak = 0, strong = 0;
        var texts = new List<string>();
        var placeholders = new SortedSet<int>();
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var (receiver, name) = Callee(invocation);
            if (name is null)
            {
                continue;
            }

            var arguments = invocation.ArgumentList.Arguments.Select(a => a.Expression).ToList();
            if (receiver == "Assert")
            {
                if (name.StartsWith("Skip", StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsPlaceholder(name, arguments))
                {
                    placeholders.Add(Line(invocation.GetLocation()));
                }
                else if (name is "NotNull" || (name is "True" && arguments.Count > 0 && IsNullCheck(arguments[0])))
                {
                    weak++;
                    texts.Add(Normalize(invocation));
                }
                else
                {
                    meaningful++;
                    strong += StrongAsserts.Contains(name) ? 1 : 0;
                    texts.Add(Normalize(invocation));
                }
            }
            else if (name.StartsWith("Should", StringComparison.Ordinal) && !IsInsideShouldChain(invocation))
            {
                var subject = invocation.Expression is MemberAccessExpressionSyntax access ? access.Expression : null;
                var chain = invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>()?.Expression.ToString() ?? "";
                if (subject is not null && IsConstant(subject))
                {
                    placeholders.Add(Line(invocation.GetLocation()));
                }
                else if (chain.EndsWith("NotBeNull()", StringComparison.Ordinal) || name == "ShouldNotBeNull")
                {
                    weak++;
                    texts.Add(chain);
                }
                else
                {
                    meaningful++;
                    strong++;
                    texts.Add(string.Join(' ', chain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                }
            }
            else if (name is "Received" or "DidNotReceive" or "ReceivedWithAnyArgs" or "DidNotReceiveWithAnyArgs"
                     or "Verify" or "VerifyAll" or "VerifyNoOtherCalls"
                     || name.StartsWith("MustHaveHappened", StringComparison.Ordinal) || name.StartsWith("MustNotHaveHappened", StringComparison.Ordinal)
                     || (receiver is null && (name.StartsWith("Assert", StringComparison.Ordinal) || name.StartsWith("Verify", StringComparison.Ordinal))))
            {
                meaningful++;
                strong++;
                texts.Add(Normalize(invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>()?.Expression ?? invocation));
            }
        }

        foreach (var thrown in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                     .Where(c => c.Type.ToString() is "NotImplementedException" or "System.NotImplementedException"
                         && c.Parent is ThrowStatementSyntax or ThrowExpressionSyntax))
        {
            placeholders.Add(Line(thrown.GetLocation()));
        }

        return (new AssertionSummary(meaningful, weak, [.. placeholders]), strong, texts);
    }

    private static string Normalize(SyntaxNode node) => string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsPlaceholder(string name, List<ExpressionSyntax> arguments) => name switch
    {
        "True" => arguments.Count > 0 && arguments[0].IsKind(SyntaxKind.TrueLiteralExpression),
        "False" => arguments.Count > 0 && arguments[0].IsKind(SyntaxKind.FalseLiteralExpression),
        "Null" => arguments.Count > 0 && arguments[0].IsKind(SyntaxKind.NullLiteralExpression),
        "NotNull" => arguments.Count > 0 && (IsConstant(arguments[0]) || arguments[0] is BaseObjectCreationExpressionSyntax),
        "Equal" or "Same" or "StrictEqual" or "Equivalent" => arguments.Count >= 2
            && ((IsConstant(arguments[0]) && IsConstant(arguments[1])) || arguments[0].IsEquivalentTo(arguments[1])),
        _ => false,
    };

    private static bool IsNullCheck(ExpressionSyntax expression) =>
        expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression } binary
            && (binary.Left.IsKind(SyntaxKind.NullLiteralExpression) || binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
        || expression is IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal } } }
            && literal.IsKind(SyntaxKind.NullLiteralExpression);

    private static bool IsConstant(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax
        || expression is PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax }
        || expression is ParenthesizedExpressionSyntax parenthesized && IsConstant(parenthesized.Expression);

    /// <summary>True for <c>Be(1)</c> in <c>x.Should().Be(1)</c>: the chain is counted once, at its <c>Should</c> call.</summary>
    private static bool IsInsideShouldChain(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax { Expression: var inner }
        && inner.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => Callee(i).Name?.StartsWith("Should", StringComparison.Ordinal) == true);

    /// <summary>Receiver identifier (for <c>Assert.Equal</c>) and method name of a call; receiver is null for local calls.</summary>
    private static (string? Receiver, string? Name) Callee(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => (
            access.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Name.Identifier.Text: var last } => last,
                _ => "",
            },
            access.Name.Identifier.Text),
        SimpleNameSyntax simple => (null, simple.Identifier.Text),
        _ => (null, null),
    };

    private static bool IsTest(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Any(a => TestAttributes.Contains(a.Name.ToString(), StringComparer.Ordinal));

    private static IEnumerable<string> ScenarioTraits(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => a.Name.ToString() is "Trait" or "Xunit.Trait" or "TraitAttribute" && a.ArgumentList?.Arguments.Count == 2)
            .Where(a => Literal(a.ArgumentList!.Arguments[0].Expression) == "Scenario")
            .Select(a => Literal(a.ArgumentList!.Arguments[1].Expression))
            .OfType<string>()
            .SelectMany(v => v.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Scenario traits whose value is not a string literal (a constant, nameof, interpolation): not checkable.</summary>
    private static IEnumerable<string> OpaqueScenarioTraits(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => a.Name.ToString() is "Trait" or "Xunit.Trait" or "TraitAttribute" && a.ArgumentList?.Arguments.Count == 2)
            .Where(a => Literal(a.ArgumentList!.Arguments[0].Expression) == "Scenario" && Literal(a.ArgumentList.Arguments[1].Expression) is null)
            .Select(a => a.ArgumentList!.Arguments[1].Expression.ToString());

    private static string? Literal(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;

    private static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;
}
