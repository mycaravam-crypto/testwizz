using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestWizz.Core.Context;

namespace TestWizz.DotNet;

/// <summary>
/// Builds <c>testwizz.context/v1</c> for a C# type or method without MSBuild: all repository <c>.cs</c> files are parsed
/// into one loose compilation against the runtime's framework assemblies. Types from NuGet packages stay unresolved and
/// are reported as diagnostics, which is enough for signatures, dependencies and thrown exceptions.
/// </summary>
public sealed class RoslynContextProvider
{
    public const string ProviderName = "roslyn";
    public const int DefaultMaxExcerptChars = 20_000;
    private const int MaxRelatedTests = 50;
    private const int MaxDependencyMembers = 30;

    /// <summary>The SDK's default implicit usings, which only exist in <c>obj/</c> after a build.</summary>
    private static readonly SyntaxTree ImplicitUsings = CSharpSyntaxTree.ParseText(
        string.Concat(new[] { "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks" }
            .Select(ns => $"global using global::{ns};\n")),
        new CSharpParseOptions(LanguageVersion.Preview),
        path: "<implicit-usings>");

    private static readonly string[] TestAttributes = ["Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod"];

    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly Lazy<MetadataReference[]> FrameworkReferences = new(() =>
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Order(StringComparer.Ordinal)
            .Select(p => MetadataReference.CreateFromFile(p))]);

    private readonly string _root;
    private readonly Compilation _compilation;
    private readonly IReadOnlyList<SourceFile> _files;
    private readonly List<ContextDiagnostic> _loadDiagnostics = [];

    private RoslynContextProvider(string root, Func<string, bool>? includeFile, CancellationToken cancellationToken)
    {
        _root = root;
        var files = new List<SourceFile>();
        foreach (var relative in RepositoryFiles.CSharpFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includeFile is not null && !includeFile(relative)) continue;
            var full = Path.Combine(root, relative);
            var info = new FileInfo(full);
            if (info.Length > RepositoryFiles.MaxFileBytes)
            {
                _loadDiagnostics.Add(new("file-skipped", $"{relative} is larger than {RepositoryFiles.MaxFileBytes} bytes and was skipped."));
                continue;
            }

            var bytes = File.ReadAllBytes(full);
            var tree = CSharpSyntaxTree.ParseText(
                Encoding.UTF8.GetString(bytes),
                new CSharpParseOptions(LanguageVersion.Preview),
                path: relative, cancellationToken: cancellationToken);
            files.Add(new SourceFile(relative, tree, Convert.ToHexStringLower(SHA256.HashData(bytes)), IsTestFile(tree)));
        }

        _files = files;
        _compilation = CSharpCompilation.Create(
            "TestWizzContext",
            files.Select(f => f.Tree).Append(ImplicitUsings),
            FrameworkReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    public static RoslynContextProvider Load(string root) => Load(root, CancellationToken.None);

    public static RoslynContextProvider Load(string root, CancellationToken cancellationToken) => new(Path.GetFullPath(root), null, cancellationToken);

    /// <summary>Load only source files selected before parsing or compilation.</summary>
    public static RoslynContextProvider Load(string root, Func<string, bool> includeFile, CancellationToken cancellationToken) =>
        new(Path.GetFullPath(root), includeFile, cancellationToken);

    /// <summary>Discover eligible named C# methods, excluding test files and declarations without bodies.</summary>
    /// <remarks>Documentation IDs are stable, overload-safe targets for GetContext.</remarks>
    public IReadOnlyList<string> DiscoverMethods() => DiscoverMethods(CancellationToken.None);

    public IReadOnlyList<string> DiscoverMethods(CancellationToken cancellationToken) =>
        DiscoverMethods(null, cancellationToken);

    /// <summary>Resolve only testable methods rather than semantically examining every type and member.</summary>
    public IReadOnlyList<string> DiscoverMethods(Action<int, int>? progress, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sourceFiles = _files.Where(file => !file.IsTest).ToArray();
        var processed = 0;
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var methods = file.Tree.GetRoot(cancellationToken).DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => (method.Body is not null || method.ExpressionBody is not null)
                    && !method.Modifiers.Any(SyntaxKind.AbstractKeyword)
                    && !method.Modifiers.Any(SyntaxKind.PrivateKeyword)
                    && (method.Modifiers.Any(SyntaxKind.PublicKeyword)
                        || method.Modifiers.Any(SyntaxKind.InternalKeyword)
                        || method.Modifiers.Any(SyntaxKind.ProtectedKeyword)
                            && method.Modifiers.Any(SyntaxKind.InternalKeyword)))
                .ToArray();
            if (methods.Length > 0)
            {
                var model = _compilation.GetSemanticModel(file.Tree);
                foreach (var method in methods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (model.GetDeclaredSymbol(method, cancellationToken) is IMethodSymbol symbol
                        && symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal
                        && symbol.GetDocumentationCommentId() is { } id
                        && id.StartsWith("M:", StringComparison.Ordinal))
                        ids.Add(id);
                }
            }
            progress?.Invoke(++processed, sourceFiles.Length);
        }
        return [.. ids.OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>Full path of the repository root the provider was loaded from.</summary>
    public string Root => _root;

    /// <summary>The repository compilation, for checking generated code against the real API.</summary>
    internal Compilation Compilation => _compilation;

    internal bool IsRepositoryType(ITypeSymbol? type) =>
        type is not null && type.OriginalDefinition.DeclaringSyntaxReferences.Any(r => _files.Any(f => f.Tree == r.SyntaxTree));

    public ContextDocument GetContext(string target, string toolVersion, int maxExcerptChars = DefaultMaxExcerptChars)
    {
        var match = Resolve(target);
        var model = _compilation.GetSemanticModel(match.File.Tree);
        var symbol = model.GetDeclaredSymbol(match.Node) ?? throw new ContextException("target.not_found", $"Could not analyze '{target}'.");
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        var typeDeclaration = match.Node as TypeDeclarationSyntax ?? match.Node.Ancestors().OfType<TypeDeclarationSyntax>().First();
        var diagnostics = new List<ContextDiagnostic>(_loadDiagnostics);

        var dependencies = new List<DependencyInfo>();
        foreach (var (name, parameterType) in ConstructorParameters(type, typeDeclaration))
        {
            dependencies.Add(Dependency("constructor", name, parameterType, diagnostics));
        }

        if (symbol is IMethodSymbol method)
        {
            foreach (var parameter in method.Parameters)
            {
                foreach (var named in RepositoryTypes(parameter.Type))
                {
                    dependencies.Add(Dependency("parameter", parameter.Name, named, diagnostics));
                }
            }

            foreach (var named in RepositoryTypes(method.ReturnType))
            {
                dependencies.Add(Dependency("return", "", named, diagnostics));
            }
        }

        foreach (var unresolved in TargetTypes(symbol).Where(t => t.TypeKind == TypeKind.Error).Select(Display).Distinct(StringComparer.Ordinal))
        {
            diagnostics.Add(new("unresolved-type", $"Type '{unresolved}' could not be resolved (external package?)."));
        }

        return new ContextDocument(
            Stack: "dotnet",
            new Provenance(ProviderName, typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown", toolVersion),
            Revision: RepositoryFiles.Revision(_root),
            Target(symbol, match.Node),
            Source(match.File, match.Node, maxExcerptChars),
            Project(match.File.Path),
            Members: symbol is INamedTypeSymbol ? Methods(typeDeclaration, model) : [],
            Constructors: Constructors(typeDeclaration, model),
            Dependencies: [.. dependencies.DistinctBy(d => (d.Role, d.ParameterName, d.Type))],
            Throws: Throws(match.Node, model),
            RelatedTests: RelatedTests(symbol, type),
            Unavailable:
            [
                new("requirements", "No requirement source is connected for the Roslyn provider."),
                new("coverage", "Line/branch coverage is not measured; relatedTests are name references only."),
            ],
            Diagnostics: [.. diagnostics.DistinctBy(d => (d.Code, d.Message))]);
    }

    private Match Resolve(string target)
    {
        var text = target.Trim();
        var (name, parameters) = text.IndexOf('(', StringComparison.Ordinal) is var open and > 0 && text.EndsWith(')')
            ? (text[..open], Normalize(text[(open + 1)..^1]))
            : (text, null);

        var all = Candidates().ToList();
        var matches = all
            .Where(c => c.Id == text || (c.Names.Contains(name, StringComparer.Ordinal) && (parameters is null || c.Parameters == parameters)))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new ContextException(
                "target.ambiguous",
                $"'{target}' matches {matches.Count} symbols. Pass one of the candidates.",
                [.. matches.Select(m => m.Display).Distinct(StringComparer.Ordinal)]);
        }

        var simple = name[(name.LastIndexOf('.') + 1)..];
        var similar = all
            .Where(c => c.Names.Any(n => n.Contains(simple, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Display)
            .Distinct(StringComparer.Ordinal)
            .Take(10);
        throw new ContextException("target.not_found", $"No C# type or method named '{target}' was found.", [.. similar]);
    }

    private IEnumerable<Match> Candidates()
    {
        foreach (var file in _files.Where(f => !f.IsTest))
        {
            var model = _compilation.GetSemanticModel(file.Tree);
            foreach (var type in file.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t is not InterfaceDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(type) is not { } typeSymbol)
                {
                    continue;
                }

                var typeName = typeSymbol.ToDisplayString(TypeFormat);
                var shortName = string.Join('.', ContainingTypes(typeSymbol).Append(typeSymbol.Name));
                yield return new Match(file, type, typeSymbol.GetDocumentationCommentId() ?? typeName, [typeName, shortName], null, typeName);

                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var parameters = Normalize(string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "")));
                    var id = model.GetDeclaredSymbol(method)?.GetDocumentationCommentId() ?? "";
                    var display = $"{shortName}.{method.Identifier.Text}({string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type?.ToString()))})";
                    yield return new Match(
                        file, method, id,
                        [$"{typeName}.{method.Identifier.Text}", $"{shortName}.{method.Identifier.Text}"],
                        parameters, display);
                }
            }
        }
    }

    private static Stack<string> ContainingTypes(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            names.Push(outer.Name);
        }

        return names;
    }

    private static TargetInfo Target(ISymbol symbol, SyntaxNode node)
    {
        var modifiers = node is MemberDeclarationSyntax member
            ? member.Modifiers.Select(m => m.Text).Where(m => m is not ("public" or "private" or "protected" or "internal" or "file")).ToList()
            : [];
        var method = symbol as IMethodSymbol;
        return new TargetInfo(
            Kind: method is null ? "type" : "method",
            Id: symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(TypeFormat),
            Name: symbol.Name,
            ContainingType: symbol.ContainingType?.ToDisplayString(TypeFormat),
            Namespace: symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            Signature: Signature(node),
            Accessibility: SyntaxFacts.GetText(symbol.DeclaredAccessibility),
            Modifiers: modifiers,
            ReturnType: method is null ? null : Display(method.ReturnType),
            Parameters: method is null
                ? []
                : [.. method.Parameters.Select(p => new ParameterInfo(
                    p.Name,
                    Display(p.Type),
                    p.RefKind switch
                    {
                        RefKind.Ref => "ref",
                        RefKind.Out => "out",
                        RefKind.In => "in",
                        RefKind.RefReadOnlyParameter => "ref readonly",
                        _ => p.IsParams ? "params" : p.IsThis ? "this" : null,
                    },
                    (p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ParameterSyntax)?.Default?.Value.ToString()))]);
    }

    private static SourceInfo Source(SourceFile file, SyntaxNode node, int maxChars)
    {
        var text = file.Tree.GetText();
        var lines = text.Lines.GetLinePositionSpan(node.Span);
        var excerptLines = Enumerable.Range(lines.Start.Line, lines.End.Line - lines.Start.Line + 1)
            .Select(i => text.Lines[i].ToString())
            .ToList();
        var indent = excerptLines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        var excerpt = new StringBuilder();
        var truncated = false;
        foreach (var line in excerptLines)
        {
            var dedented = line.Length >= indent ? line[indent..] : line.TrimStart();
            if (excerpt.Length + dedented.Length + 1 > maxChars)
            {
                truncated = true;
                break;
            }

            excerpt.Append(dedented).Append('\n');
        }

        return new SourceInfo(file.Path, lines.Start.Line + 1, lines.End.Line + 1, file.Sha256, excerpt.ToString(), truncated, Documentation(node));
    }

    private static string? Documentation(SyntaxNode node)
    {
        var comment = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        if (comment is null)
        {
            return null;
        }

        var lines = comment.ToFullString()
            .Split('\n')
            .Select(l => l.Trim())
            .Select(l => l.StartsWith("///", StringComparison.Ordinal) ? l[3..].Trim() : l)
            .Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }

    private string? Project(string file)
    {
        for (var directory = Path.GetDirectoryName(Path.Combine(_root, file)); directory is not null && directory.StartsWith(_root, StringComparison.Ordinal); directory = Path.GetDirectoryName(directory))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj").Order(StringComparer.Ordinal).FirstOrDefault();
            if (project is not null)
            {
                return Path.GetRelativePath(_root, project).Replace('\\', '/');
            }
        }

        return null;
    }

    private static List<MemberInfo> Methods(TypeDeclarationSyntax type, SemanticModel model) =>
    [
        .. type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => model.GetDeclaredSymbol(m)?.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
            .Select(m => ToMember(m, model.GetDeclaredSymbol(m)!)),
    ];

    private static List<MemberInfo> Constructors(TypeDeclarationSyntax type, SemanticModel model)
    {
        var result = new List<MemberInfo>();
        if (type.ParameterList is { } primary && model.GetDeclaredSymbol(type) is { } typeSymbol)
        {
            var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var ctor = typeSymbol.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == primary.Parameters.Count);
            result.Add(new MemberInfo(ctor?.GetDocumentationCommentId() ?? "", $"{type.Identifier.Text}{primary.NormalizeWhitespace()}", type.SyntaxTree.FilePath, line));
        }

        result.AddRange(type.Members.OfType<ConstructorDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword))
            .Select(c => ToMember(c, model.GetDeclaredSymbol(c)!)));
        return result;
    }

    private static MemberInfo ToMember(MemberDeclarationSyntax node, ISymbol symbol) =>
        new(symbol.GetDocumentationCommentId() ?? "", Signature(node), node.SyntaxTree.FilePath, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    private static IEnumerable<(string Name, ITypeSymbol Type)> ConstructorParameters(INamedTypeSymbol type, TypeDeclarationSyntax declaration)
    {
        var constructors = type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared || declaration.ParameterList is not null);
        return constructors.SelectMany(c => c.Parameters).Select(p => (p.Name, p.Type));
    }

    private DependencyInfo Dependency(string role, string name, ITypeSymbol type, List<ContextDiagnostic> diagnostics)
    {
        var declaration = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(d => _files.Any(f => f.Tree == d.SyntaxTree));
        if (declaration is null)
        {
            return new DependencyInfo(role, name, Display(type), type.TypeKind == TypeKind.Error ? null : Kind(type), null, []);
        }

        var location = declaration.GetLocation().GetLineSpan();
        var members = new List<string>();
        if (declaration is TypeDeclarationSyntax { ParameterList: { } primary } typeDecl)
        {
            members.Add($"{Keyword(typeDecl)} {typeDecl.Identifier.Text}{primary.NormalizeWhitespace()}");
        }

        var publicOnly = declaration is not InterfaceDeclarationSyntax;
        if (declaration is TypeDeclarationSyntax withMembers)
        {
            members.AddRange(withMembers.Members
                .Where(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax or ConstructorDeclarationSyntax)
                .Where(m => !publicOnly || m.Modifiers.Any(SyntaxKind.PublicKeyword))
                .Select(Signature)
                .Take(MaxDependencyMembers));
        }
        else if (declaration is EnumDeclarationSyntax enumDecl)
        {
            members.AddRange(enumDecl.Members.Select(m => m.Identifier.Text).Take(MaxDependencyMembers));
        }

        if (members.Count >= MaxDependencyMembers)
        {
            diagnostics.Add(new("members-truncated", $"Only the first {MaxDependencyMembers} members of {Display(type)} are listed."));
        }

        return new DependencyInfo(role, name, Display(type), Kind(type), new SourceLocation(location.Path, location.StartLinePosition.Line + 1), members);
    }

    /// <summary>Named types declared in this repository, including inside generic arguments (<c>IReadOnlyList&lt;OrderLine&gt;</c>).</summary>
    private IEnumerable<INamedTypeSymbol> RepositoryTypes(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            foreach (var element in RepositoryTypes(array.ElementType))
            {
                yield return element;
            }
        }

        if (type is not INamedTypeSymbol named)
        {
            yield break;
        }

        if (named.DeclaringSyntaxReferences.Any(r => _files.Any(f => f.Tree == r.SyntaxTree)))
        {
            yield return named;
        }

        foreach (var argument in named.TypeArguments)
        {
            foreach (var inner in RepositoryTypes(argument))
            {
                yield return inner;
            }
        }
    }

    private static IEnumerable<ITypeSymbol> TargetTypes(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.Parameters.Select(p => p.Type).Append(m.ReturnType),
        INamedTypeSymbol t => t.InstanceConstructors.SelectMany(c => c.Parameters).Select(p => p.Type),
        _ => [],
    };

    private static List<ThrowInfo> Throws(SyntaxNode node, SemanticModel model)
    {
        var result = new List<ThrowInfo>();
        foreach (var descendant in node.DescendantNodes())
        {
            var (type, statement) = descendant switch
            {
                ThrowStatementSyntax { Expression: { } e } s => (ThrownType(e, model), (SyntaxNode)s),
                ThrowExpressionSyntax e => (ThrownType(e.Expression, model), e.FirstAncestorOrSelf<StatementSyntax>() ?? (SyntaxNode)e),
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } i
                    when access.Name.Identifier.Text.StartsWith("ThrowIf", StringComparison.Ordinal)
                        && access.Expression.ToString().EndsWith("Exception", StringComparison.Ordinal)
                    => (model.GetTypeInfo(access.Expression).Type is { TypeKind: not TypeKind.Error } t ? Display(t) : access.Expression.ToString(),
                        i.FirstAncestorOrSelf<StatementSyntax>() ?? (SyntaxNode)i),
                _ => (null, null),
            };

            if (type is not null && statement is not null)
            {
                var line = statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var text = string.Join(' ', statement.ToString().Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
                result.Add(new ThrowInfo(type, line, text.Length > 200 ? text[..200] + "…" : text));
            }
        }

        return result;
    }

    private static string? ThrownType(ExpressionSyntax expression, SemanticModel model) =>
        model.GetTypeInfo(expression).Type is { TypeKind: not TypeKind.Error } t
            ? Display(t)
            : expression is BaseObjectCreationExpressionSyntax creation ? (creation as ObjectCreationExpressionSyntax)?.Type.ToString() : null;

    private List<RelatedTestInfo> RelatedTests(ISymbol symbol, INamedTypeSymbol type)
    {
        var result = new List<RelatedTestInfo>();
        foreach (var file in _files.Where(f => f.IsTest))
        {
            var root = file.Tree.GetRoot();
            var mentionsType = root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == type.Name);
            if (!mentionsType)
            {
                continue;
            }

            foreach (var test in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTestMethod))
            {
                var identifiers = test.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
                var matchedOn = symbol is IMethodSymbol
                    ? identifiers.Contains(symbol.Name) ? $"member:{symbol.Name}" : null
                    : $"type:{type.Name}";
                if (matchedOn is null)
                {
                    continue;
                }

                var testType = test.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.Text;
                var line = test.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                result.Add(new RelatedTestInfo($"{testType}.{test.Identifier.Text}", file.Path, line, matchedOn));
            }
        }

        return [.. result.Take(MaxRelatedTests)];
    }

    private static bool IsTestFile(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Any(IsTestMethod);

    private static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Any(a =>
        {
            var name = a.Name.ToString();
            name = name[(name.LastIndexOf('.') + 1)..];
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                name = name[..^"Attribute".Length];
            }

            return TestAttributes.Contains(name, StringComparer.Ordinal);
        });

    /// <summary>Declaration header without attributes, body, initializer or accessor bodies, on one line.</summary>
    private static string Signature(SyntaxNode node)
    {
        SyntaxNode header = node switch
        {
            MethodDeclarationSyntax m => m.WithAttributeLists([]).WithBody(null).WithExpressionBody(null).WithSemicolonToken(default),
            ConstructorDeclarationSyntax c => c.WithAttributeLists([]).WithBody(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(default),
            PropertyDeclarationSyntax p => p.WithAttributeLists([]).WithInitializer(null).WithSemicolonToken(default).WithExpressionBody(null)
                .WithAccessorList(p.AccessorList is { } list
                    ? SyntaxFactory.AccessorList([.. list.Accessors.Select(a => SyntaxFactory.AccessorDeclaration(a.Kind()).WithModifiers(a.Modifiers).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))])
                    : SyntaxFactory.AccessorList([SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))])),
            EventDeclarationSyntax e => e.WithAttributeLists([]).WithAccessorList(null),
            EventFieldDeclarationSyntax e => e.WithAttributeLists([]).WithSemicolonToken(default),
            TypeDeclarationSyntax t => t.WithAttributeLists([]).WithMembers([]).WithOpenBraceToken(default).WithCloseBraceToken(default).WithSemicolonToken(default).WithBaseList(t.BaseList),
            _ => node,
        };

        return string.Join(' ', header.WithoutTrivia().NormalizeWhitespace().ToString().Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    }

    private static string Keyword(TypeDeclarationSyntax type) => type switch
    {
        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        _ => "class",
    };

    private static string? Kind(ITypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Class => type.IsRecord ? "record" : "class",
        TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => null,
    };

    private static string Display(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static string Normalize(string parameters) =>
        string.Concat(parameters.Where(c => !char.IsWhiteSpace(c)));

    private sealed record SourceFile(string Path, SyntaxTree Tree, string Sha256, bool IsTest);

    private sealed record Match(SourceFile File, SyntaxNode Node, string Id, IReadOnlyList<string> Names, string? Parameters, string Display);
}
