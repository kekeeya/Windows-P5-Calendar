using Mona.Core.Imaging;

namespace Mona.Core.Calendar;

/// <summary>
/// A coverage map: one value per pixel, 0…1, row-major with y running down.
/// </summary>
public sealed class Field
{
    public int Width { get; }
    public int Height { get; }
    public float[] Values { get; }

    public Field(int width, int height)
    {
        Width = width;
        Height = height;
        Values = new float[Math.Max(0, width * height)];
    }

    public bool IsEmpty
    {
        get
        {
            foreach (float v in Values) if (v > 0) return false;
            return true;
        }
    }

    public byte[] Mask(float threshold = 0.5f)
    {
        var mask = new byte[Values.Length];
        for (int i = 0; i < Values.Length; i++) mask[i] = Values[i] > threshold ? (byte)1 : (byte)0;
        return mask;
    }

    /// <summary>
    /// <c>cov = max(cov, extra)</c> — how a filled hole or a sealed seam folds
    /// back into the layer.
    /// </summary>
    public void Raise(byte[] extra)
    {
        int n = Math.Min(Values.Length, extra.Length);
        for (int i = 0; i < n; i++) if (extra[i] != 0) Values[i] = 1f;
    }

    /// <summary><c>cov = min(1, cov + other)</c> — the union used inside a layer.</summary>
    public void Add(Field other)
    {
        int n = Math.Min(Values.Length, other.Values.Length);
        for (int i = 0; i < n; i++)
        {
            float sum = Values[i] + other.Values[i];
            Values[i] = sum > 1f ? 1f : sum;
        }
    }

    /// <summary>
    /// Rounds coverage to the 1/255 steps an 8-bit drawing context would have
    /// stored it in.
    ///
    /// Not cosmetic. The artwork was designed against a pipeline that draws each
    /// layer into an 8-bit surface, so every threshold downstream — the 0.5 in
    /// <see cref="Mask"/> above all — is meant to see quantised coverage. Keeping
    /// full float precision here decides a handful of edge pixels the other way,
    /// and the morphology turns each of those into a whole flipped pixel.
    /// </summary>
    public void Quantise()
    {
        for (int i = 0; i < Values.Length; i++)
        {
            float v = Values[i];
            if (v <= 0) { Values[i] = 0; continue; }
            // Away from zero, not to-even. .NET rounds halves to even by
            // default, which is not the convention the rest of this pipeline
            // uses; over a million pixels the difference does turn up.
            Values[i] = MathF.Round(v * 255f, MidpointRounding.AwayFromZero) * (1f / 255f);
        }
    }

    public Field Copy()
    {
        var copy = new Field(Width, Height);
        Array.Copy(Values, copy.Values, Values.Length);
        return copy;
    }
}

/// <summary>
/// The bitmap work the calendar's five-layer composite needs: filling enclosed
/// holes, counting how many separate pieces a shape is in, and closing with an
/// exact disc or an exact square.
///
/// All of it is exact integer or well-defined float arithmetic — no sampling, no
/// tolerances — so it produces the same answer on any machine.
/// </summary>
public static class CalendarRaster
{
    /// <summary>Draws pieces into one coverage map, reading alpha only.</summary>
    public static Field Rasterise(IReadOnlyList<(Bitmap32 image, double[] m)> pieces,
                                  int w, int h)
    {
        var field = new Field(w, h);
        if (w <= 0 || h <= 0 || pieces.Count == 0) return field;
        foreach (var (image, m) in pieces)
            Sampler.Accumulate(field.Values, w, h, image,
                               m[0], m[1], m[2], m[3], m[4], m[5]);
        field.Quantise();
        return field;
    }

    /// <summary>
    /// Fills a convex quadrilateral — the slanted white square the weather icon
    /// sits on. Four corners rather than artwork because the four sharp corners
    /// are the whole shape, and a bitmap of it arrives with a rounded halo and a
    /// crooked crop.
    ///
    /// This edge is one of the few the design leaves showing against the
    /// wallpaper, so it is worth more than a coarse supersample: pixels the
    /// shape misses or swallows whole are settled exactly by their distance to
    /// the four edges, and only the ones an edge actually crosses are sampled —
    /// densely, at 16×16.
    /// </summary>
    public static Field Quad(IReadOnlyList<(double x, double y)> points, int w, int h)
    {
        var field = new Field(w, h);
        if (points.Count != 4 || w <= 0 || h <= 0) return field;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in points)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        int x0 = Math.Max(0, (int)Math.Floor(minX) - 1);
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(maxX) + 1);
        int y0 = Math.Max(0, (int)Math.Floor(minY) - 1);
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(maxY) + 1);
        if (x1 < x0 || y1 < y0) return field;

        // The four edges as unit normals pointing inwards, so a signed distance
        // is in pixels and comparable with half a pixel diagonal.
        double sign = 0;
        for (int i = 0; i < 4 && sign == 0; i++)
        {
            var (ax, ay) = points[i];
            var (bx, by) = points[(i + 1) % 4];
            var (cx, cy) = points[(i + 2) % 4];
            sign = Math.Sign((bx - ax) * (cy - ay) - (by - ay) * (cx - ax));
        }
        if (sign == 0) return field;

        var edges = new (double nx, double ny, double c)[4];
        for (int i = 0; i < 4; i++)
        {
            var (ax, ay) = points[i];
            var (bx, by) = points[(i + 1) % 4];
            double ex = bx - ax, ey = by - ay;
            double length = Math.Sqrt(ex * ex + ey * ey);
            if (length < 1e-9) return field;
            // Inward normal: rotate the edge and orient it by the winding.
            double nx = -ey / length * sign, ny = ex / length * sign;
            edges[i] = (nx, ny, -(nx * ax + ny * ay));
        }

        const double halfDiagonal = 0.7071067811865476;
        const int taps = 16;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double cx2 = x + 0.5, cy2 = y + 0.5;
                double nearest = double.MaxValue;
                foreach (var (nx, ny, c) in edges)
                    nearest = Math.Min(nearest, nx * cx2 + ny * cy2 + c);

                if (nearest <= -halfDiagonal) continue;              // clear of it
                if (nearest >= halfDiagonal)                          // swallowed whole
                {
                    field.Values[y * w + x] = 1f;
                    continue;
                }

                int inside = 0;
                for (int j = 0; j < taps; j++)
                {
                    double sy = y + (j + 0.5) / taps;
                    for (int i = 0; i < taps; i++)
                    {
                        double sx = x + (i + 0.5) / taps;
                        bool hit = true;
                        foreach (var (nx, ny, c) in edges)
                            if (nx * sx + ny * sy + c < 0) { hit = false; break; }
                        if (hit) inside++;
                    }
                }
                if (inside == 0) continue;
                field.Values[y * w + x] = inside / (float)(taps * taps);
            }
        }
        field.Quantise();
        return field;
    }

    /// <summary>
    /// Everything enclosed by <paramref name="solid"/> that is not solid itself.
    ///
    /// Flood from the border through the background: what the flood cannot reach
    /// is inside. This is what blackens the pockets the four plates ring — month,
    /// slash, day and weekday are drawn as separate pieces, and in the design
    /// their blacks are one shape with no white showing through.
    /// </summary>
    public static byte[] Holes(byte[] solid, int w, int h)
    {
        int n = w * h;
        var seen = new byte[n];
        var stack = new int[n];
        int top = 0;

        void Push(int i)
        {
            if (solid[i] == 0 && seen[i] == 0) { seen[i] = 1; stack[top++] = i; }
        }

        for (int x = 0; x < w; x++)
        {
            Push(x);
            Push((h - 1) * w + x);
        }
        for (int y = 0; y < h; y++)
        {
            Push(y * w);
            Push(y * w + w - 1);
        }
        while (top > 0)
        {
            int i = stack[--top];
            int x = i % w;
            if (x > 0) Push(i - 1);
            if (x < w - 1) Push(i + 1);
            if (i >= w) Push(i - w);
            if (i < n - w) Push(i + w);
        }
        for (int i = 0; i < n; i++)
            seen[i] = (solid[i] == 0 && seen[i] == 0) ? (byte)1 : (byte)0;
        return seen;
    }

    /// <summary>
    /// Grows a mask by one pixel in every direction.
    ///
    /// Used on a pocket before it is folded back into its layer. Whether a pixel
    /// is inside a pocket is decided on the mask, which thresholds coverage at a
    /// half — so the ring where the surrounding artwork fades from solid to
    /// nothing, coverage between a half and one, counts as boundary and keeps its
    /// partial value. Everywhere else that ring is the antialiased edge between
    /// two different colours and is exactly right; inside a pocket that is about
    /// to be filled it has the same colour on both sides and nothing left to
    /// antialias against, so it reads as a hairline of whatever lies under the
    /// layer. Growing the pocket over it removes the seam without moving any edge
    /// that still separates two colours.
    /// </summary>
    public static byte[] Grow(byte[] mask, int w, int h)
    {
        var output = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (mask[row + x] == 0) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        output[yy * w + xx] = 1;
                    }
                }
            }
        }
        return output;
    }

    /// <summary>
    /// How many separate pieces a shape is in. Only ever compared before and
    /// after a bridging close, to tell "this joined things up" from "this just
    /// fattened one blob".
    /// </summary>
    public static int Pieces(byte[] solid, int w, int h)
    {
        int n = w * h;
        var seen = new byte[n];
        var stack = new int[n];
        int count = 0;

        for (int start = 0; start < n; start++)
        {
            if (solid[start] == 0 || seen[start] != 0) continue;
            count++;
            seen[start] = 1;
            int top = 0;
            stack[top++] = start;
            while (top > 0)
            {
                int i = stack[--top];
                int x = i % w;
                if (x > 0 && solid[i - 1] != 0 && seen[i - 1] == 0) { seen[i - 1] = 1; stack[top++] = i - 1; }
                if (x < w - 1 && solid[i + 1] != 0 && seen[i + 1] == 0) { seen[i + 1] = 1; stack[top++] = i + 1; }
                if (i >= w && solid[i - w] != 0 && seen[i - w] == 0) { seen[i - w] = 1; stack[top++] = i - w; }
                if (i < n - w && solid[i + w] != 0 && seen[i + w] == 0) { seen[i + w] = 1; stack[top++] = i + w; }
            }
        }
        return count;
    }

    /// <summary>
    /// Squared euclidean distance to the nearest set pixel, by the two-pass
    /// separable transform. An exact disc of any radius comes out of one of
    /// these, which a repeated 3×3 brush does not — and the bridge radius is
    /// nineteen pixels at the design canvas, far too big to fake.
    /// </summary>
    public static float[] Distance2(byte[] from, int w, int h, bool invert = false)
    {
        int n = w * h;
        float far = (float)(w * w + h * h) * 4;
        var d = new float[n];
        int m = Math.Max(w, h);
        var f = new float[m];
        var dd = new float[m];
        var vpos = new int[m];
        var z = new float[m + 1];

        for (int i = 0; i < n; i++)
        {
            bool on = invert ? from[i] == 0 : from[i] != 0;
            d[i] = on ? 0 : far;
        }

        void Pass(int count)
        {
            int k = 0;
            vpos[0] = 0;
            z[0] = -far;
            z[1] = far;
            for (int q = 1; q < count; q++)
            {
                float s = 0;
                while (true)
                {
                    int p = vpos[k];
                    s = ((f[q] + q * q) - (f[p] + p * p)) / (2 * (q - p));
                    if (s <= z[k] && k > 0) k--; else break;
                }
                k++;
                vpos[k] = q;
                z[k] = s;
                z[k + 1] = far;
            }
            k = 0;
            for (int q = 0; q < count; q++)
            {
                while (z[k + 1] < q) k++;
                int p = vpos[k];
                dd[q] = (q - p) * (q - p) + f[p];
            }
        }

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) f[y] = d[y * w + x];
            Pass(h);
            for (int y = 0; y < h; y++) d[y * w + x] = dd[y];
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++) f[x] = d[row + x];
            Pass(w);
            for (int x = 0; x < w; x++) d[row + x] = dd[x];
        }
        return d;
    }

    /// <summary>Close with an exact disc: grow then shrink.</summary>
    public static byte[] Close(byte[] solid, int w, int h, float radius)
    {
        if (radius < 0.5f) return solid;
        int n = w * h;
        float r2 = radius * radius + 0.5f;

        var grown = new byte[n];
        var d1 = Distance2(solid, w, h);
        for (int i = 0; i < n; i++) grown[i] = d1[i] <= r2 ? (byte)1 : (byte)0;

        var outMask = new byte[n];
        var d2 = Distance2(grown, w, h, invert: true);
        for (int i = 0; i < n; i++) outMask[i] = d2[i] > r2 ? (byte)1 : (byte)0;
        return outMask;
    }

    /// <summary>
    /// Close with a square brush — the hairline seal between neighbouring
    /// plates. Separable, so it costs a fraction of the disc version.
    /// </summary>
    public static byte[] CloseBox(byte[] solid, int w, int h, int side)
    {
        if (side < 3 || w <= 0 || h <= 0) return solid;
        int r = side / 2;
        int n = w * h;
        var a = new byte[n];
        var mid = new byte[n];
        var result = new byte[n];

        void Run(byte[] src, byte[] dst, bool dilate)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte acc = dilate ? (byte)0 : (byte)1;
                    int lo = Math.Max(0, x - r), hi = Math.Min(w - 1, x + r);
                    if (dilate)
                    {
                        for (int xx = lo; xx <= hi; xx++)
                            if (src[row + xx] != 0) { acc = 1; break; }
                    }
                    else
                    {
                        // A window that runs off the edge is not full, so the
                        // erosion clears it. That is what stops a shape touching
                        // the canvas border from surviving a close as though the
                        // canvas continued past it.
                        if (lo > x - r || hi < x + r) acc = 0;
                        if (acc != 0)
                            for (int xx = lo; xx <= hi; xx++)
                                if (src[row + xx] == 0) { acc = 0; break; }
                    }
                    mid[row + x] = acc;
                }
            }
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int lo = Math.Max(0, y - r), hi = Math.Min(h - 1, y + r);
                for (int x = 0; x < w; x++)
                {
                    byte acc = dilate ? (byte)0 : (byte)1;
                    if (dilate)
                    {
                        for (int yy = lo; yy <= hi; yy++)
                            if (mid[yy * w + x] != 0) { acc = 1; break; }
                    }
                    else
                    {
                        if (lo > y - r || hi < y + r) acc = 0;
                        if (acc != 0)
                            for (int yy = lo; yy <= hi; yy++)
                                if (mid[yy * w + x] == 0) { acc = 0; break; }
                    }
                    dst[row + x] = acc;
                }
            }
        }

        Run(solid, a, true);
        Run(a, result, false);
        return result;
    }
}
