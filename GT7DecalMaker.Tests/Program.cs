using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GT7DecalMaker;
using GT7DecalMaker.Convert;
using WpfPoint = System.Windows.Point;

public static class Program
{
    private static async Task<int> RunOcrCheck()
    {
        Directory.CreateDirectory(Dir);
        // Render decal-like text and check the name reader picks it up.
        string path = Path.Combine(Dir, "ocrtest.png");
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 600, 200));
            var ft = new FormattedText("CTM TUNING",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial Black"), FontStyles.Normal,
                    FontWeights.Bold, FontStretches.Normal),
                64, Brushes.White, 1.25);
            ctx.DrawText(ft, new WpfPoint(40, 60));
        }
        var rtb = new RenderTargetBitmap(600, 200, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path))
            enc.Save(fs);

        var name = await DecalNameReader.ReadAsync(path);
        Console.WriteLine($"ocr detected: '{name}'");
        bool ok = name != null && name.Replace("_", " ", StringComparison.OrdinalIgnoreCase)
                     .IndexOf("CTM", StringComparison.OrdinalIgnoreCase) >= 0;
        Console.WriteLine(ok ? "[PASS] ocr naming" : "[FAIL] ocr naming");
        return ok ? 0 : 1;
    }

    private static void RunMicroRepro()
    {
        // Solid cyan image through BuildPalette - nearest-color sanity check.
        int w = 24, h = 24;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = 224; px[i * 4 + 1] = 196; px[i * 4 + 2] = 46; px[i * 4 + 3] = 255;
        }
        var buf = new PixelBuffer(w, h, px);
        var pal = Quantizer.BuildPalette(buf, 16, out int[] map);

        Console.WriteLine($"palette ({pal.Count}):");
        for (int i = 0; i < pal.Count; i++)
            Console.WriteLine($"  [{i}] #{pal[i].R:X2}{pal[i].G:X2}{pal[i].B:X2}");
        Console.WriteLine($"center pixel class = {map[(h / 2) * w + w / 2]} (expect the cyan entry)");
    }

    private static int _pass, _fail;
    private const string Dir = "testout";

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "gym") { return Gym.Run(args.Skip(1).ToArray()); }
        if (args.Length > 0 && args[0] == "sample") { return SampleBench.Run(args.Skip(1).ToArray()); }
        if (args.Length > 0 && args[0] == "micro") { RunMicroRepro(); return 0; }
        if (args.Length > 0 && args[0] == "ocr") { return RunOcrCheck().GetAwaiter().GetResult(); }
        Directory.CreateDirectory(Dir);

        string shapes = Path.Combine(Dir, "shapes.png");
        string checker = Path.Combine(Dir, "checker.png");
        string empty = Path.Combine(Dir, "empty.png");
        string tiny = Path.Combine(Dir, "tiny.png");

        GenerateShapes(shapes);
        GenerateChecker(checker);
        GenerateEmpty(empty);
        GenerateTiny(tiny);

        TestTransparentThrows(empty);
        TestTiny(tiny);
        TestCheckerStress(checker);
        TestShapesAutoFit(shapes);
        TestSvgValidity(Path.Combine(Dir, "shapes_gt7.svg"));
        TestBusyAutoFit();
        TestSoftEdgeCyan();
        TestOvershootRefinement();
        TestHueGuardedReduction();
        TestBackgroundRemoval();
        TestResolutionClimb();

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ---------- generators (pure WPF imaging) ----------

    private static PixelBuffer GenerateShapes(string path)
    {
        int w = 480, h = 320;
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            var magenta = new SolidColorBrush(Color.FromRgb(0xE8, 0x1A, 0x7C));
            var cyan = new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xE0));
            var white = Brushes.White;
            var yellow = new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18));
            var black = Brushes.Black;

            // crown-ish triangles
            ctx.DrawGeometry(magenta, null, Tri(new[] { (150.0, 40.0), (190.0, 110.0), (110.0, 110.0) }));
            ctx.DrawGeometry(magenta, null, Tri(new[] { (230.0, 30.0), (270.0, 100.0), (190.0, 100.0) }));

            // big cyan slash
            ctx.PushTransform(new RotateTransform(-18, 240, 160));
            ctx.DrawRectangle(cyan, null, new Rect(60, 130, 360, 55));
            ctx.Pop();

            // white bars
            ctx.DrawRectangle(white, null, new Rect(80, 200, 300, 34));
            ctx.DrawRectangle(white, null, new Rect(120, 244, 220, 34));

            // yellow ring
            var ring = new EllipseGeometry(new WpfPoint(390, 210), 45, 45);
            var hole = new EllipseGeometry(new WpfPoint(390, 210), 22, 22);
            var group = Geometry.Combine(ring, hole, GeometryCombineMode.Exclude, null);
            ctx.DrawGeometry(yellow, null, group);

            // black outline frame to test holes/edges
            ctx.DrawGeometry(null, new Pen(black, 6), new RectangleGeometry(new Rect(10, 10, 460, 300)));
        }
        return Save(dv, w, h, path);
    }

    private static Geometry Tri((double x, double y)[] pts)
    {
        var fig = new PathFigure { StartPoint = new WpfPoint(pts[0].x, pts[0].y), IsClosed = true };
        for (int i = 1; i < pts.Length; i++)
            fig.Segments.Add(new LineSegment { Point = new WpfPoint(pts[i].x, pts[i].y) });
        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        return pg;
    }

    private static void GenerateChecker(string path)
    {
        int w = 48, h = 48;
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            var b = Brushes.White;
            for (int y = 0; y < h; y += 4)
                for (int x = 0; x < w; x += 4)
                    if ((x / 4 + y / 4) % 2 == 0)
                        ctx.DrawRectangle(b, null, new Rect(x, y, 4, 4));
        }
        Save(dv, w, h, path);
    }

    private static void GenerateEmpty(string path) => Save(new DrawingVisual(), 16, 16, path);

    private static void GenerateTiny(string path)
    {
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 4, 4));
        Save(dv, 4, 4, path);
    }

    private static PixelBuffer Save(DrawingVisual dv, int w, int h, string path)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path))
            enc.Save(fs);
        return ConvertEngine.LoadImage(path, Math.Max(w, h));
    }

    // ---------- tests ----------

    private static void TestTransparentThrows(string path)
    {
        try
        {
            var buf = ConvertEngine.LoadImage(path, 64);
            Quantizer.BuildPalette(buf, 8, out _);
            Fail("transparent: expected exception");
        }
        catch (InvalidOperationException)
        {
            Pass("transparent image throws");
        }
        catch (Exception ex)
        {
            Fail($"transparent: wrong exception {ex.GetType().Name}");
        }
    }

    private static void TestTiny(string path)
    {
        var buf = ConvertEngine.LoadImage(path, 64);
        var r = ConvertEngine.Run(buf, new ConvertSettings { Epsilon = 1, MaxColors = 8 });
        Check(r.Layers.Count == 1, $"tiny single layer (got {r.Layers.Count})");
        Check(SvgWriter.SizeOf(r.Xml) < 500, "tiny svg small");
        Check(r.Xml.StartsWith("<svg") && r.Xml.EndsWith("</svg>"), "tiny xml envelope");
        Pass("tiny conversion");
    }

    private static void TestCheckerStress(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buf = ConvertEngine.LoadImage(path, 96);
        var r = ConvertEngine.Run(buf, new ConvertSettings { Epsilon = 0.9, MaxColors = 4 });
        sw.Stop();
        Check(r.LoopCount > 0, "checker produced loops");
        Check(sw.ElapsedMilliseconds < 5000, $"checker finished fast ({sw.ElapsedMilliseconds} ms)");
        Pass($"checker stress ({r.LoopCount} loops)");
    }

    private static void TestShapesAutoFit(string path)
    {
        var buf = ConvertEngine.LoadImage(path, 384);
        var baseSettings = new ConvertSettings
        {
            Epsilon = 2.25, // detail level 8
            MaxColors = 16,
            DespecklePx = 4,
            SeamFill = true
        };
        var (fitted, bestQuality, attempts) =
            ConvertEngine.AutoFit(buf, baseSettings, 15_000);

        Console.WriteLine($"   best quality: {bestQuality.Bytes:N0} B, {bestQuality.ColorsUsed} colors, " +
                          $"{bestQuality.LoopCount} loops | fitted: {fitted.Bytes:N0} B after {attempts} passes");

        File.WriteAllText(Path.Combine(Dir, "shapes_gt7.svg"), fitted.Xml, new UTF8Encoding(false));
        Check(fitted.Bytes <= 15_000, $"fitted under 15KB ({fitted.Bytes})");

        double match = Fidelity(buf, fitted);
        Console.WriteLine($"   interior fidelity vs quantized ground truth: {match * 100:F1}%");
        Check(match > 0.90, "interior fidelity above 90%");
        Pass("shapes auto-fit");
    }

    // Chaotic multi-color image: forces the auto-fit ladder through several passes.
    private static void TestBusyAutoFit()
    {
        int w = 512, h = 512;
        var dv = new DrawingVisual();
        var rnd = new Random(42);
        using (var ctx = dv.RenderOpen())
        {
            for (int i = 0; i < 220; i++)
            {
                byte r = (byte)rnd.Next(256), g = (byte)rnd.Next(256), b = (byte)rnd.Next(256);
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                double cx = rnd.NextDouble() * w, cy = rnd.NextDouble() * h;
                double rad = 8 + rnd.NextDouble() * 60;
                ctx.DrawEllipse(brush, null, new WpfPoint(cx, cy), rad, rad);
            }
            // sharp star on top
            var star = new PathGeometry();
            var fig = new PathFigure { StartPoint = new WpfPoint(256, 60), IsClosed = true };
            for (int i = 1; i < 10; i++)
            {
                double ang = i * Math.PI / 5 - Math.PI / 2;
                double rad = i % 2 == 0 ? 180 : 80;
                fig.Segments.Add(new LineSegment
                {
                    Point = new WpfPoint(256 + rad * Math.Cos(ang), 256 + rad * Math.Sin(ang))
                });
            }
            star.Figures.Add(fig);
            ctx.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 4), star);
        }

        string path = Path.Combine(Dir, "busy.png");
        PixelBuffer buf;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path))
            enc.Save(fs);
        buf = ConvertEngine.LoadImage(path, 512);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (fitted, bestQuality, attempts) = ConvertEngine.AutoFit(buf,
            new ConvertSettings { Epsilon = 1.5, MaxColors = 32, DespecklePx = 3, SeamFill = true },
            15_000);
        sw.Stop();

        Console.WriteLine($"   busy image: quality {bestQuality.Bytes:N0} B -> fitted {fitted.Bytes:N0} B " +
                          $"in {attempts} passes, {sw.ElapsedMilliseconds} ms ({fitted.ColorsUsed} colors, {fitted.LoopCount} loops)");
        Check(fitted.Bytes <= 15_000, $"busy fitted under 15KB ({fitted.Bytes:N0})");
        Check(fitted.ColorsUsed >= 2 && fitted.LoopCount > 10, "busy kept meaningful detail");
        Check(sw.ElapsedMilliseconds < 30_000, $"busy converted fast enough ({sw.ElapsedMilliseconds} ms)");
        File.WriteAllText(Path.Combine(Dir, "busy_gt7.svg"), fitted.Xml, new UTF8Encoding(false));
        Pass("busy auto-fit");

        // Force the reduction ladder: tiny 3KB target must iterate down.
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var (tiny, _, passes) = ConvertEngine.AutoFit(buf,
            new ConvertSettings { Epsilon = 1.5, MaxColors = 32, DespecklePx = 3, SeamFill = true },
            3_000);
        sw2.Stop();
        Console.WriteLine($"   forced 3KB: {tiny.Bytes:N0} B after {passes} passes ({tiny.ColorsUsed} colors), {sw2.ElapsedMilliseconds} ms");
        Check(tiny.Bytes <= 3_000, "forced 3KB target reached");
        Check(passes > 1, "ladder actually iterated");
        Pass("busy forced-fit ladder");
    }

    // Regression: AI-art style soft translucent edges must not spawn gray halo
    // classes that drown saturated accent colors (cyan was lost in the field).
    private static void TestSoftEdgeCyan()
    {
        int w = 480, h = 200;
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            // soft-edged white bar (halo rings of decreasing alpha)
            for (int i = 6; i >= 1; i--)
            {
                var b = new SolidColorBrush(Color.FromArgb((byte)(255 / i), 255, 255, 255));
                ctx.DrawRectangle(b, null, new Rect(60 - i * 3, 80 - i * 3, 240 + i * 6, 50 + i * 6));
            }
            ctx.DrawRectangle(Brushes.White, null, new Rect(60, 80, 240, 50));

            // soft-edged cyan slash
            ctx.PushTransform(new RotateTransform(-14, 360, 90));
            for (int i = 5; i >= 1; i--)
            {
                var b = new SolidColorBrush(Color.FromArgb((byte)(220 / i), 46, 196, 224));
                ctx.DrawRectangle(b, null, new Rect(300 - i * 3, 55 - i * 3, 120 + i * 6, 70 + i * 6));
            }
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(46, 196, 224)), null,
                new Rect(300, 55, 120, 70));
            ctx.Pop();

            // magenta blob
            ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(232, 26, 124)), null,
                new WpfPoint(150, 160), 40, 28);
        }

        string path = Path.Combine(Dir, "softedge.png");
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path))
            enc.Save(fs);

        var buf = ConvertEngine.LoadImage(path, 384);

        // DEBUG: dump palette + assignment counts
        var dbgPal = Quantizer.BuildPalette(buf, 32, out int[] dbgMap);
        int dbgW = buf.Width;
        int cx = (int)(360 * 0.8), cy = (int)(90 * 0.8);
        int ci = cy * dbgW + cx;
        Console.WriteLine($"   [cyan-center px BGRA] B={buf.Pixels[ci * 4]} G={buf.Pixels[ci * 4 + 1]} " +
                          $"R={buf.Pixels[ci * 4 + 2]} A={buf.Pixels[ci * 4 + 3]} -> class {dbgMap[ci]}");
        int wx = (int)(150 * 0.8), wy = (int)(100 * 0.8);
        int wi = wy * dbgW + wx;
        Console.WriteLine($"   [white-center px BGRA] B={buf.Pixels[wi * 4]} G={buf.Pixels[wi * 4 + 1]} " +
                          $"R={buf.Pixels[wi * 4 + 2]} A={buf.Pixels[wi * 4 + 3]} -> class {dbgMap[wi]}");
        Console.WriteLine("   [palette]");
        for (int i = 0; i < dbgPal.Count; i++)
        {
            int cnt = 0;
            foreach (var m in dbgMap) if (m == i) cnt++;
            var (r, g, b) = dbgPal[i];
            Console.WriteLine($"      #{r:X2}{g:X2}{b:X2} x{cnt}");
        }

        var (fitted, bestQuality, attempts) = ConvertEngine.AutoFit(buf,
            new ConvertSettings { Epsilon = 0.75, MaxColors = 32, DespecklePx = 4, SeamFill = true },
            15_000);

        Console.WriteLine($"   soft-edge: full {bestQuality.Bytes:N0} B -> fitted {fitted.Bytes:N0} B " +
                          $"in {attempts} passes ({fitted.ColorsUsed} colors)");
        foreach (var l in fitted.Layers)
            Console.WriteLine($"      layer #{l.R:X2}{l.G:X2}{l.B:X2} loops={l.Loops.Count}");

        bool cyanSurvived = fitted.Layers.Any(l => l.B > 140 && l.G > 110 && l.B - l.R > 45);
        bool magentaSurvived = fitted.Layers.Any(l => l.R > 170 && l.B > 90 && l.G < l.R / 2);
        Check(cyanSurvived, "cyan layer retained");
        Check(magentaSurvived, "magenta layer retained");
        Check(fitted.Bytes <= 15_000, "soft-edge fits limit");

        double match = Fidelity(buf, fitted);
        Console.WriteLine($"   interior fidelity: {match * 100:F1}%");
        Check(match > 0.85, "soft-edge fidelity above 85%");
        Pass("soft-edge color retention");
    }

    // Regression: barely-over images must trim to JUST under the limit,
    // not crash down to a fraction of it.
    private static void TestOvershootRefinement()
    {
        string path = Path.Combine(Dir, "busy.png");
        var buf = ConvertEngine.LoadImage(path, 512);
        var baseSettings = new ConvertSettings { Epsilon = 1.5, MaxColors = 32, DespecklePx = 3, SeamFill = true };
        var full = ConvertEngine.Run(buf, baseSettings);

        int target = Math.Max(3_000, (int)(full.Bytes * 0.9)); // just under full quality
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (fitted, bestQuality, attempts) = ConvertEngine.AutoFit(buf, baseSettings, target);
        sw.Stop();

        Console.WriteLine($"   refinement: full {full.Bytes:N0} B, target {target:N0} -> " +
                          $"{fitted.Bytes:N0} B in {attempts} passes, {sw.ElapsedMilliseconds} ms");
        Check(fitted.Bytes <= target, "refined under target");
        Check(fitted.Bytes >= target * 3 / 4, $"no quality cratering (>=75% of target, got {fitted.Bytes})");
        Pass("overshoot refinement");
    }

    // Hue guard: reducing a palette containing red shades + cyan must never
    // sacrifice the cyan family while reds can merge among themselves.
    private static void TestHueGuardedReduction()
    {
        var palette = new List<(byte, byte, byte)>
        {
            (220, 40, 40),   // red
            (185, 35, 30),   // dark red
            (240, 90, 70),   // light red
            (40, 180, 220),  // cyan
        };
        var weights = new long[] { 100, 100, 100, 100 };
        var reduced = Quantizer.ReducePaletteHueGuarded(palette, weights, 2);

        bool cyanSurvived = reduced.Any(p => p.Item3 > 140 && p.Item2 > 110 && p.Item3 - p.Item1 > 45);
        bool redSurvived = reduced.Any(p => p.Item1 > 140 && p.Item1 - p.Item3 > 40);
        Check(reduced.Count == 2, $"reduced to 2 (got {reduced.Count})");
        Check(cyanSurvived, "cyan family survived reduction");
        Check(redSurvived, "red family survived reduction");
        Pass("hue-guarded palette reduction");
    }

    // Baked white background must become transparent; art colors must survive.
    private static void TestBackgroundRemoval()
    {
        int w = 200, h = 120;
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(220, 40, 40)), null,
                new WpfPoint(100, 60), 45, 35);
        }
        string path = Path.Combine(Dir, "whitebg.png");
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path))
            enc.Save(fs);

        var buf = ConvertEngine.LoadImage(path, 200);
        var cleaned = ConvertEngine.RemoveFlatBorderBackground(buf);
        int corner = cleaned.Pixels[3]; // alpha of pixel 0
        int centerA = cleaned.Pixels[(60 * cleaned.Width + 100) * 4 + 3];
        Check(corner == 0, $"white corner cleared (alpha {corner})");
        Check(centerA >= 200, $"art center kept (alpha {centerA})");

        // Transparent art must be left untouched.
        var shapes = ConvertEngine.LoadImage(Path.Combine(Dir, "shapes.png"), 384);
        var untouched = ConvertEngine.RemoveFlatBorderBackground(shapes);
        bool same = ReferenceEquals(shapes.Pixels, untouched.Pixels);
        Check(same, "already-transparent image untouched");
        Pass("background removal");
    }

    // Leftover size budget should buy resolution (more detail at same file size).
    private static void TestResolutionClimb()
    {
        // shapes.png converts tiny (~1KB at 384px) -> lots of budget to climb with.
        string path = Path.Combine(Dir, "shapes.png");
        var buf = ConvertEngine.LoadImage(path, 384);
        var baseSettings = new ConvertSettings { Epsilon = 2.25, MaxColors = 16, DespecklePx = 4, SeamFill = true };

        var (fitted, _, attempts) = ConvertEngine.AutoFit(buf, baseSettings, 15_000,
            resampler: r => ConvertEngine.LoadImage(path, r), maxRes: 1024);

        Console.WriteLine($"   climb: {buf.Width}px -> {fitted.Width}px, {fitted.Bytes:N0} B in {attempts} passes");
        Check(fitted.Bytes <= 15_000, "climbed result still fits");
        Check(fitted.Width > buf.Width, $"resolution climbed ({buf.Width} -> {fitted.Width})");
        Pass("resolution climbing");
    }

    private static void TestSvgValidity(string path)
    {
        string xml = File.ReadAllText(path);
        Check(xml.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\""), "svg root + xmlns");
        Check(xml.EndsWith("</svg>"), "svg closes");
        Check(!xml.Contains("NaN"), "no NaN in output");
        Check(!xml.Contains("<image"), "no embedded rasters");
        int fills = CountOccurrences(xml, "fill=\"#");
        int paths = CountOccurrences(xml, "<path");
        Check(paths >= 3 && fills >= 3, $"multi-layer color retained ({paths} paths)");
        Pass("svg structural validity");
    }

    // Rasterize the vector layers and compare with the source's quantized class map,
    // scoring interior pixels (boundary bands are excluded: smoothing there is intended).
    private static double Fidelity(PixelBuffer src, ConvertResult result)
    {
        // Ground truth: quantized class per pixel.
        var gt = Quantizer.BuildPalette(src, 16, out int[] classMap);
        int w = src.Width, h = src.Height;
        var px = src.Pixels;

        bool OpaqueSrc(int x, int y) => px[(y * w + x) * 4 + 3] >= 32;

        // Boundary mask: pixel touches a differing class or opacity transition.
        var boundary = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int c = classMap[i];
                bool op = OpaqueSrc(x, y);
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) { boundary[i] = true; break; }
                    int j = ny * w + nx;
                    if (classMap[j] != c || OpaqueSrc(nx, ny) != op) { boundary[i] = true; break; }
                }
            }

        // Rasterize vector result (all loops of a layer share one even-odd geometry).
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            foreach (var layer in result.Layers)
            {
                var brush = new SolidColorBrush(Color.FromRgb(layer.R, layer.G, layer.B));
                brush.Freeze();
                var pg = new PathGeometry { FillRule = FillRule.EvenOdd };
                foreach (var loop in layer.Loops)
                {
                    var fig = new PathFigure
                    {
                        StartPoint = new WpfPoint(loop[0].X, loop[0].Y),
                        IsClosed = true
                    };
                    for (int i = 1; i < loop.Count; i++)
                        fig.Segments.Add(new LineSegment { Point = new WpfPoint(loop[i].X, loop[i].Y) });
                    pg.Figures.Add(fig);
                }
                pg.Freeze();
                ctx.DrawGeometry(brush, new Pen(brush, 0.5), pg);
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        int stride = w * 4;
        var rendered = new byte[stride * h];
        rtb.CopyPixels(rendered, stride, 0);

        var pal = gt.Select(p => (r: (int)p.R, g: (int)p.G, b: (int)p.B)).ToArray();

        int match = 0, scored = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (boundary[i]) continue;
                scored++;

                bool sOpaque = OpaqueSrc(x, y);
                byte ra = rendered[i * 4 + 3];
                if (!sOpaque)
                {
                    if (ra < 128) match++;
                    continue;
                }

                // Nearest palette class for the rendered pixel.
                int bestC = -1;
                if (ra >= 128)
                {
                    long bd = long.MaxValue;
                    for (int p = 0; p < pal.Length; p++)
                    {
                        long dr = rendered[i * 4 + 2] - pal[p].r;
                        long dg = rendered[i * 4 + 1] - pal[p].g;
                        long db = rendered[i * 4] - pal[p].b;
                        long d = dr * dr * 2 + dg * dg * 4 + db * db;
                        if (d < bd) { bd = d; bestC = p; }
                    }
                }
                if (bestC == classMap[i]) match++;

                // Independent HUE-INTEGRITY check: the painted color at this spot
                // must be the palette entry truly nearest to the SOURCE pixel
                // (plain RGB, unweighted). Catches channel-order bugs that a
                // shared classifier would hide.
                if ((x + y) % 3 == 0)
                {
                    int hueBest = -1;
                    long hb = long.MaxValue;
                    byte sr = px[i * 4 + 2], sg = px[i * 4 + 1], sb = px[i * 4];
                    for (int p = 0; p < pal.Length; p++)
                    {
                        long dr = sr - pal[p].r, dg = sg - pal[p].g, db = sb - pal[p].b;
                        long d = dr * dr + dg * dg + db * db;
                        if (d < hb) { hb = d; hueBest = p; }
                    }
                    scored++;
                    if (hueBest >= 0)
                    {
                        int diff = Math.Abs(rendered[i * 4] - pal[hueBest].b)
                                 + Math.Abs(rendered[i * 4 + 1] - pal[hueBest].g)
                                 + Math.Abs(rendered[i * 4 + 2] - pal[hueBest].r);
                        if (!sOpaque || diff < 60) match++;
                    }
                }
            }
        return scored == 0 ? 1.0 : (double)match / scored;
    }

    private static int CountOccurrences(string s, string sub)
    {
        int n = 0, idx = 0;
        while ((idx = s.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0) { n++; idx += sub.Length; }
        return n;
    }

    private static void Check(bool cond, string label)
    {
        if (cond) { _pass++; Console.WriteLine($"   [ok] {label}"); }
        else { _fail++; Console.WriteLine($"   [FAIL] {label}"); }
    }

    private static void Pass(string label)
    {
        _pass++;
        Console.WriteLine($"[PASS] {label}");
    }

    private static void Fail(string label)
    {
        _fail++;
        Console.WriteLine($"[FAIL] {label}");
    }
}
