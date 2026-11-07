using System.Text.Json.Nodes;

namespace MacroSuite.Core.Storage;

/// <summary>
/// Applies sequential schema migrations to macro JSON documents to ensure forward/backward compatibility (Rule 18).
/// </summary>
public sealed class MacroMigrationEngine
{
    private readonly Dictionary<int, Func<JsonNode, JsonNode>> _migrations = new();

    public MacroMigrationEngine()
    {
        // Register default migrations
        RegisterMigration(0, MigrateV0ToV1);
        RegisterMigration(1, MigrateV1ToV2);
        // Optional targets preserve absence/legacy behavior. Migration is in memory, never a file rewrite.
        RegisterMigration(2, node => node);
    }

    /// <summary>
    /// Registers a migration handler from <paramref name="fromVersion"/> to <paramref name="fromVersion"/> + 1.
    /// </summary>
    public void RegisterMigration(int fromVersion, Func<JsonNode, JsonNode> migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        _migrations[fromVersion] = migration;
    }

    /// <summary>
    /// Migrates a JSON document from its detected schema version up to <paramref name="targetVersion"/>.
    /// </summary>
    public JsonNode Migrate(JsonNode rootNode, int targetVersion = MacroSchema.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(rootNode);

        int currentVersion = ExtractSchemaVersion(rootNode);

        if (currentVersion > targetVersion)
        {
            throw new InvalidOperationException(
                $"Cannot migrate macro schema from version {currentVersion} down to {targetVersion}. The macro was created with a newer version of MacroSuite.");
        }

        JsonNode currentNode = rootNode;
        while (currentVersion < targetVersion)
        {
            if (!_migrations.TryGetValue(currentVersion, out var migrationFunc))
            {
                throw new InvalidOperationException(
                    $"No migration path found for macro schema version {currentVersion} to {currentVersion + 1}.");
            }

            currentNode = migrationFunc(currentNode);
            currentVersion++;
            if (currentNode is JsonObject currentObject)
                SetPropertyIgnoreCase(currentObject, "schemaVersion", currentVersion);
        }

        return currentNode;
    }

    /// <summary>
    /// Extracts the schema version number from the JSON node, defaulting to 0 if absent.
    /// </summary>
    public static int ExtractSchemaVersion(JsonNode rootNode)
    {
        ArgumentNullException.ThrowIfNull(rootNode);

        if (rootNode is JsonObject obj
            && TryGetPropertyValueIgnoreCase(obj, "schemaVersion", out JsonNode? versionNode)
            && versionNode != null)
        {
            return versionNode.GetValue<int>();
        }

        return 0;
    }

    private static JsonNode MigrateV0ToV1(JsonNode node)
    {
        if (node is not JsonObject obj)
        {
            return node;
        }

        // V0 to V1: Ensure schemaVersion = 1, ensure settings object exists
        SetPropertyIgnoreCase(obj, "schemaVersion", 1);

        if (!TryGetPropertyValueIgnoreCase(obj, "settings", out JsonNode? settings) || settings is null)
        {
            obj["settings"] = new JsonObject
            {
                ["playbackSpeed"] = 1.0,
                ["repeatCount"] = 1,
                ["maxRuntimeSeconds"] = 3600,
                ["maxIterations"] = 100000,
                ["maxLoopDepth"] = 8
            };
        }

        if (!TryGetPropertyValueIgnoreCase(obj, "steps", out JsonNode? steps) || steps is null)
        {
            obj["steps"] = new JsonArray();
        }

        if (!TryGetPropertyValueIgnoreCase(obj, "id", out JsonNode? id) || id is null)
        {
            obj["id"] = Guid.NewGuid().ToString();
        }

        if (!TryGetPropertyValueIgnoreCase(obj, "name", out JsonNode? name) || name is null)
        {
            obj["name"] = "Imported Macro";
        }

        return obj;
    }

    /// <summary>
    /// v1 → v2: materialize vision/control-flow reliability fields without dropping unknown keys.
    /// Nested IF/THEN/ELSE/LOOP bodies, palettes, regions, and unknown step objects are preserved.
    /// </summary>
    private static JsonNode MigrateV1ToV2(JsonNode node)
    {
        if (node is not JsonObject obj)
            return node;

        SetPropertyIgnoreCase(obj, "schemaVersion", 2);
        if (TryGetObjectArray(obj, "steps", out JsonArray? steps) && steps is not null)
            MigrateStepArray(steps);

        return obj;
    }

    private static void MigrateStepArray(JsonArray steps)
    {
        foreach (JsonNode? stepNode in steps)
        {
            if (stepNode is JsonObject step)
                MigrateStepObject(step);
        }
    }

    private static void MigrateStepObject(JsonObject step)
    {
        string type = ReadTypeDiscriminator(step);
        switch (type)
        {
            case "waituntil":
                MigrateConditionObject(GetChildObject(step, "condition"));
                EnsureNumber(step, "requiredConfirmations", 1);
                EnsureBoolean(step, "waitWhile", false);
                break;
            case "ifthenelse":
                MigrateConditionObject(GetChildObject(step, "condition"));
                if (TryGetObjectArray(step, "thenSteps", out JsonArray? thenSteps) && thenSteps is not null)
                    MigrateStepArray(thenSteps);
                if (TryGetObjectArray(step, "elseSteps", out JsonArray? elseSteps) && elseSteps is not null)
                    MigrateStepArray(elseSteps);
                break;
            case "loop":
                MigrateConditionObject(GetChildObject(step, "whileCondition"));
                EnsureNumber(step, "maxDurationMs", 0);
                EnsureNumber(step, "checkIntervalMs", 50);
                EnsureNumber(step, "minBodyDelayMs", 0);
                if (TryGetObjectArray(step, "bodySteps", out JsonArray? body) && body is not null)
                    MigrateStepArray(body);
                break;
            case "visualwait":
                EnsureVisualWaitDefaults(step);
                break;
        }
    }

    private static void MigrateConditionObject(JsonObject? condition)
    {
        if (condition is null)
            return;

        switch (ReadTypeDiscriminator(condition))
        {
            case "color":
                EnsureColorConditionDefaults(condition);
                break;
            case "visualpresence":
                EnsureVisualPresenceDefaults(condition);
                break;
            case "and":
            case "or":
                if (TryGetObjectArray(condition, "conditions", out JsonArray? children) && children is not null)
                {
                    foreach (JsonNode? child in children)
                    {
                        if (child is JsonObject childObject)
                            MigrateConditionObject(childObject);
                    }
                }
                break;
            case "not":
                MigrateConditionObject(GetChildObject(condition, "innerCondition"));
                break;
        }
    }

    private static void EnsureColorConditionDefaults(JsonObject condition)
    {
        EnsureNumber(condition, "comparisonMode", 0);
        EnsureNumber(condition, "sampleStride", 1);
        EnsureNumber(condition, "pollIntervalMs", 50);
        EnsureNumber(condition, "requiredConfirmations", 1);
        EnsureNumber(condition, "requiredFalseConfirmations", 1);
        EnsureNumber(condition, "stabilityDurationMs", 0);
        EnsureNumber(condition, "timeoutMs", 10000);
        EnsureNumber(condition, "failurePolicy", (int)Rules.ConditionFailurePolicy.Default);
        EnsureArray(condition, "additionalTargets");
    }

    private static void EnsureVisualPresenceDefaults(JsonObject condition)
    {
        EnsureNumber(condition, "comparisonMode", 2);
        EnsureNumber(condition, "sampleStride", 1);
        EnsureNumber(condition, "pollIntervalMs", 150);
        EnsureNumber(condition, "requiredConfirmations", 1);
        EnsureNumber(condition, "requiredFalseConfirmations", 1);
        EnsureNumber(condition, "stabilityDurationMs", 0);
        EnsureNumber(condition, "timeoutMs", 10000);
        EnsureNumber(condition, "failurePolicy", (int)Rules.ConditionFailurePolicy.Default);
        EnsureArray(condition, "additionalTargets");
    }

    private static void EnsureVisualWaitDefaults(JsonObject step)
    {
        EnsureNumber(step, "comparisonMode", 2);
        EnsureNumber(step, "sampleStride", 1);
        EnsureNumber(step, "requiredConfirmations", 1);
        EnsureNumber(step, "requiredFalseConfirmations", 1);
        EnsureNumber(step, "stabilityDurationMs", 0);
        EnsureNumber(step, "pollIntervalMs", 150);
        EnsureNumber(step, "timeoutMs", 10000);
        EnsureArray(step, "additionalTargets");
        if (!HasPropertyIgnoreCase(step, "failurePolicy"))
        {
            string onTimeout = "Stop";
            if (TryGetPropertyValueIgnoreCase(step, "onTimeout", out JsonNode? timeoutNode)
                && timeoutNode is JsonValue timeoutValue
                && timeoutValue.TryGetValue(out string? timeoutText)
                && !string.IsNullOrWhiteSpace(timeoutText))
            {
                onTimeout = timeoutText;
            }

            int policy = onTimeout.Equals("Skip", StringComparison.OrdinalIgnoreCase) ? 1
                : onTimeout.Equals("Retry", StringComparison.OrdinalIgnoreCase) ? 2
                : 0;
            step["failurePolicy"] = policy;
        }
    }

    private static string ReadTypeDiscriminator(JsonObject obj)
    {
        if (TryGetPropertyValueIgnoreCase(obj, "$type", out JsonNode? typeNode)
            && typeNode is JsonValue value
            && value.TryGetValue(out string? type)
            && type is not null)
        {
            return type.ToLowerInvariant();
        }

        return string.Empty;
    }

    private static JsonObject? GetChildObject(JsonObject parent, string propertyName)
        => TryGetPropertyValueIgnoreCase(parent, propertyName, out JsonNode? node) && node is JsonObject child
            ? child
            : null;

    private static bool TryGetObjectArray(JsonObject parent, string propertyName, out JsonArray? array)
    {
        array = null;
        if (!TryGetPropertyValueIgnoreCase(parent, propertyName, out JsonNode? node) || node is not JsonArray jsonArray)
            return false;
        array = jsonArray;
        return true;
    }

    private static bool HasPropertyIgnoreCase(JsonObject obj, string propertyName)
        => TryGetPropertyValueIgnoreCase(obj, propertyName, out _);

    private static void EnsureNumber(JsonObject obj, string propertyName, int defaultValue)
    {
        if (!HasPropertyIgnoreCase(obj, propertyName))
            obj[propertyName] = defaultValue;
    }

    private static void EnsureBoolean(JsonObject obj, string propertyName, bool defaultValue)
    {
        if (!HasPropertyIgnoreCase(obj, propertyName))
            obj[propertyName] = defaultValue;
    }

    private static void EnsureArray(JsonObject obj, string propertyName)
    {
        if (!HasPropertyIgnoreCase(obj, propertyName))
            obj[propertyName] = new JsonArray();
    }

    private static bool TryGetPropertyValueIgnoreCase(
        JsonObject obj,
        string propertyName,
        out JsonNode? value)
    {
        foreach ((string key, JsonNode? node) in obj)
        {
            if (key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = node;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void SetPropertyIgnoreCase(JsonObject obj, string propertyName, JsonNode? value)
    {
        foreach ((string key, _) in obj)
        {
            if (key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                obj[key] = value;
                return;
            }
        }

        obj[propertyName] = value;
    }
}
