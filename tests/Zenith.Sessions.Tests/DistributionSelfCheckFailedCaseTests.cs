using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

// The failed stage's case record: static type names, code and method only, all describing one located failure.
public sealed class DistributionSelfCheckFailedCaseTests
{
    private const string PrivateDetail="C:\\Users\\someone\\private-fixture-path";

    [Fact]
    public void APlainFailureIsLocatedAtItsThrowSiteAndHasNoCause()
    {
        var failure=Capture(ThrowDamagedData);
        var failed=DistributionSelfCheck.FailedCase("Stage",failure,"Step");
        Assert.Equal(new DistributionSelfCheck.Case("Stage",false,nameof(InvalidDataException),$"0x{failure.HResult:X8}","Step",
            $"{typeof(DistributionSelfCheckFailedCaseTests).FullName}.{nameof(ThrowDamagedData)}"),failed);
    }

    [Fact]
    public void AWrapperStillPointsAtTheFailureItWrapsAndNamesItAsTheCause()
    {
        // A reflection, XAML or type-initializer wrapper is thrown by framework code: the wrapped failure says where it stopped.
        var inner=Capture(ThrowDamagedData);
        var failed=DistributionSelfCheck.FailedCase("Stage",new TargetInvocationException(inner),null);
        Assert.Equal(nameof(TargetInvocationException),failed.ErrorType);
        Assert.Equal($"0x{inner.HResult:X8}",failed.ErrorCode);
        Assert.Equal($"{typeof(DistributionSelfCheckFailedCaseTests).FullName}.{nameof(ThrowDamagedData)}",failed.FailingMethod);
        Assert.Equal(nameof(InvalidDataException),failed.CauseType);
        Assert.DoesNotContain("private-fixture-path",JsonSerializer.Serialize(failed)); // Rule 10: no message reaches the report
    }

    private static Exception Capture(Action action)
    {
        try { action(); }
        catch(Exception ex) { return ex; }
        throw new InvalidOperationException("The fixture did not throw.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDamagedData()=>throw new InvalidDataException(PrivateDetail);
}
