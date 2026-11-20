using System.Globalization;
using System.IO;
using System.Text;
using GT7DecalMaker.Convert;

// Tuning gym: converts every image in a folder under the byte limit, scores each
// result against its original with a FIXED judge, and tunes the converter's
// dials (Tunables) one at a time to minimize the average score.
//
//   dotnet run -c Release -- gym ../gym/images [targetBytes] [passes]
public static class Gym
{
    const int JudgeSide = 1024;

    sealed record Sample(string Name, string Path, PixelBuffer Buf, PixelBuffer Judge);

    // Each dial: a name, how to read it, and candidate values to try.
    sealed record Dial(string Name, Func<string> Get, (string Label, Action Set)[] Options);

    public static int Run(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "../gym/images";
        int target = args.Length > 1 ? int.Parse(args[1]) : 15_000;
        int passes = args.Length > 2 ? int.Parse(args[2]) : 1;
        string outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir))!, "out");
        Directory.CreateDirectory(outDir);

        var samples = Directory.GetFiles(dir)
            .Where(f => new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .Select(f =>
            {
                var buf = ConvertEngine.LoadImage(f, 384);
                var judge = ConvertEngine.RemoveFlatBorderBackground(ConvertEngine.LoadImage(f, JudgeSide));
                return new Sample(Path.GetFileNameWithoutExtension(f), f, buf, judge);
            }).ToList();
        Console.WriteLine($"Gym: {samples.Count} images, target {target} bytes, {passes} pass(es)");

        var dials = Dials();
        var log = new StringBuilder();
        void Log(string s) { Console.WriteLine(s); log.AppendLine(s); }

        var (baseAvg, baseScores, baseResults) = Evaluate(samples, target);
        Log($"baseline avg {baseAvg:F3}  [{Settings(dials)}]");
        SaveRenders(samples, baseResults, outDir, "before");
        double best = baseAvg;
        var bestScores = baseScores;
        var bestResults = baseResults;

        for (int pass = 1; pass <= passes; pass++)
        {
            bool improved = false;
            foreach (var dial in dials)
            {
                string current = dial.Get();
                Action? keep = null;
                foreach (var (label, set) in dial.Options)
                {
                    if (label == current) continue;
                    var restore = dial.Options.First(o => o.Label == current).Set;
                    set();
                    var (avg, scores, results) = Evaluate(samples, target);
                    Log($"  pass {pass} {dial.Name}={label}: avg {avg:F3} {(avg < best ? "BETTER" : "")}");
                    if (avg < best - 0.005)
                    {
                        best = avg; bestScores = scores; bestResults = results;
                        keep = set; current = label;
                        improved = true;
                    }
                    else restore();
                }
                keep?.Invoke();
                Log($" {dial.Name} -> {dial.Get()}  (avg {best:F3})");
            }
            if (!improved) break;
        }

        Log("");
        Log($"RESULT avg {baseAvg:F3} -> {best:F3} ({(baseAvg - best) / baseAvg * 100:F1}% lower error)");
        Log($"tuned [{Settings(dials)}]");
        for (int i = 0; i < samples.Count; i++)
            Log($"  {samples[i].Name,-18} {baseScores[i],7:F2} -> {bestScores[i],7:F2}  ({bestResults[i].Bytes} B)");
        SaveRenders(samples, bestResults, outDir, "after");
        File.WriteAllText(Path.Combine(outDir, "report.txt"), log.ToString());
        return 0;
    }

    static (double Avg, double[] Scores, ConvertResult[] Results) Evaluate(List<Sample> samples, int target)
    {
        var scores = new double[samples.Count];
        var results = new ConvertResult[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var settings = new ConvertSettings { Epsilon = 2.25, MaxColors = 16, DespecklePx = 4, SeamFill = true, RemoveFlatBackground = true, KeepText = true };
            var (fit, _, _) = ConvertEngine.AutoFit(s.Buf, settings, target, null, r => ConvertEngine.LoadImage(s.Path, r), 1024);
            if (fit.Bytes > target) throw new Exception($"{s.Name}: {fit.Bytes} B over limit");
            results[i] = fit;
            scores[i] = Judge(fit, s.Judge);
        }
        return (scores.Average(), scores, results);
    }

    // Fixed judge (independent of the dials): edge-weighted color error plus 2x tint error.
    static double Judge(ConvertResult r, PixelBuffer reference)
    {
        double e = QualitySearch.Score(r, reference);
        return e + 2.0 * QualitySearch.LastTint;
    }

    static List<Dial> Dials()
    {
        string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        Dial D(string name, Func<double> get, Action<double> set, params double[] vals) =>
            new(name, () => F(get()), vals.Select(v => (F(v), (Action)(() => set(v)))).ToArray());

        var despOptions = new[] { new[] { 3, 6, 12 }, new[] { 4, 8, 16 }, new[] { 6, 12, 24 }, new[] { 4, 8, 16, 32 } };
        return new List<Dial>
        {
            D("TintWeight", () => Tunables.TintWeight, v => Tunables.TintWeight = v, 1, 3, 6),
            new("DespeckleSet", () => string.Join("/", Tunables.DespeckleSet),
                despOptions.Select(o => (string.Join("/", o), (Action)(() => Tunables.DespeckleSet = o))).ToArray()),
            D("EpsMax", () => Tunables.EpsMax, v => Tunables.EpsMax = v, 2.5, 3.5, 5),
            D("DetailScale", () => Tunables.DetailScale, v => Tunables.DetailScale = v, 0, 0.12, 0.25),
            D("DetailEps", () => Tunables.DetailEps, v => Tunables.DetailEps = v, 0.4, 0.6, 0.9),
            D("DetailPurity", () => Tunables.DetailPurity, v => Tunables.DetailPurity = v, 0.7, 0.8, 0.9),
            D("DetailMinContrast", () => Tunables.DetailMinContrast, v => Tunables.DetailMinContrast = v, 50, 70, 100),
        };
    }

    static string Settings(List<Dial> dials) => string.Join(", ", dials.Select(d => $"{d.Name}={d.Get()}"));

    static void SaveRenders(List<Sample> samples, ConvertResult[] results, string outDir, string tag)
    {
        for (int i = 0; i < samples.Count; i++)
        {
            File.WriteAllText(Path.Combine(outDir, $"{samples[i].Name}_{tag}.svg"), results[i].Xml);
            SampleBench.SaveRender(results[i], samples[i].Judge.Width, samples[i].Judge.Height,
                Path.Combine(outDir, $"{samples[i].Name}_{tag}.png"));
        }
    }
}
