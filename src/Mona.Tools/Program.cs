using Mona.Core.Calendar;
using Mona.Core.Imaging;

namespace Mona.Tools;

/// <summary>
/// The asset chores, on any platform.
///
/// Everything here exists because artwork changes and the results have to be
/// checkable without Windows: the application icon is generated from the tray
/// drawing rather than drawn twice, a replacement silhouette has to be verified
/// before it turns into a solid rectangle, and the calendar has to be viewable
/// without starting the app.
///
///   dotnet run --project src/Mona.Tools -- icon
///   dotnet run --project src/Mona.Tools -- tray
///   dotnet run --project src/Mona.Tools -- inspect assets/Tray/MonaHead.png
///   dotnet run --project src/Mona.Tools -- render out.png 4 15 6 cloudy morning
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string root = FindRoot();
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        return verb switch
        {
            "icon" => Icon(root, args),
            "tray" => Tray(root, args),
            "inspect" => Inspect(args),
            "render" => Render(root, args),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            用法：
              icon [out.ico]                                   从托盘美术生成应用图标，并出一张预览
              tray [out.png]                                   托盘图标在 16/20/24/32 px 下的对照图
              inspect <file.png>                               这张图能不能安全地按任务栏反色
              render <out.png> <月> <日> <星期> <天气> <时段> [帧] [宽]
                                                               离线渲染一张日历贴纸
              星期：周日为 1　天气：sunny|cloudy|rain|snow　时段：dawn|morning|noon|afternoon|night
            """);
        return 2;
    }

    // MARK: - icon

    /// <summary>
    /// The application icon, generated from the same drawing the tray uses so the
    /// two cannot drift, plus a contact sheet on light and dark backgrounds —
    /// an icon buried in an executable is a poor place to first discover that it
    /// is illegible.
    /// </summary>
    private static int Icon(string root, string[] args)
    {
        var art = LoadTrayArt(root);
        if (art is null) return 1;

        string target = args.Length > 1 ? args[1] : Path.Combine(root, "assets", "Tray", "Mona.ico");
        Ico.Write(art, target, plate: Ico.DefaultPlate);
        Console.WriteLine($"{target}  {new FileInfo(target).Length / 1024} KB"
                        + $"  sizes {string.Join(", ", Ico.StandardSizes)}");

        string preview = Path.ChangeExtension(target, ".preview.png");
        Png.Encode(IconSheet(art), preview);
        Console.WriteLine(preview);
        return 0;
    }

    /// <summary>Every shipped size over white and over a dark window.</summary>
    private static Bitmap32 IconSheet(Bitmap32 source)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];
        const int pad = 16;
        int width = pad;
        foreach (int size in sizes) width += size + pad;
        int height = pad + 256 + pad + 256 + pad;

        var sheet = new Bitmap32(width, height);
        for (int y = 0; y < height; y++)
        {
            byte tone = y < pad + 256 + pad / 2 ? (byte)255 : (byte)32;
            for (int x = 0; x < width; x++)
            {
                long p = ((long)y * width + x) * 4;
                sheet.Pixels[p] = sheet.Pixels[p + 1] = sheet.Pixels[p + 2] = tone;
                sheet.Pixels[p + 3] = 255;
            }
        }

        foreach (int band in new[] { 0, 1 })
        {
            int left = pad;
            foreach (int size in sizes)
            {
                var icon = Ico.Render(source, size, 0, Ico.DefaultPlate);
                int top = pad + band * (256 + pad) + (256 - size);
                Draw(icon, sheet, left, top);
                left += size + pad;
            }
        }
        return sheet;
    }

    // MARK: - tray

    /// <summary>
    /// The tray icon at the sizes Windows asks for, on both taskbar colours,
    /// drawn through the reduction the app ships — so this is what the tray will
    /// show rather than an impression of it.
    /// </summary>
    private static int Tray(string root, string[] args)
    {
        var art = LoadTrayArt(root);
        if (art is null) return 1;

        int[] sizes = [16, 20, 24, 32];
        const int zoom = 6;
        const int pad = 4;
        var crop = TrayFrame.AlphaBounds([art]);

        string target = args.Length > 1 ? args[1] : Path.Combine(root, "assets", "Tray", "MonaHead.tray.png");
        int width = 0, height = pad;
        foreach (int size in sizes)
        {
            width = Math.Max(width, 2 * (size * zoom + pad) + pad);
            height += size * zoom + pad;
        }

        var sheet = new Bitmap32(width, height);
        int top = pad;
        foreach (int size in sizes)
        {
            var alpha = TrayFrame.Alpha(art, size, crop);
            // Dark taskbar on the left, light on the right — the app tints for
            // whichever it finds.
            foreach (var (column, background, tone) in new[]
                     { (0, (byte)32, (byte)255), (1, (byte)243, (byte)0) })
            {
                int left = pad + column * (size * zoom + pad);
                for (int y = 0; y < size * zoom; y++)
                {
                    for (int x = 0; x < size * zoom; x++)
                    {
                        double a = alpha[(y / zoom) * size + x / zoom] / 255.0;
                        byte value = (byte)Math.Round(tone * a + background * (1 - a),
                                                      MidpointRounding.AwayFromZero);
                        long p = ((long)(top + y) * width + left + x) * 4;
                        sheet.Pixels[p] = sheet.Pixels[p + 1] = sheet.Pixels[p + 2] = value;
                        sheet.Pixels[p + 3] = 255;
                    }
                }
            }
            top += size * zoom + pad;
        }

        Png.Encode(sheet, target);
        Console.WriteLine($"{target}  {string.Join(", ", sizes)} px，放大 {zoom}×，左深右浅");
        return 0;
    }

    // MARK: - inspect

    /// <summary>
    /// Whether a drawing can be used as a tray icon.
    ///
    /// The app paints the alpha channel in whatever colour the taskbar needs, so
    /// the artwork has to be a silhouette: one tone throughout, with the shape in
    /// the alpha. A picture tinted that way comes out a solid rectangle.
    /// </summary>
    private static int Inspect(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：inspect <file.png>");
            return 2;
        }
        var image = Png.Decode(args[1]);
        long pixels = (long)image.Width * image.Height;

        long clear = 0, opaque = 0, partial = 0;
        long dark = 0, mid = 0, light = 0, coloured = 0;
        for (long i = 0; i < pixels; i++)
        {
            long p = i * 4;
            byte a = image.Pixels[p + 3];
            if (a == 0) { clear++; continue; }
            if (a == 255) opaque++; else partial++;

            byte r = image.Pixels[p], g = image.Pixels[p + 1], b = image.Pixels[p + 2];
            if (r != g || g != b) coloured++;
            if (a != 255) continue;
            int luminance = (r + g + b) / 3;
            if (luminance < 64) dark++;
            else if (luminance > 191) light++;
            else mid++;
        }

        string Percent(long part) => $"{100.0 * part / Math.Max(1, pixels),6:F2}%";
        Console.WriteLine($"{args[1]}  {image.Width}×{image.Height}");
        Console.WriteLine($"  alpha    全透 {Percent(clear)}  半透 {Percent(partial)}  不透明 {Percent(opaque)}");
        Console.WriteLine($"  不透明   深 {Percent(dark)}  中 {Percent(mid)}  浅 {Percent(light)}");
        Console.WriteLine($"  非灰阶   {Percent(coloured)}");

        // One tone throughout is what makes it tintable — which tone does not
        // matter. Judging by "does it contain white" gets a white silhouette
        // wrong.
        long largest = Math.Max(dark, Math.Max(mid, light));
        bool oneTone = opaque == 0 || largest >= opaque - opaque / 100;
        Console.WriteLine(oneTone && coloured <= pixels / 100
            ? "  → 单一色调，形状在 alpha 里：可以安全染色 ✅"
            : "  → 不透明部分有多种色调，这是一张图片而不是剪影：染色会把它压平 ❌");
        return 0;
    }

    // MARK: - render

    /// <summary>
    /// One calendar sticker, drawn through the app's own renderer. The point is
    /// to be able to look at the calendar after changing artwork without starting
    /// the app — or having Windows to start it on.
    /// </summary>
    private static int Render(string root, string[] args)
    {
        if (args.Length < 7) return Usage();

        var pack = new CalendarArt(Path.Combine(root, "assets", "CalendarArt"));
        if (pack.Layout is null)
        {
            Console.Error.WriteLine("找不到或读不懂 assets/CalendarArt/cal-layout.json");
            return 1;
        }

        var weather = CalendarVocabulary.ParseWeather(args[5]);
        var slot = CalendarVocabulary.ParseSlot(args[6]);
        if (weather is null || slot is null)
        {
            Console.Error.WriteLine($"天气或时段无法识别：{args[5]} {args[6]}");
            return 2;
        }

        var content = new CalendarContent(
            int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]),
            weather.Value, slot.Value,
            args.Length > 7 ? int.Parse(args[7]) : 1);

        // The layout's own canvas unless told otherwise, so a pixel here is a
        // pixel there.
        double width = args.Length > 8 ? double.Parse(args[8]) : pack.Layout.CanvasWidth;
        var drawn = new CalendarRenderer(pack).Render(content, width, 1);
        if (drawn is null)
        {
            Console.Error.WriteLine("没有画出任何东西");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])) ?? ".");
        Png.Encode(drawn, args[1]);
        Console.WriteLine($"{args[1]}  {drawn.Width}×{drawn.Height}");
        return 0;
    }

    // MARK: - shared

    private static Bitmap32? LoadTrayArt(string root)
    {
        string path = Path.Combine(root, "assets", "Tray", "MonaHead.png");
        var art = TrayFrame.LoadStill(path);
        if (art is null) Console.Error.WriteLine($"找不到 {path}");
        return art;
    }

    private static void Draw(Bitmap32 source, Bitmap32 target, int left, int top)
    {
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                long sp = ((long)y * source.Width + x) * 4;
                double a = source.Pixels[sp + 3] / 255.0;
                if (a <= 0) continue;
                long dp = ((long)(top + y) * target.Width + left + x) * 4;
                for (int c = 0; c < 3; c++)
                    target.Pixels[dp + c] = (byte)Math.Round(
                        source.Pixels[sp + c] * a + target.Pixels[dp + c] * (1 - a),
                        MidpointRounding.AwayFromZero);
            }
        }
    }

    /// <summary>Walks up from the binary to the workspace, so paths need no arguments.</summary>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "CalendarArt")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
