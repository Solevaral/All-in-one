using System.IO;
using System.Diagnostics;
using AllInOne.Core;
using AllInOne.Core.Modules;
using AllInOne.Core.Processes;
using AllInOne.Sdk;
using Microsoft.Win32;

namespace AllInOne.Modules.Zapret;

public sealed class ZapretFactory : IModuleFactory
{
    public string Kind => "adapter:zapret";

    public ModuleBase Create(ModuleContext context) => new ZapretModule(context);
}

public enum RunMode
{
    /// <summary>Каркас сам запускает winws.exe и следит за процессом.</summary>
    Process,

    /// <summary>Служба Windows «zapret» (как пункт Install Service в service.bat) — работает и без каркаса.</summary>
    Service,
}

public sealed class ZapretSettings
{
    public string? Strategy { get; set; }
    public RunMode Mode { get; set; } = RunMode.Process;
}

/// <summary>
/// Оболочка для zapret-discord-youtube. Bat-файлы не запускаются: стратегия разбирается
/// в аргументы winws.exe (<see cref="BatStrategyParser"/>), а пункты меню service.bat
/// повторены здесь и в <see cref="ZapretFiles"/>.
/// </summary>
public sealed class ZapretModule : ModuleBase, IModuleInstallHooks
{
    internal const string ServiceName = "zapret";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\zapret";
    private const string StrategyValue = "zapret-discord-youtube";
    internal static readonly string[] DriverServices = ["WinDivert", "WinDivert14"];

    private ZapretSettings _settings;

    public ZapretModule(ModuleContext context) : base(context)
    {
        _settings = JsonFile.Read<ZapretSettings>(SettingsPath) ?? new ZapretSettings();
    }

    internal ZapretFiles Files => new(Context.PayloadDir);

    internal ZapretSettings Settings => _settings;

    private string SettingsPath => Path.Combine(Context.DataDir, "zapret.json");

    internal void SaveSettings() => JsonFile.TryWrite(SettingsPath, _settings);

    public override string? InstalledVersion => Context.IsInstalled ? Files.ReadVersion() ?? Context.Manifest.Version : null;

    /// <summary>Стратегия по умолчанию — general, если есть, иначе первая по порядку.</summary>
    internal string? CurrentStrategy
    {
        get
        {
            var all = Files.Strategies();
            if (_settings.Strategy is { } s && all.Contains(s, StringComparer.OrdinalIgnoreCase)) return s;
            return all.FirstOrDefault(n => n.Equals("general", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        }
    }

    /// <summary>Последняя найденная проблема окружения (чужой winws, чужая служба).</summary>
    internal string? Conflict { get; private set; }

    // ---------- запуск ----------

    public override async Task StartAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        if (Status.State == ModuleState.Running) return;
        if (Conflict is { } conflict) throw new InvalidOperationException(conflict);

        var strategy = CurrentStrategy ?? throw new InvalidOperationException("В папке zapret нет ни одной стратегии (*.bat).");
        var files = Files;
        if (!File.Exists(Path.Combine(files.Bin, "WinDivert64.sys")))
            throw new InvalidOperationException("Нет файла WinDivert64.sys — скорее всего, его удалил антивирус. Добавьте папку модуля в исключения и переустановите модуль.");

        files.EnsureUserLists();
        await EnableTcpTimestampsAsync(ct);
        var args = files.BuildArgs(strategy);

        SetStatus(ModuleState.Starting, strategy);
        if (_settings.Mode == RunMode.Service)
        {
            await InstallServiceAsync(strategy, args, ct);
            var r = await ServiceUtil.StartAsync(ServiceName, ct);
            if (!await ServiceUtil.WaitForAsync(ServiceName, s => s == ServiceState.Running, TimeSpan.FromSeconds(10), ct))
            {
                SetStatus(ModuleState.Error, "Служба не запустилась", r.All.Trim());
                return;
            }
        }
        else
        {
            if (await ServiceUtil.QueryAsync(ServiceName, ct) != ServiceState.NotInstalled)
                await RemoveServiceAsync(ct);   // режим «процесс»: старая служба мешала бы (второй winws)

            var psi = new ProcessStartInfo(files.WinwsExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = files.Bin,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("winws.exe не запустился.");

            // winws с неверными аргументами или без драйвера завершается сразу.
            if (p.WaitForExit(2500))
            {
                SetStatus(ModuleState.Error, "winws.exe сразу завершился",
                    $"Код выхода {p.ExitCode}. Нажмите «Запустить в окне», чтобы увидеть сообщение winws. Частые причины: антивирус удалил WinDivert, запущен другой обход блокировок (GoodbyeDPI), служба BFE выключена.");
                return;
            }
        }

        Context.Log.Info($"zapret запущен: {strategy}, режим {_settings.Mode}");
        await RefreshAsync(ct);
    }

    // ---------- остановка ----------

    public override async Task StopAsync(StopReason reason, CancellationToken ct)
    {
        SetStatus(ModuleState.Stopping, "Остановка…");

        if (await ServiceUtil.QueryAsync(ServiceName, ct) is ServiceState.Running or ServiceState.StartPending && IsOurService())
        {
            await ServiceUtil.StopAndWaitAsync(ServiceName, TimeSpan.FromSeconds(15), ct);
        }

        // winws нечего сохранять: upstream тоже останавливает его через taskkill /F.
        var processes = OurProcesses();
        foreach (var p in processes) ProcessUtil.KillTree(p);
        var exited = await ProcessUtil.WaitForExitAsync(processes, TimeSpan.FromSeconds(10), ct);
        foreach (var p in processes) p.Dispose();
        if (!exited) throw new InvalidOperationException("winws.exe не завершился.");

        // Драйвер отпускается не мгновенно: STOP_PENDING — значит, его ещё кто-то держит.
        foreach (var driver in DriverServices)
            await ServiceUtil.WaitForAsync(driver, s => s != ServiceState.StopPending, TimeSpan.FromSeconds(10), ct);

        SetStatus(ModuleStatus.Stopped);
        await RefreshAsync(ct);
    }

    // ---------- состояние ----------

    public override async Task RefreshAsync(CancellationToken ct)
    {
        if (!Context.IsInstalled)
        {
            SetStatus(ModuleStatus.NotInstalled);
            return;
        }
        if (Status.State is ModuleState.Stopping or ModuleState.Updating) return;

        var ours = OurProcesses();
        var running = ours.Count > 0;
        string? commandLine = running ? ProcessUtil.TryGetCommandLine(ours[0].Id) : null;
        foreach (var p in ours) p.Dispose();

        var service = await ServiceUtil.QueryAsync(ServiceName, ct);
        var ourService = service != ServiceState.NotInstalled && IsOurService();
        Conflict = null;

        var foreign = ProcessUtil.Find("winws.exe").Where(p => !OurPath(p)).ToList();
        if (foreign.Count > 0)
            Conflict = "Запущен другой winws.exe (не из этого модуля) — например, отдельно скачанный zapret. Закройте его, чтобы не было двойной обработки трафика.";
        foreach (var p in foreign) p.Dispose();
        if (service != ServiceState.NotInstalled && !ourService)
            Conflict = "Установлена служба «zapret» из другой папки. Удалите её (service.bat → Remove Services) или в диагностике этого модуля.";

        if (running)
        {
            var strategy = (ourService && service == ServiceState.Running ? ReadServiceStrategy() : null) ?? CurrentStrategy ?? "?";
            var mode = ourService && service == ServiceState.Running ? "служба" : "процесс";
            SetStatus(ModuleState.Running, $"{strategy} · {mode}", commandLine is null ? null : "winws " + TrimExe(commandLine));
            return;
        }

        if (Status.State == ModuleState.Running)
        {
            SetStatus(ModuleState.Stopped, "Завершился");
            RaiseUnexpectedExit();
            return;
        }

        if (Conflict is { } c) SetStatus(ModuleState.Error, "Конфликт", c);
        else if (Status.State is not (ModuleState.Error or ModuleState.Starting)) SetStatus(ModuleStatus.Stopped);
    }

    private static string TrimExe(string commandLine)
    {
        var i = commandLine.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? commandLine : commandLine[(i + "winws.exe".Length)..].TrimStart('"', ' ');
    }

    internal List<Process> OurProcesses() => ProcessUtil.Find("winws.exe", Files.WinwsExe);

    private bool OurPath(Process p) =>
        ProcessUtil.TryGetPath(p) is { } path && string.Equals(path, Files.WinwsExe, StringComparison.OrdinalIgnoreCase);

    // ---------- служба Windows ----------

    internal bool IsOurService()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        return key?.GetValue("ImagePath") is string image && image.Contains(Files.WinwsExe, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ReadServiceImagePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        return key?.GetValue("ImagePath") as string;
    }

    internal static string? ReadServiceStrategy()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
        return key?.GetValue(StrategyValue) as string;
    }

    /// <summary>То же, что пункт Install Service: sc create zapret … start= auto + имя стратегии в реестре.</summary>
    private async Task InstallServiceAsync(string strategy, IReadOnlyList<string> args, CancellationToken ct)
    {
        await RemoveServiceAsync(ct);
        var binPath = $"\"{Files.WinwsExe}\" {BatStrategyParser.ToCommandLine(args)}";
        var escaped = binPath.Replace("\"", "\\\"");
        var r = await Cli.RunRawAsync(Cli.System32("sc.exe"), $"create {ServiceName} binPath= \"{escaped}\" DisplayName= \"zapret\" start= auto", ct: ct);
        if (!r.Ok) throw new InvalidOperationException("Служба не создана: " + r.All.Trim());
        await Cli.RunRawAsync(Cli.System32("sc.exe"), $"description {ServiceName} \"Zapret DPI bypass software\"", ct: ct);

        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true);
        key?.SetValue(StrategyValue, strategy, RegistryValueKind.String);
    }

    internal async Task RemoveServiceAsync(CancellationToken ct)
    {
        if (await ServiceUtil.QueryAsync(ServiceName, ct) == ServiceState.NotInstalled) return;
        await ServiceUtil.StopAndWaitAsync(ServiceName, TimeSpan.FromSeconds(15), ct);
        await ServiceUtil.DeleteAsync(ServiceName, ct);
        await ServiceUtil.WaitForAsync(ServiceName, s => s == ServiceState.NotInstalled, TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>Выгрузить драйвер WinDivert (освобождает WinDivert64.sys для замены).</summary>
    internal static async Task RemoveDriverAsync(CancellationToken ct)
    {
        foreach (var driver in DriverServices)
        {
            if (await ServiceUtil.QueryAsync(driver, ct) == ServiceState.NotInstalled) continue;
            await ServiceUtil.StopAndWaitAsync(driver, TimeSpan.FromSeconds(10), ct);
            await ServiceUtil.DeleteAsync(driver, ct);
        }
    }

    /// <summary>fooling=ts в стратегиях требует TCP timestamps (как :tcp_enable в service.bat).</summary>
    internal static async Task<bool> EnableTcpTimestampsAsync(CancellationToken ct)
    {
        var show = await Cli.RunAsync(Cli.System32("netsh.exe"), ["interface", "tcp", "show", "global"], ct: ct);
        var line = show.Output.Split('\n').FirstOrDefault(l => l.Contains("timestamps", StringComparison.OrdinalIgnoreCase)
                                                            || l.Contains("метки времени", StringComparison.OrdinalIgnoreCase));
        if (line is not null && (line.Contains("enabled", StringComparison.OrdinalIgnoreCase) || line.Contains("включ", StringComparison.OrdinalIgnoreCase)))
            return true;
        var set = await Cli.RunAsync(Cli.System32("netsh.exe"), ["interface", "tcp", "set", "global", "timestamps=enabled"], ct: ct);
        return set.Ok;
    }

    // ---------- отладочный запуск в окне ----------

    /// <summary>Запускает winws в видимой консоли, чтобы увидеть его сообщения (как запуск bat вручную).</summary>
    internal void RunInConsole()
    {
        var strategy = CurrentStrategy ?? throw new InvalidOperationException("Нет стратегий.");
        var files = Files;
        files.EnsureUserLists();
        var args = files.BuildArgs(strategy);
        var cmd = $"/k title zapret: {strategy} & \"{files.WinwsExe}\" {BatStrategyParser.ToCommandLine(args)}";
        Process.Start(new ProcessStartInfo(Cli.System32("cmd.exe"), cmd) { UseShellExecute = true, WorkingDirectory = files.Bin });
    }

    // ---------- установка и обновление ----------

    public async Task<IDictionary<string, string>> BeforeReplaceAsync(bool isUpdate, CancellationToken ct)
    {
        var saved = new Dictionary<string, string>();

        // Служба и драйвер держат winws.exe и WinDivert64.sys — без их удаления файлы не заменить.
        if (await ServiceUtil.QueryAsync(ServiceName, ct) != ServiceState.NotInstalled && IsOurService())
        {
            saved["service"] = "1";
            await RemoveServiceAsync(ct);
        }
        foreach (var p in OurProcesses()) { ProcessUtil.KillTree(p); p.Dispose(); }
        await RemoveDriverAsync(ct);

        // Режим IPSet и загруженный список живут в файлах, которые релиз перезапишет.
        var files = Files;
        if (files.Exists)
        {
            saved["ipset"] = files.ReadIpsetMode().ToString();
            var keep = Path.Combine(Context.DataDir, "ipset-keep");
            Directory.CreateDirectory(keep);
            foreach (var name in new[] { "ipset-all.txt", "ipset-all.txt.backup" })
            {
                var src = Path.Combine(files.Lists, name);
                var dst = Path.Combine(keep, name);
                if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
                else File.Delete(dst);
            }
        }
        return saved;
    }

    public async Task AfterReplaceAsync(bool isUpdate, IDictionary<string, string> saved, CancellationToken ct)
    {
        var files = Files;
        files.EnsureUserLists();

        // Обновлениями занимается каркас — встроенная проверка в bat-файлах не нужна.
        var flag = Path.Combine(files.Utils, "check_updates.enabled");
        if (File.Exists(flag)) File.Delete(flag);

        if (saved.TryGetValue("ipset", out var mode) && Enum.TryParse<IpsetMode>(mode, out var ipset) && ipset != IpsetMode.None)
        {
            var keep = Path.Combine(Context.DataDir, "ipset-keep");
            foreach (var name in new[] { "ipset-all.txt", "ipset-all.txt.backup" })
            {
                var src = Path.Combine(keep, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(files.Lists, name), overwrite: true);
            }
        }

        if (saved.ContainsKey("service") && CurrentStrategy is { } strategy)
        {
            // Служба была установлена — ставим заново с новыми путями. Запустит её установщик, если модуль работал.
            await InstallServiceAsync(strategy, files.BuildArgs(strategy), ct);
        }
    }

    public string? ReadPayloadVersion() => Files.ReadVersion();

    public override object? CreateView() => new ZapretView(this);
}
