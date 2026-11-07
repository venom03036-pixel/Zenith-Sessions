using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IMacroAssignmentStore _macroAssignmentStore = null!;
    public MacroAssignmentsViewModel MacroAssignments { get; private set; } = null!;

    private void InitializeMacroAssignments(IMacroAssignmentStore? store)
    {
        _macroAssignmentStore = store ?? new MacroAssignmentStore(DataRoot);
        MacroAssignments = new(_macroAssignmentStore, MacroHostCatalog, ChooseMacroAssignmentTarget,
            ResolveMacroAssignmentTargetNameAsync, CanUseDirectories,
            error => _diagnostics.Record(ManagerEventKind.OperationFailed, error.GetType()));
    }

    private MacroAssignmentTargetChoice? ChooseMacroAssignmentTarget(MacroAssignmentTargetKind kind)
    {
        if (!CanUseDirectories()) return null;
        if (kind == MacroAssignmentTargetKind.Account && SelectedAccount is { } account && Accounts.Contains(account))
            return new(new(kind, account.Profile.Id), account.Label);
        if (kind == MacroAssignmentTargetKind.Group && Group.Value is { } name)
        {
            var group = GroupCatalogChoices.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (group is not null) return new(new(kind, group.Id), group.Name);
        }
        if (kind == MacroAssignmentTargetKind.Bundle && RecipientBundles.CanUseSelected && RecipientBundles.SelectedBundle is { } bundle)
            return new(new(kind, bundle.Id), bundle.Name);
        return null;
    }

    // Explicit stable-ID metadata resolution only. No credential, sign-in, capture or input access.
    private Task<string?> ResolveMacroAssignmentTargetNameAsync(MacroAssignmentTarget target, CancellationToken ct) => Task.Run(async () =>
    {
        target = target.Validated(); ct.ThrowIfCancellationRequested();
        switch (target.Kind)
        {
            case MacroAssignmentTargetKind.Account:
                var profile = await _accounts.FindAsync(target.Id, ct).ConfigureAwait(false);
                if (profile is null) return null;
                profile = profile.Validated();
                if (profile.Id != target.Id) throw new InvalidDataException("The saved account reference is inconsistent. Refresh before assigning a macro.");
                return profile.Label;
            case MacroAssignmentTargetKind.Group:
                if (_accounts is not IAccountGroupCatalog catalog) return null;
                var group = await catalog.FindGroupAsync(target.Id, ct).ConfigureAwait(false);
                if (group is null) return null;
                group = group.Validated();
                if (group.Id != target.Id) throw new InvalidDataException("The saved group reference is inconsistent. Refresh before assigning a macro.");
                return group.Name;
            case MacroAssignmentTargetKind.Bundle:
                var snapshot = await _recipientBundleStore.LoadAsync(ct).ConfigureAwait(false);
                return snapshot.Bundles.FirstOrDefault(item => item.Id == target.Id)?.Validated().Name;
            default: throw new ArgumentException("Choose a saved account, group or recipient bundle.");
        }
    }, ct);

    private async Task DisposeMacroAssignmentsAsync()
    {
        await MacroAssignments.DisposeAsync();
        if (_macroAssignmentStore is IAsyncDisposable owner) await owner.DisposeAsync();
        else if (_macroAssignmentStore is IDisposable disposable) disposable.Dispose();
    }
}
