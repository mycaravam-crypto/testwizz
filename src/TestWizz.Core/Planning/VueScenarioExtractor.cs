using System.Globalization;
using TestWizz.Core.Context;

namespace TestWizz.Core.Planning;

/// <summary>
/// Creates conservative, evidence-linked scenarios from statically extracted Vue emit call sites.
/// It does not infer runtime reachability or payload values from a call's existence.
/// </summary>
public sealed class VueScenarioExtractor : IScenarioExtractor
{
    public const string ReviewReason = "The static call site proves an emit invocation exists, but runtime reachability and payload values require review.";

    public string Version => "vue-extractor/1";

    public ExtractionResult Extract(ContextDocument context)
    {
        if (!string.Equals(context.Stack, "vue", StringComparison.Ordinal))
        {
            throw new ContextException(
                "plan.unsupported_stack",
                $"Vue scenario extraction requires stack 'vue'; received '{context.Stack}'.");
        }

        var evidence = new List<Evidence>();
        var scenarios = new List<Scenario>();
        var findings = new List<PlanFinding>();
        var calls = context.Members
            .Where(member => member.Id.StartsWith("emit-call:", StringComparison.Ordinal))
            .OrderBy(member => member.Line)
            .ThenBy(member => member.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var call in calls)
        {
            var separator = call.Id.LastIndexOf(':');
            if (separator <= "emit-call:".Length || !int.TryParse(
                    call.Id.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var line))
            {
                findings.Add(new("vue-invalid-emit-evidence", $"Could not parse emit call-site identity '{call.Id}'."));
                continue;
            }

            var eventName = call.Id["emit-call:".Length..separator];
            var payloadMarker = "(payload arguments: ";
            var payloadStart = call.Signature.LastIndexOf(payloadMarker, StringComparison.Ordinal);
            var payloadEnd = call.Signature.LastIndexOf(')');
            if (payloadStart < 0 || payloadEnd <= payloadStart + payloadMarker.Length ||
                !int.TryParse(call.Signature.AsSpan(payloadStart + payloadMarker.Length,
                    payloadEnd - payloadStart - payloadMarker.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var payloadCount))
            {
                findings.Add(new("vue-invalid-emit-evidence", $"Could not parse payload argument count for '{call.Id}'."));
                continue;
            }

            var evidenceId = $"E{evidence.Count + 1}";
            evidence.Add(new Evidence(evidenceId, "call", line, call.Signature));
            scenarios.Add(new Scenario(
                $"S{scenarios.Count + 1}",
                ScenarioCategory.StateTransition,
                $"Emits '{eventName}' at line {line}",
                $"Reach the static emit call site at line {line}; payload values are not inferred",
                $"Calls emit('{eventName}', ...) with {payloadCount} payload argument(s).",
                [evidenceId],
                "deterministic",
                true,
                ReviewReason,
                []));
        }

        if (scenarios.Count == 0)
        {
            findings.Add(new(
                "no-static-emit-calls",
                "No statically identifiable Vue emit call sites were found. No event scenarios were invented."));
        }

        return new ExtractionResult(evidence, scenarios, findings);
    }
}
