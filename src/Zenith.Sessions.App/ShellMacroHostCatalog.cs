using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    public MacroHostCatalogViewModel MacroHostCatalog { get; private set; } = null!;
    private void InitializeMacroHostCatalog(IZenithMacroCatalogClient? client)
    {
        bool permitted = !IsSample || client is not null;
        MacroHostCatalog = new(client ?? new MacroCatalogPipeClient(), () => permitted && CanUseDirectories(),
            error => _diagnostics.Record(ManagerEventKind.OperationFailed, error.GetType()));
    }
}
