using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Windows;

internal static partial class NativeMethods
{
    public const int DxgiFormatB8G8R8A8Unorm = 87;
    public const int DxgiFormatB8G8R8A8UnormSrgb = 91;

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        int flags,
        IntPtr pFeatureLevels,
        int featureLevels,
        int sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint left;
        public uint top;
        public uint front;
        public uint right;
        public uint bottom;
        public uint back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX;
        public int PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetParentFn(IntPtr self, ref Guid iid, out IntPtr parent);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int EnumOutputsFn(IntPtr self, uint index, out IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetDescFn(IntPtr self, out DXGI_OUTPUT_DESC desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int DuplicateOutputFn(IntPtr self, IntPtr device, out IntPtr duplication);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int AcquireNextFrameFn(
        IntPtr self,
        uint timeoutMs,
        out DXGI_OUTDUPL_FRAME_INFO info,
        out IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ReleaseFrameFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateTexture2DFn(
        IntPtr self,
        in D3D11_TEXTURE2D_DESC desc,
        IntPtr initialData,
        out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void Texture2DGetDescFn(IntPtr self, out D3D11_TEXTURE2D_DESC desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int MapFn(
        IntPtr self,
        IntPtr resource,
        uint subresource,
        int mapType,
        uint mapFlags,
        out D3D11_MAPPED_SUBRESOURCE mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void CopySubresourceRegionFn(
        IntPtr self,
        IntPtr dst,
        uint dstSubresource,
        uint dstX,
        uint dstY,
        uint dstZ,
        IntPtr src,
        uint srcSubresource,
        in D3D11_BOX box);

    public static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        if (instance == IntPtr.Zero)
            return IntPtr.Zero;
        int hr = GetComDelegate<QueryInterfaceFn>(instance, 0)(instance, ref iid, out IntPtr ppv);
        return hr >= 0 ? ppv : IntPtr.Zero;
    }

    public static void ReleaseCom(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
            return;
        GetComDelegate<ReleaseFn>(instance, 2)(instance);
    }

    public static IntPtr DxgiGetAdapter(IntPtr dxgiDevice)
    {
        int hr = GetComDelegate<GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out IntPtr adapter);
        return hr >= 0 ? adapter : IntPtr.Zero;
    }

    public static IntPtr DxgiGetParent(IntPtr dxgiObject, Guid iid)
    {
        int hr = GetComDelegate<GetParentFn>(dxgiObject, 6)(dxgiObject, ref iid, out IntPtr parent);
        return hr >= 0 ? parent : IntPtr.Zero;
    }

    public static IntPtr DxgiEnumOutputs(IntPtr adapter, uint index)
    {
        int hr = GetComDelegate<EnumOutputsFn>(adapter, 7)(adapter, index, out IntPtr output);
        return hr >= 0 ? output : IntPtr.Zero;
    }

    public static DXGI_OUTPUT_DESC DxgiGetOutputDesc(IntPtr output)
    {
        int hr = GetComDelegate<GetDescFn>(output, 7)(output, out DXGI_OUTPUT_DESC desc);
        if (hr < 0)
            throw new InvalidOperationException($"IDXGIOutput.GetDesc failed (HRESULT 0x{hr:X8}).");
        return desc;
    }

    public static IntPtr DxgiDuplicateOutput(IntPtr output1, IntPtr d3dDevice)
    {
        int hr = GetComDelegate<DuplicateOutputFn>(output1, 22)(output1, d3dDevice, out IntPtr duplication);
        return hr >= 0 ? duplication : IntPtr.Zero;
    }

    public static AcquireNextFrameFn BindAcquireNextFrame(IntPtr duplication)
        => GetComDelegate<AcquireNextFrameFn>(duplication, 8);

    public static ReleaseFrameFn BindReleaseFrame(IntPtr duplication)
        => GetComDelegate<ReleaseFrameFn>(duplication, 14);

    public static MapFn BindMap(IntPtr context)
        => GetComDelegate<MapFn>(context, 14);

    public static UnmapFn BindUnmap(IntPtr context)
        => GetComDelegate<UnmapFn>(context, 15);

    public static CopySubresourceRegionFn BindCopySubresourceRegion(IntPtr context)
        => GetComDelegate<CopySubresourceRegionFn>(context, 46);

    public static IntPtr CreateStagingTexture(IntPtr device, int width, int height, int format, int usage, int cpuAccess, uint bindFlags = 0)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = (uint)cpuAccess,
            MiscFlags = 0
        };
        int hr = GetComDelegate<CreateTexture2DFn>(device, 5)(device, in desc, IntPtr.Zero, out IntPtr texture);
        if (hr < 0 || texture == IntPtr.Zero)
            throw new InvalidOperationException($"ID3D11Device.CreateTexture2D failed (HRESULT 0x{hr:X8}).");
        return texture;
    }

    public static int GetTextureFormat(IntPtr texture)
        => GetTextureDescription(texture).Format;

    public static D3D11_TEXTURE2D_DESC GetTextureDescription(IntPtr texture)
    {
        GetComDelegate<Texture2DGetDescFn>(texture, 10)(texture, out D3D11_TEXTURE2D_DESC desc);
        return desc;
    }

    public static bool IsBgraDesktopFormat(int format)
        => format is DxgiFormatB8G8R8A8Unorm or DxgiFormatB8G8R8A8UnormSrgb;

    internal static unsafe T GetComDelegate<T>(IntPtr instance, int slot) where T : Delegate
    {
        IntPtr* vtable = *(IntPtr**)instance;
        return Marshal.GetDelegateForFunctionPointer<T>(vtable[slot]);
    }
}
