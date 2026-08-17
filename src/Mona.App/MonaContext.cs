using System.Windows.Forms;
using Mona.App.Native;
using Mona.Core.Diagnostics;
using Mona.App.Tray;
using Mona.Core.Calendar;
using Mona.Core.Imaging;
using Mona.Core.Settings;
using Mona.Core.Weather;

namespace Mona.App;

/// <summary>
/// The running app: a tray icon, the sticker, and the settings window.
///
/// There is no main window and no taskbar button: the tray icon is the whole of
/// the app's presence, and the calendar it can show is off until someone turns it
/// on.
/// </summary>
internal sealed class MonaContext : ApplicationContext
{
    private readonly Preferences _preferences;
    private readonly NotifyIcon _tray = new();
    private readonly IconFactory _icons;
    private readonly CalendarWindow _calendar;
    private readonly WeatherSource _weather;
    private readonly PlaceTable _places;
    private readonly ToolStripMenuItem _toggleItem;
    private SettingsForm? _settings;
    /// <summary>
    /// Keeps the icon current.
    ///
    /// It is drawn once and would then sit there in the wrong polarity for the
    /// rest of the session if the taskbar went light, or at the wrong size if the
    /// display scaling changed. Every five seconds is soon enough to notice, and
    /// the factory hands back the very same icon unless something really changed.
    /// </summary>
    private readonly System.Windows.Forms.Timer _watch = new();

    public MonaContext()
    {
        _preferences = Preferences.Load();
        _lastCity = _preferences.CalendarCity;

        var art = new CalendarArt(AppPaths.CalendarArt);
        var renderer = new CalendarRenderer(art);
        _places = new PlaceTable(AppPaths.CalendarArt);
        _weather = new WeatherSource(_preferences);
        _calendar = new CalendarWindow(renderer, _preferences, _weather);

        var still = TrayFrame.LoadStill(AppPaths.StillIcon);
        _icons = new IconFactory(still is null ? [] : [still]);

        _toggleItem = new ToolStripMenuItem("显示日历", null, (_, _) => ToggleCalendar());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出 Mona", null, (_, _) => Quit()));

        _tray.Text = "Mona";
        _tray.ContextMenuStrip = menu;
        ShowStillIcon();
        _tray.Visible = true;
        // Left-clicking the icon does what the first menu item does, which is
        // what a tray icon with one obvious action should do.
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleCalendar(); };

        // Without the art there is nothing to show, and silently doing nothing
        // when the menu item is clicked is worse than saying so. The tray icon is
        // checked separately from the calendar because either can be missing on
        // its own, and a blank tray icon is the more confusing of the two — it
        // looks exactly like the app having failed to start.
        var missing = new List<string>();
        if (art.Layout is null) missing.Add("日历（cal-layout.json 与 PNG）");
        if (still is null) missing.Add("状态栏图标（Tray/MonaHead.png）");
        if (missing.Count > 0)
        {
            _tray.BalloonTipTitle = "Mona 缺少美术资源";
            _tray.BalloonTipText = $"{string.Join("、", missing)}\n应该在：{AppPaths.Assets}";
            _tray.ShowBalloonTip(10000);
        }

        // What it found, so a report of "the icon didn't appear" comes with the
        // paths it looked in and the size it drew at.
        Log.Write($"assets {AppPaths.Assets}");
        Log.Write($"calendar art {(art.Layout is null ? "MISSING" : "loaded")}"
                + $", tray icon {(still is null ? "MISSING" : "loaded")}");
        Log.Write($"tray icon size {Win32.GetSystemMetrics(Win32.SM_CXSMICON)} px"
                + $", screens {Screen.AllScreens.Length}"
                + $", settings {Preferences.FilePath}");

        _watch.Interval = 5_000;
        _watch.Tick += (_, _) => ShowStillIcon();
        _watch.Start();

        if (_preferences.CalendarVisible) _calendar.ShowSticker();
        UpdateToggleTitle();
    }

    /// <summary>
    /// The tray's own icon size follows the display scaling rather than being a
    /// fixed sixteen pixels, and the factory tints for the taskbar it finds. Both
    /// can change while the app is running, so this is worth asking again rather
    /// than answering once at startup.
    /// </summary>
    private void ShowStillIcon()
    {
        int size = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
        var icon = _icons.Icons(size >= 8 ? size : 16).FirstOrDefault();
        if (icon is not null) _tray.Icon = icon;
    }

    private void ToggleCalendar()
    {
        _calendar.Toggle();
        UpdateToggleTitle();
        _settings?.Reload();
    }

    private void UpdateToggleTitle()
        => _toggleItem.Text = _calendar.Visible ? "隐藏日历" : "显示日历";

    private void OpenSettings()
    {
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_preferences, _places);
            _settings.Changed += OnSettingsChanged;
        }
        _settings.Reload();
        _settings.Show();
        _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
    }

    /// <summary>
    /// So a settings change can tell "the city moved" from "the width moved".
    /// Without it, dragging the size slider would fire a weather request per
    /// notch.
    /// </summary>
    private string _lastCity;

    private void OnSettingsChanged()
    {
        // Visibility is included because the settings switch and the menu item
        // are the same switch — both write the preference, and this is where it
        // takes effect no matter which one was used.
        if (_preferences.CalendarVisible != _calendar.Visible)
        {
            if (_preferences.CalendarVisible) _calendar.ShowSticker();
            else _calendar.HideSticker();
        }
        else
        {
            _calendar.SettingsChanged();
        }

        // Only when the city really changed — but then even if the sticker is
        // hidden, since the icon should already be right the next time it is
        // shown.
        if (_preferences.CalendarCity != _lastCity)
        {
            _lastCity = _preferences.CalendarCity;
            _weather.LocationChanged();
        }
        UpdateToggleTitle();
    }

    private void Quit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _icons.Dispose();
            _tray.Dispose();
            _weather.Dispose();
            _calendar.Dispose();
            _settings?.Dispose();
        }
        base.Dispose(disposing);
    }
}
