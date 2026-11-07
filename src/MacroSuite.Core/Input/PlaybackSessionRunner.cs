using System.Diagnostics;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

/// <summary>Shared per-emission scheduling for the existing first-party native players.</summary>
public static class PlaybackSessionRunner
{
    public static async Task RunAsync(IReadOnlyList<InputEvent> events, PlaybackOptions options,
        PlaybackSessionContext session, IPlaybackSessionSink sink, CancellationToken ct)
    {
        session.BindSink(sink);
        await session.ConfigureInputOptionsAsync(options, ct).ConfigureAwait(false);
        for (int repeat = 0; repeat < options.RepeatCount; repeat++)
        {
            await session.WaitUntilReadyAsync(ct).ConfigureAwait(false);
            long origin = session.ActiveTimestamp, first = events.Count > 0 ? events[0].TimestampTicks : 0;
            for (int i = 0; i < events.Count; i++)
            {
                double offset = (events[i].TimestampTicks - first) / options.SpeedMultiplier;
                if (!double.IsFinite(offset) || offset < 0 || offset > Stopwatch.Frequency * 86400d)
                    throw new InvalidDataException("Recorded input contains an invalid timestamp.");
                await session.DelayUntilAsync(origin, (long)offset, ct).ConfigureAwait(false);
                await session.EmitAsync(events[i], ct).ConfigureAwait(false);
            }
        }
    }
}
