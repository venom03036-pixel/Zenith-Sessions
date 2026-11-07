using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MacroSuite.SDK.AI;

namespace MacroSuite.Core.AI;

/// <summary>
/// Shared parse stage for provider JSON. AI output is untrusted (Rule 8);
/// malformed payloads fail closed instead of becoming a silent no-op.
/// Extra fields are rejected so a compromised model cannot smuggle unexpected keys.
/// </summary>
public static class AIActionJsonParser
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "action_type",
        "x",
        "y",
        "key_code",
        "text",
        "confidence",
        "reasoning"
    };

    private static readonly JsonDocumentOptions StrictDocument = new()
    {
        MaxDepth = 4,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    public static AIAction Parse(string? rawJson, string providerDisplayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDisplayName);
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new InvalidDataException($"{providerDisplayName} returned an empty structured action.");

        string clean = StripCodeFences(rawJson);
        try
        {
            var node = JsonNode.Parse(clean, documentOptions: StrictDocument);
            if (node is null)
                throw new JsonException($"{providerDisplayName} returned a null JSON action.");
            if (node is not JsonObject obj)
                throw new JsonException($"{providerDisplayName} returned a non-object JSON action.");

            foreach (var pair in obj)
            {
                if (!AllowedFields.Contains(pair.Key))
                    throw new JsonException($"{providerDisplayName} returned unexpected action field '{pair.Key}'.");
            }

            return new AIAction
            {
                ActionType = obj["action_type"]?.GetValue<string>() ?? "none",
                X = ReadInt(obj["x"]),
                Y = ReadInt(obj["y"]),
                KeyCode = ReadInt(obj["key_code"]),
                Text = obj["text"]?.GetValue<string?>(),
                Confidence = ReadConfidence(obj["confidence"]),
                Reasoning = obj["reasoning"]?.GetValue<string>() ?? string.Empty
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{providerDisplayName} returned malformed structured action JSON.", ex);
        }
    }

    internal static string StripCodeFences(string rawJson)
    {
        string clean = rawJson.Trim();
        if (clean.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            clean = clean[7..];
        else if (clean.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            clean = clean[3..];
        if (clean.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^3];
        return clean.Trim();
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue(out int i))
            return i;
        if (value.TryGetValue(out long l))
            return (int)l;
        if (value.TryGetValue(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        if (value.TryGetValue(out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;

        return null;
    }

    private static double? ReadConfidence(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return Math.Clamp(d, 0.0, 1.0);
        if (value.TryGetValue(out int i))
            return Math.Clamp(i, 0.0, 1.0);

        return null;
    }
}
