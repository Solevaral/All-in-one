using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AllInOne.Core.Processes;

public static class ProcessUtil
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Мьютекс с таким именем существует — значит, программа запущена.</summary>
    public static bool MutexExists(string name)
    {
        try
        {
            if (Mutex.TryOpenExisting(name, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Есть, но без доступа — всё равно «существует».
            return true;
        }
    }

    /// <summary>
    /// Процессы с таким именем exe; если задан путь — только запущенные именно из него.
    /// Уже завершившиеся процессы пропускаются: пока кто-то держит их дескриптор (антивирус, сам каркас),
    /// Windows ещё показывает их в списке, и каркас принимал такой «зомби» за снова запущенную программу.
    /// </summary>
    public static List<Process> Find(string processName, string? exactPath = null)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            var keep = IsAlive(p.Id) && (exactPath is null || TryGetPath(p) is { } path &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(exactPath), StringComparison.OrdinalIgnoreCase));
            if (keep) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    /// <summary>Живые процессы, чей exe лежит внутри папки (для проверки «файлы модуля свободны»).</summary>
    public static List<Process> FindUnder(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        var result = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            var path = TryGetPath(p);
            if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && IsAlive(p.Id)) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    public static string? TryGetPath(Process p)
    {
        try
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new char[1024];
                var size = buffer.Length;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Командная строка чужого процесса (NtQueryInformationProcess, класс 60 — Windows 8.1+).</summary>
    public static unsafe string? TryGetCommandLine(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return null;

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
                var str = (UnicodeString*)buffer;
                return str->Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(str->Buffer, str->Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Ждёт завершения всех процессов. true — все завершились за отведённое время.
    /// Проверка по PID через WaitForSingleObject: Process.WaitForExitAsync для процесса, запущенного
    /// не этим объектом Process, возвращался раньше реального выхода (fDimmer ещё восстанавливал экран).
    /// </summary>
    public static async Task<bool> WaitForExitAsync(IEnumerable<Process> processes, TimeSpan timeout, CancellationToken ct)
    {
        var pids = processes.Select(p =>
        {
            try { return p.Id; }
            catch (InvalidOperationException) { return 0; }
        }).Where(id => id != 0).ToList();

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            pids.RemoveAll(pid => !IsAlive(pid));
            if (pids.Count == 0) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Процесс с этим PID ещё работает.</summary>
    public static bool IsAlive(int pid)
    {
        var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            // Нет такого процесса — завершён. Нет доступа — существует, но проверить нельзя: считаем живым.
            return Marshal.GetLastWin32Error() == ErrorAccessDenied;
        }
        try
        {
            return WaitForSingleObject(handle, 0) == WaitTimeout;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Уже завершён или нет доступа — дальше проверит вызывающий.
        }
    }

    /// <summary>Порт на 127.0.0.1 принимает подключения. Данные не отправляются.</summary>
    public static async Task<bool> ProbePortAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var address = host is "0.0.0.0" or "" ? IPAddress.Loopback.ToString() : host;
            await client.ConnectAsync(address, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Порт кем-то слушается (без подключения к нему).</summary>
    public static bool IsPortListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    /// <summary>Файл не открыт на запись другими процессами (exe не запущен и не заблокирован).</summary>
    public static bool IsFileFree(string path)
    {
        if (!File.Exists(path)) return true;
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- interop ----

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int Synchronize = 0x00100000;
    private const int ErrorAccessDenied = 5;
    private const uint WaitTimeout = 0x102;

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] buffer, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
}
