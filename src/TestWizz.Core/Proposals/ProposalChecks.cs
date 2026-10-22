using TestWizz.Core.Generation;

namespace TestWizz.Core.Proposals;

/// <summary>
/// Checks every stack shares: proposed paths stay inside the repository and never replace an existing file, test names
/// are unique, and every test asserts something that can fail. Stack analyzers supply the per-test facts.
/// </summary>
public static class ProposalChecks
{
    public static IReadOnlyList<GenerationFinding> Check(string root, IReadOnlyList<GeneratedFile> files, IReadOnlyList<GeneratedTest> tests) =>
        [.. Paths(root, files), .. Tests(tests)];

    public static IEnumerable<GenerationFinding> Paths(string root, IReadOnlyList<GeneratedFile> files)
    {
        foreach (var group in files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            yield return GenerationFinding.Error("duplicate-path", $"{group.Key} is proposed more than once.");
        }

        foreach (var file in files)
        {
            if (!PathGuard.IsSafeRelative(file.Path))
            {
                yield return GenerationFinding.Error("unsafe-path", $"{file.Path} is not a plain repository-relative path.");
                continue;
            }

            var full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
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
                yield return GenerationFinding.Error("path-escape", $"{file.Path} resolves outside the repository through a symbolic link.");
            }
            else if (PathGuard.Exists(full))
            {
                yield return GenerationFinding.Error("file-exists", $"{file.Path} already exists; proposals never overwrite files.");
            }
        }
    }

    public static IEnumerable<GenerationFinding> Tests(IReadOnlyList<GeneratedTest> tests)
    {
        foreach (var group in tests.GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return GenerationFinding.Error("duplicate-test-name", $"{group.Key} is declared {group.Count()} times.", group.Skip(1).First().Line);
        }

        foreach (var test in tests)
        {
            if (test.Skipped)
            {
                yield return GenerationFinding.Error("skipped-test", $"{test.Name} is skipped; generated tests must run.", test.Line);
            }

            foreach (var line in test.Assertions.PlaceholderLines)
            {
                yield return GenerationFinding.Error("placeholder-assertion", $"{test.Name} contains an assertion that cannot fail or an unfinished body.", line);
            }

            if (test.Assertions is { Meaningful: 0, Weak: 0, PlaceholderLines.Count: 0 })
            {
                yield return GenerationFinding.Error("no-assertions", $"{test.Name} asserts nothing.", test.Line);
            }
            else if (test.Assertions is { Meaningful: 0, Weak: > 0 })
            {
                yield return GenerationFinding.Warning("weak-assertions", $"{test.Name} only checks for null; assert the expected value too.", test.Line);
            }
        }
    }
}
