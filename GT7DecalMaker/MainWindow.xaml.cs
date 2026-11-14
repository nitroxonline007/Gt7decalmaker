using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GT7DecalMaker.Convert;
using Microsoft.Win32;
using IoPath = System.IO.Path;
using WpfPath = System.Windows.Shapes.Path;
using WpfPoint = System.Windows.Point;

namespace GT7DecalMaker
{
    public partial class MainWindow : Window
    {
        private string? _sourcePath;
        private PixelBuffer? _buffer;
        private int _bufferRes = -1;
        private ConvertResult? _result;
        private Task<string?>? _ocrTask;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                OpenImage(files[0]);
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open PNG",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|PNG|*.png|All files|*.*"
            };
            if (dlg.ShowDialog(this) == true) OpenImage(dlg.FileName);
        }

        private void OpenImage(string path)
        {
            try
            {
                _sourcePath = path;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                ImgSource.Source = bmp;

                _buffer = null;
                BtnConvert.IsEnabled = true;
                TxtFile.Text = IoPath.GetFileName(path) + $"  ({bmp.PixelWidth}\u00d7{bmp.PixelHeight})";
                TxtStatus.Text = "Ready. Adjust settings and press Convert.";
                SetBadge(0, null);
                VectorHost.Content = null;

                // Try to read the decal's text in the background so the SVG can
                // be named after it (GT7 shows decals under their file name).
                _ocrTask = Task.Run(() => DecalNameReader.ReadAsync(path));
                _ = _ocrTask.ContinueWith(t =>
                {
                    if (!t.IsCompletedSuccessfully || t.Result == null) return;
                    Dispatcher.Invoke(() =>
                    {
                        if (_sourcePath != path) return;
                        TxtFile.Text += $"   \u2022 name: {t.Result}";
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open image:\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private PixelBuffer GetBuffer()
        {
            int res = (int)SldWorkRes.Value;
            if (_buffer == null || _bufferRes != res)
            {
                _buffer = ConvertEngine.LoadImage(_sourcePath!, res);
                _bufferRes = res;
            }
            return _buffer;
        }

        private async void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (_sourcePath == null) return;
            BtnConvert.IsEnabled = false;
            BtnSave.IsEnabled = false;
            Cursor = System.Windows.Input.Cursors.Wait;

            int colors = (int)SldColors.Value;
            int detail = (int)SldDetail.Value;
            int despeckle = (int)SldDespeckle.Value;
            bool autofit = ChkAutoFit.IsChecked == true;
            bool seamFill = ChkSeamFill.IsChecked == true;
            bool bgRemove = ChkBgRemove.IsChecked == true;
            bool autoRes = ChkAutoRes.IsChecked == true;
            bool keepText = ChkKeepText.IsChecked == true;
            int targetBytes = Math.Max(1, ParseTargetKB()) * 1000;
            double epsBase = (11 - detail) * 0.75;

            TxtStatus.Text = "Converting...";
            try
            {
                var buffer = GetBuffer();
                var progress = new Progress<(int attempt, int bytes, int target)>(p =>
                    TxtStatus.Text = $"Auto-fit pass {p.attempt}: {FmtKB(p.bytes)} (target {FmtKB(p.target)})...");

                ConvertResult fitted, bestQuality;
                int attempts;

                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    if (!autofit)
                    {
                        var single = RunOnce(buffer);
                        return (single, single, 1);
                    }

                    var baseSettings = new ConvertSettings
                    {
                        Epsilon = epsBase,
                        MaxColors = colors,
                        DespecklePx = despeckle,
                        SeamFill = seamFill,
                        RemoveFlatBackground = bgRemove,
                        KeepText = keepText
                    };
                    string srcPath = _sourcePath!;
                    Func<int, PixelBuffer>? resampler = autoRes
                        ? new Func<int, PixelBuffer>(r => ConvertEngine.LoadImage(srcPath, r))
                        : null;
                    return ConvertEngine.AutoFit(buffer, baseSettings, targetBytes,
                        (a, b, t) => ((IProgress<(int, int, int)>)progress).Report((a, b, t)),
                        resampler, 1024);
                });
                (fitted, bestQuality, attempts) = result;

                _result = fitted;
                RenderVector(fitted);
                BtnSave.IsEnabled = true;

                bool fits = fitted.Bytes <= targetBytes;
                string qualityInfo = autofit && attempts > 1 && bestQuality.Bytes > targetBytes
                    ? $" | full quality: {FmtKB(bestQuality.Bytes)}"
                    : "";
                TxtStatus.Text =
                    $"{fitted.Width}\u00d7{fitted.Height} px | {fitted.ColorsUsed} colors | {fitted.LoopCount} shapes" +
                    qualityInfo +
                    (attempts > 1 ? $" | {attempts} auto-fit passes" : "") +
                    (fits ? " \u2713 under limit" : " \u26a0 could not fit - lower resolution or colors");
                SetBadge(fitted.Bytes, fits ? Brushes.LimeGreen : Brushes.OrangeRed);

                if (ChkAutoSave.IsChecked == true)
                {
                    string? ocrStem = null;
                    try { ocrStem = _ocrTask == null ? null : await _ocrTask; } catch { /* ignore */ }
                    AutoSaveSvg(ocrStem);
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Conversion failed: " + ex.Message;
                SetBadge(0, Brushes.OrangeRed);
            }
            finally
            {
                Cursor = null;
                BtnConvert.IsEnabled = true;
            }

            ConvertResult RunOnce(PixelBuffer buf) => ConvertEngine.Run(buf, new ConvertSettings
            {
                Epsilon = epsBase,
                MaxColors = colors,
                DespecklePx = despeckle,
                SeamFill = seamFill,
                RemoveFlatBackground = bgRemove
            });
        }

        private void RenderVector(ConvertResult r)
        {
            var canvas = new Canvas { Width = r.Width, Height = r.Height };
            foreach (var layer in r.Layers)
            {
                var brush = new SolidColorBrush(Color.FromRgb(layer.R, layer.G, layer.B));
                // All loops of a layer share one even-odd geometry so holes render correctly.
                var pg = new PathGeometry { FillRule = FillRule.EvenOdd };
                foreach (var loop in layer.Loops)
                {
                    var fig = new PathFigure
                    {
                        StartPoint = new WpfPoint(loop[0].X, loop[0].Y),
                        IsClosed = true
                    };
                    for (int i = 1; i < loop.Count; i++)
                        fig.Segments.Add(new LineSegment { Point = new WpfPoint(loop[i].X, loop[i].Y) });
                    pg.Figures.Add(fig);
                }

                canvas.Children.Add(new WpfPath
                {
                    Data = pg,
                    Fill = brush,
                    Stroke = r.Settings.SeamFill ? brush : null,
                    StrokeThickness = r.Settings.SeamFill ? 0.5 : 0
                });
            }
            VectorHost.Content = canvas;
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _sourcePath == null) return;

            string stem;
            string? ocrName = null;
            try { ocrName = _ocrTask == null ? null : await _ocrTask; } catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(ocrName))
                stem = ocrName!;
            else
                stem = IoPath.GetFileNameWithoutExtension(_sourcePath);

            if (stem.Length > 60) stem = stem[..60];

            var dlg = new SaveFileDialog
            {
                Title = "Save SVG decal",
                Filter = "SVG|*.svg",
                FileName = stem + ".svg"
            };
            if (dlg.ShowDialog(this) != true) return;

            File.WriteAllText(dlg.FileName, _result.Xml, new UTF8Encoding(false));
            TxtStatus.Text = $"Saved {_result.Bytes:N0} bytes \u2192 {IoPath.GetFileName(dlg.FileName)}";
        }

        private void AutoSaveSvg(string? ocrStem)
        {
            if (_result == null) return;
            try
            {
                string dir = IoPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "svg");
                Directory.CreateDirectory(dir);

                string stem = !string.IsNullOrWhiteSpace(ocrStem)
                    ? ocrStem!
                    : IoPath.GetFileNameWithoutExtension(_sourcePath ?? "decal");
                foreach (char c in IoPath.GetInvalidFileNameChars())
                    stem = stem.Replace(c, '_');
                if (stem.Length > 60) stem = stem[..60];

                string file = IoPath.Combine(dir, stem + ".svg");
                int n = 1;
                while (File.Exists(file))
                    file = IoPath.Combine(dir, $"{stem}_{n++}.svg");

                File.WriteAllText(file, _result.Xml, new UTF8Encoding(false));
                TxtStatus.Text += $" | auto-saved: {IoPath.GetFileName(file)}";
            }
            catch (Exception ex)
            {
                TxtStatus.Text += $" | auto-save failed: {ex.Message}";
            }
        }

        private int ParseTargetKB() =>
            int.TryParse(TxtTargetKB.Text?.Trim(), out int v) && v > 0 ? v : 15;

        private static string FmtKB(int bytes) => (bytes / 1000.0).ToString("0.#") + " KB";

        private void SetBadge(int bytes, Brush? color)
        {
            TxtBadge.Text = bytes > 0 ? FmtKB(bytes) + " / " + ParseTargetKBDynamic() + " KB" : "-- KB";
            SizeBadge.Background = color ?? (Brush)FindResource("CheckerBase");
            if (bytes == 0) TxtBadge.Text = "-- KB";
        }

        private string ParseTargetKBDynamic() => ParseTargetKB().ToString();
    }
}
