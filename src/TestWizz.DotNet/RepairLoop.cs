using System.Globalization;
using System.Text.Json.Nodes;
using TestWizz.AI;
using TestWizz.Core.Configuration;
using TestWizz.Core.Context;
using TestWizz.Core.Generation;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;

namespace TestWizz.DotNet;

/// <param name="Attempts">Repairs requested from the model, accepted or not.</param>
/// <param name="StillFailing">Tests that fail after the last attempt: a possible bug in the code or a wrong test.</param>
public sealed record RepairLoopResult(RunOutput Final, string RunDirectory, int Attempts, int Accepted, IReadOnlyList<string> StillFailing);

/// <summary>
/// Runs a proposal and, while it fails to compile or has failing tests, asks the model for at most
/// <c>maxAttempts</c> repairs. Only the proposed test files change; a repair that removes tests, links or assertions,
/// weakens an assertion or adds a skip is rejected. Every run and repair is recorded in the manifest history.
/// </summary>
public sealed class RepairLoop(DotNetTestRunner runner, Func<ModelClient>? clientFactory, int maxAttempts, TimeProvider? time = null)
{
    public const string StillFailingLabel = "possible bug in the code or a wrong test";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<RepairLoopResult> RunAsync(string root, string bundle, RunOptions options, CancellationToken cancellationToken)
    {
        var proposal = ProposalReader.Load(bundle);
        var output = await runner.RunAsync(root, proposal, options, cancellationToken).ConfigureAwait(false);
        var runDirectory = Record(bundle, output, attempt: 0);
        var rejections = new List<string>();
        int attempts = 0, accepted = 0;
        ModelClient? client = null;
        try
        {
            while (attempts < maxAttempts && clientFactory is not null && output.Result.Status is RunStatus.Failed or RunStatus.CompileError)
            {
                if (output.Result.CompileErrors.FirstOrDefault(e => !e.InProposal) is { } foreign)
                {
                    Note(bundle, "repair-skipped", $"Compile errors outside the proposed files ({foreign.File}); a test repair cannot fix them.");
                    break;
                }

                ContextDocument context;
                RoslynContextProvider provider;
                try
                {
                    provider = RoslynContextProvider.Load(root, cancellationToken);
                    context = provider.GetContext((string)proposal.Manifest["target"]!["id"]!, (string?)proposal.Manifest["toolVersion"] ?? "unknown");
                }
                catch (ContextException ex)
                {
                    Note(bundle, "repair-skipped", $"The target can no longer be found: {ex.Message}");
                    break;
                }

                attempts++;
                client ??= clientFactory();
                RepairProposal repair;
                try
                {
                    repair = await XUnitRepairer.RepairAsync(client, context, proposal, output.Result, rejections, cancellationToken).ConfigureAwait(false);
                }
                catch (ModelClientException ex)
                {
                    Note(bundle, "repair-failed", $"Attempt {attempts}: the model call failed ({ex.Code}): {ex.Message}");
                    break;
                }

                var before = proposal.Files[0];
                var check = XUnitRepairer.Check(before.Content, repair.Code);
                var (tests, findings) = Validate(root, provider, context, proposal, before.Path, repair.Code);
                var reasons = check.Reasons
                    .Concat(findings.Where(f => f.Severity == "error").Select(f => $"{f.Code}{(f.Line is { } line ? $" (line {line})" : "")}: {f.Message}"))
                    .ToList();

                var entry = Entry("repair", $"Attempt {attempts}: ");
                entry["attempt"] = attempts;
                entry["generator"] = new JsonObject
                {
                    ["provider"] = ConfigJson.ProviderName(client.Settings.Provider),
                    ["model"] = client.Settings.Model,
                    ["promptTemplate"] = XUnitRepairer.PromptTemplate,
                    ["promptSha256"] = XUnitRepairer.PromptSha256,
                };
                entry["changes"] = Array(repair.Changes);
                entry["suspectedBugs"] = new JsonArray([.. repair.SuspectedBugs.Select(b => (JsonNode)new JsonObject { ["test"] = b.Test, ["reason"] = b.Reason })]);
                if (reasons.Count > 0)
                {
                    entry["accepted"] = false;
                    entry["reasons"] = Array(reasons);
                    entry["summary"] = $"Attempt {attempts}: repair rejected. {string.Join(" ", reasons)}";
                    ProposalBundle.AppendHistory(bundle, entry);
                    rejections = reasons;
                    continue;
                }

                accepted++;
                rejections = [];
                ProposalBundle.UpdateFiles(bundle, attempts, [new GeneratedFile(before.Path, repair.Code)], tests, findings);
                entry["accepted"] = true;
                entry["changedExpectations"] = Array(check.ChangedExpectations);
                entry["summary"] = $"Attempt {attempts}: repair accepted. {string.Join(" ", repair.Changes)}"
                    + (check.ChangedExpectations.Count > 0 ? $" Review changed expectations in: {string.Join(", ", check.ChangedExpectations)}." : "");
                ProposalBundle.AppendHistory(bundle, entry);

                proposal = ProposalReader.Load(bundle);
                output = await runner.RunAsync(root, proposal, options, cancellationToken).ConfigureAwait(false);
                runDirectory = Record(bundle, output, attempts);
            }
        }
        finally
        {
            client?.Dispose();
        }

        var stillFailing = output.Result.Status == RunStatus.Failed
            ? output.Result.Tests.Where(t => t.Outcome is "failed" or "not-run").Select(t => t.Name).ToList()
            : [];
        if (stillFailing.Count > 0)
        {
            var entry = Entry("result", $"{stillFailing.Count} test(s) still fail ({StillFailingLabel}); review before applying: {string.Join(", ", stillFailing)}.");
            entry["label"] = StillFailingLabel;
            entry["tests"] = Array(stillFailing);
            ProposalBundle.AppendHistory(bundle, entry);
        }

        return new RepairLoopResult(output, runDirectory, attempts, accepted, stillFailing);
    }

    /// <summary>The same static checks <c>generate</c> applies, with the proposal's own file, class and namespace.</summary>
    private static (IReadOnlyList<GeneratedTest> Tests, IReadOnlyList<GenerationFinding> Findings) Validate(
        string root, RoslynContextProvider provider, ContextDocument context, LoadedProposal proposal, string path, string code)
    {
        var recorded = proposal.Conventions;
        var conventions = XUnitConventions.Detect(root, context) with
        {
            FilePath = path,
            ClassName = (string?)recorded["className"] ?? "",
            Namespace = (string?)recorded["namespace"] ?? "",
        };
        var scenarioIds = (proposal.Manifest["scenarios"] as JsonArray ?? []).Select(s => (string?)s?["id"]).OfType<string>().ToList();
        var (tests, validation) = XUnitValidator.Validate(provider, code, path, conventions, scenarioIds);
        var findings = new List<GenerationFinding>(conventions.Findings);
        findings.AddRange(validation);
        findings.AddRange(ProposalChecks.Check(root, [new GeneratedFile(path, code)], tests));
        return (tests, findings);
    }

    private string Record(string bundle, RunOutput output, int attempt)
    {
        var r = output.Result;
        var directory = ProposalBundle.WriteRun(bundle, RunJson.ToJson(r), output.Logs);
        var entry = Entry("run", $"{(attempt == 0 ? "Initial run" : $"Run after attempt {attempt}")}: {RunJson.StatusName(r.Status)} ({r.Passed} passed, {r.Failed} failed, {r.CompileErrors.Count} compile errors).");
        entry["attempt"] = attempt;
        entry["status"] = RunJson.StatusName(r.Status);
        entry["passed"] = r.Passed;
        entry["failed"] = r.Failed;
        entry["compileErrors"] = r.CompileErrors.Count;
        entry["image"] = r.Image;
        ProposalBundle.AppendHistory(bundle, entry);
        return directory;
    }

    private void Note(string bundle, string kind, string summary) => ProposalBundle.AppendHistory(bundle, Entry(kind, summary));

    private JsonObject Entry(string kind, string summary) => new()
    {
        ["at"] = _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["kind"] = kind,
        ["summary"] = summary,
    };

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)i)]);
}
