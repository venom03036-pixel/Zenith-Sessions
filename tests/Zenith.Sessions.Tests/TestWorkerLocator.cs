using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Finds a test-only worker in its own build output, where its apphost, main assembly and runtime files belong together.
/// The test project's ProjectReference (ReferenceOutputAssembly="false") builds each worker but copies only its apphost,
/// deps.json and runtimeconfig.json beside the tests; that partial copy has no main assembly and exits before readiness,
/// and a .NET 8 worker must not borrow the .NET 10 Windows projection that sits beside the tests anyway.
/// </summary>
internal static class TestWorkerLocator
{
    public static string Find(string project, string targetFramework)
    {
        string configuration = typeof(TestWorkerLocator).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        if (configuration is not ("Release" or "Debug")) throw new InvalidOperationException("Unknown test worker configuration.");
        string? directory = AppContext.BaseDirectory;
        for (int depth = 0; directory is not null && depth < 8; depth++, directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)))
        {
            string folder = Path.Combine(directory, "tools", project);
            if (!File.Exists(Path.Combine(folder, project + ".csproj"))) continue;
            string output = Path.Combine(folder, "bin", configuration, targetFramework);
            string executable = Path.Combine(output, project + ".exe");
            if (File.Exists(executable) && File.Exists(Path.Combine(output, project + ".dll"))) return executable;
            break;
        }
        throw new InvalidOperationException("Build the test-only worker " + project + " through the test project's ProjectReference before running these fixtures.");
    }

    private static readonly TimeSpan DiagnosticWait = TimeSpan.FromSeconds(2);
    private const int StandardErrorExcerpt = 1024;

    /// <summary>
    /// Says why a worker stopped, for a failure message: its exit code in decimal and hex (so a .NET host code such as
    /// 0x80008083 is recognisable), what the code means, and the start of its standard error when the caller passes the
    /// read of that redirected stream. The workers themselves print nothing there; the .NET host and the runtime's
    /// unhandled-exception report do, and both name the cause first and add paths or stack frames after it, so a long
    /// report keeps its start. Every wait and read is bounded, and a failed read is reported in the text, so the
    /// description never replaces the original failure. Workers run on synthetic fixtures only; the excerpt can name
    /// local paths but holds no secrets.
    /// </summary>
    public static async Task<string> DescribeExitAsync(Process process, Task<string>? standardError, Func<int, string?>? meaning = null)
    {
        var text = new StringBuilder();
        try
        {
            try { await process.WaitForExitAsync().WaitAsync(DiagnosticWait).ConfigureAwait(false); }
            catch (TimeoutException) { /* Reported just below as still running. */ }
            if (!process.HasExited) return "the worker was still running " + DiagnosticWait.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s later";
            int code = process.ExitCode;
            text.Append("exit code ").Append(code.ToString(CultureInfo.InvariantCulture))
                .Append(" (0x").Append(code.ToString("X8", CultureInfo.InvariantCulture)).Append(')');
            string? known = ((uint)code & 0xFFFFFF00u) == 0x80008000u ? "the .NET host could not start the worker" : meaning?.Invoke(code);
            if (known is not null) text.Append(", ").Append(known);
            if (standardError is not null)
            {
                string error = (await standardError.WaitAsync(DiagnosticWait).ConfigureAwait(false)).Trim();
                if (error.Length == 0) text.Append("; no standard error output");
                else text.Append("; standard error: ").Append(error.Length > StandardErrorExcerpt ? error[..StandardErrorExcerpt] + "..." : error);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            text.Append(text.Length == 0 ? "" : "; ").Append("exit details unavailable (").Append(error.GetType().Name).Append(')');
        }
        return text.ToString();
    }
}
