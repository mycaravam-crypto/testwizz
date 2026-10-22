using System.Diagnostics;

namespace TestWizz.DotNet;

/// <summary>
/// Lists C# files under a root in a stable order. Inside a git work tree it uses <c>git ls-files</c> so ignored files
/// are skipped; otherwise it walks the directory, skipping <c>bin</c>, <c>obj</c>, <c>node_modules</c> and dot-folders.
/// </summary>
internal static class RepositoryFiles
{
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxFiles = 10_000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

    /// <summary>Repository-relative paths with forward slashes, ordinal order.</summary>
    public static IReadOnlyList<string> CSharpFiles(string root)
    {
        var files = Git(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "*.cs") is { } output
            ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(f => File.Exists(Path.Combine(root, f)))
            : Walk(root);
        return [.. files.Select(f => f.Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxFiles)];
    }

    /// <summary>HEAD commit, or null outside a git work tree or when git is unavailable.</summary>
    public static string? Revision(string root) => Git(root, "rev-parse", "HEAD")?.Trim() is { Length: 40 } sha ? sha : null;

    private static IEnumerable<string> Walk(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            {
                yield return Path.GetRelativePath(root, file);
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !SkippedDirectories.Contains(name)
                    && !new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static string? Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not installed
        }
    }
}
