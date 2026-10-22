using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestWizz.Core.Generation;

namespace TestWizz.Core.Proposals;

/// <param name="Edited">Proposed files whose content no longer matches the manifest hash (edited by hand after generation).</param>
public sealed record LoadedProposal(
    string Id,
    string Directory,
    JsonObject Manifest,
    string Stack,
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<string> Edited)
{
    public JsonObject Conventions => Manifest["conventions"] as JsonObject ?? [];

    /// <summary>Scenario ids per test method, as recorded at generation time.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TestScenarios =>
        (Manifest["tests"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(t => t["name"] is JsonValue)
            .GroupBy(t => (string)t["name"]!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)[.. g.SelectMany(t => (t["scenarios"] as JsonArray ?? []).Select(s => (string?)s).OfType<string>()).Distinct(StringComparer.Ordinal)],
                StringComparer.Ordinal);
}

/// <summary>Reads a proposal bundle back and re-checks what <see cref="ProposalBundle"/> guaranteed when it wrote it.</summary>
public static class ProposalReader
{
    /// <param name="reference">A bundle directory, or a proposal id inside <paramref name="outputDirectory"/>.</param>
    public static string Resolve(string reference, string outputDirectory, string workingDirectory)
    {
        var asPath = Path.GetFullPath(reference, workingDirectory);
        if (File.Exists(Path.Combine(asPath, ProposalBundle.ManifestFile)))
        {
            return asPath;
        }

        if (PathGuard.IsSafeRelative(reference) && !reference.Contains('/', StringComparison.Ordinal)
            && File.Exists(Path.Combine(outputDirectory, reference, ProposalBundle.ManifestFile)))
        {
            return Path.Combine(outputDirectory, reference);
        }

        throw new ProposalException("proposal.not_found", $"No proposal '{reference}' as a directory or in {outputDirectory}.");
    }

    public static LoadedProposal Load(string directory)
    {
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, ProposalBundle.ManifestFile))) as JsonObject
                ?? throw new ProposalException("proposal.invalid", "manifest.json is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ProposalException("proposal.invalid", $"manifest.json is not valid JSON: {ex.Message}");
        }

        if ((string?)manifest["schema"] != ProposalBundle.Schema)
        {
            throw new ProposalException("proposal.invalid", $"manifest.json is not a {ProposalBundle.Schema} manifest.");
        }

        var id = (string?)manifest["id"] ?? Path.GetFileName(directory);
        var files = new List<GeneratedFile>();
        var edited = new List<string>();
        foreach (var entry in (manifest["files"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var path = (string?)entry["path"] ?? "";
            if (!PathGuard.IsSafeRelative(path))
            {
                throw new ProposalException("proposal.unsafe_path", $"The proposal contains an unsafe path: {path}");
            }

            var source = Path.Combine(directory, ProposalBundle.FilesFolder, path.Replace('/', Path.DirectorySeparatorChar));
            if (!PathGuard.IsWithin(directory, source) || !File.Exists(source))
            {
                throw new ProposalException("proposal.invalid", $"The bundle is missing {ProposalBundle.FilesFolder}/{path}.");
            }

            var content = File.ReadAllText(source);
            if (!string.Equals((string?)entry["sha256"], Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), StringComparison.Ordinal))
            {
                edited.Add(path);
            }

            files.Add(new GeneratedFile(path, content));
        }

        if (files.Count == 0)
        {
            throw new ProposalException("proposal.invalid", "The proposal contains no files.");
        }

        return new LoadedProposal(id, directory, manifest, (string?)manifest["stack"] ?? "", files, edited);
    }

    /// <summary>Recorded source files that changed or disappeared since the proposal was generated.</summary>
    public static IReadOnlyList<string> StaleSources(string root, LoadedProposal proposal)
    {
        var stale = new List<string>();
        foreach (var source in (proposal.Manifest["sources"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var path = (string?)source["path"] ?? "";
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!PathGuard.IsSafeRelative(path) || !File.Exists(full)
                || !string.Equals((string?)source["sha256"], Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))), StringComparison.Ordinal))
            {
                stale.Add(path);
            }
        }

        return stale;
    }
}
