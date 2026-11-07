namespace MacroSuite.Core.Licensing;

/// <summary>
/// Identifies distinct capabilities within the macro automation suite subject to licensing verification (Rule 18).
/// </summary>
public enum FeatureId
{
    /// <summary>Recording low-level keyboard and mouse inputs.</summary>
    BasicMacroRecording,

    /// <summary>Replaying recorded or authored input sequences.</summary>
    BasicPlayback,

    /// <summary>Region- and pixel-based RGB color matching.</summary>
    ColorDetection,

    /// <summary>OpenCV template matching and image recognition.</summary>
    ImageRecognition,

    /// <summary>Multi-modal AI vision integration (GPT-4V, Gemini, Claude).</summary>
    AIVision,

    /// <summary>Autonomous operation mode with persistent safety controls.</summary>
    AutonomousMode,

    /// <summary>Composite logical conditions (AND, OR, NOT) and wait loops.</summary>
    AdvancedConditions,

    /// <summary>Loading and executing third-party plugins via MacroSuite.SDK.</summary>
    PluginExecution,

    /// <summary>Structured asynchronous audit logging.</summary>
    AuditLog,

    /// <summary>Usage telemetry and spend cap monitoring.</summary>
    UsageTracking,
}
