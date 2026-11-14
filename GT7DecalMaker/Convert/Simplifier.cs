namespace GT7DecalMaker.Convert;

public static class Simplifier
{
    public static List<Point> SimplifyLoop(List<Point> loop, double eps)
    {
        // Remove duplicate and collinear vertices first.
        // All input coordinates are integers, so exact-zero cross products are safe here.
        var pts = new List<Point>(loop.Count);
        int n = loop.Count;
        for (int i = 0; i < n; i++)
        {
            var prev = loop[(i - 1 + n) % n];
            var cur = loop[i];
            var next = loop[(i + 1) % n];
            if (Dist2(cur, prev) < 0.01) continue;
            double cross = (cur.X - prev.X) * (next.Y - prev.Y) - (cur.Y - prev.Y) * (next.X - prev.X);
            double fwdX = next.X - cur.X, fwdY = next.Y - cur.Y;
            double dot = (cur.X - prev.X) * fwdX + (cur.Y - prev.Y) * fwdY;
            if (Math.Abs(cross) < 0.01 && dot > 0) continue; // straight continuation, drop vertex
            pts.Add(cur);
        }
        if (pts.Count < 3) return pts;

        if (eps <= 0.01) return pts;

        // Closed-loop RDP: split at two far-apart anchors and simplify each half.
        int b = 0; double bestD = -1;
        for (int i = 1; i < pts.Count; i++)
        {
            double d = Dist2(pts[0], pts[i]);
            if (d > bestD) { bestD = d; b = i; }
        }

        var seg1 = Rdp(pts, 0, b + 1, eps);
        var seg2 = Rdp(pts, b, pts.Count, eps);
        var result = new List<Point>(seg1.Count + seg2.Count);
        result.AddRange(seg1);
        if (seg2.Count > 0) result.AddRange(seg2.Skip(1));
        return result;
    }

    private static List<Point> Rdp(List<Point> pts, int from, int to, double eps)
    {
        var keep = new bool[to - from];
        keep[0] = true;
        keep[^1] = true;
        var stack = new Stack<(int F, int T)>();
        stack.Push((from, to));
        double eps2 = eps * eps;

        while (stack.Count > 0)
        {
            var (f, t) = stack.Pop();
            if (t - f < 3) continue;
            double maxD = -1; int maxI = -1;
            var pa = pts[f]; var pb = pts[t - 1];
            double dx = pb.X - pa.X, dy = pb.Y - pa.Y;
            double len2 = dx * dx + dy * dy;
            for (int i = f + 1; i < t - 1; i++)
            {
                double d2;
                if (len2 < 1e-12)
                {
                    d2 = Dist2(pts[i], pa);
                }
                else
                {
                    double tt = ((pts[i].X - pa.X) * dx + (pts[i].Y - pa.Y) * dy) / len2;
                    tt = Math.Clamp(tt, 0.0, 1.0);
                    double px = pa.X + tt * dx - pts[i].X;
                    double py = pa.Y + tt * dy - pts[i].Y;
                    d2 = px * px + py * py;
                }
                if (d2 > maxD) { maxD = d2; maxI = i; }
            }
            if (maxD > eps2)
            {
                keep[maxI - from] = true;
                stack.Push((f, maxI));
                stack.Push((maxI, t));
            }
        }

        var result = new List<Point>();
        for (int i = 0; i < keep.Length; i++)
            if (keep[i]) result.Add(pts[from + i]);
        return result;
    }

    private static double Dist2(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
