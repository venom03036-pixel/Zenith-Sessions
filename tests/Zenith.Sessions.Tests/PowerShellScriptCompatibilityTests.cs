using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Zenith.Sessions.Tests;

// Developer scripts must run under Windows PowerShell 5.1, the only PowerShell on a stock Windows PC.
public sealed class PowerShellScriptCompatibilityTests : IDisposable
{
    private const string Prefix="zenith-ps51-scripts-";
    internal static readonly string? FoundRepository=FindRepository(AppContext.BaseDirectory);
    internal static string RepositoryRoot=>FoundRepository??throw new InvalidOperationException("No repository (scripts\\Check-ZenithSessionsManager.ps1) was found within 8 folders above the test output "+AppContext.BaseDirectory+".");
    internal static string LuauDirectory=>Path.Combine(RepositoryRoot,"artifacts","sessions-owned-bridge-0.57","luau");
    private static readonly char EmDash=(char)0x2014;
    private readonly string _root=Path.Combine(Path.GetTempPath(),Prefix+Guid.NewGuid().ToString("N"));

    [Fact]
    public void RepositoryRootFollowsTheRepositoryNotTheOutputDepth()
    {
        string repo=Path.Combine(_root,"repo");
        Directory.CreateDirectory(Path.Combine(repo,"scripts"));
        File.WriteAllBytes(Path.Combine(repo,"scripts","Check-ZenithSessionsManager.ps1"),[]);
        // The manager check script builds the tests into artifacts\sessions-tests-bin\, two levels below the repository.
        string runnerOutput=Path.Combine(repo,"artifacts","sessions-tests-bin")+Path.DirectorySeparatorChar;Directory.CreateDirectory(runnerOutput);
        Assert.Equal(repo,FindRepository(runnerOutput));
        // The default layout is tests\Zenith.Sessions.Tests\bin\<Config>\<tfm>\, five levels below it.
        string defaultOutput=Path.Combine(repo,"tests","Zenith.Sessions.Tests","bin","Release","net10.0-windows10.0.19041.0")+Path.DirectorySeparatorChar;Directory.CreateDirectory(defaultOutput);
        Assert.Equal(repo,FindRepository(defaultOutput));
        // No repository above the output: null, so the script tests fail with a plain message instead of reading another folder.
        string elsewhere=Path.Combine(_root,"elsewhere","a","b")+Path.DirectorySeparatorChar;Directory.CreateDirectory(elsewhere);
        Assert.Null(FindRepository(elsewhere));
        // Whichever layout this run uses, the shared root is the repository that holds the scripts the tests copy and scan.
        Assert.True(File.Exists(Path.Combine(RepositoryRoot,"scripts","Check-ZenithSessionsManager.ps1")),"RepositoryRoot is "+RepositoryRoot);
        Assert.Equal(Path.Combine(RepositoryRoot,"artifacts","sessions-owned-bridge-0.57","luau"),LuauDirectory);
    }

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

    [Fact]
    public async Task WindowsPowerShellUsesItsOwnStockModulesWhenTheCallerSuppliesOnlyPowerShell7Modules()
    {
        Directory.CreateDirectory(_root);
        string fixture=Path.Combine(_root,"hash-fixture.txt");
        byte[] content=Encoding.UTF8.GetBytes("synthetic stock module check");await File.WriteAllBytesAsync(fixture,content);
        string quote(string path)=>"'"+path.Replace("'","''")+"'";
        string command="$ErrorActionPreference='Stop'; $hash=Get-FileHash -LiteralPath "+quote(fixture)+" -Algorithm SHA256; "
            +"[pscustomobject]@{Major=$PSVersionTable.PSVersion.Major;Minor=$PSVersionTable.PSVersion.Minor;Hash=$hash.Hash;ModulePath=$env:PSModulePath} | ConvertTo-Json -Compress";
        // A portable stand-in for the caller's PS7-only module tree. Its Utility manifest cannot load under 5.1.
        string ps7=Path.Combine(_root,"PowerShell7","Modules"),utility=Path.Combine(ps7,"Microsoft.PowerShell.Utility");
        Directory.CreateDirectory(utility);
        await File.WriteAllTextAsync(Path.Combine(utility,"Microsoft.PowerShell.Utility.psd1"),"@{ModuleVersion='7.0.0';PowerShellVersion='7.0';FunctionsToExport=@('Get-FileHash')}");
        var inherited=new Dictionary<string,string>{["PSModulePath"]=ps7,["USERPROFILE"]=Path.Combine(_root,"profile")};
        var run=await RunWindowsPowerShell(_root,inherited,"-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        Assert.True(run.ExitCode==0,$"Exit {run.ExitCode}. Output: {run.Output} Errors: {run.Errors}");
        using var result=JsonDocument.Parse(run.Output);
        Assert.Equal(5,result.RootElement.GetProperty("Major").GetInt32());
        Assert.Equal(1,result.RootElement.GetProperty("Minor").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)),result.RootElement.GetProperty("Hash").GetString());
        string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","Modules");
        Assert.Contains(expected,result.RootElement.GetProperty("ModulePath").GetString()!.Split(Path.PathSeparator),StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(ps7,result.RootElement.GetProperty("ModulePath").GetString()!,StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ScriptsAvoidWhatWindowsPowerShell51CannotRun()
    {
        // Windows PowerShell 5.1 runs on .NET Framework 4.x, which lacks these .NET Core members, and it reads a script that has no
        // UTF-8 byte-order mark in the ANSI code page, so any non-ASCII text in such a script reaches its output as mojibake.
        var missingFromNetFramework=new (string Api,Regex Use)[]
        {
            ("ProcessStartInfo.ArgumentList",new Regex(@"\.ArgumentList\b")),
            ("Path.GetRelativePath",new Regex(@"\bGetRelativePath\s*\(")),
            ("String.Contains(string, StringComparison)",new Regex(@"\.Contains\s*\([^)]*StringComparison\]")),
            ("-Encoding utf8NoBOM/utf8BOM",new Regex(@"-Encoding\s+['""]?utf8(No)?BOM\b",RegexOptions.IgnoreCase)),
        };
        var problems=new List<string>();
        foreach(string path in Directory.EnumerateFiles(Path.Combine(RepositoryRoot,"scripts"),"*.ps1").Order(StringComparer.Ordinal))
        {
            byte[] bytes=File.ReadAllBytes(path);
            bool bom=bytes.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF});
            string text=Encoding.UTF8.GetString(bytes,bom?3:0,bytes.Length-(bom?3:0)),name=Path.GetFileName(path);
            // A script that declares PowerShell 7 stops at once with a clear message instead of failing part-way.
            if(Regex.IsMatch(text,@"^#Requires\s+-Version\s+([6-9]|\d\d)",RegexOptions.IgnoreCase|RegexOptions.Multiline))continue;
            foreach(var (api,use) in missingFromNetFramework)
                foreach(Match match in use.Matches(text))problems.Add($"{name}:{text.AsSpan(0,match.Index).Count('\n')+1} uses {api}");
            if(!bom&&bytes.Any(value=>value>0x7F))problems.Add($"{name} has non-ASCII text but no UTF-8 byte-order mark");
        }
        Assert.True(problems.Count==0,"Windows PowerShell 5.1 cannot run these scripts as written:"+Environment.NewLine+string.Join(Environment.NewLine,problems));
    }

    /// <summary>
    /// The repository these script tests copy and scan, found by walking up from the test output (at most 8 levels) to the
    /// folder that holds scripts\Check-ZenithSessionsManager.ps1, so the default bin\ layout and the check script's
    /// artifacts\sessions-tests-bin\ output both find the same place. The marker is a file this project already embeds, so
    /// renaming it breaks the build first. Null when no repository is above the output.
    /// </summary>
    internal static string? FindRepository(string baseDirectory)
    {
        var directory=new DirectoryInfo(Path.GetFullPath(baseDirectory));
        for(int depth=0;directory is not null&&depth<8;depth++,directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"scripts","Check-ZenithSessionsManager.ps1")))return directory.FullName;
        return null;
    }

    internal static async Task<(int ExitCode,string Output,string Errors)> RunWindowsPowerShell(string workingDirectory,IReadOnlyDictionary<string,string> environment,params string[] arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workingDirectory};
        foreach(string argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass"}.Concat(arguments))start.ArgumentList.Add(argument);
        foreach(var pair in environment)start.Environment[pair.Key]=pair.Value;
        // A PS7 caller can export its own incompatible Utility/Management modules. These are stock-5.1 compatibility
        // checks, so isolate only this child's module discovery to the modules beside the exact powershell.exe we start.
        start.Environment["PSModulePath"]=Path.Combine(Path.GetDirectoryName(start.FileName)!,"Modules");
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
        // No repository above the output is not a reason to skip: the test then runs and fails with RepositoryRoot's message.
        if(PowerShellScriptCompatibilityTests.FoundRepository is null)return;
        if(!File.Exists(Path.Combine(PowerShellScriptCompatibilityTests.LuauDirectory,"luau.exe"))||!File.Exists(Path.Combine(PowerShellScriptCompatibilityTests.LuauDirectory,"luau-compile.exe")))
            Skip="Needs the official Luau 0.745 binaries under artifacts/sessions-owned-bridge-0.57/luau.";
    }
}
