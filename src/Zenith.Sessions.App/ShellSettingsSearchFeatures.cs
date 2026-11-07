using System.Collections.ObjectModel;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private string _settingsSearchQuery="";
    private string _settingsSearchStatus="Find themes, RAM, launch timing, sign-in or recovery. Ctrl+F focuses this search while Settings is open.";
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
            SettingsSearchStatus=value.Length==0?"Find themes, RAM, launch timing, sign-in or recovery. Search never changes your preferences."
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
            SelectedSettingSearchResult=entry;SettingsSearchNavigationRequested?.Invoke(entry);
        });
        ClearSettingsSearch=new(_=>{SettingsSearchQuery="";SettingsSearchResults.Clear();SelectedSettingSearchResult=null;});
    }
    public void ReportSettingSearchNavigation(SettingsSearchEntry entry,bool fallback,bool focused=true)
    {
        if(!SettingsSearchResults.Contains(entry)||IsLocked||_shuttingDown)return;
        SettingsSearchStatus=fallback?"Custom timing is shown only for the Custom profile. Choose it yourself if wanted; search did not change the current profile or draft."
            :focused?$"Opened {entry.Title}. Search did not change or save your preferences."
            :$"Shown {entry.Title}. Keyboard focus was unavailable; select its control. Search did not change or save your preferences.";
    }
    private void DisposeSettingsSearch()
    {SettingsSearchQuery="";SettingsSearchResults.Clear();SelectedSettingSearchResult=null;SettingsSearchNavigationRequested=null;}
}
