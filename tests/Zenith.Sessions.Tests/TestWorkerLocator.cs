using System.IO;
using System.Reflection;

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
}
