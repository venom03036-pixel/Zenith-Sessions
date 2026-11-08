using System.Runtime.InteropServices;

namespace Zenith.Sessions.Windows;

/// <summary>
/// GPU-side preview reduction on the provider's own private D3D11 device and immediate context (never a client's).
/// A borrowed WGC frame is copied into a cached mip-enabled texture, GenerateMips box-filters it, and only the
/// smallest level that still covers the 320x180 output is copied into a small staging texture and mapped. The CPU
/// then samples that small level into the output. When the driver cannot generate BGRA mips, level 0 is read the
/// same way. No SoftwareBitmap and no source-size managed array is created.
/// Ownership: the provider's single readback lane serializes every call and disposes this before releasing the
/// device/context pointers it borrows. Cached textures (keyed by source size, least recently used evicted first)
/// stay within ClientPreviewBufferReader.MaximumReadbackBytes, the shared surface budget's readback allowance.
/// D3D11 is called through its documented COM vtable slots (d3d11.h); no extra package is needed (Rule 19).
/// </summary>
internal sealed class ClientPreviewGpuReadback : IDisposable
{
    private const uint Bgra8 = 87, BindShaderResource = 0x8, BindRenderTarget = 0x20, MiscGenerateMips = 0x1;
    private const uint UsageStaging = 3, CpuAccessRead = 0x20000, MapRead = 1;
    private const uint SupportMipAutogen = 0x2000, SupportRenderTarget = 0x4000;
    private readonly nint _device, _context;
    private readonly CreateTexture2D _createTexture;
    private readonly CreateShaderResourceView _createView;
    private readonly CopySubresourceRegion _copy;
    private readonly GenerateMipsCall _generateMips;
    private readonly MapCall _map;
    private readonly UnmapCall _unmap;
    private readonly Dictionary<PreviewSurfaceSize, Entry> _entries = [];
    private long _cachedBytes, _uses;
    private bool _disposed;

    internal ClientPreviewGpuReadback(nint device, nint context)
    {
        if (device == 0 || context == 0) throw new ArgumentException("A live preview device and context are required.");
        _device = device; _context = context;
        _createTexture = Slot<CreateTexture2D>(device, 5); _createView = Slot<CreateShaderResourceView>(device, 7);
        _map = Slot<MapCall>(context, 14); _unmap = Slot<UnmapCall>(context, 15);
        _copy = Slot<CopySubresourceRegion>(context, 46); _generateMips = Slot<GenerateMipsCall>(context, 54);
        // A capability query, not a failure: without automatic BGRA mips the level-0 path is used.
        const uint required = SupportMipAutogen | SupportRenderTarget;
        MipsSupported = Slot<CheckFormatSupport>(device, 29)(device, Bgra8, out uint support) >= 0 && (support & required) == required;
    }

    internal bool MipsSupported { get; }

    internal long CachedBytes => _cachedBytes;

    /// <summary>
    /// Reduces one borrowed frame texture of at least <paramref name="source"/> size and returns owned output pixels of
    /// ClientPreviewBufferReader.OutputSize(source). <paramref name="read"/> is the level that was copied to the CPU.
    /// The map waits for this device's own copy/mip work only; the caller keeps the frame alive until it returns.
    /// </summary>
    internal byte[] Read(nint frameTexture, PreviewSurfaceSize source, CancellationToken ct, out PreviewSurfaceSize read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (frameTexture == 0 || !source.Valid) throw new ArgumentException("A valid preview frame texture is required.");
        Slot<GetDescription>(frameTexture, 10)(frameTexture, out var frame);
        if (frame.Format != Bgra8 || frame.SampleCount != 1 || frame.ArraySize != 1 || frame.Width < source.Width || frame.Height < source.Height)
            throw new InvalidOperationException("The preview frame is not a single-sample BGRA surface of the expected size.");
        int level = MipsSupported ? ClientPreviewBufferReader.ReadbackLevel(source) : 0;
        read = ClientPreviewBufferReader.LevelSize(source, level);
        var entry = Ensure(source, level, read);
        var whole = new Box { Right = (uint)source.Width, Bottom = (uint)source.Height, Back = 1 };
        if (level == 0) _copy(_context, entry.Staging, 0, 0, 0, 0, frameTexture, 0, in whole);
        else
        {
            var reduced = new Box { Right = (uint)read.Width, Bottom = (uint)read.Height, Back = 1 };
            _copy(_context, entry.Mips, 0, 0, 0, 0, frameTexture, 0, in whole);
            _generateMips(_context, entry.View);
            _copy(_context, entry.Staging, 0, 0, 0, 0, entry.Mips, (uint)level, in reduced);
        }
        ct.ThrowIfCancellationRequested();
        Check(_map(_context, entry.Staging, 0, MapRead, 0, out var mapped));
        try
        {
            if (mapped.Data == 0 || mapped.RowPitch < read.Width * 4L || mapped.RowPitch > int.MaxValue)
                throw new InvalidOperationException("The preview readback returned an incomplete buffer.");
            long touched = (long)mapped.RowPitch * (read.Height - 1) + read.Width * 4L;
            if (touched > ClientPreviewBufferReader.MaximumReadbackBytes) throw new InvalidOperationException("The preview readback exceeds its budget.");
            return ClientPreviewBufferReader.CopyBgra(mapped.Data, (uint)touched, 0, (int)mapped.RowPitch, read,
                ClientPreviewBufferReader.OutputSize(source), ct);
        }
        finally { _unmap(_context, entry.Staging, 0); }
    }

    /// <summary>Conservative GPU bytes for one cache entry: 256-byte rows for every mip level plus the staging copy.</summary>
    internal static long EntryBytes(PreviewSurfaceSize source, int level)
    {
        static long Bytes(PreviewSurfaceSize size) => (size.Width * 4L + 255) / 256 * 256 * size.Height;
        long total = Bytes(ClientPreviewBufferReader.LevelSize(source, level));
        if (level > 0) for (int index = 0; index <= level; index++) total += Bytes(ClientPreviewBufferReader.LevelSize(source, index));
        return total;
    }

    private Entry Ensure(PreviewSurfaceSize source, int level, PreviewSurfaceSize read)
    {
        if (_entries.TryGetValue(source, out var cached)) { cached.LastUse = ++_uses; return cached; }
        long bytes = EntryBytes(source, level);
        if (bytes > ClientPreviewBufferReader.MaximumReadbackBytes) throw new InvalidOperationException("The preview readback exceeds its budget.");
        while (_entries.Count > 0 && _cachedBytes + bytes > ClientPreviewBufferReader.MaximumReadbackBytes)
        {
            KeyValuePair<PreviewSurfaceSize, Entry> oldest = default; bool found = false;
            foreach (var pair in _entries)
                if (!found || pair.Value.LastUse < oldest.Value.LastUse) { oldest = pair; found = true; }
            _entries.Remove(oldest.Key); _cachedBytes -= oldest.Value.Bytes; oldest.Value.Dispose();
        }
        var entry = new Entry { Bytes = bytes, LastUse = ++_uses };
        try
        {
            if (level > 0)
            {
                var mips = new TextureDescription
                {
                    Width = (uint)source.Width, Height = (uint)source.Height, MipLevels = (uint)level + 1, ArraySize = 1, Format = Bgra8,
                    SampleCount = 1, BindFlags = BindShaderResource | BindRenderTarget, MiscFlags = MiscGenerateMips
                };
                Check(_createTexture(_device, in mips, 0, out entry.Mips));
                Check(_createView(_device, entry.Mips, 0, out entry.View));
            }
            var staging = new TextureDescription
            {
                Width = (uint)read.Width, Height = (uint)read.Height, MipLevels = 1, ArraySize = 1, Format = Bgra8,
                SampleCount = 1, Usage = UsageStaging, CpuAccessFlags = CpuAccessRead
            };
            Check(_createTexture(_device, in staging, 0, out entry.Staging));
            _entries.Add(source, entry); _cachedBytes += bytes;
            return entry;
        }
        catch { entry.Dispose(); throw; }
    }

    /// <summary>Fixed-text failure carrying only the HRESULT; the provider maps it to a Failed preview status.</summary>
    internal static void Check(int hr)
    {
        if (hr < 0) throw new COMException("A Direct3D preview call failed.", hr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear(); _cachedBytes = 0;
    }

    private static T Slot<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    private sealed class Entry : IDisposable
    {
        public nint Mips, View, Staging;
        public long Bytes, LastUse;
        public void Dispose()
        {
            if (View != 0) Marshal.Release(View);
            if (Mips != 0) Marshal.Release(Mips);
            if (Staging != 0) Marshal.Release(Staging);
            View = Mips = Staging = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedSubresource { public nint Data; public uint RowPitch, DepthPitch; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2D(nint self, in TextureDescription description, nint initialData, out nint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateShaderResourceView(nint self, nint resource, nint description, out nint view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CheckFormatSupport(nint self, uint format, out uint support);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDescription(nint self, out TextureDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapCall(nint self, nint resource, uint subresource, uint type, uint flags, out MappedSubresource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapCall(nint self, nint resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopySubresourceRegion(nint self, nint destination, uint destinationSubresource,
        uint x, uint y, uint z, nint source, uint sourceSubresource, in Box box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GenerateMipsCall(nint self, nint view);
}
