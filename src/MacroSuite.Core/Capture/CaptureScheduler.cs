using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Capture;

/// <summary>
/// Centralized frame acquisition and fan-out coordinator.
/// Subscribers register regions and intervals; the scheduler computes bounding-unions
/// and shares a single capture per tick across all consumers (Rule 5, Rule 6).
/// Pauses completely when subscriber count is zero to avoid burning CPU at idle.
/// <para>
/// Production owner of capture is <see cref="SharedCaptureService"/>. This scheduler is a
/// push fan-out only: wrap that shared service so watchers do not start a second DXGI/GDI loop.
/// Macro conditions continue to pull latest frames through <see cref="SharedCaptureService"/>.
/// </para>
/// </summary>
public sealed class CaptureScheduler : IDisposable, IAsyncDisposable
{
    private readonly IScreenCaptureService _captureService;
    private readonly ConcurrentDictionary<Guid, ICaptureSubscriber> _subscribers = new();
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _subscriberSignal = new(0, 1);

    private Task? _loopTask;
    private bool _disposed;
    private volatile bool _isRunning;

    // Minimum interval floor to prevent spinning too fast (1ms ~ up to 1000 FPS max)
    private static readonly TimeSpan s_minIntervalFloor = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan s_defaultIdleSleep = TimeSpan.FromMilliseconds(100);

    /// <summary>Number of currently active subscribers.</summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>True if the background capture loop is currently active and capturing.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Creates a new <see cref="CaptureScheduler"/> wrapping the specified capture service.
    /// </summary>
    public CaptureScheduler(IScreenCaptureService captureService)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
    }

    /// <summary>
    /// Registers a subscriber. If this is the first subscriber, the capture loop starts automatically.
    /// </summary>
    public void Subscribe(ICaptureSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers[subscriber.Id] = subscriber;
            _isRunning = true;
            EnsureLoopStarted();
            SignalSubscriberChange();
        }
    }

    /// <summary>
    /// Unregisters a subscriber. If no subscribers remain, the capture loop pauses completely.
    /// </summary>
    public bool Unsubscribe(Guid subscriberId)
    {
        lock (_stateLock)
        {
            bool removed = _subscribers.TryRemove(subscriberId, out _);
            if (_subscribers.IsEmpty)
                _isRunning = false;
            SignalSubscriberChange();
            return removed;
        }
    }

    private void EnsureLoopStarted()
    {
        if (_disposed || _loopTask is not null) return;
        _loopTask = Task.Run(() => CaptureLoopAsync(_shutdown.Token));
    }

    private void SignalSubscriberChange()
    {
        if (_subscriberSignal.CurrentCount == 0)
            _subscriberSignal.Release();
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var subscribers = _subscribers.Values.ToArray();
            if (subscribers.Length == 0)
            {
                _isRunning = false;
                await _subscriberSignal.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            // 1. Determine shortest interval among active subscribers
            TimeSpan minInterval = TimeSpan.MaxValue;
            foreach (var sub in subscribers)
            {
                if (sub.PollingInterval > TimeSpan.Zero && sub.PollingInterval < minInterval)
                {
                    minInterval = sub.PollingInterval;
                }
            }

            if (minInterval == TimeSpan.MaxValue)
                minInterval = s_defaultIdleSleep;
            if (minInterval < s_minIntervalFloor)
            {
                minInterval = s_minIntervalFloor;
            }

            // 2. Compute bounding union of all subscriber regions
            Rectangle[] regions = new Rectangle[subscribers.Length];
            for (int i = 0; i < subscribers.Length; i++)
            {
                regions[i] = subscribers[i].Region;
            }

            Rectangle boundingUnion = RegionMerger.ComputeBoundingUnion(regions.AsSpan());

            if (boundingUnion.Width > 0 && boundingUnion.Height > 0)
            {
                CaptureFrame? frame = null;
                try
                {
                    frame = await _captureService.CaptureRegionAsync(boundingUnion, ct).ConfigureAwait(false);

                    if (frame != null && !frame.IsDisposed)
                    {
                        CaptureFrameIntegrity.ThrowIfUnpopulated(frame);

                        // 3. Fan out to all subscribers with relative coordinates
                        foreach (var sub in subscribers)
                        {
                            if (ct.IsCancellationRequested) break;

                            Rectangle relativeRect = RegionMerger.ToRelative(sub.Region, boundingUnion);
                            try
                            {
                                sub.OnFrameCaptured(frame, relativeRect);
                            }
                            catch (Exception ex)
                            {
                                // Isolate one watcher without making the failure silent.
                                Trace.TraceError(
                                    $"Capture subscriber '{sub.Id}' failed while handling a frame: {ex}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"Shared frame acquisition failed: {ex}");
                }
                finally
                {
                    // Rule 13: Clear ownership - dispose the frame buffer immediately
                    frame?.Dispose();
                }
            }

            // Wait for next scheduled interval
            try
            {
                // A subscription change wakes the loop early so a newly-added faster
                // subscriber is never delayed by an older, slower interval.
                await _subscriberSignal.WaitAsync(minInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Task? taskToWait;

        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _isRunning = false;
            taskToWait = _loopTask;
            _shutdown.Cancel();
            SignalSubscriberChange();
        }

        WaitForShutdown(taskToWait);
        _subscribers.Clear();
        _subscriberSignal.Dispose();
        _shutdown.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Task? taskToWait;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _isRunning = false;
            taskToWait = _loopTask;
            _shutdown.Cancel();
            SignalSubscriberChange();
        }

        if (taskToWait != null)
        {
            try
            {
                await taskToWait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _subscribers.Clear();
        _subscriberSignal.Dispose();
        _shutdown.Dispose();
    }

    private static void WaitForShutdown(Task? task)
    {
        if (task is null)
            return;
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
