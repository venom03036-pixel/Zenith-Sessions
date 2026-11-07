using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Windows;

internal static partial class NativeMethods
{
    // COM slots and layouts follow the Windows SDK d3d11.h interfaces.
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_BUFFER_DESC
    {
        public uint ByteWidth;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_SAMPLER_DESC
    {
        public int Filter;
        public int AddressU, AddressV, AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float Border0, Border1, Border2, Border3;
        public float MinLOD, MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIEWPORT
    {
        public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int D3DCompile(IntPtr source, nuint sourceSize, string? sourceName,
        IntPtr defines, IntPtr include, string entryPoint, string target, uint flags1, uint flags2,
        out IntPtr code, out IntPtr errors);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr BlobPointerFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate nuint BlobSizeFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateShaderFn(IntPtr self, IntPtr bytecode, nuint length, IntPtr linkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateResourceViewFn(IntPtr self, IntPtr resource, IntPtr description, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateSamplerFn(IntPtr self, in D3D11_SAMPLER_DESC description, out IntPtr sampler);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateBufferFn(IntPtr self, in D3D11_BUFFER_DESC description, IntPtr initialData, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SetShaderFn(IntPtr self, IntPtr shader, IntPtr classInstances, uint classCount);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SetResourcesFn(IntPtr self, uint startSlot, uint count, in IntPtr resources);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SetTopologyFn(IntPtr self, int topology);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SetRenderTargetsFn(IntPtr self, uint count, in IntPtr views, IntPtr depthStencilView);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SetViewportsFn(IntPtr self, uint count, in D3D11_VIEWPORT viewport);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DrawFn(IntPtr self, uint vertexCount, uint startVertex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void CopyResourceFn(IntPtr self, IntPtr destination, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void UpdateSubresourceFn(IntPtr self, IntPtr destination, uint subresource,
        IntPtr destinationBox, IntPtr source, uint sourceRowPitch, uint sourceDepthPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void ClearStateFn(IntPtr self);

}
