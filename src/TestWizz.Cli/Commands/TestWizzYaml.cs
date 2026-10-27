using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace TestWizz.Cli.Commands;

/// <summary>Convert a small YAML configuration into the existing strict JSON configuration contract.</summary>
internal static class TestWizzYaml
{
    internal static string DefaultText = """
        version: 1
        ai:
          provider: ollama
          endpoint: http://127.0.0.1:11434
          model: qwen2.5-coder:14b
          apiKeyEnv: null
          timeoutSeconds: 120
          maxContextTokens: 16000
        run:
          runtime: docker
          maxRepairAttempts: 2
        output: ../.testwizz-proposals
        """;

    internal static string ToJson(string yamlText)
    {
        using var reader = new StringReader(yamlText);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count != 1)
            throw new FormatException("Expected one YAML document.");
        return ConvertNode(yaml.Documents[0].RootNode).ToJsonString();
    }

    private static JsonNode ConvertNode(YamlNode node) => node switch
    {
        YamlMappingNode map => Map(map),
        YamlSequenceNode seq => new JsonArray([.. seq.Children.Select(ConvertNode)]),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new FormatException("Unsupported YAML node."),
    };

    private static JsonObject Map(YamlMappingNode map)
    {
        var result = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            if (key is not YamlScalarNode name || string.IsNullOrEmpty(name.Value))
                throw new FormatException("YAML configuration keys must be strings.");
            if (result.ContainsKey(name.Value))
                throw new FormatException($"Duplicate YAML key '{name.Value}'.");
            result[name.Value] = ConvertNode(value);
        }
        return result;
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            return JsonValue.Create(scalar.Value);
        var value = scalar.Value;
        if (value is null or "~" or "null" or "Null" or "NULL") return null;
        if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        return JsonValue.Create(value);
    }
}
