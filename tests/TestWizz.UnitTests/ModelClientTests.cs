using System.Text.Json.Nodes;
using TestWizz.AI;

namespace TestWizz.UnitTests;

public sealed class ModelClientTests : IAsyncLifetime
{
    private const string Secret = "sk-test-secret-0123456789";

    private FakeModelServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await FakeModelServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private Uri OpenAiBase => new(_server.BaseUri, "v1");

    private static string? Env(string name) => name == "TESTWIZZ_KEY" ? Secret : null;

    private static Task<AiCheckReport> Check(Core.Configuration.AiSettings settings) =>
        AiCheck.RunAsync(settings, Env, new EndpointGuard(), TestContext.Current.CancellationToken);

    private static AiCheckStep Step(AiCheckReport report, string name) => report.Steps.Single(s => s.Name == name);

    [Fact]
    public async Task Ollama_Success()
    {
        _server.AsOllama("qwen2.5-coder:14b", """{"answer":5}""");

        var report = await Check(TestSettings.Ollama(_server.BaseUri));

        Assert.True(report.Ok);
        Assert.All(report.Steps.Skip(1), s => Assert.Equal(CheckStatus.Ok, s.Status));
        Assert.Equal(CheckStatus.Skipped, Step(report, "apiKey").Status);
        Assert.Equal("sha256:abc123", report.ModelDigest);
        Assert.Equal(42, report.PromptTokens);
        Assert.Equal(7, report.CompletionTokens);
        Assert.NotNull(report.LatencyMs);
    }

    [Fact]
    public async Task Ollama_SendsSchemaStreamOffAndContextSize()
    {
        _server.AsOllama("qwen2.5-coder:14b", """{"answer":5}""");

        await Check(TestSettings.Ollama(_server.BaseUri));

        var chat = JsonNode.Parse(_server.Requests.Single(r => r.Path == "/api/chat").Body)!;
        Assert.Equal("qwen2.5-coder:14b", (string)chat["model"]!);
        Assert.False((bool)chat["stream"]!);
        Assert.Equal(16000, (int)chat["options"]!["num_ctx"]!);
        Assert.Equal(0, (int)chat["options"]!["temperature"]!);
        Assert.True(JsonNode.DeepEquals(AiCheck.Probe.Schema, chat["format"]));
    }

    [Fact]
    public async Task Ollama_UntaggedModelMatchesLatest()
    {
        _server.AsOllama("llama3:latest", """{"answer":5}""");

        var report = await Check(TestSettings.Ollama(_server.BaseUri, "llama3"));

        Assert.Equal(CheckStatus.Ok, Step(report, "model").Status);
    }

    [Fact]
    public async Task OpenAi_Success_SendsBearerKeyAndJsonSchema()
    {
        _server.AsOpenAi("codestral", """{"answer":5}""");

        var report = await Check(TestSettings.OpenAi(OpenAiBase, apiKeyEnv: "TESTWIZZ_KEY"));

        Assert.True(report.Ok);
        Assert.Equal(CheckStatus.Ok, Step(report, "apiKey").Status);
        Assert.DoesNotContain(Secret, Step(report, "apiKey").Detail, StringComparison.Ordinal);
        Assert.All(_server.Requests, r => Assert.Equal($"Bearer {Secret}", r.Authorization));
        Assert.All(_server.Requests, r => Assert.DoesNotContain(Secret, r.Body, StringComparison.Ordinal));
        var chat = JsonNode.Parse(_server.Requests.Single(r => r.Path == "/v1/chat/completions").Body)!;
        Assert.Equal("json_schema", (string)chat["response_format"]!["type"]!);
        Assert.Equal(40, report.PromptTokens);
    }

    [Fact]
    public async Task OpenAi_WithoutModelListing_SkipsModelStep()
    {
        _server.AsOpenAi("codestral", """{"answer":5}""").OnJson("GET /v1/models", "{}", status: 404);

        var report = await Check(TestSettings.OpenAi(OpenAiBase));

        Assert.True(report.Ok);
        Assert.Equal(CheckStatus.Skipped, Step(report, "model").Status);
    }

    [Fact]
    public async Task MissingApiKey_FailsWithoutAnyRequest()
    {
        var report = await Check(TestSettings.OpenAi(OpenAiBase, apiKeyEnv: "UNSET_KEY"));

        Assert.False(report.Ok);
        Assert.Equal(ModelFailure.MissingApiKey, report.Failure);
        Assert.Contains("UNSET_KEY", Step(report, "apiKey").Detail, StringComparison.Ordinal);
        Assert.All(report.Steps.Skip(1), s => Assert.Equal(CheckStatus.Skipped, s.Status));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task MissingModel_IsReportedWithInstalledModels()
    {
        _server.AsOllama("llama3:8b", """{"answer":5}""");

        var report = await Check(TestSettings.Ollama(_server.BaseUri, "qwen2.5-coder:14b"));

        Assert.Equal(ModelFailure.ModelNotFound, report.Failure);
        Assert.Contains("llama3:8b", Step(report, "model").Detail, StringComparison.Ordinal);
        Assert.Contains("ollama pull", Step(report, "model").Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Requests, r => r.Path == "/api/chat");
    }

    [Fact]
    public async Task Ollama_ChatNotFound_IsModelNotFound()
    {
        _server.OnJson("POST /api/chat", """{"error":"model not found"}""", status: 404);

        using var client = ModelClient.Create(TestSettings.Ollama(_server.BaseUri), _ => null);
        var ex = await Assert.ThrowsAsync<ModelClientException>(() => client.CompleteJsonAsync(AiCheck.Probe, TestContext.Current.CancellationToken));

        Assert.Equal(ModelFailure.ModelNotFound, ex.Failure);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthFailure_IsReportedWithoutTheKey(int status)
    {
        _server.OnJson("GET /v1/models", """{"error":"bad key"}""", status);

        var report = await Check(TestSettings.OpenAi(OpenAiBase, apiKeyEnv: "TESTWIZZ_KEY"));

        Assert.Equal(ModelFailure.AuthFailed, report.Failure);
        Assert.All(report.Steps, s => Assert.DoesNotContain(Secret, s.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerError_IsHttpError()
    {
        _server.OnJson("GET /api/tags", "{}", status: 500);

        var report = await Check(TestSettings.Ollama(_server.BaseUri));

        Assert.Equal(ModelFailure.HttpError, report.Failure);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed()
    {
        _server.On("GET /api/tags", context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "http://8.8.8.8/api/tags";
            return Task.CompletedTask;
        });

        var report = await Check(TestSettings.Ollama(_server.BaseUri));

        Assert.Equal(ModelFailure.RedirectRejected, report.Failure);
        Assert.Single(_server.Requests);
    }

    [Theory]
    [InlineData("", ModelFailure.EmptyResponse)]
    [InlineData("   ", ModelFailure.EmptyResponse)]
    [InlineData("I think the answer is 5", ModelFailure.InvalidResponse)]
    [InlineData("""{"answer":"five"}""", ModelFailure.InvalidResponse)]
    [InlineData("""{"answer":5,"extra":true}""", ModelFailure.InvalidResponse)]
    [InlineData("""{}""", ModelFailure.InvalidResponse)]
    [InlineData("""[5]""", ModelFailure.InvalidResponse)]
    public async Task BadModelContent_IsRejected(string content, ModelFailure expected)
    {
        _server.AsOllama("qwen2.5-coder:14b", content);

        var report = await Check(TestSettings.Ollama(_server.BaseUri));

        Assert.Equal(expected, report.Failure);
        Assert.Equal(CheckStatus.Failed, Step(report, "structuredOutput").Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>not json</html>")]
    public async Task BadServerBody_IsRejected(string body)
    {
        _server.OnJson("GET /api/tags", body);

        var report = await Check(TestSettings.Ollama(_server.BaseUri));

        Assert.Contains(report.Failure, new ModelFailure?[] { ModelFailure.EmptyResponse, ModelFailure.InvalidResponse });
    }

    [Fact]
    public async Task MissingMessageContent_IsEmptyResponse()
    {
        _server.AsOllama("qwen2.5-coder:14b", "x").OnJson("POST /api/chat", """{"done":true}""");

        var report = await Check(TestSettings.Ollama(_server.BaseUri));

        Assert.Equal(ModelFailure.EmptyResponse, report.Failure);
    }

    [Fact]
    public async Task CodeFencedJson_IsAccepted()
    {
        _server.AsOllama("qwen2.5-coder:14b", "```json\n{\"answer\":5}\n```");

        Assert.True((await Check(TestSettings.Ollama(_server.BaseUri))).Ok);
    }

    [Fact]
    public async Task WrongAnswer_IsWarningNotFailure()
    {
        _server.AsOllama("qwen2.5-coder:14b", """{"answer":6}""");

        var report = await Check(TestSettings.Ollama(_server.BaseUri));

        Assert.True(report.Ok);
        Assert.Equal(CheckStatus.Warning, Step(report, "structuredOutput").Status);
    }

    [Fact]
    public async Task SlowServer_TimesOut()
    {
        _server.AsOllama("qwen2.5-coder:14b", """{"answer":5}""").On("POST /api/chat", async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
        });

        var report = await Check(TestSettings.Ollama(_server.BaseUri, timeoutSeconds: 1));

        Assert.Equal(ModelFailure.Timeout, report.Failure);
        Assert.Contains("ai.timeoutSeconds", Step(report, "structuredOutput").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_IsReportedAsCancelled()
    {
        _server.AsOllama("qwen2.5-coder:14b", """{"answer":5}""").On("POST /api/chat", async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        var report = await AiCheck.RunAsync(TestSettings.Ollama(_server.BaseUri), Env, new EndpointGuard(), cancellation.Token);

        Assert.Equal(ModelFailure.Cancelled, report.Failure);
    }

    [Fact]
    public async Task NothingListening_IsConnectionFailed()
    {
        var port = _server.BaseUri.Port;
        await _server.DisposeAsync();
        _server = await FakeModelServer.StartAsync();

        var report = await Check(TestSettings.Ollama(new Uri($"http://127.0.0.1:{port}")));

        Assert.Equal(ModelFailure.ConnectionFailed, report.Failure);
    }

    [Fact]
    public async Task UntrustedCertificate_IsTlsFailed()
    {
        await using var https = await FakeModelServer.StartAsync(https: true);
        https.AsOllama("qwen2.5-coder:14b", """{"answer":5}""");

        var report = await Check(TestSettings.Ollama(https.BaseUri));

        Assert.Equal(ModelFailure.TlsFailed, report.Failure);
        Assert.Empty(https.Requests);
    }

    [Fact]
    public async Task PublicEndpoint_IsRejectedWithoutNetworkAccess()
    {
        var report = await Check(TestSettings.Ollama(new Uri("http://8.8.8.8:11434")));

        Assert.Equal(ModelFailure.EndpointNotPrivate, report.Failure);
        Assert.Equal(CheckStatus.Failed, Step(report, "endpoint").Status);
    }
}
