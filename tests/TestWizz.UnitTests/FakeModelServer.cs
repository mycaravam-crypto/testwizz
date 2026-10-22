using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TestWizz.UnitTests;

public sealed record RecordedRequest(string Method, string Path, string? Authorization, string Body);

/// <summary>Loopback HTTP(S) server that plays a model server. Routes map "METHOD /path" to a handler.</summary>
public sealed class FakeModelServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Dictionary<string, Func<HttpContext, Task>> _routes = new(StringComparer.Ordinal);

    private FakeModelServer(WebApplication app) => _app = app;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public Uri BaseUri { get; private set; } = null!;

    public static async Task<FakeModelServer> StartAsync(bool https = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (https)
            {
                listen.UseHttps(CreateSelfSignedCertificate());
            }
        }));

        var app = builder.Build();
        var server = new FakeModelServer(app);
        app.Run(server.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.BaseUri = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal));
        return server;
    }

    public FakeModelServer On(string route, Func<HttpContext, Task> handler)
    {
        _routes[route] = handler;
        return this;
    }

    public FakeModelServer OnJson(string route, string json, int status = 200) => On(route, async context =>
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    });

    /// <summary>Standard Ollama responses: one installed model and a chat reply with the given message content.</summary>
    public FakeModelServer AsOllama(string model, string content) => this
        .OnJson("GET /api/tags", new JsonObject
        {
            ["models"] = new JsonArray(new JsonObject { ["name"] = model, ["digest"] = "sha256:abc123" }),
        }.ToJsonString())
        .OnJson("POST /api/chat", new JsonObject
        {
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["prompt_eval_count"] = 42,
            ["eval_count"] = 7,
        }.ToJsonString());

    /// <summary>Standard OpenAI-compatible responses under <c>/v1</c>.</summary>
    public FakeModelServer AsOpenAi(string model, string content) => this
        .OnJson("GET /v1/models", new JsonObject
        {
            ["object"] = "list",
            ["data"] = new JsonArray(new JsonObject { ["id"] = model, ["object"] = "model" }),
        }.ToJsonString())
        .OnJson("POST /v1/chat/completions", new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 40, ["completion_tokens"] = 5 },
        }.ToJsonString());

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        var route = $"{context.Request.Method} {context.Request.Path}";
        Requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path, context.Request.Headers.Authorization.FirstOrDefault(), body));
        if (_routes.TryGetValue(route, out var handler))
        {
            await handler(context);
        }
        else
        {
            context.Response.StatusCode = 404;
        }
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}
