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
/// </summary>
internal static class TestCategory
{
    public const string Name = "Category", CrossProcess = "CrossProcess";
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

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public) ?? throw new InvalidOperationException(type.Name + "." + name + " no longer exists.");

    private static bool HasCrossProcessCategory(MemberInfo member) => member.GetCustomAttributesData().Any(attribute =>
        attribute.AttributeType == typeof(TraitAttribute) && attribute.ConstructorArguments.Count == 2
        && attribute.ConstructorArguments[0].Value as string == TestCategory.Name
        && attribute.ConstructorArguments[1].Value as string == TestCategory.CrossProcess);

    private static string Describe(MemberInfo member) => member is Type type ? type.Name : member.DeclaringType!.Name + "." + member.Name;
}
