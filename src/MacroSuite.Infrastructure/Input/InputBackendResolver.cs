using MacroSuite.Core.Abstractions;

namespace MacroSuite.Infrastructure.Input;

[Flags]
public enum InputBackendCapabilities
{
    None = 0,
    Keyboard = 1 << 0,
    MouseButtons = 1 << 1,
    AbsolutePointer = 1 << 2,
    RelativePointer = 1 << 3,
    Wheel = 1 << 4,
    TimedPlayback = 1 << 5,
    EmergencyRelease = 1 << 6,
    All = Keyboard
        | MouseButtons
        | AbsolutePointer
        | RelativePointer
        | Wheel
        | TimedPlayback
        | EmergencyRelease
}

public readonly record struct InputBackendHealth(bool IsHealthy, string Detail)
{
    public static InputBackendHealth Healthy(string detail = "Backend is available.")
        => new(true, detail);

    public static InputBackendHealth Unhealthy(string detail)
        => new(false, detail);
}

public interface IInputBackend
{
    string Id { get; }

    InputBackendCapabilities Capabilities { get; }

    InputBackendHealth CheckHealth();

    IInputPlayer CreatePlayer();
}

public sealed record InputBackendDiagnostic(
    string Code,
    string BackendId,
    string Message,
    Exception? Exception = null);

/// <summary>
/// Owns the selected player and reports how the backend was resolved.
/// </summary>
public sealed class InputBackendResolution : IDisposable
{
    private IInputPlayer? _player;

    public string BackendId { get; }
    public InputBackendCapabilities Capabilities { get; }
    public InputBackendHealth Health { get; }
    public bool UsedFallback { get; }
    public IReadOnlyList<InputBackendDiagnostic> Diagnostics { get; }

    public IInputPlayer Player
        => _player ?? throw new ObjectDisposedException(nameof(InputBackendResolution));

    internal InputBackendResolution(
        IInputPlayer player,
        IInputBackend backend,
        InputBackendHealth health,
        bool usedFallback,
        IReadOnlyList<InputBackendDiagnostic> diagnostics)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        BackendId = backend.Id;
        Capabilities = backend.Capabilities;
        Health = health;
        UsedFallback = usedFallback;
        Diagnostics = diagnostics;
    }

    public void Dispose()
        => Interlocked.Exchange(ref _player, null)?.Dispose();
}

/// <summary>
/// Selects an infrastructure input backend while keeping the macro engine on
/// <see cref="IInputPlayer"/>. Unavailable optional backends safely fall back to Win32.
/// </summary>
public sealed class InputBackendResolver
{
    public static readonly InputBackendCapabilities DefaultRequiredCapabilities =
        InputBackendCapabilities.All;

    private readonly Dictionary<string, IInputBackend> _optionalBackends;
    private readonly IInputBackend _win32Fallback;

    public InputBackendResolver(
        IEnumerable<IInputBackend>? optionalBackends = null,
        IInputBackend? win32Fallback = null)
    {
        _win32Fallback = win32Fallback ?? new Win32InputBackend();
        if (string.IsNullOrWhiteSpace(_win32Fallback.Id))
            throw new ArgumentException("The Win32 fallback backend must have a non-empty identifier.", nameof(win32Fallback));

        _optionalBackends = new Dictionary<string, IInputBackend>(StringComparer.OrdinalIgnoreCase);
        if (optionalBackends is not null)
        {
            foreach (IInputBackend backend in optionalBackends)
            {
                ArgumentNullException.ThrowIfNull(backend);
                if (string.IsNullOrWhiteSpace(backend.Id))
                    throw new ArgumentException("Input backends must have non-empty identifiers.", nameof(optionalBackends));
                if (!_optionalBackends.TryAdd(backend.Id, backend))
                    throw new ArgumentException($"Duplicate input backend identifier '{backend.Id}'.", nameof(optionalBackends));
            }
        }

        _optionalBackends.TryAdd(DeferredVirtualHidBackend.BackendId, new DeferredVirtualHidBackend());
    }

    public InputBackendResolution Resolve(
        string? preferredBackendId = null,
        InputBackendCapabilities requiredCapabilities = InputBackendCapabilities.All)
    {
        var diagnostics = new List<InputBackendDiagnostic>(4);

        if (!string.IsNullOrWhiteSpace(preferredBackendId)
            && !preferredBackendId.Equals(_win32Fallback.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (_optionalBackends.TryGetValue(preferredBackendId, out IInputBackend? preferred))
            {
                if (TryCreate(
                    preferred,
                    requiredCapabilities,
                    usedFallback: false,
                    diagnostics,
                    out InputBackendResolution? preferredResolution))
                {
                    return preferredResolution!;
                }
            }
            else
            {
                diagnostics.Add(new InputBackendDiagnostic(
                    "PreferredBackendNotFound",
                    preferredBackendId,
                    $"Preferred input backend '{preferredBackendId}' is not registered."));
            }
        }

        if (TryCreate(
            _win32Fallback,
            requiredCapabilities,
            usedFallback: !string.IsNullOrWhiteSpace(preferredBackendId)
                && !preferredBackendId.Equals(_win32Fallback.Id, StringComparison.OrdinalIgnoreCase),
            diagnostics,
            out InputBackendResolution? fallbackResolution))
        {
            return fallbackResolution!;
        }

        throw new InvalidOperationException(
            $"No healthy input backend provides required capabilities '{requiredCapabilities}'. "
            + string.Join(" ", diagnostics.Select(d => d.Message)));
    }

    private static bool TryCreate(
        IInputBackend backend,
        InputBackendCapabilities requiredCapabilities,
        bool usedFallback,
        List<InputBackendDiagnostic> diagnostics,
        out InputBackendResolution? resolution)
    {
        resolution = null;
        InputBackendHealth health;
        try
        {
            health = backend.CheckHealth();
        }
        catch (Exception ex)
        {
            diagnostics.Add(new InputBackendDiagnostic(
                "HealthCheckFailed",
                backend.Id,
                $"Input backend '{backend.Id}' health check failed: {ex.Message}",
                ex));
            return false;
        }

        if (!health.IsHealthy)
        {
            diagnostics.Add(new InputBackendDiagnostic(
                "BackendUnhealthy",
                backend.Id,
                $"Input backend '{backend.Id}' is unavailable: {health.Detail}"));
            return false;
        }

        InputBackendCapabilities missing = requiredCapabilities & ~backend.Capabilities;
        if (missing != InputBackendCapabilities.None)
        {
            diagnostics.Add(new InputBackendDiagnostic(
                "MissingCapabilities",
                backend.Id,
                $"Input backend '{backend.Id}' is missing required capabilities '{missing}'."));
            return false;
        }

        try
        {
            IInputPlayer player = backend.CreatePlayer()
                ?? throw new InvalidOperationException($"Input backend '{backend.Id}' returned no player.");
            diagnostics.Add(new InputBackendDiagnostic(
                usedFallback ? "FallbackSelected" : "BackendSelected",
                backend.Id,
                usedFallback
                    ? $"Fell back to input backend '{backend.Id}'."
                    : $"Selected input backend '{backend.Id}'."));
            resolution = new InputBackendResolution(
                player,
                backend,
                health,
                usedFallback,
                diagnostics.AsReadOnly());
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(new InputBackendDiagnostic(
                "BackendCreationFailed",
                backend.Id,
                $"Input backend '{backend.Id}' could not create a player: {ex.Message}",
                ex));
            return false;
        }
    }
}

/// <summary>
/// Factory and health descriptor for the existing Win32 input implementation.
/// </summary>
public sealed class Win32InputBackend : IInputBackend
{
    private readonly IWindowCoordinateService? _coordinateService;
    private readonly IForegroundInputGuard? _foregroundGuard;

    public string Id => "win32";

    public InputBackendCapabilities Capabilities => InputBackendCapabilities.All;

    public Win32InputBackend(
        IWindowCoordinateService? coordinateService = null,
        IForegroundInputGuard? foregroundGuard = null)
    {
        _coordinateService = coordinateService;
        _foregroundGuard = foregroundGuard;
    }

    public InputBackendHealth CheckHealth()
        => OperatingSystem.IsWindows()
            ? InputBackendHealth.Healthy("Win32 SendInput pipeline is available.")
            : InputBackendHealth.Unhealthy("Win32 input simulation requires Windows.");

    public IInputPlayer CreatePlayer()
    {
        InputBackendHealth health = CheckHealth();
        if (!health.IsHealthy)
            throw new PlatformNotSupportedException(health.Detail);
        return new Win32InputPlayer(_coordinateService, _foregroundGuard);
    }
}

/// <summary>
/// Reserved optional backend for a future signed virtual-HID / kernel adapter.
/// Always unhealthy until that driver exists (Rule 16, Rule 19).
/// </summary>
public sealed class DeferredVirtualHidBackend : IInputBackend
{
    public const string BackendId = "virtual-hid";

    public string Id => BackendId;

    public InputBackendCapabilities Capabilities => InputBackendCapabilities.All;

    public InputBackendHealth CheckHealth()
        => InputBackendHealth.Unhealthy(
            "Virtual HID is reserved for a future signed kernel/virtual-HID adapter and is not implemented. Win32 SendInput remains the active pipeline.");

    public IInputPlayer CreatePlayer()
        => throw new InvalidOperationException(CheckHealth().Detail);
}
