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
    public void ExtractsVuePublicSurfaceAndMockCandidates()
    {
        string root = FindVueFixtureRoot();
        ContextDocument context = VueContext.Load(root, "src/components/CartItem.vue", 20_000);

        Assert.Equal("vue", context.Stack);
        Assert.Contains(context.Members, member => member.Signature.Contains("prop title: string (required: true", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature.Contains("prop count: number (required: false, default: 0", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature.Contains("emit select(id: number)", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature == "exposed focus");
        Assert.Contains(context.Members, member => member.Signature == "computed isVisible");
        Assert.Contains(context.Members, member => member.Signature == "watched () => props.count");
        Assert.Contains(context.Members, member => member.Signature == "imports vue-router");
        Assert.Contains(context.Members, member => member.Signature == "mockCandidates useRouter");
        Assert.Contains(context.Members, member => member.Signature == "slots actions");
        Assert.Contains(context.Members, member => member.Signature == "import @/utils/cart -> src/utils/cart.ts (alias)");
        Assert.Contains(context.RelatedTests, test =>
            test.File == "src/components/CartItem.spec.ts" && test.MatchedOn == "basename");
        Assert.Contains(context.RelatedTests, test =>
            test.File == "src/components/CartItemInteractions.test.ts" && test.MatchedOn == "import");

    }

    [Fact]
    public void ExtractsComposablePublicSurfaceAndRelatedTests()
    {
        string root = FindVueFixtureRoot();
        ContextDocument context = VueTypeScriptContext.Load(root, "src/composables/useCart.ts", 20_000);

        Assert.Equal("vue", context.Stack);
        Assert.Equal("Vue composable useCart", context.Target.Signature);
        Assert.Contains(context.Members, member => member.Signature.StartsWith("export function useCart(", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature == "imports vue");
        Assert.Contains(context.Members, member => member.Signature == "imports @/stores/cart");
        Assert.Contains(context.Members, member => member.Signature == "dependency useCartStore");
        Assert.Contains(context.RelatedTests, test =>
            test.File == "src/composables/useCart.test.ts" && test.MatchedOn == "basename");
    }

    [Fact]
    public void ExtractsPiniaStoreStateGettersAndActions()
    {
        string root = FindVueFixtureRoot();
        ContextDocument context = VueTypeScriptContext.Load(root, "src/stores/cart.ts", 20_000);

        Assert.Equal("vue", context.Stack);
        Assert.Equal("Pinia store cart", context.Target.Signature);
        Assert.Contains(context.Members, member => member.Signature == "Pinia store cart");
        Assert.Contains(context.Members, member => member.Signature.StartsWith("state items:", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature.StartsWith("state loading:", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature.StartsWith("getter itemCount:", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature.StartsWith("action addItem:", StringComparison.Ordinal));
        Assert.Contains(context.Members, member => member.Signature.StartsWith("action clear:", StringComparison.Ordinal));
    }

    private static string FindVueFixtureRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string fixtureRoot = Path.Combine(directory.FullName, "fixtures", "vue");
            if (File.Exists(Path.Combine(fixtureRoot, "package.json")))
                return fixtureRoot;
        }

        throw new DirectoryNotFoundException("Could not locate the Vue fixture project root.");
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
