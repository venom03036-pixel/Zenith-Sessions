using System.IO;
using System.Text.Json;

namespace MacroSuite.App.Common;

/// <summary>Serializes saves of related local preferences and never reports partial saves as complete.</summary>
public static class SettingsSaveCoordinator
{
    private static readonly object SaveGate = new();
    public static bool Save(IEnumerable<Func<bool>> operations)
    {
        lock (SaveGate)
        {
            bool success = true;
            foreach (var operation in operations) success = operation() && success;
            return success;
        }
    }
}

public static class SettingsBootstrapDefaults
{
    /// <summary>Copies only known cosmetic/transport defaults. Existing files are never replaced.</summary>
    public static void Ensure(string configDirectory, string? defaultsDirectory = null)
    {
        Directory.CreateDirectory(configDirectory);
        string sourceDirectory = defaultsDirectory ?? Path.Combine(AppContext.BaseDirectory, "Defaults");
        foreach (string name in new[] { "settings.json", "appearance.json", "autoclicker.json" })
        {
            string target = Path.Combine(configDirectory, name), source = Path.Combine(sourceDirectory, name);
            if (File.Exists(target) || File.Exists(target + ".bak") || !File.Exists(source)) continue;
            if (new FileInfo(source).Length > 1_000_000) throw new InvalidDataException("A preferences default is too large.");
            byte[] bytes = File.ReadAllBytes(source);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A preferences default must be an object.");
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target)) { /* Another startup completed this file first. */ }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
