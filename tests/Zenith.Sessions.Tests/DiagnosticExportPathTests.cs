using System.Diagnostics;
using System.IO;
using System.Text;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class DiagnosticExportPathTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-export-path-tests-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task DeviceAndJunctionAliasesCannotSelectProtectedSettings()
    {
        string data=Path.Combine(_root,"data"),app=Path.Combine(_root,"app"),share=Path.Combine(_root,"share"),alias=Path.Combine(_root,"alias");
        Directory.CreateDirectory(data);Directory.CreateDirectory(app);Directory.CreateDirectory(share);
        string settings=Path.Combine(data,"settings.json");await File.WriteAllTextAsync(settings,"preserve me");
        Assert.Throws<ArgumentException>(()=>DiagnosticExportPath.Validate("\\\\?\\"+settings,data,app));
        Assert.Throws<ArgumentException>(()=>DiagnosticExportPath.Validate(settings,data,app));
        Assert.Equal(Path.Combine(share,"report.json"),DiagnosticExportPath.Validate(Path.Combine(share,"report.json"),data,app));
        Assert.Throws<ArgumentException>(()=>DiagnosticExportPath.Validate(Path.Combine(share,"report.json"),data,Path.GetPathRoot(app)!));
        // Native Windows junction creation with literal synthetic paths; no external data or shell deletion.
        string quote(string path)=>"'"+path.Replace("'","''")+"'";
        string script="$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path "+quote(alias)+" -Target "+quote(data)+" | Out-Null";
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32/WindowsPowerShell/v1.0/powershell.exe")) {UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-EncodedCommand");start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process=Process.Start(start)!;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try {await process.WaitForExitAsync(deadline.Token);Assert.Equal(0,process.ExitCode);}
        finally {if(!process.HasExited){process.Kill();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}}
        try {Assert.Throws<ArgumentException>(()=>DiagnosticExportPath.Validate(Path.Combine(alias,"settings.json"),data,app));Assert.Equal("preserve me",await File.ReadAllTextAsync(settings));}
        finally {if(Directory.Exists(alias))Directory.Delete(alias,false);}
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith("zenith-export-path-tests-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        string alias=Path.Combine(full,"alias");if(Directory.Exists(alias))Directory.Delete(alias,false);
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
