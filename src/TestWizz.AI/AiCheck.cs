using System.Diagnostics;
using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;

namespace TestWizz.AI;

public enum CheckStatus
{
    Ok,
    Warning,
    Failed,
    Skipped,
}

public sealed record AiCheckStep(string Name, CheckStatus Status, string Detail, ModelFailure? Failure = null);

public sealed record AiCheckReport(
    IReadOnlyList<AiCheckStep> Steps,
    string? ModelDigest,
    long? LatencyMs,
    int? PromptTokens,
    int? CompletionTokens)
{
    public bool Ok => Steps.All(s => s.Status != CheckStatus.Failed);

    public ModelFailure? Failure => Steps.FirstOrDefault(s => s.Status == CheckStatus.Failed)?.Failure;
}

/// <summary>
/// Verifies that the configured local model is usable: API key present, endpoint private, model installed, and a tiny
/// synthetic prompt answered with schema-valid JSON. Each step stops the remaining ones when it fails.
/// </summary>
public static class AiCheck
{
    public static readonly JsonChatRequest Probe = new(
        "You are a connectivity probe. Reply only with JSON that matches the provided schema.",
        "Set \"answer\" to the sum of 2 and 3.",
        "testwizz_probe",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["answer"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("answer"),
            ["additionalProperties"] = false,
        });

    public static async Task<AiCheckReport> RunAsync(
        AiSettings settings,
        Func<string, string?> getEnvironmentVariable,
        EndpointGuard guard,
        CancellationToken cancellationToken)
    {
        var steps = new List<AiCheckStep>();
        string? digest = null;
        long? latency = null;
        int? promptTokens = null, completionTokens = null;

        var remaining = new Queue<string>(["apiKey", "endpoint", "model", "structuredOutput"]);
        AiCheckReport Report()
        {
            while (remaining.TryDequeue(out var name))
            {
                steps.Add(new AiCheckStep(name, CheckStatus.Skipped, "Skipped after an earlier failure."));
            }

            return new AiCheckReport(steps, digest, latency, promptTokens, completionTokens);
        }

        void Fail(ModelClientException ex) =>
            steps.Add(new AiCheckStep(remaining.Dequeue(), CheckStatus.Failed, ex.Message, ex.Failure));

        void Pass(CheckStatus status, string detail) => steps.Add(new AiCheckStep(remaining.Dequeue(), status, detail));

        ModelClient client;
        try
        {
            client = ModelClient.Create(settings, getEnvironmentVariable, guard);
        }
        catch (ModelClientException ex)
        {
            Fail(ex);
            return Report();
        }

        using (client)
        {
            Pass(
                settings.ApiKeyEnv is null ? CheckStatus.Skipped : CheckStatus.Ok,
                settings.ApiKeyEnv is null ? "No API key configured." : $"{settings.ApiKeyEnv} is set.");

            try
            {
                var addresses = await guard.ResolveAsync(settings.Endpoint.DnsSafeHost, cancellationToken).ConfigureAwait(false);
                Pass(CheckStatus.Ok, $"{settings.Endpoint.DnsSafeHost} resolves to private address(es): {string.Join(", ", addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal))}.");
            }
            catch (ModelClientException ex)
            {
                Fail(ex);
                return Report();
            }

            try
            {
                var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
                if (models is null)
                {
                    Pass(CheckStatus.Skipped, "Server does not list models; availability is checked by the probe.");
                }
                else if (Find(models, settings.Model) is { } model)
                {
                    digest = model.Digest;
                    Pass(CheckStatus.Ok, $"Model '{model.Name}' is installed.");
                }
                else
                {
                    var installed = models.Count == 0 ? "none" : string.Join(", ", models.Select(m => m.Name).Order(StringComparer.Ordinal).Take(10));
                    var hint = settings.Provider == AiProvider.Ollama ? $" Run 'ollama pull {settings.Model}'." : "";
                    Fail(new ModelClientException(ModelFailure.ModelNotFound, $"Model '{settings.Model}' is not installed (installed: {installed}).{hint}"));
                    return Report();
                }
            }
            catch (ModelClientException ex)
            {
                Fail(ex);
                return Report();
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var response = await client.CompleteJsonAsync(Probe, cancellationToken).ConfigureAwait(false);
                latency = stopwatch.ElapsedMilliseconds;
                promptTokens = response.PromptTokens;
                completionTokens = response.CompletionTokens;
                var correct = response.Content["answer"]?.GetValue<long>() == 5;
                Pass(
                    correct ? CheckStatus.Ok : CheckStatus.Warning,
                    correct
                        ? "Model returned schema-valid JSON."
                        : "Model returned schema-valid JSON but the wrong answer; consider a stronger model.");
            }
            catch (ModelClientException ex)
            {
                Fail(ex);
            }
        }

        return Report();
    }

    /// <summary>Ollama reports <c>name:latest</c> for models configured without a tag.</summary>
    private static ModelInfo? Find(IReadOnlyList<ModelInfo> models, string name) =>
        models.FirstOrDefault(m => m.Name == name)
        ?? (name.Contains(':', StringComparison.Ordinal) ? null : models.FirstOrDefault(m => m.Name == name + ":latest"));
}
