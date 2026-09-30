using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static AllInOne.Host.Interop.NativeMethods;

namespace AllInOne.Host.UI;

/// <summary>
/// Иконка в области уведомлений поверх Shell_NotifyIcon (как в magniF) с восстановлением
/// после перезапуска Explorer и всплывающими уведомлениями.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYCALLBACK = 0x8000 + 1;
    private const uint IconId = 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private readonly MessageWindow _window;
    private readonly uint _taskbarCreated;
    private IntPtr _hIcon;
    private string _tip = "All-in-one";
    private bool _added;
    private bool _disposed;

    public TrayIcon()
    {
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new MessageWindow(OnMessage);
        _hIcon = TrayIconArt.CreateHIcon(attention: false);
        Add();
    }

    public event EventHandler? LeftClick;
    public event EventHandler? RightClick;
    public event EventHandler? BalloonClick;

    public void Update(string tip, bool attention)
    {
        if (_disposed) return;

        var old = _hIcon;
        _hIcon = TrayIconArt.CreateHIcon(attention);
        _tip = tip.Length > 127 ? tip[..127] : tip;

        var data = BuildData(NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);

        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;
        var data = BuildData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..252] + "…" : text;
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA BuildData(uint flags) => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAYCALLBACK,
        hIcon = _hIcon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void OnMessage(ref Message m)
    {
        if (m.Msg == WM_TRAYCALLBACK)
        {
            switch ((int)m.LParam)
            {
                case WM_LBUTTONUP: LeftClick?.Invoke(this, EventArgs.Empty); break;
                case WM_RBUTTONUP: RightClick?.Invoke(this, EventArgs.Empty); break;
                case NIN_BALLOONUSERCLICK: BalloonClick?.Invoke(this, EventArgs.Empty); break;
            }
        }
        else if (_taskbarCreated != 0 && m.Msg == (int)_taskbarCreated)
        {
            // Explorer перезапустился — иконку нужно добавить заново.
            Add();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = BuildData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _window.DestroyHandle();
    }

    /// <summary>Скрытое окно-приёмник сообщений оболочки.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        public delegate void MessageHandler(ref Message m);

        private readonly MessageHandler _handler;

        public MessageWindow(MessageHandler handler)
        {
            _handler = handler;
            CreateHandle(new CreateParams
            {
                Caption = "AllInOne.TrayWindow",
                ExStyle = WS_EX_TOOLWINDOW,
            });
        }

        protected override void WndProc(ref Message m)
        {
            _handler(ref m);
            base.WndProc(ref m);
        }
    }
}

/// <summary>
/// Иконка трея — логотип каркаса из app.ico нужного размера.
/// При ошибке модуля в углу появляется оранжевая точка.
/// </summary>
internal static class TrayIconArt
{
    public static IntPtr CreateHIcon(bool attention)
    {
        var size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        using var bmp = Draw(size, attention);
        return bmp.GetHicon();
    }

    public static Bitmap Draw(int size, bool attention)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        using (var logo = LoadLogo(size))
        {
            if (logo is not null) g.DrawImage(logo, 0, 0, size, size);
        }

        if (attention)
        {
            var d = size * 0.42f;
            using var ring = new SolidBrush(Color.White);
            using var dot = new SolidBrush(Color.FromArgb(255, 240, 150, 60));
            g.FillEllipse(ring, size - d - 0.5f, size - d - 0.5f, d, d);
            g.FillEllipse(dot, size - d + size * 0.06f - 0.5f, size - d + size * 0.06f - 0.5f, d - size * 0.12f, d - size * 0.12f);
        }

        return bmp;
    }

    /// <summary>
    /// Кадр app.ico не меньше нужного размера (или самый большой). Кадры иконки хранятся в PNG,
    /// поэтому читаем каталог ICO сами и декодируем PNG напрямую.
    /// </summary>
    private static Bitmap? LoadLogo(int size)
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        if (resource is null) return null;

        using var ms = new MemoryStream();
        using (resource.Stream) resource.Stream.CopyTo(ms);
        var data = ms.ToArray();

        var count = BitConverter.ToUInt16(data, 4);
        (int Width, int Offset, int Length)? best = null;
        for (var i = 0; i < count; i++)
        {
            var e = 6 + 16 * i;
            var width = data[e] == 0 ? 256 : data[e];
            var length = BitConverter.ToInt32(data, e + 8);
            var offset = BitConverter.ToInt32(data, e + 12);
            var better = best is not { } b
                         || (width >= size && (b.Width < size || width < b.Width))
                         || (b.Width < size && width > b.Width);
            if (better) best = (width, offset, length);
        }
        if (best is not { } frame) return null;

        try
        {
            using var frameStream = new MemoryStream(data, frame.Offset, frame.Length);
            using var decoded = new Bitmap(frameStream);
            return new Bitmap(decoded);   // копия, не зависящая от потока
        }
        catch (ArgumentException)
        {
            return null;   // кадр в формате BMP — трей обойдётся без логотипа
        }
    }
}
