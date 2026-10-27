using System.Diagnostics;

namespace TestWizz.E2ETests;

public sealed class CliSmokeTests
{
    [Fact]
    public void CliAssemblyCanBeResolved()
    {
        Assert.NotNull(typeof(TestWizz.Cli.CliApp).Assembly);
    }
}
