namespace GT7DecalMaker.Convert;

public static class Tracer
{
    // Removes connected components smaller than minArea pixels from the index map.
    // Specks that differ from their surroundings by at least protectContrast in
    // brightness (0-255) are kept: those are text strokes and fine lettering, not noise.
    public static void Despeckle(int[] indexMap, int w, int h, int paletteCount, int minArea,
        double[]? paletteLuma = null, double protectContrast = 0)
    {
        if (minArea <= 1) return;
        var opaque = new bool[indexMap.Length];
        for (int i = 0; i < indexMap.Length; i++) opaque[i] = indexMap[i] >= 0;
        var visited = new bool[indexMap.Length];
        var stack = new Stack<int>();

        for (int layer = 0; layer < paletteCount; layer++)
        {
            Array.Clear(visited);
            for (int start = 0; start < indexMap.Length; start++)
            {
                if (indexMap[start] != layer || visited[start]) continue;
                var members = new List<int>();
                var touch = new Dictionary<int, int>();
                stack.Push(start);
                visited[start] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    members.Add(i);
                    int x = i % w, y = i / w;
                    void Visit(int j)
                    {
                        int v = indexMap[j];
                        if (v == layer) { if (!visited[j]) { visited[j] = true; stack.Push(j); } }
                        else if (v >= 0) touch[v] = touch.GetValueOrDefault(v) + 1;
                    }
                    if (x > 0) Visit(i - 1);
                    if (x < w - 1) Visit(i + 1);
                    if (y > 0) Visit(i - w);
                    if (y < h - 1) Visit(i + w);
                }
                if (members.Count >= minArea) continue;
                if (paletteLuma != null && protectContrast > 0 && touch.Count > 0 && members.Count >= 2)
                {
                    int around = touch.MaxBy(kv => kv.Value).Key;
                    if (Math.Abs(paletteLuma[layer] - paletteLuma[around]) >= protectContrast)
                        continue; // crisp detail, keep it
                }
                foreach (var m in members) indexMap[m] = -2; // pending refill
            }
        }

        // Refill removed specks from neighboring colors so no holes open up.
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < indexMap.Length; i++)
            {
                if (indexMap[i] != -2) continue;
                int x = i % w, y = i / w, v = -2;
                if (x > 0 && indexMap[i - 1] >= 0) v = indexMap[i - 1];
                else if (x < w - 1 && indexMap[i + 1] >= 0) v = indexMap[i + 1];
                else if (y > 0 && indexMap[i - w] >= 0) v = indexMap[i - w];
                else if (y < h - 1 && indexMap[i + w] >= 0) v = indexMap[i + w];
                if (v >= 0) { indexMap[i] = v; changed = true; }
            }
        }
        for (int i = 0; i < indexMap.Length; i++)
            if (indexMap[i] == -2) indexMap[i] = -1;
        _ = opaque;
    }

    private const byte DirE = 0, DirS = 1, DirW = 2, DirN = 3;

    // Traces closed boundary loops of a binary mask by stitching directed edges
    // (fill kept on the right side of travel), preferring right turns at corner
    // ambiguities so diagonal pinch points stay tight. Vertices are integer pixel corners.
    public static List<List<Point>> TraceLoops(bool[] mask, int w, int h)
    {
        int vw = w + 1;

        var fromV = new List<int>();
        var toV = new List<int>();
        var dirE = new List<byte>();
        var used = new List<bool>();
        var adj = new Dictionary<int, List<int>>();

        void AddEdge(int from, int to, byte dir)
        {
            if (!adj.TryGetValue(from, out var list))
                adj[from] = list = new List<int>(4);
            list.Add(fromV.Count);
            fromV.Add(from);
            toV.Add(to);
            dirE.Add(dir);
            used.Add(false);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!mask[y * w + x]) continue;
                int tl = y * vw + x;
                int tr = tl + 1, bl = tl + vw, br = bl + 1;
                if (y == 0 || !mask[(y - 1) * w + x]) AddEdge(tl, tr, DirE);
                if (x == w - 1 || !mask[y * w + x + 1]) AddEdge(tr, br, DirS);
                if (y == h - 1 || !mask[(y + 1) * w + x]) AddEdge(br, bl, DirW);
                if (x == 0 || !mask[y * w + x - 1]) AddEdge(bl, tl, DirN);
            }
        }

        int totalEdges = fromV.Count;
        var loops = new List<List<Point>>();

        Span<byte> pref = stackalloc byte[4];
        for (int e0 = 0; e0 < totalEdges; e0++)
        {
            if (used[e0]) continue;
            var loop = new List<Point>();
            int cur = e0;
            int guard = totalEdges + 8;

            while (guard-- > 0)
            {
                loop.Add(new Point(fromV[cur] % vw, fromV[cur] / vw));
                used[cur] = true;
                int at = toV[cur];

                pref[0] = (byte)((dirE[cur] + 1) & 3); // right turn
                pref[1] = dirE[cur];                   // straight
                pref[2] = (byte)((dirE[cur] + 3) & 3); // left turn
                pref[3] = (byte)((dirE[cur] + 2) & 3); // back

                int next = -1;
                if (adj.TryGetValue(at, out var cands))
                {
                    foreach (byte p in pref)
                    {
                        foreach (int ei in cands)
                        {
                            if (!used[ei] && dirE[ei] == p) { next = ei; break; }
                        }
                        if (next >= 0) break;
                    }
                }

                if (next < 0)
                {
                    loop.Add(new Point(at % vw, at / vw)); // close the loop explicitly
                    break;
                }
                cur = next;
            }

            if (loop.Count >= 4) loops.Add(loop);
        }
        return loops;
    }
}
