using System.Threading.Channels;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Records low-level OS input events and writes them to a <see cref="ChannelWriter{T}"/>.
/// The hook callback must complete in &lt;1 ms; all heavy processing belongs downstream.
/// </summary>
public interface IInputRecorder : IDisposable
{
    /// <summary>
    /// Starts the recording hooks and begins writing events to <paramref name="writer"/>.
    /// </summary>
    /// <param name="writer">
    /// The channel writer that receives raw <see cref="InputEvent"/> values.
    /// The recorder does not own the channel; the caller manages its lifetime.
    /// </param>
    /// <param name="ct">
    /// Token monitored for cancellation. When cancelled, the recorder stops and
    /// completes the writer.
    /// </param>
    Task StartAsync(ChannelWriter<InputEvent> writer, CancellationToken ct);

    /// <summary>
    /// Stops recording. Equivalent to cancelling the token passed to <see cref="StartAsync"/>.
    /// Safe to call multiple times.
    /// </summary>
    Task StopAsync();

    /// <summary>Gets a value indicating whether the recorder is currently active.</summary>
    bool IsRecording { get; }
}
