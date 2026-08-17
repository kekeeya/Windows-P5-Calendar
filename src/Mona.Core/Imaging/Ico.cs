using System.Buffers.Binary;

namespace Mona.Core.Imaging;

/// <summary>
/// Writes a multi-resolution Windows icon.
///
/// Written rather than shelled out to, because the machine this project is built
/// on has no icon tooling — and because the sizes have to come out of the same
/// box filter the tray icon uses, or the file icon and the tray icon would be
/// two different renderings of one drawing.
///
/// The payloads are PNG, which Windows has accepted inside an ICO since Vista and
/// which keeps the writer to a header and a directory. The alternative — a BMP
/// DIB with a padded AND mask and the rows upside down — is three times the code
/// for a format nothing here needs to read.
/// </summary>
public static class Ico
{
    /// <summary>
    /// The sizes Windows asks for. 16 through 48 are the ones actually seen —
    /// list view, details, taskbar, alt-tab — and 256 is what the extra-large
    /// view and the file properties dialog scale from.
    /// </summary>
    public static readonly int[] StandardSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>
    /// The rounded plate the silhouette sits on.
    ///
    /// A file icon cannot do what the tray icon does — the tray reads the
    /// taskbar's theme and inverts, while this one drawing has to hold up on a
    /// white Explorer window and a dark one both. A bare black cat disappears
    /// into the second. Putting it on its own light plate settles the background
    /// question from inside the icon, and the hairline border keeps the plate
    /// from vanishing into a white window.
    /// </summary>
    public readonly record struct Plate(
        byte Red, byte Green, byte Blue,
        byte BorderRed, byte BorderGreen, byte BorderBlue,
        /// <summary>Corner radius, as a fraction of the icon's side.</summary>
        double Radius = 0.22,
        /// <summary>Gap between the plate and the icon's edge, as a fraction of the side.</summary>
        double Inset = 0.03,
        /// <summary>How much of the plate the artwork is allowed to cover.</summary>
        double Fill = 0.68);

    /// <summary>Light warm grey with a hairline edge — visible on white and on black.</summary>
    public static readonly Plate DefaultPlate = new(0xF2, 0xF1, 0xEE, 0xC9, 0xC7, 0xC2);

    /// <summary>
    /// Renders <paramref name="source"/> at every size and writes the icon.
    ///
    /// <paramref name="tone"/> paints the shape: the artwork carries its outline
    /// in the alpha channel and its colour says nothing, exactly as it does for
    /// the tray.
    /// </summary>
    public static void Write(Bitmap32 source, string path, byte tone = 0,
                             int[]? sizes = null, Plate? plate = null)
    {
        sizes ??= StandardSizes;

        var payloads = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var buffer = new MemoryStream();
            Png.Encode(Render(source, size, tone, plate), buffer);
            payloads.Add(buffer.ToArray());
        }

        using var file = File.Create(path);

        // ICONDIR
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header[..2], 0);            // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(2, 2), 1);     // 1 = icon
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), (ushort)sizes.Length);
        file.Write(header);

        // One ICONDIRENTRY each, then the payloads back to back.
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            var entry = new byte[16];
            // 256 is written as 0: the field is one byte and 256 does not fit.
            entry[0] = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            entry[1] = entry[0];
            entry[2] = 0;   // no colour palette
            entry[3] = 0;   // reserved
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4, 2), 1);    // planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6, 2), 32);   // bits per pixel
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8, 4), payloads[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12, 4), offset);
            file.Write(entry);
            offset += payloads[i].Length;
        }

        foreach (var payload in payloads) file.Write(payload);
    }

    /// <summary>
    /// One size of the icon: the plate, then the silhouette on top of it.
    ///
    /// Public because it is worth looking at before it is buried inside an
    /// executable — <c>Mona.Parity --icon</c> writes the same renderings out as
    /// a contact sheet.
    /// </summary>
    public static Bitmap32 Render(Bitmap32 source, int size, byte tone = 0, Plate? plate = null)
    {
        var image = new Bitmap32(size, size);
        var crop = TrayFrame.AlphaBounds([source]);

        if (plate is null)
        {
            var bare = TrayFrame.Alpha(source, size, crop);
            for (int i = 0; i < size * size; i++)
            {
                image.Pixels[i * 4] = tone;
                image.Pixels[i * 4 + 1] = tone;
                image.Pixels[i * 4 + 2] = tone;
                image.Pixels[i * 4 + 3] = bare[i];
            }
            return image;
        }

        var style = plate.Value;
        double side = size;
        double inset = side * style.Inset;
        double radius = side * style.Radius;
        // A hairline that stays a hairline: one pixel at the sizes anyone looks
        // at closely, and proportional once the icon is large.
        double border = Math.Max(1.0, side / 64.0);

        var outer = RoundedCoverage(size, inset, radius);
        var inner = RoundedCoverage(size, inset + border, Math.Max(0, radius - border));

        // The artwork, drawn small enough to sit inside the plate with air around
        // it, then centred.
        //
        // Small icons get less of that air. The margin is what makes a large icon
        // look composed, but at sixteen pixels it is spent on the two or three
        // pixels the cat most needs — and sixteen is the size that actually gets
        // looked at, in a list view or on the taskbar.
        double fill = size <= 24 ? style.Fill + 0.14
                    : size <= 48 ? style.Fill + 0.07
                    : style.Fill;
        int art = Math.Max(1, (int)Math.Round(side * Math.Min(fill, 0.92), MidpointRounding.AwayFromZero));
        var glyph = TrayFrame.Alpha(source, art, crop);
        int offset = (size - art) / 2;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                double plateAlpha = outer[i];
                if (plateAlpha <= 0) continue;

                // Border where the outer shape covers and the inner one does not.
                double edge = Math.Clamp(plateAlpha - inner[i], 0, 1);
                double r = style.Red * (1 - edge) + style.BorderRed * edge;
                double g = style.Green * (1 - edge) + style.BorderGreen * edge;
                double b = style.Blue * (1 - edge) + style.BorderBlue * edge;

                int gx = x - offset, gy = y - offset;
                if (gx >= 0 && gy >= 0 && gx < art && gy < art)
                {
                    double a = glyph[gy * art + gx] / 255.0;
                    if (a > 0)
                    {
                        r = tone * a + r * (1 - a);
                        g = tone * a + g * (1 - a);
                        b = tone * a + b * (1 - a);
                    }
                }

                image.Pixels[i * 4] = Round(r);
                image.Pixels[i * 4 + 1] = Round(g);
                image.Pixels[i * 4 + 2] = Round(b);
                image.Pixels[i * 4 + 3] = Round(plateAlpha * 255);
            }
        }
        return image;
    }

    private static byte Round(double value)
        => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>
    /// Coverage of a rounded square, antialiased from its signed distance.
    ///
    /// The distance to a rounded rectangle has a closed form, and turning it into
    /// coverage with a one-pixel ramp gives cleaner corners than supersampling
    /// does at sixteen pixels — where the whole corner is barely three pixels
    /// across and every sample counts.
    /// </summary>
    private static double[] RoundedCoverage(int size, double inset, double radius)
    {
        var coverage = new double[size * size];
        double centre = size / 2.0;
        double half = centre - inset;
        if (half <= 0) return coverage;
        radius = Math.Clamp(radius, 0, half);

        double straight = half - radius;
        for (int y = 0; y < size; y++)
        {
            double py = Math.Abs(y + 0.5 - centre);
            for (int x = 0; x < size; x++)
            {
                double px = Math.Abs(x + 0.5 - centre);
                double dx = Math.Max(px - straight, 0);
                double dy = Math.Max(py - straight, 0);
                double distance = Math.Sqrt(dx * dx + dy * dy) - radius;
                coverage[y * size + x] = Math.Clamp(0.5 - distance, 0, 1);
            }
        }
        return coverage;
    }
}
