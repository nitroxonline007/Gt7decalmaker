using System.Globalization;
using System.Text;

namespace GT7DecalMaker.Convert;

public static class SvgWriter
{
    public static string Build(int width, int height, List<LayerResult> layers, bool seamFill)
    {
        var sb = new StringBuilder(4096);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
          .Append(width).Append(' ').Append(height).Append("\">");

        foreach (var layer in layers)
        {
            if (layer.Loops.Count == 0) continue;
            string hex = ToHex(layer.R, layer.G, layer.B);
            sb.Append("<path d=\"");
            foreach (var loop in layer.Loops) EncodeLoop(sb, loop);
            sb.Append("\" fill=\"").Append(hex).Append("\" fill-rule=\"evenodd\"");
            if (seamFill)
                sb.Append(" stroke=\"").Append(hex).Append("\" stroke-width=\".5\"");
            sb.Append("/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    internal static void EncodeLoop(StringBuilder sb, List<Point> loop)
    {
        int startX = (int)Math.Round(loop[0].X);
        int startY = (int)Math.Round(loop[0].Y);
        sb.Append('M').Append(startX).Append(' ').Append(startY);

        // Axis-aligned moves use h/v (one number instead of two). The command
        // letter is only written when it changes, as SVG allows repeats.
        int px = startX, py = startY;
        char cmd = 'M';
        int n = loop.Count;
        // The closing edge back to start is implied by 'z', so skip a final point equal to start.
        for (int i = 1; i < n; i++)
        {
            int x = (int)Math.Round(loop[i].X);
            int y = (int)Math.Round(loop[i].Y);
            int dx = x - px, dy = y - py;
            if (dx == 0 && dy == 0) continue;
            if (i == n - 1 && x == startX && y == startY) break;
            char want = dy == 0 ? 'h' : dx == 0 ? 'v' : 'l';
            bool fresh = want != cmd;
            if (fresh) { sb.Append(want); cmd = want; }
            if (want == 'h') AppendNum(sb, dx, fresh);
            else if (want == 'v') AppendNum(sb, dy, fresh);
            else { AppendNum(sb, dx, fresh); AppendNum(sb, dy, false); }
            px = x; py = y;
        }
        sb.Append('z');
    }

    private static void AppendNum(StringBuilder sb, int v, bool firstAfterCommand)
    {
        if (v < 0) sb.Append('-').Append(-v);          // '-' doubles as separator
        else if (firstAfterCommand) sb.Append(v);       // command char already precedes
        else sb.Append(' ').Append(v);
    }

    public static string ToHex(byte r, byte g, byte b)
    {
        if ((r & 0x0F) == (r >> 4) && (g & 0x0F) == (g >> 4) && (b & 0x0F) == (b >> 4))
            return string.Create(4, (r >> 4, g >> 4, b >> 4), static (span, s) =>
            {
                span[0] = '#';
                span[1] = ToHexDigit(s.Item1);
                span[2] = ToHexDigit(s.Item2);
                span[3] = ToHexDigit(s.Item3);
            });
        return string.Create(7, (r, g, b), static (span, s) =>
        {
            span[0] = '#';
            span[1] = ToHexDigit(s.r >> 4);
            span[2] = ToHexDigit(s.r & 0xF);
            span[3] = ToHexDigit(s.g >> 4);
            span[4] = ToHexDigit(s.g & 0xF);
            span[5] = ToHexDigit(s.b >> 4);
            span[6] = ToHexDigit(s.b & 0xF);
        });
    }

    private static char ToHexDigit(int v) => "0123456789abcdef"[v & 0xF];

    public static int SizeOf(string xml) => Encoding.UTF8.GetByteCount(xml);
}
