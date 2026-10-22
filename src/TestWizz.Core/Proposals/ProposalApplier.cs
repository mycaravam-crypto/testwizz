using System.Globalization;
using System.Text.Json.Nodes;

namespace TestWizz.Core.Proposals;

/// <summary>
/// Copies a proposal's files into the repository after re-checking everything that could have changed since it was
/// generated. Only new files inside the test project are written; existing files are never touched and nothing is
/// committed.
/// </summary>
public static class ProposalApplier
{
    /// <param name="allowUnverified">Apply even when the last run did not pass, no run was recorded, or files were edited by hand.</param>
    /// <returns>Repository-relative paths of the files written.</returns>
    public static IReadOnlyList<string> Apply(string root, LoadedProposal proposal, bool allowUnverified, TimeProvider? time = null)
    {
        root = Path.GetFullPath(root);
        if (proposal.Manifest["valid"]?.GetValue<bool>() != true)
        {
            throw new ProposalException("apply.invalid", "The proposal has validation errors (see findings.md); fix them or generate it again.");
        }

        var stale = ProposalReader.StaleSources(root, proposal);
        if (stale.Count > 0)
        {
            throw new ProposalException("apply.stale", $"Source files changed since the proposal was generated: {string.Join(", ", stale)}. Generate it again.");
        }

        var lastRun = (proposal.Manifest["history"] as JsonArray ?? []).OfType<JsonObject>().LastOrDefault(h => (string?)h["kind"] == "run");
        var verified = lastRun is not null && (string?)lastRun["status"] == "passed" && proposal.Edited.Count == 0;
        if (!verified && !allowUnverified)
        {
            var why = lastRun is null ? "it has not been run ('testwizz run')"
                : proposal.Edited.Count > 0 ? $"{string.Join(", ", proposal.Edited)} was edited after the last run"
                : $"its last run ended with '{(string?)lastRun["status"]}'";
            throw new ProposalException("apply.unverified", $"Not applying because {why}. Pass --allow-unverified to apply anyway.");
        }

        var testProject = (string?)proposal.Conventions["testProject"];
        var testDirectory = testProject is null ? null : testProject.Contains('/', StringComparison.Ordinal) ? testProject[..testProject.LastIndexOf('/')] + "/" : "";
        var targets = new List<(string Path, string Full, string Content)>();
        foreach (var file in proposal.Files)
        {
            var full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (testDirectory is null || !file.Path.StartsWith(testDirectory, StringComparison.Ordinal))
            {
                throw new ProposalException("apply.outside_test_project", $"{file.Path} is not inside the test project {testProject ?? "(none)"}; only test files are applied.");
            }

            bool inside;
            try
            {
                inside = PathGuard.IsWithin(root, full);
            }
            catch (IOException)
            {
                inside = false;
            }

            if (!inside)
            {
                throw new ProposalException("apply.path_escape", $"{file.Path} resolves outside the repository.");
            }

            if (PathGuard.Exists(full))
            {
                throw new ProposalException("apply.file_exists", $"{file.Path} already exists; apply never overwrites files.");
            }

            targets.Add((file.Path, full, file.Content));
        }

        var written = new List<(string Path, string Full)>();
        try
        {
            foreach (var (path, full, content) in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(content);
                }

                written.Add((path, full));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (_, full) in written)
            {
                File.Delete(full);
            }

            throw new ProposalException("apply.write_failed", $"Could not write the files; nothing was applied: {ex.Message}");
        }

        var paths = written.Select(w => w.Path).ToList();
        ProposalBundle.AppendHistory(proposal.Directory, new JsonObject
        {
            ["at"] = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["kind"] = "apply",
            ["summary"] = $"Applied {string.Join(", ", paths)}{(verified ? "" : " (unverified)")}.",
            ["files"] = new JsonArray([.. paths.Select(p => (JsonNode)p)]),
            ["verified"] = verified,
        });
        return paths;
    }
}
