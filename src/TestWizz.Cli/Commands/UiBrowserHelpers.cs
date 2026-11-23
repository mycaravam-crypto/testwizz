namespace TestWizz.Cli.Commands;

/// <summary>Extracts the browser helper and its shared session loader into one disposable directory.</summary>
internal static class UiBrowserHelpers
{
    public static string Create(string entrypoint)
    {
        if (entrypoint is not ("ui-snapshot.mjs" or "ui-journey.mjs"))
            throw new ArgumentException("Unsupported browser helper.", nameof(entrypoint));
        var directory = Path.Combine(Path.GetTempPath(), "testwizz-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { entrypoint, "ui-auth.mjs" })
            {
                using var resource = typeof(UiBrowserHelpers).Assembly.GetManifestResourceStream(name)
                    ?? throw new CliException("ui.helper_missing", "An embedded Playwright helper is missing.");
                using var file = File.Create(Path.Combine(directory, name));
                resource.CopyTo(file);
            }
            return Path.Combine(directory, entrypoint);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public static void Delete(string entrypoint)
    {
        var directory = Path.GetDirectoryName(entrypoint);
        if (directory is not null && Directory.Exists(directory) &&
            Path.GetFileName(directory).StartsWith("testwizz-ui-", StringComparison.Ordinal))
            Directory.Delete(directory, recursive: true);
    }
}
