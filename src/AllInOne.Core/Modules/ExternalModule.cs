using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AllInOne.Core.HostLink;
using AllInOne.Core.Processes;
using AllInOne.Sdk;

namespace AllInOne.Core.Modules;

/// <summary>
/// Модуль — отдельная программа, описанная в module.json (fDimmer, magniF, TryToCatchMe).
/// Связь по IPC, если программа поддерживает режим --hosted; иначе — только процесс.
/// Каркас не держит процесс модуля: после перезапуска каркаса модуль находится по пути exe
/// и каналу IPC и продолжает работать без перезапуска.
/// </summary>
public sealed class ExternalModule(ModuleContext context) : ModuleBase(context)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private HostLinkClient? _link;
    private HelloResult? _hello;
    private bool _stopping;
    private bool _wasRunning;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private ModuleManifest M => Context.Manifest;

    private string ExePath => Context.Resolve(M.Run?.Exe ?? throw new InvalidOperationException("В module.json не задан run.exe"));

    private string PipeName => M.Ipc?.Pipe ?? HostLinkProtocol.PipeName(M.Id);

    private bool UsesIpc => M.Ipc is not null;

    public override IReadOnlyList<ModuleAction> Actions
    {
        get
        {
            var actions = new List<ModuleAction>();
            if (UsesIpc && _link is { IsConnected: true })
            {
                actions.Add(new ModuleAction("showWindow", "Открыть окно", () => CallQuietAsync(HostLinkProtocol.Methods.ShowWindow, null)));
                if (_hello is not null)
                {
                    foreach (var a in _hello.Capabilities.Actions)
                    {
                        actions.Add(new ModuleAction(a.Id, a.Title,
                            () => CallQuietAsync(HostLinkProtocol.Methods.Invoke, new { action = a.Id })));
                    }
                }
            }
            return actions;
        }
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        if (!Context.IsInstalled) throw new InvalidOperationException("Модуль не установлен.");

        await RefreshAsync(ct);
        if (Status.State == ModuleState.Running) return;

        SetStatus(ModuleState.Starting, "Запуск…");
        var run = M.Run!;
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = run.WorkingDir is { } wd ? Context.Resolve(wd) : Path.GetDirectoryName(ExePath)!,
        };
        foreach (var arg in run.Args) psi.ArgumentList.Add(arg.Replace("{pipe}", PipeName));

        try
        {
            using var _ = Process.Start(psi) ?? throw new InvalidOperationException("Процесс не запустился.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(ModuleState.Error, "Не запускается", ex.Message);
            throw;
        }

        // Ждём, пока программа поднимет канал (или хотя бы останется жива).
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(400, ct);
            await RefreshAsync(ct);
            if (Status.State == ModuleState.Running && (!UsesIpc || _link is { IsConnected: true })) return;
            if (Status.State == ModuleState.Error) return;

            var alive = FindProcesses();
            var any = alive.Count > 0;
            foreach (var p in alive) p.Dispose();
            if (!any && DateTime.UtcNow - started > TimeSpan.FromSeconds(2))
            {
                SetStatus(ModuleState.Error, "Сразу завершился", "Программа закрылась сразу после запуска. Проверьте, не запущена ли она отдельно.");
                return;
            }
        }

        if (UsesIpc && _link is null)
        {
            SetStatus(ModuleState.Error, "Нет связи", "Программа запущена, но не отвечает по каналу IPC. Возможно, установлена версия без режима --hosted.");
        }
    }

    public override async Task StopAsync(StopReason reason, CancellationToken ct)
    {
        await RefreshAsync(ct);
        var processes = FindProcesses();
        if (processes.Count == 0 && _link is null)
        {
            SetStatus(ModuleStatus.Stopped);
            return;
        }

        _stopping = true;
        SetStatus(ModuleState.Stopping, "Остановка…");
        var timeout = TimeSpan.FromSeconds(Math.Max(3, M.Stop.TimeoutSec));

        try
        {
            var requested = await RequestGracefulStopAsync(reason, processes, ct);

            var exited = requested && await ProcessUtil.WaitForExitAsync(processes, timeout, ct);
            var killed = false;
            while (!exited)
            {
                var details = killed
                    ? "Программа не завершилась даже принудительно."
                    : requested
                        ? $"Программа не завершилась за {timeout.TotalSeconds:0} с после команды остановки."
                        : "Программа не поддерживает бережную остановку или не отвечает.";
                var decision = await DecideForceStopAsync(details +
                    " Принудительное завершение может оставить систему в изменённом состоянии (например, включённый системный прокси).", reason);

                if (decision == ForceStopDecision.Cancel)
                {
                    _stopping = false;
                    await RefreshAsync(ct);
                    throw new StopCancelledException($"Остановка «{M.Name}» отменена.");
                }

                if (decision == ForceStopDecision.Kill)
                {
                    Context.Log.Warn("Принудительное завершение");
                    foreach (var p in processes) ProcessUtil.KillTree(p);
                    killed = true;
                }

                exited = await ProcessUtil.WaitForExitAsync(processes, decision == ForceStopDecision.Kill ? TimeSpan.FromSeconds(10) : timeout, ct);
            }

            await WaitForReleaseAsync(ct);
            await DisconnectAsync();
            _wasRunning = false;
            SetStatus(ModuleStatus.Stopped);
        }
        finally
        {
            _stopping = false;
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>Посылает команду остановки. false — послать было нечем (нет IPC и нет окна).</summary>
    private async Task<bool> RequestGracefulStopAsync(StopReason reason, List<Process> processes, CancellationToken ct)
    {
        switch (M.Stop.Strategy)
        {
            case "ipc":
                if (_link is not { IsConnected: true }) await TryConnectAsync(TimeSpan.FromSeconds(2), ct);
                if (_link is not { IsConnected: true }) return false;
                try
                {
                    await _link.CallAsync(HostLinkProtocol.Methods.Shutdown,
                        new { reason = reason.ToString().ToLowerInvariant() }, CallTimeout, ct);
                    return true;
                }
                catch (HostLinkException ex)
                {
                    // Разрыв канала сразу после команды — нормальный исход: программа уже выходит.
                    Context.Log.Warn($"shutdown: {ex.Message}");
                    return !(_link?.IsConnected ?? false);
                }

            case "close":
                var any = false;
                foreach (var p in processes)
                {
                    try { any |= p.CloseMainWindow(); }
                    catch (InvalidOperationException) { }
                }
                return any;

            case "processTree":
                // Программе нечего освобождать — завершение дерева процессов и есть штатная остановка.
                foreach (var p in processes) ProcessUtil.KillTree(p);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// После выхода основного процесса ждём, пока завершатся дочерние из папки модуля
    /// (например, sing-box у TryToCatchMe) и освободится порт.
    /// </summary>
    private async Task WaitForReleaseAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var leftovers = ProcessUtil.FindUnder(Context.ModuleDir);
            var portBusy = M.Detect?.Port is { } port && ProcessUtil.IsPortListening(port);
            foreach (var p in leftovers) p.Dispose();
            if (leftovers.Count == 0 && !portBusy) return;
            await Task.Delay(300, ct);
        }
        Context.Log.Warn("После остановки в папке модуля остались процессы или занят порт");
    }

    public override async Task RefreshAsync(CancellationToken ct)
    {
        if (!Context.IsInstalled)
        {
            SetStatus(ModuleStatus.NotInstalled);
            return;
        }

        if (!await _refreshGate.WaitAsync(0, ct)) return;
        try
        {
            var processes = FindProcesses();
            var running = processes.Count > 0;
            foreach (var p in processes) p.Dispose();

            if (!running)
            {
                await DisconnectAsync();
                if (_stopping) return;
                if (M.Detect?.Mutex is { } mutex && ProcessUtil.MutexExists(mutex))
                {
                    // Мьютекс занят, а нашего процесса нет — работает отдельно установленная копия.
                    SetStatus(ModuleState.Error, "Запущена отдельная копия",
                        $"«{M.Name}» уже запущен не из каркаса. Закройте его, чтобы каркас мог управлять модулем.");
                    return;
                }
                if (_wasRunning && Status.State == ModuleState.Running)
                {
                    _wasRunning = false;
                    SetStatus(ModuleState.Stopped, "Завершился");
                    RaiseUnexpectedExit();
                    return;
                }
                if (Status.State is not (ModuleState.Starting or ModuleState.Error)) SetStatus(ModuleStatus.Stopped);
                return;
            }

            if (UsesIpc && _link is not { IsConnected: true }) await TryConnectAsync(TimeSpan.FromMilliseconds(500), ct);

            if (_stopping) return;
            _wasRunning = true;

            if (_link is { IsConnected: true })
            {
                try
                {
                    var status = await _link.CallAsync<StatusResult>(HostLinkProtocol.Methods.GetStatus, null, CallTimeout, ct);
                    ApplyStatus(status);
                }
                catch (HostLinkException ex)
                {
                    SetStatus(ModuleState.Running, "Нет ответа", ex.Message);
                }
            }
            else
            {
                SetStatus(ModuleState.Running, UsesIpc ? "Работает (без связи)" : "Работает");
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyStatus(StatusResult? status)
    {
        if (status is null) return;
        var state = status.State switch
        {
            "error" => ModuleState.Error,
            "busy" => ModuleState.Starting,
            _ => ModuleState.Running,
        };
        SetStatus(state, status.Summary, status.Detail);
    }

    private async Task TryConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        await DisconnectAsync();
        var link = await HostLinkClient.TryConnectAsync(PipeName, Context.ModuleDir, timeout, ct);
        if (link is null) return;

        try
        {
            _hello = await link.CallAsync<HelloResult>(HostLinkProtocol.Methods.Hello,
                new { protocol = HostLinkProtocol.Version, host = "AllInOne" }, CallTimeout, ct);
        }
        catch (HostLinkException ex)
        {
            Context.Log.Warn($"hello: {ex.Message}");
            await link.DisposeAsync();
            return;
        }

        if (_hello is { Protocol: > HostLinkProtocol.Version })
        {
            Context.Log.Warn($"Модуль говорит на протоколе {_hello.Protocol}, каркас — на {HostLinkProtocol.Version}");
        }

        link.EventReceived += OnLinkEvent;
        link.Disconnected += () => _ = Context.Host.InvokeOnUiAsync(() => _ = RefreshAsync(CancellationToken.None));
        _link = link;
        Context.Log.Info($"Связь установлена: {M.Name} {_hello?.AppVersion}, pid {_hello?.ProcessId}");
    }

    private void OnLinkEvent(string name, JsonNode? data)
    {
        if (data is null) return;
        switch (name)
        {
            case HostLinkProtocol.Events.StatusChanged:
                var status = data.Deserialize<StatusResult>(Json.Compact);
                _ = Context.Host.InvokeOnUiAsync(() => { if (!_stopping) ApplyStatus(status); });
                break;

            case HostLinkProtocol.Events.Notify:
                var title = data["title"]?.GetValue<string>() ?? M.Name;
                var text = data["text"]?.GetValue<string>() ?? "";
                if (text.Length > 0) Context.Notify(title, text);
                break;
        }
    }

    private async Task DisconnectAsync()
    {
        var link = _link;
        _link = null;
        _hello = null;
        if (link is not null) await link.DisposeAsync();
    }

    private async Task CallQuietAsync(string method, object? parameters)
    {
        if (_link is not { IsConnected: true }) return;
        try
        {
            await _link.CallAsync(method, parameters, CallTimeout, CancellationToken.None);
        }
        catch (HostLinkException ex)
        {
            Context.Notify(M.Name, ex.Message);
        }
    }

    private List<Process> FindProcesses()
    {
        if (M.Run is null) return [];
        var name = M.Detect?.Process ?? Path.GetFileName(ExePath);
        return ProcessUtil.Find(name, ExePath);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _refreshGate.Dispose();
    }
}
