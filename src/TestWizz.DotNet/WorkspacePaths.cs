using TestWizz.Core.Proposals;

namespace TestWizz.DotNet;

/// <summary>Two repository paths that would land on the same place in the isolated workspace.</summary>
/// <param name="Kind"><c>case</c> (paths differ only in letter case) or <c>file-directory</c> (a file and a folder share a name).</param>
public sealed record PathCollision(string First, string Second, string Kind);

/// <param name="Files">Distinct paths to copy, in their original order.</param>
/// <param name="DuplicatesRemoved">Paths that were enumerated more than once with identical spelling; copied once.</param>
public sealed record WorkspacePlan(IReadOnlyList<string> Files, int DuplicatesRemoved, IReadOnlyList<PathCollision> Collisions);

/// <summary>
/// Checks the files of an isolated run's workspace <b>before</b> anything is copied, using the destination file
/// system's semantics. Paths enumerated twice with the same spelling are harmless and are copied once. Distinct
/// paths that the destination treats as the same entry (on Windows and default macOS, <c>src/Foo.ts</c> and
/// <c>src/foo.ts</c>; anywhere, a file <c>a</c> next to a folder <c>a/</c>) are collisions: one would overwrite or
/// block the other, so the run is refused with both paths instead of copying either.
/// </summary>
public static class WorkspacePaths
{
    private static readonly Lazy<bool> TempIsCaseSensitive = new(() => ProbeCaseSensitive(Path.GetTempPath()));

    /// <summary>Whether the system temp folder, where workspaces are created, distinguishes letter case.</summary>
    public static bool DestinationIsCaseSensitive => TempIsCaseSensitive.Value;

    public static WorkspacePlan Plan(IEnumerable<string> files, bool caseSensitive)
    {
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, string>(comparer);
        var directories = new Dictionary<string, string>(comparer);
        var distinct = new List<string>();
        var collisions = new List<PathCollision>();
        var duplicates = 0;
        foreach (var file in files)
        {
            if (!exact.Add(file))
            {
                duplicates++;
                continue;
            }

            distinct.Add(file);
            if (byKey.TryGetValue(file, out var other))
            {
                collisions.Add(new PathCollision(other, file, "case"));
                continue;
            }

            byKey[file] = file;
            var segments = file.Split('/');
            for (var i = 1; i < segments.Length; i++)
            {
                var directory = string.Join('/', segments[..i]);
                directories.TryAdd(directory, file);
            }
        }

        foreach (var file in byKey.Values)
        {
            if (directories.TryGetValue(file, out var inside))
            {
                collisions.Add(new PathCollision(file, inside, "file-directory"));
            }
        }

        return new WorkspacePlan(distinct, duplicates, collisions);
    }

    /// <summary>
    /// The distinct files to copy; throws <c>run.path_collision</c> naming every colliding pair when the destination
    /// cannot hold them all side by side. Nothing has been copied at that point.
    /// </summary>
    public static IReadOnlyList<string> Checked(IEnumerable<string> files, bool caseSensitive)
    {
        var plan = Plan(files, caseSensitive);
        if (plan.Collisions.Count == 0)
        {
            return plan.Files;
        }

        var pairs = string.Join("; ", plan.Collisions.Take(10).Select(c => c.Kind == "case"
            ? $"'{c.First}' and '{c.Second}' differ only in letter case"
            : $"file '{c.First}' and '{c.Second}' need a folder of the same name"));
        var more = plan.Collisions.Count > 10 ? $" (and {plan.Collisions.Count - 10} more)" : "";
        var semantics = caseSensitive ? "" : " The temporary workspace's file system ignores letter case.";
        throw new ProposalException("run.path_collision",
            $"The repository has paths that cannot be copied side by side into the isolated workspace: {pairs}{more}.{semantics} " +
            "Nothing was overwritten. Rename or remove one path of each pair in git (for example 'git mv'), then run again.");
    }

    /// <summary>Creates a probe file in <paramref name="directory"/> and checks whether its upper-case name finds it.</summary>
    public static bool ProbeCaseSensitive(string directory)
    {
        var name = $"testwizz-case-{Guid.NewGuid():N}.probe";
        try
        {
            using (File.Create(Path.Combine(directory, name), 1, FileOptions.DeleteOnClose))
            {
                return !File.Exists(Path.Combine(directory, name.ToUpperInvariant()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unknown: assume case-insensitive, the stricter semantics, which can only refuse more, never overwrite.
            return false;
        }
    }
}
