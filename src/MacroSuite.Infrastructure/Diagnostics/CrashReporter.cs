using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace MacroSuite.Infrastructure.Diagnostics;

/// <summary>
/// Execution state snapshot supplied to crash reports.
/// </summary>
public sealed class CrashReportContext
{
    public string Version { get; init; } = "1.0.0";
    public bool IsRecording { get; init; }
    public bool IsPlaying { get; init; }
    public string? SelectedMacro { get; init; }
    public int EventsCaptured { get; init; }
    public int QueueLength { get; init; }
    public string? RuntimeVersion { get; init; }
    public string? VisualMatchMode { get; init; }
    public int? VisualSimilarityPercent { get; init; }
    public int? VisualPollIntervalMs { get; init; }
    public int? VisualTimeoutMs { get; init; }
    public double? VisualLastScore { get; init; }
}

/// <summary>
/// High-performance, fail-safe crash reporter and diagnostics subsystem.
/// Generates structured JSON crash reports, rolling daily application logs, and optional Win32 mini-dumps.
/// </summary>
public static class CrashReporter
{
    private static readonly object s_lock = new();
    private static string s_baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
    private static Func<CrashReportContext>? s_contextProvider;
    private static string? s_lastCrashFilePath;
    private static string? s_lastDumpFilePath;
    private static TimeSpan s_lastCpuTime = TimeSpan.Zero;
    private static DateTime s_lastCpuCheck = DateTime.UtcNow;

    public static string BaseDirectory => s_baseDirectory;
    public static string? LastCrashFilePath => s_lastCrashFilePath;
    public static string? LastDumpFilePath => s_lastDumpFilePath;

    /// <summary>
    /// Initializes crash reporting with a designated base directory and contextual provider.
    /// Idempotently prepares Logs/ and Diagnostics/ directory hierarchies.
    /// </summary>
    public static void Initialize(string? baseDirectory = null, Func<CrashReportContext>? contextProvider = null)
    {
        lock (s_lock)
        {
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                s_baseDirectory = Path.GetFullPath(baseDirectory);
            }

            s_contextProvider = contextProvider;

            EnsureDirectoriesExist();
        }
    }

    /// <summary>
    /// Updates the runtime context provider used to gather application and recorder state during a crash.
    /// </summary>
    public static void SetContextProvider(Func<CrashReportContext> contextProvider)
    {
        lock (s_lock)
        {
            s_contextProvider = contextProvider;
        }
    }

    /// <summary>
    /// Ensures all diagnostic and log directories exist.
    /// </summary>
    private static void EnsureDirectoriesExist()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(s_baseDirectory, "Logs", "crashes"));
            Directory.CreateDirectory(Path.Combine(s_baseDirectory, "Logs", "application"));
            Directory.CreateDirectory(Path.Combine(s_baseDirectory, "Diagnostics", "Logs"));
            Directory.CreateDirectory(Path.Combine(s_baseDirectory, "Diagnostics", "Dumps"));
            Directory.CreateDirectory(Path.Combine(s_baseDirectory, "Diagnostics", "Performance"));
        }
        catch
        {
            // Suppress initialization directory creation failures; individual log writes will retry
        }
    }

    /// <summary>
    /// Records an unhandled exception into a structured JSON crash log and rolling application log.
    /// Never throws exceptions under any circumstances.
    /// </summary>
    public static string? RecordCrash(Exception ex, string? customMessage = null, bool generateMiniDump = true)
    {
        lock (s_lock)
        {
            try
            {
                EnsureDirectoriesExist();

                var now = DateTime.Now;
                string timestampStr = now.ToString("yyyy-MM-ddTHH:mm:ss");
                string fileTimestamp = now.ToString("yyyy-MM-dd_HH-mm-ss");

                // Gather application and recorder context
                CrashReportContext ctx;
                try
                {
                    ctx = s_contextProvider?.Invoke() ?? new CrashReportContext();
                }
                catch
                {
                    ctx = new CrashReportContext();
                }

                // Performance metrics
                double cpuPercent = 0.0;
                double workingSetMb = 0.0;
                double managedHeapMb = 0.0;

                try
                {
                    using var currentProc = Process.GetCurrentProcess();
                    workingSetMb = Math.Round(currentProc.WorkingSet64 / (1024.0 * 1024.0), 2);
                    managedHeapMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2);

                    var currentCpuTime = currentProc.TotalProcessorTime;
                    var currentTime = DateTime.UtcNow;
                    var timeDiff = (currentTime - s_lastCpuCheck).TotalMilliseconds;
                    if (timeDiff > 200 && s_lastCpuTime > TimeSpan.Zero)
                    {
                        var cpuDiff = (currentCpuTime - s_lastCpuTime).TotalMilliseconds;
                        cpuPercent = Math.Round((cpuDiff / (timeDiff * Environment.ProcessorCount)) * 100.0, 1);
                        if (cpuPercent < 0) cpuPercent = 0;
                        if (cpuPercent > 100) cpuPercent = 100;
                    }
                    s_lastCpuTime = currentCpuTime;
                    s_lastCpuCheck = currentTime;
                }
                catch
                {
                    // Ignore perf counters if access denied or unavailable
                }

                // Build crash model matching required schema
                var crashModel = new
                {
                    timestamp = timestampStr,
                    exception = new
                    {
                        type = ex.GetType().FullName ?? ex.GetType().Name,
                        message = string.IsNullOrWhiteSpace(customMessage) ? ex.Message : $"{customMessage}: {ex.Message}",
                        stackTrace = ex.StackTrace ?? string.Empty,
                        innerException = ex.InnerException?.ToString()
                    },
                    application = new
                    {
                        version = ctx.Version,
                        runtime = ctx.RuntimeVersion ?? Environment.Version.ToString(),
                        recording = ctx.IsRecording,
                        playing = ctx.IsPlaying,
                        selectedMacro = ctx.SelectedMacro
                    },
                    performance = new
                    {
                        cpuPercent = cpuPercent,
                        workingSetMb = workingSetMb,
                        managedHeapMb = managedHeapMb
                    },
                    recorder = new
                    {
                        eventsCaptured = ctx.EventsCaptured,
                        queueLength = ctx.QueueLength
                    },
                    visualMatcher = new
                    {
                        mode = ctx.VisualMatchMode,
                        similarityPercent = ctx.VisualSimilarityPercent,
                        pollIntervalMs = ctx.VisualPollIntervalMs,
                        timeoutMs = ctx.VisualTimeoutMs,
                        lastScore = ctx.VisualLastScore,
                        lastMatched = (bool?)null,
                        algorithm = (string?)null
                    }
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                string json = JsonSerializer.Serialize(crashModel, options);

                string crashDir = Path.Combine(s_baseDirectory, "Logs", "crashes");
                string crashFilePath = Path.Combine(crashDir, $"crash_{fileTimestamp}.json");

                File.WriteAllText(crashFilePath, json);
                s_lastCrashFilePath = crashFilePath;

                // Also log to rolling application log
                LogError(ex, $"Application crash captured in {Path.GetFileName(crashFilePath)}: {customMessage}");

                // Generate optional crash dump
                if (generateMiniDump && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        string dumpDir = Path.Combine(s_baseDirectory, "Diagnostics", "Dumps");
                        string dumpPath = Path.Combine(dumpDir, $"crash_{fileTimestamp}.dmp");
                        if (TryWriteMiniDump(dumpPath))
                        {
                            s_lastDumpFilePath = dumpPath;
                        }
                    }
                    catch
                    {
                        // MiniDump is best-effort
                    }
                }

                return crashFilePath;
            }
            catch (Exception writeEx)
            {
                // Absolute fallback: write to emergency text file in temp or current directory
                try
                {
                    string emergencyFile = Path.Combine(Path.GetTempPath(), "macrosuite_emergency_crash.txt");
                    File.AppendAllText(emergencyFile, $"{DateTime.Now}: CRASH: {ex}\nCrashLogger Failure: {writeEx}\n\n");
                }
                catch { }

                return null;
            }
        }
    }

    /// <summary>
    /// Writes an informational message to the daily rolling application log.
    /// </summary>
    public static void LogInfo(string message) => WriteApplicationLog("INFO", message, null);

    /// <summary>
    /// Writes a warning message to the daily rolling application log.
    /// </summary>
    public static void LogWarning(string message) => WriteApplicationLog("WARN", message, null);

    /// <summary>
    /// Writes an error message to the daily rolling application log.
    /// </summary>
    public static void LogError(Exception? ex, string message) => WriteApplicationLog("ERROR", message, ex);

    private static void WriteApplicationLog(string level, string message, Exception? ex)
    {
        lock (s_lock)
        {
            try
            {
                EnsureDirectoriesExist();

                var now = DateTime.Now;
                string dateStr = now.ToString("yyyy-MM-dd");
                string logFile = Path.Combine(s_baseDirectory, "Logs", "application", $"app_{dateStr}.log");

                string line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                if (ex != null)
                {
                    line += $"{Environment.NewLine}  Exception: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}  StackTrace: {ex.StackTrace}";
                }

                File.AppendAllText(logFile, line + Environment.NewLine);
            }
            catch
            {
                // Logging failures must not terminate or disrupt the application
            }
        }
    }

    #region MiniDump Support

    [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeFileHandle hFile,
        uint dumpType,
        IntPtr expParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    private const uint MiniDumpNormal = 0x00000000;

    /// <summary>
    /// Attempts to write a mini-dump of the current process using dbghelp.dll.
    /// </summary>
    public static bool TryWriteMiniDump(string dumpFilePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            using var proc = Process.GetCurrentProcess();
            using var fs = new FileStream(dumpFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            return MiniDumpWriteDump(
                proc.Handle,
                (uint)proc.Id,
                fs.SafeFileHandle,
                MiniDumpNormal,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
