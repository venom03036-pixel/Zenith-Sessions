using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zenith.Sessions.Tests;

// Developer scripts must run under Windows PowerShell 5.1, the only PowerShell on a stock Windows PC.
public sealed class PowerShellScriptCompatibilityTests : IDisposable
{
    private const string Prefix="zenith-ps51-scripts-";
    internal static readonly string RepositoryRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
    internal static readonly string LuauDirectory=Path.Combine(RepositoryRoot,"artifacts","sessions-owned-bridge-0.57","luau");
    private static readonly char EmDash=(char)0x2014;
    private readonly string _root=Path.Combine(Path.GetTempPath(),Prefix+Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReviewCheckpointRecordsUnderWindowsPowerShell51()
    {
        string repository=Path.Combine(_root,"repo"),downloads=Path.Combine(_root,"profile","Downloads");
        Directory.CreateDirectory(Path.Combine(repository,"scripts"));Directory.CreateDirectory(Path.Combine(repository,"docs","zenith-sessions"));Directory.CreateDirectory(downloads);
        string script=Path.Combine(repository,"scripts","Review-ZenithSessionsRequirements.ps1");
        File.Copy(Path.Combine(RepositoryRoot,"scripts","Review-ZenithSessionsRequirements.ps1"),script);
        var utf8=new UTF8Encoding(false);var ledger=new StringBuilder("# Synthetic ledger\n\n");
        foreach(string name in new[]{"Roblox_Launcher_Full_Prompt_With_Encrypted_Account_Vault.txt","Roblox_Saved_Accounts_Encrypted_Vault_Addendum.txt","# MASTER PROMPT "+EmDash+" COMPLETE PRODUCT.txt"})
        {
            byte[] content=utf8.GetBytes("Synthetic prompt "+name+"\n");await File.WriteAllBytesAsync(Path.Combine(downloads,name),content);
            ledger.Append("- [F] ").Append(Convert.ToHexString(SHA256.HashData(content))).Append('\n');
        }
        ledger.Append("- **CYCLE-01 ").Append(EmDash).Append(" synthetic.** Review before work.\n");
        await File.WriteAllTextAsync(Path.Combine(repository,"docs","zenith-sessions","REQUIREMENTS-LEDGER.md"),ledger.ToString(),utf8);

        var run=await RunWindowsPowerShell(repository,new Dictionary<string,string>{["USERPROFILE"]=Path.Combine(_root,"profile")},"-File",script,"-Phase","Before","-RequirementIds","CYCLE-01","-Checkpoint","0.99");

        Assert.True(run.ExitCode==0,$"Exit {run.ExitCode}. Output: {run.Output} Errors: {run.Errors}");
        string record=Path.Combine(repository,"artifacts","sessions-requirements-review","before-0.99.json");
        byte[] bytes=await File.ReadAllBytesAsync(record);
        Assert.False(bytes.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF}),"The review record starts with a UTF-8 byte-order mark; earlier records have none.");
        using var json=JsonDocument.Parse(bytes);
        var sources=json.RootElement.GetProperty("Sources").EnumerateArray().ToArray();
        Assert.Equal(3,sources.Length);
        Assert.All(sources,source=>Assert.True(source.GetProperty("MatchesReviewedLedger").GetBoolean(),source.GetProperty("Name").GetString()));
        Assert.Contains(sources,source=>source.GetProperty("Name").GetString()=="# MASTER PROMPT "+EmDash+" COMPLETE PRODUCT.txt");
        Assert.Equal("- **CYCLE-01 "+EmDash+" synthetic.** Review before work.",json.RootElement.GetProperty("Scope")[0].GetProperty("ReviewedClause").GetString());
    }

    [LuauBinariesFact]
    public async Task OwnedBridgeRunnerWritesBomFreeUtf8UnderWindowsPowerShell51()
    {
        // HANDOFF.md passes a relative output folder; the shell location differs from the process directory here on purpose.
        string location=Path.Combine(_root,"location"),processDirectory=Path.Combine(_root,"process");
        Directory.CreateDirectory(location);Directory.CreateDirectory(processDirectory);
        string quote(string path)=>"'"+path.Replace("'","''")+"'";
        string command="$ErrorActionPreference='Stop'; Set-Location -LiteralPath "+quote(location)+"; & "+quote(Path.Combine(RepositoryRoot,"scripts","Test-ZenithOwnedExperienceBridge.ps1"))+" -LuauDirectory "+quote(LuauDirectory)+" -OutputDirectory 'bridge-output'; exit 0";

        var run=await RunWindowsPowerShell(processDirectory,new Dictionary<string,string>(),"-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

        Assert.True(run.ExitCode==0,$"Exit {run.ExitCode}. Output: {run.Output} Errors: {run.Errors}");
        string output=Path.Combine(location,"bridge-output");
        byte[] program=await File.ReadAllBytesAsync(Path.Combine(output,"server-tests.luau"));
        Assert.False(program.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF}),"The generated Luau program starts with a UTF-8 byte-order mark.");
        Assert.StartsWith("local function installDisabled(",Encoding.UTF8.GetString(program),StringComparison.Ordinal);
        byte[] log=await File.ReadAllBytesAsync(Path.Combine(output,"server-tests.log"));
        Assert.False(log.AsSpan().StartsWith(new byte[]{0xFF,0xFE}),"The Luau log was written as UTF-16.");
        string text=new UTF8Encoding(false,true).GetString(log);
        Assert.StartsWith("PASS ",text,StringComparison.Ordinal);
        Assert.Contains(" owned-experience server checks.",text,StringComparison.Ordinal);
        Assert.Contains("PASS ",run.Output,StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(processDirectory,"bridge-output")),"The runner resolved the relative output folder against the process directory instead of the shell location.");
    }

    internal static async Task<(int ExitCode,string Output,string Errors)> RunWindowsPowerShell(string workingDirectory,IReadOnlyDictionary<string,string> environment,params string[] arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workingDirectory};
        foreach(string argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass"}.Concat(arguments))start.ArgumentList.Add(argument);
        foreach(var pair in environment)start.Environment[pair.Key]=pair.Value;
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try {await process.WaitForExitAsync(deadline.Token);}
        finally {if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}}
        return (process.ExitCode,await output,await errors);
    }

    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith(Prefix,StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}

public sealed class LuauBinariesFactAttribute : FactAttribute
{
    public LuauBinariesFactAttribute()
    {
        if(!File.Exists(Path.Combine(PowerShellScriptCompatibilityTests.LuauDirectory,"luau.exe"))||!File.Exists(Path.Combine(PowerShellScriptCompatibilityTests.LuauDirectory,"luau-compile.exe")))
            Skip="Needs the official Luau 0.745 binaries under artifacts/sessions-owned-bridge-0.57/luau.";
    }
}
