using System.Text.Json;
using Xunit;
using Zenith.Sessions.Capabilities;

namespace Zenith.Sessions.CapabilityHarness.Tests;

/// <summary>These tests redirect the process-wide Console.Error, so they never run beside other tests.</summary>
[CollectionDefinition(nameof(ProgramExitCodeTests), DisableParallelization = true)]
public sealed class ProgramExitCodeCollection;

[Collection(nameof(ProgramExitCodeTests))]
public sealed class ProgramExitCodeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "zenith-exit-code-test-" + Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(_folder, "out");

    public ProgramExitCodeTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task MissingSourceFileReturnsConfigurationFailure()
    {
        string emptyRoot = Path.Combine(_folder, "root");
        Directory.CreateDirectory(emptyRoot);

        var (code, error) = await CaptureErrorAsync(() => Program.Main(["--source-root", emptyRoot, "--output", Output]));

        Assert.Equal(2, code);
        Assert.Contains("Missing or oversized required source file", error);
        Assert.Contains("no release gate was approved", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task StaleNativeReportReturnsConfigurationFailure()
    {
        var completed = DateTimeOffset.UtcNow.AddHours(-25);
        var report = new NativeProbeReport(1, "zenith-native-probe/0.3.0", EvidenceOrigin.NativeControlledExercise,
            completed.AddSeconds(-1), completed, true, [new("fixture", true, "Synthetic fixture")], "Own windows only");
        string native = Path.Combine(_folder, "native.json");
        await Reports.WriteAtomicAsync(native, JsonSerializer.Serialize(report, Reports.JsonOptions), default);

        var (code, error) = await CaptureErrorAsync(() => Program.Main(
            ["--source-root", RepositoryRoot(), "--output", Output, "--native-report", native]));

        Assert.Equal(2, code);
        Assert.Contains("more than 24 hours old", error);
        Assert.Contains("no release gate was approved", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(Output, "feasibility.json")));
    }

    [Fact]
    public async Task ExpiredDeadlineReturnsConfigurationFailureNotUserCancellation()
    {
        var (code, error) = await CaptureErrorAsync(() => Program.RunAsync(
            ["--source-root", RepositoryRoot(), "--output", Output], CancellationToken.None, new CancellationToken(canceled: true)));

        Assert.Equal(2, code);
        Assert.Contains("timed out", error);
        Assert.DoesNotContain("cancelled", error);
        Assert.False(File.Exists(Path.Combine(Output, "feasibility.json")));
    }

    [Fact]
    public async Task UserCancellationReturns130()
    {
        var (code, error) = await CaptureErrorAsync(() => Program.RunAsync(
            ["--source-root", RepositoryRoot(), "--output", Output], new CancellationToken(canceled: true), CancellationToken.None));

        Assert.Equal(130, code);
        Assert.Contains("cancelled", error);
        Assert.False(File.Exists(Path.Combine(Output, "feasibility.json")));
    }

    private static async Task<(int Code, string Error)> CaptureErrorAsync(Func<Task<int>> run)
    {
        var original = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try { return (await run(), error.ToString()); }
        finally { Console.SetError(original); }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("The repository root (the folder holding AGENTS.md) was not found above the test output.");
    }

    public void Dispose()
    {
        // Only newly-created, flat, test-owned directories; never recursively delete a computed tree.
        foreach (string directory in new[] { Output, Path.Combine(_folder, "root"), _folder })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
    }
}
