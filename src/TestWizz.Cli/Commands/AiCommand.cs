using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TestWizz.AI;
using TestWizz.Core.Configuration;

namespace TestWizz.Cli.Commands;

public static class AiCommand
{
    public static CommandDefinition Definition { get; } = new(
        "ai",
        "Check that the configured local model is reachable, private and returns valid JSON.",
        "testwizz ai check [path]",
        Options: [],
        MaxArguments: 2,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        if (context.Arguments.Count == 0 || context.Arguments[0] != "check")
        {
            throw new CliException("usage", "Expected 'testwizz ai check [path]'. Run 'testwizz help ai' for usage.");
        }

        var directory = Path.GetFullPath(context.Arguments.Count > 1 ? context.Arguments[1] : ".", context.WorkingDirectory);
        var settings = ConfigCommand.Load(Path.Combine(directory, TestWizzConfig.FileName)).Ai;

        var report = AiCheck.RunAsync(settings, context.GetEnvironmentVariable, new EndpointGuard(), context.CancellationToken)
            .GetAwaiter().GetResult();

        if (report.Failure is { } failure && ModelClientException.IsConfigurationFailure(failure))
        {
            var step = report.Steps.First(s => s.Status == CheckStatus.Failed);
            throw new CliException($"ai.{ModelClientException.FailureCode(failure)}", step.Detail);
        }

        context.Output.Result(
            "ai",
            ToJson(settings, report),
            ToText(settings, report),
            report.Ok ? "ok" : "failed");
        return report.Ok ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static JsonObject ToJson(AiSettings settings, AiCheckReport report)
    {
        var steps = new JsonArray();
        foreach (var step in report.Steps)
        {
            steps.Add(new JsonObject
            {
                ["name"] = step.Name,
                ["status"] = StatusName(step.Status),
                ["code"] = step.Failure is { } f ? ModelClientException.FailureCode(f) : null,
                ["detail"] = step.Detail,
            });
        }

        return new JsonObject
        {
            ["ok"] = report.Ok,
            ["provider"] = ConfigJson.ProviderName(settings.Provider),
            ["endpoint"] = settings.Endpoint.OriginalString,
            ["model"] = settings.Model,
            ["modelDigest"] = report.ModelDigest,
            ["steps"] = steps,
            ["latencyMs"] = report.LatencyMs,
            ["promptTokens"] = report.PromptTokens,
            ["completionTokens"] = report.CompletionTokens,
        };
    }

    private static string ToText(AiSettings settings, AiCheckReport report)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"AI check: {ConfigJson.ProviderName(settings.Provider)} model '{settings.Model}' at {settings.Endpoint.OriginalString}\n");
        foreach (var step in report.Steps)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {StatusName(step.Status),-8} {step.Name,-17} {step.Detail}\n");
        }

        text.Append(report.Ok ? "Result: OK" : "Result: FAILED");
        if (report.LatencyMs is { } latency)
        {
            text.Append(CultureInfo.InvariantCulture, $" (probe took {latency} ms)");
        }

        text.Append('\n');
        return text.ToString();
    }

    private static string StatusName(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "ok",
        CheckStatus.Warning => "warning",
        CheckStatus.Failed => "failed",
        _ => "skipped",
    };
}
