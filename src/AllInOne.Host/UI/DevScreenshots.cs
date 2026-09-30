#if DEBUG
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AllInOne.Host.UI;

/// <summary>
/// Только для отладочной сборки: --dev-screenshots &lt;папка&gt; проходит по страницам и сохраняет их PNG.
/// Окно каркаса работает от администратора, и снять его снаружи обычным процессом нельзя (UIPI).
/// </summary>
internal static class DevScreenshots
{
    public static async Task RunAsync(MainWindow window, string dir, IEnumerable<string> pages)
    {
        System.IO.Directory.CreateDirectory(dir);
        foreach (var page in pages)
        {
            window.Navigate(page);
            await Task.Delay(1500);
            Save(window, System.IO.Path.Combine(dir, page + ".png"));
        }
    }

    /// <summary>Все виды отсчёта при разном остатке — для проверки отрисовки.</summary>
    public static void SaveDials(string path)
    {
        var now = DateTime.Now;
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Background = AllInOne.Ui.UiKit.Brush("Card") };
        foreach (var (leftMin, totalMin) in new[] { (22.5, 30.0), (95.0, 120.0), (0.6, 30.0) })
        {
            foreach (var face in Enum.GetValues<AllInOne.Modules.ShutdownTimer.CountdownFace>())
            {
                grid.Children.Add(new AllInOne.Modules.ShutdownTimer.CountdownDial(now.AddMinutes(leftMin), now.AddMinutes(leftMin - totalMin), face)
                {
                    Margin = new Thickness(16),
                });
            }
        }
        var content = new System.Windows.Controls.Border { Child = grid };
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    public static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var bmp = new RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }
}
#endif
