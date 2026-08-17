using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mona.App.Native;
using Mona.Core.Imaging;

namespace Mona.App.Tray;

/// <summary>
/// Turns the run frames into tray icons.
///
/// A tray icon is a bitmap and Windows draws it exactly as given — there is no
/// "template image" that the system will recolour for the taskbar it sits on. So
/// the silhouette is tinted here, from the same registry value the taskbar itself
/// reads. Without that, black artwork is invisible on the default dark taskbar,
/// which looks exactly like the app having failed to start.
///
/// Icons are built through <c>CreateIconIndirect</c> from a 32-bit DIB rather
/// than through <c>Bitmap.GetHicon</c>. The latter is one line, but it has a
/// long history of thresholding alpha to a 1-bit mask, and these frames are
/// antialiased silhouettes at sixteen pixels — hard edges are the whole of what
/// they would have left.
/// </summary>
internal sealed class IconFactory : IDisposable
{
    private readonly Bitmap32[] _frames;
    private readonly List<IntPtr> _handles = new();
    /// <summary>
    /// The generation before this one, kept alive on purpose.
    ///
    /// <c>Shell_NotifyIcon</c> holds the handle it was given rather than a copy,
    /// so destroying the old icons the moment new ones are built can blank the
    /// tray for as long as it takes the next frame to arrive. Retiring them one
    /// generation late costs eight handles and never shows a gap.
    /// </summary>
    private readonly List<IntPtr> _retiring = new();
    private Icon[]? _icons;
    private int _builtSize;
    private bool _builtLight;

    /// <summary>
    /// When the taskbar's polarity was last read. The animation asks for icons
    /// thirteen times a second and the registry is not something to open at that
    /// rate for a value that changes when someone visits Settings.
    /// </summary>
    private DateTime _themeChecked = DateTime.MinValue;
    private bool _themeIsLight;

    /// <summary>
    /// One frame for a still icon, eight for the run cycle. The class does not
    /// care which — the tinting, the sizing and the handle bookkeeping are the
    /// same either way.
    /// </summary>
    public IconFactory(Bitmap32[] frames)
    {
        _frames = frames.Length > 0 ? frames : [new Bitmap32(1, 1)];
        // Once, and shared by every frame: the artwork's own extent decides how
        // much of the sixteen pixels it gets, and a box computed per frame would
        // re-centre the cat on each one.
        _crop = TrayFrame.AlphaBounds(_frames);
    }

    private readonly TrayFrame.Bounds _crop;

    public int Count => _frames.Length;

    /// <summary>
    /// The eight frames at the tray's icon size, in the taskbar's own polarity.
    /// Rebuilt only when one of those two things changes.
    /// </summary>
    public Icon[] Icons(int size)
    {
        bool light = TaskbarIsLight();
        if (_icons is not null && size == _builtSize && light == _builtLight) return _icons;

        // The generation before last is safely gone by now; this one steps back.
        foreach (var handle in _retiring) Win32.DestroyIcon(handle);
        _retiring.Clear();
        _retiring.AddRange(_handles);
        _handles.Clear();
        _icons = null;

        // A light taskbar wants the dark silhouette, and a dark one the light —
        // the same inversion the template image gets for free over there.
        byte tone = light ? (byte)0 : (byte)255;
        var icons = new Icon[_frames.Length];
        for (int i = 0; i < _frames.Length; i++)
            icons[i] = Build(_frames[i], size, tone);

        _icons = icons;
        _builtSize = size;
        _builtLight = light;
        return icons;
    }

    private bool TaskbarIsLight()
    {
        var now = DateTime.UtcNow;
        if (now - _themeChecked < TimeSpan.FromSeconds(5)) return _themeIsLight;
        _themeChecked = now;
        _themeIsLight = ReadTaskbarIsLight();
        return _themeIsLight;
    }

    /// <summary>
    /// Whether the taskbar is drawn light. The key is absent on builds that never
    /// had the setting, where the taskbar is dark.
    /// </summary>
    private static bool ReadTaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// One frame, box-filtered down to the tray size and tinted, as an icon that
    /// owns its handle until <see cref="Release"/>.
    /// </summary>
    private Icon Build(Bitmap32 frame, int size, byte tone)
    {
        var header = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = size,
                // Top-down, matching how every buffer in this project is stored.
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB
            }
        };

        IntPtr colour = Win32.CreateDIBSection(IntPtr.Zero, ref header, Win32.DIB_RGB_COLORS,
                                               out IntPtr bits, IntPtr.Zero, 0);
        if (colour == IntPtr.Zero) return SystemIcons.Application;

        var pixels = TrayFrame.Alpha(frame, size, _crop);
        var buffer = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            byte alpha = pixels[i];
            // Straight alpha, which is what an icon's colour bitmap carries; the
            // premultiplication happens when Windows draws it.
            buffer[i * 4 + 0] = tone;   // blue
            buffer[i * 4 + 1] = tone;   // green
            buffer[i * 4 + 2] = tone;   // red
            buffer[i * 4 + 3] = alpha;
        }
        Marshal.Copy(buffer, 0, bits, buffer.Length);

        // An all-zero monochrome mask: with a 32-bit colour bitmap the alpha
        // channel is what decides coverage, and the mask must not veto it.
        IntPtr mask = Win32.CreateBitmap(size, size, 1, 1, IntPtr.Zero);

        var info = new Win32.ICONINFO
        {
            fIcon = true,
            hbmMask = mask,
            hbmColor = colour
        };
        IntPtr handle = Win32.CreateIconIndirect(ref info);
        Win32.DeleteObject(colour);
        Win32.DeleteObject(mask);

        if (handle == IntPtr.Zero) return SystemIcons.Application;
        _handles.Add(handle);
        return Icon.FromHandle(handle);
    }

    /// <summary>
    /// Icon handles are not garbage — nothing collects them, and a process that
    /// keeps building icons without destroying them runs the desktop out of
    /// handles rather than itself out of memory.
    /// </summary>
    public void Dispose()
    {
        foreach (var handle in _retiring) Win32.DestroyIcon(handle);
        foreach (var handle in _handles) Win32.DestroyIcon(handle);
        _retiring.Clear();
        _handles.Clear();
        _icons = null;
    }
}
