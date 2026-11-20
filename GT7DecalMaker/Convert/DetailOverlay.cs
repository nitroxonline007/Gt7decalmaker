using System.Text;

namespace GT7DecalMaker.Convert;

// Adds a high-resolution "detail" layer on top of a coarse base trace: small,
// high-contrast shapes (lettering, badges, logos) that the base lost are traced
// again at a finer grid and painted over it. The base is rescaled onto the same
// grid so the whole file still uses one viewBox.
public static class DetailOverlay
{
    public static ConvertResult Compose(ConvertResult baseR, PixelBuffer hi, int targetBytes,
        double minContrast = -1, double eps = -1)
    {
        if (minContrast < 0) minContrast = Tunables.DetailMinContrast;
        if (eps < 0) eps = Tunables.DetailEps;
        int W = hi.Width, H = hi.Height;
        double kx = (double)W / baseR.Width, ky = (double)H / baseR.Height;

        var layers = new List<LayerResult>();
        foreach (var L in baseR.Layers)
        {
            var s = new LayerResult(L.R, L.G, L.B);
            foreach (var loop in L.Loops)
                s.Loops.Add(loop.Select(p => new Point(Math.Round(p.X * kx), Math.Round(p.Y * ky))).ToList());
            layers.Add(s);
        }
        var scaled = Build(baseR, layers, W, H);
        if (scaled.Bytes > targetBytes || baseR.Layers.Count == 0) return baseR;

        // Classify the hi-res image with the base palette.
        var pal = baseR.Layers.Select(l => new[] { l.R, l.G, l.B }).ToArray();
        Quantizer.Assign(hi, pal, out var map);
        var luma = pal.Select(c => 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]).ToArray();
        var baseRender = QualitySearch.Render(scaled, W, H);
        if (Environment.GetEnvironmentVariable("GT7_DUMP") is string dump)
        {
            var sbm = new StringBuilder();
            sbm.Append(W).Append(' ').Append(H).Append('\n');
            foreach (int v in map) sbm.Append((char)('a' + Math.Max(0, v)));
            System.IO.File.WriteAllText(dump, sbm.ToString());
            System.IO.File.WriteAllText(dump + ".pal", string.Join("\n", pal.Select(c => $"{c[0]} {c[1]} {c[2]}")));
        }

        // Label every same-color connected component.
        int maxSide = Math.Max(8, (int)(Math.Max(W, H) * 0.08));
        var comp = new int[W * H];
        Array.Fill(comp, -1);
        var comps = new List<Comp>();
        var stack = new Stack<int>();
        for (int start = 0; start < map.Length; start++)
        {
            int c = map[start];
            if (c < 0 || comp[start] >= 0) continue;
            var cm = new Comp { Color = c, X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1 };
            int id = comps.Count;
            comps.Add(cm);
            stack.Push(start); comp[start] = id;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % W, y = i / W;
                if (!cm.TooBig) cm.Px.Add(i);
                if (x < cm.X0) cm.X0 = x; if (x > cm.X1) cm.X1 = x;
                if (y < cm.Y0) cm.Y0 = y; if (y > cm.Y1) cm.Y1 = y;
                if (cm.X1 - cm.X0 >= maxSide || cm.Y1 - cm.Y0 >= maxSide) { cm.TooBig = true; cm.Px.Clear(); }
                void Visit(int j) { if (map[j] == c && comp[j] < 0) { comp[j] = id; stack.Push(j); } }
                if (x > 0) Visit(i - 1);
                if (x < W - 1) Visit(i + 1);
                if (y > 0) Visit(i - W);
                if (y < H - 1) Visit(i + W);
            }
        }
        // Who touches whom (only small components need it).
        for (int i = 0; i < map.Length; i++)
        {
            int a = comp[i];
            if (a < 0) continue;
            int x = i % W;
            if (x < W - 1) Link(comps, a, comp[i + 1]);
            if (i + W < map.Length) Link(comps, a, comp[i + W]);
        }

        // Find lettering: crisp shapes sitting on a single flat backing (a plate,
        // a badge) that the base trace got wrong. Engine speckle touches many
        // colors, so it scores low on purity.
        var letters = new List<int>();
        for (int id = 0; id < comps.Count; id++)
        {
            var cm = comps[id];
            if (cm.TooBig || cm.Px.Count < 4 || cm.Touch.Count == 0) continue;
            var topComp = cm.Touch.MaxBy(kv => kv.Value);
            int around = comps[topComp.Key].Color;
            int total = cm.Touch.Values.Sum();
            int same = cm.Touch.Where(kv => comps[kv.Key].Color == around).Sum(kv => kv.Value);
            double purity = (double)same / total;
            double contrast = ColorContrast(pal[cm.Color], pal[around]);
            if (contrast < minContrast || purity < Tunables.DetailPurity) continue;
            int wrong = 0;
            for (int y = cm.Y0; y <= cm.Y1; y++)
                for (int x = cm.X0; x <= cm.X1; x++)
                {
                    int i = y * W + x, v = map[i];
                    if (v >= 0 && baseRender[i] != ((pal[v][0] << 16) | (pal[v][1] << 8) | pal[v][2])) wrong++;
                }
            if (wrong * 10 < cm.Px.Count * 3) continue; // the base already shows it
            cm.Value = contrast * wrong * purity * purity;
            cm.Around = around;
            letters.Add(id);
        }

        // Group neighbouring letters into words / labels (union-find on padded boxes).
        var parent = Enumerable.Range(0, letters.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        // Per-cluster box and tallest letter, so words stay short horizontal strips.
        var cb = letters.Select(id => { var c = comps[id]; return (X0: c.X0, Y0: c.Y0, X1: c.X1, Y1: c.Y1, Hmax: c.Y1 - c.Y0 + 1); }).ToArray();
        for (int i = 0; i < letters.Count; i++)
            for (int j = i + 1; j < letters.Count; j++)
            {
                var p = comps[letters[i]]; var q = comps[letters[j]];
                int hp = p.Y1 - p.Y0 + 1, hq = q.Y1 - q.Y0 + 1;
                // Same word: similar letter height and the same backing color.
                if (p.Around != q.Around || Math.Max(hp, hq) > 2 * Math.Min(hp, hq)) continue;
                int gap = Math.Max(3, Math.Min(hp, hq) * 3 / 4);
                if (p.X0 - gap <= q.X1 && q.X0 - gap <= p.X1 && p.Y0 - gap <= q.Y1 && q.Y0 - gap <= p.Y1)
                {
                    int ri = Find(i), rj = Find(j);
                    if (ri == rj) continue;
                    var a = cb[ri]; var b = cb[rj];
                    var u = (X0: Math.Min(a.X0, b.X0), Y0: Math.Min(a.Y0, b.Y0), X1: Math.Max(a.X1, b.X1),
                             Y1: Math.Max(a.Y1, b.Y1), Hmax: Math.Max(a.Hmax, b.Hmax));
                    if (u.Y1 - u.Y0 + 1 > u.Hmax * 5 / 2 || u.X1 - u.X0 + 1 > W / 3) continue;
                    parent[ri] = rj; cb[rj] = u;
                }
            }
        var windows = letters.Select((id, i) => (id, root: Find(i))).GroupBy(t => t.root)
            .Select(g =>
            {
                int pad = 2;
                var m = g.Select(t => comps[t.id]).ToList();
                return new Window
                {
                    X0 = Math.Max(0, m.Min(c => c.X0) - pad), Y0 = Math.Max(0, m.Min(c => c.Y0) - pad),
                    X1 = Math.Min(W - 1, m.Max(c => c.X1) + pad), Y1 = Math.Min(H - 1, m.Max(c => c.Y1) + pad),
                    Letters = m.Count,
                    Value = m.Sum(c => c.Value) * Math.Sqrt(Math.Min(m.Count, 8)) // words (several letters) first
                };
            })
            .ToList();
        // Merge windows that overlap (e.g. a white "K" box and a red "20" box).
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < windows.Count && !merged; i++)
                for (int j = i + 1; j < windows.Count && !merged; j++)
                {
                    var p = windows[i]; var q = windows[j];
                    long Area(int a0, int b0, int a1, int b1) => (long)(a1 - a0 + 1) * (b1 - b0 + 1);
                    long union = Area(Math.Min(p.X0, q.X0), Math.Min(p.Y0, q.Y0), Math.Max(p.X1, q.X1), Math.Max(p.Y1, q.Y1));
                    if (p.X0 <= q.X1 && q.X0 <= p.X1 && p.Y0 <= q.Y1 && q.Y0 <= p.Y1
                        && union * 10 <= (Area(p.X0, p.Y0, p.X1, p.Y1) + Area(q.X0, q.Y0, q.X1, q.Y1)) * 13
                        && union <= (long)W * H / 30 && Math.Max(p.X1, q.X1) - Math.Min(p.X0, q.X0) < W / 3)
                    {
                        p.X0 = Math.Min(p.X0, q.X0); p.Y0 = Math.Min(p.Y0, q.Y0);
                        p.X1 = Math.Max(p.X1, q.X1); p.Y1 = Math.Max(p.Y1, q.Y1);
                        p.Value += q.Value; p.Letters += q.Letters;
                        windows.RemoveAt(j);
                        merged = true;
                    }
                }
        }
        windows = windows.Where(w => (long)(w.X1 - w.X0 + 1) * (w.Y1 - w.Y0 + 1) <= (long)W * H / 6).ToList();

        // Re-trace each window at full resolution with the same palette; stacked, so
        // it is opaque and fully replaces the base inside its rectangle.
        var palList = pal.Select(c => (c[0], c[1], c[2])).ToList();
        foreach (var win in windows)
        {
            int ww = win.X1 - win.X0 + 1, wh = win.Y1 - win.Y0 + 1;
            var crop = new byte[ww * wh * 4];
            for (int y = 0; y < wh; y++)
                Array.Copy(hi.Pixels, ((win.Y0 + y) * W + win.X0) * 4, crop, y * ww * 4, ww * 4);
            var r = ConvertEngine.Run(new PixelBuffer(ww, wh, crop), new ConvertSettings
            {
                Epsilon = eps, DespecklePx = Tunables.DetailDespeckle, SeamFill = baseR.Settings.SeamFill,
                RemoveFlatBackground = false, Stacked = true, DetailScale = 0, PaletteOverride = palList
            });
            foreach (var L in r.Layers)
            {
                var moved = new LayerResult(L.R, L.G, L.B);
                foreach (var loop in L.Loops)
                {
                    var ml = loop.Select(p => new Point(p.X + win.X0, p.Y + win.Y0)).ToList();
                    var sb = new StringBuilder();
                    SvgWriter.EncodeLoop(sb, ml);
                    win.Cost += sb.Length;
                    moved.Loops.Add(ml);
                }
                win.Layers.Add(moved);
            }
        }

        // Most value per byte first; skip windows that overlap one already taken.
        int bytes = scaled.Bytes;
        const int PathOverhead = 60; // <path d="" fill="#..." .../> per stage/color group
        var groups = new HashSet<(int Stage, byte R, byte G, byte B)>();
        var taken = new List<Window>();
        foreach (var win in windows.Where(w => w.Cost > 0).OrderByDescending(w => w.Value / w.Cost))
        {
            if (taken.Any(t => t.X0 <= win.X1 && win.X0 <= t.X1 && t.Y0 <= win.Y1 && win.Y0 <= t.Y1)) continue;
            int cost = win.Cost + win.Layers.Select((l, k) => (k, l.R, l.G, l.B)).Count(g => !groups.Contains(g)) * PathOverhead;
            if (bytes + cost > targetBytes) continue;
            bytes += cost;
            taken.Add(win);
            for (int k = 0; k < win.Layers.Count; k++) groups.Add((k, win.Layers[k].R, win.Layers[k].G, win.Layers[k].B));
        }

        if (Environment.GetEnvironmentVariable("GT7_TRACE") == "1")
            Console.WriteLine($"   detail: {letters.Count} letters, {windows.Count} windows, {taken.Count} taken, base {scaled.Bytes}B -> {bytes}B");
        // Windows don't overlap, so all windows' k-th layers can share one path per color.
        ConvertResult Compose(List<Window> ws)
        {
            var all = new List<LayerResult>(layers);
            int stages = ws.Count == 0 ? 0 : ws.Max(t => t.Layers.Count);
            for (int k = 0; k < stages; k++)
                foreach (var g in ws.Where(t => k < t.Layers.Count).Select(t => t.Layers[k]).GroupBy(l => (l.R, l.G, l.B)))
                {
                    var layer = new LayerResult(g.Key.R, g.Key.G, g.Key.B);
                    foreach (var l in g) layer.Loops.AddRange(l.Loops);
                    all.Add(layer);
                }
            return Build(baseR, all, W, H);
        }
        var result = Compose(taken);
        // Estimates are close but not exact: drop the least valuable whole window
        // until it fits (never leave a word half drawn).
        while (result.Bytes > targetBytes && taken.Count > 0)
        {
            taken.Remove(taken.MinBy(t => t.Value / t.Cost)!);
            result = Compose(taken);
        }
        result.DetailWords = taken.Count(t => t.Letters >= 2);
        return result.Bytes <= targetBytes ? result : baseR;
    }

    private sealed class Window
    {
        public int X0, Y0, X1, Y1, Cost, Letters;
        public double Value;
        public List<LayerResult> Layers = new();
    }

    private sealed class Comp
    {
        public int Color, X0, Y0, X1, Y1, Wrong, Cost, Enclosing = -1, Around = -1;
        public bool TooBig, Inner, NeedsPatch;
        public List<Point>? PatchLoop;
        public double Value;
        public List<int> Px = new();
        public Dictionary<int, int> Touch = new();
        public List<List<Point>> Loops = new();
    }

    // Perceptual color difference on a brightness-like 0-255 scale (red on black counts).
    private static double ColorContrast(byte[] a, byte[] b)
        => Math.Sqrt(Quantizer.Dist(a[0] - b[0], a[1] - b[1], a[2] - b[2]) / 7.0);

    private static void Link(List<Comp> comps, int a, int b)
    {
        if (b < 0 || a == b) return;
        if (!comps[a].TooBig) comps[a].Touch[b] = comps[a].Touch.GetValueOrDefault(b) + 1;
        if (!comps[b].TooBig) comps[b].Touch[a] = comps[b].Touch.GetValueOrDefault(a) + 1;
    }

    private static void TraceComp(Comp cm, int W, double eps)
    {
        int bw = cm.X1 - cm.X0 + 1, bh = cm.Y1 - cm.Y0 + 1;
        var mask = new bool[bw * bh];
        foreach (int i in cm.Px) mask[(i / W - cm.Y0) * bw + (i % W - cm.X0)] = true;
        foreach (var loop in Tracer.TraceLoops(mask, bw, bh))
        {
            var sl = Simplifier.SimplifyLoop(loop, eps);
            if (sl.Count < 3) continue;
            var moved = sl.Select(p => new Point(p.X + cm.X0, p.Y + cm.Y0)).ToList();
            var sb = new StringBuilder();
            SvgWriter.EncodeLoop(sb, moved);
            cm.Cost += sb.Length;
            cm.Loops.Add(moved);
        }
    }

    private static ConvertResult Build(ConvertResult baseR, List<LayerResult> layers, int w, int h)
    {
        string xml = SvgWriter.Build(w, h, layers, baseR.Settings.SeamFill);
        return new ConvertResult
        {
            Xml = xml,
            Bytes = SvgWriter.SizeOf(xml),
            Width = w,
            Height = h,
            ColorsUsed = baseR.ColorsUsed,
            LoopCount = layers.Sum(l => l.Loops.Count),
            Settings = baseR.Settings,
            Layers = layers
        };
    }
}
