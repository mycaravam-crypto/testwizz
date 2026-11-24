using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TestWizz.AI;
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
        if (listOnly && context.Has("--resume"))
            throw new CliException("usage", "--resume cannot be combined with --list-only.");
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
        var project = ReadPlanProject(planJson);
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

        // Capture the read-only route once and reuse one local-model connection for the batch.
        // The requested route is explicit and never inferred from untrusted ADO text.
        var storageState = !listOnly
            ? BrowserStorageState.Resolve(context, root, new Uri(url!)) : null;
        var snapshot = !listOnly && selection.Links.Count > 0
            ? BrowserStorageState.RedactSnapshot(
                UiCommand.CaptureSnapshot(root, url!, context.CancellationToken, storageState)) : null;
        using var model = !listOnly && selection.Links.Count > 0
            ? ModelClient.Create(config.Ai, context.GetEnvironmentVariable) : null;

        const string schema = "testwizz.journey-draft-checkpoint/v1";
        var checkpoint = Path.Combine(output, $"journey-draft-plan-{planId}-checkpoint.json");
        var fingerprint = listOnly ? "" : BatchResume.Fingerprint(new[]
        {
            "journey-draft-batch/v2", CliApp.Version, Path.GetFullPath(root), Path.GetFullPath(output),
            planId.ToString(CultureInfo.InvariantCulture), selection.Project, area, url!,
            BatchResume.PublicConfigFingerprint(config),
            BatchResume.StableAdoJson(planJson), BatchResume.StableAdoJson(traceJson),
            snapshot ?? "",
        });
        var loadedRows = !listOnly && context.Has("--resume")
            ? BatchResume.Read(checkpoint, schema, fingerprint) : new JsonArray();
        var saved = loadedRows.OfType<JsonObject>()
            .Where(item => (string?)item["key"] is { Length: > 0 })
            .ToDictionary(item => (string)item["key"]!, item => item, StringComparer.Ordinal);
        void Save()
        {
            if (listOnly) return;
            var entries = new JsonArray([.. saved.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (JsonNode?)pair.Value.DeepClone())]);
            BatchResume.Save(checkpoint, schema, fingerprint, entries);
        }
        if (!listOnly && !context.Has("--resume")) Save();

        var items = new JsonArray();
        var drafted = 0;
        var resumed = 0;
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
                var unrelated = new JsonObject { ["testCaseId"] = caseId, ["status"] = "skipped",
                    ["reason"] = "no_explicit_tested_by_link" };
                items.Add(unrelated.DeepClone());
                if (!listOnly)
                {
                    saved["case:" + caseId] = new JsonObject
                    {
                        ["key"] = "case:" + caseId, ["status"] = "skipped",
                        ["testCaseId"] = caseId, ["reason"] = "no_explicit_tested_by_link",
                    };
                    Save();
                }
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

                var key = caseId.ToString(CultureInfo.InvariantCulture) + ":" +
                    link.WorkItemId.ToString(CultureInfo.InvariantCulture);
                if (context.Has("--resume") && saved.TryGetValue(key, out var previous) &&
                    (string?)previous["status"] == "drafted" &&
                    BatchResume.Intact(root, output, (string?)previous["path"], (string?)previous["draftSha256"]) &&
                    BatchResume.Intact(root, output, (string?)previous["reviewNotes"], (string?)previous["reviewSha256"]))
                {
                    try
                    {
                        // A plan export does not contain the complete current requirement revision.
                        // Check ADO again without calling AI before reusing a generated draft.
                        var current = AdoUiCriteria.Read(root, link.WorkItemId, context.CancellationToken);
                        if (current.Revision == previous["workItemRevision"]?.GetValue<int>() &&
                            string.Equals(current.Sha256, (string?)previous["criteriaSha256"],
                                StringComparison.OrdinalIgnoreCase))
                        {
                            resumed++;
                            items.Add(new JsonObject
                            {
                                ["testCaseId"] = caseId, ["workItemId"] = link.WorkItemId,
                                ["status"] = "resumed",
                                ["path"] = (string?)previous["path"],
                                ["reviewNotes"] = (string?)previous["reviewNotes"],
                            });
                            log.Append("  resumed WI ").Append(link.WorkItemId)
                                .Append(" / case ").Append(caseId).Append('\n');
                            context.Output.Stderr.WriteLine("testwizz journey-draft: resumed WI " +
                                link.WorkItemId + " / case " + caseId);
                            continue;
                        }
                    }
                    catch (CliException ex)
                    {
                        // Fail closed: a failed ADO freshness check must not result in a
                        // stale successful draft being accepted. Record a retriable error.
                        failed++;
                        items.Add(new JsonObject
                        {
                            ["testCaseId"] = caseId, ["workItemId"] = link.WorkItemId,
                            ["status"] = "failed", ["reason"] = ex.Code,
                        });
                        // Preserve the verified previous row: a temporary ADO outage is
                        // not evidence that its artifact became stale. Retry validation
                        // on the next --resume without regenerating it prematurely.
                        continue;
                    }
                }

                // Call the existing, validated one-case command in-process. It never executes
                // browser actions and guarantees schema/step order before saving a draft.
                context.Output.Stderr.WriteLine("testwizz journey-draft: preparing WI " +
                    link.WorkItemId + " / case " + caseId);
                using var writer = new StringWriter(CultureInfo.InvariantCulture);
                var options = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["--url"] = url,
                    ["--workitem"] = link.WorkItemId.ToString(CultureInfo.InvariantCulture),
                    ["--case"] = caseId.ToString(CultureInfo.InvariantCulture),
                    ["--plan"] = planPath,
                    ["--out"] = output,
                    ["--ado-link-confirmed"] = null, // internal marker, not exposed by the CLI parser
                    ["--batch-snapshot"] = snapshot, // internal in-memory cache, never written to disk
                };
                var child = context with
                {
                    Options = options,
                    Output = new CliOutput(writer, context.Output.Stderr, OutputFormat.Json),
                    SharedModel = model,
                };
                try
                {
                    var exit = JourneyDraftCommand.Definition.Handler(child);
                    if (exit != ExitCodes.Success)
                        throw new CliException("journey_batch.draft", "The individual draft was not generated.");
                    var result = JsonNode.Parse(writer.ToString())?["result"];
                    // JourneyDraftCommand returns FILE paths, unlike the proposal-bundle
                    // generators which return directory paths.
                    var journeyFile = result?["path"]?.GetValue<string>();
                    var reviewFile = result?["reviewNotes"]?.GetValue<string>();
                    if (journeyFile is null || Path.GetFileName(journeyFile) != "journey.json" ||
                        reviewFile is null || Path.GetFileName(reviewFile) != "review.md" ||
                        BatchResume.ShaFile(journeyFile) is not { } journeySha ||
                        BatchResume.ShaFile(reviewFile) is not { } reviewSha)
                        throw new CliException("journey_batch.draft_artifact",
                            "The individual draft did not produce valid journey.json and review.md files.");
                    var reviewed = File.ReadAllText(reviewFile);
                    var criteria = Regex.Match(reviewed, @"(?m)^Criteria SHA-256: ([0-9a-f]{64})$").Groups[1].Value;
                    if (criteria.Length != 64)
                        throw new CliException("journey_batch.draft_artifact",
                            "The review notes are missing the requirement criteria fingerprint.");
                    saved[key] = new JsonObject
                    {
                        ["key"] = key, ["status"] = "drafted",
                        ["testCaseId"] = caseId, ["workItemId"] = link.WorkItemId,
                        ["workItemRevision"] = result?["workItemRevision"]?.DeepClone(),
                        ["criteriaSha256"] = criteria,
                        ["path"] = journeyFile, ["draftSha256"] = journeySha,
                        ["reviewNotes"] = reviewFile, ["reviewSha256"] = reviewSha,
                    };
                    Save();
                    drafted++;
                    items.Add(new JsonObject
                    {
                        ["testCaseId"] = caseId, ["workItemId"] = link.WorkItemId,
                        ["status"] = "drafted", ["path"] = journeyFile, ["reviewNotes"] = reviewFile,
                    });
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
                    saved[key] = new JsonObject
                    {
                        ["key"] = key, ["status"] = existing ? "skipped" : "failed",
                        ["testCaseId"] = caseId, ["workItemId"] = link.WorkItemId,
                        ["reason"] = ex.Code,
                    };
                    Save();
                }
            }
        }

        log.Append("Summary: ").Append(drafted).Append(" drafted, ")
            .Append(skipped).Append(" skipped, ").Append(failed).Append(" failed. ")
            .Append("Review required; nothing executed or applied.\n");
        var success = listOnly || (failed == 0 && (drafted + resumed > 0));
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
            ["resumed"] = resumed,
            ["checkpoint"] = listOnly ? null : checkpoint,
            ["skipped"] = skipped,
            ["failed"] = failed,
            ["listedOnly"] = listOnly,
            ["needsReview"] = true,
            ["executed"] = false,
            ["items"] = items,
        }, log.ToString(), success ? "ok" : "failed");
        return success ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static string ReadPlanProject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var project = root.GetProperty("project").GetString();
            if (root.GetProperty("format").GetString() != "ado-testplan/1" ||
                string.IsNullOrWhiteSpace(project))
                throw new CliException("journey_batch.plan", "ADO CLI returned an unsupported plan export.");
            return project;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new CliException("journey_batch.plan", "ADO CLI returned an invalid plan export.");
        }
    }

    private static string RunAdo(string root, CancellationToken cancellationToken, params string[] args)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        // Drain stderr concurrently without exposing potentially sensitive ADO diagnostics.
        process.ErrorDataReceived += static (_, _) => { };
        try { process.Start(); }
        catch (Win32Exception)
        {
            throw new CliException("journey_batch.ado_missing", "The ado-devops CLI is not on PATH.");
        }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromMinutes(10));
            using var cancellation = limit.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // The child may have exited while cancellation was signaled.
                }
            });
            process.BeginErrorReadLine();
            // ADO prints one JSON document to stdout. This synchronous reader avoids
            // sync-over-async and stays interruptible by the process-kill registration.
            var value = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            if (limit.IsCancellationRequested)
                throw new CliException("journey_batch.ado_timeout", "ADO export exceeded the 10-minute limit.");
            if (process.ExitCode != 0)
                throw new CliException("journey_batch.ado_failed",
                    "ADO export failed (exit " + process.ExitCode + "). Verify 'ado auth status' and project permissions.");
            if (Encoding.UTF8.GetByteCount(value) > 16 * 1024 * 1024)
                throw new CliException("journey_batch.ado_size", "ADO export exceeds the 16 MiB batch limit.");
            return value;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
