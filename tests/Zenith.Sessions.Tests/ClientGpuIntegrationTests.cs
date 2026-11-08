using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Per-client GPU numbers (R-instances-15) from the saved option to the Sessions row: off by default, applied to the monitor
/// when saved, shown only for a fresh snapshot's attributed client, and never as a number for anything unknown.
/// </summary>
public sealed class ClientGpuIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-client-gpu-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task PerClientGpuIsOffByDefaultAndSavingTheOptionConfiguresTheMonitorAndShowsNumbersOnTheRow() => OnUi(async () =>
    {
        var monitor = new GpuMonitor();
        await using var shell = await Ready(monitor);
        Assert.False(shell.ShowClientGpu);
        Assert.True(monitor.ConfigureCalls >= 1); Assert.False(monitor.Enabled); // Startup applies the saved choice: off.
        Assert.Equal(2, shell.Sessions.Count);
        Assert.All(shell.Sessions, row => Assert.Equal("Off in Settings", row.Gpu));
        shell.ShowClientGpu = true;
        Assert.Equal("Unsaved settings", shell.SaveStatus);
        shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.True(monitor.Enabled);
        Assert.Contains("\"ShowClientGpu\":true", File.ReadAllText(Path.Combine(_root, "settings.json")));
        await Refresh(shell);
        var attributed = shell.Sessions.Single(row => row.Observation.ProcessId == 101);
        Assert.Equal(12.3.ToString("0.0", CultureInfo.CurrentCulture) + "% busiest engine · " + 0.5.ToString("0.00", CultureInfo.CurrentCulture) + " GiB dedicated", attributed.Gpu);
        Assert.Equal("Unavailable", shell.Sessions.Single(row => row.Observation.ProcessId == 102).Gpu);
        // Expired GPU counters show no number, and a client row is matched by PID and start time, never by PID alone.
        monitor.GpuAge = TimeSpan.FromSeconds(4); await Refresh(shell);
        Assert.Equal("Unavailable", attributed.Gpu);
        monitor.GpuAge = TimeSpan.Zero; monitor.StartOffset = 1; await Refresh(shell);
        Assert.Equal("Unavailable", attributed.Gpu);
        monitor.StartOffset = 0; await Refresh(shell);
        Assert.StartsWith(12.3.ToString("0.0", CultureInfo.CurrentCulture), attributed.Gpu);
        shell.ShowClientGpu = false; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.False(monitor.Enabled); await Refresh(shell);
        Assert.Equal("Off in Settings", attributed.Gpu);
    });

    [Fact]
    public void TheSessionsTableHasAGpuColumnBesideCpuAndSettingsHasTheOption()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        var document = XDocument.Load(source!);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml", p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var page = Assert.Single(document.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "SessionsPage"));
        var table = Assert.Single(page.Descendants(p + "ListView").Where(l => (string?)l.Attribute("ItemsSource") == "{Binding Sessions}"));
        var columns = table.Descendants(p + "GridViewColumn").ToList();
        int cpu = columns.FindIndex(c => (string?)c.Attribute("Header") == "CPU (%)");
        int gpu = columns.FindIndex(c => (string?)c.Attribute("DisplayMemberBinding") == "{Binding Gpu}");
        Assert.True(cpu >= 0, "The CPU column was not found."); Assert.Equal(cpu + 1, gpu);
        Assert.StartsWith("GPU", (string?)columns[gpu].Attribute("Header"));
        var option = Assert.Single(document.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "SettingsClientGpu"));
        Assert.Equal("CheckBox", option.Name.LocalName);
        Assert.Equal("{Binding ShowClientGpu}", (string?)option.Attribute("IsChecked"));
    }

    [Fact]
    public async Task SettingsWithoutTheOptionLoadAsOffAndTheSavedOptionIsReadBack()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{\"Version\":1}");
        using var store = new SettingsStore(_root);
        var (loaded, notice) = await store.LoadAsync(default);
        Assert.False(loaded.ShowClientGpu); Assert.Null(notice);
        await store.SaveAsync(loaded with { ShowClientGpu = true }, default);
        Assert.True((await store.LoadAsync(default)).Settings.ShowClientGpu);
    }

    private async Task<ShellViewModel> Ready(IDesktopMonitor monitor)
    {
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor);
        await vm.InitializeAsync(); await Refresh(vm);
        Assert.True(vm.IsReady, vm.Notice);
        return vm;
    }
    private static async Task Refresh(ShellViewModel shell)
    { shell.Refresh.Execute(null); await shell.Refresh.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }

    /// <summary>Two synthetic clients; the GPU snapshot attributes numbers to client 101 only, with an adjustable age and start time.</summary>
    private sealed class GpuMonitor : IDesktopMonitor, IClientGpuTelemetryControl
    {
        private static readonly long Start = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        public bool Enabled; public int ConfigureCalls; public TimeSpan GpuAge; public long StartOffset;
        public void ConfigureClientGpuTelemetry(bool enabled) { Enabled = enabled; ConfigureCalls++; }
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var now = DateTimeOffset.UtcNow;
            var gpu = new GpuTelemetrySnapshot(now - GpuAge, [], null, [new(101, Start + StartOffset, 12.3, CapacityPolicy.GiB / 2)]);
            var resources = new ResourceSnapshot(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10, Gpu: gpu);
            return Task.FromResult(new DesktopSnapshot(resources, [Row(101, now), Row(102, now)], null, now, 1, true));
        }
        private static SessionObservation Row(int pid, DateTimeOffset now) => new(pid, Start, "Synthetic", pid, "", false, true, 1, 1, null, now);
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnUi(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => operation = Run());
            async Task Run()
            {
                try { await work(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-client-gpu-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
