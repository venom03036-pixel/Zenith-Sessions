using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MacroSuite.Core.Licensing;

/// <summary>Bounded, exact JSON parsing shared by activation verification and the private issuer.</summary>
public static class ActivationWireFormat
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.MakeReadOnly(populateMissingResolver: true); return options;
    }
    public static string KeyId(ReadOnlySpan<byte> publicKey) => Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
    public static bool IsKeyId(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static byte[] DecodeBase64(string value, int maximumBytes = ActivationProduct.MaximumFileBytes)
    {
        if (value.Length > ((maximumBytes + 2L) / 3L) * 4L) throw new FormatException("Activation data is too large.");
        byte[] bytes = Convert.FromBase64String(value);
        if (bytes.Length > maximumBytes || Convert.ToBase64String(bytes) != value)
            throw new FormatException("Activation data uses an invalid encoding.");
        return bytes;
    }

    public static JsonDocument Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > ActivationProduct.MaximumFileBytes) throw new FormatException("Activation file exceeds 64 KiB.");
        return Parse(Encoding.UTF8.GetBytes(text));
    }

    public static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length > ActivationProduct.MaximumFileBytes) throw new FormatException("Activation file exceeds 64 KiB.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 12 });
        var names = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) names.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) names.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !names.Peek().Add(reader.GetString()!))
                throw new FormatException("Activation data contains duplicate fields.");
        }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
    }

    public static void ExactObject(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Activation object expected.");
        var expected = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!expected.Remove(property.Name)) throw new FormatException("Activation data contains an unsupported field.");
        if (expected.Count != 0) throw new FormatException("Activation data is missing required fields.");
    }

    public static string Text(JsonElement obj, string field, int maxLength = 128)
    {
        string? value = obj.GetProperty(field).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new FormatException("Activation text field is invalid.");
        return value;
    }

    public static string UnwrapLicenseCode(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > ActivationProduct.MaximumFileBytes) throw new FormatException("Activation file exceeds 64 KiB.");
        string trimmed = text.Trim();
        const string prefix = "ZENITH-LICENSE-2:";
        return trimmed.StartsWith(prefix, StringComparison.Ordinal)
            ? new UTF8Encoding(false, true).GetString(DecodeBase64(trimmed[prefix.Length..])) : trimmed;
    }

    public static string AsLicenseCode(string envelope) => "ZENITH-LICENSE-2:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));
}

public sealed record ActivationTrustKey(string KeyId, string PublicKeyBase64);

public sealed class ActivationTrustManifest
{
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    public string ActiveKeyId { get; }
    public IReadOnlyList<ActivationTrustKey> Keys { get; }

    public ActivationTrustManifest(string activeKeyId, IEnumerable<ActivationTrustKey> keys)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var list = new List<ActivationTrustKey>();
        foreach (var key in keys)
        {
            byte[] spki = ActivationWireFormat.DecodeBase64(key.PublicKeyBase64, 512);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(spki, out int read);
            var parameters = verifier.ExportParameters(false);
            if (read != spki.Length || parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" || key.KeyId != ActivationWireFormat.KeyId(spki) || !map.TryAdd(key.KeyId, spki))
                throw new FormatException("The issuer trust key is invalid.");
            list.Add(key);
            if (list.Count > 8) throw new FormatException("Too many issuer trust keys.");
        }
        if (map.Count == 0 || !map.ContainsKey(activeKeyId)) throw new FormatException("An active issuer trust key is required.");
        ActiveKeyId = activeKeyId; _keys = map; Keys = list.AsReadOnly();
    }

    public bool TryGetPublicKey(string id, out byte[] key)
    {
        if (_keys.TryGetValue(id, out byte[]? found)) { key = found.ToArray(); return true; }
        key = []; return false;
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, productId = ActivationProduct.ProductId,
        algorithm = ActivationProduct.SignatureAlgorithm, activeKeyId = ActiveKeyId, keys = Keys
    }, ActivationWireFormat.JsonOptions);

    public static ActivationTrustManifest Parse(string text)
    {
        using var doc = ActivationWireFormat.Parse(text); var root = doc.RootElement;
        ActivationWireFormat.ExactObject(root, "schemaVersion", "productId", "algorithm", "activeKeyId", "keys");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("productId").GetString() != ActivationProduct.ProductId || root.GetProperty("algorithm").GetString() != ActivationProduct.SignatureAlgorithm)
            throw new FormatException("Unsupported issuer trust manifest.");
        var keys = new List<ActivationTrustKey>();
        foreach (var item in root.GetProperty("keys").EnumerateArray())
        {
            ActivationWireFormat.ExactObject(item, "keyId", "publicKeyBase64");
            keys.Add(new(ActivationWireFormat.Text(item, "keyId"), ActivationWireFormat.Text(item, "publicKeyBase64", 1024)));
        }
        return new(ActivationWireFormat.Text(root, "activeKeyId"), keys);
    }
}
