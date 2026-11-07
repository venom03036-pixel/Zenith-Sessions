using System.Buffers;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Capture;

/// <summary>
/// Crops and resamples a shader-readable BGRA desktop texture on the GPU. Only the
/// smaller output is mapped to the CPU. The capture owner serializes all calls and
/// disposes this helper before its borrowed device/context. No capture loop is added.
/// </summary>
internal sealed class D3D11ImageScaler : IDisposable
{
    private const long CacheBytesLimit = 64 * 1024 * 1024;
    private const int CacheEntryLimit = 4;
    private const string ShaderSource = """
        Texture2D desktop : register(t0);
        SamplerState linearClamp : register(s0);
        cbuffer Crop : register(b0) { float4 cropUv; };
        struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        Vertex VS(uint id : SV_VertexID) {
            Vertex result;
            result.uv = float2((id << 1) & 2, id & 2);
            result.position = float4(result.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return result;
        }
        float4 PS(Vertex input) : SV_TARGET {
            return desktop.SampleLevel(linearClamp, cropUv.xy + input.uv * cropUv.zw, 0);
        }
        """;
    private static readonly Lazy<byte[]> VertexCode = new(() => Compile("VS", "vs_4_0"));
    private static readonly Lazy<byte[]> PixelCode = new(() => Compile("PS", "ps_4_0"));
    private readonly IntPtr _device, _context;
    private readonly NativeMethods.SetShaderFn _setVertexShader, _setPixelShader;
    private readonly NativeMethods.SetResourcesFn _setResources, _setSamplers, _setConstants;
    private readonly NativeMethods.SetTopologyFn _setTopology;
    private readonly NativeMethods.SetRenderTargetsFn _setRenderTargets;
    private readonly NativeMethods.SetViewportsFn _setViewports;
    private readonly NativeMethods.DrawFn _draw;
    private readonly NativeMethods.CopyResourceFn _copy;
    private readonly NativeMethods.UpdateSubresourceFn _update;
    private readonly NativeMethods.ClearStateFn _clear;
    private readonly NativeMethods.MapFn _map;
    private readonly NativeMethods.UnmapFn _unmap;
    private readonly Dictionary<Size, OutputTargets> _outputs = new();
    private IntPtr _vertexShader, _pixelShader, _sampler, _constants, _sourceView, _viewTexture;
    private NativeMethods.D3D11_TEXTURE2D_DESC _sourceDescription;
    private long _useCounter, _cachedBytes;
    private bool _disposed;

    public D3D11ImageScaler(IntPtr device, IntPtr context)
    {
        if (device == IntPtr.Zero || context == IntPtr.Zero) throw new ArgumentException("A live D3D11 device and immediate context are required.");
        _device = device; _context = context;
        _setResources = NativeMethods.GetComDelegate<NativeMethods.SetResourcesFn>(context, 8);
        _setPixelShader = NativeMethods.GetComDelegate<NativeMethods.SetShaderFn>(context, 9);
        _setSamplers = NativeMethods.GetComDelegate<NativeMethods.SetResourcesFn>(context, 10);
        _setVertexShader = NativeMethods.GetComDelegate<NativeMethods.SetShaderFn>(context, 11);
        _draw = NativeMethods.GetComDelegate<NativeMethods.DrawFn>(context, 13);
        _setConstants = NativeMethods.GetComDelegate<NativeMethods.SetResourcesFn>(context, 16);
        _setTopology = NativeMethods.GetComDelegate<NativeMethods.SetTopologyFn>(context, 24);
        _setRenderTargets = NativeMethods.GetComDelegate<NativeMethods.SetRenderTargetsFn>(context, 33);
        _setViewports = NativeMethods.GetComDelegate<NativeMethods.SetViewportsFn>(context, 44);
        _copy = NativeMethods.GetComDelegate<NativeMethods.CopyResourceFn>(context, 47);
        _update = NativeMethods.GetComDelegate<NativeMethods.UpdateSubresourceFn>(context, 48);
        _clear = NativeMethods.GetComDelegate<NativeMethods.ClearStateFn>(context, 110);
        _map = NativeMethods.BindMap(context); _unmap = NativeMethods.BindUnmap(context);
        try
        {
            _vertexShader = CreateShader(VertexCode.Value, 12);
            _pixelShader = CreateShader(PixelCode.Value, 15);
            var sampler = new NativeMethods.D3D11_SAMPLER_DESC
            {
                Filter = 0x15, // MIN_MAG_MIP_LINEAR
                AddressU = 3, AddressV = 3, AddressW = 3, // CLAMP
                MaxAnisotropy = 1, ComparisonFunc = 1, MaxLOD = float.MaxValue
            };
            Check(NativeMethods.GetComDelegate<NativeMethods.CreateSamplerFn>(device, 23)(device, in sampler, out _sampler), "Create image sampler");
            var buffer = new NativeMethods.D3D11_BUFFER_DESC { ByteWidth = 16, BindFlags = 4 };
            Check(NativeMethods.GetComDelegate<NativeMethods.CreateBufferFn>(device, 3)(device, in buffer, IntPtr.Zero, out _constants), "Create crop constants");
        }
        catch { Dispose(); throw; }
    }

    public unsafe ImageScanFrame Scale(IntPtr desktopTexture, Rectangle localCrop, Rectangle physicalBounds,
        Size sampledSize, long timestampTicks, long generation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (desktopTexture == IntPtr.Zero) throw new ArgumentException("A desktop texture is required.", nameof(desktopTexture));
        if (localCrop.Width <= 0 || localCrop.Height <= 0 || physicalBounds.Size != localCrop.Size
            || sampledSize.Width <= 0 || sampledSize.Height <= 0
            || sampledSize.Width > localCrop.Width || sampledSize.Height > localCrop.Height)
            throw new ArgumentException("Invalid image crop or downscale dimensions.");
        EnsureSourceView(desktopTexture);
        if (localCrop.Left < 0 || localCrop.Top < 0 || localCrop.Right > _sourceDescription.Width || localCrop.Bottom > _sourceDescription.Height)
            throw new ArgumentOutOfRangeException(nameof(localCrop), "The image crop extends outside the GPU texture.");
        OutputTargets output = EnsureOutput(sampledSize);
        float* crop = stackalloc float[4]
        {
            (float)localCrop.X / _sourceDescription.Width, (float)localCrop.Y / _sourceDescription.Height,
            (float)localCrop.Width / _sourceDescription.Width, (float)localCrop.Height / _sourceDescription.Height
        };
        var viewport = new NativeMethods.D3D11_VIEWPORT { Width = sampledSize.Width, Height = sampledSize.Height, MaxDepth = 1 };
        _clear(_context); // This is the capture device's private context, never the game's context.
        try
        {
            _update(_context, _constants, 0, IntPtr.Zero, (IntPtr)crop, 0, 0);
            _setTopology(_context, 4); // TRIANGLELIST; SV_VertexID needs no vertex buffer.
            _setVertexShader(_context, _vertexShader, IntPtr.Zero, 0);
            _setPixelShader(_context, _pixelShader, IntPtr.Zero, 0);
            _setResources(_context, 0, 1, in _sourceView);
            _setSamplers(_context, 0, 1, in _sampler);
            _setConstants(_context, 0, 1, in _constants);
            _setRenderTargets(_context, 1, in output.View, IntPtr.Zero);
            _setViewports(_context, 1, in viewport);
            _draw(_context, 3, 0);
        }
        finally { _clear(_context); } // Unbind SRV/RTV before copying or replacing resources.
        ct.ThrowIfCancellationRequested();
        _copy(_context, output.Staging, output.Texture);
        Check(_map(_context, output.Staging, 0, 1, 0, out var mapped), "Map sampled image");
        try
        {
            int stride = checked(sampledSize.Width * 4);
            if (mapped.pData == IntPtr.Zero || mapped.RowPitch < stride)
                throw new InvalidOperationException("GPU image readback returned an incomplete buffer.");
            ct.ThrowIfCancellationRequested();
            var owner = MemoryPool<byte>.Shared.Rent(checked(stride * sampledSize.Height));
            try
            {
                Span<byte> destination = owner.Memory.Span;
                for (int y = 0; y < sampledSize.Height; y++)
                {
                    if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
                    new ReadOnlySpan<byte>((byte*)mapped.pData + (nuint)y * mapped.RowPitch, stride)
                        .CopyTo(destination.Slice(y * stride, stride));
                }
                return new ImageScanFrame(physicalBounds, sampledSize, stride, FramePixelFormat.Bgra32,
                    owner, timestampTicks, generation);
            }
            catch { owner.Dispose(); throw; }
        }
        finally { _unmap(_context, output.Staging, 0); }
    }

    private void EnsureSourceView(IntPtr texture)
    {
        if (texture == _viewTexture) return;
        var desc = NativeMethods.GetTextureDescription(texture);
        if (desc.Format != NativeMethods.DxgiFormatB8G8R8A8Unorm || (desc.BindFlags & 8) == 0 || desc.SampleCount != 1)
            throw new InvalidOperationException("Image scaling requires a BGRA8 UNORM shader-readable desktop snapshot.");
        Check(NativeMethods.GetComDelegate<NativeMethods.CreateResourceViewFn>(_device, 7)(_device, texture, IntPtr.Zero, out var view), "Create desktop image view");
        NativeMethods.ReleaseCom(_sourceView);
        _sourceView = view; _viewTexture = texture; _sourceDescription = desc;
    }

    private OutputTargets EnsureOutput(Size size)
    {
        if (_outputs.TryGetValue(size, out var cached)) { cached.LastUse = ++_useCounter; return cached; }
        long bytes = checked((long)size.Width * size.Height * 8); // GPU render target plus staging readback.
        while (_outputs.Count > 0 && (_outputs.Count >= CacheEntryLimit || _cachedBytes + bytes > CacheBytesLimit))
        {
            KeyValuePair<Size, OutputTargets> oldest = default;
            foreach (var entry in _outputs)
                if (oldest.Value is null || entry.Value.LastUse < oldest.Value.LastUse) oldest = entry;
            _outputs.Remove(oldest.Key); _cachedBytes -= oldest.Value.Bytes; oldest.Value.Dispose();
        }
        var output = new OutputTargets { Bytes = bytes, LastUse = ++_useCounter };
        try
        {
            var desc = new NativeMethods.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1,
                Format = NativeMethods.DxgiFormatB8G8R8A8Unorm, SampleCount = 1, BindFlags = 0x20
            };
            Check(NativeMethods.GetComDelegate<NativeMethods.CreateTexture2DFn>(_device, 5)(_device, in desc, IntPtr.Zero, out output.Texture), "Create sampled render target");
            Check(NativeMethods.GetComDelegate<NativeMethods.CreateResourceViewFn>(_device, 9)(_device, output.Texture, IntPtr.Zero, out output.View), "Create sampled render view");
            output.Staging = NativeMethods.CreateStagingTexture(_device, size.Width, size.Height,
                NativeMethods.DxgiFormatB8G8R8A8Unorm, 3, 0x20000);
            _outputs.Add(size, output); _cachedBytes += bytes;
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private unsafe IntPtr CreateShader(byte[] code, int slot)
    {
        fixed (byte* bytes = code)
        {
            Check(NativeMethods.GetComDelegate<NativeMethods.CreateShaderFn>(_device, slot)(_device,
                (IntPtr)bytes, (nuint)code.Length, IntPtr.Zero, out var shader), "Create image shader");
            return shader;
        }
    }

    private static unsafe byte[] Compile(string entryPoint, string target)
    {
        byte[] source = Encoding.ASCII.GetBytes(ShaderSource);
        IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
        try
        {
            int hr;
            fixed (byte* bytes = source)
                hr = NativeMethods.D3DCompile((IntPtr)bytes, (nuint)source.Length, "ZenithImageScale",
                    IntPtr.Zero, IntPtr.Zero, entryPoint, target, 1u << 15, 0, out code, out errors);
            if (hr < 0)
            {
                string detail = errors == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(
                    NativeMethods.GetComDelegate<NativeMethods.BlobPointerFn>(errors, 3)(errors)) ?? string.Empty;
                throw new InvalidOperationException($"Image shader compilation failed (HRESULT 0x{hr:X8}): {detail}");
            }
            int length = checked((int)NativeMethods.GetComDelegate<NativeMethods.BlobSizeFn>(code, 4)(code));
            var result = new byte[length];
            Marshal.Copy(NativeMethods.GetComDelegate<NativeMethods.BlobPointerFn>(code, 3)(code), result, 0, length);
            return result;
        }
        finally { NativeMethods.ReleaseCom(errors); NativeMethods.ReleaseCom(code); }
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"{operation} failed (HRESULT 0x{hr:X8}).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clear(_context);
        foreach (var output in _outputs.Values) output.Dispose();
        _outputs.Clear(); _cachedBytes = 0;
        NativeMethods.ReleaseCom(_sourceView); NativeMethods.ReleaseCom(_constants);
        NativeMethods.ReleaseCom(_sampler); NativeMethods.ReleaseCom(_pixelShader); NativeMethods.ReleaseCom(_vertexShader);
        _sourceView = _viewTexture = _constants = _sampler = _pixelShader = _vertexShader = IntPtr.Zero;
    }

    private sealed class OutputTargets : IDisposable
    {
        public IntPtr Texture, View, Staging;
        public long LastUse, Bytes;
        public void Dispose()
        {
            NativeMethods.ReleaseCom(View); NativeMethods.ReleaseCom(Staging); NativeMethods.ReleaseCom(Texture);
            Texture = View = Staging = IntPtr.Zero;
        }
    }
}
