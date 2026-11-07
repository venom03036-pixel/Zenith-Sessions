using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Zenith.Sessions.Tests;

// scripts/package-shipping.ps1 must label the package with the version the binary carries, and a failed publish must not cost the previous package.
// The script runs for real under Windows PowerShell 5.1; a fake dotnet.cmd first on PATH stands in for dotnet publish and records its arguments.
public sealed class PackageShippingScriptTests : IDisposable
{
    private const string Prefix="zenith-package-shipping-";
    private const string AppVersion="9.8.7";
    private readonly string _root=Path.Combine(Path.GetTempPath(),Prefix+Guid.NewGuid().ToString("N"));
    private string Repository=>Path.Combine(_root,"repo");
    private string Dist=>Path.Combine(Repository,"dist");
    private string PublishLog=>Path.Combine(_root,"dotnet-publish.log");

    [Fact]
    public async Task DefaultRunLabelsThePackageWithTheAppProjectVersionAndPublishesThatVersion()
    {
        string script=await CreateFixture(AppVersion);

        var run=await Run(script,publishExitCode:0);

        Assert.True(run.ExitCode==0,$"Exit {run.ExitCode}. Output: {run.Output} Errors: {run.Errors}");
        string package="ZenithMacros-v"+AppVersion,zip=package+"-win-x64.zip";
        Assert.Equal(new[]{package,zip},Directory.GetFileSystemEntries(Dist).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("-p:Version="+AppVersion,await File.ReadAllTextAsync(PublishLog),StringComparison.Ordinal);
        string readme=await File.ReadAllTextAsync(Path.Combine(Dist,package,"README.txt"));
        Assert.Contains("(Version "+AppVersion+")",readme,StringComparison.Ordinal);
        // Windows PowerShell 5.1 reads a script without a UTF-8 byte-order mark as ANSI; the heading dash and key arrows must survive.
        Assert.Contains("ZENITH MACROS "+(char)0x2014+" AUTOMATION SUITE",readme,StringComparison.Ordinal);
        Assert.Contains((char)0x2191+" (Up)",readme,StringComparison.Ordinal);
        Assert.Equal("new build",(await File.ReadAllTextAsync(Path.Combine(Dist,package,"MacroSuite.App.exe"))).Trim());
        using var archive=ZipFile.OpenRead(Path.Combine(Dist,zip));
        Assert.Contains(archive.Entries,entry=>entry.FullName=="MacroSuite.App.exe");
    }

    [Fact]
    public async Task ExplicitVersionReachesThePublishedBinary()
    {
        string script=await CreateFixture(AppVersion);

        var run=await Run(script,publishExitCode:0,"-Version","3.1.4");

        Assert.True(run.ExitCode==0,$"Exit {run.ExitCode}. Output: {run.Output} Errors: {run.Errors}");
        Assert.True(File.Exists(Path.Combine(Dist,"ZenithMacros-v3.1.4","MacroSuite.App.exe")),"The package folder is not labelled with the explicit -Version.");
        Assert.Contains("-p:Version=3.1.4",await File.ReadAllTextAsync(PublishLog),StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedPublishLeavesThePreviousPackageInPlace()
    {
        string script=await CreateFixture(AppVersion);
        string previous=Path.Combine(Dist,"ZenithMacros-v"+AppVersion);
        Directory.CreateDirectory(previous);
        await File.WriteAllTextAsync(Path.Combine(previous,"MacroSuite.App.exe"),"previous build");

        // -Version names the same package explicitly, so this test isolates the publish failure from how the version is chosen.
        var run=await Run(script,publishExitCode:1,"-Version",AppVersion);

        Assert.True(run.ExitCode!=0,$"A failed publish exited 0. Output: {run.Output}");
        Assert.True(File.Exists(PublishLog),"The fake dotnet publish never ran, so this test proves nothing. Errors: "+run.Errors);
        Assert.Equal("previous build",await File.ReadAllTextAsync(Path.Combine(previous,"MacroSuite.App.exe")));
        Assert.Equal(new[]{"ZenithMacros-v"+AppVersion},Directory.GetFileSystemEntries(Dist).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task MissingAppProjectVersionStopsBeforePublishing()
    {
        string script=await CreateFixture(null);

        var run=await Run(script,publishExitCode:0);

        Assert.True(run.ExitCode!=0,$"The script packaged without a version. Output: {run.Output}");
        Assert.False(File.Exists(PublishLog),"dotnet publish ran although the app project has no <Version>.");
        Assert.Contains("<Version>",run.Errors,StringComparison.Ordinal);
    }

    private async Task<string> CreateFixture(string? version)
    {
        string scripts=Path.Combine(Repository,"scripts"),app=Path.Combine(Repository,"src","MacroSuite.App"),bin=Path.Combine(_root,"bin");
        Directory.CreateDirectory(scripts);Directory.CreateDirectory(app);Directory.CreateDirectory(bin);
        string script=Path.Combine(scripts,"package-shipping.ps1");
        File.Copy(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"scripts","package-shipping.ps1"),script);
        // The real app project keeps its shape; only the version changes, so a hard-coded label cannot pass by accident.
        string project=await File.ReadAllTextAsync(Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot,"src","MacroSuite.App","MacroSuite.App.csproj"));
        var element=new Regex(@"[ \t]*<Version>[^<]*</Version>\r?\n?");
        Assert.Single(element.Matches(project));
        project=element.Replace(project,version is null?"":"    <Version>"+version+"</Version>"+Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(app,"MacroSuite.App.csproj"),project,new UTF8Encoding(true));
        string fake="""
            @echo off
            setlocal
            >>"%ZENITH_FAKE_DOTNET_LOG%" echo(%*
            set "OUT="
            :next
            if "%~1"=="" goto publish
            if /i "%~1"=="-o" set "OUT=%~2"
            shift
            goto next
            :publish
            if not defined OUT exit /b 90
            if not exist "%OUT%" mkdir "%OUT%"
            >"%OUT%\MacroSuite.App.exe" echo(new build
            exit /b %ZENITH_FAKE_DOTNET_EXIT%
            """;
        await File.WriteAllTextAsync(Path.Combine(bin,"dotnet.cmd"),fake.Replace("\n","\r\n").Replace("\r\r\n","\r\n"),new UTF8Encoding(false));
        return script;
    }

    private Task<(int ExitCode,string Output,string Errors)> Run(string script,int publishExitCode,params string[] arguments)=>
        PowerShellScriptCompatibilityTests.RunWindowsPowerShell(_root,new Dictionary<string,string>
        {
            ["PATH"]=Path.Combine(_root,"bin")+";"+Environment.GetEnvironmentVariable("PATH"),
            ["ZENITH_FAKE_DOTNET_LOG"]=PublishLog,
            ["ZENITH_FAKE_DOTNET_EXIT"]=publishExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },new[]{"-File",script}.Concat(arguments).ToArray());

    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(full).StartsWith(Prefix,StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
