namespace Mona.Core.Imaging;

/// <summary>
/// The tray silhouette, reduced to the size a tray icon is drawn at.
///
/// Here rather than next to the icon-building code it serves, for the same
/// reason the calendar renderer is here: this is the part whose result can be
/// looked at without Windows, and a copy of it living in the Windows-only project
/// is a copy that stops agreeing with what ships. <c>Mona.Parity --tray</c> draws
/// a contact sheet through this very method.
/// </summary>
public static class TrayFrame
{
    /// <summary>The part of a canvas the artwork actually occupies.</summary>
    public readonly record struct Bounds(int X, int Y, int Width, int Height);

    /// <summary>
    /// The alpha bounding box across a whole set of frames at once.
    ///
    /// Whole set, not each frame on its own. Cropping every frame to its own
    /// artwork would re-centre the cat on each one and the run cycle would jitter
    /// in place instead of running; a shared box keeps whatever movement the
    /// drawings have relative to each other.
    ///
    /// It matters more than it sounds: the still icon arrives on a 1024-square
    /// canvas with the head in one corner of it, and scaling that whole square
    /// into sixteen pixels spends most of them on empty space and leaves the face
    /// as a smudge.
    /// </summary>
    public static Bounds AlphaBounds(IReadOnlyList<Bitmap32> frames, byte threshold = 8)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var frame in frames)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    if (frame.Pixels[((long)y * frame.Width + x) * 4 + 3] < threshold) continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
        }
        if (right < left || bottom < top)
        {
            var first = frames.Count > 0 ? frames[0] : new Bitmap32(1, 1);
            return new Bounds(0, 0, first.Width, first.Height);
        }
        return new Bounds(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// Alpha only, box-filtered to a square, cropped to where the artwork is.
    ///
    /// The frames are a few hundred pixels across and land at sixteen to
    /// thirty-two, so this is a reduction by an order of magnitude and averaging
    /// is the only thing that survives it — picking one source pixel in a hundred
    /// and seventy is noise, not a silhouette.
    ///
    /// The drawing keeps its own proportions and is centred in the square, so he
    /// does not stretch as the tray size changes with the display scaling.
    /// </summary>
    public static byte[] Alpha(Bitmap32 source, int size)
        => Alpha(source, size, AlphaBounds([source]));

    public static byte[] Alpha(Bitmap32 source, int size, Bounds crop)
    {
        var output = new byte[size * size];
        if (source.Width < 2 || source.Height < 2 || size <= 0) return output;
        if (crop.Width <= 0 || crop.Height <= 0) return output;

        double scale = Math.Min((double)size / crop.Width, (double)size / crop.Height);
        double drawnWidth = crop.Width * scale, drawnHeight = crop.Height * scale;
        double offsetX = (size - drawnWidth) / 2 - crop.X * scale;
        double offsetY = (size - drawnHeight) / 2 - crop.Y * scale;

        for (int y = 0; y < size; y++)
        {
            double sy0 = (y - offsetY) / scale, sy1 = (y + 1 - offsetY) / scale;
            int fromY = Math.Max(0, (int)Math.Floor(sy0));
            int toY = Math.Min(source.Height - 1, (int)Math.Ceiling(sy1) - 1);

            for (int x = 0; x < size; x++)
            {
                double sx0 = (x - offsetX) / scale, sx1 = (x + 1 - offsetX) / scale;
                int fromX = Math.Max(0, (int)Math.Floor(sx0));
                int toX = Math.Min(source.Width - 1, (int)Math.Ceiling(sx1) - 1);

                double total = 0, weight = 0;
                for (int j = fromY; j <= toY; j++)
                {
                    double coverY = Math.Min(sy1, j + 1) - Math.Max(sy0, j);
                    if (coverY <= 0) continue;
                    for (int i = fromX; i <= toX; i++)
                    {
                        double coverX = Math.Min(sx1, i + 1) - Math.Max(sx0, i);
                        if (coverX <= 0) continue;
                        double area = coverX * coverY;
                        total += area * source.Pixels[((long)j * source.Width + i) * 4 + 3];
                        weight += area;
                    }
                }
                output[y * size + x] = weight > 0
                    ? (byte)Math.Clamp(Math.Round(total / weight, MidpointRounding.AwayFromZero), 0, 255)
                    : (byte)0;
            }
        }
        return output;
    }

    /// <summary>
    /// The still icon — Mona's head — or null when it is not there.
    ///
    /// Null rather than a blank, because the caller has somewhere else to fall
    /// back to and an invisible tray icon is the worst of the three outcomes.
    /// </summary>
    public static Bitmap32? LoadStill(string path)
        => File.Exists(path) ? Png.Decode(path) : null;

}
