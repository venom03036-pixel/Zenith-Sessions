using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Explicit optional WGC window previews, one D3D device and one readback owner. No desktop fallback,
/// input, client modification or disk output. Native compositor cadence can exceed the output cadence.
/// Original-size GPU-to-CPU SoftwareBitmap readback still occurs before bounded output sampling.
/// </summary>
public sealed class WindowsClientPreviewProvider : IClientPreviewBatchProvider
{
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly ClientPreviewSourceCache<Source> _sources = new();
    private readonly Dictionary<ClientPreviewTarget, long> _delivered = [];
    private IDirect3DDevice? _device;
    private nint _devicePointer, _contextPointer;
    private long _epoch, _generation, _lastBatch;
    private int _disposing;
    private Task? _dispose;
    private bool _deviceFailed;

    public async Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets); ct.ThrowIfCancellationRequested();
        if (targets.Count > ClientPreviewLimits.MaximumTargets || generation <= 0) throw new ArgumentException("The preview batch exceeds its target or generation limits.");
        var frozen = targets.ToArray(); long epoch = Volatile.Read(ref _epoch);
        if (frozen.Length > ClientPreviewLimits.MaximumTargets || frozen.Any(target => target is null)) throw new ArgumentException("The preview batch contains an invalid target.");
        if (Volatile.Read(ref _disposing) != 0) return Results(frozen, ClientPreviewStatus.Cancelled);
        if (!await _lane.WaitAsync(0, ct).ConfigureAwait(false)) return Results(frozen, ClientPreviewStatus.Deferred);
        try
        {
            // All WinRT/device, wait, conversion and sampling work stays in this one awaited owner off the UI thread.
            return await Task.Run(() => CaptureOwnedAsync(frozen, generation, epoch, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lane.Release(); }
    }

    /// <summary>
    /// Overview batches share the existing device/readback owner. Only attempted prefix rows are
    /// returned; the coordinator schedules the remaining roster. Retaining four native sources
    /// does not make original-size readback cheaper or promise a per-window frame rate.
    /// </summary>
    public async Task<ClientPreviewBatchResult> CaptureBatchAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets); ct.ThrowIfCancellationRequested();
        if (targets.Count > ClientPreviewLimits.MaximumTargets || generation <= 0)
            throw new ArgumentException("The preview batch exceeds its target or generation limits.");
        var frozen = targets.ToArray(); long epoch = Volatile.Read(ref _epoch);
        if (frozen.Length > ClientPreviewLimits.MaximumTargets || frozen.Any(target => target is null))
            throw new ArgumentException("The preview batch contains an invalid target.");
        if (Volatile.Read(ref _disposing) != 0) return EmptyBatch();
        if (!await _lane.WaitAsync(0, ct).ConfigureAwait(false)) return EmptyBatch();
        try
        {
            return await Task.Run(() => CaptureBatchOwnedAsync(frozen, generation, epoch, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lane.Release(); }
    }

    private async Task<ClientPreviewBatchResult> CaptureBatchOwnedAsync(ClientPreviewTarget[] targets, long generation, long epoch, CancellationToken ct)
    {
        if (Stopped(epoch, ct) || generation < _generation) return EmptyBatch();
        _generation = generation; _sources.RequireUsable();
        var unique = new HashSet<ClientPreviewTarget>(); var windows = new HashSet<nint>();
        foreach (var target in targets)
            if (!unique.Add(target) || !windows.Add(target.Window))
                return new(targets.Length, Results(targets, ClientPreviewStatus.InvalidClient));
        if (targets.Length == 0) return EmptyBatch();
        long now = Stopwatch.GetTimestamp();
        if (_lastBatch != 0 && Stopwatch.GetElapsedTime(_lastBatch, now) < ClientPreviewLimits.MinimumBatchInterval)
            return EmptyBatch();
        _lastBatch = now;
        // No per-window task/timer: each native call is joined, and the soft budget is checked
        // only between targets. The native operation may itself take longer than this budget.
        return await RunOverviewBatchAsync(targets,
            target => CaptureOneAsync(target, generation, epoch, ct, evictForOverview: true),
            TimeProvider.System, ct).ConfigureAwait(false);
    }

    internal static async Task<ClientPreviewBatchResult> RunOverviewBatchAsync(IReadOnlyList<ClientPreviewTarget> targets,
        Func<ClientPreviewTarget, Task<ClientPreviewResult>> capture, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(clock);
        if (targets.Count > ClientPreviewLimits.MaximumTargets) throw new ArgumentException("The preview batch exceeds its target limits.");
        var results = new List<ClientPreviewResult>(targets.Count); long started = clock.GetTimestamp();
        for (int index = 0; index < targets.Count; index++)
        {
            if (ct.IsCancellationRequested || index > 0 && clock.GetElapsedTime(started) >= ClientPreviewLimits.MinimumBatchInterval) break;
            results.Add(await capture(targets[index]).ConfigureAwait(false));
        }
        return new(results.Count, Array.AsReadOnly(results.ToArray()));
    }

    private async Task<IReadOnlyList<ClientPreviewResult>> CaptureOwnedAsync(ClientPreviewTarget[] targets, long generation, long epoch, CancellationToken ct)
    {
        if (Stopped(epoch, ct) || generation < _generation) return Results(targets, ClientPreviewStatus.Cancelled);
        _generation = generation; _sources.RequireUsable();
        var unique = new HashSet<ClientPreviewTarget>(); var windows = new HashSet<nint>();
        foreach (var target in targets)
            if (target is null || !unique.Add(target) || !windows.Add(target.Window)) return Results(targets, ClientPreviewStatus.InvalidClient);
        foreach (var previous in _sources.Keys) if (!unique.Contains(previous)) Drop(previous);
        foreach (var previous in _delivered.Keys.ToArray()) if (!unique.Contains(previous)) _delivered.Remove(previous);
        if (targets.Length == 0) { ReleaseOwned(); return Array.AsReadOnly(Array.Empty<ClientPreviewResult>()); }
        long now = Stopwatch.GetTimestamp();
        if (_lastBatch != 0 && Stopwatch.GetElapsedTime(_lastBatch, now) < ClientPreviewLimits.MinimumBatchInterval)
            return Results(targets, ClientPreviewStatus.Deferred);
        _lastBatch = now;
        var results = new ClientPreviewResult[targets.Length];
        for (int index = 0; index < targets.Length; index++)
            results[index] = Stopped(epoch, ct) ? Result(targets[index], ClientPreviewStatus.Cancelled)
                : await CaptureOneAsync(targets[index], generation, epoch, ct).ConfigureAwait(false);
        return Array.AsReadOnly(results);
    }
    private async Task<ClientPreviewResult> CaptureOneAsync(ClientPreviewTarget target, long generation, long epoch, CancellationToken ct, bool evictForOverview = false)
    {
        bool drop = false;
        try
        {
            if (Stopped(epoch, ct)) return Result(target, ClientPreviewStatus.Cancelled);
            var state = CheckTarget(target);
            if (state != ClientPreviewStatus.Ready) { Drop(target); return Result(target, state); }
            if (_deviceFailed) return Result(target, ClientPreviewStatus.Unsupported);
            if (!_sources.TryGet(target, out var source))
            {
                if (Stopped(epoch, ct)) return Result(target, ClientPreviewStatus.Cancelled);
                if (!GraphicsCaptureSession.IsSupported()) { _deviceFailed = true; return Result(target, ClientPreviewStatus.Unsupported); }
                GraphicsCaptureItem? item = null;
                try
                {
                    item = CreateItem(target.Window); var nativeSize = item.Size;
                    var size = new PreviewSurfaceSize(nativeSize.Width, nativeSize.Height);
                    bool fits = evictForOverview ? _sources.MakeRoom(size, ct) : _sources.Fits(size);
                    if (!fits)
                        return Result(target, ClientPreviewStatus.Deferred);
                    if (Stopped(epoch, ct)) return Result(target, ClientPreviewStatus.Cancelled);
                    source = new Source(item, size); _sources.Add(target, size, source); item = null;
                    try { EnsureDevice(); source.Start(_device!); }
                    catch { _deviceFailed = _device is null; throw; }
                }
                finally { if (item is not null) ReleaseInspectable(item); }
            }
            if (source.Closed) { drop = true; return Result(target, ClientPreviewStatus.InvalidClient); }
            using var frame = await source.LatestAsync(ct).ConfigureAwait(false);
            if (frame is null) return Result(target, ClientPreviewStatus.NoFrame);
            if (frame.ContentSize.Width != source.Size.Width || frame.ContentSize.Height != source.Size.Height)
            {
                // The borrowed frame is disposed before the source. Recreate on a later request after re-budgeting.
                drop = true; return Result(target, ClientPreviewStatus.NoFrame);
            }
            long timestamp = frame.SystemRelativeTime.Ticks;
            long previous = _delivered.GetValueOrDefault(target);
            if (!ClientPreviewBufferReader.TryTimestamp(timestamp, previous, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                DateTimeOffset.UtcNow, out _)) return Result(target, ClientPreviewStatus.NoFrame);
            if (Stopped(epoch, ct)) return Result(target, ClientPreviewStatus.Cancelled);
            state = CheckTarget(target);
            if (state != ClientPreviewStatus.Ready) { drop = true; return Result(target, state); }
            // No cancellable AsTask/timeout race: the borrowed frame and device remain alive until the
            // actual native operation reaches its terminal state. Cancellation here is deliberately soft.
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().ConfigureAwait(false);
            if (Stopped(epoch, ct)) return Result(target, ClientPreviewStatus.Cancelled);
            state = CheckTarget(target);
            if (state != ClientPreviewStatus.Ready) { drop = true; return Result(target, state); }
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.PixelWidth != source.Size.Width || bitmap.PixelHeight != source.Size.Height)
            { drop = true; return Result(target, ClientPreviewStatus.Failed); }
            byte[] pixels;
            using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                if (buffer.GetPlaneCount() != 1) return Result(target, ClientPreviewStatus.Failed);
                var plane = buffer.GetPlaneDescription(0);
                if (plane.Width != source.Size.Width || plane.Height != source.Size.Height) return Result(target, ClientPreviewStatus.Failed);
                nint memory = 0;
                try
                {
                    // Query the reference's native identity, not a CLR RCW/CCW cast. Pointer lifetime is
                    // bounded by reference + locked buffer + bitmap + borrowed frame in this scope.
                    Guid iid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
                    var projected = reference as WinRT.IWinRTObject ?? throw new InvalidOperationException("The bitmap reference has no native projection.");
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(projected.NativeObject.ThisPtr, in iid, out memory));
                    var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBuffer>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(memory), 3 * nint.Size));
                    Marshal.ThrowExceptionForHR(getBuffer(memory, out nint data, out uint capacity));
                    if (capacity > reference.Capacity) throw new InvalidOperationException("The native bitmap capacity is inconsistent.");
                    pixels = ClientPreviewBufferReader.CopyBgra(data, capacity, plane.StartIndex, plane.Stride, source.Size, ct);
                }
                finally { ReleasePointer(ref memory); }
            }
            if (Stopped(epoch, ct)) return Result(target, ClientPreviewStatus.Cancelled);
            state = CheckTarget(target);
            if (state != ClientPreviewStatus.Ready) { drop = true; return Result(target, state); }
            if (source.Closed) { drop = true; return Result(target, ClientPreviewStatus.InvalidClient); }
            if (!ClientPreviewBufferReader.TryTimestamp(timestamp, previous, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                DateTimeOffset.UtcNow, out var captured)) return Result(target, ClientPreviewStatus.NoFrame);
            var output = ClientPreviewBufferReader.OutputSize(source.Size); RememberDelivery(target, timestamp);
            var ready = new ClientPreviewFrame(target, generation, output.Width, output.Height, output.Width * 4, pixels,
                captured, source.Size.Width, source.Size.Height, timestamp);
            return new(target, ClientPreviewStatus.Ready, ready, ClientPreviewMessages.For(ClientPreviewStatus.Ready));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { drop = true; return Result(target, ClientPreviewStatus.Cancelled); }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        { drop = true; return Result(target, error is NotSupportedException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException ? ClientPreviewStatus.Unsupported : ClientPreviewStatus.Failed); }
        finally { if (drop) Drop(target); }
    }
    private bool Stopped(long epoch, CancellationToken ct) => ct.IsCancellationRequested || Volatile.Read(ref _disposing) != 0 || epoch != Volatile.Read(ref _epoch);
    private static ClientPreviewStatus CheckTarget(ClientPreviewTarget target)
    {
        if (!OperatingSystem.IsWindows() || target is null || !target.Process.IsValid || target.Process.HostId is not null || target.Window == 0) return ClientPreviewStatus.InvalidClient;
        var session = new SessionObservation(target.Process.ProcessId, target.Process.ProcessStartUtcTicks, "", target.Window, "", false, false, 0, 0, null, DateTimeOffset.UtcNow);
        if (!NativeClientWindowAdapter.SameClient(session)) return ClientPreviewStatus.InvalidClient;
        if (Native.IsIconic(target.Window)) return ClientPreviewStatus.Minimized;
        return Native.IsWindowVisible(target.Window) ? ClientPreviewStatus.Ready : ClientPreviewStatus.Hidden;
    }
    private void EnsureDevice()
    {
        if (_device is not null) return;
        nint dxgi = 0, inspectable = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Native.D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out _devicePointer, out _, out _contextPointer));
            Guid iid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_devicePointer, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            _device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        catch { ReleasePointer(ref _contextPointer); ReleasePointer(ref _devicePointer); throw; }
        finally { ReleasePointer(ref inspectable); ReleasePointer(ref dxgi); }
    }
    private static GraphicsCaptureItem CreateItem(nint window)
    {
        nint name = 0, factory = 0, item = 0;
        try
        {
            const string type = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(Native.WindowsCreateString(type, type.Length, out name));
            Guid interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(name, ref interop, out factory));
            Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * nint.Size));
            Marshal.ThrowExceptionForHR(create(factory, window, ref iid, out item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item) ?? throw new InvalidOperationException("Windows did not return a capture item.");
        }
        finally { ReleasePointer(ref item); ReleasePointer(ref factory); if (name != 0) Native.WindowsDeleteString(name); }
    }
    private void Drop(ClientPreviewTarget target)
        => _sources.Drop(target);
    private void RememberDelivery(ClientPreviewTarget target, long timestamp)
    {
        if (!_delivered.ContainsKey(target) && _delivered.Count >= ClientPreviewLimits.MaximumOverviewTargets)
        {
            ClientPreviewTarget? oldest = null; long oldestTime = long.MaxValue;
            foreach (var entry in _delivered)
                if (oldest is null || entry.Value < oldestTime) { oldest = entry.Key; oldestTime = entry.Value; }
            if (oldest is not null) _delivered.Remove(oldest);
        }
        // Coordinator retains its own per-target watermark across native cache eviction.
        _delivered[target] = timestamp;
    }
    private void ReleaseOwned()
    {
        bool failed = false;
        try { _sources.ReleaseAll(); } catch (ClientPreviewException) { failed = true; }
        if (_sources.Count == 0)
        {
            try { _device?.Dispose(); _device = null; } catch { failed = true; }
            if (_device is null) { ReleasePointer(ref _contextPointer); ReleasePointer(ref _devicePointer); }
        }
        _delivered.Clear(); _deviceFailed = false; _lastBatch = 0;
        if (failed) throw new ClientPreviewException();
    }
    public async Task ReleaseAsync(CancellationToken ct)
    {
        // Teardown intent is not abandoned when the caller token is cancelled. Wait for actual native work,
        // then finish releasing it; no token can dispose a frame/device while a native copy still uses it.
        Interlocked.Increment(ref _epoch); await _lane.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await Task.Run(ReleaseOwned, CancellationToken.None).ConfigureAwait(false); }
        finally { _lane.Release(); }
    }
    public ValueTask DisposeAsync() => new(_dispose ??= DisposeOwnedAsync());
    private async Task DisposeOwnedAsync()
    { Interlocked.Exchange(ref _disposing, 1); await ReleaseAsync(CancellationToken.None).ConfigureAwait(false); _lane.Dispose(); }
    private static ClientPreviewResult Result(ClientPreviewTarget target, ClientPreviewStatus status) => new(target, status, null, ClientPreviewMessages.For(status));
    private static IReadOnlyList<ClientPreviewResult> Results(ClientPreviewTarget[] targets, ClientPreviewStatus status) => Array.AsReadOnly(targets.Select(target => Result(target, status)).ToArray());
    private static ClientPreviewBatchResult EmptyBatch() => new(0, Array.Empty<ClientPreviewResult>());
    private static void ReleasePointer(ref nint value) { if (value != 0) Marshal.Release(value); value = 0; }
    private static void ReleaseInspectable(object value) { if (value is WinRT.IWinRTObject projected) projected.NativeObject.Dispose(); }

    private sealed class Source(GraphicsCaptureItem item, PreviewSurfaceSize size) : IDisposable
    {
        private GraphicsCaptureItem? _item = item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;
        private readonly ClientPreviewFrameSignal _signal = new();
        private int _stopped, _closed;
        private bool _frameSubscribed, _closedSubscribed, _signalDisposed;
        public PreviewSurfaceSize Size { get; } = size;
        public bool Closed => Volatile.Read(ref _closed) != 0;
        public void Start(IDirect3DDevice device)
        {
            var captureItem = _item ?? throw new InvalidOperationException("The capture item was released.");
            var boundedSize = new global::Windows.Graphics.SizeInt32 { Width = Size.Width, Height = Size.Height };
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, boundedSize);
            _pool.FrameArrived += OnFrame; _frameSubscribed = true;
            captureItem.Closed += OnClosed; _closedSubscribed = true;
            _session = _pool.CreateCaptureSession(captureItem);
            // Preserve the default Windows capture border/indicator. No newer MinUpdateInterval API under19041.
            _session.StartCapture();
        }
        private void OnFrame(Direct3D11CaptureFramePool sender, object args) => Signal();
        private void OnClosed(GraphicsCaptureItem sender, object args) { Volatile.Write(ref _closed, 1); Signal(); }
        private void Signal()
        {
            if (Volatile.Read(ref _stopped) == 0) _signal.Signal();
        }
        public async Task<Direct3D11CaptureFrame?> LatestAsync(CancellationToken ct)
        {
            if (!await _signal.WaitAsync(TimeSpan.FromMilliseconds(120), ct).ConfigureAwait(false)) return null;
            ct.ThrowIfCancellationRequested();
            Direct3D11CaptureFrame? latest = null;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested(); var next = _pool!.TryGetNextFrame(); if (next is null) break;
                    var previous = latest; latest = next; previous?.Dispose();
                }
                var result = latest; latest = null; return result;
            }
            finally { latest?.Dispose(); }
        }
        public void Dispose()
        {
            Volatile.Write(ref _stopped, 1); bool failed = false;
            if (_frameSubscribed) try { if (_pool is not null) _pool.FrameArrived -= OnFrame; _frameSubscribed = false; } catch { failed = true; }
            if (_closedSubscribed) try { _item!.Closed -= OnClosed; _closedSubscribed = false; } catch { failed = true; }
            try { _session?.Dispose(); _session = null; } catch { failed = true; }
            try { _pool?.Dispose(); _pool = null; } catch { failed = true; }
            if (!_frameSubscribed && !_closedSubscribed && _session is null && _pool is null)
                try { if (_item is not null) ReleaseInspectable(_item); _item = null; } catch { failed = true; }
            if (!_signalDisposed) { _signal.Dispose(); _signalDisposed = true; }
            if (failed) throw new ClientPreviewException();
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateForWindow(nint self, nint window, ref Guid iid, out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBuffer(nint self, out nint data, out uint capacity);
    private static class Native
    {
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("d3d11.dll")] internal static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags, nint levels,
            uint count, uint sdk, out nint device, out int level, out nint context);
        [DllImport("d3d11.dll")] internal static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] internal static extern int WindowsCreateString(string value, int length, out nint name);
        [DllImport("combase.dll")] internal static extern int WindowsDeleteString(nint name);
        [DllImport("combase.dll")] internal static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    }
}
