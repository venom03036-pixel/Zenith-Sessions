using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class DistributionInstallScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-install-tests-" + Guid.NewGuid().ToString("N"));
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private string Common => ". " + Quote(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot, "scripts", "ZenithSessionsDistribution.Common.ps1")) + "; ";
    private string Install => Path.Combine(_root, "programs", "Zenith Sessions");
    private string Pending => Install + ".pending";
    private string Previous => Install + ".previous";
    private string Promote(string source, string? trustedInventoryHash = null) => "Promote-ZenithRelease " + string.Join(" ", new[] { source, Install, Pending, Previous, trustedInventoryHash ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,"release-manifest.json")))) }.Select(Quote));
    private async Task<string> Package(string folder, string version = "2.0.0.0", bool ready = true)
    {
        string path = Path.Combine(_root, folder); Directory.CreateDirectory(path);
        foreach (string file in new[] { "Zenith.Sessions.App.exe", "Zenith.Sessions.App.dll", "Zenith.Sessions.App.runtimeconfig.json", "Zenith.Sessions.App.deps.json", "Zenith.Sessions.Core.dll", "Zenith.Sessions.Windows.dll", "Uninstall-ZenithSessions.ps1", "ZenithSessionsDistribution.Common.ps1" }) await File.WriteAllTextAsync(Path.Combine(path, file), "synthetic " + version);
        var files = Directory.GetFiles(path).Select(file => new { Path = Path.GetFileName(file), Length = new FileInfo(file).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(path, "release-manifest.json"), JsonSerializer.Serialize(new { FormatVersion = 1, Product = "Zenith.Sessions.Personal", Version = version, ReleaseReady = ready, Files = files })); return path;
    }
    private async Task<(int ExitCode, string Output, string Errors)> Run(string command)
        => await PowerShellScriptCompatibilityTests.RunWindowsPowerShell(_root, new Dictionary<string, string>(), "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Common + command)));
    [Fact]
    public async Task PreviewAndUnlistedPayloadCannotBeInstalled()
    {
        string preview = await Package("preview", ready: false);
        var result = await Run("try { " + Promote(preview) + "; exit 99 } catch { $_.Exception.Message; exit 0 }");
        Assert.Equal(0, result.ExitCode); Assert.Contains("not approved", result.Output); Assert.False(Directory.Exists(Install));
        string release = await Package("release"); await File.WriteAllTextAsync(Path.Combine(release, "unlisted.dll"), "unexpected");
        result = await Run("try { " + Promote(release) + "; exit 99 } catch { $_.Exception.Message; exit 0 }");
        Assert.Equal(0, result.ExitCode); Assert.Contains("unlisted", result.Output); Assert.False(Directory.Exists(Install));
    }
    [Fact]
    public async Task InstallAndUpdateKeepPreviousVersionAndSeparateData()
    {
        string old = await Package("old", "1.0.0.0"), next = await Package("new");
        string data = Path.Combine(_root, "data", "accounts.db"); Directory.CreateDirectory(Path.GetDirectoryName(data)!); await File.WriteAllTextAsync(data, "preserved synthetic data");
        var first = await Run(Promote(old)); Assert.True(first.ExitCode == 0, first.Errors);
        var second = await Run(Promote(next)); Assert.True(second.ExitCode == 0, second.Errors);
        Assert.Equal("synthetic 2.0.0.0", await File.ReadAllTextAsync(Path.Combine(Install, "Zenith.Sessions.App.exe")));
        Assert.Equal("synthetic 1.0.0.0", await File.ReadAllTextAsync(Path.Combine(Previous, "Zenith.Sessions.App.exe")));
        Assert.Equal("preserved synthetic data", await File.ReadAllTextAsync(data)); Assert.False(Directory.Exists(Pending));
    }
    [Fact]
    public async Task FinalVerificationFailureRollsBackAndPreservesRejectedFiles()
    {
        string old = await Package("old", "1.0.0.0"), next = await Package("new");
        Assert.Equal(0, (await Run(Promote(old))).ExitCode);
        // Fail only the verification after promotion, not the source/copy checks.
        string command = "$original=${function:Assert-ZenithReleaseFiles}; function Assert-ZenithReleaseFiles([string]$Package){ if($Package -eq " + Quote(Install) + "){throw 'synthetic post-promotion failure'}; & $original $Package }; try { " + Promote(next) + "; exit 99 } catch { $_.Exception.Message; exit 0 }";
        var result = await Run(command); Assert.Equal(0, result.ExitCode); Assert.Contains("post-promotion", result.Output);
        Assert.Equal("synthetic 1.0.0.0", await File.ReadAllTextAsync(Path.Combine(Install, "Zenith.Sessions.App.exe")));
        Assert.Equal("synthetic 2.0.0.0", await File.ReadAllTextAsync(Path.Combine(Pending, "Zenith.Sessions.App.exe")));
    }
    [Fact]
    public async Task UnownedExistingFoldersAndInterruptedStagesArePreserved()
    {
        string next = await Package("new"); Directory.CreateDirectory(Install); string foreign = Path.Combine(Install, "user.txt"); await File.WriteAllTextAsync(foreign, "keep");
        var result = await Run("try { " + Promote(next) + "; exit 99 } catch { $_.Exception.Message; exit 0 }");
        Assert.Equal(0, result.ExitCode); Assert.Contains("ownership marker", result.Output); Assert.Equal("keep", await File.ReadAllTextAsync(foreign));
        Directory.CreateDirectory(Pending); await File.WriteAllTextAsync(Path.Combine(Pending, "partial"), "recover");
        result = await Run("try { " + Promote(next) + "; exit 99 } catch { $_.Exception.Message; exit 0 }");
        Assert.Equal(0, result.ExitCode); Assert.Contains("pending installation", result.Output); Assert.True(File.Exists(Path.Combine(Pending, "partial")));
    }
    [Fact]
    public async Task ChangingAStagedDllAndItsLocalInventoryCannotReplaceTheSignedInventoryProvenance()
    {
        string stage=await Package("stage");string manifestPath=Path.Combine(stage,"release-manifest.json");
        string signedInventoryHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath)));
        string dll=Path.Combine(stage,"Zenith.Sessions.Core.dll");await File.WriteAllTextAsync(dll,"tampered DLL");
        var node=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        foreach(var entry in node["Files"]!.AsArray())if(entry!["Path"]!.GetValue<string>()=="Zenith.Sessions.Core.dll")
        {entry["Length"]=new FileInfo(dll).Length;entry["Sha256"]=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dll)));}
        await File.WriteAllTextAsync(manifestPath,node.ToJsonString());
        var result=await Run(Promote(stage,signedInventoryHash));Assert.NotEqual(0,result.ExitCode);Assert.False(Directory.Exists(Install));
    }
    [Fact]
    public async Task ProductionEntryScriptsParseUnderStockWindowsPowerShell()
    {
        Directory.CreateDirectory(_root);
        string command = "$bad=0; foreach($name in @('Install-ZenithSessions.ps1','Uninstall-ZenithSessions.ps1','New-ZenithSessionsReleaseKey.ps1','Sign-ZenithSessionsRelease.ps1')) { $tokens=$null;$errors=$null;[void][Management.Automation.Language.Parser]::ParseFile((Join-Path " + Quote(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot, "scripts")) + " $name),[ref]$tokens,[ref]$errors);if($errors){$errors; $bad++} }; exit $bad";
        var result = await Run(command); Assert.True(result.ExitCode == 0, result.Output + result.Errors);
    }
    [Theory]
    [InlineData("140.0.1.2", true)][InlineData("0.0.0.0", false)][InlineData("", false)]
    public async Task WebViewDetectionUsesMicrosoftDocumentedRuntimeRegistryId(string version, bool expected)
    {
        Directory.CreateDirectory(_root);
        string command = "function Get-ItemProperty { param($LiteralPath,$ErrorAction) if($LiteralPath -eq 'HKCU:\\Software\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'){[pscustomobject]@{pv=" + Quote(version) + "}} }; if((Test-ZenithWebView2) -eq $" + expected.ToString().ToLowerInvariant() + "){exit 0}else{exit 19}";
        var result = await Run(command); Assert.True(result.ExitCode == 0, result.Output + result.Errors);
    }
    [Fact]
    public async Task RegistrationFailureRestoresThePriorInstallAndRunsRegistrationRecovery()
    {
        string old=await Package("old","1.0.0.0"),next=await Package("new");
        Assert.Equal(0,(await Run(Promote(old))).ExitCode);Assert.Equal(0,(await Run(Promote(next))).ExitCode);
        string restored=Path.Combine(_root,"registration-restored.txt");
        string command="$paths=[pscustomobject]@{Install="+Quote(Install)+";Pending="+Quote(Pending)+";Previous="+Quote(Previous)+"}; try { Complete-ZenithRegistration $paths {throw 'synthetic registry denial'} {[IO.File]::WriteAllText("+Quote(restored)+",'restored')}; exit 99 } catch { $_.Exception.Message; exit 0 }";
        var result=await Run(command);Assert.Equal(0,result.ExitCode);Assert.Contains("registry denial",result.Output);
        Assert.Equal("restored",await File.ReadAllTextAsync(restored));Assert.Equal("synthetic 1.0.0.0",await File.ReadAllTextAsync(Path.Combine(Install,"Zenith.Sessions.App.exe")));
        Assert.True(File.Exists(Path.Combine(Pending,"Zenith.Sessions.App.exe")));
    }
    [Fact]
    public async Task PackageGenerationKeepsPreviewGateAndBuildsAnExactFileInventory()
    {
        string source=await Package("preview-source");string oldManifest=Path.Combine(source,"release-manifest.json");
        using(var document=JsonDocument.Parse(await File.ReadAllTextAsync(oldManifest)))
            await File.WriteAllTextAsync(Path.Combine(source,"preview-manifest.json"),JsonSerializer.Serialize(new{FormatVersion=1,ReleaseReady=false,DevelopmentPreview=true,Files=document.RootElement.GetProperty("Files").Clone()}));
        File.Delete(oldManifest);string output=Path.Combine(_root,"generated");
        string script=Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"scripts","New-ZenithSessionsReleasePackage.ps1");
        var result=await Run("& "+Quote(script)+" -Source "+Quote(source)+" -Version '2.0.0.0' -OutputDirectory "+Quote(output));Assert.True(result.ExitCode==0,result.Output+result.Errors);
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output,"content","release-manifest.json")));
        Assert.False(manifest.RootElement.GetProperty("ReleaseReady").GetBoolean());
        foreach(var item in manifest.RootElement.GetProperty("Files").EnumerateArray())
        {string path=Path.Combine(output,"content",item.GetProperty("Path").GetString()!);Assert.Equal(item.GetProperty("Sha256").GetString(),Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));}
        Assert.True(File.Exists(Path.Combine(output,"package.zip")));
        using var archive=System.IO.Compression.ZipFile.OpenRead(Path.Combine(output,"package.zip"));Assert.All(archive.Entries,entry=>Assert.False(entry.FullName.EndsWith('/')));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
