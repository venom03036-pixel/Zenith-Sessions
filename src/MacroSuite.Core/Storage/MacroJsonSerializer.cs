using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Storage;

/// <summary>
/// Handles polymorphic JSON serialization and deserialization for <see cref="MacroModel"/> documents.
/// Integrates with <see cref="MacroMigrationEngine"/> and preserves unrecognized step types (Rule 18).
/// </summary>
public sealed class MacroJsonSerializer
{
    private readonly MacroMigrationEngine _migrationEngine;
    private readonly JsonSerializerOptions _options;

    public MacroJsonSerializer(MacroMigrationEngine? migrationEngine = null)
    {
        _migrationEngine = migrationEngine ?? new MacroMigrationEngine();
        _options = CreateOptions();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        options.Converters.Add(new MacroStepJsonConverter());
        return options;
    }

    /// <summary>Serializes a <see cref="MacroModel"/> into formatted JSON.</summary>
    public string Serialize(MacroModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Settings?.Target?.Validate();
        model.RecordingCommit?.Validate();
        return JsonSerializer.Serialize(model, _options);
    }

    /// <summary>Serializes a <see cref="MacroModel"/> directly to a file.</summary>
    public void SerializeToFile(MacroModel model, string filePath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        filePath = Path.GetFullPath(filePath);
        string json = Serialize(model);
        string dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);
        string temporaryPath = Path.Combine(dir, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Complete the new document before replacing the last saved version.
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not remove temporary macro file '{temporaryPath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Deserializes a macro JSON document, applying schema migrations if needed.
    /// </summary>
    public MacroModel Deserialize(string json, bool applyMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        if (applyMigrations)
        {
            var node = JsonNode.Parse(json);
            if (node != null)
            {
                var migrated = _migrationEngine.Migrate(node);
                var model = migrated.Deserialize<MacroModel>(_options)
                       ?? throw new JsonException("Failed to deserialize migrated JSON node to MacroModel.");
                model.Settings?.Target?.Validate();
                model.RecordingCommit?.Validate();
                return model;
            }
        }

        var direct = JsonSerializer.Deserialize<MacroModel>(json, _options)
               ?? throw new JsonException("Failed to deserialize JSON string to MacroModel.");
        direct.Settings?.Target?.Validate(); direct.RecordingCommit?.Validate(); return direct;
    }

    /// <summary>Loads and deserializes a macro from a file.</summary>
    public MacroModel DeserializeFromFile(string filePath, bool applyMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Macro file not found: '{filePath}'", filePath);
        }

        string json = File.ReadAllText(filePath);
        return Deserialize(json, applyMigrations);
    }

    /// <summary>
    /// Custom polymorphic converter for <see cref="MacroStep"/> providing fallback to <see cref="UnknownStep"/>.
    /// </summary>
    private sealed class MacroStepJsonConverter : JsonConverter<MacroStep>
    {
        public override MacroStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            string typeDiscriminator = string.Empty;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name.Equals("$type", StringComparison.OrdinalIgnoreCase))
                {
                    typeDiscriminator = property.Value.GetString() ?? string.Empty;
                    break;
                }
            }

            string rawText = root.GetRawText();
            Type? clrType = StepTypeRegistry.ResolveClrType(typeDiscriminator);
            if (clrType is not null)
            {
                return (MacroStep)(JsonSerializer.Deserialize(rawText, clrType, options)
                       ?? throw new JsonException($"Failed to deserialize {clrType.Name}."));
            }

            var rawProps = new Dictionary<string, JsonElement>();
            Guid stepId = Guid.NewGuid();
            string? label = null;
            bool isEnabled = true;

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.Equals("stepId", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.TryGetGuid(out var id))
                {
                    stepId = id;
                }
                else if (prop.Name.Equals("label", StringComparison.OrdinalIgnoreCase))
                {
                    label = prop.Value.GetString();
                }
                else if (prop.Name.Equals("isEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    isEnabled = prop.Value.GetBoolean();
                }
                else if (!prop.Name.Equals("$type", StringComparison.OrdinalIgnoreCase))
                {
                    rawProps[prop.Name] = prop.Value.Clone();
                }
            }

            return new UnknownStep(typeDiscriminator, rawProps)
            {
                StepId = stepId,
                Label = label ?? $"Unknown Step ({typeDiscriminator})",
                IsEnabled = isEnabled
            };
        }

        public override void Write(Utf8JsonWriter writer, MacroStep value, JsonSerializerOptions options)
        {
            if (value is UnknownStep unknown)
            {
                writer.WriteStartObject();
                writer.WriteString("$type", unknown.RawType);
                writer.WriteString("stepId", unknown.StepId);
                if (unknown.Label != null) writer.WriteString("label", unknown.Label);
                writer.WriteBoolean("isEnabled", unknown.IsEnabled);

                foreach (var (k, v) in unknown.RawData)
                {
                    writer.WritePropertyName(k);
                    v.WriteTo(writer);
                }

                writer.WriteEndObject();
                return;
            }

            // Standard step types
            writer.WriteStartObject();

            string typeTag = StepTypeRegistry.GetCanonicalDiscriminator(value.GetType())
                ?? throw new InvalidOperationException(
                    $"No JSON discriminator is registered for action type '{value.GetType().Name}'. The action cannot be serialized.");

            writer.WriteString("$type", typeTag);

            using (var doc = JsonSerializer.SerializeToDocument(value, value.GetType(), options))
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!prop.NameEquals("$type"))
                    {
                        prop.WriteTo(writer);
                    }
                }
            }

            writer.WriteEndObject();
        }
    }
}
