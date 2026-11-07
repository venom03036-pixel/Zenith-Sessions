using System.IO;
using System.Text.Json;
using FluentAssertions;
using MacroSuite.Infrastructure.Diagnostics;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Diagnostics;

public class CrashReporterTests : IDisposable
{
    private readonly string _tempDir;

    public CrashReporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MacroSuite_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        CrashReporter.Initialize(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Initialize_CreatesExpectedDirectoryHierarchy()
    {
        Directory.Exists(Path.Combine(_tempDir, "Logs", "crashes")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tempDir, "Logs", "application")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tempDir, "Diagnostics", "Logs")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tempDir, "Diagnostics", "Dumps")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tempDir, "Diagnostics", "Performance")).Should().BeTrue();
    }

    [Fact]
    public void RecordCrash_WritesStructuredJsonReportWithContextAndMetrics()
    {
        CrashReporter.SetContextProvider(() => new CrashReportContext
        {
            Version = "2.4.1",
            IsRecording = true,
            IsPlaying = false,
            SelectedMacro = "Boss Rush Combo",
            EventsCaptured = 1532,
            QueueLength = 4
        });

        var testEx = new InvalidOperationException("Simulated engine crash");
        string? crashFile = CrashReporter.RecordCrash(testEx, "Macro execution faulted", generateMiniDump: false);

        crashFile.Should().NotBeNull();
        File.Exists(crashFile!).Should().BeTrue();

        string jsonContent = File.ReadAllText(crashFile!);
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        // Verify root fields
        root.TryGetProperty("timestamp", out var ts).Should().BeTrue();
        ts.GetString().Should().NotBeNullOrWhiteSpace();

        // Verify exception fields
        root.TryGetProperty("exception", out var exElem).Should().BeTrue();
        exElem.GetProperty("type").GetString().Should().Be(typeof(InvalidOperationException).FullName);
        exElem.GetProperty("message").GetString().Should().Contain("Macro execution faulted");
        exElem.GetProperty("message").GetString().Should().Contain("Simulated engine crash");

        // Verify application fields
        root.TryGetProperty("application", out var appElem).Should().BeTrue();
        appElem.GetProperty("version").GetString().Should().Be("2.4.1");
        appElem.GetProperty("recording").GetBoolean().Should().BeTrue();
        appElem.GetProperty("playing").GetBoolean().Should().BeFalse();
        appElem.GetProperty("selectedMacro").GetString().Should().Be("Boss Rush Combo");

        // Verify performance fields
        root.TryGetProperty("performance", out var perfElem).Should().BeTrue();
        perfElem.GetProperty("workingSetMb").GetDouble().Should().BeGreaterThan(0);
        perfElem.GetProperty("managedHeapMb").GetDouble().Should().BeGreaterThan(0);

        // Verify recorder telemetry fields
        root.TryGetProperty("recorder", out var recElem).Should().BeTrue();
        recElem.GetProperty("eventsCaptured").GetInt32().Should().Be(1532);
        recElem.GetProperty("queueLength").GetInt32().Should().Be(4);
    }

    [Fact]
    public void LogInfo_And_LogError_WritesToRollingApplicationLog()
    {
        CrashReporter.LogInfo("Worker thread started successfully.");
        CrashReporter.LogWarning("Potential high latency detected.");
        CrashReporter.LogError(new TimeoutException("Operation timed out"), "Heartbeat failed.");

        string appLogDir = Path.Combine(_tempDir, "Logs", "application");
        var logFiles = Directory.GetFiles(appLogDir, "app_*.log");
        logFiles.Should().NotBeEmpty();

        string content = File.ReadAllText(logFiles[0]);
        content.Should().Contain("[INFO] Worker thread started successfully.");
        content.Should().Contain("[WARN] Potential high latency detected.");
        content.Should().Contain("[ERROR] Heartbeat failed.");
        content.Should().Contain("TimeoutException");
    }

    [Fact]
    public void TryWriteMiniDump_HandlesGracefullyWithoutThrowing()
    {
        string dumpPath = Path.Combine(_tempDir, "Diagnostics", "Dumps", "test_dump.dmp");
        var act = () => CrashReporter.TryWriteMiniDump(dumpPath);
        act.Should().NotThrow();
    }
}
