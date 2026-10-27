using YamlDotNet.RepresentationModel;

namespace TestWizz.Cli.Commands;

/// <summary>Only DocWizz's explicit scope.projects.include is used; absent scope means all projects.</summary>
internal static class DocWizzProjectScope
{
    internal static IReadOnlySet<string>? Read(string root)
    {
        var path = Path.Combine(root, "docwizz.yaml");
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = File.OpenText(path);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode rootNode)
                throw new FormatException("Expected one YAML mapping.");
            if (!Get(rootNode, "scope", out var scopeNode)) return null;
            if (scopeNode is not YamlMappingNode scope
                || !Get(scope, "projects", out var projectsNode)
                || projectsNode is not YamlMappingNode projects
                || !Get(projects, "include", out var includeNode)
                || includeNode is not YamlSequenceNode include
                || include.Children.Count == 0)
                throw new FormatException("scope.projects.include must be a nonempty list of project manifests.");
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in include.Children)
            {
                if (entry is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
                    throw new FormatException("scope.projects.include entries must be project paths.");
                var value = scalar.Value!;
                if (value.StartsWith('/') || value.Contains('\\') || value.Contains(':')
                    || value.Contains('*') || value.Contains('?')
                    || value.Split('/').Any(part => part is "" or "." or "..")
                    || !(value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || value.EndsWith("/package.json", StringComparison.Ordinal) || value == "package.json" || value.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException($"Invalid project manifest path '{value}' in scope.projects.include.");
                if (!File.Exists(Path.Combine(root, value.Replace('/', Path.DirectorySeparatorChar))))
                    throw new FormatException($"Scoped project '{value}' does not exist.");
                if (!selected.Add(value))
                    throw new FormatException($"Duplicate project '{value}'.");
            }
            return selected;
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or FormatException)
        {
            throw new CliException("bootstrap.scope_invalid", $"Invalid docwizz.yaml: {ex.Message}");
        }
    }

    private static bool Get(YamlMappingNode map, string key, out YamlNode value)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
            {
                value = pair.Value;
                return true;
            }
        }
        value = null!;
        return false;
    }
}
