using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MacroSuite.App.Common;

public enum SettingsLoadKind { Defaults = 0, Loaded = 1, Missing = 2, Corrupt = 3, Inaccessible = 4, Recovered = 5 }
public sealed class SettingsLoadResult
{
    public SettingsLoadKind Kind { get; init; }
    public JsonDocument? Document { get; init; }
    public string? Message { get; init; }
    public bool TrustSafetyCritical { get; init; }
}

/// <summary>Serialized atomic preferences with validated backups and visible recovery.</summary>
public static class SettingsJsonStore
{
    private const int MaximumBytes = 1_000_000;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static SettingsLoadResult Load(string configPath, Func<JsonElement, bool>? validate = null)
    {
        string path = Path.GetFullPath(configPath);
        lock (Gates.GetOrAdd(path, _ => new object()))
        {
            SettingsLoadKind kind; string message;
            try { return new() { Kind = SettingsLoadKind.Loaded, Document = Read(path, validate), TrustSafetyCritical = true }; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { kind = SettingsLoadKind.Missing; message = "Settings file was not found; using defaults."; }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            { kind = SettingsLoadKind.Corrupt; message = "Settings file was corrupt" + (Quarantine(path) ? " and was quarantined: " : ": ") + ex.Message; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new() { Kind = SettingsLoadKind.Inaccessible, Message = $"Settings file could not be read: {ex.Message}", TrustSafetyCritical = false }; }
            try
            {
                var backup = Read(path + ".bak", validate);
                string recovery = "Recovered the last saved settings from backup. Review your preferences.";
                try { SaveCore(path, backup.RootElement.GetRawText(), validate); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { recovery = $"Last saved settings were loaded from backup, but the main file could not be repaired: {ex.Message}"; }
                return new() { Kind = SettingsLoadKind.Recovered, Document = backup, Message = recovery, TrustSafetyCritical = false };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { return new() { Kind = kind, Message = message, TrustSafetyCritical = false }; }
        }
    }

    public static void SaveAtomic(string configPath, string json, Func<JsonElement, bool>? validate = null)
    {
        string path = Path.GetFullPath(configPath);
        lock (Gates.GetOrAdd(path, _ => new object())) SaveCore(path, json, validate);
    }
    private static JsonDocument Parse(string json, Func<JsonElement, bool>? validate)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("The settings file exceeds its size limit.");
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || validate?.Invoke(doc.RootElement) == false)
        { doc.Dispose(); throw new InvalidDataException("The settings document is not valid for this version."); }
        return doc;
    }
    private static JsonDocument Read(string path, Func<JsonElement, bool>? validate)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The settings file exceeds its size limit.");
        return Parse(File.ReadAllText(path), validate);
    }
    private static void SaveCore(string path, string json, Func<JsonElement, bool>? validate)
    {
        using var validated = Parse(json, validate);
        string? directory = Path.GetDirectoryName(path); if (directory is not null) Directory.CreateDirectory(directory);
        bool currentValid = false;
        if (File.Exists(path))
        {
            try { using var current = Read(path, validate); currentValid = true; }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { Quarantine(path); }
        }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(Encoding.UTF8.GetBytes(json)); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, currentValid ? path + ".bak" : null, true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool Quarantine(string path)
    {
        try { if (!File.Exists(path)) return false; File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N") + ".bak", false); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
