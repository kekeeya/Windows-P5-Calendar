namespace Mona.Core.Imaging;

/// <summary>
/// Draws an asset into a coverage map under an affine map, reading alpha only.
///
/// The art pack's pieces arrive around 1000×1200 and land on the canvas at
/// roughly a sixth of that, so this is a downscale by six in both directions and
/// the filter has to prefilter — a plain bicubic tap at the destination samples
/// one source pixel in thirty-six and aliases the glyph edges visibly. The kernel
/// therefore lives in <em>destination</em> space: the output pixel is the
/// weighted integral of the source over the kernel's support around it, which is
/// what any good downscaler approximates.
///
/// The kernel is a tent of radius one. That is not an aesthetic preference — it
/// was measured against the renderer this artwork was designed on, over the real
/// pieces at the scales and rotations the layout table asks for, and it came out
/// closest. Several other kernels were tried and are not kept here: a filter with
/// nothing left to compare it against is a knob nobody can turn correctly.
///
/// Only alpha is read. The layer's colour is a property of the layer, not of the
/// piece.
/// </summary>
public static class Sampler
{
    /// <summary>Half-width of the tent, in destination pixels.</summary>
    private const double Radius = 1.0;

    /// <summary>
    /// Adds one piece's coverage into <paramref name="coverage"/>.
    ///
    /// The six numbers are the layout table's, already scaled to the render size:
    /// <c>canvasX = a·px + b·py + tx</c>, <c>canvasY = c·px + d·py + ty</c>, with
    /// the asset's own pixels measured y-down from its top-left. That is the
    /// table's native convention, so nothing is flipped on the way in.
    /// </summary>
    public static void Accumulate(float[] coverage, int width, int height,
                                  Bitmap32 source,
                                  double a, double b, double c, double d,
                                  double tx, double ty)
    {
        double det = a * d - b * c;
        if (Math.Abs(det) < 1e-12) return;

        // Inverse of the 2×2, so a destination point can be asked which source
        // pixel it came from.
        double ia = d / det, ib = -b / det, ic = -c / det, id = a / det;

        int sw = source.Width, sh = source.Height;

        // The piece's own rectangle, mapped out to a destination bounding box.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        Span<(double px, double py)> corners = stackalloc (double, double)[4]
        {
            (0, 0), (sw, 0), (0, sh), (sw, sh)
        };
        foreach (var (px, py) in corners)
        {
            double x = a * px + b * py + tx;
            double y = c * px + d * py + ty;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        int x0 = Math.Max(0, (int)Math.Floor(minX - Radius - 1));
        int x1 = Math.Min(width - 1, (int)Math.Ceiling(maxX + Radius + 1));
        int y0 = Math.Max(0, (int)Math.Floor(minY - Radius - 1));
        int y1 = Math.Min(height - 1, (int)Math.Ceiling(maxY + Radius + 1));
        if (x1 < x0 || y1 < y0) return;

        // How many source pixels one destination pixel covers, which is what
        // decides how densely the footprint has to be sampled to stand in for an
        // integral. Six-by-six wants more than four taps; a piece drawn near 1:1
        // wants barely any.
        double sourcePerDest = Math.Sqrt(Math.Abs(1.0 / det));
        int taps = (int)Math.Ceiling(sourcePerDest * 2 * Radius);
        taps = Math.Clamp(taps, 2, 16);

        // Sample positions and weights are the same for every pixel — the map is
        // affine — so they are worked out once.
        var offsets = new double[taps];
        var weights = new double[taps];
        double step = 2 * Radius / taps;
        double total = 0;
        for (int i = 0; i < taps; i++)
        {
            offsets[i] = -Radius + (i + 0.5) * step;
            weights[i] = Weight(offsets[i]);
            total += weights[i];
        }
        if (total <= 0) return;

        var pixels = source.Pixels;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double accumulated = 0, mass = 0;
                for (int j = 0; j < taps; j++)
                {
                    double sampleY = y + 0.5 + offsets[j];
                    double wj = weights[j];
                    for (int i = 0; i < taps; i++)
                    {
                        double sampleX = x + 0.5 + offsets[i];
                        double weight = wj * weights[i];
                        // Back to the asset's own pixel grid.
                        double ux = sampleX - tx, uy = sampleY - ty;
                        double px = ia * ux + ib * uy;
                        double py = ic * ux + id * uy;
                        accumulated += weight * SampleAlpha(pixels, sw, sh, px, py);
                        mass += weight;
                    }
                }
                if (mass <= 0) continue;
                float value = Quantise((float)(accumulated / mass));
                if (value <= 0) continue;
                int index = y * width + x;
                // The union inside a layer is min(1, a + b) — a saturating add,
                // not the multiplicative one. Two whites that merely touch each
                // cover half the boundary pixel; 1-(1-a)(1-b) gives 0.75 there
                // and leaves a quarter-transparent hairline down the join.
                float sum = coverage[index] + value;
                coverage[index] = sum > 1f ? 1f : sum;
            }
        }
    }

    /// <summary>The tent: full weight at the centre, nothing at a pixel out.</summary>
    private static double Weight(double t)
    {
        double x = Math.Abs(t);
        return x < Radius ? Radius - x : 0;
    }

    /// <summary>
    /// One piece's coverage, rounded to the 1/255 steps a drawing context would
    /// have stored it in.
    ///
    /// Per piece, not once at the end, because that is where the renderer this
    /// artwork was designed on rounds: it draws each piece into an 8-bit context
    /// and the saturating add happens between values that are already whole
    /// counts. A single count is enough to put a pixel the other side of the 0.5
    /// the morphology thresholds at, which then becomes a whole flipped pixel.
    /// </summary>
    private static float Quantise(float value)
        => MathF.Round(value * 255f, MidpointRounding.AwayFromZero) * (1f / 255f);

    /// <summary>
    /// Bilinear on the source.
    ///
    /// Sample positions are pixel centres, so an asset pixel <c>(i, j)</c> has its
    /// centre at <c>(i + 0.5, j + 0.5)</c> in the coordinates the layout table
    /// uses.
    ///
    /// Two separate things decide what happens at the border, and conflating them
    /// puts half a count of coverage all round every piece. A sample outside the
    /// artwork's own rectangle is outside what was drawn at all and contributes
    /// nothing. A sample inside the rectangle but within half a pixel of its edge
    /// has no neighbour to interpolate towards, and there the border pixel
    /// repeats — which is why an asset with solid pixels on its edge comes out
    /// solid there rather than half-covered.
    /// </summary>
    private static double SampleAlpha(byte[] pixels, int w, int h, double px, double py)
    {
        if (px < 0 || py < 0 || px > w || py > h) return 0;

        double fx = px - 0.5, fy = py - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double dx = fx - x0, dy = fy - y0;

        double a00 = At(pixels, w, h, x0, y0);
        double a10 = At(pixels, w, h, x0 + 1, y0);
        double a01 = At(pixels, w, h, x0, y0 + 1);
        double a11 = At(pixels, w, h, x0 + 1, y0 + 1);

        double top = a00 + (a10 - a00) * dx;
        double bottom = a01 + (a11 - a01) * dx;
        return top + (bottom - top) * dy;
    }

    private static double At(byte[] pixels, int w, int h, int x, int y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        return pixels[(y * w + x) * 4 + 3] * (1.0 / 255.0);
    }
}
