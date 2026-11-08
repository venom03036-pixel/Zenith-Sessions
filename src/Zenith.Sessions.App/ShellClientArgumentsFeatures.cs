using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>
/// Extra client arguments (R-launch-16): the saved default lives in settings.json (Settings → Roblox installation), an
/// account's own set in its profile (Edit account profile). Both are validated by <see cref="ExtraClientArgumentPolicy"/>
/// and reach only Zenith's direct native player start, after Roblox's own request; the native trial review names the set
/// it will pass. The draft here edits nothing until Save settings. Nothing about the arguments is logged.
/// </summary>
public sealed partial class ShellViewModel
{
    private string _defaultClientArguments = "";
    /// <summary>Draft of the saved default. Applied after Save settings, never from the draft; blank passes none.</summary>
    public string DefaultClientArguments { get => _defaultClientArguments; set { if (Set(ref _defaultClientArguments, value ?? "")) PreferencesEdited(); } }
    private bool DefaultClientArgumentsDraftIsSaved
        => ExtraClientArgumentPolicy.TryParse(_defaultClientArguments, out var draft, out _) && string.Join(' ', draft) == _savedSettings.DefaultClientArguments;

    /// <summary>
    /// What the provider reads at preparation time: the account as saved in the library now and the last saved default,
    /// never an unsaved draft. Runs on the provider's worker; the repository serializes its own access and the saved
    /// settings reference is read once.
    /// </summary>
    private sealed class SavedExtraClientArguments(ShellViewModel owner) : IExtraClientArgumentsSource
    {
        public async Task<IReadOnlyList<string>> ResolveAsync(Guid profileId, CancellationToken ct)
        {
            var profile = await owner._accounts.FindAsync(profileId, ct).ConfigureAwait(false);
            return ExtraClientArgumentPolicy.Effective(profile?.ExtraClientArguments, Volatile.Read(ref owner._savedSettings).DefaultClientArguments);
        }
    }
}
