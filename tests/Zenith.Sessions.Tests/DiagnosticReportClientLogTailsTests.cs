using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Diagnostics > Save report with "Include Roblox client log entries", over a fake tail reader: off by default, the copy
/// never includes them, a ticked save adds typed entries only and says so, the tick clears after that save, Panic during the
/// read writes nothing, and the sample profile cannot turn it on without an injected reader.
/// </summary>
public sealed class DiagnosticReportClientLogTailsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-logtails-ui-tests-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string ReportPath { get { string folder = Path.Combine(_root, "share"); Directory.CreateDirectory(folder); return Path.Combine(folder, "report.json"); } }
    private static readonly DateTimeOffset Logged = new(2026, 11, 8, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid Job = Guid.Parse("7c2a2a7e-2d9e-4a3b-9d1e-0f6c1a2b3c4d");

    private static ClientLogTailReport Report(int folders = 2, params ClientLogTail[] logs)
        => new(DateTimeOffset.UtcNow, folders, 0, logs.Length, false, logs);

    private static ClientLogTail Tail(int order, params ClientLogTailEntry[] entries)
        => new(order, false, DateTimeOffset.UtcNow.AddMinutes(-order), 4096, 4096, entries.Length, 0, ClientLogTailOutcome.Read, entries);

    [Fact]
    public Task TheOptionIsOffByDefaultAndAReportWithoutItCarriesNoClientLogEntries() => OnUi(async () =>
    {
        var probe = new TailProbe { Report = Report(2, Tail(1, new ClientLogTailEntry(Logged, ClientLogFact.Joining, 123, Job))) };
        await using var vm = await Ready(probe);
        Assert.False(vm.IncludeClientLogTails);
        Assert.True(vm.CanIncludeClientLogTails);

        string path = ReportPath;
        vm.ExportDiagnostics.Execute(path); await vm.ExportDiagnostics.Execution;

        Assert.Equal(0, probe.Reads);
        Assert.Contains("report saved", vm.Notice);
        Assert.DoesNotContain("client log", vm.Notice);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = parsed.RootElement;
        Assert.Equal(3, root.GetProperty("FormatVersion").GetInt32());
        Assert.False(root.GetProperty("ClientLogs").GetProperty("Included").GetBoolean());
        Assert.Contains("no prior-session replay or raw log files", root.GetProperty("Scope").GetString());
    });

    [Fact]
    public Task TickingTheOptionAddsTypedEntriesOnlySaysSoAndClearsTheOption() => OnUi(async () =>
    {
        var probe = new TailProbe
        {
            Report = Report(2, Tail(1,
                new ClientLogTailEntry(Logged, ClientLogFact.Joining, 123456789, Job),
                new ClientLogTailEntry(Logged.AddSeconds(1), ClientLogFact.UserIdentityObserved),
                new ClientLogTailEntry(Logged.AddSeconds(2), ClientLogFact.Connected),
                new ClientLogTailEntry(Logged.AddMinutes(5), ClientLogFact.Disconnected, DisconnectReason: 267)))
        };
        await using var vm = await Ready(probe);
        vm.IncludeClientLogTails = true;

        // Copy diagnostics is the clipboard report: the tick applies to Save report only.
        using (var copied = JsonDocument.Parse(vm.BuildDiagnosticReport()))
            Assert.False(copied.RootElement.GetProperty("ClientLogs").GetProperty("Included").GetBoolean());
        Assert.Equal(0, probe.Reads);

        string path = ReportPath;
        vm.ExportDiagnostics.Execute(path); await vm.ExportDiagnostics.Execution;

        Assert.Equal(1, probe.Reads);
        Assert.Contains("report saved", vm.Notice);
        Assert.Contains("4 recognised entries from 1 recent Roblox client log", vm.Notice);
        Assert.Contains("no raw log lines, player IDs, URLs or paths", vm.Notice);
        Assert.False(vm.IncludeClientLogTails);
        string text = await File.ReadAllTextAsync(path);
        using var parsed = JsonDocument.Parse(text);
        var root = parsed.RootElement;
        Assert.Equal(3, root.GetProperty("FormatVersion").GetInt32());
        Assert.Contains("parsed fields only", root.GetProperty("Scope").GetString());
        var clientLogs = root.GetProperty("ClientLogs");
        Assert.True(clientLogs.GetProperty("Included").GetBoolean());
        Assert.Equal(2, clientLogs.GetProperty("FoldersChecked").GetInt32());
        Assert.Equal(1, clientLogs.GetProperty("LogsFound").GetInt32());
        var log = Assert.Single(clientLogs.GetProperty("Logs").EnumerateArray());
        Assert.Equal(1, log.GetProperty("Order").GetInt32());
        Assert.Equal("Read", log.GetProperty("Outcome").GetString());
        var entries = log.GetProperty("Entries").EnumerateArray().ToArray();
        Assert.Equal(4, entries.Length);
        Assert.Equal("Joining", entries[0].GetProperty("Fact").GetString());
        Assert.Equal(123456789, entries[0].GetProperty("PlaceId").GetInt64());
        Assert.Equal(Job, entries[0].GetProperty("JobId").GetGuid());
        Assert.Equal(Logged, entries[0].GetProperty("LoggedUtc").GetDateTimeOffset());
        Assert.Equal("Disconnected", entries[3].GetProperty("Fact").GetString());
        Assert.Equal(267, entries[3].GetProperty("DisconnectReason").GetInt32());
        Assert.DoesNotContain("\"UserId\"", text); // The identity line's player ID is dropped; only its kind remains.
        Assert.DoesNotContain(Data, text);
    });

    [Fact]
    public Task PanicDuringTheClientLogReadWritesNothingAndKeepsTheStopNotice() => OnUi(async () =>
    {
        var probe = new TailProbe { Report = Report(2, Tail(1)), Block = true };
        await using var vm = await Ready(probe);
        vm.IncludeClientLogTails = true;
        string path = ReportPath;

        vm.ExportDiagnostics.Execute(path);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Panic.Execute(null);
        await vm.ExportDiagnostics.Execution;

        Assert.True(probe.LastToken.IsCancellationRequested);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.StartsWith("Panic stop", vm.Notice, StringComparison.Ordinal);
    });

    [Fact]
    public Task AnEmptyReadIsSaidPlainly() => OnUi(async () =>
    {
        var probe = new TailProbe { Report = Report(0) };
        await using var vm = await Ready(probe);
        string path = ReportPath;

        vm.IncludeClientLogTails = true;
        vm.ExportDiagnostics.Execute(path); await vm.ExportDiagnostics.Execution;
        Assert.Contains("No Roblox logs folder was found for this Windows user", vm.Notice);

        probe.Report = Report(2);
        vm.IncludeClientLogTails = true;
        vm.ExportDiagnostics.Execute(path); await vm.ExportDiagnostics.Execution;
        Assert.Contains("No Roblox Player log written in the last day was found", vm.Notice);

        probe.Report = Report(2, Tail(1), Tail(2));
        vm.IncludeClientLogTails = true;
        vm.ExportDiagnostics.Execute(path); await vm.ExportDiagnostics.Execution;
        Assert.Contains("No recognised entries were found in 2 recent Roblox client logs", vm.Notice);
        Assert.Equal(3, probe.Reads);
    });

    [Fact]
    public Task SampleModeWithoutAnInjectedReaderKeepsTheOptionOff() => OnUi(async () =>
    {
        await using var vm = await Ready(null, sample: true);
        Assert.False(vm.CanIncludeClientLogTails);
        vm.IncludeClientLogTails = true;
        Assert.False(vm.IncludeClientLogTails);

        string path = ReportPath;
        vm.ExportDiagnostics.Execute(path); await vm.ExportDiagnostics.Execution;
        Assert.Contains("report saved", vm.Notice);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.False(parsed.RootElement.GetProperty("ClientLogs").GetProperty("Included").GetBoolean());
    });

    private async Task<ShellViewModel> Ready(IClientLogTailReader? tails, bool sample = false)
    {
        var shell = new ShellViewModel(Data, sample, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), clientLogTails: tails);
        try { await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice); return shell; }
        catch { await shell.DisposeAsync(); throw; }
    }

    private sealed class TailProbe : IClientLogTailReader
    {
        public ClientLogTailReport? Report;
        public int Reads;
        public bool Block;
        public CancellationToken LastToken;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ClientLogTailReport> ReadAsync(CancellationToken ct)
        {
            Reads++; LastToken = ct;
            if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            return Report ?? throw new InvalidOperationException("No synthetic tail report was prepared.");
        }
    }

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), []));
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnUi(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => operation = RunAsync());
            async Task RunAsync()
            {
                try { await work(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-logtails-ui-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
