using System.Runtime.InteropServices;
using System.Windows.Forms;
using Mona.App.Native;
using Mona.Core.Calendar;
using Mona.Core.Imaging;
using Mona.Core.Settings;
using Mona.Core.Weather;

namespace Mona.App;

/// <summary>
/// The desktop calendar sticker: a borderless window you can drag anywhere.
///
/// A layered window, painted through <c>UpdateLayeredWindow</c> rather than
/// through WinForms. That is not a performance choice — it is the only way to get
/// genuine per-pixel alpha on a top-level window, and it brings the click-through
/// with it: Windows routes mouse messages by the window's own alpha, so the
/// transparent corners of a tilted sticker fall through to whatever is underneath
/// without this code testing a single pixel.
///
/// Dragging comes from answering <c>WM_NCHITTEST</c> with <c>HTCAPTION</c>: every
/// opaque pixel is a title bar, so the sticker moves from anywhere on the
/// artwork.
/// </summary>
internal sealed class CalendarWindow : Form
{
    private readonly CalendarRenderer _renderer;
    private readonly Preferences _preferences;
    private readonly WeatherSource _weather;

    private readonly System.Windows.Forms.Timer _tick = new();
    private readonly System.Windows.Forms.Timer _flip = new();
    /// <summary>Waits for a dragged slider to stop before drawing what it asked for.</summary>
    private readonly System.Windows.Forms.Timer _settle = new();
    private CalendarContent? _content;
    private List<Bitmap32> _frames = new();
    private int _frameIndex;
    /// <summary>
    /// Bumped on every refresh so a slow render that has been superseded can drop
    /// its result instead of putting yesterday back on the screen.
    /// </summary>
    private int _renderJob;

    public CalendarWindow(CalendarRenderer renderer, Preferences preferences, WeatherSource weather)
    {
        _renderer = renderer;
        _preferences = preferences;
        _weather = weather;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = preferences.CalendarAlwaysOnTop;
        Text = "Mona 日历";

        // Once a minute is enough for a date and a time-of-day card, and it lands
        // the change within a minute of midnight without a wakeup budget.
        _tick.Interval = 60_000;
        _tick.Tick += (_, _) => Refresh(force: false);

        // The three drawings are meant to run 1-2-3 on a loop. They are drawn in
        // register, so nothing but the icon moves between them.
        _flip.Interval = 400;
        _flip.Tick += (_, _) => ShowNextFrame();

        _settle.Interval = 150;
        _settle.Tick += (_, _) => { _settle.Stop(); Refresh(force: true); };

        _weather.Changed += OnWeatherChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    /// <summary>Nothing here paints through GDI; the layered bitmap is the window.</summary>
    protected override void OnPaintBackground(PaintEventArgs e) { }

    // MARK: - showing

    public void ShowSticker()
    {
        // Shown first, and only then sized and placed. Both of those need the
        // monitor's DPI, which needs a window handle to ask about — and a layered
        // window puts nothing on screen until UpdateLayeredWindow is called, so
        // there is no wrongly-sized flash to avoid by doing it the other way.
        Show();
        ApplyPreferences();
        PlaceIfUnplaced();
        _weather.Start();
        Refresh(force: true);
        _tick.Start();
        _preferences.CalendarVisible = true;
        _preferences.Save();
    }

    public void HideSticker()
    {
        Hide();
        _tick.Stop();
        // Nothing is on screen, so nothing should be animating it.
        _flip.Stop();
        _weather.Stop();
        _preferences.CalendarVisible = false;
        _preferences.Save();
    }

    public void Toggle()
    {
        if (Visible) HideSticker(); else ShowSticker();
    }

    /// <summary>
    /// Applies whatever the settings window just changed.
    ///
    /// The re-render is held back a moment because the size control is a slider:
    /// dragging it end to end raises this a hundred times, and each one is a full
    /// five-layer composite with two flood fills in it. The window resizes at
    /// once — that is what makes the slider feel live — and the artwork catches
    /// up when the dragging stops.
    /// </summary>
    public void SettingsChanged()
    {
        TopMost = _preferences.CalendarAlwaysOnTop;
        if (!Visible) return;
        ApplyPreferences();
        _settle.Stop();
        _settle.Start();
    }

    private void OnWeatherChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => Refresh(force: true));
    }

    // MARK: - geometry

    private uint Dpi => IsHandleCreated ? Math.Max(96, Win32.GetDpiForWindow(Handle)) : 96;

    /// <summary>
    /// The sticker's width in real pixels. The preference is in logical units,
    /// so the same setting is the same apparent size on every display, and the
    /// factor comes from the monitor rather than from a fixed guess.
    /// </summary>
    private int PhysicalWidth
    {
        get
        {
            double logical = _preferences.CalendarWidth >= 160 ? _preferences.CalendarWidth : 320;
            return (int)Math.Round(logical * Dpi / 96.0, MidpointRounding.AwayFromZero);
        }
    }

    private void ApplyPreferences()
    {
        int width = PhysicalWidth;
        int height = (int)Math.Round(width / _renderer.Aspect, MidpointRounding.AwayFromZero);
        if (Size.Width == width && Size.Height == height) return;
        // Anchored top-left, so growing the sticker does not walk it up the screen.
        Size = new System.Drawing.Size(width, height);
    }

    private void PlaceIfUnplaced()
    {
        if (_preferences.CalendarX is { } x && _preferences.CalendarY is { } y)
        {
            Location = new System.Drawing.Point(x, y);
            if (OnAScreen()) return;
        }
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1440, 900);
        int margin = (int)Math.Round(64 * Dpi / 96.0, MidpointRounding.AwayFromZero);
        Location = new System.Drawing.Point(area.Right - Width - margin, area.Top + margin);
    }

    /// <summary>
    /// A remembered position belongs to a monitor that may since have been
    /// unplugged, and a sticker restored onto a screen that no longer exists is a
    /// sticker nobody can find.
    /// </summary>
    private bool OnAScreen()
    {
        var bounds = new Rectangle(Location, Size);
        foreach (var screen in Screen.AllScreens)
            if (screen.WorkingArea.IntersectsWith(bounds)) return true;
        return false;
    }

    // MARK: - drawing

    private void Refresh(bool force)
    {
        if (!Visible) return;
        var next = CalendarContent.Now(_weather.Kind) with { Frame = 1 };
        if (!force && _content is { } current && current == next) return;
        _content = next;

        int width = Width;
        int height = Height;
        if (width <= 0 || height <= 0) return;

        _renderJob++;
        int job = _renderJob;

        // Off the UI thread. Compositing five layers of a million pixels is tens
        // of milliseconds and the flood fills are not free; on the UI thread that
        // is the app visibly hanging every minute.
        Task.Run(() =>
        {
            var made = _renderer.RenderFrames(next, [1, 2, 3], width, 1);
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    // A later refresh may have overtaken this one while it drew.
                    if (job != _renderJob) return;
                    _frames = made;
                    _frameIndex = 0;
                    if (made.Count > 0) ApplyLayeredBitmap(made[0]);
                    if (made.Count > 1) _flip.Start(); else _flip.Stop();
                });
            }
            catch (Exception)
            {
                // Closed mid-render, which is not worth a log line.
            }
        });
    }

    private void ShowNextFrame()
    {
        if (_frames.Count <= 1) return;
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        ApplyLayeredBitmap(_frames[_frameIndex]);
    }

    /// <summary>
    /// Hands the finished sticker to the window manager.
    ///
    /// The buffer arrives straight-alpha, the way a PNG stores it, and
    /// <c>UpdateLayeredWindow</c> wants it premultiplied — skip that and every
    /// antialiased edge turns into a bright halo.
    /// </summary>
    private void ApplyLayeredBitmap(Bitmap32 image)
    {
        if (!IsHandleCreated) return;
        int w = image.Width, h = image.Height;
        if (w <= 0 || h <= 0) return;

        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr memory = Win32.CreateCompatibleDC(screen);
        IntPtr previous = IntPtr.Zero;
        IntPtr section = IntPtr.Zero;

        try
        {
            var header = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,   // top-down, like every buffer in this project
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BI_RGB
                }
            };
            section = Win32.CreateDIBSection(memory, ref header, Win32.DIB_RGB_COLORS,
                                             out IntPtr bits, IntPtr.Zero, 0);
            if (section == IntPtr.Zero) return;

            var buffer = new byte[w * h * 4];
            var pixels = image.Pixels;
            for (int i = 0; i < w * h; i++)
            {
                byte alpha = pixels[i * 4 + 3];
                byte value = pixels[i * 4];
                byte premultiplied = (byte)(value * alpha / 255);
                buffer[i * 4 + 0] = premultiplied;   // blue
                buffer[i * 4 + 1] = premultiplied;   // green
                buffer[i * 4 + 2] = premultiplied;   // red
                buffer[i * 4 + 3] = alpha;
            }
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            previous = Win32.SelectObject(memory, section);

            var position = new Win32.POINT(Left, Top);
            var size = new Win32.SIZE(w, h);
            var origin = new Win32.POINT(0, 0);
            var blend = new Win32.BLENDFUNCTION
            {
                BlendOp = Win32.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32.AC_SRC_ALPHA
            };
            Win32.UpdateLayeredWindow(Handle, screen, ref position, ref size,
                                      memory, ref origin, 0, ref blend, Win32.ULW_ALPHA);
        }
        finally
        {
            if (previous != IntPtr.Zero) Win32.SelectObject(memory, previous);
            if (section != IntPtr.Zero) Win32.DeleteObject(section);
            Win32.DeleteDC(memory);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // MARK: - window messages

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Win32.WM_NCHITTEST:
                // Every pixel the sticker actually covers is a title bar, so it
                // drags from anywhere. The transparent ones never get here — a
                // layered window resolves those against what is underneath.
                base.WndProc(ref m);
                if (m.Result == IntPtr.Zero || m.Result == (IntPtr)Win32.HTCLIENT)
                    m.Result = (IntPtr)Win32.HTCAPTION;
                return;

            case 0x00A4: // WM_NCRBUTTONDOWN
            case 0x00A5: // WM_NCRBUTTONUP
                // Answering HTCAPTION would otherwise hand a right-click to the
                // system menu, which on a borderless sticker is an empty box
                // offering to move a window that has no title bar.
                return;

            case Win32.WM_EXITSIZEMOVE:
                _preferences.CalendarX = Left;
                _preferences.CalendarY = Top;
                _preferences.Save();
                break;

            case Win32.WM_DPICHANGED:
                // A drag onto a monitor at another scale means the sticker is now
                // the wrong number of pixels; it is re-rendered rather than
                // stretched, because stretching is what the layout table exists
                // to avoid.
                BeginInvoke(() => { ApplyPreferences(); Refresh(force: true); });
                break;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _weather.Changed -= OnWeatherChanged;
            _tick.Dispose();
            _flip.Dispose();
            _settle.Dispose();
        }
        base.Dispose(disposing);
    }
}
