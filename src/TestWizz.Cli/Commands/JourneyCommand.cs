using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TestWizz.Core.Proposals;

namespace TestWizz.Cli.Commands;

/// <summary>Captures reviewed, declarative browser actions; never executes model-generated code.</summary>
public static class JourneyCommand
{
    public sealed record JourneyStep(int StepId, string Action, string? Role = null, string? Name = null,
        string? Label = null, string? TestId = null, string? Value = null,
        string? ExpectRole = null, string? ExpectName = null, string? ExpectText = null);
    public sealed record Spec(string Schema, List<JourneyStep> Steps);
    public sealed record CaseStep(int Id, string Action, string Expected);
    public sealed record TestCase(int Id, int Revision, string Title, string Project, int PlanId, IReadOnlyList<CaseStep> Steps);

    public static CommandDefinition Definition { get; } = new(
        "journey",
        "Capture screenshot evidence from reviewed ADO test-case steps. Never executes AI-generated code.",
        "testwizz journey [path] --url <loopback-url> --workitem <id> --plan <ado-plan.json> --case <id> --steps <reviewed.json> [--out <dir>]",
        [
            new("--url", "Running HTTP loopback application URL.", "url"),
            new("--workitem", "ADO work-item ID (read via ado workitem context).", "id"),
            new("--plan", "File exported using ado testplan export.", "json"),
            new("--case", "Test-case ID in the exported plan.", "id"),
            new("--steps", "Repository-relative reviewed declarative browser steps.", "json"),
            new("--out", "Evidence directory outside the repository.", "dir"),
        ], 1, Run);

    private static readonly JsonSerializerOptions SpecOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static Spec ParseSpec(string json)
    {
        Spec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<Spec>(json, SpecOptions);
        }
        catch (JsonException)
        {
            throw new CliException("journey.invalid_steps", "Reviewed journey is not valid testwizz.journey/v1 JSON.");
        }
        if (spec?.Schema != "testwizz.journey/v1" || spec.Steps is null || spec.Steps.Count is < 1 or > 6)
            throw new CliException("journey.invalid_steps", "Reviewed journey requires 1 to 6 steps in testwizz.journey/v1.");
        var seen = new HashSet<int>();
        foreach (var step in spec.Steps)
        {
            if (step is null || step.StepId <= 0 || !seen.Add(step.StepId) || step.Action is not ("click" or "fill"))
                throw new CliException("journey.invalid_steps", "Each step needs a unique positive ID and click or fill action.");
            var selectors = (step.Role is not null ? 1 : 0) + (step.Label is not null ? 1 : 0) + (step.TestId is not null ? 1 : 0);
            if (selectors != 1 || (step.Role is not null && string.IsNullOrWhiteSpace(step.Name)) ||
                (step.Role is null && step.Name is not null) ||
                (step.Action == "fill" && string.IsNullOrEmpty(step.Value)) ||
                (step.Action == "click" && step.Value is not null) ||
                (step.ExpectRole is not null) != (step.ExpectName is not null) ||
                ((step.ExpectRole is not null ? 1 : 0) + (step.ExpectText is not null ? 1 : 0)) != 1 ||
                new[] { step.Role, step.Name, step.Label, step.TestId, step.Value,
                    step.ExpectRole, step.ExpectName, step.ExpectText }.Any(x => x is { Length: > 200 }))
                throw new CliException("journey.invalid_steps", "Each step needs one locator and one explicit visibility assertion.");
        }
        return spec;
    }

    public static TestCase ParseCase(string json, int id)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var plan = document.RootElement;
            if (plan.GetProperty("format").GetString() != "ado-testplan/1")
                throw new CliException("journey.plan_schema", "Expected an ado-testplan/1 export.");
            var planId = plan.GetProperty("plan").GetProperty("id").GetInt32();
            var project = plan.GetProperty("project").GetString() ?? "";
            var assigned = plan.GetProperty("suites").EnumerateArray().Any(s =>
                s.GetProperty("testCaseIds").EnumerateArray().Any(c => c.GetInt32() == id));
            var found = plan.GetProperty("testCases").EnumerateArray().Where(c => c.GetProperty("id").GetInt32() == id).ToArray();
            if (!assigned || found.Length != 1)
                throw new CliException("journey.case_missing", $"Test case {id} is not uniquely assigned to this ADO plan.");
            var selected = found[0];
            var revision = selected.GetProperty("rev").GetInt32();
            var title = selected.GetProperty("title").GetString() ?? "";
            var steps = new List<CaseStep>();
            foreach (var step in selected.GetProperty("steps").EnumerateArray())
            {
                if (step.TryGetProperty("sharedStepsId", out _))
                    throw new CliException("journey.shared_steps", "Shared steps must be expanded and reviewed before capture.");
                steps.Add(new(step.GetProperty("id").GetInt32(),
                    Plain(step.GetProperty("action").GetString()),
                    Plain(step.GetProperty("expectedResult").GetString())));
            }
            if (id <= 0 || planId <= 0 || revision <= 0 || title.Length is 0 or > 250 ||
                string.IsNullOrWhiteSpace(project) || steps.Count is < 1 or > 6 ||
                steps.Any(x => x.Id <= 0 || x.Action.Length is 0 or > 500 || x.Expected.Length > 500) ||
                steps.Select(x => x.Id).Distinct().Count() != steps.Count)
                throw new CliException("journey.plan_invalid", "ADO case must have 1 to 6 explicit, bounded steps.");
            return new(id, revision, title, project, planId, steps);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CliException("journey.plan_invalid", "Malformed ado-testplan/1 JSON.");
        }
    }

    private static string Plain(string? html)
    {
        var noScript = Regex.Replace(html ?? "", @"<(script|style)\b.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return WebUtility.HtmlDecode(Regex.Replace(noScript, "<[^>]*>", "")).Trim();
    }

    private static int Run(CommandContext context)
    {
        var urlText = context.Options.GetValueOrDefault("--url");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != "http" ||
            !url.IsLoopback || url.Port <= 0 || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath.StartsWith("//", StringComparison.Ordinal))
            throw new CliException("journey.invalid_url", "Only HTTP loopback URLs are supported.");
        static int Positive(IReadOnlyDictionary<string, string?> options, string key) =>
            int.TryParse(options.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n : throw new CliException("usage", key + " requires a positive integer.");
        var wiId = Positive(context.Options, "--workitem");
        var caseId = Positive(context.Options, "--case");
        var root = context.PathArgument();
        var planPath = context.Options.GetValueOrDefault("--plan");
        var stepsPath = context.Options.GetValueOrDefault("--steps");
        if (string.IsNullOrWhiteSpace(planPath) || string.IsNullOrWhiteSpace(stepsPath))
            throw new CliException("usage", Definition.Usage);
        if (!PathGuard.IsSafeRelative(stepsPath) || !PathGuard.IsWithin(root, Path.Combine(root, stepsPath)) ||
            !stepsPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new CliException("journey.steps_path", "--steps must be a repository-relative reviewed JSON file.");
        var stepsFull = Path.Combine(root, stepsPath);
        if (!File.Exists(stepsFull) || new FileInfo(stepsFull).Length is < 1 or > 16384)
            throw new CliException("journey.steps_path", "Reviewed steps must exist and be at most 16 KiB.");
        var reviewedBytes = File.ReadAllBytes(stepsFull);
        var spec = ParseSpec(Encoding.UTF8.GetString(reviewedBytes));
        var planFull = Path.GetFullPath(planPath, context.WorkingDirectory);
        if (!File.Exists(planFull) || new FileInfo(planFull).Length is < 1 or > 1048576)
            throw new CliException("journey.plan_path", "ADO plan export must exist and be at most 1 MiB.");
        var planBytes = File.ReadAllBytes(planFull);
        var testCase = ParseCase(Encoding.UTF8.GetString(planBytes), caseId);
        if (!testCase.Steps.Select(s => s.Id).SequenceEqual(spec.Steps.Select(s => s.StepId)))
            throw new CliException("journey.step_mismatch", "Reviewed steps must match the exported test-case step IDs and order.");
        var ado = AdoUiCriteria.Read(root, wiId, context.CancellationToken);
        if (!string.Equals(ado.Project, testCase.Project, StringComparison.OrdinalIgnoreCase))
            throw new CliException("journey.project_mismatch", "Work item and test plan belong to different ADO projects.");

        var output = context.Options.GetValueOrDefault("--out") is { } directory
            ? Path.GetFullPath(directory, context.WorkingDirectory)
            : Path.GetFullPath("../testwizz-journeys", root);
        try { ProposalBundle.EnsureOutsideRepository(root, output); }
        catch (ProposalException ex) { throw new CliException(ex.Code, ex.Message); }

        Directory.CreateDirectory(output);
        var staging = Path.Combine(output, ".journey-" + Guid.NewGuid().ToString("N"));
        var name = $"wi-{wiId}-case-{caseId}-" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(ado.Sha256 + testCase.Revision + url.PathAndQuery +
                Convert.ToHexStringLower(SHA256.HashData(reviewedBytes)))))[..12];
        var final = Path.Combine(output, name);
        if (Directory.Exists(final))
            throw new CliException("journey.exists", "Evidence already exists for these inputs; remove it explicitly before recapturing.");
        Directory.CreateDirectory(staging);
        var helper = Path.Combine(Path.GetTempPath(), "testwizz-journey-" + Guid.NewGuid().ToString("N") + ".mjs");
        try
        {
            using (var resource = typeof(JourneyCommand).Assembly.GetManifestResourceStream("ui-journey.mjs")
                ?? throw new CliException("journey.helper_missing", "Embedded Playwright helper missing."))
            using (var file = File.Create(helper)) resource.CopyTo(file);
            var browser = Capture(root, url.ToString(), stepsFull, staging, helper, context.CancellationToken);
            if (browser.Length != spec.Steps.Count)
                throw new CliException("journey.capture_invalid", "Playwright returned a different number of screenshots.");
            var evidence = new JsonArray();
            for (var i = 0; i < spec.Steps.Count; i++)
            {
                var rel = $"screenshots/step-{spec.Steps[i].StepId:D4}.png";
                if (browser[i] != spec.Steps[i].StepId || !File.Exists(Path.Combine(staging, rel)))
                    throw new CliException("journey.capture_invalid", "A required screenshot is missing or out of order.");
                var image = File.ReadAllBytes(Path.Combine(staging, rel));
                if (image.Length is < 32 or > 10485760)
                    throw new CliException("journey.capture_invalid", "Screenshot has an invalid size.");
                evidence.Add(new JsonObject {
                    ["stepId"] = spec.Steps[i].StepId, ["action"] = testCase.Steps[i].Action,
                    ["expected"] = testCase.Steps[i].Expected, ["result"] = "passed",
                    ["screenshot"] = rel, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(image)),
                });
            }
            var manifest = new JsonObject {
                ["schema"] = "testwizz.ui-evidence/v1", ["status"] = "passed",
                ["title"] = testCase.Title, ["route"] = url.PathAndQuery,
                ["source"] = new JsonObject {
                    ["workItemId"] = wiId, ["workItemRevision"] = ado.Revision,
                    ["workItemUrl"] = ado.WebUrl, ["criteriaSha256"] = ado.Sha256,
                    ["testCaseId"] = caseId, ["testCaseRevision"] = testCase.Revision,
                    ["planId"] = testCase.PlanId, ["project"] = testCase.Project,
                    ["planSha256"] = Convert.ToHexStringLower(SHA256.HashData(planBytes)),
                    ["reviewedStepsSha256"] = Convert.ToHexStringLower(SHA256.HashData(reviewedBytes)),
                    ["relation"] = "selected-not-verified",
                },
                ["acceptanceCriteria"] = new JsonArray([.. ado.Criteria.Select(c => (JsonNode)new JsonObject
                    { ["id"] = c.Id, ["text"] = c.Text })]),
                ["steps"] = evidence,
            };
            File.WriteAllText(Path.Combine(staging, "manifest.json"),
                manifest.ToJsonString(CliOutput.JsonOptions) + "\n");
            Directory.Move(staging, final);
            context.Output.Result("journey", new JsonObject {
                ["schema"] = "testwizz.ui-evidence/v1", ["path"] = final,
                ["workItemId"] = wiId, ["testCaseId"] = caseId,
                ["steps"] = evidence.Count, ["status"] = "passed", ["reviewed"] = false,
            }, $"Journey evidence: {final}\nCaptured {evidence.Count} asserted steps. Documentation review required.\n");
            return ExitCodes.Success;
        }
        finally
        {
            if (File.Exists(helper)) File.Delete(helper);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static int[] Capture(string root, string url, string spec, string output, string helper, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(root, "package.json")))
            throw new CliException("journey.playwright_missing", "Target repository needs package.json and @playwright/test.");
        using var process = new Process {
            StartInfo = new ProcessStartInfo("node") {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var item in new[] { helper, root, url, spec, output }) process.StartInfo.ArgumentList.Add(item);
        try { process.Start(); }
        catch (Win32Exception) { throw new CliException("journey.node_missing", "Node.js is required."); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                var response = stdout.GetAwaiter().GetResult();
                _ = stderr.GetAwaiter().GetResult(); // Never include browser logs or page contents.
                if (process.ExitCode != 0)
                    throw new CliException("journey.capture_failed", "Reviewed Playwright journey failed. No evidence published.");
                if (response.Length > 8192)
                    throw new CliException("journey.capture_invalid", "Unexpectedly large browser response.");
                using var json = JsonDocument.Parse(response);
                return json.RootElement.GetProperty("stepIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new CliException("journey.timeout", "Journey capture exceeded 90 seconds.");
            }
            catch (JsonException)
            {
                throw new CliException("journey.capture_invalid", "Browser returned invalid evidence metadata.");
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
