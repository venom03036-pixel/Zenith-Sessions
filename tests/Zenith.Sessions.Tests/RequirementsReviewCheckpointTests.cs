using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zenith.Sessions.Tests;

// Each review checkpoint keeps its own labelled record and cites the test result files the cycle actually wrote.
public sealed class RequirementsReviewCheckpointTests : IDisposable
{
    private const string Prefix="zenith-review-checkpoint-";
    private static readonly char EmDash=(char)0x2014;
    private readonly string _root=Path.Combine(Path.GetTempPath(),Prefix+Guid.NewGuid().ToString("N"));
    private string Repository=>Path.Combine(_root,"repo");
    private string ReviewDirectory=>Path.Combine(Repository,"artifacts","sessions-requirements-review");

    [Fact]
    public async Task AfterCheckpointRecordsItsLabelAndTheSuppliedTestArtifact()
    {
        string script=await CreateFixture();
        string stale=Path.Combine(Repository,"artifacts","sessions-manager-tests","manager-core.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);await File.WriteAllTextAsync(stale,"<TestRun old=\"true\"/>");
        File.SetLastWriteTimeUtc(stale,new DateTime(2026,11,7,6,13,29,DateTimeKind.Utc));
        string cycle=Path.Combine(Repository,"artifacts","cycle","x.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(cycle)!);await File.WriteAllTextAsync(cycle,"<TestRun cycle=\"0.99\"/>");
        var written=new DateTime(2026,11,7,7,52,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(cycle,written);

        var first=await Run(script,"-Phase","After","-RequirementIds","CAP-12-05","-Checkpoint","0.99","-TestArtifact","artifacts/cycle/x.trx");

        Assert.True(first.ExitCode==0,$"Exit {first.ExitCode}. Output: {first.Output} Errors: {first.Errors}");
        string record=Path.Combine(ReviewDirectory,"after-0.99.json");
        Assert.True(File.Exists(record),"The After checkpoint was not written to after-0.99.json. Files: "+string.Join(", ",Directory.GetFiles(ReviewDirectory).Select(Path.GetFileName)));
        Assert.False(File.Exists(Path.Combine(ReviewDirectory,"after.json")),"The script still writes the label-less after.json.");
        byte[] bytes=await File.ReadAllBytesAsync(record);
        using(var json=JsonDocument.Parse(bytes))
        {
            var root=json.RootElement;
            Assert.Equal(2,root.GetProperty("FormatVersion").GetInt32());
            Assert.Equal("0.99",root.GetProperty("Checkpoint").GetString());
            Assert.False(root.TryGetProperty("LatestTestArtifactUtc",out _),"The record still carries the single fixed LatestTestArtifactUtc field.");
            var artifacts=root.GetProperty("TestArtifacts").EnumerateArray().ToArray();
            var cited=Assert.Single(artifacts);
            Assert.Equal("artifacts/cycle/x.trx",cited.GetProperty("Path").GetString());
            Assert.Equal(written.ToString("O"),cited.GetProperty("LastWriteUtc").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cycle))),cited.GetProperty("Sha256").GetString());
        }

        var second=await Run(script,"-Phase","After","-RequirementIds","CAP-12-05","-Checkpoint","0.100","-TestArtifact","artifacts/cycle/x.trx");

        Assert.True(second.ExitCode==0,$"Exit {second.ExitCode}. Output: {second.Output} Errors: {second.Errors}");
        Assert.True(File.Exists(Path.Combine(ReviewDirectory,"after-0.100.json")),"The second checkpoint was not written to after-0.100.json.");
        Assert.Equal(bytes,await File.ReadAllBytesAsync(record));
    }

    [Fact]
    public async Task BeforeCheckpointMayOmitTestArtifactsAndRecordsAnEmptyList()
    {
        string script=await CreateFixture();

        var run=await Run(script,"-Phase","Before","-RequirementIds","CAP-12-05","-Checkpoint","0.45-initial");

        Assert.True(run.ExitCode==0,$"Exit {run.ExitCode}. Output: {run.Output} Errors: {run.Errors}");
        Assert.Contains("before-0.45-initial.json",run.Output,StringComparison.Ordinal);
        using var json=JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(ReviewDirectory,"before-0.45-initial.json")));
        Assert.Equal("0.45-initial",json.RootElement.GetProperty("Checkpoint").GetString());
        Assert.Equal(JsonValueKind.Array,json.RootElement.GetProperty("TestArtifacts").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("TestArtifacts").EnumerateArray());
    }

    [Theory]
    [InlineData("missing","test artifact")]
    [InlineData("absent-file","artifacts/cycle/absent.trx")]
    [InlineData("outside-root","outside the source tree")]
    [InlineData("bad-label","Cannot validate argument on parameter 'Checkpoint'")]
    public async Task CheckpointIsRefusedWithoutAWrittenRecord(string kind,string expected)
    {
        string script=await CreateFixture();
        string outside=Path.Combine(_root,"elsewhere.trx");await File.WriteAllTextAsync(outside,"<TestRun/>");
        string[] arguments=kind switch
        {
            "missing"=>new[]{"-Phase","After","-RequirementIds","CAP-12-05","-Checkpoint","0.99"},
            "absent-file"=>new[]{"-Phase","After","-RequirementIds","CAP-12-05","-Checkpoint","0.99","-TestArtifact","artifacts/cycle/absent.trx"},
            "outside-root"=>new[]{"-Phase","After","-RequirementIds","CAP-12-05","-Checkpoint","0.99","-TestArtifact",outside},
            "bad-label"=>new[]{"-Phase","Before","-RequirementIds","CAP-12-05","-Checkpoint","..\\0.99"},
            _=>throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var run=await Run(script,arguments);

        Assert.True(run.ExitCode!=0,$"The {kind} checkpoint was accepted. Output: {run.Output}");
        // Windows PowerShell wraps redirected error text at the console width, sometimes mid-word.
        Assert.Contains(expected,run.Errors.Replace("\r","").Replace("\n",""),StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(ReviewDirectory)&&Directory.EnumerateFiles(ReviewDirectory).Any(),"A refused checkpoint still wrote a record.");
        Assert.False(File.Exists(Path.Combine(Repository,"artifacts","0.99.json")),"The checkpoint label escaped the review folder.");
    }

    private async Task<string> CreateFixture()
    {
        string downloads=Path.Combine(_root,"profile","Downloads");
        Directory.CreateDirectory(Path.Combine(Repository,"scripts"));Directory.CreateDirectory(Path.Combine(Repository,"docs","zenith-sessions"));Directory.CreateDirectory(downloads);
        string script=Path.Combine(Repository,"scripts","Review-ZenithSessionsRequirements.ps1");
        File.Copy(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"scripts","Review-ZenithSessionsRequirements.ps1"),script);
        var utf8=new UTF8Encoding(false);var ledger=new StringBuilder("# Synthetic ledger\n\n");
        foreach(string name in new[]{"Roblox_Launcher_Full_Prompt_With_Encrypted_Account_Vault.txt","Roblox_Saved_Accounts_Encrypted_Vault_Addendum.txt","# MASTER PROMPT "+EmDash+" COMPLETE PRODUCT.txt"})
        {
            byte[] content=utf8.GetBytes("Synthetic prompt "+name+"\n");await File.WriteAllBytesAsync(Path.Combine(downloads,name),content);
            ledger.Append("- [F] ").Append(Convert.ToHexString(SHA256.HashData(content))).Append('\n');
        }
        ledger.Append("- **CAP-12-05 ").Append(EmDash).Append(" synthetic.** Exact public server arrival.\n");
        await File.WriteAllTextAsync(Path.Combine(Repository,"docs","zenith-sessions","REQUIREMENTS-LEDGER.md"),ledger.ToString(),utf8);
        return script;
    }

    private Task<(int ExitCode,string Output,string Errors)> Run(string script,params string[] arguments)=>
        PowerShellScriptCompatibilityTests.RunWindowsPowerShell(Repository,new Dictionary<string,string>{["USERPROFILE"]=Path.Combine(_root,"profile")},new[]{"-File",script}.Concat(arguments).ToArray());

    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith(Prefix,StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
