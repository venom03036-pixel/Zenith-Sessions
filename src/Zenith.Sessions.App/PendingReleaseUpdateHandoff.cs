using System.Diagnostics;
using System.Text.Json;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

internal static class PendingReleaseUpdateHandoff
{
    internal static bool Offer(string root, string applicationFolder, Func<string, bool> confirm, Action<ProcessStartInfo> launch)
    {
        string statePath = Path.Combine(root, "release-updates.json");
        var state = ReleaseUpdateSession.ReadState(statePath);
        if (!state.InstallOnNextStart || state.Pending is null) return false;
        ReleaseUpdateSession.Validate(state.Settings);
        if (!Version.TryParse(state.Pending.Version, out _) || !ReleaseUpdateSession.Configured(state.Settings)) throw new InvalidDataException("Invalid pending update approval.");
        string updateRoot = Path.GetFullPath(Path.Combine(root, "Updates")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(state.Pending.Directory).StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pending update is outside its owned folder.");
        bool approved = confirm(state.Pending.Version);
        // Consume the approval before launching; a failure remains recoverable through the still-retained pending update.
        string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state with { InstallOnNextStart = false })); File.Move(temporary, statePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (!approved) return false;
        string installer = Path.Combine(applicationFolder, "Install-ZenithSessions.ps1"), tool = Path.Combine(applicationFolder, "Zenith.Sessions.ReleaseTool.exe");
        if (!File.Exists(installer) || !File.Exists(tool)) throw new FileNotFoundException("The release installer/tool are not packaged in this development build.");
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = applicationFolder };
        foreach (string arg in new[] { "-NoProfile", "-File", installer, "-ReleaseTool", tool, "-FeedUri", state.Settings.FeedUri!, "-PublisherCertificateSha256", state.Settings.PublisherCertificateSha256!,
            "-WaitForProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "-WaitForProcessStartTicks", current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        launch(start); return true;
    }
}
