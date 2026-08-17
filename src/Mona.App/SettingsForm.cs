using System.Drawing;
using System.Windows.Forms;
using Mona.Core.Settings;

namespace Mona.App;

/// <summary>
/// The settings window: what the sticker looks like, where its weather comes
/// from, and whether Mona starts with Windows.
///
/// Laid out by <see cref="TableLayoutPanel"/> rather than by coordinates. The
/// first version placed every control at a hand-computed pixel and the size
/// slider promptly sat on top of the checkbox below it — and that is the good
/// case, because a substituted font or a display at another scale moves
/// everything and a fixed layout has nothing to absorb it with. Rows that size
/// themselves cannot overlap.
///
/// Every control writes its preference and raises <see cref="Changed"/> as it is
/// touched, rather than collecting an OK button's worth of state. A sticker on
/// the desktop is being looked at while the slider moves, so the slider had
/// better move it.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly Preferences _preferences;
    private readonly PlaceTable _places;

    private readonly CheckBox _visible = new();
    private readonly CheckBox _onTop = new();
    private readonly TrackBar _width = new();
    private readonly Label _widthValue = new();
    private readonly TextBox _search = new();
    private readonly ListBox _results = new();
    private readonly Label _city = new();
    private readonly CheckBox _login = new();

    /// <summary>Raised whenever something the sticker cares about has changed.</summary>
    public event Action? Changed;

    private readonly List<CalendarChoice> _choices = new();
    private readonly TableLayoutPanel _body;
    private bool _loading = true;

    public SettingsForm(Preferences preferences, PlaceTable places)
    {
        _preferences = preferences;
        _places = places;

        Text = "Mona 设置";
        // Sizable rather than a fixed dialog: the content is text in whatever
        // font the machine happens to have, and being able to drag the window
        // bigger is a better answer to a surprise than a clipped control.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFont(9F);
        BackColor = SystemColors.Window;
        ClientSize = new Size(440, 520);

        _body = BuildBody();
        Controls.Add(_body);
        LoadValues();
        _loading = false;
    }

    /// <summary>
    /// Sizes the window from what the layout actually needs, measured after
    /// scaling has been applied.
    ///
    /// The numbers above are a starting shape, not a promise. What the content
    /// really occupies depends on the display's scaling and on which font the
    /// machine substituted, and neither is knowable when the constructor runs —
    /// so the floor is measured here instead of guessed. A control that does not
    /// fit is the bug this window already had once.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        var content = _body.PreferredSize;
        var needed = SizeFromClientSize(content);
        MinimumSize = new Size(Math.Max(needed.Width, 360), needed.Height);
        if (ClientSize.Height < content.Height)
            ClientSize = new Size(ClientSize.Width, content.Height);
        if (ClientSize.Width < content.Width)
            ClientSize = new Size(content.Width, ClientSize.Height);
    }

    /// <summary>
    /// Segoe UI carries no Chinese and Microsoft YaHei UI is what Windows itself
    /// uses for it. Asking for a font that is not installed lands silently on
    /// whatever the system substitutes, so the fallback chain is spelled out
    /// rather than left to chance.
    /// </summary>
    private static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        foreach (string name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
        {
            var font = new Font(name, size, style);
            if (string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase)) return font;
            font.Dispose();
        }
        return new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, size, style);
    }

    private TableLayoutPanel BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = SystemColors.Window
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Add(Control control, SizeType sizing = SizeType.AutoSize, float value = 0)
        {
            body.Controls.Add(control);
            body.RowStyles.Add(new RowStyle(sizing, value));
        }

        // MARK: 桌面日历

        Add(Header("桌面日历", first: true));

        _visible.Text = "显示日历";
        _visible.CheckedChanged += (_, _) => Apply(p => p.CalendarVisible = _visible.Checked);
        Add(Check(_visible));

        _onTop.Text = "永远置顶";
        _onTop.CheckedChanged += (_, _) => Apply(p => p.CalendarAlwaysOnTop = _onTop.Checked);
        Add(Check(_onTop));

        Add(BuildSizeRow());

        // MARK: 天气城市

        Add(Header("天气城市"));

        _city.AutoSize = true;
        _city.ForeColor = SystemColors.GrayText;
        _city.Margin = new Padding(2, 0, 0, 8);
        Add(_city);

        _search.Dock = DockStyle.Fill;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.PlaceholderText = "搜索城市 —— 中英文和拼音都能搜";
        _search.Margin = new Padding(0, 0, 0, 8);
        _search.TextChanged += (_, _) => FillResults();
        Add(_search);

        _results.Dock = DockStyle.Fill;
        _results.BorderStyle = BorderStyle.FixedSingle;
        _results.IntegralHeight = false;
        _results.Margin = new Padding(0);
        _results.SelectedIndexChanged += OnCityPicked;
        // The one row that takes the slack: dragging the window taller grows the
        // list, and nothing else moves.
        Add(_results, SizeType.Percent, 100);

        // MARK: 启动

        Add(Header("启动"));

        _login.Text = "开机时启动 Mona";
        _login.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            LaunchAtLogin.Set(_login.Checked);
            Apply(p => p.LaunchAtLogin = _login.Checked);
        };
        Add(Check(_login));

        return body;
    }

    /// <summary>
    /// A section title with a hairline under it. Lighter than a group box, which
    /// draws a frame around everything and makes a small window look like a form
    /// from 1998.
    /// </summary>
    private Control Header(string text, bool first = false)
    {
        var holder = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, first ? 0 : 20, 0, 10)
        };
        holder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        holder.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        holder.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));

        holder.Controls.Add(new Label
        {
            Text = text,
            Font = UiFont(9.75F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        });
        holder.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.ControlLight,
            Margin = new Padding(0)
        });
        return holder;
    }

    /// <summary>
    /// How tall to make the size slider: a line of text plus enough for the thumb
    /// to draw. Measured rather than written down, so it follows the font and the
    /// display scaling instead of being right on one machine.
    /// </summary>
    private int SliderHeight()
        => Math.Max(24, TextRenderer.MeasureText("大小", Font).Height + 8);

    /// <summary>A checkbox with the indent and breathing room the rest of the column has.</summary>
    private static Control Check(CheckBox box)
    {
        box.AutoSize = true;
        box.Margin = new Padding(2, 4, 0, 4);
        return box;
    }

    private Control BuildSizeRow()
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 8, 0, 0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Everything in this row is anchored left-only, which in a table cell
        // means "centre me vertically". Combined with the slider being trimmed to
        // roughly the height of a line of text, the three read as one line.
        var label = new Label
        {
            Text = "大小",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0)
        };

        _width.Minimum = 160;
        _width.Maximum = 640;
        _width.SmallChange = 8;
        _width.LargeChange = 40;
        // No ticks: they cost height and look dated, and the exact number is
        // spelled out beside the slider anyway.
        _width.TickStyle = TickStyle.None;
        // A TrackBar left to itself is about 45 pixels tall — two and a half
        // times the text beside it — and no amount of alignment makes a control
        // that tall look like it is on the same line. Without ticks the slider
        // needs only its thumb, so the height is taken down to the height of a
        // line of text and the row stops looking like two rows.
        _width.AutoSize = false;
        _width.Height = SliderHeight();
        _width.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _width.Margin = new Padding(0);
        _width.ValueChanged += (_, _) =>
        {
            _widthValue.Text = $"{_width.Value} px";
            Apply(p => p.CalendarWidth = _width.Value);
        };

        _widthValue.AutoSize = true;
        _widthValue.Anchor = AnchorStyles.Left;
        _widthValue.ForeColor = SystemColors.GrayText;
        _widthValue.Margin = new Padding(12, 0, 0, 0);
        // Reserved at the width of the longest value it will ever show, so the
        // slider does not shuffle sideways as the number grows a digit.
        _widthValue.MinimumSize = new Size(TextRenderer.MeasureText("640 px", Font).Width, 0);

        row.Controls.Add(label, 0, 0);
        row.Controls.Add(_width, 1, 0);
        row.Controls.Add(_widthValue, 2, 0);
        return row;
    }

    private void OnCityPicked(object? sender, EventArgs e)
    {
        if (_loading) return;
        int index = _results.SelectedIndex;
        if (index < 0 || index >= _choices.Count) return;
        var choice = _choices[index];
        Apply(p => p.CalendarCity = choice is CalendarChoice.Fixed place
            ? CalendarChoice.Encode(place.Name, place.Latitude, place.Longitude)
            : Cities.CurrentId);
        ShowChosenCity();
    }

    private void LoadValues()
    {
        _visible.Checked = _preferences.CalendarVisible;
        _onTop.Checked = _preferences.CalendarAlwaysOnTop;
        _width.Value = (int)Math.Clamp(_preferences.CalendarWidth, _width.Minimum, _width.Maximum);
        _widthValue.Text = $"{_width.Value} px";
        // The registry is the truth here, not the settings file: the entry can be
        // switched off in Task Manager without this app ever hearing about it.
        _login.Checked = LaunchAtLogin.Enabled;
        ShowChosenCity();
        FillResults();
    }

    private void ShowChosenCity()
        => _city.Text = "当前：" + CalendarChoice.Decode(_preferences.CalendarCity).DisplayName;

    /// <summary>
    /// The shortlist until something is typed, then the table. Twenty results is
    /// the cap — it is a shortcut, not a directory.
    /// </summary>
    private void FillResults()
    {
        _results.BeginUpdate();
        _results.Items.Clear();
        _choices.Clear();

        string query = _search.Text.Trim();
        if (query.Length == 0)
        {
            _results.Items.Add("当前位置（按 IP 定位）");
            _choices.Add(new CalendarChoice.Here());
            foreach (var group in Cities.Groups)
            {
                foreach (var city in group.Cities)
                {
                    _results.Items.Add($"{city.Name}　{group.Title}");
                    _choices.Add(new CalendarChoice.Fixed(city.Name, city.Latitude, city.Longitude));
                }
            }
        }
        else
        {
            foreach (var place in _places.Search(query))
            {
                _results.Items.Add($"{place.Name}　{place.Detail}");
                _choices.Add(new CalendarChoice.Fixed(place.Name, place.Latitude, place.Longitude));
            }
            if (_results.Items.Count == 0) _results.Items.Add("没有找到这个地方");
        }

        _results.EndUpdate();
    }

    private void Apply(Action<Preferences> change)
    {
        if (_loading) return;
        change(_preferences);
        _preferences.Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Closing hides rather than disposes: the tray menu opens this again, and a
    /// window that has to be rebuilt each time loses the search box's contents
    /// for no reason.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>Re-reads the preferences, for when the tray menu changed one.</summary>
    public void Reload()
    {
        _loading = true;
        LoadValues();
        _loading = false;
    }
}
