namespace TestWizz.Vue;

/// <summary>Normalizes project-relative Vue targets without allowing traversal.</summary>
public static class VueTargetPath
{
    public static bool TryNormalize(string? target, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target))
            return false;
        string candidate = target.Replace('\\', '/');
        string[] parts = candidate.Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.Contains(':')))
            return false;
        if (!candidate.EndsWith(".vue", StringComparison.OrdinalIgnoreCase))
            return false;
        normalized = string.Join("/", parts);
        return true;
    }
}
