using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using AllInOne.Core;
using AllInOne.Core.Modules;

namespace AllInOne.Host.UI;

/// <summary>
/// Экспериментально: главное окно внешней программы, встроенное в окно All in One (SetParent).
/// При уходе со страницы окно возвращается программе и скрывается — программа продолжает работать.
/// Оба процесса работают от администратора, поэтому перенос окна между ними разрешён.
/// </summary>
internal sealed class EmbeddedWindowHost(ExternalModule module) : HwndHost
{
    private IntPtr _container;
    private IntPtr _child;
    private int _oldStyle;
    private bool _attaching;

    public bool IsAttached => _child != IntPtr.Zero;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _container);
    }

    /// <summary>Просит программу показать окно, находит его и переносит внутрь.</summary>
    public async Task AttachAsync()
    {
        if (IsAttached || _attaching) return;
        _attaching = true;
        try
        {
            await module.ShowWindowAsync();
            for (var i = 0; i < 25 && !IsAttached; i++)
            {
                if (_container != IntPtr.Zero && module.ProcessId is { } pid && FindMainWindow(pid) is { } hwnd && hwnd != IntPtr.Zero)
                {
                    Attach(hwnd);
                    break;
                }
                await Task.Delay(200);
            }
        }
        finally
        {
            _attaching = false;
        }
    }

    private void Attach(IntPtr hwnd)
    {
        _child = hwnd;
        _oldStyle = GetWindowLong(hwnd, GWL_STYLE);
        var style = (_oldStyle & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU)) | WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);
        SetParent(hwnd, _container);
        ShowWindow(hwnd, SW_SHOW);
        FitChild();
        Log.Info($"Окно {module.Id} встроено в All in One");
    }

    private void Detach()
    {
        if (_child == IntPtr.Zero) return;
        var hwnd = _child;
        _child = IntPtr.Zero;
        if (!IsWindow(hwnd)) return;
        ShowWindow(hwnd, SW_HIDE);
        SetParent(hwnd, IntPtr.Zero);
        SetWindowLong(hwnd, GWL_STYLE, _oldStyle);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        FitChild();
    }

    private void FitChild()
    {
        if (_child == IntPtr.Zero || _container == IntPtr.Zero) return;
        GetClientRect(_container, out var rc);
        MoveWindow(_child, 0, 0, rc.Right - rc.Left, rc.Bottom - rc.Top, true);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Detach();
        DestroyWindow(hwnd.Handle);
        _container = IntPtr.Zero;
    }

    /// <summary>Видимое или скрытое окно верхнего уровня процесса с заголовком (главное окно программы).</summary>
    private static IntPtr? FindMainWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.Length == 0 || !IsWindowVisible(h)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    // ---- interop ----

    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_SYSMENU = 0x00080000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
