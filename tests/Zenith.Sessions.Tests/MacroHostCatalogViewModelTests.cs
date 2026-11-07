using System.Diagnostics;
using System.IO;
using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;
using Zenith.Sessions.App;
using Xunit;

namespace Zenith.Sessions.Tests;

// Metadata fixtures only. The cross-runtime worker cannot open a user library or the production pipe.
public sealed class MacroHostCatalogViewModelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly Guid Session = Guid.NewGuid();
    private static SavedMacroDescriptor Macro(int index) => new(new Guid(index + 1, 0, 0, new byte[8]),
        new Guid(index + 1000, 0, 0, new byte[8]), "Fixture " + index, "Ctrl+9", index, true, "Screen");
    private static MacroCatalogPage Page(MacroCatalogQuery query, int total = 1) => new(MacroCatalogState.Ready, Session, 1,
        "2.5.12-fixture", Enumerable.Range(query.Offset, Math.Min(query.Limit, total - query.Offset)).Select(Macro).ToArray(),
        total, query.Offset + Math.Min(query.Limit, total - query.Offset) < total ? query.Offset + query.Limit : null,
        MacroCatalogMessages.For(MacroCatalogState.Ready));

    [Fact]
    public async Task ConstructionDoesNotConnectOrScanAndDisposalOwnsTheClient()
    {
        var client = new Client(); var vm = new MacroHostCatalogViewModel(client, () => true);
        Assert.Equal(0, client.Calls); Assert.Empty(vm.Macros); Assert.False(vm.CanNext);
        await vm.DisposeAsync(); Assert.True(client.Disposed); Assert.False(vm.CanRefresh);
    }

    [Fact]
    public async Task ExplicitPagesPinHostGenerationAndDoNotMixOrReorderRows()
    {
        var queries = new List<MacroCatalogQuery>();
        var client = new Client { Read = (query, _) => { queries.Add(query); return Task.FromResult(Page(query, 101)); } };
        await using var vm = new MacroHostCatalogViewModel(client, () => true);
        await vm.RefreshAsync(); Assert.Equal(100, vm.Macros.Count); Assert.True(vm.CanNext); Assert.False(vm.CanPrevious);
        vm.SelectedMacro = vm.Macros[0];
        await vm.NextAsync(); Assert.Equal(Macro(100), Assert.Single(vm.Macros)); Assert.True(vm.CanPrevious); Assert.False(vm.CanNext);
        Assert.Null(vm.SelectedMacro); Assert.Equal(Session, queries[1].HostSessionId); Assert.Equal(1, queries[1].CatalogRevision); Assert.Equal(100, queries[1].Offset);
        await vm.PreviousAsync(); Assert.Equal(100, vm.Macros.Count); Assert.Equal(Macro(0), vm.Macros[0]); Assert.Equal(3, client.Calls);
    }

    [Theory]
    [InlineData(MacroCatalogState.Disconnected)]
    [InlineData(MacroCatalogState.Incompatible)]
    [InlineData(MacroCatalogState.Busy)]
    [InlineData(MacroCatalogState.Starting)]
    [InlineData(MacroCatalogState.Unavailable)]
    public async Task AnUnavailableHostPreservesItsLastRowsAndOriginalReceiveTimeAsStale(MacroCatalogState state)
    {
        var client = new Client(); await using var vm = new MacroHostCatalogViewModel(client, () => true);
        await vm.RefreshAsync(); string received = vm.ReceivedSummary; var old = Assert.Single(vm.Macros);
        client.Read = (_, _) => Task.FromResult(MacroCatalogPage.Failure(state));
        await vm.RefreshAsync(); Assert.Same(old, Assert.Single(vm.Macros)); Assert.True(vm.IsStale);
        Assert.Equal(received, vm.ReceivedSummary); Assert.Equal(MacroCatalogMessages.For(state), vm.Status);
        Assert.False(vm.CanNext); Assert.False(vm.CanPrevious);
    }

    [Fact]
    public async Task ChangedPaginationRequiresExplicitRefreshInsteadOfSilentlyFetchingANewGeneration()
    {
        var client = new Client { Read = (query, _) => Task.FromResult(Page(query, 101)) };
        await using var vm = new MacroHostCatalogViewModel(client, () => true);
        await vm.RefreshAsync(); client.Read = (_, _) => Task.FromResult(MacroCatalogPage.Failure(MacroCatalogState.Changed));
        await vm.NextAsync(); Assert.Equal(2, client.Calls); Assert.Equal(100, vm.Macros.Count); Assert.True(vm.IsStale); Assert.False(vm.CanNext);
    }

    [Fact]
    public async Task MalformedPeerRowsCannotReplaceAValidSnapshotOrLeakAnErrorMessage()
    {
        var failures = new List<Type>(); var client = new Client();
        await using var vm = new MacroHostCatalogViewModel(client, () => true, error => failures.Add(error.GetType()));
        await vm.RefreshAsync(); var previous = Assert.Single(vm.Macros);
        client.Read = (query, _) => Task.FromResult(Page(query) with { Total = 2, Items = new[] { Macro(0), Macro(0) } });
        await vm.RefreshAsync(); Assert.Same(previous, Assert.Single(vm.Macros)); Assert.True(vm.IsStale); Assert.Single(failures);
        client.Read = (_, _) => throw new IOException("synthetic-private-error-marker");
        await vm.RefreshAsync(); Assert.DoesNotContain("synthetic-private-error-marker", vm.Status);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public async Task BorrowedClientRowsAreFrozenBeforePublication()
    {
        var rows = new[] { Macro(0) }; var client = new Client { Read = (query, _) => Task.FromResult(Page(query) with { Items = rows }) };
        await using var vm = new MacroHostCatalogViewModel(client, () => true); await vm.RefreshAsync();
        rows[0] = Macro(99); Assert.Equal(Macro(0), Assert.Single(vm.Macros));
    }

    [Fact]
    public async Task StopFencesALateClientAndBusyWorkRemainsOwnedUntilActualCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MacroCatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Read = (_, _) => { entered.TrySetResult(); return finish.Task; } };
        await using var vm = new MacroHostCatalogViewModel(client, () => true);
        var active = vm.RefreshAsync(); await entered.Task.WaitAsync(Deadline);
        vm.CancelWork(); await vm.RefreshAsync(); Assert.Equal(1, client.Calls); Assert.True(vm.IsBusy); Assert.False(active.IsCompleted);
        finish.SetResult(Page(new())); await active.WaitAsync(Deadline);
        Assert.Empty(vm.Macros); Assert.False(vm.IsBusy); Assert.True(vm.IsStale); Assert.Contains("stopped", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LosingControlPermissionSuppressesPublicationWithoutAHiddenRetry()
    {
        bool allowed = true; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MacroCatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Read = (_, _) => { entered.TrySetResult(); return finish.Task; } };
        await using var vm = new MacroHostCatalogViewModel(client, () => allowed);
        var active = vm.RefreshAsync(); await entered.Task.WaitAsync(Deadline); allowed = false;
        finish.SetResult(Page(new())); await active.WaitAsync(Deadline);
        Assert.Empty(vm.Macros); Assert.Equal(1, client.Calls); Assert.False(vm.CanRefresh); Assert.True(vm.IsStale);
    }

    [Fact]
    public async Task CallerCancellationReportsAStoppedRequestRatherThanAnEndlessReadingStatus()
    {
        var client = new Client { Read = async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Page(new()); } };
        await using var vm = new MacroHostCatalogViewModel(client, () => true); using var stop = new CancellationTokenSource();
        var active = vm.RefreshAsync(stop.Token); stop.Cancel(); await active.WaitAsync(Deadline);
        Assert.False(vm.IsBusy); Assert.Empty(vm.Macros); Assert.True(vm.IsStale); Assert.Contains("cancelled", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposalJoinsAnUncooperativeClientBeforeDisposingItsOwner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MacroCatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Read = (_, _) => { entered.TrySetResult(); return finish.Task; } };
        var vm = new MacroHostCatalogViewModel(client, () => true); var active = vm.RefreshAsync();
        await entered.Task.WaitAsync(Deadline); var dispose = vm.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted); Assert.False(client.Disposed);
        finish.SetResult(Page(new())); await Task.WhenAll(active, dispose).WaitAsync(Deadline);
        Assert.True(client.Disposed); Assert.Empty(vm.Macros); Assert.False(vm.CanRefresh);
    }

    [Fact]
    public async Task ReentrantStopFromBusyPublicationCannotBeOverwrittenByReadingOrSuccess()
    {
        var client = new Client(); await using var vm = new MacroHostCatalogViewModel(client, () => true);
        bool stopped = false;
        vm.PropertyChanged += (_, change) => { if (!stopped && change.PropertyName == nameof(vm.IsBusy) && vm.IsBusy) { stopped = true; vm.CancelWork(); } };
        await vm.RefreshAsync().WaitAsync(Deadline);
        Assert.True(stopped); Assert.Equal(0, client.Calls); Assert.Empty(vm.Macros); Assert.True(vm.IsStale);
        Assert.Contains("stopped", vm.Status, StringComparison.OrdinalIgnoreCase); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ReentrantDisposalFromBusyPublicationJoinsTheNewWorkBeforeDisposingTheClient()
    {
        var client = new Client(); var vm = new MacroHostCatalogViewModel(client, () => true); Task? disposal = null;
        vm.PropertyChanged += (_, change) => { if (disposal is null && change.PropertyName == nameof(vm.IsBusy) && vm.IsBusy) disposal = vm.DisposeAsync().AsTask(); };
        var active = vm.RefreshAsync(); Assert.NotNull(disposal);
        await Task.WhenAll(active, disposal!).WaitAsync(Deadline);
        Assert.Equal(0, client.Calls); Assert.True(client.Disposed); Assert.Empty(vm.Macros); Assert.False(vm.CanRefresh);
    }

    [Fact]
    public async Task ReentrantStopDuringRowNotificationKeepsTheCommittedPageStaleAndItsStopNotice()
    {
        var client = new Client(); await using var vm = new MacroHostCatalogViewModel(client, () => true);
        bool stopped = false;
        vm.PropertyChanged += (_, change) => { if (!stopped && change.PropertyName == nameof(vm.Macros) && vm.Macros.Count != 0) { stopped = true; vm.CancelWork(); } };
        await vm.RefreshAsync().WaitAsync(Deadline);
        Assert.True(stopped); Assert.Single(vm.Macros); Assert.True(vm.IsStale);
        Assert.Contains("stopped", vm.Status, StringComparison.OrdinalIgnoreCase); Assert.False(vm.CanNext);
    }

    [Fact]
    public async Task Net10ClientReadsTheRealPipeFromAnIsolatedNet8Worker()
    {
        string path = TestWorkerLocator.Find("Zenith.MacroHost.ProbeWorker", "net8.0-windows10.0.19041.0");
        string pipe = MacroHostProtocol.PipeName + ".fixture-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipe);
        using var process = Process.Start(start)!;
        try
        {
            await ReadReadyLineAsync(process);
            await using var client = new MacroCatalogPipeClient(pipe);
            var page = await client.GetPageAsync(new(), default).WaitAsync(Deadline);
            Assert.Equal(MacroCatalogState.Ready, page.State); Assert.Equal("2.5.12-fixture", page.HostVersion);
            Assert.Equal("Cross-runtime fixture", Assert.Single(page.Items).Name);
            await process.StandardInput.WriteLineAsync("EXIT");
            await process.WaitForExitAsync().WaitAsync(Deadline); Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            { try { await process.StandardInput.WriteLineAsync("EXIT"); await process.WaitForExitAsync().WaitAsync(Deadline); }
                catch (Exception) { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(Deadline); } } }
        }
    }

    [Fact]
    public async Task ProbeWorkerThatExitsBeforeReadyReportsItsExitCode()
    {
        string path = TestWorkerLocator.Find("Zenith.MacroHost.ProbeWorker", "net8.0-windows10.0.19041.0");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add("bad"); // The real worker rejects a non-fixture pipe with exit code 2 and prints nothing.
        using var process = Process.Start(start)!;
        try
        {
            var error = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => ReadReadyLineAsync(process));
            Assert.Contains("closed its output without printing READY NET8", error.Message, StringComparison.Ordinal);
            Assert.Contains("exit code 2 (0x00000002), bad arguments", error.Message, StringComparison.Ordinal);
            Assert.Contains("no standard error output", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(Deadline); }
        }
    }

    // Reads the probe's READY line; on anything else the failure names what was read, the exit code and the stderr tail.
    private static async Task ReadReadyLineAsync(Process process)
    {
        string problem;
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
            if (line == "READY NET8") return;
            problem = line is null ? "closed its output without printing READY NET8"
                : "printed \"" + (line.Length > 200 ? line[..200] + "..." : line) + "\" instead of READY NET8";
        }
        catch (TimeoutException)
        { problem = "printed nothing within " + Deadline.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s"; }
        Assert.Fail("The probe worker " + problem + ": " + await TestWorkerLocator.DescribeExitAsync(process, true, ProbeExitMeaning) + ".");
    }

    // The probe's own codes, from tools\Zenith.MacroHost.ProbeWorker\Program.cs.
    private static string? ProbeExitMeaning(int code) => code switch
    { 2 => "bad arguments", 3 => "unexpected input instead of EXIT", 4 => "timed out waiting for EXIT", _ => null };

    private sealed class Client : IZenithMacroCatalogClient, IAsyncDisposable
    {
        public int Calls; public bool Disposed;
        public Func<MacroCatalogQuery, CancellationToken, Task<MacroCatalogPage>> Read = (query, _) => Task.FromResult(Page(query));
        public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default)
        { Calls++; return Read(query, ct); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
