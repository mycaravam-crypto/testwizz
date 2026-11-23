using System.Text.Json;
using System.Text.RegularExpressions;
using TestWizz.Core.Proposals;

namespace TestWizz.Cli.Commands;

/// <summary>Validates a DocWizz-compatible Playwright state without exposing any token in diagnostics or model prompts.</summary>
internal static partial class BrowserStorageState
{
    private const int MaxBytes = 1048576;

    public static string? Resolve(CommandContext context, string root, Uri target)
    {
        var requested = context.Options.GetValueOrDefault("--storage-state")
            ?? context.GetEnvironmentVariable("TESTWIZZ_STORAGE_STATE");
        return Resolve(root, target, requested);
    }

    public static string? Resolve(string root, Uri target, string? requested)
    {
        if (requested is null) return null;
        var relative = requested.Replace('\\', '/');
        if (!PathGuard.IsSafeRelative(relative) || !relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new CliException("ui.storage_state_path",
                "Storage state must be a repository-relative JSON file (for example .auth/docwizz-state.json).");
        var path = Path.Combine(root, relative);
        if (!PathGuard.IsWithin(root, path) || !File.Exists(path) ||
            new FileInfo(path).Length is < 1 or > MaxBytes)
            throw new CliException("ui.storage_state_path",
                "Storage state must exist within the repository and be at most 1 MiB.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var state = document.RootElement;
            if (state.ValueKind != JsonValueKind.Object ||
                !state.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array ||
                !state.TryGetProperty("origins", out var origins) || origins.ValueKind != JsonValueKind.Array)
                throw new CliException("ui.storage_state_invalid", "Invalid Playwright storage-state format.");

            var origin = target.GetLeftPart(UriPartial.Authority);
            var matchingStorage = false;
            foreach (var item in origins.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("origin", out var entry) || entry.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("localStorage", out var local) || local.ValueKind != JsonValueKind.Array)
                    throw new CliException("ui.storage_state_invalid", "Invalid Playwright storage-state origin.");
                var address = entry.GetString();
                if (Uri.TryCreate(address, UriKind.Absolute, out var parsed) &&
                    parsed.GetLeftPart(UriPartial.Authority).Equals(origin, StringComparison.OrdinalIgnoreCase))
                    matchingStorage = true;
                if (item.TryGetProperty("sessionStorage", out var session))
                {
                    if (session.ValueKind != JsonValueKind.Array)
                        throw new CliException("ui.storage_state_invalid", "Invalid sessionStorage entry.");
                    foreach (var value in session.EnumerateArray())
                        if (value.ValueKind != JsonValueKind.Object ||
                            !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                            !value.TryGetProperty("value", out var data) || data.ValueKind != JsonValueKind.String)
                            throw new CliException("ui.storage_state_invalid", "Invalid sessionStorage entry.");
                }
            }

            var cookieForHost = false;
            foreach (var cookie in cookies.EnumerateArray())
            {
                if (cookie.ValueKind != JsonValueKind.Object ||
                    !cookie.TryGetProperty("domain", out var domain) || domain.ValueKind != JsonValueKind.String)
                    throw new CliException("ui.storage_state_invalid", "Invalid Playwright cookie entry.");
                var host = domain.GetString()!.TrimStart('.');
                if (string.Equals(host, target.Host, StringComparison.OrdinalIgnoreCase))
                    cookieForHost = true;
            }
            if (!matchingStorage && !cookieForHost)
                throw new CliException("ui.storage_state_origin",
                    "The storage state has no cookies or storage for the requested application origin. " +
                    "Record the login using the same localhost/127.0.0.1 hostname and port.");
            return Path.GetFullPath(path);
        }
        catch (JsonException)
        {
            throw new CliException("ui.storage_state_invalid", "Storage state is not valid Playwright JSON.");
        }
    }

    // Auth values must not be sent as part of an accessibility snapshot, even if a test UI
    // accidentally renders a JWT on screen. The storage-state file itself is never sent to AI.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(?![A-Za-z0-9_-])")]
    private static partial Regex JwtPattern();

    public static string RedactSnapshot(string snapshot) => JwtPattern().Replace(snapshot, "[JWT redacted]");
}
