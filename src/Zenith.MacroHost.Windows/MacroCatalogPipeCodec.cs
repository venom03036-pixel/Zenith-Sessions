using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Windows;

internal static class MacroCatalogPipeCodec
{
    internal const string Operation = "Catalog";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter<MacroCatalogState>(allowIntegerValues: false) }
    };

    internal sealed record Request(string Product, int Version, Guid RequestId, string Operation, MacroCatalogQuery Query);
    internal sealed record Response(string Product, int Version, Guid RequestId, string Operation, MacroCatalogPage Page);

    internal static string PipeName(string? name)
    {
        name ??= MacroHostProtocol.PipeName;
        if (name.Length is < 1 or > 160 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("A bounded local pipe name is required.", nameof(name));
        return name;
    }

    internal static async Task<Request> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var request = Decode<Request>(await ReadFrameAsync(stream, ct).ConfigureAwait(false));
        CheckHeader(request.Product, request.Version, request.RequestId, request.Operation);
        if (request.Query is null) throw Malformed();
        request.Query.Validated();
        return request;
    }

    internal static async Task<MacroCatalogPage> ReadResponseAsync(Stream stream, Guid requestId, MacroCatalogQuery query, CancellationToken ct)
    {
        var response = Decode<Response>(await ReadFrameAsync(stream, ct).ConfigureAwait(false));
        CheckHeader(response.Product, response.Version, response.RequestId, response.Operation);
        if (response.RequestId != requestId || response.Page is null) throw Malformed();
        return response.Page.Validated(query);
    }

    internal static Task WriteRequestAsync(Stream stream, Guid requestId, MacroCatalogQuery query, CancellationToken ct)
        => WriteFrameAsync(stream, new Request(MacroHostProtocol.Product, MacroHostProtocol.Version, requestId, Operation, query), ct);
    internal static Task WriteResponseAsync(Stream stream, Guid requestId, MacroCatalogPage page, CancellationToken ct)
        => WriteFrameAsync(stream, new Response(MacroHostProtocol.Product, MacroHostProtocol.Version, requestId, Operation, page), ct);

    private static void CheckHeader(string product, int version, Guid id, string operation)
    {
        if (product != MacroHostProtocol.Product || version != MacroHostProtocol.Version || id == Guid.Empty || operation != Operation)
            throw Malformed();
    }

    private static T Decode<T>(byte[] bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
                {
                    string value = reader.GetString() ?? throw Malformed();
                    for (int i = 0; i < value.Length; i++)
                    {
                        if (char.IsHighSurrogate(value[i]))
                        { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw Malformed(); }
                        else if (char.IsLowSurrogate(value[i])) throw Malformed();
                    }
                    if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(value)) throw Malformed();
                }
            }
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (typeof(T) == typeof(Request))
            {
                RequireFields(root, "Product", "Version", "RequestId", "Operation", "Query");
                RequireFields(root.GetProperty("Query"), "Offset", "Limit", "HostSessionId", "CatalogRevision");
            }
            else
            {
                RequireFields(root, "Product", "Version", "RequestId", "Operation", "Page");
                var page = root.GetProperty("Page");
                RequireFields(page, "State", "HostSessionId", "CatalogRevision", "HostVersion", "Items", "Total", "NextOffset", "Explanation");
                var items = page.GetProperty("Items");
                if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MacroCatalogLimits.MaximumPageSize) throw Malformed();
                foreach (var item in items.EnumerateArray())
                    RequireFields(item, "Id", "SavedRevision", "Name", "Hotkey", "TopLevelActions", "Enabled", "TargetMode");
            }
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Malformed();
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        { throw Malformed(); }
    }

    private static void RequireFields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Malformed();
        int count = 0;
        foreach (var property in element.EnumerateObject())
        { if (!names.Contains(property.Name, StringComparer.Ordinal)) throw Malformed(); count++; }
        if (count != names.Length) throw Malformed();
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MacroHostProtocol.MaximumFrameBytes) throw Malformed();
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length is <= 0 or > MacroHostProtocol.MaximumFrameBytes) throw Malformed();
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static InvalidDataException Malformed() => new("The macro catalog message is incompatible or malformed.");
}
