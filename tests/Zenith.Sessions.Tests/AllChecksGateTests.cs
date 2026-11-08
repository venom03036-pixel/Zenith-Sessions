using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class AllChecksGateTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-gate-tests-"+Guid.NewGuid().ToString("N"));
    private const string Paid="MacroSuite.Infrastructure.Tests.AI.AIVisionLiveActionTests.AIVision_CloudProvider_ClicksGreenTargetButton_WhenKeyConfigured";
    private static string Quote(string value)=>"'"+value.Replace("'","''")+"'";

    [Fact]
    public async Task TheLocalPlanIncludesEverySuiteAndAlwaysExcludesExactlyThePaidTest()
    {
        Directory.CreateDirectory(_root);
        var result=await PowerShellScriptCompatibilityTests.RunWindowsPowerShell(_root,new Dictionary<string,string>(),
            "-File",Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"scripts","Run-AllChecks.ps1"),"-PlanOnly","-OutputDirectory",_root);
        Assert.True(result.ExitCode==0,result.Errors);
        using var record=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"checks.json")));
        Assert.Equal("Planned",record.RootElement.GetProperty("Status").GetString());
        Assert.False(record.RootElement.GetProperty("ReleaseReady").GetBoolean());
        Assert.Equal(Paid,record.RootElement.GetProperty("ExcludedTest").GetString());
        var steps=record.RootElement.GetProperty("Steps").EnumerateArray().ToArray();
        var tests=steps.Where(step=>step.GetProperty("Name").GetString()!.EndsWith(" tests",StringComparison.Ordinal)).ToArray();
        Assert.Equal(6,tests.Length);
        var actual=Directory.GetFiles(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"tests"),"*Tests.csproj",SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        Assert.Equal(actual,tests.Select(step=>step.GetProperty("Name").GetString()![..^6]).Order(StringComparer.Ordinal));
        foreach(var test in tests)
        {
            var args=test.GetProperty("Arguments").EnumerateArray().Select(value=>value.GetString()).ToArray();
            Assert.Equal("FullyQualifiedName!="+Paid,args[Array.IndexOf(args,"--filter")+1]);
            Assert.Contains("--no-build",args);Assert.Contains("Release",args);
        }
        Assert.Equal(6,steps.Count(step=>step.GetProperty("Arguments").EnumerateArray().Any(arg=>arg.GetString()=="-warnaserror")));
        Assert.Contains(steps,step=>step.GetProperty("Kind").GetString()=="Luau");
        Assert.Contains(steps,step=>step.GetProperty("Kind").GetString()=="Package"&&step.GetProperty("Arguments").EnumerateArray().Any(arg=>arg.GetString()=="-StageOnly"));
        Assert.All(steps,step=>Assert.Equal("Pending",step.GetProperty("Status").GetString()));
    }

    [Fact]
    public async Task FailedBuildsAreRecordedAndNeverRunStaleTestsOrClaimPackagedSuccess()
    {
        string scripts=Path.Combine(_root,"scripts"),output=Path.Combine(_root,"results");Directory.CreateDirectory(scripts);
        File.Copy(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"scripts","Run-AllChecks.ps1"),Path.Combine(scripts,"Run-AllChecks.ps1"));
        await File.WriteAllTextAsync(Path.Combine(scripts,"Get-ZenithCheckTools.ps1"),"param([switch]$AllowDownload)\nthrow 'synthetic unavailable tools'");
        foreach(string project in Directory.GetFiles(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"tests"),"*Tests.csproj",SearchOption.AllDirectories))
        {
            string name=Path.GetFileNameWithoutExtension(project),folder=Path.Combine(_root,"tests",name);Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder,name+".csproj"),"<Project />");
        }
        string command="function dotnet { if($args[0] -ne 'build'){throw 'stale test command ran'}; $global:LASTEXITCODE=23; 'synthetic build failure' }; "
            +"try { & "+Quote(Path.Combine(scripts,"Run-AllChecks.ps1"))+" -NoRestore -OutputDirectory "+Quote(output)+"; exit 0 } catch { $_.Exception.Message; exit 17 }";
        var result=await PowerShellScriptCompatibilityTests.RunWindowsPowerShell(_root,new Dictionary<string,string>(),"-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        Assert.Equal(17,result.ExitCode);
        using var record=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output,"checks.json")));
        Assert.Equal("Failed",record.RootElement.GetProperty("Status").GetString());
        var steps=record.RootElement.GetProperty("Steps").EnumerateArray().ToArray();
        Assert.All(steps,step=>Assert.Equal("Failed",step.GetProperty("Status").GetString()));
        Assert.Equal(6,steps.Count(step=>step.GetProperty("ExitCode").GetInt32()==23));
        Assert.All(steps.Where(step=>step.GetProperty("Name").GetString()!.EndsWith(" tests",StringComparison.Ordinal)),
            step=>Assert.Contains("stale binaries will not run",step.GetProperty("Failure").GetString()));
    }

    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith("zenith-gate-tests-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
