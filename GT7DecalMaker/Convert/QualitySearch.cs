namespace GT7DecalMaker.Convert;

// Picks the conversion that looks closest to the source while staying under the
// byte limit. Tries resolution x colors x despeckle, finds the sharpest edge
// tolerance that fits for each, renders every fitting candidate and scores it.
public static class QualitySearch
{
    public const int RefSide = 512;

    // Tries several splits of the byte budget between the base trace and a
    // high-res detail layer (lettering, badges) and keeps the best-looking one.
    public static ConvertResult? Find(PixelBuffer buf, ConvertSettings baseSettings, int targetBytes,
        Func<int, PixelBuffer>? resampler, int maxRes, Action<int, int, int>? progress, out int attempts)
    {
        int srcSide = Math.Max(buf.Width, buf.Height);
        int hiSide = resampler != null ? Math.Max(maxRes, 2048) : srcSide; // loader caps at native size
        var hi = hiSide <= srcSide ? Downsample(buf, hiSide) : resampler!(hiSide);
        if (baseSettings.RemoveFlatBackground) hi = ConvertEngine.RemoveFlatBorderBackground(hi);
        var judge = Downsample(hi, 768);

        attempts = 0;
        ConvertResult? best = null;
        double bestErr = double.MaxValue;
        var shares = Environment.GetEnvironmentVariable("GT7_SHARE") is string fs
            ? new[] { double.Parse(fs, System.Globalization.CultureInfo.InvariantCulture) } : DetailShares;
        var found = new List<(ConvertResult R, double Err)>();
        foreach (double share in shares)
        {
            if (share > 0 && !baseSettings.KeepText) continue;
            int baseTarget = share == 0 ? targetBytes : (int)(targetBytes * (1 - share) / 1.12);
            var b = FindBase(buf, baseSettings, baseTarget, resampler, maxRes, progress, out int n);
            attempts += n;
            if (b == null) continue;
            var cand = share == 0 ? b : DetailOverlay.Compose(b, hi, targetBytes);
            double err = Objective(cand, judge);
            if (Environment.GetEnvironmentVariable("GT7_TRACE") == "1")
                Console.WriteLine($" share {share:F2}: {cand.Bytes}B base r{b.Width} c{b.ColorsUsed} words {cand.DetailWords} -> err {err:F2}");
            found.Add((cand, err));
        }
        if (found.Count == 0) return null;
        double minErr = found.Min(f => f.Err);
        // Keep-lettering: an average score barely notices a few letters, so accept up
        // to TextTolerance more overall error to keep more words readable.
        best = baseSettings.KeepText
            ? found.Where(f => f.Err <= minErr * (1 + TextTolerance))
                   .OrderByDescending(f => f.R.DetailWords).ThenBy(f => f.Err).First().R
            : found.MinBy(f => f.Err).R;
        return best;
    }

    public const double TextTolerance = 0.08;
    public static readonly double[] DetailShares = { 0, 0.15, 0.3, 0.45 };

    private static ConvertResult? FindBase(PixelBuffer buf, ConvertSettings baseSettings, int targetBytes,
        Func<int, PixelBuffer>? resampler, int maxRes, Action<int, int, int>? progress, out int attempts)
    {
        PixelBuffer Get(int side) =>
            side <= Math.Max(buf.Width, buf.Height) || resampler == null
                ? Downsample(buf, side) : resampler(side);

        var reference = Get(RefSide);
        if (baseSettings.RemoveFlatBackground)
            reference = ConvertEngine.RemoveFlatBorderBackground(reference);

        int[] resLadder = { 64, 80, 96, 112, 128, 144, 160, 192, 224, 256, 288, 320, 384, 448, 512, 640, 768, 1024 };
        int topRes = resampler != null ? maxRes : Math.Max(buf.Width, buf.Height);
        var resList = resLadder.Where(r => r <= topRes).ToList();
        if (resList.Count == 0) resList.Add(Math.Max(buf.Width, buf.Height));

        int maxC = Math.Max(2, baseSettings.MaxColors);
        var colorList = new[] { 2, 3, 4, 5, 6, 7, 8, 10, 12, 16, 24, 32 }
            .Where(c => c <= maxC).ToList();
        int[] despList = Tunables.DespeckleSet;

        var bufCache = new System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<PixelBuffer>>();
        PixelBuffer At(int r) => bufCache.GetOrAdd(r, k => new Lazy<PixelBuffer>(() => Get(k))).Value;

        int runs = 0;
        object gate = new();
        ConvertResult? best = null;
        double bestErr = double.MaxValue;

        var combos = colorList.SelectMany(c => despList.Select(d => (c, d))).ToList();
        Parallel.ForEach(combos, combo =>
        {
            var (colors, desp) = combo;
            foreach (int res in resList)
            {
                var b = At(res);
                var settings = new ConvertSettings
                {
                    MaxColors = colors,
                    DespecklePx = desp,
                    SeamFill = baseSettings.SeamFill,
                    RemoveFlatBackground = baseSettings.RemoveFlatBackground,
                    Stacked = baseSettings.Stacked,
                    ProtectContrast = baseSettings.ProtectContrast,
                    DetailScale = baseSettings.DetailScale
                };
                var traced = ConvertEngine.Trace(b, settings); // once; eps steps reuse it
                ConvertResult Try(double eps)
                {
                    Interlocked.Increment(ref runs);
                    settings.Epsilon = eps;
                    var r = ConvertEngine.Simplify(traced, settings);
                    r.Settings = new ConvertSettings
                    {
                        Epsilon = eps, MaxColors = colors, DespecklePx = desp, SeamFill = settings.SeamFill,
                        RemoveFlatBackground = settings.RemoveFlatBackground, Stacked = settings.Stacked,
                        ProtectContrast = settings.ProtectContrast, DetailScale = settings.DetailScale
                    };
                    return r;
                }

                double epsHi = Tunables.EpsMax, epsLo = Tunables.EpsMin;
                var loose = Try(epsHi);
                if (loose.Bytes > targetBytes) break; // higher res only gets bigger
                ConvertResult fit = loose;
                for (int i = 0; i < 6; i++)
                {
                    double mid = (epsLo + epsHi) / 2;
                    var r = Try(mid);
                    if (r.Bytes <= targetBytes) { fit = r; epsHi = mid; }
                    else epsLo = mid;
                }

                double err = Objective(fit, reference);
                if (Environment.GetEnvironmentVariable("GT7_TRACE") == "2")
                    Console.WriteLine($"  c{colors} d{desp} r{res} eps{fit.Settings.Epsilon:F2} {fit.Bytes}B err {err:F2}");
                lock (gate)
                {
                    if (err < bestErr) { bestErr = err; best = fit; }
                    progress?.Invoke(runs, fit.Bytes, targetBytes);
                }
            }
        });
        attempts = runs;
        return best;
    }

    // Alpha-weighted box downsample.
    public static PixelBuffer Downsample(PixelBuffer src, int maxSide)
    {
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
        if (scale >= 1.0) return src;
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = y * src.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * src.Height / h);
            for (int x = 0; x < w; x++)
            {
                int x0 = x * src.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * src.Width / w);
                long b = 0, g = 0, r = 0, a = 0; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = (yy * src.Width + xx) * 4;
                        int al = src.Pixels[i + 3];
                        b += src.Pixels[i] * al; g += src.Pixels[i + 1] * al; r += src.Pixels[i + 2] * al;
                        a += al; n++;
                    }
                int o = (y * w + x) * 4;
                if (a > 0) { dst[o] = (byte)(b / a); dst[o + 1] = (byte)(g / a); dst[o + 2] = (byte)(r / a); }
                dst[o + 3] = (byte)(a / n);
            }
        }
        return new PixelBuffer(w, h, dst);
    }

    // Even-odd scanline render of the layers at the reference size; mean abs RGB
    // error with both images composited over mid-gray.
    public static double Score(ConvertResult r, PixelBuffer reference)
    {
        int w = reference.Width, h = reference.Height;
        var outRgb = Render(r, w, h);
        var wt = EdgeWeights(reference);
        double sum = 0, tint = 0, wsum = 0;
        var p = reference.Pixels;
        for (int i = 0; i < w * h; i++)
        {
            double a = p[i * 4 + 3] / 255.0;
            int c = outRgb[i];
            int rr = c < 0 ? 128 : (c >> 16) & 255;
            int gg = c < 0 ? 128 : (c >> 8) & 255;
            int bb = c < 0 ? 128 : c & 255;
            double dr = p[i * 4 + 2] * a + 128 * (1 - a) - rr;
            double dg = p[i * 4 + 1] * a + 128 * (1 - a) - gg;
            double db = p[i * 4] * a + 128 * (1 - a) - bb;
            double k = wt[i];
            sum += k * (Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db));
            double m = (dr + dg + db) / 3;
            tint += k * (Math.Abs(dr - m) + Math.Abs(dg - m) + Math.Abs(db - m));
            wsum += k;
        }
        LastTint = tint / (wsum * 3.0);
        return sum / (wsum * 3.0);
    }

    // Pixels on sharp edges (lettering, outlines) count up to EdgeBoost x more.
    public const double EdgeBoost = 4.0;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PixelBuffer, float[]> _weights = new();
    private static float[] EdgeWeights(PixelBuffer reference) => _weights.GetValue(reference, b =>
    {
        int w = b.Width, h = b.Height;
        var y = new float[w * h];
        for (int i = 0; i < w * h; i++)
            y[i] = (0.299f * b.Pixels[i * 4 + 2] + 0.587f * b.Pixels[i * 4 + 1] + 0.114f * b.Pixels[i * 4]) * b.Pixels[i * 4 + 3] / 255f;
        var wt = new float[w * h];
        for (int j = 0; j < h; j++)
            for (int x = 0; x < w; x++)
            {
                int i = j * w + x;
                float gx = x > 0 && x < w - 1 ? Math.Abs(y[i + 1] - y[i - 1]) : 0;
                float gy = j > 0 && j < h - 1 ? Math.Abs(y[i + w] - y[i - w]) : 0;
                wt[i] = 1f + (float)EdgeBoost * Math.Min(1f, (gx + gy) / 160f);
            }
        return wt;
    });

    // Even-odd scanline render of the layers at w x h; packed RGB, -1 = transparent.
    public static int[] Render(ConvertResult r, int w, int h)
    {
        var outRgb = new int[w * h]; // -1 = transparent
        Array.Fill(outRgb, -1);
        double sx = (double)r.Width / w, sy = (double)r.Height / h;
        var xs = new List<double>();
        foreach (var L in r.Layers)
        {
            int col = (L.R << 16) | (L.G << 8) | L.B;
            for (int y = 0; y < h; y++)
            {
                double py = (y + 0.5) * sy;
                xs.Clear();
                foreach (var loop in L.Loops)
                    for (int i = 0; i < loop.Count; i++)
                    {
                        var a = loop[i];
                        var b = loop[(i + 1) % loop.Count];
                        double ay = Math.Round(a.Y), by = Math.Round(b.Y);
                        if ((ay <= py) == (by <= py)) continue;
                        double ax = Math.Round(a.X), bx = Math.Round(b.X);
                        xs.Add((ax + (py - ay) * (bx - ax) / (by - ay)) / sx);
                    }
                if (xs.Count < 2) continue;
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5));
                    int x1 = Math.Min(w - 1, (int)Math.Ceiling(xs[k + 1] - 0.5) - 1);
                    for (int x = x0; x <= x1; x++) outRgb[y * w + x] = col;
                }
            }
        }

        return outRgb;
    }

    [ThreadStatic] public static double LastTint;

    // What the search minimizes: overall error plus extra weight on wrong tint.
    public static double Objective(ConvertResult r, PixelBuffer reference)
    {
        double e = Score(r, reference);
        return e + TintPenalty * LastTint;
    }
    public const double TintPenalty = 2.0;
}
