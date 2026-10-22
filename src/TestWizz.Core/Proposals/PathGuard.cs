namespace TestWizz.Core.Proposals;

/// <summary>
/// Confines proposed paths to a repository. Paths must be relative, use forward slashes and contain no <c>.</c> or
/// <c>..</c> segments; symbolic links on the way are resolved, so a linked folder that points elsewhere is an escape.
/// </summary>
public static class PathGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True for a plain repository-relative path such as <c>tests/App.Tests/FooTests.cs</c>.</summary>
    public static bool IsSafeRelative(string path) =>
        path.Length is > 0 and <= 512
        && !path.Any(c => char.IsControl(c) || c is '\\' or ':')
        && !path.StartsWith('/')
        && !Path.IsPathRooted(path)
        && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." && segment.Trim() == segment);

    /// <summary>Full path with every existing symbolic link on the way resolved to its final target.</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                // A dangling link resolves to where it points; an unreadable one fails closed via the escape check.
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                current = target is null ? Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(current)!) : RealPath(target.FullName);
            }
        }

        return current;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> itself or inside it, after resolving links.</summary>
    public static bool IsWithin(string directory, string path)
    {
        var parent = Path.TrimEndingDirectorySeparator(RealPath(directory));
        var child = Path.TrimEndingDirectorySeparator(RealPath(path));
        return child.Equals(parent, PathComparison)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>True when anything, including a dangling symbolic link, already exists at the path.</summary>
    public static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
}
