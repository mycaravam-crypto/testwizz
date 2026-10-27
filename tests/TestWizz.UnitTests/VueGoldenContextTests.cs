using System.Text.Json;
using System.Text.Json.Nodes;
using TestWizz.Cli.Commands;
using TestWizz.Core.Context;

namespace TestWizz.UnitTests;

public sealed class VueGoldenContextTests
{
    [Theory]
    [InlineData("src/components/CartItem.vue", "CartItem.context.json", true)]
    [InlineData("src/composables/useCart.ts", "useCart.context.json", false)]
    [InlineData("src/stores/cart.ts", "cart-store.context.json", false)]
    [InlineData("src/stores/useCounterStore.ts", "counter-store.context.json", false)]
    public void ContextMatchesGoldenFixture(string target, string goldenFile, bool vueSfc)
    {
        string root = FindVueFixtureRoot();
        ContextDocument context = vueSfc
            ? VueContext.Load(root, target, 20_000)
            : VueTypeScriptContext.Load(root, target, 20_000);

        object projection = new
        {
            targetSignature = context.Target.Signature,
            members = vueSfc
                ? context.Members.Select(m => m.Signature)
                    .Where(signature =>
                        signature.StartsWith("computed ", StringComparison.Ordinal)
                        || signature.StartsWith("emit ", StringComparison.Ordinal)
                        || signature.StartsWith("exposed ", StringComparison.Ordinal)
                        || signature.StartsWith("import @/", StringComparison.Ordinal)
                        || signature.StartsWith("imports ", StringComparison.Ordinal)
                        || signature.StartsWith("mockCandidates ", StringComparison.Ordinal)
                        || signature.StartsWith("prop ", StringComparison.Ordinal)
                        || signature.StartsWith("slots ", StringComparison.Ordinal)
                        || signature.StartsWith("template event ", StringComparison.Ordinal)
                        || signature.StartsWith("watched ", StringComparison.Ordinal))
                    .OrderBy(signature => signature, StringComparer.Ordinal).ToArray()
                : context.Members.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            relatedTests = context.RelatedTests.Select(test => new
            {
                name = test.Name,
                file = test.File,
                matchedOn = test.MatchedOn,
            }).OrderBy(test => test.file, StringComparer.Ordinal).ToArray(),
            unavailable = context.Unavailable.Select(field => field.Field).ToArray(),
        };

        string goldenPath = Path.Combine(root, "golden", goldenFile);
        JsonNode expected = JsonNode.Parse(File.ReadAllText(goldenPath))
            ?? throw new InvalidDataException($"Golden fixture is empty: {goldenPath}");
        JsonNode actual = JsonSerializer.SerializeToNode(projection)
            ?? throw new InvalidDataException("Context projection serialized to null.");

        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"Context differs from golden fixture '{goldenFile}'.\nExpected:\n{expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}\nActual:\n{actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
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
}
