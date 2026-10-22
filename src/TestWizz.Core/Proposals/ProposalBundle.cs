using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestWizz.Core.Context;
using TestWizz.Core.Generation;
using TestWizz.Core.Planning;

namespace TestWizz.Core.Proposals;

/// <param name="Id">Directory name of the bundle; derived from the target and a hash of the manifest.</param>
public sealed record Proposal(string Id, string Directory, JsonObject Manifest)
{
    public bool IsValid => (bool)Manifest["valid"]!;
}

public sealed class ProposalException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Writes a <c>testwizz.proposal/v1</c> bundle outside the repository: <c>manifest.json</c>, <c>plan.md</c>, the
/// proposed files under <c>files/</c>, <c>changes.patch</c> (applies with <c>git apply</c>) and <c>findings.md</c>.
/// The bundle is assembled in a temporary folder and moved into place, so a failed write leaves nothing behind.
/// </summary>
public static class ProposalBundle
{
    public const string Schema = "testwizz.proposal/v1";
    public const string ManifestFile = "manifest.json";
    public const string FilesFolder = "files";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Proposal Write(string root, string outputDirectory, ContextDocument context, PlanDocument plan, GenerationResult generation)
    {
        root = Path.GetFullPath(root);
        outputDirectory = Path.GetFullPath(outputDirectory);
        EnsureOutsideRepository(root, outputDirectory);

        var manifest = Manifest(root, context, plan, generation);
        var id = UniqueId(outputDirectory, Slug(context.Target) + "-" + Sha256(manifest.ToJsonString())[..12]);
        manifest.Insert(1, "id", id);

        var final = Path.Combine(outputDirectory, id);
        var staging = Path.Combine(outputDirectory, $".{id}.tmp-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in generation.Files.Where(f => PathGuard.IsSafeRelative(f.Path)))
            {
                var path = Path.Combine(staging, FilesFolder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
            }

            File.WriteAllText(Path.Combine(staging, "plan.md"), PlanJson.ToMarkdown(plan));
            File.WriteAllText(Path.Combine(staging, "changes.patch"), Patch(generation.Files));
            File.WriteAllText(Path.Combine(staging, "findings.md"), FindingsMarkdown(id, plan, generation));
            File.WriteAllText(Path.Combine(staging, ManifestFile), manifest.ToJsonString(JsonOptions) + "\n");
            Directory.Move(staging, final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            throw new ProposalException("proposal.write_failed", $"Could not write the proposal to {final}: {ex.Message}");
        }

        return new Proposal(id, final, manifest);
    }

    /// <summary>Refuses an output directory inside the repository, including one reached through a symbolic link.</summary>
    public static void EnsureOutsideRepository(string root, string outputDirectory)
    {
        bool inside;
        try
        {
            inside = PathGuard.IsWithin(root, outputDirectory);
        }
        catch (IOException ex)
        {
            throw new ProposalException("proposal.output_unresolvable", $"Cannot resolve the output directory {outputDirectory}: {ex.Message}");
        }

        if (inside)
        {
            throw new ProposalException(
                "proposal.output_inside_repository",
                $"The output directory {outputDirectory} is inside the repository. Proposals are written outside the working tree; choose another --out or 'output' in testwizz.json.");
        }
    }

    public static string PlanHash(PlanDocument plan) => Sha256(PlanJson.ToJson(plan).ToJsonString());

    private static JsonObject Manifest(string root, ContextDocument context, PlanDocument plan, GenerationResult generation)
    {
        var generationJson = GenerationJson.ToJson(generation);
        var coveredBy = plan.Scenarios.ToDictionary(
            s => s.Id,
            s => generation.Tests.Where(t => t.Scenarios.Contains(s.Id)).Select(t => t.Name).ToList(),
            StringComparer.Ordinal);

        return new JsonObject
        {
            ["schema"] = Schema,
            ["valid"] = generation.IsValid,
            ["stack"] = generation.Stack,
            ["target"] = new JsonObject { ["id"] = generation.TargetId, ["signature"] = generation.TargetSignature },
            ["toolVersion"] = context.Provenance.ToolVersion,
            ["revision"] = context.Revision,
            ["contextSha256"] = generation.ContextSha256,
            ["planSha256"] = PlanHash(plan),
            ["generator"] = generationJson["generator"]!.DeepClone(),
            ["conventions"] = generationJson["conventions"]!.DeepClone(),
            ["sources"] = new JsonArray([.. Sources(root, context).Select(s => (JsonNode)new JsonObject { ["path"] = s.Path, ["sha256"] = s.Sha256 })]),
            ["files"] = new JsonArray([.. generation.Files.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Path,
                ["bundlePath"] = PathGuard.IsSafeRelative(f.Path) ? $"{FilesFolder}/{f.Path}" : null,
                ["sha256"] = Sha256(f.Content),
            })]),
            ["tests"] = generationJson["tests"]!.DeepClone(),
            ["scenarios"] = new JsonArray([.. plan.Scenarios.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id,
                ["category"] = ScenarioCategories.Name(s.Category),
                ["title"] = s.Title,
                ["source"] = s.Source,
                ["needsReview"] = s.NeedsReview,
                ["coveredBy"] = new JsonArray([.. coveredBy[s.Id].Select(n => (JsonNode)n)]),
            })]),
            ["assumptions"] = generationJson["assumptions"]!.DeepClone(),
            ["findings"] = generationJson["findings"]!.DeepClone(),
        };
    }

    /// <summary>Repository files the proposal was derived from; <c>apply</c> refuses when any of them changed.</summary>
    private static IEnumerable<(string Path, string Sha256)> Sources(string root, ContextDocument context)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal) { [context.Source.File] = context.Source.Sha256 };
        foreach (var file in context.Dependencies.Select(d => d.DeclaredIn?.File).OfType<string>().Where(f => !sources.ContainsKey(f)))
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!PathGuard.IsSafeRelative(file) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                sources[file] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable now means unreadable for the stale check too; the target source hash still applies.
            }
        }

        return sources.Select(s => (s.Key, s.Value));
    }

    /// <summary>A git-style patch that creates each proposed file; existing files are never part of it.</summary>
    public static string Patch(IReadOnlyList<GeneratedFile> files)
    {
        var patch = new StringBuilder();
        foreach (var file in files.Where(f => PathGuard.IsSafeRelative(f.Path)))
        {
            var lines = file.Content.Split('\n');
            var endsWithNewline = file.Content.EndsWith('\n');
            if (endsWithNewline)
            {
                lines = lines[..^1];
            }

            patch.Append(CultureInfo.InvariantCulture, $"diff --git a/{file.Path} b/{file.Path}\n");
            patch.Append("new file mode 100644\n--- /dev/null\n");
            patch.Append(CultureInfo.InvariantCulture, $"+++ b/{file.Path}\n");
            if (lines.Length == 0)
            {
                continue;
            }

            patch.Append(CultureInfo.InvariantCulture, $"@@ -0,0 +1{(lines.Length == 1 ? "" : $",{lines.Length}")} @@\n");
            foreach (var line in lines)
            {
                patch.Append('+').Append(line).Append('\n');
            }

            if (!endsWithNewline)
            {
                patch.Append("\\ No newline at end of file\n");
            }
        }

        return patch.ToString();
    }

    private static string FindingsMarkdown(string id, PlanDocument plan, GenerationResult generation)
    {
        var md = new StringBuilder();
        md.Append("# Proposal `").Append(id).Append("`\n\n");
        md.Append("Target: `").Append(generation.TargetSignature).Append("`  \n");
        md.Append("Model: ").Append(generation.Generator.Provider).Append(" `").Append(generation.Generator.Model).Append('`')
            .Append(generation.Generator.ModelDigest is { } digest ? $" ({digest})" : "").Append(" · prompt `").Append(generation.Generator.PromptTemplate).Append("`  \n");
        var errors = generation.Findings.Count(f => f.Severity == "error");
        var warnings = generation.Findings.Count - errors;
        md.Append("Result: **").Append(generation.IsValid ? "valid" : "invalid").Append("** · ")
            .Append(errors.ToString(CultureInfo.InvariantCulture)).Append(" error(s), ")
            .Append(warnings.ToString(CultureInfo.InvariantCulture)).Append(" warning(s)\n\n");
        md.Append("Nothing has been applied to the repository. Review `changes.patch` and the files under `files/`.\n");

        md.Append("\n## Findings\n\n");
        if (generation.Findings.Count == 0)
        {
            md.Append("None.\n");
        }
        else
        {
            md.Append("| Severity | Code | Line | Message |\n|----------|------|------|---------|\n");
            foreach (var f in generation.Findings)
            {
                md.Append("| ").Append(f.Severity).Append(" | ").Append(f.Code).Append(" | ")
                    .Append(f.Line?.ToString(CultureInfo.InvariantCulture) ?? "").Append(" | ").Append(Cell(f.Message)).Append(" |\n");
            }
        }

        md.Append("\n## Tests\n\n");
        if (generation.Tests.Count == 0)
        {
            md.Append("None.\n");
        }

        foreach (var t in generation.Tests)
        {
            md.Append("- `").Append(t.Name).Append("` (line ").Append(t.Line.ToString(CultureInfo.InvariantCulture)).Append(") → ")
                .Append(t.Scenarios.Count == 0 ? "no scenario" : string.Join(", ", t.Scenarios)).Append('\n');
        }

        var review = plan.Scenarios.Where(s => s.NeedsReview).ToList();
        md.Append("\n## Scenarios to review\n\n");
        if (review.Count == 0)
        {
            md.Append("None.\n");
        }

        foreach (var s in review)
        {
            md.Append("- **").Append(s.Id).Append("** ").Append(s.Title).Append(": ").Append(s.ReviewReason ?? "needs review").Append('\n');
        }

        md.Append("\n## Assumptions\n\n");
        if (generation.Assumptions.Count == 0)
        {
            md.Append("None stated by the model.\n");
        }

        foreach (var a in generation.Assumptions)
        {
            md.Append("- ").Append(a).Append('\n');
        }

        return md.ToString();
    }

    private static string UniqueId(string outputDirectory, string baseId)
    {
        var id = baseId;
        for (var n = 2; PathGuard.Exists(Path.Combine(outputDirectory, id)); n++)
        {
            id = $"{baseId}-{n.ToString(CultureInfo.InvariantCulture)}";
        }

        return id;
    }

    private static string Slug(TargetInfo target)
    {
        var name = target.ContainingType is { } type ? $"{type}-{target.Name}" : target.Name;
        var slug = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        return text.Length == 0 ? "proposal" : text[..Math.Min(text.Length, 60)];
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
