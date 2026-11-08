using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Builds are deterministic: a Core- or Windows-only fix leaves Zenith.Sessions.App.dll byte-identical. The packaged
// self-check report must therefore identify every first-party assembly it exercises, not the App dll alone.
public sealed class DistributionIdentityTests : IDisposable
{
    private const string App="Zenith.Sessions.App.dll",Core="Zenith.Sessions.Core.dll",MacroHostWindows="Zenith.MacroHost.Windows.dll";
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-identity-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}

    private Dictionary<string,byte[]> WriteSyntheticAssemblies()
    {
        Directory.CreateDirectory(_root);var payloads=new Dictionary<string,byte[]>();
        foreach(string name in DistributionSelfCheck.FirstPartyAssemblies)
        {var bytes=Encoding.UTF8.GetBytes("synthetic payload for "+name);File.WriteAllBytes(Path.Combine(_root,name),bytes);payloads.Add(name,bytes);}
        return payloads;
    }
    private static string Sha256(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}

    [Fact]
    public async Task FirstPartyIdentityChangesWhenOnlyCoreChanges()
    {
        var payloads=WriteSyntheticAssemblies();
        var before=await DistributionSelfCheck.HashFirstPartyAssembliesAsync(_root,CancellationToken.None);
        Assert.Equal(DistributionSelfCheck.FirstPartyAssemblies.Order(StringComparer.Ordinal),before.Keys.Order(StringComparer.Ordinal));
        foreach(var (name,bytes) in payloads)Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),before[name]);
        // The combined value is SHA-256 over "name:hash\n" lines in the fixed order; the manager script recomputes it.
        string lines=string.Concat(DistributionSelfCheck.FirstPartyAssemblies.Select(name=>name+":"+before[name]+"\n"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines))),DistributionSelfCheck.CombinedIdentity(before));

        File.WriteAllBytes(Path.Combine(_root,Core),Encoding.UTF8.GetBytes("synthetic payload after a Core-only fix"));
        var after=await DistributionSelfCheck.HashFirstPartyAssembliesAsync(_root,CancellationToken.None);
        Assert.Equal(before[App],after[App]);
        Assert.NotEqual(before[Core],after[Core]);
        Assert.NotEqual(DistributionSelfCheck.CombinedIdentity(before),DistributionSelfCheck.CombinedIdentity(after));
    }

    [Fact]
    public async Task MissingFirstPartyAssemblyIsRejected()
    {
        WriteSyntheticAssemblies();File.Delete(Path.Combine(_root,MacroHostWindows));
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>DistributionSelfCheck.HashFirstPartyAssembliesAsync(_root,CancellationToken.None));
        Assert.Contains(MacroHostWindows,error.Message);Assert.DoesNotContain(_root,error.Message);
        var partial=new Dictionary<string,string>{[App]="00",[Core]="11"};
        Assert.Throws<InvalidDataException>(()=>DistributionSelfCheck.CombinedIdentity(partial));
    }

    [Fact]
    public async Task ReportIdentityHashesTheLoadedFirstPartyAssemblies()
    {
        // The test host loads the App, Core and Windows assemblies from its own output folder, like the packaged exe does.
        _=typeof(AccountProfile);_=typeof(ProtectedCredentialVault);
        var identity=await DistributionSelfCheck.IdentifyBuildAsync(AppContext.BaseDirectory,AppDomain.CurrentDomain.GetAssemblies(),CancellationToken.None);
        Assert.Equal(Sha256(typeof(DistributionSelfCheck).Assembly.Location),identity.AssemblySha256);
        Assert.Equal(Sha256(typeof(AccountProfile).Assembly.Location),identity.FirstPartySha256[Core]);
        Assert.Equal(Sha256(typeof(ProtectedCredentialVault).Assembly.Location),identity.FirstPartySha256["Zenith.Sessions.Windows.dll"]);
        Assert.Equal(DistributionSelfCheck.FirstPartyAssemblies.Length,identity.FirstPartySha256.Count);
        Assert.Equal(DistributionSelfCheck.CombinedIdentity(identity.FirstPartySha256),identity.FirstPartyIdentitySha256);
    }

    [Fact]
    public async Task ReportIdentityRejectsAFolderOtherThanTheOneTheAssembliesLoadedFrom()
    {
        WriteSyntheticAssemblies();
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>DistributionSelfCheck.IdentifyBuildAsync(_root,AppDomain.CurrentDomain.GetAssemblies(),CancellationToken.None));
        Assert.DoesNotContain(_root,error.Message);
    }

    // Ownership is established before the report is written, so a build that cannot be identified must still leave the stage
    // results and a static failure type on disk, and must fail the run.
    [Fact]
    public async Task ReportIsStillWrittenAndTheRunFailsWhenAFirstPartyAssemblyIsMissing()
    {
        WriteSyntheticAssemblies();File.Delete(Path.Combine(_root,MacroHostWindows));
        string report=Path.Combine(_root,"distribution-check.json");
        var cases=new DistributionSelfCheck.Case[]{new("Isolated storage ownership",true),new("Packaged brand image",true)};

        int exit=await DistributionSelfCheck.WriteReportAsync(report,cases,new(SQLiteVersion:"3.46.1",CleanupPhase:"Final fixture"),_root,[]);

        Assert.Equal(1,exit);
        string text=File.ReadAllText(report);
        Assert.DoesNotContain(Path.GetFileName(_root),text);Assert.DoesNotContain(MacroHostWindows,text);
        Assert.False(File.Exists(report+".tmp"));
        using var json=JsonDocument.Parse(text);var root=json.RootElement;
        Assert.Equal("InvalidDataException",root.GetProperty("IdentityError").GetString());
        Assert.Equal(JsonValueKind.Null,root.GetProperty("AssemblySha256").ValueKind);
        Assert.Equal(JsonValueKind.Null,root.GetProperty("FirstPartySha256").ValueKind);
        Assert.Equal(JsonValueKind.Null,root.GetProperty("FirstPartyIdentitySha256").ValueKind);
        Assert.Equal(cases.Select(item=>item.Name),root.GetProperty("Cases").EnumerateArray().Select(item=>item.GetProperty("Name").GetString()));
        Assert.Equal("3.46.1",root.GetProperty("SQLiteVersion").GetString());
        Assert.Equal("Final fixture",root.GetProperty("CleanupDiagnostic").GetProperty("Phase").GetString());
    }

    [Fact]
    public async Task ReportCarriesTheBuildIdentityAndNoIdentityErrorWhenTheSetIsComplete()
    {
        WriteSyntheticAssemblies();
        string report=Path.Combine(_root,"distribution-check.json");

        int exit=await DistributionSelfCheck.WriteReportAsync(report,[new("Isolated storage ownership",true)],new(),_root,[]);

        Assert.Equal(0,exit);
        using var json=JsonDocument.Parse(File.ReadAllText(report));var root=json.RootElement;
        var hashes=await DistributionSelfCheck.HashFirstPartyAssembliesAsync(_root,CancellationToken.None);
        Assert.Equal(JsonValueKind.Null,root.GetProperty("IdentityError").ValueKind);
        Assert.Equal(hashes[App],root.GetProperty("AssemblySha256").GetString());
        Assert.Equal(DistributionSelfCheck.CombinedIdentity(hashes),root.GetProperty("FirstPartyIdentitySha256").GetString());
    }

    // N-B10-1: a person may read or copy only the first console line, so a run that exits 1 because the build cannot be
    // identified must not print the success line, even when every planned stage passed.
    [Fact]
    public async Task FirstConsoleLineIsTheSuccessLineOnlyWhenTheRunPasses()
    {
        WriteSyntheticAssemblies();
        string report=Path.Combine(_root,"distribution-check.json");
        var cases=Enumerable.Range(1,DistributionSelfCheck.PlannedStageCount).Select(i=>new DistributionSelfCheck.Case($"Stage {i}",true)).ToArray();
        string success=DistributionSelfCheck.Summary(DistributionSelfCheck.PlannedStageCount,DistributionSelfCheck.PlannedStageCount,null);

        var passing=new StringWriter();
        Assert.Equal(0,await DistributionSelfCheck.WriteReportAsync(report,cases,new(),_root,[],passing));
        Assert.Equal(success,FirstLine(passing));

        File.Delete(Path.Combine(_root,MacroHostWindows));
        var unidentified=new StringWriter();
        Assert.Equal(1,await DistributionSelfCheck.WriteReportAsync(report,cases,new(),_root,[],unidentified));
        Assert.Equal("Distribution self-check: 43/43 (build not identified: InvalidDataException); release readiness remains false.",FirstLine(unidentified));
        Assert.NotEqual(success,FirstLine(unidentified));
        // Rule 10: the console names the failure type only, never the folder or the missing file.
        Assert.DoesNotContain(_root,unidentified.ToString());Assert.DoesNotContain(MacroHostWindows,unidentified.ToString());

        static string FirstLine(StringWriter output)=>output.ToString().Split(Environment.NewLine)[0];
    }

    [Fact]
    public void ManagerScriptComparesEveryFirstPartyHashWithTheStagedFiles()
    {
        using var stream=typeof(DistributionIdentityTests).Assembly.GetManifestResourceStream("Check-ZenithSessionsManager.ps1")!;
        string script=new StreamReader(stream).ReadToEnd();
        static string[] Names(string script,string variable)
        {
            var list=Regex.Match(script,@"\$"+variable+@"=@\(([^)]*)\)");
            Assert.True(list.Success,$"The manager script has no ${variable} list.");
            return Regex.Matches(list.Groups[1].Value,"'([^']+)'").Select(match=>match.Groups[1].Value).ToArray();
        }
        Assert.Equal(DistributionSelfCheck.FirstPartyAssemblies,Names(script,"firstParty"));
        Assert.Subset(Names(script,"required").ToHashSet(StringComparer.Ordinal),DistributionSelfCheck.FirstPartyAssemblies.ToHashSet(StringComparer.Ordinal));
        Assert.Contains("$native.FirstPartySha256",script);Assert.Contains("$native.FirstPartyIdentitySha256",script);
    }
}
