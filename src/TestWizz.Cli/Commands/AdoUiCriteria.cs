using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestWizz.Cli.Commands;

/// <summary>Read-only adapter for the versioned ado-devops requirement context.</summary>
public static class AdoUiCriteria
{
    public sealed record Snapshot(
        int Id, int Revision, string Server, string Project, string SourceField,
        string WebUrl, string CriteriaMarkdown, string Sha256,
        IReadOnlyList<(string Id, string Text)> Criteria);

    public static Snapshot Read(string root, int workItemId, CancellationToken cancellationToken)
    {
        if (workItemId <= 0)
            throw new CliException("ui.ado_id", "The Azure DevOps work item ID must be a positive integer.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ado")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("workitem");
        process.StartInfo.ArgumentList.Add("context");
        process.StartInfo.ArgumentList.Add(workItemId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new CliException("ui.ado_missing", "The ado-devops executable 'ado' was not found on PATH.");
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                process.WaitForExitAsync(limit.Token).GetAwaiter().GetResult();
                var data = stdout.GetAwaiter().GetResult();
                _ = stderr.GetAwaiter().GetResult(); // Never include ADO diagnostics: they may contain sensitive data.
                if (process.ExitCode != 0)
                    throw new CliException("ui.ado_failed",
                        $"ado workitem context {workItemId} failed with exit code {process.ExitCode}. Check 'ado auth status' and work-item permissions.");
                if (Encoding.UTF8.GetByteCount(data) > 131072)
                    throw new CliException("ui.ado_response_size", "ADO requirement context exceeds the 128 KiB limit.");
                return Parse(data, workItemId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CliException("ui.ado_timeout", "Fetching the ADO requirement context exceeded 30 seconds.");
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Strictly validates the ADO contract; never infers missing acceptance criteria from descriptions.</summary>
    public static Snapshot Parse(string json, int requestedId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("schema").GetString() != "ado.requirement-context/v1" ||
                root.GetProperty("id").GetInt32() != requestedId)
                throw new CliException("ui.ado_contract", "Unexpected ADO requirement context schema or work-item ID.");

            var revision = root.GetProperty("revision").GetInt32();
            var currentRevision = root.GetProperty("currentRevision").GetInt32();
            if (revision <= 0 || revision != currentRevision || !root.GetProperty("isCurrent").GetBoolean())
                throw new CliException("ui.ado_revision", "Only the current ADO work-item revision is supported.");

            var server = Required(root, "server");
            var project = Required(root, "project");
            var webUrl = Required(root, "webUrl");
            var acceptance = root.GetProperty("acceptanceCriteria");
            var field = Required(acceptance, "sourceField");
            var items = acceptance.GetProperty("items");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 5)
                throw new CliException("ui.criteria_count", "ADO work item must contain 1 to 5 explicit acceptance criteria.");

            var criteria = new List<(string Id, string Text)>();
            var i = 0;
            foreach (var item in items.EnumerateArray())
            {
                i++;
                if (item.GetProperty("index").GetInt32() != i)
                    throw new CliException("ui.ado_contract", "ADO acceptance-criterion indexes are not sequential.");
                var text = Required(item, "text").Trim();
                if (text.Contains('\n') || text.Contains('\r') || text.Length > 10000)
                    throw new CliException("ui.ado_contract", "ADO acceptance criterion must be a bounded single line.");
                criteria.Add(($"AC{i}", text));
            }

            var markdown = string.Join("\n", criteria.Select(c => "- " + c.Text)) + "\n";
            if (Encoding.UTF8.GetByteCount(markdown) > 32768)
                throw new CliException("ui.criteria_size", "ADO criteria exceed the 32768-byte limit.");

            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));
            return new Snapshot(requestedId, revision, server, project, field, webUrl, markdown, hash, criteria);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new CliException("ui.ado_contract", "Malformed ado.requirement-context/v1 JSON returned by ado-devops.");
        }
    }

    private static string Required(JsonElement obj, string property)
    {
        var value = obj.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new CliException("ui.ado_contract", $"ADO requirement context is missing '{property}'.");
        return value;
    }
}
