using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;

namespace Zenith.Sessions.App;

// Real current-user named-pipe round trip with an owned synthetic catalog; never the Personal endpoint/library.
internal static class PackagedMacroHostLinkCheck
{
    public static async Task VerifyAsync(CancellationToken ct)
    {
        string pipe = MacroHostProtocol.PipeName + ".fixture-" + Guid.NewGuid().ToString("N");
        var source = new SavedMacroCatalog("2.5.12-fixture");
        var id = Guid.NewGuid();
        var original = new SavedMacroDescriptor(id, Guid.NewGuid(), "Packaged metadata fixture", "Ctrl+Shift+9", 3, true, "Legacy");
        var loading = source.BeginLoad();
        if (!source.CompleteLoad(loading, new[] { original })) throw new InvalidDataException("The owned catalog fixture could not load.");
        // The client is shut down before the server, as nested `await using` declarations would.
        await SelfCheckOwners.UseAsync(new MacroCatalogPipeServer(source, pipe), _ =>
            SelfCheckOwners.UseAsync(new MacroCatalogPipeClient(pipe), client => ExerciseAsync(source, client, id, original, ct)));
    }

    private static async Task ExerciseAsync(SavedMacroCatalog source, MacroCatalogPipeClient client, Guid id, SavedMacroDescriptor original, CancellationToken ct)
    {
        var page = await client.GetPageAsync(new(), ct);
        if (page.State != MacroCatalogState.Ready || page.Items.Count != 1 || page.Items[0] != original)
            throw new InvalidDataException("The packaged metadata pipe did not preserve the saved reference.");
        var save = source.BeginSave(id) ?? throw new InvalidDataException("The owned fixture could not begin its saved update.");
        var updated = original with { SavedRevision = save.SavedRevision, Name = "Updated packaged metadata fixture" };
        if (!source.PublishSaved(save, updated)) throw new InvalidDataException("The owned saved update was rejected.");
        var changed = await client.GetPageAsync(new(0, 100, page.HostSessionId, page.CatalogRevision), ct);
        if (changed.State != MacroCatalogState.Changed || changed.Items.Count != 0)
            throw new InvalidDataException("The metadata pipe mixed saved catalog generations.");
        var fresh = await client.GetPageAsync(new(), ct);
        if (fresh.State != MacroCatalogState.Ready || fresh.Items.Count != 1 || fresh.Items[0] != updated)
            throw new InvalidDataException("An explicit refresh did not return the current saved reference.");
    }
}
