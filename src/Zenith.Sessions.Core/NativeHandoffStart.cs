namespace Zenith.Sessions.Core;

/// <summary>
/// One prepared, held executable for the final synchronous handoff boundary. The value is an
/// ephemeral official-page handoff, never a reusable credential. Implementations must not log,
/// persist or put it in an environment variable. A successful start is not account/arrival proof.
/// Only the handoff coordinator may call this after its current-grant and admission checks.
/// </summary>
public interface IPreparedNativeHandoffStart : IDisposable
{
    NativeHandoffDispatchResult Start(string officialHandoff);
}
