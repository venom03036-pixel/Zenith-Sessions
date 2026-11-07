namespace MacroSuite.Core.Licensing;

/// <summary>
/// Explicit entitlement maps. Feature grants must come from these lists, never
/// from <see cref="Enum.GetValues{TEnum}"/> on an untrusted key (safety program R1/R4).
/// </summary>
public static class FeatureCatalog
{
    public static readonly FeatureId[] Free =
    [
        FeatureId.BasicMacroRecording,
        FeatureId.BasicPlayback
    ];

    public static readonly FeatureId[] Pro =
    [
        FeatureId.BasicMacroRecording,
        FeatureId.BasicPlayback,
        FeatureId.ColorDetection,
        FeatureId.ImageRecognition,
        FeatureId.AdvancedConditions,
        FeatureId.UsageTracking
    ];

    public static readonly FeatureId[] Enterprise =
    [
        FeatureId.BasicMacroRecording,
        FeatureId.BasicPlayback,
        FeatureId.ColorDetection,
        FeatureId.ImageRecognition,
        FeatureId.AIVision,
        FeatureId.AutonomousMode,
        FeatureId.AdvancedConditions,
        FeatureId.PluginExecution,
        FeatureId.AuditLog,
        FeatureId.UsageTracking
    ];

    public static HashSet<FeatureId> ToSet(IReadOnlyList<FeatureId> features)
        => new(features);
}
