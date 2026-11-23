using TestWizz.Cli;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public sealed class BrowserStorageStateTests
{
    private static readonly Uri Url = new("http://127.0.0.1:5173/orders");
    private const string SessionState = """
        {"cookies":[],"origins":[{"origin":"http://127.0.0.1:5173","localStorage":[],
          "sessionStorage":[{"name":"accessToken","value":"fake-secret-jwt"}]}]}
        """;

    [Fact]
    public void AcceptsDocWizzSessionStorageAndResolvesWithinRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "testwizz-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".auth"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".auth", "docwizz-state.json"), SessionState);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, ".auth", "docwizz-state.json")),
                BrowserStorageState.Resolve(root, Url, ".auth/docwizz-state.json"));
            Assert.Null(BrowserStorageState.Resolve(root, Url, null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AcceptsCookieOnlyStateForSameHostname()
    {
        var root = Path.Combine(Path.GetTempPath(), "testwizz-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "state.json"), """
                {"cookies":[{"domain":"127.0.0.1","name":"session","value":"fake"}],"origins":[]}
                """);
            Assert.NotNull(BrowserStorageState.Resolve(root, Url, "state.json"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("../secret.json")]
    [InlineData("/tmp/session.json")]
    [InlineData("C:\\Users\\Me\\session.json")]
    [InlineData(".auth/state.txt")]
    public void RejectsPathsOutsideRepositoryOrWithoutJsonExtension(string file)
    {
        Assert.Equal("ui.storage_state_path", Assert.Throws<CliException>(() =>
            BrowserStorageState.Resolve(Path.GetTempPath(), Url, file)).Code);
    }

    [Fact]
    public void RejectsDifferentOriginAndMalformedSessionStorageWithoutLeakingValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "testwizz-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "state.json");
            File.WriteAllText(file, SessionState.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
            var wrongHost = Assert.Throws<CliException>(() =>
                BrowserStorageState.Resolve(root, Url, "state.json"));
            Assert.Equal("ui.storage_state_origin", wrongHost.Code);

            File.WriteAllText(file, SessionState.Replace(
                "\"sessionStorage\":[{\"name\":\"accessToken\",\"value\":\"fake-secret-jwt\"}]",
                "\"sessionStorage\":[{\"name\":\"accessToken\",\"value\":123}]",
                StringComparison.Ordinal));
            var malformed = Assert.Throws<CliException>(() =>
                BrowserStorageState.Resolve(root, Url, "state.json"));
            Assert.Equal("ui.storage_state_invalid", malformed.Code);
            Assert.DoesNotContain("fake-secret-jwt", malformed.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RedactsVisibleJWTsBeforeCallingLocalModel()
    {
        const string token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.synthetic_signature";
        var snapshot = BrowserStorageState.RedactSnapshot("text: " + token + " / Save order");
        Assert.DoesNotContain(token, snapshot, StringComparison.Ordinal);
        Assert.Contains("[JWT redacted] / Save order", snapshot, StringComparison.Ordinal);
    }
}
