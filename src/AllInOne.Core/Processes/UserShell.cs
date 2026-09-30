using System.Diagnostics;

namespace AllInOne.Core.Processes;

/// <summary>
/// Открытие ссылок и программ без прав администратора. All in One работает от администратора,
/// и всё, что он запускает напрямую, наследует эти права: браузер или Telegram, не запущенные
/// в этот момент, стартовали бы от администратора. Здесь запуск поручается Проводнику
/// (рабочему столу), который работает от имени пользователя.
/// </summary>
public static class UserShell
{
    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;

    /// <summary>Открывает ссылку (https://, tg://) или файл программой по умолчанию от имени пользователя.</summary>
    public static void Open(string target)
    {
        try
        {
            ShellExecuteAsUser(target);
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"Запуск через Проводник не удался ({ex.Message}), открываю через explorer.exe");
        }

        try
        {
            // explorer.exe передаёт команду уже работающему Проводнику пользователя.
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(target);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Не удалось открыть {target}: {ex.Message}");
        }
    }

    /// <summary>IShellDispatch2.ShellExecute рабочего стола — выполняется в процессе Проводника.</summary>
    private static void ShellExecuteAsUser(string target)
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClsid) ?? throw new InvalidOperationException("ShellWindows недоступен");
        dynamic shellWindows = Activator.CreateInstance(type)!;
        object location = CsidlDesktop;
        object empty = Type.Missing;
        var hwnd = 0;
        dynamic desktop = shellWindows.FindWindowSW(ref location, ref empty, SwcDesktop, ref hwnd, SwfoNeedDispatch)
                          ?? throw new InvalidOperationException("рабочий стол не найден");
        dynamic app = desktop.Document.Application;
        app.ShellExecute(target, "", "", "open", 1);
    }
}
