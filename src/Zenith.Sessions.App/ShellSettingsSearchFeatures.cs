using System.Collections.ObjectModel;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private string _settingsSearchQuery="";
    private string _settingsSearchStatus="Find themes, RAM, launch timing, sign-in, tile layout, client priority or diagnostics filters. Ctrl+F focuses this search while Settings is open.";
    private SettingsSearchEntry? _selectedSettingResult;
    public ObservableCollection<SettingsSearchEntry> SettingsSearchResults {get;}=[];
    public event Action<SettingsSearchEntry>? SettingsSearchNavigationRequested;
    public Command OpenSettingSearchResult {get;private set;}=null!;
    public Command ClearSettingsSearch {get;private set;}=null!;
    public string SettingsSearchQuery
    {
        get=>_settingsSearchQuery;
        set
        {
            if(value is null)value="";
            if(value.Length>SettingsSearchCatalog.MaximumQueryLength)
            {SettingsSearchStatus="Use up to 160 characters to find a setting.";Raise();return;}
            if(!Set(ref _settingsSearchQuery,value))return;
            SettingsSearchResults.Clear();SelectedSettingSearchResult=null;
            foreach(var entry in SettingsSearchCatalog.Search(value))SettingsSearchResults.Add(entry);
            Raise(nameof(HasSettingsSearchQuery));
            SettingsSearchStatus=value.Length==0?"Find themes, RAM, launch timing, sign-in, tile layout or client priority. Search never changes your preferences."
                :SettingsSearchResults.Count==0?"No matching settings. Use a short description, such as spare RAM or launch spacing."
                :$"{SettingsSearchResults.Count} matching topics. Open a result to move to its controls; your draft stays unchanged.";
        }
    }
    public string SettingsSearchStatus {get=>_settingsSearchStatus;private set=>Set(ref _settingsSearchStatus,value);}
    public bool HasSettingsSearchQuery=>_settingsSearchQuery.Length>0;
    public SettingsSearchEntry? SelectedSettingSearchResult
    {get=>_selectedSettingResult;set=>Set(ref _selectedSettingResult,value is null||SettingsSearchResults.Contains(value)?value:null);}
    private void InitializeSettingsSearch()
    {
        OpenSettingSearchResult=new(parameter=>
        {
            if(IsLocked||_shuttingDown||!IsSettings)return;
            var entry=parameter as SettingsSearchEntry??SelectedSettingSearchResult;
            if(entry is null||!SettingsSearchResults.Contains(entry))return;
            SelectedSettingSearchResult=entry;
            // A preference on Sessions or Diagnostics opens that page first, exactly like its navigation button, so the
            // window can open its section and focus the control. Nothing is saved, switched or started.
            Tab=SettingsSearchCatalog.PageOf(entry.Target);
            SettingsSearchNavigationRequested?.Invoke(entry);
        });
        ClearSettingsSearch=new(_=>{SettingsSearchQuery="";SettingsSearchResults.Clear();SelectedSettingSearchResult=null;});
    }
    public void ReportSettingSearchNavigation(SettingsSearchEntry entry,bool fallback,bool focused=true)
    {
        if(!SettingsSearchResults.Contains(entry)||IsLocked||_shuttingDown)return;
        SettingsSearchStatus=fallback?"Custom timing is shown only for the Custom profile. Choose it yourself if wanted; search did not change the current profile or draft."
            :focused?$"Opened {entry.Title}. Search did not change or save your preferences."
            :$"Shown {entry.Title}. Keyboard focus was unavailable; select its control. Search did not change or save your preferences.";
        // The status line above stays on the Settings page, so a result on another page also reports in the footer.
        string page=SettingsSearchCatalog.PageOf(entry.Target);
        if(page!="Settings")
            Inform(focused?$"Opened {entry.Title} on {page}. Search did not change or save your preferences."
                :$"Shown {entry.Title} on {page}. Keyboard focus was unavailable; select its control. Search did not change or save your preferences.");
    }
    private void DisposeSettingsSearch()
    {SettingsSearchQuery="";SettingsSearchResults.Clear();SelectedSettingSearchResult=null;SettingsSearchNavigationRequested=null;}
}

/// <summary>Where a result outside the Settings page lands in MainWindow.xaml: the Expander to open (if any) and the
/// control to focus, by x:Name. The page itself comes from <see cref="SettingsSearchCatalog.PageOf"/>.</summary>
internal sealed record SettingsSearchDestination(string? Section, string Focus);

internal static class SettingsSearchDestinations
{
    public static SettingsSearchDestination? For(SettingsSearchTarget target) => target switch
    {
        SettingsSearchTarget.SessionsLayout => new("ClientLayoutPreferences", "SessionsTileColumns"),
        // The size fields are disabled while resizing is unavailable; the window then focuses the section instead.
        SettingsSearchTarget.ContentSize => new("ClientWindowSizeTools", "ClientContentWidthBox"),
        // Every button here changes running clients at once, so search lands on the section, never on a button.
        SettingsSearchTarget.ClientPriority => new("ClientPriorityTools", "ClientPriorityTools"),
        // Choosing the Previews view stays the user's step, so search focuses that button instead of switching the view.
        SettingsSearchTarget.Previews => new(null, "SessionsPreviewModeButton"),
        // Mute and Unmute change running clients at once, so search lands on the saved option, which only edits the draft
        // until Save all settings; focusing it never ticks it.
        SettingsSearchTarget.ClientSound => new("ClientSoundTools", "MuteArrivingClientsOption"),
        // Apply and Remove each start an administrator run, so search lands on the rate box, which changes nothing by itself.
        SettingsSearchTarget.UploadLimit => new("UploadLimitTools", "UploadLimitRateBox"),
        SettingsSearchTarget.DiagnosticsFilters => new(null, "DiagnosticsFilterBox"),
        _ => null
    };
}
