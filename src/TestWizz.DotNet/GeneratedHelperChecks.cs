using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestWizz.Core.Generation;

namespace TestWizz.DotNet;

/// <summary>
/// Checks helper types the model writes into a test file — fakes such as <c>FakeLogger : ILogger&lt;T&gt;</c> or
/// <c>FakeScopeFactory : IServiceScopeFactory</c> — and the types it passes to the code under test (#94).
/// Findings say how certain they are:
/// <list type="bullet">
/// <item><b>verified</b>: the interface or parameter type resolves in the validation compilation (repository or BCL
/// types), so Roslyn's own diagnostics apply (missing interface members, argument conversions, missing <c>using</c>).</item>
/// <item><b>known contract</b>: a common package interface that is not compiled offline, checked against a fixed list
/// of its members.</item>
/// <item><b>unverified</b> (warning only): any other unresolvable interface; <c>run</c> checks it by compiling.</item>
/// </list>
/// </summary>
internal static class GeneratedHelperChecks
{
    private const string ContractNote = "checked against the interface's known members; the package is not compiled offline";

    /// <summary>Members every implementation must declare, for package interfaces that are not resolvable offline.</summary>
    private static readonly Dictionary<string, (string Package, string[] Members)> KnownContracts = new(StringComparer.Ordinal)
    {
        ["ILogger"] = ("Microsoft.Extensions.Logging", ["Log", "IsEnabled", "BeginScope"]),
        ["ILoggerFactory"] = ("Microsoft.Extensions.Logging", ["CreateLogger", "AddProvider", "Dispose"]),
        ["ILoggerProvider"] = ("Microsoft.Extensions.Logging", ["CreateLogger", "Dispose"]),
        ["IServiceScope"] = ("Microsoft.Extensions.DependencyInjection", ["ServiceProvider", "Dispose"]),
        ["IServiceScopeFactory"] = ("Microsoft.Extensions.DependencyInjection", ["CreateScope"]),
        ["IOptions"] = ("Microsoft.Extensions.Options", ["Value"]),
        ["IOptionsMonitor"] = ("Microsoft.Extensions.Options", ["CurrentValue", "Get", "OnChange"]),
        ["IHttpClientFactory"] = ("Microsoft.Extensions.Http", ["CreateClient"]),
        ["IHostApplicationLifetime"] = ("Microsoft.Extensions.Hosting", ["ApplicationStarted", "ApplicationStopping", "ApplicationStopped", "StopApplication"]),
    };

    /// <summary>Roslyn diagnostics on helper types that are certain because the base type resolved.</summary>
    private static readonly Dictionary<string, string> VerifiedDiagnostics = new(StringComparer.Ordinal)
    {
        ["CS0535"] = "incomplete-fake",
        ["CS0738"] = "incomplete-fake",
        ["CS0534"] = "incomplete-fake",
        ["CS0144"] = "incomplete-fake",
    };

    public static IEnumerable<GenerationFinding> Check(RoslynContextProvider provider, Compilation compilation, SemanticModel model, SyntaxTree tree)
    {
        var seen = new HashSet<(string, int)>();
        IEnumerable<GenerationFinding> Once(IEnumerable<GenerationFinding> findings) =>
            findings.Where(f => seen.Add((f.Message, f.Line ?? 0)));

        var root = tree.GetRoot();
        var diagnostics = model.GetDiagnostics();
        foreach (var finding in Once(Verified(diagnostics)))
        {
            yield return finding;
        }

        foreach (var finding in Once(MissingRepositoryUsings(provider, compilation, diagnostics, root)))
        {
            yield return finding;
        }

        var helpers = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(c => (Syntax: c, Symbol: model.GetDeclaredSymbol(c) as INamedTypeSymbol))
            .Where(h => h.Symbol is not null)
            .ToList();
        foreach (var (syntax, symbol) in helpers)
        {
            foreach (var finding in Once(Contracts(syntax, symbol!)))
            {
                yield return finding;
            }
        }

        foreach (var finding in Once(ArgumentTypes(provider, model, root, tree)))
        {
            yield return finding;
        }
    }

    private static IEnumerable<GenerationFinding> Verified(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (VerifiedDiagnostics.TryGetValue(diagnostic.Id, out var code))
            {
                yield return GenerationFinding.Error(code, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture) + " (verified by compilation)", Line(diagnostic.Location));
            }
        }
    }

    /// <summary>CS0246 for a name that is a repository type in a namespace the test file does not import.</summary>
    private static IEnumerable<GenerationFinding> MissingRepositoryUsings(
        RoslynContextProvider provider, Compilation compilation, IEnumerable<Diagnostic> diagnostics, SyntaxNode root)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(d => d.Id == "CS0246").OrderBy(d => d.Location.SourceSpan.Start))
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var name = (node as SimpleNameSyntax ?? node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault())?.Identifier.Text;
            if (name is null || reported.Contains(name))
            {
                continue;
            }

            var namespaces = compilation.GetSymbolsWithName(name, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .Where(provider.IsRepositoryType)
                .Select(t => t.ContainingNamespace)
                .Where(ns => ns is { IsGlobalNamespace: false })
                .Select(ns => ns.ToDisplayString())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (namespaces.Count > 0 && reported.Add(name))
            {
                yield return GenerationFinding.Error("missing-using",
                    $"'{name}' is a repository type in namespace {string.Join(" or ", namespaces.Select(n => $"'{n}'"))}, which the test file does not import; add 'using {namespaces[0]};' (verified by compilation).",
                    Line(diagnostic.Location));
            }
        }
    }

    /// <summary>Generated classes implementing known package interfaces must declare all of their members.</summary>
    private static IEnumerable<GenerationFinding> Contracts(ClassDeclarationSyntax syntax, INamedTypeSymbol symbol)
    {
        var declared = Declared(symbol);
        foreach (var contract in symbol.AllInterfaces.Where(i => i.TypeKind == TypeKind.Error).Concat(BaseListErrors(syntax, symbol)).DistinctBy(i => i.Name))
        {
            if (KnownContracts.TryGetValue(contract.Name, out var known))
            {
                var missing = known.Members.Where(m => !declared.Contains(m)).ToList();
                if (missing.Count > 0)
                {
                    yield return GenerationFinding.Error("incomplete-fake",
                        $"{symbol.Name} implements {known.Package}.{contract.Name} but does not declare {string.Join(", ", missing)} ({ContractNote}).",
                        Line(syntax.Identifier.GetLocation()));
                }
            }
            else
            {
                yield return GenerationFinding.Warning("unverified-fake",
                    $"{symbol.Name} implements {contract.Name}, which could not be resolved offline; its members are only checked when 'run' compiles the test.",
                    Line(syntax.Identifier.GetLocation()));
            }
        }
    }

    /// <summary>Unresolved types named in the base list (Roslyn may not keep error types in <c>AllInterfaces</c>).</summary>
    private static IEnumerable<INamedTypeSymbol> BaseListErrors(ClassDeclarationSyntax syntax, INamedTypeSymbol symbol) =>
        symbol.Interfaces.Concat(symbol.BaseType is { TypeKind: TypeKind.Error } baseType ? [baseType] : [])
            .Where(t => t.TypeKind == TypeKind.Error && syntax.BaseList is not null);

    /// <summary>Member names a class and its generated base classes declare, explicit implementations included.</summary>
    private static HashSet<string> Declared(INamedTypeSymbol symbol)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var type = symbol; type is not null && type.TypeKind != TypeKind.Error && !type.DeclaringSyntaxReferences.IsEmpty; type = type.BaseType)
        {
            foreach (var member in type.GetMembers())
            {
                var name = member.Name;
                names.Add(name[(name.LastIndexOf('.') + 1)..]);
            }
        }

        return names;
    }

    /// <summary>
    /// A generated helper passed to a repository constructor or method where the parameter's type is not resolvable
    /// offline (e.g. <c>IServiceScopeFactory</c>) must at least name that type in its base list. Resolvable parameter
    /// types are checked by Roslyn itself (CS1503).
    /// </summary>
    private static IEnumerable<GenerationFinding> ArgumentTypes(RoslynContextProvider provider, SemanticModel model, SyntaxNode root, SyntaxTree tree)
    {
        foreach (var call in root.DescendantNodes().Where(n => n is BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax))
        {
            // A helper that does not convert to an unresolved parameter type makes overload resolution fail without
            // a diagnostic; a single remaining candidate is still the method that was meant.
            var info = model.GetSymbolInfo(call);
            if ((info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) is not IMethodSymbol method
                || !provider.IsRepositoryType(method.ContainingType))
            {
                continue;
            }

            var arguments = call switch
            {
                BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments,
                InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
                _ => null,
            };
            foreach (var (argument, index) in (arguments ?? default).Select((a, i) => (a, i)))
            {
                var parameter = argument.NameColon is { } named
                    ? method.Parameters.FirstOrDefault(p => p.Name == named.Name.Identifier.Text)
                    : index < method.Parameters.Length ? method.Parameters[index] : method.Parameters.LastOrDefault(p => p.IsParams);
                if (parameter?.Type is not INamedTypeSymbol { TypeKind: TypeKind.Error } expected
                    || model.GetTypeInfo(argument.Expression).Type is not INamedTypeSymbol actual
                    || !actual.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == tree))
                {
                    continue;
                }

                if (!Names(actual).Contains(expected.Name))
                {
                    yield return GenerationFinding.Error("argument-type-mismatch",
                        $"{actual.Name} is passed as '{parameter.Name}' of {method.ContainingType.Name}.{(method.MethodKind == MethodKind.Constructor ? "ctor" : method.Name)}, which expects {expected.Name}, but {actual.Name} does not implement {expected.Name}.",
                        Line(argument.GetLocation()));
                }
            }
        }
    }

    /// <summary>Simple names of a type, its base types and interfaces, resolved or not.</summary>
    private static HashSet<string> Names(INamedTypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            names.Add(current.Name);
            foreach (var implemented in current.AllInterfaces.Concat(current.Interfaces))
            {
                names.Add(implemented.Name);
            }
        }

        return names;
    }

    private static int Line(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;
}
