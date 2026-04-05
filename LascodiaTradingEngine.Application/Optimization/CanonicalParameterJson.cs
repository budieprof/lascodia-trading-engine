using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Produces canonical JSON for parameter dictionaries so duplicate detection and persisted
/// comparisons are stable across serializer settings and key ordering.
/// </summary>
internal static class CanonicalParameterJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    internal static string Serialize(IReadOnlyDictionary<string, object> values)
    {
        var ordered = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            ordered[key] = value;

        return JsonSerializer.Serialize(ordered, SerializerOptions);
    }

    internal static string Normalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return json.Trim();
            return CanonicalizeNode(node).ToJsonString(SerializerOptions);
        }
        catch
        {
            return json.Trim();
        }
    }

    private static JsonNode CanonicalizeNode(JsonNode node) => node switch
    {
        JsonObject obj => CanonicalizeObject(obj),
        JsonArray arr => CanonicalizeArray(arr),
        _ => node.DeepClone()
    };

    private static JsonObject CanonicalizeObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var key in source.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
            result[key] = source[key] is null ? null : CanonicalizeNode(source[key]!);

        return result;
    }

    private static JsonArray CanonicalizeArray(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
            result.Add(item is null ? null : CanonicalizeNode(item));

        return result;
    }
}
