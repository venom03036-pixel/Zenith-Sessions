using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>Owns the small icon extracted from the application's executable resource.</summary>
public sealed class ApplicationIconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private ApplicationIconHandle(IntPtr icon) : base(true) => SetHandle(icon);

    public static ApplicationIconHandle FromExecutable(string path)
    {
        ExtractIconEx(path, 0, out var large, out var small, 1);
        if (large != IntPtr.Zero) DestroyIcon(large);
        return new ApplicationIconHandle(small);
    }

    public static ApplicationIconHandle FromBgra32(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4)) throw new ArgumentException("Invalid icon pixels.");
        var header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return new(IntPtr.Zero);
        IntPtr mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
            var info = new IconInfo { IsIcon = true, ColorBitmap = color, MaskBitmap = mask };
            return new(CreateIconIndirect(ref info));
        }
        finally { if (mask != IntPtr.Zero) DeleteObject(mask); DeleteObject(color); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo
    { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint XHotspot, YHotspot; public IntPtr MaskBitmap, ColorBitmap; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, IntPtr data);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

    protected override bool ReleaseHandle() => DestroyIcon(handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
