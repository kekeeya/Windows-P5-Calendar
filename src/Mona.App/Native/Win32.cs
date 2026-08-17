using System.Runtime.InteropServices;

namespace Mona.App.Native;

/// <summary>
/// The Win32 the app actually needs: a per-pixel-alpha window and an icon built
/// from a bitmap.
///
/// Kept in one place and named after what it is for, because the alternative —
/// declarations scattered next to their callers — is how a project ends up with
/// three subtly different <c>BLENDFUNCTION</c>s.
/// </summary>
internal static class Win32
{
    // MARK: - window styles

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_NCHITTEST = 0x0084;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTTRANSPARENT = -1;

    public const int SM_CXSMICON = 49;

    // MARK: - layered windows

    public const int ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int Width;
        public int Height;
        public SIZE(int width, int height) { Width = width; Height = height; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // MARK: - GDI

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
                                                 out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes,
                                             uint bitsPerPixel, IntPtr bits);

    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        /// <summary>Negative for a top-down bitmap, which is how everything here stores its rows.</summary>
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        /// <summary>Unused at 32bpp, but the struct the API expects has it.</summary>
        public uint bmiColors;
    }

    // MARK: - icons

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);

}
