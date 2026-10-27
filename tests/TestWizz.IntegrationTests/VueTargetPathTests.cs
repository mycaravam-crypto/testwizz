using TestWizz.Vue;

namespace TestWizz.IntegrationTests;

public sealed class VueTargetPathTests
{
    [Theory]
    [InlineData("src/components/CartItem.vue", "src/components/CartItem.vue")]
    [InlineData("src\\components\\CartItem.vue", "src/components/CartItem.vue")]
    public void AcceptsProjectRelativeVueTargets(string input, string expected)
    {
        Assert.True(VueTargetPath.TryNormalize(input, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("../CartItem.vue")]
    [InlineData("/tmp/CartItem.vue")]
    [InlineData("src/./CartItem.vue")]
    [InlineData("src//CartItem.vue")]
    [InlineData("src/CartItem.ts")]
    public void RejectsUnsafeTargets(string input)
    {
        Assert.False(VueTargetPath.TryNormalize(input, out _));
    }
}
