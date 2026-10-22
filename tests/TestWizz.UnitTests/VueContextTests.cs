using TestWizz.Cli.Commands;
using TestWizz.Core.Context;

namespace TestWizz.UnitTests;

public sealed class VueContextTests
{
    [Theory]
    [InlineData("../outside.vue")]
    [InlineData("/etc/passwd.vue")]
    [InlineData("src/./Comp.vue")]
    [InlineData("src/App.ts")]
    public void RejectsUnsafeOrNonVueTargets(string target)
    {
        var root = Directory.CreateTempSubdirectory("testwizz-vue-").FullName;
        try
        {
            Assert.Throws<ContextException>(() => VueContext.Load(root, target, 20_000));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingSfcIsActionable()
    {
        var root = Directory.CreateTempSubdirectory("testwizz-vue-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "components"));
            File.WriteAllText(Path.Combine(root, "src", "components", "CartItem.vue"), "<template/>");
            ContextException ex = Assert.Throws<ContextException>(() => VueContext.Load(root, "src/App.vue", 20_000));
            Assert.Equal("target.not_found", ex.Code);
            Assert.Contains("src/components/CartItem.vue", ex.Candidates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
