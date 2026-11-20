using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GT7DecalMaker.Convert;

// Quality bench: auto-fit each sample to 15,000 bytes exactly like the app,
// rasterize the SVG layers, and score against the source (mean RGB error at 512px).
public static class SampleBench
{
    public static int Run(string[] files)
    {
        int rc = 0;
        foreach (var f in files)
        {
            var buf = ConvertEngine.LoadImage(f, 384);
            var s = new ConvertSettings { Epsilon = 2.25, MaxColors = 16, DespecklePx = 4, SeamFill = true, RemoveFlatBackground = true };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (fit, _, att) = ConvertEngine.AutoFit(buf, s, 15_000, null, r => ConvertEngine.LoadImage(f, r), 1024);
            string baseName = Path.ChangeExtension(f, null);
            File.WriteAllText(baseName + "_out.svg", fit.Xml);
            var refBuf = ConvertEngine.LoadImage(f, 1536);
            var render = Rasterize(fit, refBuf.Width, refBuf.Height);
            double err = Score(refBuf, render, out double tint);
            SavePng(render, refBuf.Width, refBuf.Height, baseName + "_render.png");
            Console.WriteLine($"{Path.GetFileName(f)}: {fit.Bytes} B, res {fit.Width}x{fit.Height}, colors {fit.ColorsUsed}, eps {fit.Settings.Epsilon:F2}, loops {fit.LoopCount}, passes {att}, {sw.ElapsedMilliseconds} ms, ERR {err:F2} TINT {tint:F2}");
            if (fit.Bytes > 15_000) { Console.WriteLine("  OVER LIMIT"); rc = 1; }
        }
        return rc;
    }

    public static void SaveRender(ConvertResult r, int w, int h, string path) => SavePng(Rasterize(r, w, h), w, h, path);

    static byte[] Rasterize(ConvertResult r, int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform((double)w / r.Width, (double)h / r.Height));
            foreach (var L in r.Layers)
            {
                var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var ctx = g.Open())
                    foreach (var loop in L.Loops)
                    {
                        ctx.BeginFigure(new System.Windows.Point(Math.Round(loop[0].X), Math.Round(loop[0].Y)), true, true);
                        for (int i = 1; i < loop.Count; i++)
                            ctx.LineTo(new System.Windows.Point(Math.Round(loop[i].X), Math.Round(loop[i].Y)), true, false);
                    }
                var br = new SolidColorBrush(Color.FromRgb(L.R, L.G, L.B));
                dc.DrawGeometry(br, r.Settings.SeamFill ? new Pen(br, .5) : null, g);
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        return px;
    }

    // Composite both over mid-gray, mean abs RGB error per channel.
    static double Score(PixelBuffer src, byte[] ren, out double tint)
    {
        double sum = 0; tint = 0; int n = src.Width * src.Height;
        for (int i = 0; i < n; i++)
        {
            double a = src.Pixels[i * 4 + 3] / 255.0, b = ren[i * 4 + 3] / 255.0;
            var d = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double s = src.Pixels[i * 4 + c] * a + 128 * (1 - a);
                double r = ren[i * 4 + c] + 128 * (1 - b); // premultiplied
                d[c] = s - r;
                sum += Math.Abs(s - r);
            }
            double m = (d[0] + d[1] + d[2]) / 3;
            tint += Math.Abs(d[0] - m) + Math.Abs(d[1] - m) + Math.Abs(d[2] - m);
        }
        tint /= n * 3;
        return sum / (n * 3);
    }

    static void SavePng(byte[] px, int w, int h, string path)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
