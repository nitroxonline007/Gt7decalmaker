namespace GT7DecalMaker.Convert;

public static class Quantizer
{
    // Pixels softer than this are treated as background - prevents translucent
    // edge/glow halos from spawning muddy intermediate palette classes.
    public const byte AlphaThreshold = 160;

    // Median-cut color quantization refined with k-means (Lloyd) iterations.
    public static List<(byte R, byte G, byte B)> BuildPalette(PixelBuffer buf, int maxColors, out int[] indexMap)
    {
        int w = buf.Width, h = buf.Height;
        var px = buf.Pixels;
        int n = w * h;

        var samples = new List<byte[]>(n);
        for (int i = 0; i < n; i++)
        {
            if (px[i * 4 + 3] >= AlphaThreshold)
                samples.Add(new[] { px[i * 4 + 2], px[i * 4 + 1], px[i * 4] }); // R,G,B
        }

        if (samples.Count == 0)
            throw new InvalidOperationException("Image is fully transparent - nothing to convert.");

        var boxes = new List<Box> { new Box(samples) };
        while (boxes.Count < maxColors)
        {
            Box? best = null;
            double bestScore = -1;
            foreach (var b in boxes)
            {
                if (b.Samples.Count < 2 || b.LongestRange <= 0) continue;
                double score = b.LongestRange * Math.Sqrt(b.Samples.Count);
                if (score > bestScore) { bestScore = score; best = b; }
            }
            if (best == null) break;
            boxes.Remove(best);
            foreach (var half in best.Split()) boxes.Add(half);
        }

        var cents = new byte[boxes.Count][];
        for (int i = 0; i < boxes.Count; i++)
            cents[i] = boxes[i].Average();

        // Perceptual merge of near-duplicate entries (antialiasing leftovers).
        var weights = boxes.Select(b => (long)b.Samples.Count).ToArray();
        cents = MergeNearest(cents, weights, MergeDistance: 60);

        // Lloyd iterations pull centroids to the true density peaks.
        KMeans(samples, cents, iterations: 10);

        var palette = new List<(byte, byte, byte)>(cents.Length);
        foreach (var c in cents) palette.Add((c[0], c[1], c[2]));

        Assign(buf, cents, out indexMap);
        return palette;
    }

    // Assigns every opaque pixel to its nearest palette entry (luma-weighted RGB).
    public static void Assign(PixelBuffer buf, byte[][] palette, out int[] indexMap)
    {
        var px = buf.Pixels;
        int n = buf.Width * buf.Height;
        indexMap = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (px[i * 4 + 3] < AlphaThreshold) { indexMap[i] = -1; continue; }
            // BGRA layout: [+0]=B [+1]=G [+2]=R [+3]=A
            int b0 = px[i * 4], g0 = px[i * 4 + 1], r0 = px[i * 4 + 2];
            int bi = 0; long bd = long.MaxValue;
            for (int p = 0; p < palette.Length; p++)
            {
                long dr = r0 - palette[p][0], dg = g0 - palette[p][1], db = b0 - palette[p][2];
                long d = Dist(dr, dg, db);
                if (d < bd) { bd = d; bi = p; }
            }
            indexMap[i] = bi;
        }
    }

    // Pixel count per palette entry (for weight-preserving reductions).
    public static long[] CountClasses(int[] indexMap, int paletteCount)
    {
        var counts = new long[paletteCount];
        foreach (var m in indexMap)
            if (m >= 0) counts[m]++;
        return counts;
    }

    // Repeatedly merges the perceptually closest palette pair while a hue guard
    // protects distinct saturated color families (accent colors survive cuts).
    public static List<(byte R, byte G, byte B)> ReducePaletteHueGuarded(
        List<(byte R, byte G, byte B)> palette, long[] weights, int targetCount)
    {
        var cols = palette.Select(p => new[] { p.R, p.G, p.B }).ToArray();
        var w = (long[])weights.Clone();

        while (cols.Length > Math.Max(1, targetCount))
        {
            int bi = -1, bj = -1;
            long bd = long.MaxValue;
            long guarded = long.MaxValue;
            int gi = -1, gj = -1;
            for (int i = 0; i < cols.Length; i++)
                for (int j = i + 1; j < cols.Length; j++)
                {
                    long d = ColorDist(cols[i], cols[j]);
                    if (d < bd) { bd = d; bi = i; bj = j; }
                    if (d < guarded && !HueGuardBlocks(cols[i], cols[j])) { guarded = d; gi = i; gj = j; }
                }

            if (gi >= 0)
            {
                bi = gi; bj = gj;           // prefer an allowed pair
            }
            else if (bi < 0)
            {
                break;                       // nothing left to merge
            }
            // else: every pair is hue-guarded -> force the closest one

            long tw = w[bi] + w[bj];
            for (int c = 0; c < 3; c++)
                cols[bi][c] = (byte)Math.Clamp((cols[bi][c] * w[bi] + cols[bj][c] * w[bj]) / Math.Max(1, tw), 0, 255);
            w[bi] = tw;
            cols = cols.Where((_, idx) => idx != bj).ToArray();
            w = w.Where((_, idx) => idx != bj).ToArray();
        }

        return cols.Select(c => ((byte)c[0], (byte)c[1], (byte)c[2])).ToList();
    }

    // Luma-weighted RGB distance plus a tint term: the off-gray part of the
    // difference is weighted heavily, so silver never reads as "close" to pale green.
    public const int TintWeight = 3;
    internal static long Dist(long dr, long dg, long db)
    {
        long m = (dr + dg + db) / 3;
        long cr = dr - m, cg = dg - m, cb = db - m;
        return dr * dr * 2 + dg * dg * 4 + db * db + TintWeight * 3 * (cr * cr + cg * cg + cb * cb);
    }

    private static long ColorDist(byte[] a, byte[] b)
    {
        long dr = a[0] - b[0], dg = a[1] - b[1], db = a[2] - b[2];
        return Dist(dr, dg, db);
    }

    // True when both colors are saturated accents of clearly different hue.
    private static bool HueGuardBlocks(byte[] a, byte[] b)
    {
        var (sa, ha) = SaturationHue(a);
        var (sb, hb) = SaturationHue(b);
        if (sa < 0.4 || sb < 0.4) return false;      // grays/pastels may merge freely
        double dh = Math.Abs(ha - hb);
        if (dh > 180) dh = 360 - dh;
        return dh > 30;                               // distinct accent families
    }

    private static (double S, double H) SaturationHue(byte[] rgb)
    {
        double r = rgb[0] / 255.0, g = rgb[1] / 255.0, b = rgb[2] / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double s = max <= 0 ? 0 : d / max;
        if (d <= 0) return (s, 0);
        double h;
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (s, h);
    }

    private static byte[][] MergeNearest(byte[][] cents, long[] weights, long MergeDistance)
    {
        bool merged = true;
        while (merged && cents.Length > 2)
        {
            merged = false;
            int bi = -1, bj = -1;
            long bd = long.MaxValue;
            for (int i = 0; i < cents.Length; i++)
                for (int j = i + 1; j < cents.Length; j++)
                {
                    long d = ColorDist(cents[i], cents[j]);
                    if (d < bd) { bd = d; bi = i; bj = j; }
                }
            if (bd < MergeDistance)
            {
                long tw = weights[bi] + weights[bj];
                for (int c = 0; c < 3; c++)
                    cents[bi][c] = (byte)Math.Clamp((cents[bi][c] * weights[bi] + cents[bj][c] * weights[bj]) / tw, 0, 255);
                weights[bi] = tw;
                cents = cents.Where((_, idx) => idx != bj).ToArray();
                weights = weights.Where((_, idx) => idx != bj).ToArray();
                merged = true;
            }
        }
        return cents;
    }

    private static void KMeans(List<byte[]> samples, byte[][] cents, int iterations)
    {
        var sums = new long[cents.Length][];
        var counts = new long[cents.Length];
        for (int i = 0; i < cents.Length; i++) sums[i] = new long[3];

        for (int it = 0; it < iterations; it++)
        {
            foreach (var s in sums) { s[0] = 0; s[1] = 0; s[2] = 0; }
            Array.Clear(counts);

            for (int si = 0; si < samples.Count; si++)
            {
                var smp = samples[si];
                int bi = 0; long bd = long.MaxValue;
                for (int p = 0; p < cents.Length; p++)
                {
                    long dr = smp[0] - cents[p][0], dg = smp[1] - cents[p][1], db = smp[2] - cents[p][2];
                    long d = Dist(dr, dg, db);
                    if (d < bd) { bd = d; bi = p; }
                }
                sums[bi][0] += smp[0]; sums[bi][1] += smp[1]; sums[bi][2] += smp[2];
                counts[bi]++;
            }

            double moved = 0;
            for (int p = 0; p < cents.Length; p++)
            {
                if (counts[p] == 0) continue;
                for (int c = 0; c < 3; c++)
                {
                    byte nv = (byte)Math.Clamp(sums[p][c] / counts[p], 0, 255);
                    moved += Math.Abs(nv - cents[p][c]);
                    cents[p][c] = nv;
                }
            }
            if (moved < 3) break; // converged
        }
    }

    private sealed class Box
    {
        public List<byte[]> Samples;

        public Box(List<byte[]> s) { Samples = s; Compute(); }

        public int LongestRange;
        public int Axis;
        private byte _minR, _maxR, _minG, _maxG, _minB, _maxB;

        private void Compute()
        {
            _minR = _minG = _minB = 255;
            _maxR = _maxG = _maxB = 0;
            foreach (var s in Samples)
            {
                if (s[0] < _minR) _minR = s[0]; if (s[0] > _maxR) _maxR = s[0];
                if (s[1] < _minG) _minG = s[1]; if (s[1] > _maxG) _maxG = s[1];
                if (s[2] < _minB) _minB = s[2]; if (s[2] > _maxB) _maxB = s[2];
            }
            int rr = _maxR - _minR, rg = _maxG - _minG, rb = _maxB - _minB;
            Axis = rr >= rg && rr >= rb ? 0 : (rg >= rb ? 1 : 2);
            LongestRange = Axis == 0 ? rr : Axis == 1 ? rg : rb;
        }

        public IEnumerable<Box> Split()
        {
            int axis = Axis;
            Samples.Sort((a, b) => a[axis].CompareTo(b[axis]));
            int mid = Samples.Count / 2;
            yield return new Box(Samples.GetRange(0, mid));
            yield return new Box(Samples.GetRange(mid, Samples.Count - mid));
        }

        public byte[] Average()
        {
            long r = 0, g = 0, b = 0;
            foreach (var s in Samples) { r += s[0]; g += s[1]; b += s[2]; }
            int c = Samples.Count;
            return new[]
            {
                (byte)Math.Clamp(r / c, 0, 255),
                (byte)Math.Clamp(g / c, 0, 255),
                (byte)Math.Clamp(b / c, 0, 255)
            };
        }
    }
}
