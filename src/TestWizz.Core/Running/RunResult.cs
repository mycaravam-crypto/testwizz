using System.Text.Json.Nodes;

namespace TestWizz.Core.Running;

/// <summary>Overall outcome of running a proposal's tests in a container.</summary>
public enum RunStatus
{
    /// <summary>Every generated test ran and passed.</summary>
    Passed,

    /// <summary>The tests compiled and ran, and at least one failed (or none ran).</summary>
    Failed,

    /// <summary>The test project did not build.</summary>
    CompileError,

    /// <summary>A phase exceeded its time limit.</summary>
    Timeout,

    /// <summary>Dependencies could not be restored; usually a network or feed problem, not the proposal.</summary>
    RestoreFailed,
}

/// <param name="Name">Phase name: <c>restore</c>, <c>build</c> or <c>test</c>.</param>
public sealed record RunPhase(string Name, bool Network, int ExitCode, long DurationMs, bool TimedOut);

/// <param name="InProposal">The error is in a file the proposal adds, as opposed to existing repository code.</param>
public sealed record CompileError(string File, int Line, int Column, string Code, string Message, bool InProposal);

/// <param name="Name">Full display name from the runner, e.g. with theory arguments.</param>
/// <param name="Method">Test method name, used to link the result to the proposal's tests and scenarios.</param>
/// <param name="Outcome"><c>passed</c>, <c>failed</c>, <c>skipped</c> or <c>not-run</c>.</param>
public sealed record TestOutcome(
    string Name,
    string Method,
    string Outcome,
    long DurationMs,
    string? Message,
    string? StackTrace,
    IReadOnlyList<string> Scenarios);

/// <summary><c>testwizz.run/v1</c>: what happened when a proposal's tests were built and run in a container.</summary>
public sealed record RunResult(
    string ProposalId,
    RunStatus Status,
    string Runtime,
    string Image,
    IReadOnlyList<RunPhase> Phases,
    IReadOnlyList<string> StaleSources,
    IReadOnlyList<CompileError> CompileErrors,
    IReadOnlyList<TestOutcome> Tests)
{
    public const string Schema = "testwizz.run/v1";

    public int Passed => Tests.Count(t => t.Outcome == "passed");

    public int Failed => Tests.Count(t => t.Outcome == "failed");
}

public static class RunJson
{
    public static string StatusName(RunStatus status) => status switch
    {
        RunStatus.Passed => "passed",
        RunStatus.Failed => "failed",
        RunStatus.CompileError => "compile-error",
        RunStatus.Timeout => "timeout",
        RunStatus.RestoreFailed => "restore-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static JsonObject ToJson(RunResult r) => new()
    {
        ["schema"] = RunResult.Schema,
        ["proposalId"] = r.ProposalId,
        ["status"] = StatusName(r.Status),
        ["runtime"] = r.Runtime,
        ["image"] = r.Image,
        ["summary"] = new JsonObject
        {
            ["total"] = r.Tests.Count,
            ["passed"] = r.Passed,
            ["failed"] = r.Failed,
            ["compileErrors"] = r.CompileErrors.Count,
        },
        ["staleSources"] = new JsonArray([.. r.StaleSources.Select(s => (JsonNode)s)]),
        ["phases"] = new JsonArray([.. r.Phases.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name,
            ["network"] = p.Network,
            ["exitCode"] = p.ExitCode,
            ["durationMs"] = p.DurationMs,
            ["timedOut"] = p.TimedOut,
        })]),
        ["compileErrors"] = new JsonArray([.. r.CompileErrors.Select(e => (JsonNode)new JsonObject
        {
            ["file"] = e.File,
            ["line"] = e.Line,
            ["column"] = e.Column,
            ["code"] = e.Code,
            ["message"] = e.Message,
            ["inProposal"] = e.InProposal,
        })]),
        ["tests"] = new JsonArray([.. r.Tests.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["method"] = t.Method,
            ["outcome"] = t.Outcome,
            ["durationMs"] = t.DurationMs,
            ["message"] = t.Message,
            ["stackTrace"] = t.StackTrace,
            ["scenarios"] = new JsonArray([.. t.Scenarios.Select(s => (JsonNode)s)]),
        })]),
    };
}
