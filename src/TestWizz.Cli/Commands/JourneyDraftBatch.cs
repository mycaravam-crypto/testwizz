using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;

namespace TestWizz.Cli.Commands;

/// <summary>
/// Read-only ADO discovery followed by separate, review-required AI drafts. Never executes model actions.
/// </summary>
public static class JourneyDraftBatch
{
    public sealed record Link(int WorkItemId, int TestCaseId);
    public sealed record Selection(string Project, int PlanId, IReadOnlyList<int> CaseIds, IReadOnlyList<Link> Links);

    /// <summary>Only explicit Tested By links count. Missing links are not inferred from titles or descriptions.</summary>
    public static Selection Select(string planJson, string traceJson, int requestedPlanId)
    {
        try
        {
            using var planDocument = JsonDocument.Parse(planJson);
            using var traceDocument = JsonDocument.Parse(traceJson);
            var plan = planDocument.RootElement;
            var trace = traceDocument.RootElement;
            var planId = plan.GetProperty("plan").GetProperty("id").GetInt32();
            var project = plan.GetProperty("project").GetString();
            if (requestedPlanId <= 0 || plan.GetProperty("format").GetString() != "ado-testplan/1" ||
                planId != requestedPlanId || string.IsNullOrWhiteSpace(project))
                throw new CliException("journey_batch.plan", "ADO plan export does not match the requested plan ID.");

            var caseIds = plan.GetProperty("suites").EnumerateArray()
                .SelectMany(s => s.GetProperty("testCaseIds").EnumerateArray().Select(c => c.GetInt32()))
                .Distinct().Order().ToArray();
            var exportedCases = plan.GetProperty("testCases").EnumerateArray()
                .Select(c => c.GetProperty("id").GetInt32()).ToHashSet();
            if (caseIds.Any(id => id <= 0 || !exportedCases.Contains(id)))
                throw new CliException("journey_batch.plan", "ADO plan export is missing an assigned test case.");

            if (trace.GetProperty("format").GetString() != "ado.test-traceability/v1" ||
                trace.GetProperty("planId").GetInt32() != planId ||
                !string.Equals(trace.GetProperty("project").GetString(), project, StringComparison.OrdinalIgnoreCase))
                throw new CliException("journey_batch.trace", "The ADO trace export has a different plan or project.");

            var validIds = caseIds.ToHashSet();
            var links = trace.GetProperty("requirements").EnumerateArray()
                .SelectMany(wi =>
                {
                    var id = wi.GetProperty("id").GetInt32();
                    if (id <= 0)
                        throw new CliException("journey_batch.trace", "ADO trace contains an invalid work-item ID.");
                    return wi.GetProperty("testCaseIds").EnumerateArray()
                        .Select(c => new Link(id, c.GetInt32()));
                })
                .Where(link => validIds.Contains(link.TestCaseId))
                .Distinct()
                .OrderBy(link => link.TestCaseId).ThenBy(link => link.WorkItemId)
                .ToArray();
            return new Selection(project, planId, caseIds, links);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new CliException("journey_batch.contract", "Malformed ADO test-plan or trace-export JSON.");
        }
    }

    public static int Run(CommandContext context)
    {
        var raw = context.Options.GetValueOrDefault("--plan-id");
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var planId) || planId <= 0)
            throw new CliException("usage", "--plan-id must be a positive integer.");

        var listOnly = context.Has("--list-only");
        var url = context.Options.GetValueOrDefault("--url") ?? context.GetEnvironmentVariable("TESTWIZZ_JOURNEY_URL");
        if (!listOnly && string.IsNullOrWhiteSpace(url))
            throw new CliException("journey_batch.url",
                "Set TESTWIZZ_JOURNEY_URL to a running HTTP loopback route or pass --url. The ADO plan does not define a browser URL.");
        if (!listOnly && (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            parsedUrl.Scheme != "http" || !parsedUrl.IsLoopback || parsedUrl.UserInfo.Length != 0 ||
            parsedUrl.Port <= 0 || parsedUrl.Query.Length != 0 || parsedUrl.Fragment.Length != 0 ||
            parsedUrl.AbsolutePath.StartsWith("//", StringComparison.Ordinal)))
            throw new CliException("journey_draft.invalid_url", "Only HTTP loopback URLs without credentials, query or fragment are supported.");

        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var output = context.Options.GetValueOrDefault("--out") is { } dir
            ? Path.GetFullPath(dir, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);
        try { ProposalBundle.EnsureOutsideRepository(root, output); }
        catch (ProposalException ex) { throw new CliException(ex.Code, ex.Message); }

        // Export from the configured ADO project, never guess requirement relationships.
        var planJson = RunAdo(root, context.CancellationToken, "testplan", "export", planId.ToString(CultureInfo.InvariantCulture));
        using var planDocument = ParsePlan(planJson);
        var project = planDocument.RootElement.GetProperty("project").GetString()!;
        var area = context.Options.GetValueOrDefault("--area") ?? project;
        if (string.IsNullOrWhiteSpace(area))
            throw new CliException("journey_batch.area", "The ADO Area Path cannot be empty.");
        var traceJson = RunAdo(root, context.CancellationToken, "testplan", "trace-export",
            planId.ToString(CultureInfo.InvariantCulture), "--area", area);
        var selection = Select(planJson, traceJson, planId);

        Directory.CreateDirectory(output);
        // Unique, external evidence folder: do not silently overwrite a previous plan snapshot.
        var runFolder = Path.Combine(output, "journey-plan-" + planId.ToString(CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(runFolder);
        var planPath = Path.Combine(runFolder, "ado-plan.json");
        File.WriteAllText(planPath, planJson);
        File.WriteAllText(Path.Combine(runFolder, "ado-trace.json"), traceJson);

        var items = new JsonArray();
        var drafted = 0;
        var skipped = 0;
        var failed = 0;
        var log = new StringBuilder("Journey draft batch for ADO plan ")
            .Append(planId).Append(": ").Append(selection.CaseIds.Count)
            .Append(" test cases, ").Append(selection.Links.Count).Append(" explicit links.\n");
        var pairs = selection.Links.GroupBy(link => link.TestCaseId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        foreach (var caseId in selection.CaseIds)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!pairs.TryGetValue(caseId, out var linked) || linked.Length == 0)
            {
                skipped++;
                items.Add(new JsonObject { ["testCaseId"] = caseId, ["status"] = "skipped",
                    ["reason"] = "no_explicit_tested_by_link" });
                log.Append("  skipped case ").Append(caseId).Append(": no explicit ADO Tested By link\n");
                continue;
            }

            foreach (var link in linked)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (listOnly)
                {
                    items.Add(new JsonObject { ["testCaseId"] = caseId, ["workItemId"] = link.WorkItemId,
                        ["status"] = "linked" });
                    continue;
                }

                // Call the existing, validated one-case command in-process. It never executes
                // browser actions and guarantees schema/step order before saving a draft.
                using var writer = new StringWriter(CultureInfo.InvariantCulture);
                var options = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["--url"] = url,
                    ["--workitem"] = link.WorkItemId.ToString(CultureInfo.InvariantCulture),
                    ["--case"] = caseId.ToString(CultureInfo.InvariantCulture),
                    ["--plan"] = planPath,
                    ["--out"] = output,
                    ["--ado-link-confirmed"] = null, // internal marker, not exposed by the CLI parser
                };
                var child = context with
                {
                    Options = options,
                    Output = new CliOutput(writer, context.Output.Stderr, OutputFormat.Json),
                };
                try
                {
                    var exit = JourneyDraftCommand.Definition.Handler(child);
                    if (exit != ExitCodes.Success)
                        throw new CliException("journey_batch.draft", "The individual draft was not generated.");
                    var result = JsonNode.Parse(writer.ToString())?["result"];
                    items.Add(new JsonObject
                    {
                        ["testCaseId"] = caseId,
                        ["workItemId"] = link.WorkItemId,
                        ["status"] = "drafted",
                        ["path"] = result?["path"]?.GetValue<string>(),
                        ["reviewNotes"] = result?["reviewNotes"]?.GetValue<string>(),
                    });
                    drafted++;
                    log.Append("  drafted WI ").Append(link.WorkItemId).Append(" / case ").Append(caseId).Append('\n');
                }
                catch (CliException ex)
                {
                    var existing = ex.Code == "journey_draft.exists";
                    if (existing) skipped++; else failed++;
                    items.Add(new JsonObject
                    {
                        ["testCaseId"] = caseId,
                        ["workItemId"] = link.WorkItemId,
                        ["status"] = existing ? "skipped" : "failed",
                        ["reason"] = ex.Code,
                    });
                    log.Append("  ").Append(existing ? "skipped" : "failed")
                        .Append(" WI ").Append(link.WorkItemId).Append(" / case ")
                        .Append(caseId).Append(": ").Append(ex.Code).Append('\n');
                }
            }
        }

        log.Append("Summary: ").Append(drafted).Append(" drafted, ")
            .Append(skipped).Append(" skipped, ").Append(failed).Append(" failed. ")
            .Append("Review required; nothing executed or applied.\n");
        var success = listOnly || (failed == 0 && drafted > 0);
        context.Output.Result("journey-draft", new JsonObject
        {
            ["schema"] = "testwizz.journey-draft-batch/v1",
            ["planId"] = planId,
            ["project"] = selection.Project,
            ["area"] = area,
            ["planFile"] = planPath,
            ["traceFile"] = Path.Combine(runFolder, "ado-trace.json"),
            ["totalCases"] = selection.CaseIds.Count,
            ["linkedPairs"] = selection.Links.Count,
            ["drafted"] = drafted,
            ["skipped"] = skipped,
            ["failed"] = failed,
            ["listedOnly"] = listOnly,
            ["needsReview"] = true,
            ["executed"] = false,
            ["items"] = items,
        }, log.ToString(), success ? "ok" : "failed");
        return success ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static JsonDocument ParsePlan(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "ado-testplan/1" ||
                string.IsNullOrWhiteSpace(root.GetProperty("project").GetString()))
                throw new CliException("journey_batch.plan", "ADO CLI returned an unsupported plan export.");
            return document;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new CliException("journey_batch.plan", "ADO CLI returned an invalid plan export.");
        }
    }

    private static string RunAdo(string root, CancellationToken cancellationToken, params string[] args)
    {
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
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try { process.Start(); }
        catch (Win32Exception)
        {
            throw new CliException("journey_batch.ado_missing", "The ado-devops CLI is not on PATH.");
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                var value = stdout.GetAwaiter().GetResult();
                _ = stderr.GetAwaiter().GetResult(); // ADO diagnostics may contain sensitive information.
                if (process.ExitCode != 0)
                    throw new CliException("journey_batch.ado_failed",
                        "ADO export failed (exit " + process.ExitCode + "). Verify 'ado auth status' and project permissions.");
                if (Encoding.UTF8.GetByteCount(value) > 16 * 1024 * 1024)
                    throw new CliException("journey_batch.ado_size", "ADO export exceeds the 16 MiB batch limit.");
                return value;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CliException("journey_batch.ado_timeout", "ADO export exceeded the 3-minute limit.");
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
