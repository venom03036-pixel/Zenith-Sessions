using System.Reflection;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// xunit trait names for tests whose result depends on the machine rather than on the code under test.
/// CrossProcess: the test starts a repository-built worker executable from tools/&lt;project&gt;/bin (VaultTestWorker
/// or the .NET 8 MacroHost ProbeWorker) and waits for it to answer, so it fails with "exited before readiness" where
/// that worker cannot start (its .NET runtime is missing, the test output was copied away from the repository, or the
/// account cannot start child processes).
/// A checkpoint records two runs: the main run with --filter "Category!=CrossProcess", and a separate run with
/// --filter "Category=CrossProcess" labelled as the environment run. The main run alone is never the full-suite count.
/// NativeCrash: the test makes a real process crash (Environment.FailFast), so Windows writes an error report and an
/// Application Error event on the machine, and the result depends on Windows Error Reporting settings. These tests use
/// [NativeCrashFact] and are skipped unless ZENITH_RUN_NATIVE_CRASH_TESTS=1; run them on purpose with that variable set
/// and --filter "Category=NativeCrash". A checkpoint reports them as skipped, never as passed.
/// </summary>
internal static class TestCategory
{
    public const string Name = "Category", CrossProcess = "CrossProcess", NativeCrash = "NativeCrash";
}

/// <summary>A test that crashes a real process. It runs only when ZENITH_RUN_NATIVE_CRASH_TESTS=1.</summary>
public sealed class NativeCrashFactAttribute : FactAttribute
{
    public const string OptInVariable = "ZENITH_RUN_NATIVE_CRASH_TESTS";

    public NativeCrashFactAttribute() => Skip = SkipReasonFor(Environment.GetEnvironmentVariable(OptInVariable));

    internal static string? SkipReasonFor(string? optIn) => optIn == "1" ? null
        : "Opt-in: this test crashes an inbox Windows PowerShell on purpose, which writes a real Windows error report and an "
        + "Application Error event on this PC. Set " + OptInVariable + "=1 to run it; it needs Application Error logging on and "
        + "no Windows crash dialog.";
}

/// <summary>
/// Pins which tests carry the CrossProcess category so a checkpoint run can leave them out with
/// --filter "Category!=CrossProcess" and run them separately, and so the category never grows quietly
/// to hide ordinary tests from the main run.
/// </summary>
public sealed class TestEnvironmentCategoryTests
{
    // Each of these starts a repository-built worker executable (VaultTestWorker or the .NET 8 MacroHost
    // ProbeWorker) from tools/<project>/bin and waits for it to answer.
    private static readonly MemberInfo[] WorkerProcessTests =
    {
        typeof(VaultCrossProcessTests),
        Method(typeof(MacroHostCatalogViewModelTests), "Net10ClientReadsTheRealPipeFromAnIsolatedNet8Worker"),
        Method(typeof(MacroHostCatalogViewModelTests), "ProbeWorkerThatExitsBeforeReadyReportsItsExitCode"),
    };

    [Fact]
    public void ChildProcessTestsCarryTheCrossProcessCategory()
    {
        string[] missing = WorkerProcessTests.Where(member => !HasCrossProcessCategory(member)).Select(Describe).ToArray();
        Assert.True(missing.Length == 0, "These tests start a worker process but have no [Trait(\"Category\", \"CrossProcess\")], so a run with "
            + "--filter \"Category!=CrossProcess\" still includes them: " + string.Join(", ", missing));
    }

    [Fact]
    public void CrossProcessCategoryCoversOnlyTheWorkerProcessTests()
    {
        var known = WorkerProcessTests.ToHashSet();
        string[] extra = typeof(TestEnvironmentCategoryTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>().Prepend(type))
            .Where(member => HasCrossProcessCategory(member) && !known.Contains(member)).Select(Describe).ToArray();
        Assert.True(extra.Length == 0, "These members carry the CrossProcess category but are not known worker-process tests, so "
            + "--filter \"Category!=CrossProcess\" would leave them out of the main run: " + string.Join(", ", extra));
    }

    // Each of these ends a real process through Environment.FailFast and reads the record Windows writes about it.
    private static readonly MethodInfo[] NativeCrashTests =
    {
        Method(typeof(WindowsClientFaultProbeTests), "FindsWindowsFaultRecordForTheExactCrashedProcessOnly"),
    };

    [Fact]
    public void NativeCrashTestsAreOptInAndCarryTheNativeCrashCategory()
    {
        string[] runByDefault = NativeCrashTests.Where(method => !HasCategory(method, TestCategory.NativeCrash)
            || method.GetCustomAttributesData().Count(attribute => typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType)) != 1
            || !method.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(NativeCrashFactAttribute))).Select(Describe).ToArray();
        Assert.True(runByDefault.Length == 0, "These tests crash a real process, which writes a Windows error report on the machine, but "
            + "they are not [NativeCrashFact] with [Trait(\"Category\", \"NativeCrash\")], so every suite run crashes a process: " + string.Join(", ", runByDefault));
        Assert.NotNull(NativeCrashFactAttribute.SkipReasonFor(null));
        Assert.NotNull(NativeCrashFactAttribute.SkipReasonFor(""));
        Assert.NotNull(NativeCrashFactAttribute.SkipReasonFor("0"));
        Assert.NotNull(NativeCrashFactAttribute.SkipReasonFor("true"));
        Assert.Null(NativeCrashFactAttribute.SkipReasonFor("1"));
    }

    [Fact]
    public void NativeCrashCategoryCoversOnlyTheKnownCrashTests()
    {
        var known = NativeCrashTests.Cast<MemberInfo>().ToHashSet();
        string[] extra = typeof(TestEnvironmentCategoryTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>().Prepend(type))
            .Where(member => (HasCategory(member, TestCategory.NativeCrash)
                || member.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(NativeCrashFactAttribute))) && !known.Contains(member))
            .Select(Describe).ToArray();
        Assert.True(extra.Length == 0, "These members are opt-in native crash tests but are not known crash tests, so the default run "
            + "skips them: " + string.Join(", ", extra));
    }

    // Each real Core Audio test needs a Windows audio session, which no program can have without an active output device.
    [Fact]
    public void RealCoreAudioTestsAreSkippedOnlyWhereThereIsNoActiveOutputDevice()
    {
        MethodInfo[] tests = typeof(ClientAudioMuteNativeTests).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributesData().Any(attribute => typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType))).ToArray();
        Assert.NotEmpty(tests);
        string[] plain = tests.Where(method => method.GetCustomAttributesData().Count(attribute => typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType)) != 1
            || !method.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(AudioOutputFactAttribute))).Select(Describe).ToArray();
        Assert.True(plain.Length == 0, "These tests play sound through a real Windows audio session but are not [AudioOutputFact], so they "
            + "fail instead of skipping on a PC with no active audio output device: " + string.Join(", ", plain));
        Assert.NotNull(AudioOutputFactAttribute.SkipReasonFor(0));
        Assert.Null(AudioOutputFactAttribute.SkipReasonFor(1));
        Assert.Null(AudioOutputFactAttribute.SkipReasonFor(null)); // A Core Audio error runs the test, so it fails with that error.
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public) ?? throw new InvalidOperationException(type.Name + "." + name + " no longer exists.");

    private static bool HasCrossProcessCategory(MemberInfo member) => HasCategory(member, TestCategory.CrossProcess);

    private static bool HasCategory(MemberInfo member, string category) => member.GetCustomAttributesData().Any(attribute =>
        attribute.AttributeType == typeof(TraitAttribute) && attribute.ConstructorArguments.Count == 2
        && attribute.ConstructorArguments[0].Value as string == TestCategory.Name
        && attribute.ConstructorArguments[1].Value as string == category);

    private static string Describe(MemberInfo member) => member is Type type ? type.Name : member.DeclaringType!.Name + "." + member.Name;
}
