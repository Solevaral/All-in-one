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
