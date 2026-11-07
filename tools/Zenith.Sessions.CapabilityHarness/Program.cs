using System.Text.Json;
using Zenith.Sessions.Capabilities;
using Zenith.Sessions.Capabilities.Infrastructure;

namespace Zenith.Sessions.CapabilityHarness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("Zenith Sessions capability harness (.NET 10)\n" +
                "Usage: --source-root <Personal source directory> --output <report directory> [--native-report <own-window report.json>]\n" +
                "Reads bounded Windows metadata and runs controlled Core boundary checks.\n" +
                "Does not send native input, capture screens, launch games or read credentials.\n" +
                "Exit codes: 0 gates passed; 2 harness/configuration failure; 3 release gate blocked; 130 cancelled.");
            return 0;
        }
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = Parse(args);
            string root = Path.GetFullPath(options.SourceRoot);
            var source = Reports.AuditSource(root);
            var environment = WindowsDiscovery.Capture(lifetime.Token);
            var tests = await BoundaryProbe.RunAsync(lifetime.Token);
            var facts = CapabilityGate.AssessExistingImplementation();
            var gate = CapabilityGate.Evaluate(facts);
            var native = options.NativeReport is null ? null : await NativeEvidence.ReadAsync(options.NativeReport, lifetime.Token);
            var report = new FeasibilityReport(2, DateTimeOffset.UtcNow, environment, source,
                Reports.Documentation, tests, facts, gate, native);
            string output = Path.GetFullPath(options.Output);
            await Reports.WriteAtomicAsync(Path.Combine(output, "feasibility.json"),
                JsonSerializer.Serialize(report, Reports.JsonOptions), lifetime.Token);
            await Reports.WriteAtomicAsync(Path.Combine(output, "feasibility.md"), Reports.Markdown(report), lifetime.Token);
            Console.WriteLine($"Controlled checks: {tests.Count(t => t.Passed)}/{tests.Count} passed (not live-client tests).");
            Console.WriteLine($"Roblox process metadata entries: {environment.Processes.Count}; independent account identity unverified.");
            Console.WriteLine($"Feasibility gate: {(gate.CanProceed ? "PASSED" : "BLOCKED")}; product release ready: false. Report: {Path.Combine(output, "feasibility.md")}");
            if (tests.Any(t => !t.Passed)) return 2;
            return gate.CanProceed ? 0 : 3;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { Console.Error.WriteLine("Capability inspection cancelled; no release gate was approved."); return 130; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine($"Capability inspection failed ({ex.GetType().Name}); no release gate was approved. Check source/output paths and permissions."); return 2; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static (string SourceRoot, string Output, string? NativeReport) Parse(string[] args)
    {
        string? root = null, output = null, native = null;
        if (args.Length % 2 != 0) throw new ArgumentException("Options require values.");
        for (int i = 0; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--source-root" when root == null: root = args[i + 1]; break;
                case "--output" when output == null: output = args[i + 1]; break;
                case "--native-report" when native == null: native = args[i + 1]; break;
                default: throw new ArgumentException("Unknown or repeated option.");
            }
        }
        return (root ?? throw new ArgumentException("--source-root is required."),
            output ?? throw new ArgumentException("--output is required."), native);
    }
}
