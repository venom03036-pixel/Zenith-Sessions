using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;

// Isolated cross-runtime fixture only. It cannot serve the production endpoint or read a user library.
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
