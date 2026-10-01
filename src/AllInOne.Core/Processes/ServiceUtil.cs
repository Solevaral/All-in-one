using System.Text.RegularExpressions;

namespace AllInOne.Core.Processes;

/// <summary>Службы Windows через sc.exe. Состояние разбирается по числовому коду — он не зависит от языка системы.</summary>
public static partial class ServiceUtil
{
    /// <summary>Служба, которая работает в процессе с этим PID (tasklist /svc). null — процесс не служба.</summary>
    public static async Task<string?> FindByProcessIdAsync(int pid, CancellationToken ct = default)
    {
        var r = await Cli.RunAsync(Cli.System32("tasklist.exe"), ["/svc", "/fo", "csv", "/nh", "/fi", $"PID eq {pid}"], ct: ct);
        if (!r.Ok) return null;
        // "имя.exe","PID","служба1,служба2" — для обычного процесса в третьем поле «N/A» / «Н/Д».
        var fields = r.Output.Trim().Split("\",\"");
        if (fields.Length < 3) return null;
        var name = fields[2].Trim('"', ' ', '\r', '\n').Split(',')[0].Trim();
        if (name.Length == 0) return null;
        return await QueryAsync(name, ct) == ServiceState.NotInstalled ? null : name;
    }

    public static async Task<ServiceState> QueryAsync(string name, CancellationToken ct = default)
    {
        var r = await Cli.RunAsync(Cli.System32("sc.exe"), ["query", name], ct: ct);
        // 1060 — служба не установлена.
        if (r.ExitCode == 1060) return ServiceState.NotInstalled;
        var m = StateRegex().Match(r.Output);
        if (!m.Success) return r.Ok ? ServiceState.Unknown : ServiceState.NotInstalled;
        return (ServiceState)int.Parse(m.Groups[1].Value);
    }

    public static Task<Cli.Result> StopAsync(string name, CancellationToken ct = default) =>
        Cli.RunAsync(Cli.System32("sc.exe"), ["stop", name], ct: ct);

    public static Task<Cli.Result> StartAsync(string name, CancellationToken ct = default) =>
        Cli.RunAsync(Cli.System32("sc.exe"), ["start", name], ct: ct);

    public static Task<Cli.Result> DeleteAsync(string name, CancellationToken ct = default) =>
        Cli.RunAsync(Cli.System32("sc.exe"), ["delete", name], ct: ct);

    /// <summary>Останавливает службу и ждёт STOPPED (или отсутствия службы).</summary>
    public static async Task<bool> StopAndWaitAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var state = await QueryAsync(name, ct);
        if (state is ServiceState.NotInstalled or ServiceState.Stopped) return true;
        if (state != ServiceState.StopPending) await StopAsync(name, ct);
        return await WaitForAsync(name, s => s is ServiceState.Stopped or ServiceState.NotInstalled, timeout, ct);
    }

    public static async Task<bool> WaitForAsync(string name, Func<ServiceState, bool> condition, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition(await QueryAsync(name, ct))) return true;
            await Task.Delay(250, ct);
        }
        return condition(await QueryAsync(name, ct));
    }

    // Имена состояний sc выводит по-английски в любой локализации; строку TYPE (у драйвера «1 KERNEL_DRIVER») отсекаем явным списком.
    [GeneratedRegex(@":\s*(\d)\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StateRegex();
}

/// <summary>Коды SERVICE_STATUS.dwCurrentState.</summary>
public enum ServiceState
{
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
    NotInstalled = 100,
}
