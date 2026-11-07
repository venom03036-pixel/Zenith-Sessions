using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Security;
using MacroSuite.Infrastructure.Storage;

namespace MacroSuite.App.Services;

/// <summary>
/// Determines whether a downloaded macro needs user approval before it can run,
/// and records that approval after the user accepts.
/// </summary>
/// <remarks>
/// <para>
/// A macro requires approval when:
/// <list type="bullet">
///   <item>It has never been approved before, or</item>
///   <item>Its file content has changed since the last approval (hash mismatch).</item>
/// </list>
/// </para>
/// <para>
/// This service does <em>not</em> show any UI. It raises <see cref="ApprovalRequired"/>
/// which the presentation layer handles. This keeps the service testable without a real screen
/// (Rule 1, Rule 18).
/// </para>
/// </remarks>
public sealed class MacroApprovalService
{
    private readonly MacroValidationCache _cache;

    /// <summary>
    /// Raised when a macro requires user approval before execution.
    /// The handler is responsible for showing the permission dialog and calling
    /// <see cref="RecordApprovalAsync"/> if the user accepts.
    /// </summary>
    public event EventHandler<MacroApprovalRequiredEventArgs>? ApprovalRequired;

    public MacroApprovalService(MacroValidationCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>
    /// Checks whether <paramref name="model"/> needs approval.
    /// If so, raises <see cref="ApprovalRequired"/> and returns <c>false</c> until approved.
    /// If already approved (and hash unchanged), returns <c>true</c> immediately.
    /// </summary>
    /// <param name="model">The macro about to be executed.</param>
    /// <param name="filePath">Absolute path of the macro file (used for hash computation).</param>
    /// <param name="validationResult">The validation result from the load pipeline.</param>
    public bool CheckApproval(
        MacroModel model,
        string filePath,
        MacroValidationResult validationResult)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(validationResult);

        uint fileHash = MacroLoadService.ComputeFileHash(filePath);

        if (_cache.IsApproved(model.Id.ToString(), fileHash))
        {
            return true;
        }

        // Need approval — collect permission categories from the model
        var permissions = BuildPermissionList(model, validationResult);
        var args = new MacroApprovalRequiredEventArgs(model, fileHash, permissions, validationResult);
        ApprovalRequired?.Invoke(this, args);

        // The dialog sets args.Approved; if not handled, approval is denied by default
        return args.Approved;
    }

    /// <summary>Persists the user's approval decision for the given macro and hash.</summary>
    public Task RecordApprovalAsync(string macroId, uint fileHash, CancellationToken ct = default)
        => _cache.RecordApprovalAsync(macroId, fileHash, ct);

    // ── Private helpers ────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildPermissionList(
        MacroModel model, MacroValidationResult validationResult)
    {
        var perms = new List<string>();

        // Inspect steps for capability flags
        bool hasClick = false, hasKey = false, hasAI = false, hasLoop = false,
             hasWait = false, hasPlugin = false;

        foreach (var step in model.Steps)
        {
            switch (step.GetType().Name)
            {
                case "ClickStep":    hasClick  = true; break;
                case "KeyPressStep": hasKey    = true; break;
                case "AIStep":       hasAI     = true; break;
                case "LoopStep":     hasLoop   = true; break;
                case "WaitUntilStep":hasWait   = true; break;
                case "PluginStep":   hasPlugin = true; break;
            }
        }

        if (hasClick)  perms.Add("🖱  Sends simulated mouse clicks");
        if (hasKey)    perms.Add("⌨  Sends simulated keystrokes");
        if (hasAI)     perms.Add("🤖  Uses AI Vision to analyze your screen");
        if (hasLoop)   perms.Add("🔁  Contains loops (may repeat indefinitely)");
        if (hasWait)   perms.Add("⏳  Waits on screen conditions before continuing");
        if (hasPlugin) perms.Add("🔌  Loads third-party plugin code");

        if (validationResult.Violations.Count > 0)
        {
            perms.Add($"⚠  {validationResult.Violations.Count} security warning(s) detected");
        }

        if (perms.Count == 0)
        {
            perms.Add("ℹ  This macro contains only delay steps and is read-only");
        }

        return perms;
    }
}

/// <summary>
/// Event arguments for <see cref="MacroApprovalService.ApprovalRequired"/>.
/// </summary>
public sealed class MacroApprovalRequiredEventArgs : EventArgs
{
    /// <summary>The macro awaiting approval.</summary>
    public MacroModel Model { get; }

    /// <summary>CRC32 of the macro file bytes at the time of the check.</summary>
    public uint FileHash { get; }

    /// <summary>Human-readable list of permissions/capabilities this macro uses.</summary>
    public IReadOnlyList<string> Permissions { get; }

    /// <summary>The security validation result for this macro.</summary>
    public MacroValidationResult ValidationResult { get; }

    /// <summary>
    /// Set to <c>true</c> by the event handler if the user approves the macro.
    /// Defaults to <c>false</c> (deny).
    /// </summary>
    public bool Approved { get; set; }

    public MacroApprovalRequiredEventArgs(
        MacroModel model,
        uint fileHash,
        IReadOnlyList<string> permissions,
        MacroValidationResult validationResult)
    {
        Model = model;
        FileHash = fileHash;
        Permissions = permissions;
        ValidationResult = validationResult;
    }
}
