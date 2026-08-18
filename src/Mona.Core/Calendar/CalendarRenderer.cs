using Mona.Core.Imaging;

namespace Mona.Core.Calendar;

/// <summary>
/// Draws the date sticker by stacking the design's five layers.
///
/// Bottom to top: every white backing, the weather icon, every black plate, every
/// white glyph, then the lettering. Not piece by piece — stacking whole pieces
/// always loses one of them, because the weekday tab and the date overlap and
/// whichever goes second buries the other. Layer by layer neither can: no backing
/// is ever above a glyph, and two whites that meet are the same white, so they
/// simply become one shape. That is what the reference stickers do.
///
/// Where each piece goes is not worked out here — it is read out of
/// <c>cal-layout.json</c>. See <see cref="CalendarLayout"/> for why.
/// </summary>
public sealed class CalendarRenderer
{
    private readonly CalendarArt _art;

    public CalendarRenderer(CalendarArt art) => _art = art;

    public double Aspect => _art.Layout?.Aspect ?? 640.0 / 500.0;

    public Bitmap32? Render(CalendarContent content, double width, double scale)
        => RenderFrames(content, [content.Frame], width, scale).FirstOrDefault();

    /// <summary>
    /// Several weather frames of the same sticker at once.
    ///
    /// Only the bottom two layers have anything to do with the weather — the icon
    /// and the white square under it. The black plates, the white glyphs and the
    /// lettering are identical in all three, and those carry the whole morphology
    /// bill (a flood fill and two separable closes each). Rendering the frames one
    /// at a time paid that three times over.
    /// </summary>
    public List<Bitmap32> RenderFrames(CalendarContent content, IReadOnlyList<int> frames,
                                       double width, double scale)
    {
        var output = new List<Bitmap32>();
        var layout = _art.Layout;
        if (layout is null || frames.Count == 0) return output;

        double k = width * scale / layout.CanvasWidth;
        // Away from zero throughout. .NET rounds halves to even by default,
        // which is not the convention the rest of this pipeline uses.
        int w = (int)Math.Round(layout.CanvasWidth * k, MidpointRounding.AwayFromZero);
        int h = (int)Math.Round(layout.CanvasHeight * k, MidpointRounding.AwayFromZero);
        if (w <= 8 || h <= 8) return output;

        string day = content.Day.ToString();
        string variant = day.Length >= 2 ? "d2" : "d1";
        string week = CalendarArt.WeekKey(content.Weekday);

        var put = new Dictionary<string, CalendarLayout.Entry>();
        if (layout.Placements.Date.TryGetValue($"{content.Month}-{content.Day}", out var date))
            foreach (var (name, entry) in date) put[name] = entry;
        if (layout.Placements.Week.TryGetValue($"{variant}|{week}", out var weekEntry))
            put["week"] = weekEntry;
        if (layout.Placements.Status.TryGetValue($"{variant}|{content.Slot.Key()}", out var statusEntry))
            put["status"] = statusEntry;
        if (put.Count == 0) return output;

        var shared = new Dictionary<int, Field>();

        foreach (int raw in frames)
        {
            int frame = Math.Clamp(raw, 1, 3);
            // A piece the table has no row for simply is not drawn — which is
            // what should happen for, say, the tens digit of a one-digit month.
            put.Remove("weather");
            if (layout.Placements.Weather.TryGetValue(
                    $"{variant}|{week}|{content.Weather.Key()}|{frame}", out var weatherEntry))
                put["weather"] = weatherEntry;

            // The card is four corners rather than artwork; it comes along with
            // the weather placement because it is sized and turned off the icon.
            Field? card = null;
            if (put.TryGetValue("weather", out var withCard) && withCard.Card is { Length: 4 })
            {
                var corners = new (double, double)[4];
                for (int i = 0; i < 4; i++)
                    corners[i] = (withCard.Card[i][0] * k, withCard.Card[i][1] * k);
                card = CalendarRaster.Quad(corners, w, h);
            }

            var rgb = new float[w * h];
            var alpha = new float[w * h];

            for (int index = 0; index < layout.Stack.Length; index++)
            {
                var layer = layout.Stack[index];
                bool movesWithWeather = layer.Items.Any(item => item.Length > 0 && item[0] == "weather");

                Field coverage;
                if (!movesWithWeather && shared.TryGetValue(index, out var cached))
                {
                    coverage = cached;
                }
                else
                {
                    coverage = Finish(layer, put, card, layout, k, w, h);
                    if (!movesWithWeather) shared[index] = coverage;
                }
                if (coverage.IsEmpty) continue;

                float tone = layer.White ? 1f : 0f;
                var values = coverage.Values;
                for (int i = 0; i < w * h; i++)
                {
                    float c = values[i];
                    if (c <= 0) continue;
                    rgb[i] = tone * c + rgb[i] * (1 - c);
                    alpha[i] = c + alpha[i] * (1 - c);
                }
            }

            output.Add(Compose(rgb, alpha, w, h));
        }

        return output;
    }

    /// <summary>
    /// One layer's finished coverage: the pieces drawn, then the fills and seals
    /// the design asks of that particular layer.
    /// </summary>
    private Field Finish(CalendarLayout.Layer layer,
                         Dictionary<string, CalendarLayout.Entry> put,
                         Field? card, CalendarLayout layout,
                         double k, int w, int h)
    {
        var coverage = Coverage(layer, put, card, layout, k, w, h);
        if (!layer.White) FillBetweenPlates(coverage, layer, put, layout, k, w, h);
        if (coverage.IsEmpty) return coverage;

        var parts = new HashSet<string>();
        foreach (var item in layer.Items) if (item.Length > 1) parts.Add(item[1]);

        // The bottom white is one continuous sheet in the design; anything
        // enclosed by it is sheet too. Without this the seam where the weather
        // card meets the weekday tab's backing shows as a hairline of wallpaper —
        // too wide for the square brush below to close, and open at one end so it
        // is not a hole in the layer's own sense.
        if (layer.White && (parts.Contains("under") || parts.Contains("card")))
        {
            var sheet = CalendarRaster.Holes(coverage.Mask(), w, h);
            coverage.Raise(CalendarRaster.Grow(sheet, w, h));
        }

        // Sealed only where the layer has backings. A lettering layer must not be:
        // the white slots inside a Chinese character are two or three pixels wide
        // and the brush would weld them shut.
        if (parts.Contains("plate") || parts.Contains("under") || parts.Contains("ink"))
        {
            int side = layer.White ? layout.Seal.GetValueOrDefault("white", 5)
                                   : layout.Seal.GetValueOrDefault("black", 3);
            // The brush is a radius either side of the pixel, so it has to stay
            // odd when the canvas is scaled up — rounding the whole side instead
            // lands on 6 at 2x and seals half a pixel wider than the offline
            // renderer does.
            int r = Math.Max(1, (int)Math.Round(side / 2 * k, MidpointRounding.AwayFromZero));
            var solid = coverage.Mask();
            var sealed_ = CalendarRaster.CloseBox(solid, w, h, 2 * r + 1);
            var gained = new byte[solid.Length];
            for (int i = 0; i < solid.Length; i++)
                if (sealed_[i] != 0 && solid[i] == 0) gained[i] = 1;
            coverage.Raise(gained);
        }

        return coverage;
    }

    private Field Coverage(CalendarLayout.Layer layer,
                           Dictionary<string, CalendarLayout.Entry> put,
                           Field? card, CalendarLayout layout,
                           double k, int w, int h)
    {
        var pieces = new List<(Bitmap32, double[])>();
        Field? withCard = null;

        foreach (var item in layer.Items)
        {
            if (item.Length < 2) continue;
            string name = item[0], part = item[1];
            if (part == "card") { withCard = card; continue; }
            if (!put.TryGetValue(name, out var entry)) continue;
            var image = Asset(entry, part, layout);
            if (image is null) continue;
            pieces.Add((image, Scaled(entry, k)));
        }

        var field = CalendarRaster.Rasterise(pieces, w, h);
        if (withCard is not null) field.Add(withCard);
        return field;
    }

    /// <summary>
    /// The pockets the four black plates ring — month, slash, day and weekday —
    /// filled in.
    ///
    /// Their blacks are one shape in the design, so any white the four of them
    /// close around is black too. Bridging first, and only when it actually joins
    /// separate plates: a close that merely fattens one blob is a fatter plate,
    /// not a bridge, and the design's plate is not fatter.
    /// </summary>
    private void FillBetweenPlates(Field coverage, CalendarLayout.Layer layer,
                                   Dictionary<string, CalendarLayout.Entry> put,
                                   CalendarLayout layout, double k, int w, int h)
    {
        var pieces = new List<(Bitmap32, double[])>();
        foreach (var item in layer.Items)
        {
            if (item.Length < 2) continue;
            string name = item[0], part = item[1];
            if (part != "plate" && part != "ink") continue;
            if (!put.TryGetValue(name, out var entry)) continue;
            var image = Asset(entry, part, layout);
            if (image is null) continue;
            pieces.Add((image, Scaled(entry, k)));
        }
        if (pieces.Count == 0) return;

        var solid = CalendarRaster.Rasterise(pieces, w, h).Mask();
        bool any = false;
        foreach (byte b in solid) if (b != 0) { any = true; break; }
        if (!any) return;

        // Count first. Bridging costs two full distance transforms — by far the
        // most expensive thing in the whole composite — and it can only help if
        // the plates are in more than one piece to begin with. With the drawn
        // artwork they nearly always already overlap into one, so this skips it
        // outright rather than computing a close and then discovering it changed
        // nothing.
        int before = CalendarRaster.Pieces(solid, w, h);
        if (before > 1)
        {
            float radius = (float)Math.Max(2, Math.Round(layout.BridgePx * k, MidpointRounding.AwayFromZero));
            var bridged = CalendarRaster.Close(solid, w, h, radius);
            if (CalendarRaster.Pieces(bridged, w, h) < before)
            {
                var gained = new byte[solid.Length];
                for (int i = 0; i < solid.Length; i++)
                    if (bridged[i] != 0 && solid[i] == 0) gained[i] = 1;
                coverage.Raise(gained);
                solid = bridged;
            }
        }
        // Grown by a pixel: the ring where the plates fade out sits between two
        // blacks once the pocket is filled, and left partial it shows the white
        // sheet underneath as a hairline.
        var pocket = CalendarRaster.Holes(solid, w, h);
        coverage.Raise(CalendarRaster.Grow(pocket, w, h));
    }

    private Bitmap32? Asset(CalendarLayout.Entry entry, string part, CalendarLayout layout)
    {
        // `icon` is the weather, which is a single shape with no suffix.
        string suffix = part == "icon" ? "" : layout.Suffix.GetValueOrDefault(part, "");
        return _art.Image(entry.G + suffix);
    }

    /// <summary>
    /// The table's six numbers at the render size. They are all in the layout's
    /// own canvas, so all six scale by the same k — including the translation.
    /// </summary>
    private static double[] Scaled(CalendarLayout.Entry entry, double k)
    {
        var m = entry.Matrix;
        return [m[0] * k, m[1] * k, m[2] * k, m[3] * k, m[4] * k, m[5] * k];
    }

    /// <summary>
    /// The accumulated colour is premultiplied; a PNG-style straight-alpha image
    /// is not. Skipping the divide puts a grey rim right round the sticker: on the
    /// white outline's antialiased edge the coverage is a half, and a half stored
    /// straight is mid-grey rather than white at half opacity.
    /// </summary>
    private static Bitmap32 Compose(float[] rgb, float[] alpha, int w, int h)
    {
        var image = new Bitmap32(w, h);
        var pixels = image.Pixels;
        for (int i = 0; i < w * h; i++)
        {
            float a = alpha[i];
            byte v = a > 1e-4f
                ? (byte)Math.Clamp(MathF.Round(rgb[i] / a * 255f, MidpointRounding.AwayFromZero), 0, 255)
                : (byte)0;
            pixels[i * 4] = v;
            pixels[i * 4 + 1] = v;
            pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = (byte)Math.Clamp(MathF.Round(a * 255f, MidpointRounding.AwayFromZero), 0, 255);
        }
        return image;
    }
}
