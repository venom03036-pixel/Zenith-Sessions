using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;

// Isolated cross-runtime fixture only. It cannot serve the production endpoint or read a user library.
if (args is ["--ownership-window-fixture"]) return OwnershipWindowFixture.Run();

if (args is ["--foreground-pipe", var foregroundName]
    && foregroundName.StartsWith(ForegroundControlProtocol.PipeName + ".fixture-", StringComparison.Ordinal)
    && foregroundName.Length <= 150)
    return await RunForegroundFixtureAsync(foregroundName);

if (args.Length != 2 || args[0] != "--pipe" || !args[1].StartsWith(MacroHostProtocol.PipeName + ".fixture-", StringComparison.Ordinal)
    || args[1].Length > 150) return 2;
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
await using var server = new MacroCatalogPipeServer(new FixtureSource(), args[1]);
Console.WriteLine("READY NET" + Environment.Version.Major);
try
{
    string? line = await Console.In.ReadLineAsync(stop.Token);
    if (line != "EXIT") return 3;
}
catch (OperationCanceledException) { return 4; }
finally { server.SignalStop(); }
return 0;

static async Task<int> RunForegroundFixtureAsync(string name)
{
    var run = new FixtureForeground();
    int starts = 0;
    await using var server = new ForegroundControlPipeServer((peer, request, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        if (peer <= 0 || request.HostSessionId != new Guid("6ab58055-c06d-4c29-9f6d-b0b2af79ad27")
            || request.MacroId != new Guid("708d8d3b-36c7-4f12-a3db-c7712b091504")
            || request.SavedRevision != new Guid("e4b2bb83-7d49-4447-bfdc-aeac8c4c829e")
            || request.Window != 0x123456 || request.ProcessId != 4242 || request.CreationFileTime != 99)
            return Task.FromResult(new ForegroundControlAdmission(null, ForegroundControlOutcome.InvalidRequest));
        Interlocked.Increment(ref starts);
        return Task.FromResult(new ForegroundControlAdmission(run, ForegroundControlOutcome.None));
    }, name);
    // This one-time synthetic code is sent exclusively to the parent test over redirected stdout.
    Console.WriteLine("READY " + server.TakePairingCode());
    try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(12)); }
    catch (TimeoutException) { return 4; }
    return starts == 1 && run.Completion.IsCompletedSuccessfully ? 0 : 5;
}

sealed class FixtureForeground : IForegroundControlOperation
{
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _paused;
    public Guid Id { get; } = Guid.NewGuid();
    public Task Completion => _stopped.Task;
    public ForegroundControlSnapshot Snapshot => Completion.IsCompleted
        ? new(ForegroundControlState.Finished, ForegroundControlOutcome.Stopped)
        : new(_paused ? ForegroundControlState.Paused : ForegroundControlState.Running);
    public Task<bool> PauseAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); _paused = true; return Task.FromResult(true); }
    public Task<bool> ResumeAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); _paused = false; return Task.FromResult(true); }
    public void Stop() => _stopped.TrySetResult();
}

sealed class FixtureSource : IMacroCatalogSource
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly SavedMacroDescriptor _macro = new(new("708d8d3b-36c7-4f12-a3db-c7712b091504"),
        new("e4b2bb83-7d49-4447-bfdc-aeac8c4c829e"), "Cross-runtime fixture", "Ctrl+Shift+9", 3, true, "Screen");
    public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); query.Validated();
        if (query.HostSessionId is { } id && id != _session || query.CatalogRevision is { } revision && revision != 1)
            return Task.FromResult(MacroCatalogPage.Failure(MacroCatalogState.Changed));
        var page = new MacroCatalogPage(MacroCatalogState.Ready, _session, 1, "2.5.12-fixture",
            query.Offset == 0 ? new[] { _macro } : Array.Empty<SavedMacroDescriptor>(), 1, null, MacroCatalogMessages.For(MacroCatalogState.Ready));
        return Task.FromResult(page.Validated(query));
    }
}
