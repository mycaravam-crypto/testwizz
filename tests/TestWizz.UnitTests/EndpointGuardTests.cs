using System.Net;
using System.Net.Sockets;
using TestWizz.AI;
using TestWizz.Core.Configuration;

namespace TestWizz.UnitTests;

public class EndpointGuardTests
{
    private static EndpointGuard GuardResolving(params string[] addresses) =>
        new((_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray()));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.1.1")]
    public void IsPrivate_AcceptsLocalAddresses(string address)
    {
        Assert.True(EndpointGuard.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPrivate_RejectsEverythingElse(string address)
    {
        Assert.False(EndpointGuard.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Resolve_PublicName_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<ModelClientException>(() => GuardResolving("8.8.8.8").ResolveAsync("models.example.com", TestContext.Current.CancellationToken));

        Assert.Equal(ModelFailure.EndpointNotPrivate, ex.Failure);
    }

    [Fact]
    public async Task Resolve_MixedPrivateAndPublic_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<ModelClientException>(() => GuardResolving("192.168.1.5", "203.0.113.9").ResolveAsync("gpu.lan", TestContext.Current.CancellationToken));

        Assert.Equal(ModelFailure.EndpointNotPrivate, ex.Failure);
    }

    [Fact]
    public async Task Resolve_PrivateName_ReturnsAddresses()
    {
        var addresses = await GuardResolving("192.168.1.5").ResolveAsync("gpu.lan", TestContext.Current.CancellationToken);

        Assert.Equal([IPAddress.Parse("192.168.1.5")], addresses);
    }

    [Fact]
    public async Task Resolve_IpLiteral_DoesNotUseDns()
    {
        var guard = new EndpointGuard((_, _) => throw new InvalidOperationException("DNS must not be used"));

        Assert.Equal([IPAddress.IPv6Loopback], await guard.ResolveAsync("[::1]", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ModelClientException>(() => guard.ResolveAsync("8.8.8.8", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_DnsFailure_IsReported()
    {
        var failing = new EndpointGuard((_, _) => throw new SocketException((int)SocketError.HostNotFound));
        var empty = GuardResolving();

        Assert.Equal(ModelFailure.DnsFailed, (await Assert.ThrowsAsync<ModelClientException>(() => failing.ResolveAsync("nope.lan", TestContext.Current.CancellationToken))).Failure);
        Assert.Equal(ModelFailure.DnsFailed, (await Assert.ThrowsAsync<ModelClientException>(() => empty.ResolveAsync("nope.lan", TestContext.Current.CancellationToken))).Failure);
    }

    [Fact]
    public void Handler_DisablesProxyAndRedirects()
    {
        using var handler = new EndpointGuard().CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public async Task Client_ConnectsToTheValidatedAddress()
    {
        await using var server = await FakeModelServer.StartAsync();
        server.AsOllama("m", """{"answer":5}""");
        var settings = TestSettings.Ollama(new Uri($"http://gpu.lan:{server.BaseUri.Port}"), "m");

        using var client = ModelClient.Create(settings, _ => null, GuardResolving("127.0.0.1"));
        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("m", Assert.Single(models!).Name);
    }

    [Fact]
    public async Task Client_PublicResolution_IsRejectedBeforeConnecting()
    {
        var settings = TestSettings.Ollama(new Uri("http://models.example.com:11434"), "m");

        using var client = ModelClient.Create(settings, _ => null, GuardResolving("8.8.8.8"));
        var ex = await Assert.ThrowsAsync<ModelClientException>(() => client.ListModelsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ModelFailure.EndpointNotPrivate, ex.Failure);
    }
}

internal static class TestSettings
{
    public static AiSettings Ollama(Uri endpoint, string model = "qwen2.5-coder:14b", string? apiKeyEnv = null, int timeoutSeconds = 10) =>
        new(AiProvider.Ollama, endpoint, model, apiKeyEnv, timeoutSeconds, 16000);

    public static AiSettings OpenAi(Uri endpoint, string model = "codestral", string? apiKeyEnv = null, int timeoutSeconds = 10) =>
        new(AiProvider.OpenAiCompatible, endpoint, model, apiKeyEnv, timeoutSeconds, 16000);
}
