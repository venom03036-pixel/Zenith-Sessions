using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Localization = Zenith.Sessions.App.Localization;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Localization infrastructure (R-shell-16): the shell's texts come from one resx catalog through the <c>{local:Text Key}</c>
/// markup extension, the language is a saved choice (English, Follow Windows, or a pseudo-locale for testing), and a
/// right-to-left language mirrors the window. English is the only shipped language.
/// </summary>
public sealed class ShellLocalizationTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex KeyReference = new(@"^\{local:Text (?<key>[A-Za-z0-9_]+)\}$", RegexOptions.CultureInvariant);

    private static XDocument ProductionWindow()
    {
        using var stream = typeof(ShellLocalizationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }
    private static string? Key(string? attribute) => attribute is not null && KeyReference.Match(attribute) is { Success: true } match ? match.Groups["key"].Value : null;

    [Theory]
    [InlineData("language")]
    [InlineData("localization")]
    [InlineData("rtl")]
    public void SettingsSearchFindsTheLanguageChoice(string query)
    {
        var result=SettingsSearchCatalog.Search(query).First();
        Assert.Equal("language",result.Id);Assert.Equal(SettingsSearchTarget.Appearance,result.Target);
    }

    [Fact]
    public void TheSidebarTheWindowTitleTheAppearanceSectionAndTheGuideHeadingsComeFromTheCatalog()
    {
        var document = ProductionWindow();
        var window = document.Root!;
        // The window hands its current strings to every element it contains and mirrors itself for a right-to-left language.
        Assert.True((string?)window.Attribute(XName.Get("Localization.Strings", "clr-namespace:Zenith.Sessions.App")) == "{Binding Strings}",
            "The window does not pass the shell's current strings to its elements (local:Localization.Strings).");
        Assert.True((string?)window.Attribute("FlowDirection") == "{Binding Strings.FlowDirection}", "The window's FlowDirection does not follow the chosen language.");
        Assert.True((string?)window.Attribute("Title") == "{Binding WindowTitle}", "The window title is no longer the shell's page-led title.");

        var rail = document.Descendants(Ui + "Border").Single(e => (string?)e.Attribute(X + "Name") == "NavigationRail");
        var problems = new List<string>();
        foreach (var button in rail.Descendants(Ui + "Button"))
        {
            string? name = Key((string?)button.Attribute("AutomationProperties.Name"));
            if (name is null || !name.StartsWith("Page_", StringComparison.Ordinal)) problems.Add($"sidebar button '{(string?)button.Attribute("AutomationProperties.Name")}' is not named from a Page_ key");
            var label = button.Descendants(Ui + "TextBlock").FirstOrDefault(t => Key((string?)t.Attribute("Text")) is not null);
            if (label is null) problems.Add($"sidebar button '{(string?)button.Attribute("AutomationProperties.Name")}' shows a literal label");
            else if (Key((string?)label.Attribute("Text")) != name) problems.Add($"sidebar button '{name}' shows a different key than its name");
        }
        var appearance = document.Descendants(Ui + "TextBlock").Single(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        if (Key((string?)appearance.Attribute("Text")) is null) problems.Add("the Appearance section title is a literal");
        if (!document.Descendants(Ui + "ComboBox").Any(e => (string?)e.Attribute(X + "Name") == "SettingsLanguage")) problems.Add("Appearance has no language choice (SettingsLanguage)");
        var guide = document.Descendants(Ui + "ScrollViewer").Single(e => (string?)e.Attribute("Visibility") == "{Binding IsGuide, Converter={StaticResource VisibleIf}}");
        var headings = guide.Descendants(Ui + "TextBlock").Where(e => (string?)e.Attribute("Style") is "{StaticResource SectionTitle}" or "{StaticResource PageTitle}").ToArray();
        Assert.True(headings.Length > 20, "The Guide lost its section headings.");
        foreach (var heading in headings)
            if (Key((string?)heading.Attribute("Text")) is null) problems.Add($"Guide heading '{(string?)heading.Attribute("Text")}' is a literal");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void EveryCatalogHasTheSameKeysAndFormatPlaceholdersAndEveryMigratedReferenceExists()
    {
        string folder=Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"src","Zenith.Sessions.App","Localization");
        var files=Directory.GetFiles(folder,"ShellStrings*.resx");
        var english=ReadCatalog(Path.Combine(folder,"ShellStrings.resx"));
        Assert.NotEmpty(files);
        foreach(string file in files)
        {
            var translated=ReadCatalog(file);
            Assert.Equal(english.Keys.Order(),translated.Keys.Order());
            foreach(var entry in english)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[entry.Key]));
                _=CompositeFormat.Parse(translated[entry.Key]);
                Assert.Equal(Placeholders(entry.Value),Placeholders(translated[entry.Key]));
                Assert.Equal(Placeholders(entry.Value),Placeholders(PseudoLocalizer.Localize(entry.Value)));
            }
        }
        foreach(var attribute in ProductionWindow().Descendants().Attributes())
            if(Key(attribute.Value) is { } key) Assert.Contains(key,english.Keys);
        Assert.Equal(english.Keys.Order(),ShellStrings.Keys().Order());
    }

    [Fact]
    public void AppearanceContainsNoUnmigratedStaticLabelsDescriptionsOrAccessibleNames()
    {
        var elements=ProductionWindow().Descendants().ToList();
        int first=elements.FindIndex(e=>(string?)e.Attribute(X+"Name")=="SettingsAppearanceAnchor");
        int last=elements.FindIndex(e=>(string?)e.Attribute(X+"Name")=="SettingsShutdownAnchor");
        Assert.True(first>=0 && last>first);
        var remaining=elements.Skip(first).Take(last-first).SelectMany(e=>e.Attributes())
            .Where(a=>a.Name.LocalName is "Text" or "Content" or "Header" or "ToolTip" or "AutomationProperties.Name")
            .Where(a=>!a.Value.StartsWith('{') && !string.IsNullOrWhiteSpace(a.Value))
            .Select(a=>a.Name+"="+a.Value).ToArray();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task SchemaOneDefaultsToEnglishAndKeepsAChosenLanguageWithoutChangingOtherSettings()
    {
        string root=Path.Combine(Path.GetTempPath(),"zenith-language-store-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"settings.json"),"{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
            using var store=new SettingsStore(root);
            var old=await store.LoadAsync(default);Assert.Null(old.Notice);Assert.Equal("en",old.Settings.Language);
            await store.SaveAsync(old.Settings with {Language="pseudo-rtl"},default);
            var saved=await store.LoadAsync(default);Assert.Null(saved.Notice);Assert.Equal("pseudo-rtl",saved.Settings.Language);
            Assert.Equal("ocean-blue",saved.Settings.Theme);Assert.True(saved.Settings.DenseAccounts);
            await store.SaveAsync(saved.Settings with {Language="unknown-later-choice"},default);
            Assert.Equal("en",(await store.LoadAsync(default)).Settings.Language);
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public Task InheritedCatalogRebindsTextAndAccessibleNamesWithoutReplacingTheView()=>OnSta(async ()=>
    {
        const string xaml="<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='clr-namespace:Zenith.Sessions.App;assembly=Zenith.Sessions.App'><TextBlock Text='{local:Text Page_Accounts}' AutomationProperties.Name='{local:Text Page_Accounts}' /></Border>";
        var view=(Border)XamlReader.Parse(xaml);var label=(TextBlock)view.Child;
        view.Measure(new(300,100));view.Arrange(new(0,0,300,100));view.UpdateLayout();
        Assert.Equal("Accounts",label.Text);
        var pseudo=new ShellStrings(ShellLanguages.Resolve("pseudo-rtl",CultureInfo.GetCultureInfo("en-US")));
        Localization.SetStrings(view,pseudo);view.FlowDirection=pseudo.FlowDirection;
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(pseudo["Page_Accounts"],label.Text);
        Assert.Equal(label.Text,System.Windows.Automation.AutomationProperties.GetName(label));
        Assert.Equal(FlowDirection.RightToLeft,label.FlowDirection);
        Localization.SetStrings(view,ShellStrings.English);await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal("Accounts",label.Text);
    });

    [Fact]
    public Task LanguageAutosavePreservesDraftsAndSurvivesThemeSavesAndRestart()=>OnSta(async ()=>
    {
        string root=Path.Combine(Path.GetTempPath(),"zenith-language-shell-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using(var shell=new ShellViewModel(root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor()))
            {
                await shell.InitializeAsync();Assert.Equal("en",shell.Language);
                shell.CloseButtonExits=true;
                shell.ApplyLanguage.Execute("pseudo-rtl");await shell.ApplyLanguage.Execution;
                Assert.Equal(FlowDirection.RightToLeft,shell.Strings.FlowDirection);
                Assert.StartsWith("[",shell.PageName);Assert.StartsWith("[",shell.ThemeStatus);
                using(var store=new SettingsStore(root)) Assert.False((await store.LoadAsync(default)).Settings.CloseButtonExits);
                shell.ApplyTheme.Execute("ocean-blue");await shell.ApplyTheme.Execution;
                shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
                await shell.SaveManagerWindowAsync(new(10,10,1320,840));
                using(var store=new SettingsStore(root)) Assert.Equal("pseudo-rtl",(await store.LoadAsync(default)).Settings.Language);
            }
            await using(var reopened=new ShellViewModel(root,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor()))
            {
                await reopened.InitializeAsync();Assert.Equal("pseudo-rtl",reopened.Language);
                Assert.True(reopened.CloseButtonExits);Assert.Equal(FlowDirection.RightToLeft,reopened.Strings.FlowDirection);
                reopened.ApplyLanguage.Execute("follow-windows");await reopened.ApplyLanguage.Execution;
                reopened.SetWindowsDisplayLanguage(CultureInfo.GetCultureInfo("ar-SA"));
                Assert.Equal(FlowDirection.RightToLeft,reopened.Strings.FlowDirection);
                Assert.Equal("Overview",reopened.PageName); // English fallback, not a shipped Arabic translation.
                Assert.Contains("no translation yet",reopened.LanguageStatus);
            }
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    });

    private static Dictionary<string,string> ReadCatalog(string path)
        =>XDocument.Load(path).Root!.Elements("data").ToDictionary(e=>(string)e.Attribute("name")!,e=>(string)e.Element("value")!,StringComparer.Ordinal);
    private static string[] Placeholders(string format)=>Regex.Matches(format,@"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]+)?\}")
        .Select(m=>m.Groups["index"].Value).Order().ToArray();
    private static Task OnSta(Func<Task> work)=>PackagedLayoutCheck.RunOnStaAsync(()=>new object(),_=>work(),_=>{},CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested();return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[])); }
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
    }
}
