using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestWizz.AI;

/// <summary>
/// Validates model output against the small JSON Schema subset TestWizz sends to models: <c>type</c>, <c>properties</c>,
/// <c>required</c>, <c>additionalProperties: false</c>, <c>items</c> and <c>enum</c>. Local servers enforce schemas
/// unevenly, so every response is checked again here.
/// </summary>
public static class JsonSchemaLite
{
    public static IReadOnlyList<string> Validate(JsonNode? value, JsonObject schema)
    {
        var errors = new List<string>();
        Validate(value, schema, "$", errors);
        return errors;
    }

    private static void Validate(JsonNode? value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["type"] is JsonValue typeNode && typeNode.TryGetValue<string>(out var type) && !HasType(value, type))
        {
            errors.Add($"{path}: expected {type}.");
            return;
        }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
        {
            errors.Add($"{path}: value is not one of the allowed values.");
        }

        if (value is JsonObject obj)
        {
            var properties = schema["properties"] as JsonObject;
            foreach (var name in (schema["required"] as JsonArray)?.Select(r => r?.GetValue<string>()) ?? [])
            {
                if (name is not null && !obj.ContainsKey(name))
                {
                    errors.Add($"{path}.{name}: required.");
                }
            }

            foreach (var (name, child) in obj)
            {
                if (properties?[name] is JsonObject childSchema)
                {
                    Validate(child, childSchema, $"{path}.{name}", errors);
                }
                else if (schema["additionalProperties"] is JsonValue extra && extra.TryGetValue<bool>(out var allowedExtra) && !allowedExtra)
                {
                    errors.Add($"{path}.{name}: not allowed.");
                }
            }
        }
        else if (value is JsonArray array && schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Validate(array[i], itemSchema, $"{path}[{i}]", errors);
            }
        }
    }

    private static bool HasType(JsonNode? value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value?.GetValueKind() == JsonValueKind.String,
        "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value?.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out _),
        "null" => value is null,
        _ => true,
    };
}
