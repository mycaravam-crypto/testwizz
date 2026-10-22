using System.Globalization;
using System.Text;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;
using TestWizz.DotNet;

namespace TestWizz.Cli.Commands;

public static class RunCommand
{
    private const string ProposalOption = "--proposal";
    private const string ImageOption = "--image";
    private const string TimeoutOption = "--timeout";
    private const string OutOption = "--out";
    private const int DefaultTimeoutSeconds = 600;

    public static CommandDefinition Definition { get; } = new(
        "run",
        "Build and run a proposal's tests in a Docker/Podman container (network off for build and test). Never runs on the host and never changes the repository.",
        "testwizz run [path] --proposal <id|dir> [--image <image>] [--timeout <seconds>] [--out <dir>]",
        Options:
        [
            new(ProposalOption, "Proposal id (a folder in the output directory) or the bundle directory itself.", "id"),
            new(ImageOption, "Container image (default: mcr.microsoft.com/dotnet/sdk:<version> from the target framework).", "image"),
            new(TimeoutOption, $"Time limit for all phases together, 30-7200 seconds (default {DefaultTimeoutSeconds}).", "seconds"),
            new(OutOption, "Directory that holds proposal bundles (default: 'output' in testwizz.json).", "dir"),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        if (!context.Options.TryGetValue(ProposalOption, out var reference) || string.IsNullOrWhiteSpace(reference))
        {
            throw new CliException("usage", $"Missing {ProposalOption} <id>. Usage: {Definition.Usage}");
        }

        var timeoutSeconds = DefaultTimeoutSeconds;
        if (context.Options.TryGetValue(TimeoutOption, out var timeoutText)
            && (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) || timeoutSeconds is < 30 or > 7200))
        {
            throw new CliException("usage", $"{TimeoutOption} must be a whole number of seconds between 30 and 7200.");
        }

        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var output = context.Options.TryGetValue(OutOption, out var outText) && !string.IsNullOrWhiteSpace(outText)
            ? Path.GetFullPath(outText, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);

        RunOutput run;
        LoadedProposal proposal;
        string runDirectory;
        try
        {
            proposal = ProposalReader.Load(ProposalReader.Resolve(reference, output, context.WorkingDirectory));
            if (proposal.Stack != "dotnet")
            {
                throw new CliException("run.unsupported_stack", $"Running '{proposal.Stack}' proposals is not supported yet.");
            }

            var runtime = context.Runtime(config.Run.Runtime);
            if (runtime.CheckAsync(context.CancellationToken).GetAwaiter().GetResult() is { } problem)
            {
                throw new CliException(
                    "run.no_container_runtime",
                    $"No usable container runtime: {problem} TestWizz never runs generated tests on the host; start {runtime.Name} or set run.runtime in testwizz.json.");
            }

            var image = context.Options.TryGetValue(ImageOption, out var imageText) && !string.IsNullOrWhiteSpace(imageText) ? imageText : null;
            run = new DotNetTestRunner(runtime)
                .RunAsync(root, proposal, new RunOptions(image, TimeSpan.FromSeconds(timeoutSeconds), ContainerLimits.Default), context.CancellationToken)
                .GetAwaiter().GetResult();
            runDirectory = ProposalBundle.WriteRun(proposal.Directory, RunJson.ToJson(run.Result), run.Logs);
        }
        catch (ProposalException ex)
        {
            throw new CliException(ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw new CliException("cancelled", "The run was cancelled; its containers were removed.");
        }

        var result = run.Result;
        var json = RunJson.ToJson(result);
        json["edited"] = new System.Text.Json.Nodes.JsonArray([.. proposal.Edited.Select(e => (System.Text.Json.Nodes.JsonNode)e)]);
        json["path"] = runDirectory;
        var exit = result.Status switch
        {
            RunStatus.Passed => ExitCodes.Success,
            RunStatus.RestoreFailed => ExitCodes.Error,
            _ => ExitCodes.ValidationFailed,
        };
        context.Output.Result("run", json, ToText(result, proposal, runDirectory), exit == ExitCodes.Success ? "ok" : "failed");
        return exit;
    }

    private static string ToText(RunResult r, LoadedProposal proposal, string runDirectory)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Run of {r.ProposalId} in {r.Runtime} ({r.Image})\n");
        foreach (var phase in r.Phases)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {phase.Name,-8} network {(phase.Network ? "on " : "off")}  exit {phase.ExitCode,3}  {phase.DurationMs / 1000.0:0.0}s{(phase.TimedOut ? "  TIMED OUT" : "")}\n");
        }

        foreach (var stale in r.StaleSources)
        {
            text.Append(CultureInfo.InvariantCulture, $"warning: {stale} changed since the proposal was generated.\n");
        }

        foreach (var edited in proposal.Edited)
        {
            text.Append(CultureInfo.InvariantCulture, $"note: {edited} was edited in the bundle after generation.\n");
        }

        if (r.CompileErrors.Count > 0)
        {
            text.Append("\nCompile errors:\n");
            foreach (var e in r.CompileErrors)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {e.File}({e.Line},{e.Column}): {e.Code} {e.Message}\n");
            }
        }

        if (r.Tests.Count > 0)
        {
            text.Append("\nTests:\n");
            foreach (var t in r.Tests)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {t.Outcome,-8} {t.Name}{(t.Scenarios.Count > 0 ? $" ({string.Join(", ", t.Scenarios)})" : "")}\n");
                if (t.Outcome == "failed" && t.Message is { } message)
                {
                    text.Append(CultureInfo.InvariantCulture, $"           {message.Split('\n')[0].Trim()}\n");
                }
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"\nResult: {RunJson.StatusName(r.Status).ToUpperInvariant()} · {r.Passed} passed, {r.Failed} failed. The repository was not changed.\n");
        text.Append(CultureInfo.InvariantCulture, $"Logs: {runDirectory}\n");
        return text.ToString();
    }
}
