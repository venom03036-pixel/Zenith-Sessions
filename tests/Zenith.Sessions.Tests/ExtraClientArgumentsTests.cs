using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Extra client arguments (R-launch-16): a saved default and a per-account set, validated in Core, kept in the account
/// library and settings.json, and appended after Roblox's own request only by Zenith's direct player start. No client,
/// browser or account is used; every start is a fake.
/// </summary>
public sealed class ExtraClientArgumentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-extra-arguments-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings.json");
    private string DatabasePath => Path.Combine(_root, "accounts.db");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ArgumentsSplitOnSpacesAndTheSavedFormIsSingleSpaced()
    {
        Assert.True(ExtraClientArgumentPolicy.TryParse("  --synthetic-flag   --value=1 -x:a/b.c,d@e#f~g[h]{i}(j)+k ", out var arguments, out string problem));
        Assert.Equal("", problem);
        Assert.Equal(new[] { "--synthetic-flag", "--value=1", "-x:a/b.c,d@e#f~g[h]{i}(j)+k" }, arguments);
        Assert.Equal("--synthetic-flag --value=1 -x:a/b.c,d@e#f~g[h]{i}(j)+k", ExtraClientArgumentPolicy.Validate("  --synthetic-flag   --value=1 -x:a/b.c,d@e#f~g[h]{i}(j)+k "));
        foreach (string? none in new[] { null, "", "   " })
        {
            Assert.True(ExtraClientArgumentPolicy.TryParse(none, out var empty, out _)); Assert.Empty(empty);
            Assert.Equal("", ExtraClientArgumentPolicy.Validate(none));
        }
        Assert.True(ExtraClientArgumentPolicy.TryParse(string.Join(' ', Enumerable.Repeat("-a", ExtraClientArgumentPolicy.MaximumArguments)), out var most, out _));
        Assert.Equal(ExtraClientArgumentPolicy.MaximumArguments, most.Count);
        Assert.True(ExtraClientArgumentPolicy.TryParse(new string('a', ExtraClientArgumentPolicy.MaximumCharacters), out _, out _));
    }

    [Theory]
    [InlineData("--a \"quoted\"", "Quotes are not allowed")]
    [InlineData("--a 'quoted'", "Quotes are not allowed")]
    [InlineData("--a %TEMP%", "Percent signs are not allowed")]
    [InlineData("--a > out", "Redirection and pipe characters")]
    [InlineData("--a < in", "Redirection and pipe characters")]
    [InlineData("--a | more", "Redirection and pipe characters")]
    [InlineData("--a && b", "Redirection and pipe characters")]
    [InlineData("--a C:\\x", "Backslashes and the escape characters")]
    [InlineData("--a ^b", "Backslashes and the escape characters")]
    [InlineData("--a `b", "Backslashes and the escape characters")]
    [InlineData("--a\n--b", "plain text on one line")]
    [InlineData("--a\t--b", "plain text on one line")]
    [InlineData("--a \u00e9", "plain text on one line")]
    [InlineData("--a $b", "may use only")]
    [InlineData("--a roblox-player:1+launchmode:play", "cannot contain a launch request")]
    [InlineData("--a ROBLOX://x", "cannot contain a launch request")]
    [InlineData("--a .ROBLOSECURITY=x", "Do not enter sign-in cookies or tokens")]
    [InlineData("--a do-not-share-this", "Do not enter sign-in cookies or tokens")]
    public void EveryBrokenRuleIsRefusedWithPlainWording(string text, string expected)
    {
        Assert.False(ExtraClientArgumentPolicy.TryParse(text, out var arguments, out string problem));
        Assert.Empty(arguments); Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.DoesNotContain(text, problem, StringComparison.Ordinal);
        Assert.Equal(problem, Assert.Throws<ArgumentException>(() => ExtraClientArgumentPolicy.Validate(text)).Message);
    }

    [Fact]
    public void LengthAndCountBoundsAreExact()
    {
        Assert.False(ExtraClientArgumentPolicy.TryParse(new string('a', ExtraClientArgumentPolicy.MaximumCharacters + 1), out _, out string problem));
        Assert.Equal("Extra client arguments must be at most 512 characters.", problem);
        Assert.False(ExtraClientArgumentPolicy.TryParse(string.Join(' ', Enumerable.Repeat("-a", ExtraClientArgumentPolicy.MaximumArguments + 1)), out _, out problem));
        Assert.Equal("Extra client arguments must be at most 16 arguments separated by spaces.", problem);
        Assert.True(ExtraClientArgumentPolicy.IsValidArgument("--flag=1"));
        Assert.False(ExtraClientArgumentPolicy.IsValidArgument("")); Assert.False(ExtraClientArgumentPolicy.IsValidArgument(null));
        Assert.False(ExtraClientArgumentPolicy.IsValidArgument("two words")); Assert.False(ExtraClientArgumentPolicy.IsValidArgument("a%b"));
        Assert.False(ExtraClientArgumentPolicy.IsValidArgument("roblox-player:1+x")); Assert.False(ExtraClientArgumentPolicy.IsValidArgument("x.ROBLOSECURITY"));
    }

    [Fact]
    public void AnAccountsOwnSetReplacesTheDefaultAndABlankOneFallsBackToIt()
    {
        Assert.Equal(new[] { "--own" }, ExtraClientArgumentPolicy.Effective("--own", "--default --value=1"));
        Assert.Equal(new[] { "--default", "--value=1" }, ExtraClientArgumentPolicy.Effective("  ", "--default --value=1"));
        Assert.Equal(new[] { "--default" }, ExtraClientArgumentPolicy.Effective(null, "--default"));
        Assert.Empty(ExtraClientArgumentPolicy.Effective("", ""));
        Assert.Empty(ExtraClientArgumentPolicy.Effective(null, null));
        // A tampered saved value refuses the start instead of being dropped or passed on.
        Assert.Throws<ArgumentException>(() => ExtraClientArgumentPolicy.Effective("--a%b", ""));
        Assert.Throws<ArgumentException>(() => ExtraClientArgumentPolicy.Effective("", "--a\"b"));
        Assert.Equal("Extra client arguments (this account): --own", ExtraClientArgumentPolicy.Describe("--own", "--default"));
        Assert.Equal("Extra client arguments (default): --default --value=1", ExtraClientArgumentPolicy.Describe("", " --default  --value=1"));
        Assert.Equal("Extra client arguments: none", ExtraClientArgumentPolicy.Describe("", ""));
        Assert.Equal("Extra client arguments: none", ExtraClientArgumentPolicy.Describe(null, null));
    }

    [Fact]
    public void AProfileKeepsTheSavedFormAndRefusesABrokenRule()
    {
        var profile = AccountProfile.Create("Local alias");
        Assert.Equal("", profile.ExtraClientArguments); Assert.Equal("", profile.Validated().ExtraClientArguments);
        Assert.Equal("--own --value=1", (profile with { ExtraClientArguments = "  --own   --value=1 " }).Validated().ExtraClientArguments);
        var error = Assert.Throws<ArgumentException>(() => (profile with { ExtraClientArguments = "--own %x" }).Validated());
        Assert.Equal("Percent signs are not allowed in extra client arguments.", error.Message);
        Assert.Throws<ArgumentException>(() => (profile with { ExtraClientArguments = null! }).Validated());
    }

    [Fact]
    public void ArgumentsStayOutOfMetadataExportsAndAnExportCarryingThemIsRefused()
    {
        var profile = (AccountProfile.Create("Local alias") with { ExtraClientArguments = "--own" }).Validated();
        string json = JsonSerializer.Serialize(profile);
        Assert.DoesNotContain("ExtraClientArguments", json, StringComparison.Ordinal); Assert.DoesNotContain("--own", json, StringComparison.Ordinal);
        // An export without the field (every export) still decodes to a profile with no arguments of its own.
        string document = "{\"Version\":4,\"Groups\":[],\"Profiles\":[" + json + "]}";
        Assert.Equal("", Assert.Single(ProfileMetadataImport.Decode(System.Text.Encoding.UTF8.GetBytes(document))).ExtraClientArguments);
        string smuggled = "{\"Version\":4,\"Groups\":[],\"Profiles\":[" + json.Insert(1, "\"ExtraClientArguments\":\"--own\",") + "]}";
        Assert.Throws<InvalidDataException>(() => ProfileMetadataImport.Decode(System.Text.Encoding.UTF8.GetBytes(smuggled)));
    }

    [Fact]
    public async Task TheAccountLibraryIsAtSchemaSixWithAnArgumentsColumn()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        await using var db = await Database();
        using var query = db.CreateCommand(); query.CommandText = "PRAGMA user_version";
        Assert.Equal(6L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT count(*) FROM pragma_table_info('profiles') WHERE name='extra_client_arguments'";
        Assert.Equal(1L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ASchemaFiveLibraryIsBackedUpAndMigratedAndItsProfilesHaveNoArgumentsYet()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var profile = await repository.SaveAsync(AccountProfile.Create("Preserved"), default);
        await using (var old = await Database())
        {
            using var command = old.CreateCommand();
            command.CommandText = "ALTER TABLE profiles DROP COLUMN extra_client_arguments; PRAGMA user_version=5;"; await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.FindAsync(profile.Id, default));
        await repository.InitializeAsync(default);
        Assert.Equal(profile, await repository.FindAsync(profile.Id, default)); Assert.Equal("", profile.ExtraClientArguments);
        await using (var backup = await Database(DatabasePath + ".schema5.bak", true))
        {
            // The schema 5 catalog orders names with the repository's collation; the integrity check needs it registered here too.
            backup.CreateCollation("ZENITH_GROUP", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using var verify = backup.CreateCommand(); verify.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", await verify.ExecuteScalarAsync());
            verify.CommandText = "PRAGMA user_version"; Assert.Equal(5L, await verify.ExecuteScalarAsync());
            verify.CommandText = "SELECT count(*) FROM pragma_table_info('profiles') WHERE name='extra_client_arguments'"; Assert.Equal(0L, await verify.ExecuteScalarAsync());
            verify.CommandText = "SELECT label FROM profiles"; Assert.Equal("Preserved", await verify.ExecuteScalarAsync());
        }
        await using (var current = await Database())
        {
            using var verify = current.CreateCommand(); verify.CommandText = "PRAGMA user_version"; Assert.Equal(6L, await verify.ExecuteScalarAsync());
        }
        var saved = await repository.SaveAsync(profile with { ExtraClientArguments = " --own  --value=1 " }, default);
        Assert.Equal("--own --value=1", saved.ExtraClientArguments);
        Assert.Equal(saved, await repository.FindAsync(profile.Id, default));
        Assert.Equal("--own --value=1", Assert.Single((await repository.ReadAsync(new(), default)).Items).ExtraClientArguments);
    }

    [Fact]
    public async Task ArgumentsRoundTripThroughEverySaveAndAFutureSchemaIsRefused()
    {
        using var repository = new AccountRepository(_root); await repository.InitializeAsync(default);
        var created = (AccountProfile.Create("Imported") with { ExtraClientArguments = "--imported" }).Validated();
        await repository.CreateManyAsync([created], default);
        Assert.Equal("--imported", (await repository.FindAsync(created.Id, default))!.ExtraClientArguments);
        var saved = await repository.SaveAsync((AccountProfile.Create("Saved") with { ExtraClientArguments = "--saved" }).Validated(), default);
        Assert.Equal("--saved", (await repository.FindAsync(saved.Id, default))!.ExtraClientArguments);
        var cleared = await repository.SaveAsync(saved with { ExtraClientArguments = "" }, default);
        Assert.Equal("", (await repository.FindAsync(saved.Id, default))!.ExtraClientArguments); Assert.Equal(saved.Revision + 1, cleared.Revision);
        // A group edit keeps the arguments it does not touch.
        Assert.Equal(1, await repository.ApplyGroupAsync([(await repository.FindAsync(created.Id, default))!], "Raid", default));
        Assert.Equal("--imported", (await repository.FindAsync(created.Id, default))!.ExtraClientArguments);
        await using (var db = await Database()) { using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=7"; await command.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.FindAsync(created.Id, default));
        using var rejected = new AccountRepository(_root); await Assert.ThrowsAsync<InvalidDataException>(() => rejected.InitializeAsync(default));
    }

    [Fact]
    public async Task ABrokenSavedDefaultMakesTheSettingsFileInvalidInsteadOfReachingAStart()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DefaultClientArguments\":\"--a %TEMP%\"}");
        using var store = new SettingsStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(default));
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(DefaultClientArguments: "--a \"b\"").Validated());
        Assert.Equal("--a --b", new SessionsSettings(DefaultClientArguments: "  --a   --b ").Validated().DefaultClientArguments);
        Assert.Equal("", new SessionsSettings().Validated().DefaultClientArguments);
    }

    [Fact]
    public Task SaveSettingsRefusesABrokenDefaultWithItsRuleAndKeepsAValidOne() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync(); Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.Equal("", vm.DefaultClientArguments);

        vm.DefaultClientArguments = "--synthetic-flag %TEMP%";
        Assert.Equal("Unsaved settings", vm.SaveStatus);
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Percent signs are not allowed in extra client arguments.", vm.ErrorNotice);
        Assert.Equal("Unsaved settings", vm.SaveStatus);
        Assert.DoesNotContain("synthetic-flag", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);

        vm.DefaultClientArguments = "  --synthetic-flag   --value=1 ";
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.Equal("--synthetic-flag --value=1", vm.DefaultClientArguments);
        Assert.Contains("\"DefaultClientArguments\":\"--synthetic-flag --value=1\"", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);
        // The saved form and a draft that only differs by spacing are the same preference.
        vm.DefaultClientArguments = "--synthetic-flag    --value=1"; Assert.Equal("Settings saved", vm.SaveStatus);
        vm.DefaultClientArguments = "--synthetic-flag"; Assert.Equal("Unsaved settings", vm.SaveStatus);
        // Nothing about the arguments reaches the event log.
        Assert.DoesNotContain(vm.Events, e => e.Contains("synthetic-flag", StringComparison.Ordinal));
    });

    [Fact]
    public async Task TheSavedDefaultSurvivesLoadAndSaveAndOlderFilesHaveNone()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DefaultClientArguments\":\"  --synthetic-flag   --value=1 \"}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.Contains("\"DefaultClientArguments\":\"--synthetic-flag --value=1\"", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);

        // A schema-1 file from before this setting loads without a notice and passes no extra arguments.
        await File.WriteAllTextAsync(SettingsPath, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.Contains("\"DefaultClientArguments\":\"\"", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("command line")]
    [InlineData("launch flags")]
    public void SettingsSearchFindsTheExtraArgumentsUnderRobloxInstallation(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "extra-client-arguments");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find the extra client arguments.");
        Assert.Equal(SettingsSearchTarget.Installations, entry!.Target);
    }

    [Fact]
    public void SettingsOffersTheDefaultArgumentsUnderRobloxInstallationAndTheGuideNamesThem()
    {
        var document = ProductionWindow();
        var box = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SettingsDefaultClientArguments");
        Assert.True(box is not null, "Settings has no field for the default extra client arguments.");
        Assert.Equal("TextBox", box!.Name.LocalName);
        Assert.Contains("{Binding DefaultClientArguments", (string?)box.Attribute("Text"), StringComparison.Ordinal);
        Assert.Equal("512", (string?)box.Attribute("MaxLength"));
        Assert.False(string.IsNullOrWhiteSpace((string?)box.Attribute("AutomationProperties.Name")));
        // The field sits in the Roblox installation section, before Saved sign-ins, where a search result for it lands.
        var all = document.Descendants().ToList();
        int installations = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsInstallationsAnchor");
        int signIn = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsSignInAnchor");
        Assert.InRange(all.IndexOf(box), installations + 1, signIn - 1);

        var guide = Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Review selected-account trial shows", StringComparison.Ordinal) == true));
        string text = (string)guide.Attribute("Text")!;
        Assert.Contains("Extra client arguments", text, StringComparison.Ordinal);
        Assert.Contains("never passed through a bootstrapper", text, StringComparison.Ordinal);
    }

    private async Task<SqliteConnection> Database(string? path = null, bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await db.OpenAsync(); return db;
    }
    private static XDocument ProductionWindow()
    {
        using var stream = typeof(ExtraClientArgumentsTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }
    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }
    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Task operation = Task.CompletedTask;
            // A tracked delegate owns the async work; no window, tray icon or desktop input is created.
            dispatcher.BeginInvoke(() => { operation = RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-extra-arguments-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected synthetic fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
