using System.Buffers.Binary;
using System.IO.Compression;

namespace Mona.Core.Imaging;

/// <summary>
/// A straight-alpha RGBA8 bitmap, row-major with y running down.
/// </summary>
public sealed class Bitmap32
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Four bytes per pixel, R G B A, straight (not premultiplied).</summary>
    public byte[] Pixels { get; }

    public Bitmap32(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public Bitmap32(int width, int height, byte[] pixels)
    {
        if (pixels.Length < width * height * 4)
            throw new ArgumentException("pixel buffer too small", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

/// <summary>
/// Just enough PNG to read the art pack and write a sticker.
///
/// Written rather than taken off the shelf because the alternative managed
/// decoders come with either a native blob or a licence to think about, and the
/// whole art pack is 8-bit non-interlaced — the one case a couple of hundred
/// lines covers completely. .NET has had zlib in the box since 6, so the only
/// real work here is the five filter types.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    public static Bitmap32 Decode(string path) => Decode(File.ReadAllBytes(path));

    public static Bitmap32 Decode(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        var idat = new MemoryStream();

        int offset = 8;
        while (offset + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            int body = offset + 8;
            if (length < 0 || body + length > data.Length)
                throw new InvalidDataException($"truncated chunk {type}");

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body + 4, 4));
                    bitDepth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                    break;
                case "PLTE":
                    palette = data.AsSpan(body, length).ToArray();
                    break;
                case "tRNS":
                    transparency = data.AsSpan(body, length).ToArray();
                    break;
                case "IDAT":
                    idat.Write(data, body, length);
                    break;
                case "IEND":
                    offset = data.Length;
                    break;
            }
            offset = body + length + 4; // + CRC
        }

        if (width <= 0 || height <= 0) throw new InvalidDataException("no IHDR");
        if (interlace != 0) throw new NotSupportedException("interlaced PNG");
        if (bitDepth != 8) throw new NotSupportedException($"bit depth {bitDepth}");

        int channels = colorType switch
        {
            0 => 1,  // grey
            2 => 3,  // rgb
            3 => 1,  // palette index
            4 => 2,  // grey + alpha
            6 => 4,  // rgba
            _ => throw new NotSupportedException($"colour type {colorType}")
        };

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = width * channels;
        var raw = new byte[checked((stride + 1) * height)];
        ReadExactly(inflate, raw);

        var pixels = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            byte filter = raw[rowStart];
            Buffer.BlockCopy(raw, rowStart + 1, current, 0, stride);
            Unfilter(filter, current, previous, channels);

            int outRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = x * channels;
                int d = outRow + x * 4;
                switch (colorType)
                {
                    case 0:
                        pixels[d] = pixels[d + 1] = pixels[d + 2] = current[s];
                        pixels[d + 3] = 255;
                        break;
                    case 2:
                        pixels[d] = current[s];
                        pixels[d + 1] = current[s + 1];
                        pixels[d + 2] = current[s + 2];
                        pixels[d + 3] = 255;
                        break;
                    case 3:
                    {
                        int index = current[s];
                        if (palette is null || index * 3 + 2 >= palette.Length)
                            throw new InvalidDataException("palette index out of range");
                        pixels[d] = palette[index * 3];
                        pixels[d + 1] = palette[index * 3 + 1];
                        pixels[d + 2] = palette[index * 3 + 2];
                        pixels[d + 3] = transparency is not null && index < transparency.Length
                            ? transparency[index] : (byte)255;
                        break;
                    }
                    case 4:
                        pixels[d] = pixels[d + 1] = pixels[d + 2] = current[s];
                        pixels[d + 3] = current[s + 1];
                        break;
                    default:
                        pixels[d] = current[s];
                        pixels[d + 1] = current[s + 1];
                        pixels[d + 2] = current[s + 2];
                        pixels[d + 3] = current[s + 3];
                        break;
                }
            }

            (previous, current) = (current, previous);
        }

        return new Bitmap32(width, height, pixels);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new InvalidDataException("PNG data ended early");
            read += n;
        }
    }

    /// <summary>PNG's five per-row predictors, undone in place.</summary>
    private static void Unfilter(byte filter, byte[] row, byte[] previous, int bpp)
    {
        int n = row.Length;
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bpp; i < n; i++) row[i] = (byte)(row[i] + row[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < n; i++) row[i] = (byte)(row[i] + previous[i]);
                break;
            case 3:
                for (int i = 0; i < n; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }
                break;
            case 4:
                for (int i = 0; i < n; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0;
                    int b = previous[i];
                    int c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int pick = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    row[i] = (byte)(row[i] + pick);
                }
                break;
            default:
                throw new InvalidDataException($"filter {filter}");
        }
    }

    // MARK: - writing

    public static void Encode(Bitmap32 image, string path)
    {
        using var file = File.Create(path);
        Encode(image, file);
    }

    /// <summary>Colour type 6, filter 0. Size is not the point here.</summary>
    public static void Encode(Bitmap32 image, Stream output)
    {
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), image.Height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        header[10] = 0;  // deflate
        header[11] = 0;  // adaptive filtering
        header[12] = 0;  // no interlace
        WriteChunk(output, "IHDR", header);

        int stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Buffer.BlockCopy(image.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream output, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);

        // The CRC covers the type then the body, as one run.
        uint crc = 0xFFFFFFFFu;
        crc = Crc32(typeBytes, crc);
        crc = Crc32(body, crc);
        crc ^= 0xFFFFFFFFu;

        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        output.Write(tail);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
