using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GT7DecalMaker.Convert;

public static class ConvertEngine
{
    public static PixelBuffer LoadImage(string path, int maxSide)
    {
        byte[] bytes = ReadAllBytesWithRetry(path);
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        double scale = Math.Min(1.0, (double)maxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        int w = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
        int h = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));

        var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        var bgra = new FormatConvertedBitmap(transformed, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();

        int stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        return new PixelBuffer(w, h, px);
    }

    private static byte[] ReadAllBytesWithRetry(string path, int attempts = 4)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(120);
            }
        }
    }

    // Detects a flat, uniform color filling the image border (baked-in background)
    // and makes it transparent. Skips images that already use transparency.
    public static PixelBuffer RemoveFlatBorderBackground(PixelBuffer buf)
    {
        int w = buf.Width, h = buf.Height;
        var src = buf.Pixels;

        // Already has meaningful transparency? Leave it alone.
        int transparent = 0;
        int total = w * h;
        for (int i = 0; i < total; i += 3)
            if (src[i * 4 + 3] < 250) transparent++;
        if (transparent > total / 50) return buf; // >2%

        // Sample the border ring.
        int t = Math.Max(2, Math.Min(w, h) / 50);
        var buckets = new Dictionary<long, (long Count, long R, long G, long B)>();
        long borderTotal = 0;

        void Tally(int x, int y)
        {
            int i = (y * w + x) * 4;
            long key = (src[i + 2] >> 4 << 8) | (src[i + 1] >> 4 << 4) | (src[i] >> 4);
            long r = src[i + 2], g = src[i + 1], b = src[i];
            ref var e = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(buckets, key, out _);
            e.Count++; e.R += r; e.G += g; e.B += b;
            borderTotal++;
        }

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < t || y < t || x >= w - t || y >= h - t) Tally(x, y);

        if (borderTotal == 0) return buf;

        long bestKey = 0, bestCount = 0;
        foreach (var kv in buckets)
            if (kv.Value.Count > bestCount) { bestCount = kv.Value.Count; bestKey = kv.Key; }

        if (bestCount * 100 < borderTotal * 55) return buf; // border not uniform enough

        long br = buckets[bestKey].R / bestCount;
        long bg = buckets[bestKey].G / bestCount;
        long bb = buckets[bestKey].B / bestCount;

        // Only near-white / near-black / gray tones count as "baked background";
        // a flat saturated image must never be wiped.
        double mx = Math.Max(br, Math.Max(bg, bb)) / 255.0;
        double mn = Math.Min(br, Math.Min(bg, bb)) / 255.0;
        double sat = mx <= 0 ? 0 : (mx - mn) / mx;
        bool backgroundLike = sat < 0.15 && (mx > 0.82 || mx < 0.18);
        if (!backgroundLike) return buf;

        // Clear every pixel close to the background color.
        var dst = (byte[])src.Clone();
        const long Tol = 90; // sum of channel diffs
        int br8 = (int)br, bg8 = (int)bg, bb8 = (int)bb;
        int cleared = 0;
        for (int i = 0; i < total; i++)
        {
            int dr = Math.Abs(dst[i * 4 + 2] - br8);
            int dg = Math.Abs(dst[i * 4 + 1] - bg8);
            int db = Math.Abs(dst[i * 4] - bb8);
            if (dr + dg + db <= Tol) { dst[i * 4 + 3] = 0; cleared++; }
        }

        // Sanity: if almost everything matched, this was not a background.
        if (cleared > total * 9 / 10) return buf;

        return new PixelBuffer(w, h, dst);
    }

    public static ConvertResult Run(PixelBuffer buf, ConvertSettings s)
    {
        if (s.RemoveFlatBackground)
            buf = RemoveFlatBorderBackground(buf);

        int[] indexMap;
        List<(byte R, byte G, byte B)> palette;
        if (s.PaletteOverride != null)
        {
            var arr = s.PaletteOverride.Select(p => new byte[] { p.R, p.G, p.B }).ToArray();
            Quantizer.Assign(buf, arr, out indexMap);
            palette = s.PaletteOverride;
        }
        else
        {
            palette = Quantizer.BuildPalette(buf, s.MaxColors, out indexMap);
        }

        var luma = palette.Select(c => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B).ToArray();
        Tracer.Despeckle(indexMap, buf.Width, buf.Height, palette.Count, s.DespecklePx, luma, s.ProtectContrast);

        var layers = new List<LayerResult>();
        var layerPixels = new int[palette.Count];
        foreach (var idx in indexMap)
            if (idx >= 0) layerPixels[idx]++;

        // Paint large areas first so small detail layers sit on top.
        var order = Enumerable.Range(0, palette.Count)
            .Where(i => layerPixels[i] > 0)
            .OrderByDescending(i => layerPixels[i]).ToList();
        // rank[k] = paint position; in stacked mode a layer also covers every
        // pixel owned by layers painted above it (no holes, far fewer edges).
        var rank = new int[palette.Count];
        for (int j = 0; j < order.Count; j++) rank[order[j]] = j;

        var traced = new Traced { Width = buf.Width, Height = buf.Height };
        foreach (int k in order)
        {
            var mask = new bool[buf.Width * buf.Height];
            int rk = rank[k];
            for (int i = 0; i < indexMap.Length; i++)
            {
                int v = indexMap[i];
                if (v == k || (s.Stacked && v >= 0 && rank[v] > rk)) mask[i] = true;
            }
            traced.Layers.Add((palette[k], Tracer.TraceLoops(mask, buf.Width, buf.Height)));
        }
        return Simplify(traced, s);
    }

    // Raw pixel-exact outlines; the expensive part, independent of Epsilon.
    public sealed class Traced
    {
        public int Width, Height;
        public List<((byte R, byte G, byte B) Color, List<List<Point>> Loops)> Layers = new();
    }

    public static Traced Trace(PixelBuffer buf, ConvertSettings s)
    {
        var r = Run(buf, new ConvertSettings
        {
            Epsilon = -1, MaxColors = s.MaxColors, DespecklePx = s.DespecklePx, SeamFill = s.SeamFill,
            RemoveFlatBackground = s.RemoveFlatBackground, Stacked = s.Stacked,
            ProtectContrast = s.ProtectContrast, DetailScale = s.DetailScale, PaletteOverride = s.PaletteOverride
        });
        return r.Raw!;
    }

    public static ConvertResult Simplify(Traced traced, ConvertSettings s)
    {
        if (s.Epsilon < 0) return new ConvertResult { Raw = traced };
        var layers = new List<LayerResult>();
        int loopCount = 0;
        foreach (var (c, loops) in traced.Layers)
        {
            var layer = new LayerResult(c.R, c.G, c.B);
            foreach (var loop in loops)
            {
                var simplified = Simplifier.SimplifyLoop(loop, LoopEpsilon(loop, s));
                if (simplified.Count >= 3 && Math.Abs(PolygonArea(simplified)) >= 0.75)
                    layer.Loops.Add(simplified);
            }
            if (layer.Loops.Count > 0)
            {
                loopCount += layer.Loops.Count;
                layers.Add(layer);
            }
        }
        var buf = (Width: traced.Width, Height: traced.Height);

        string xml = SvgWriter.Build(buf.Width, buf.Height, layers, s.SeamFill);
        return new ConvertResult
        {
            Xml = xml,
            Bytes = SvgWriter.SizeOf(xml),
            Width = buf.Width,
            Height = buf.Height,
            ColorsUsed = layers.Count,
            LoopCount = loopCount,
            Settings = s,
            Layers = layers
        };
    }

    // Small shapes (letters, badges) get gentler smoothing in proportion to their
    // size, so a glyph a few pixels tall keeps its form. They cost few bytes anyway.
    private static double LoopEpsilon(List<Point> loop, ConvertSettings s)
    {
        if (s.DetailScale <= 0) return s.Epsilon;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in loop)
        {
            if (p.X < x0) x0 = p.X; if (p.X > x1) x1 = p.X;
            if (p.Y < y0) y0 = p.Y; if (p.Y > y1) y1 = p.Y;
        }
        double size = Math.Min(x1 - x0, y1 - y0);
        return Math.Min(s.Epsilon, Math.Max(0.5, size * s.DetailScale));
    }

    private static double PolygonArea(List<Point> pts)
    {
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2.0;
    }

    // Auto-fit pipeline:
    //   Phase A - grow edge-simplification until it fits (full palette preserved).
    //   Phase B - hue-guarded palette merging (accent color families survive).
    //   Phase C - binary refinement to land JUST under the target.
    //   Phase D - resolution climbing: spend leftover budget on more pixels.
    public static (ConvertResult Fitted, ConvertResult BestQuality, int Attempts) AutoFit(
        PixelBuffer buf, ConvertSettings baseSettings, int targetBytes,
        Action<int, int, int>? progress = null,
        Func<int, PixelBuffer>? resampler = null, int maxRes = 1024)
    {
        var bestQuality = Step(buf, baseSettings);
        var searched = QualitySearch.Find(buf, baseSettings, targetBytes, resampler, maxRes, progress, out int sAttempts);
        if (searched != null) return (searched, bestQuality, sAttempts);

        // Fallback: nothing in the search grid fit; use the legacy ladder.
        int attempts = 0;
        var best = bestQuality;
        ConvertResult? fitted = null;
        double eps = baseSettings.Epsilon;

        double diag = Math.Sqrt((double)buf.Width * buf.Width + buf.Height * buf.Height);
        double epsMax = Math.Max(3.0, diag / 6.0);
        int minColors = Math.Min(2, baseSettings.MaxColors);

        // ---- Phase A: detail reduction with full palette ----
        double lo = baseSettings.Epsilon, hi = epsMax;
        while (true)
        {
            attempts++;
            if (best.Bytes <= targetBytes)
            {
                fitted = best;
                hi = eps;
                break;
            }
            if (eps >= epsMax && attempts > 1) break;
            eps = Math.Min(epsMax, eps * 1.55 + 0.45);
            var r = Step(buf, With(baseSettings, epsilon: eps));
            if (r.Bytes < best.Bytes) best = r;
            progress?.Invoke(attempts, r.Bytes, targetBytes);
            if (r.Bytes <= targetBytes) { fitted = r; hi = eps; break; }
            lo = eps;
        }

        // ---- Phase B: hue-guarded palette merging at fixed (max) epsilon ----
        if (fitted == null)
        {
            var fullPal = Quantizer.BuildPalette(buf, baseSettings.MaxColors, out var map);
            var counts = Quantizer.CountClasses(map, fullPal.Count);
            var pal = fullPal;
            lo = epsMax * 0.5; hi = epsMax;

            while (pal.Count > minColors && attempts < 80)
            {
                attempts++;
                int nextCount = Math.Max(minColors, pal.Count * 3 / 4);
                pal = Quantizer.ReducePaletteHueGuarded(pal, counts, nextCount);
                Quantizer.Assign(buf, pal.Select(p => new byte[] { p.R, p.G, p.B }).ToArray(), out map);
                counts = Quantizer.CountClasses(map, pal.Count);

                var r = Step(buf, With(baseSettings, epsilon: epsMax, palette: pal));
                if (r.Bytes < best.Bytes) best = r;
                progress?.Invoke(attempts, r.Bytes, targetBytes);
                if (r.Bytes <= targetBytes) { fitted = r; break; }
            }
        }

        // ---- Phase C: binary refinement (epsilon only) ----
        if (fitted != null)
        {
            var fitSettings = fitted.Settings;
            while (attempts < 90 && hi - lo > 0.05)
            {
                attempts++;
                double mid = (lo + hi) / 2;
                var r = Step(buf, With(fitSettings, epsilon: mid));
                if (r.Bytes < best.Bytes) best = r;
                if (r.Bytes <= targetBytes)
                {
                    hi = mid;
                    if (r.Bytes > fitted.Bytes) fitted = r;
                }
                else
                {
                    lo = mid;
                }
            }

            // ---- Phase D: spend leftover size budget on resolution ----
            if (resampler != null)
            {
                int res = Math.Max(fitted.Width, fitted.Height);
                while (res < maxRes && attempts < 100)
                {
                    int next = Math.Min(maxRes, res * 4 / 3 + 2);
                    var hiBuf = resampler(next);
                    attempts++;
                    var r = Step(hiBuf, With(fitted.Settings, epsilon: fitted.Settings.Epsilon));
                    progress?.Invoke(attempts, r.Bytes, targetBytes);
                    if (r.Bytes <= targetBytes) { fitted = r; res = next; }
                    else break;
                }
            }
        }

        return (fitted ?? best, bestQuality, Math.Max(1, attempts));
    }

    private static ConvertResult Step(PixelBuffer buf, ConvertSettings s)
    {
        // RemoveFlatBackground is applied once by the caller-provided buffer path
        // (Run applies it internally; keep idempotent by design).
        return Run(buf, s);
    }

    private static ConvertSettings With(
        ConvertSettings src,
        double? epsilon = null,
        int? maxColors = null,
        int? despeckle = null,
        bool? seamFill = null,
        List<(byte R, byte G, byte B)>? palette = null)
        => new()
        {
            Epsilon = epsilon ?? src.Epsilon,
            MaxColors = maxColors ?? src.MaxColors,
            DespecklePx = despeckle ?? src.DespecklePx,
            SeamFill = seamFill ?? src.SeamFill,
            RemoveFlatBackground = src.RemoveFlatBackground,
            Stacked = src.Stacked,
            ProtectContrast = src.ProtectContrast,
            DetailScale = src.DetailScale,
            PaletteOverride = palette ?? src.PaletteOverride
        };
}
