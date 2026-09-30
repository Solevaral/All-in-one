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

    /// <summary>Процессы с таким именем exe; если задан путь — только запущенные именно из него.</summary>
    public static List<Process> Find(string processName, string? exactPath = null)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (exactPath is null)
            {
                result.Add(p);
                continue;
            }

            var path = TryGetPath(p);
            if (path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exactPath), StringComparison.OrdinalIgnoreCase))
                result.Add(p);
            else
                p.Dispose();
        }
        return result;
    }

    /// <summary>Процессы, чей exe лежит внутри папки (для проверки «файлы модуля свободны»).</summary>
    public static List<Process> FindUnder(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        var result = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            var path = TryGetPath(p);
            if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) result.Add(p);
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

    /// <summary>Ждёт завершения всех процессов. true — все завершились за отведённое время.</summary>
    public static async Task<bool> WaitForExitAsync(IEnumerable<Process> processes, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(cts.Token)));
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершён и отсоединён.
            return true;
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
