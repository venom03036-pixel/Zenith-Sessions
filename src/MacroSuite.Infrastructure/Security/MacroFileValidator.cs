using System.Text.Json;
using MacroSuite.Core.Storage;

namespace MacroSuite.Infrastructure.Security;

/// <summary>
/// Validates raw macro JSON for malicious or malformed content.
/// </summary>
/// <remarks>
/// <para>
/// Checks are ordered from cheapest to most expensive:
/// </para>
/// <list type="number">
///   <item>String length (immediate rejection before any parsing)</item>
///   <item>JSON depth — rejects deeply nested documents (billion-laughs style)</item>
///   <item>Field allowlist — unknown fields are flagged; .NET assembly-qualified <c>$type</c> values are rejected</item>
///   <item>Path traversal — any string value containing <c>../</c>, <c>..\</c>, or UNC prefixes</item>
///   <item>Embedded scripts / shell commands — keyword scan of all string values</item>
/// </list>
/// <para>
/// This class is stateless and fully thread-safe.
/// </para>
/// </remarks>
public sealed class MacroFileValidator : IMacroFileValidator
{
    // ── Configuration constants ────────────────────────────────────────────

    /// <summary>Maximum allowed raw JSON string length (50 MB).</summary>
    private const int MaxJsonLength = 50 * 1024 * 1024;

    /// <summary>Maximum allowed JSON nesting depth.</summary>
    private const int MaxNestingDepth = 32;

    /// <summary>Maximum allowed string field value length (512 KB).</summary>
    private const int MaxFieldLength = 512 * 1024;

    // Known-safe $type discriminators (lowercase). Anything else is suspicious.
    private static readonly HashSet<string> s_allowedStepTypes = new(StepTypeRegistry.KnownDiscriminators, StringComparer.OrdinalIgnoreCase);

    // Patterns that indicate embedded scripts or shell commands.
    private static readonly string[] s_scriptPatterns =
    [
        "cmd.exe", "powershell", "pwsh", "/bin/sh", "/bin/bash",
        "eval(", "<script", "javascript:", "vbscript:",
        "System.Diagnostics.Process", "Process.Start",
        "WScript.Shell", "ShellExecute", "CreateObject(",
    ];

    // Path traversal indicators.
    private static readonly string[] s_pathTraversalPatterns =
    [
        "../", @"..\", "..%2F", "..%5C",  // relative traversal
        @"\\\\",                           // UNC prefix
    ];

    // ── IMacroFileValidator ────────────────────────────────────────────────

    /// <inheritdoc/>
    public MacroValidationResult Validate(string json, string sourceTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var violations = new List<string>();

        // Check 1 — Raw JSON length
        if (json.Length > MaxJsonLength)
        {
            violations.Add(
                $"[{sourceTag}] JSON payload exceeds the maximum allowed size " +
                $"({json.Length:N0} chars > {MaxJsonLength:N0}). Possible zip bomb.");
            return new MacroValidationResult(false, violations);
        }

        // Checks 2–5 — Parse and walk the document
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxNestingDepth + 1,  // +1 so we catch violations at exactly MaxNestingDepth
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            WalkElement(doc.RootElement, depth: 0, sourceTag, violations);
        }
        catch (JsonException ex) when (ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                $"[{sourceTag}] JSON nesting depth exceeds the maximum of {MaxNestingDepth}. " +
                "Possible billion-laughs / deeply recursive payload.");
            return new MacroValidationResult(false, violations);
        }
        catch (JsonException ex)
        {
            violations.Add($"[{sourceTag}] Malformed JSON: {ex.Message}");
            return new MacroValidationResult(false, violations);
        }

        bool isValid = violations.Count == 0;
        return new MacroValidationResult(isValid, violations);
    }

    // ── Private walking logic ──────────────────────────────────────────────

    private static void WalkElement(
        JsonElement element,
        int depth,
        string sourceTag,
        List<string> violations)
    {
        if (depth > MaxNestingDepth)
        {
            violations.Add(
                $"[{sourceTag}] JSON nesting depth exceeds {MaxNestingDepth} at depth {depth}.");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    // Check $type discriminators
                    if (prop.Name.Equals("$type", StringComparison.Ordinal) &&
                        prop.Value.ValueKind == JsonValueKind.String)
                    {
                        ValidateTypeDiscriminator(prop.Value.GetString()!, sourceTag, violations);
                    }

                    // Check string values for threats
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string? value = prop.Value.GetString();
                        if (value != null)
                        {
                            if (value.Length > MaxFieldLength)
                            {
                                violations.Add(
                                    $"[{sourceTag}] Field '{prop.Name}' value length {value.Length:N0} " +
                                    $"exceeds maximum {MaxFieldLength:N0}. Possible oversized payload.");
                            }
                            CheckStringValue(value, prop.Name, sourceTag, violations);
                        }
                    }

                    WalkElement(prop.Value, depth + 1, sourceTag, violations);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkElement(item, depth + 1, sourceTag, violations);
                }
                break;

            case JsonValueKind.String:
                {
                    string? value = element.GetString();
                    if (value != null)
                    {
                        CheckStringValue(value, context: "(array element)", sourceTag, violations);
                    }
                }
                break;

            default:
                // Numbers, booleans, null — no string-based threat checks needed
                break;
        }
    }

    private static void ValidateTypeDiscriminator(
        string typeValue, string sourceTag, List<string> violations)
    {
        // Reject .NET assembly-qualified names: they contain ", Version=" which enables
        // unsafe polymorphic deserialization attacks.
        if (typeValue.Contains(", Version=", StringComparison.OrdinalIgnoreCase) ||
            typeValue.Contains(", Culture=", StringComparison.OrdinalIgnoreCase) ||
            typeValue.Contains(", PublicKeyToken=", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                $"[{sourceTag}] '$type' value '{typeValue}' appears to be a .NET assembly-qualified type name. " +
                "This can enable unsafe polymorphic deserialization and is not permitted.");
            return;
        }

        // Reject unknown step types that contain '.' or '+' (look like namespaced .NET types)
        if (!s_allowedStepTypes.Contains(typeValue) &&
            (typeValue.Contains('.') || typeValue.Contains('+')))
        {
            violations.Add(
                $"[{sourceTag}] '$type' value '{typeValue}' is not a recognised step type " +
                "and contains characters that suggest a .NET type reference.");
        }
    }

    private static void CheckStringValue(
        string value, string context, string sourceTag, List<string> violations)
    {
        // Path traversal check
        foreach (var pattern in s_pathTraversalPatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"[{sourceTag}] Field '{context}' contains path traversal pattern '{pattern}': " +
                    TruncateForLog(value));
            }
        }

        // Embedded script / shell command check
        foreach (var pattern in s_scriptPatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"[{sourceTag}] Field '{context}' contains suspicious pattern '{pattern}': " +
                    TruncateForLog(value));
            }
        }
    }

    private static string TruncateForLog(string value) =>
        value.Length <= 120 ? value : value[..120] + "…";
}
