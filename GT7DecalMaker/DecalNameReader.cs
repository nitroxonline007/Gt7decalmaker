using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GT7DecalMaker;

// Reads text from decal art using the OCR engine built into Windows,
// so the exported SVG can be named after the decal itself (GT7 shows
// imported decals under their file name).
public static class DecalNameReader
{
    public static async Task<string?> ReadAsync(string imagePath)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (engine == null) return null;

            byte[] bytes = await File.ReadAllBytesAsync(imagePath);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var bmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var result = await engine.RecognizeAsync(bmp);

            var words = result.Lines
                .SelectMany(l => l.Words)
                .Select(w => CleanWord(w.Text))
                .Where(w => w.Length >= 2 && w.Count(char.IsLetter) >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6);

            string name = string.Join("_", words);
            if (name.Length > 48) name = name[..48];
            return name.Length >= 3 ? name : null;
        }
        catch
        {
            return null; // OCR unavailable or failed - caller falls back
        }
    }

    private static string CleanWord(string w)
    {
        var chars = w.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }
}
