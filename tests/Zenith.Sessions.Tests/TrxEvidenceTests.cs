using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zenith.Sessions.Tests;
public sealed class TrxEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),"zenith-trx-tests-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task ARealSkippedResultIsCountedEvenWhenVstestNotExecutedCounterIsZero()
    {
        string file=await Write("NotExecuted",0);
        var result=await Run(file);Assert.True(result.ExitCode==0,result.Errors);
        using var json=JsonDocument.Parse(result.Output);
        Assert.Equal(2,json.RootElement.GetProperty("Total").GetInt32());Assert.Equal(1,json.RootElement.GetProperty("Executed").GetInt32());
        Assert.Equal(1,json.RootElement.GetProperty("NotExecuted").GetInt32());Assert.Equal(0,json.RootElement.GetProperty("ReportedNotExecuted").GetInt32());
        Assert.Equal("Skipped fixture",json.RootElement.GetProperty("SkippedNames")[0].GetString());
    }
    [Theory][InlineData("Failed")][InlineData("Error")][InlineData("Timeout")][InlineData("Aborted")]
    public async Task ErrorResultsNeverBecomePassingEvidence(string outcome)
    {var result=await Run(await Write(outcome,0));Assert.NotEqual(0,result.ExitCode);}
    private async Task<string> Write(string outcome,int notExecuted)
    {
        Directory.CreateDirectory(_root);string path=Path.Combine(_root,"fixture.trx");
        await File.WriteAllTextAsync(path,$"<TestRun><Results><UnitTestResult testName='Passed fixture' outcome='Passed'/><UnitTestResult testName='Skipped fixture' outcome='{outcome}'/></Results><ResultSummary><Counters total='2' executed='1' passed='1' failed='0' notExecuted='{notExecuted}'/></ResultSummary></TestRun>");return path;
    }
    private async Task<(int ExitCode,string Output,string Errors)> Run(string path)
    {
        string script=Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"scripts","Read-ZenithTestResults.ps1");
        string command="& '"+script.Replace("'","''")+"' -Path '"+path.Replace("'","''")+"' | ConvertTo-Json -Depth 4";
        return await PowerShellScriptCompatibilityTests.RunWindowsPowerShell(_root,new Dictionary<string,string>(),"-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
