using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AllInOne.Core;
using AllInOne.Core.Modules;
using AllInOne.Core.Processes;
using AllInOne.Sdk;
using AllInOne.Ui;

namespace AllInOne.Modules.ShutdownTimer;

public sealed class ShutdownTimerFactory : IModuleFactory
{
    public string Kind => "builtin:shutdown-timer";

    public ModuleBase Create(ModuleContext context) => new ShutdownTimerModule(context);
}

/// <summary>
/// Таймер выключения. Живёт в процессе каркаса. Перед выключением, перезагрузкой или выходом
/// из системы бережно останавливает все модули (прежде всего VPN — он держит системный прокси).
/// </summary>
public sealed class ShutdownTimerModule : ModuleBase
{
    private static readonly TimeSpan FirstWarning = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LastMinute = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<TimeSpan> _warned = [];
    private TimerState _state;
    private bool _restoredChecked;
    private bool _executing;
    private CountdownWindow? _countdown;

    public ShutdownTimerModule(ModuleContext context) : base(context)
    {
        _state = JsonFile.Read<TimerState>(StatePath) ?? new TimerState();
        _tick.Tick += (_, _) => OnTick();
        UpdateStatus();
    }

    private string StatePath => Path.Combine(Context.DataDir, "state.json");

    internal TimerState State => _state;

    /// <summary>Таймер взведён или изменился — страница перерисовывается.</summary>
    internal event EventHandler? StateChanged;

    public override IReadOnlyList<ModuleAction> Actions
    {
        get
        {
            if (!Context.IsInstalled) return [];
            if (_state.Target is not null)
            {
                return
                [
                    new ModuleAction("cancel", "Отменить таймер выключения", () => { Cancel(); return Task.CompletedTask; }),
                    new ModuleAction("plus10", "Отложить выключение на 10 мин", () => { Postpone(TimeSpan.FromMinutes(10)); return Task.CompletedTask; }),
                ];
            }
            return
            [
                new ModuleAction("in30", "Выключить через 30 мин", () => { ArmIn(TimeSpan.FromMinutes(30), PowerAction.Shutdown); return Task.CompletedTask; }),
                new ModuleAction("in60", "Выключить через 1 ч", () => { ArmIn(TimeSpan.FromHours(1), PowerAction.Shutdown); return Task.CompletedTask; }),
                new ModuleAction("in120", "Выключить через 2 ч", () => { ArmIn(TimeSpan.FromHours(2), PowerAction.Shutdown); return Task.CompletedTask; }),
            ];
        }
    }

    // ---------- управление ----------

    public void ArmIn(TimeSpan delay, PowerAction action) => Arm(DateTime.Now + delay, action);

    public void ArmAt(TimeOnly time, PowerAction action)
    {
        var target = DateTime.Today + time.ToTimeSpan();
        if (target <= DateTime.Now.AddSeconds(30)) target = target.AddDays(1);   // время сегодня уже прошло — значит, завтра
        Arm(target, action);
    }

    private void Arm(DateTime target, PowerAction action)
    {
        _state.Target = target;
        _state.Action = action;
        _warned.Clear();
        Save();
        Context.Log.Info($"Таймер: {TimerState.Title(action)} в {target:dd.MM HH:mm:ss}");
        Context.Notify("Таймер выключения", $"{TimerState.Title(action)} в {target:HH:mm} (через {FormatSpan(target - DateTime.Now)}).");
        _tick.Start();
        UpdateStatus();
    }

    public void Cancel()
    {
        if (_state.Target is null) return;
        _state.Target = null;
        Save();
        CloseCountdown();
        _tick.Stop();
        Context.Log.Info("Таймер отменён");
        Context.Notify("Таймер выключения", "Таймер отменён.");
        UpdateStatus();
    }

    public void Postpone(TimeSpan by)
    {
        if (_state.Target is not { } target) return;
        var from = target < DateTime.Now ? DateTime.Now : target;
        _state.Target = from + by;
        _warned.Clear();
        Save();
        CloseCountdown();
        UpdateStatus();
    }

    internal void SaveFormDefaults(Action<TimerState> update)
    {
        update(_state);
        Save();
    }

    private void Save() => JsonFile.TryWrite(StatePath, _state);

    // ---------- отсчёт ----------

    private void OnTick()
    {
        if (_state.Target is not { } target || _executing)
        {
            if (_state.Target is null) _tick.Stop();
            return;
        }

        var left = target - DateTime.Now;
        if (left <= LastMinute && _warned.Add(LastMinute))
        {
            // Последняя минута: окно поверх всех с кнопками «Отменить» и «+10 мин».
            ShowCountdown();
        }
        else if (left <= FirstWarning && left > LastMinute && _warned.Add(FirstWarning))
        {
            Context.Notify("Таймер выключения", $"{TimerState.Title(_state.Action)} через {FormatSpan(left)}.");
        }

        _countdown?.Update(left);
        UpdateStatus();

        if (left <= TimeSpan.Zero) _ = ExecuteAsync();
    }

    private async Task ExecuteAsync()
    {
        if (_executing) return;
        _executing = true;
        _tick.Stop();
        var action = _state.Action;
        var force = _state.Force;

        // Состояние сбрасываем заранее: после перезагрузки таймер не должен сработать снова.
        _state.Target = null;
        Save();
        CloseCountdown();

        try
        {
            if (TimerState.EndsSession(action))
            {
                SetStatus(ModuleState.Running, "Остановка модулей…");
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await Context.Host.StopAllModulesAsync(StopReason.SystemShutdown, Id, cts.Token);
                }
                catch (Exception ex)
                {
                    // Выключаемся всё равно: пользователь просил выключить ПК, а Windows сама завершит оставшееся.
                    Context.Log.Warn("Перед выключением не все модули остановились: " + ex.Message);
                }
            }

            Context.Log.Info($"Таймер сработал: {TimerState.Title(action)}");
            await PerformAsync(action, force);
        }
        catch (Exception ex)
        {
            Context.Log.Error("Действие таймера не выполнилось", ex);
            Context.Notify("Таймер выключения", "Не удалось выполнить: " + ex.Message);
        }
        finally
        {
            _executing = false;
            UpdateStatus();
        }
    }

    private static async Task PerformAsync(PowerAction action, bool force)
    {
        var f = force ? " /f" : "";
        switch (action)
        {
            case PowerAction.Shutdown:
                await RunShutdownAsync("/s /t 0" + f);
                break;
            case PowerAction.Restart:
                await RunShutdownAsync("/r /t 0" + f);
                break;
            case PowerAction.LogOff:
                await RunShutdownAsync("/l" + f);
                break;
            case PowerAction.Sleep:
                if (!SetSuspendState(false, force, false)) throw new InvalidOperationException("Windows не перешла в сон.");
                break;
            case PowerAction.Hibernate:
                if (!SetSuspendState(true, force, false)) throw new InvalidOperationException("Гибернация недоступна — возможно, она выключена (powercfg /hibernate on).");
                break;
        }
    }

    private static async Task RunShutdownAsync(string args)
    {
        var r = await Cli.RunRawAsync(Cli.System32("shutdown.exe"), args);
        if (!r.Ok) throw new InvalidOperationException("shutdown.exe: " + r.All.Trim());
    }

    // ---------- окно обратного отсчёта ----------

    private void ShowCountdown()
    {
        if (_countdown is not null || _state.Target is not { } target) return;
        _countdown = new CountdownWindow(TimerState.Title(_state.Action), target - DateTime.Now);
        _countdown.CancelRequested += Cancel;
        _countdown.PostponeRequested += () => Postpone(TimeSpan.FromMinutes(10));
        _countdown.Closed += (_, _) => _countdown = null;
        _countdown.Show();
    }

    private void CloseCountdown()
    {
        var w = _countdown;
        _countdown = null;
        w?.Close();
    }

    // ---------- IModule ----------

    private void UpdateStatus()
    {
        if (!Context.IsInstalled)
        {
            SetStatus(ModuleStatus.NotInstalled);
        }
        else if (_state.Target is { } target && !_executing)
        {
            var left = target - DateTime.Now;
            SetStatus(ModuleState.Running, $"{TimerState.Title(_state.Action)} в {target:HH:mm} · через {FormatSpan(left)}");
        }
        else if (!_executing)
        {
            SetStatus(ModuleState.Stopped, "Таймер не задан");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>«Остановка» встроенного модуля — только при удалении: таймер отменяется.</summary>
    public override Task StopAsync(StopReason reason, CancellationToken ct)
    {
        if (reason == StopReason.Uninstall) Cancel();
        return Task.CompletedTask;
    }

    public override async Task RefreshAsync(CancellationToken ct)
    {
        if (_restoredChecked || !Context.IsInstalled) return;
        _restoredChecked = true;

        if (_state.Target is not { } target) return;
        if (target > DateTime.Now)
        {
            // Каркас перезапустился — продолжаем отсчёт.
            _tick.Start();
            UpdateStatus();
            return;
        }

        // Срок прошёл, пока каркас не работал: сами ничего не выключаем — спрашиваем.
        var action = _state.Action;
        _state.Target = null;
        Save();
        UpdateStatus();
        var run = await Dialog.ConfirmAsync("Таймер выключения",
            $"Таймер должен был выполнить «{TimerState.Title(action)}» в {target:dd.MM HH:mm}, но каркас в это время не работал.\n\nВыполнить сейчас?",
            "Выполнить", "Не нужно");
        if (run)
        {
            _state.Target = DateTime.Now;
            _state.Action = action;
            await ExecuteAsync();
        }
    }

    public override void OnInstallationChanged()
    {
        base.OnInstallationChanged();
        UpdateStatus();
    }

    public override object? CreateView() => new ShutdownTimerView(this);

    public override ValueTask DisposeAsync()
    {
        _tick.Stop();
        CloseCountdown();
        return ValueTask.CompletedTask;
    }

    internal static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} ч {span.Minutes:00} мин";
        if (span.TotalMinutes >= 1) return $"{span.Minutes} мин {span.Seconds:00} с";
        return $"{span.Seconds} с";
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
