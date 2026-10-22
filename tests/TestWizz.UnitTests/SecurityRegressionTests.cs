using System.Net;
using TestWizz.AI;
using TestWizz.Core.Proposals;

namespace TestWizz.UnitTests;

/// <summary>Offline, deterministic regressions for issue #11: SSRF, path escape, and redirects.</summary>
public sealed class SecurityRegressionTests
{
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("fe80::1")]
    [InlineData("2001:4860:4860::8888")]
    public void MetadataAndPublicAddresses_AreNeverAccepted(string address) =>
        Assert.False(EndpointGuard.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public async Task MixedDnsAnswers_FailClosedInsteadOfSelectingPrivateAddress()
    {
        var guard = new EndpointGuard((_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("10.2.0.5"), IPAddress.Parse("169.254.169.254")
        }));
        var ex = await Assert.ThrowsAsync<ModelClientException>(() =>
            guard.ResolveAsync("internal-model.test", TestContext.Current.CancellationToken));
        Assert.Equal(ModelFailure.EndpointNotPrivate, ex.Failure);
    }

    [Fact]
    public void HttpHandler_DisallowsRedirectsProxiesAndCookieStorage()
    {
        using var handler = new EndpointGuard().CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("tests/../../outside.cs")]
    [InlineData("tests/./outside.cs")]
    [InlineData("/tmp/outside.cs")]
    [InlineData("C:/temp/outside.cs")]
    [InlineData("tests\\outside.cs")]
    public void TraversalAndAbsoluteProposalPaths_AreRejected(string path) =>
        Assert.False(PathGuard.IsSafeRelative(path));

    [Fact]
    public void SymlinkToExternalDirectory_CannotEscapeRepository()
    {
        var root = Directory.CreateTempSubdirectory("testwizz-security-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("testwizz-security-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "tests"), outside);
            Assert.False(PathGuard.IsWithin(root, Path.Combine(root, "tests", "Escape.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
