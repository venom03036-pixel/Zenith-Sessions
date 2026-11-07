using MacroSuite.SDK.AI;
using MacroSuite.SDK.Contracts;
using MacroSuite.SDK.Steps;

namespace MacroSuite.SDK.Tests;

/// <summary>
/// The SDK is the only assembly third-party plugins reference. It must stay free of the host's engine,
/// infrastructure and UI (layering, Rules 1-3), and its AI types must default to "do nothing" (Rule 8).
/// </summary>
public sealed class SdkSurfaceTests
{
    private static readonly string[] ForbiddenUiAssemblies =
    {
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Windows.Forms",
        "System.Xaml",
    };

    [Fact]
    public void SdkAssembly_ReferencesNoHostEngineInfrastructureOrUiAssembly()
    {
        var sdk = typeof(PluginManifest).Assembly;
        Assert.Equal("MacroSuite.SDK", sdk.GetName().Name);

        var referenced = sdk.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty).ToArray();

        Assert.DoesNotContain(referenced, name => name.StartsWith("MacroSuite.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.StartsWith("Zenith.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => ForbiddenUiAssemblies.Contains(name, StringComparer.Ordinal));
    }

    [Fact]
    public void SdkPublicTypes_AllLiveUnderTheSdkNamespace()
    {
        var outside = typeof(PluginManifest).Assembly.GetExportedTypes()
            .Where(type => type.Namespace is null
                || !(type.Namespace == "MacroSuite.SDK" || type.Namespace.StartsWith("MacroSuite.SDK.", StringComparison.Ordinal)))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(outside);
    }

    [Fact]
    public void NewAIAction_ProposesNothing()
    {
        var action = new AIAction();

        Assert.Equal("none", action.ActionType);
        Assert.Null(action.X);
        Assert.Null(action.Y);
        Assert.Null(action.KeyCode);
        Assert.Null(action.Text);
        Assert.Null(action.Confidence);
        Assert.Equal(string.Empty, action.Reasoning);
    }

    [Fact]
    public void NewAIResponse_CarriesANoneActionAndNoDraft()
    {
        var response = new AIResponse();

        Assert.Equal("none", response.Action.ActionType);
        Assert.Null(response.MacroDraftJson);
        Assert.Equal(0, response.InputTokens);
        Assert.Equal(0, response.OutputTokens);
        Assert.Equal(0m, response.EstimatedCostUsd);
    }

    [Fact]
    public void NewAIRequest_IsALiveRequestWithNoImageAndOwnParameterBag()
    {
        var first = new AIRequest();
        var second = new AIRequest();

        Assert.False(first.IsMacroDraft);
        Assert.Null(first.ImageBytes);
        Assert.Empty(first.Parameters);
        Assert.NotSame(first.Parameters, second.Parameters);
    }

    [Fact]
    public void NewStepDescriptor_IsInTheGeneralCategoryWithNoParameters()
    {
        var descriptor = new StepDescriptor();

        Assert.Equal("General", descriptor.Category);
        Assert.Empty(descriptor.Parameters);
        Assert.Equal(typeof(string), new StepParameterDescriptor().ParameterType);
        Assert.False(new StepParameterDescriptor().IsRequired);
    }
}
