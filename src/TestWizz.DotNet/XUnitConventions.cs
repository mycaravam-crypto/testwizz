using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestWizz.Core.Context;
using TestWizz.Core.Generation;

namespace TestWizz.DotNet;

/// <summary>
/// How tests for a target should look in this repository: which test project references the target's project, which
/// test, mocking and assertion packages it already has, and how existing tests are named and namespaced.
/// Packages are only ever reported, never added.
/// </summary>
public sealed record XUnitConventions(
    string? TargetProject,
    string? TestProject,
    string? TargetFramework,
    string? XunitPackage,
    string? MockingLibrary,
    string? AssertionLibrary,
    bool XunitGlobalUsing,
    bool ImplicitUsings,
    IReadOnlyList<string> Packages,
    string Namespace,
    string ClassName,
    string FilePath,
    string NamingPattern,
    IReadOnlyList<string> ExampleTestNames,
    string? StyleExampleFile,
    string? StyleExample,
    IReadOnlyList<GenerationFinding> Findings)
{
    public const int MaxStyleExampleChars = 4000;

    private static readonly string[] MockingLibraries = ["NSubstitute", "Moq", "FakeItEasy"];
    private static readonly string[] AssertionLibraries = ["FluentAssertions", "AwesomeAssertions", "Shouldly"];
    private static readonly string[] OtherFrameworks = ["NUnit", "MSTest.TestFramework", "MSTest"];
    private static readonly string[] XunitPackages = ["xunit.v3", "xunit"];

    public static XUnitConventions Detect(string root, ContextDocument context)
    {
        root = Path.GetFullPath(root);
        var findings = new List<GenerationFinding>();
        var projects = RepositoryFiles.Files(root, "*.csproj").Select(p => new Project(root, p)).ToList();
        var targetProject = context.Project is null ? null : projects.FirstOrDefault(p => p.Path == context.Project);
        var typeName = context.Target.ContainingType is { } containing ? containing[(containing.LastIndexOf('.') + 1)..] : context.Target.Name;

        var candidates = targetProject is null
            ? []
            : projects.Where(p => p.References.Contains(targetProject.FullPath, StringComparer.Ordinal)).ToList();
        var testProject = candidates
            .OrderByDescending(p => p.Packages.ContainsKey("xunit.v3") || p.Packages.ContainsKey("xunit"))
            .ThenByDescending(p => p.Name == targetProject?.Name + ".Tests")
            .ThenBy(p => p.Path, StringComparer.Ordinal)
            .FirstOrDefault();

        if (testProject is null)
        {
            findings.Add(GenerationFinding.Warning(
                "no-test-project",
                targetProject is null
                    ? "The target is not inside a .csproj project; the proposed path is a guess."
                    : $"No project references {targetProject.Path}. Create a test project before applying the proposal."));
        }

        var packages = testProject?.Packages ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var xunit = XunitPackages.FirstOrDefault(packages.ContainsKey);
        if (xunit is null)
        {
            var other = OtherFrameworks.FirstOrDefault(packages.ContainsKey);
            findings.Add(other is not null
                ? GenerationFinding.Error("unsupported-framework", $"{testProject!.Path} uses {other}; only xUnit generation is implemented.")
                : GenerationFinding.Warning("no-xunit-reference", "The test project does not reference xunit or xunit.v3; the generated tests need it."));
        }

        var mocking = MockingLibraries.FirstOrDefault(packages.ContainsKey);
        if (mocking is null && context.Dependencies.Any(d => d.Role == "constructor" && d.Kind == "interface"))
        {
            findings.Add(GenerationFinding.Warning("no-mocking-library", "No mocking library is referenced; dependencies are replaced by hand-written fakes."));
        }

        var assertions = AssertionLibraries.FirstOrDefault(packages.ContainsKey);

        // Existing tests in the test project, for naming, namespace and a style example.
        var testFiles = testProject is null
            ? []
            : RepositoryFiles.CSharpFiles(root)
                .Where(f => f.StartsWith(testProject.Directory.Length == 0 ? "" : testProject.Directory + "/", StringComparison.Ordinal))
                .Select(f => (Path: f, Text: File.ReadAllText(Path.Combine(root, f))))
                .Where(f => f.Text.Contains("[Fact", StringComparison.Ordinal) || f.Text.Contains("[Theory", StringComparison.Ordinal))
                .ToList();
        var testNames = testFiles.SelectMany(f => TestMethodNames(f.Text)).ToList();

        var testDirectory = testProject?.Directory ?? (targetProject is null ? "tests" : $"tests/{targetProject.Name}.Tests");
        var relativeFolder = targetProject is null ? "" : RelativeFolder(targetProject.Directory, context.Source.File);
        var folder = string.Join('/', new[] { testDirectory, relativeFolder }.Where(p => p.Length > 0));
        var rootNamespace = testFiles.Select(f => NamespaceOf(f.Text)).FirstOrDefault(n => n is not null)
            ?? testProject?.Name
            ?? (targetProject is null ? "Tests" : $"{targetProject.Name}.Tests");
        var ns = relativeFolder.Length == 0 ? rootNamespace : $"{rootNamespace}.{relativeFolder.Replace('/', '.')}";

        var className = UniqueClassName(root, folder, [$"{typeName}Tests", $"{typeName}{context.Target.Name}Tests"]);
        var style = testFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Path) == $"{typeName}Tests");
        if (style.Path is null && testFiles.Count > 0)
        {
            style = testFiles.MinBy(f => f.Text.Length);
        }

        return new XUnitConventions(
            targetProject?.Path,
            testProject?.Path,
            testProject?.Property("TargetFramework") ?? targetProject?.Property("TargetFramework"),
            xunit is null ? null : $"{xunit} {packages[xunit]}".Trim(),
            mocking,
            assertions,
            XunitGlobalUsing: testProject?.Usings.Contains("Xunit", StringComparer.Ordinal) ?? false,
            ImplicitUsings: string.Equals(testProject?.Property("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase),
            [.. packages.Select(p => p.Value is null ? p.Key : $"{p.Key} {p.Value}").Order(StringComparer.Ordinal)],
            ns,
            className,
            $"{folder}/{className}.cs",
            DetectNamingPattern(testNames),
            [.. testNames.Take(5)],
            style.Path,
            style.Text is null ? null : style.Text.Length <= MaxStyleExampleChars ? style.Text : style.Text[..MaxStyleExampleChars],
            findings);
    }

    public JsonObject ToJson() => new()
    {
        ["targetProject"] = TargetProject,
        ["testProject"] = TestProject,
        ["targetFramework"] = TargetFramework,
        ["xunit"] = XunitPackage,
        ["mocking"] = MockingLibrary,
        ["assertions"] = AssertionLibrary ?? "xunit Assert",
        ["xunitGlobalUsing"] = XunitGlobalUsing,
        ["implicitUsings"] = ImplicitUsings,
        ["packages"] = new JsonArray([.. Packages.Select(p => (JsonNode)p)]),
        ["namespace"] = Namespace,
        ["className"] = ClassName,
        ["filePath"] = FilePath,
        ["namingPattern"] = NamingPattern,
        ["exampleTestNames"] = new JsonArray([.. ExampleTestNames.Select(n => (JsonNode)n)]),
        ["styleExampleFile"] = StyleExampleFile,
    };

    private static string UniqueClassName(string root, string folder, string[] preferred)
    {
        foreach (var name in preferred)
        {
            if (!File.Exists(Path.Combine(root, folder, name + ".cs")))
            {
                return name;
            }
        }

        for (var i = 2; ; i++)
        {
            var name = $"{preferred[^1]}{i}";
            if (!File.Exists(Path.Combine(root, folder, name + ".cs")))
            {
                return name;
            }
        }
    }

    private static string RelativeFolder(string projectDirectory, string sourceFile)
    {
        var folder = Path.GetDirectoryName(sourceFile)?.Replace('\\', '/') ?? "";
        var prefix = projectDirectory.Length == 0 ? "" : projectDirectory + "/";
        return folder == projectDirectory ? "" : folder.StartsWith(prefix, StringComparison.Ordinal) ? folder[prefix.Length..] : "";
    }

    private static IEnumerable<string> TestMethodNames(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name.ToString() is "Fact" or "Theory" or "Xunit.Fact" or "Xunit.Theory"))
            .Select(m => m.Identifier.Text);

    private static string? NamespaceOf(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    private static string DetectNamingPattern(List<string> names)
    {
        if (names.Count == 0)
        {
            return "Method_Scenario_ExpectedResult";
        }

        var underscores = names.GroupBy(n => n.Count(c => c == '_')).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        return underscores switch
        {
            0 => "DescriptiveBehaviorName",
            1 => "Method_ExpectedResult",
            _ => "Method_Scenario_ExpectedResult",
        };
    }

    /// <summary>The parts of a .csproj (plus Directory.Build.props / Directory.Packages.props above it) that matter here.</summary>
    private sealed class Project
    {
        private readonly Dictionary<string, string> _properties = new(StringComparer.Ordinal);

        public Project(string root, string relativePath)
        {
            Path = relativePath;
            FullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
            Name = System.IO.Path.GetFileNameWithoutExtension(relativePath);
            Directory = System.IO.Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
            var projectDirectory = System.IO.Path.GetDirectoryName(FullPath)!;

            // Inherited first, so the project's own values win.
            var chain = Ancestors(root, projectDirectory, "Directory.Build.props").Reverse().Append(FullPath);
            var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var props in Ancestors(root, projectDirectory, "Directory.Packages.props").Reverse())
            {
                foreach (var version in Load(props)?.Descendants().Where(e => e.Name.LocalName == "PackageVersion") ?? [])
                {
                    if (version.Attribute("Include")?.Value is { } id && version.Attribute("Version")?.Value is { } v)
                    {
                        centralVersions[id] = v;
                    }
                }
            }

            foreach (var file in chain)
            {
                var document = Load(file);
                if (document is null)
                {
                    continue;
                }

                foreach (var property in document.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup"))
                {
                    _properties[property.Name.LocalName] = property.Value.Trim();
                }

                foreach (var item in document.Descendants())
                {
                    var include = item.Attribute("Include")?.Value;
                    if (include is null)
                    {
                        continue;
                    }

                    switch (item.Name.LocalName)
                    {
                        case "PackageReference":
                            Packages[include] = item.Attribute("Version")?.Value ?? item.Element(item.Name.Namespace + "Version")?.Value
                                ?? centralVersions.GetValueOrDefault(include);
                            break;
                        case "ProjectReference" when file == FullPath:
                            References.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, include.Replace('\\', System.IO.Path.DirectorySeparatorChar))));
                            break;
                        case "Using" when item.Attribute("Static") is null && item.Attribute("Alias") is null:
                            Usings.Add(include);
                            break;
                    }
                }
            }
        }

        public string Path { get; }

        public string FullPath { get; }

        public string Name { get; }

        /// <summary>Repository-relative directory, empty for the root.</summary>
        public string Directory { get; }

        public Dictionary<string, string?> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> References { get; } = [];

        public List<string> Usings { get; } = [];

        public string? Property(string name) => _properties.GetValueOrDefault(name);

        private static IEnumerable<string> Ancestors(string root, string directory, string fileName)
        {
            for (var current = directory; current is not null && current.StartsWith(root, StringComparison.Ordinal); current = System.IO.Path.GetDirectoryName(current))
            {
                var candidate = System.IO.Path.Combine(current, fileName);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }

        private static XDocument? Load(string file)
        {
            try
            {
                return XDocument.Load(file);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
            {
                return null;
            }
        }
    }
}
