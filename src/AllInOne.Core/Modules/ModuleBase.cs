using AllInOne.Sdk;

namespace AllInOne.Core.Modules;

/// <summary>Общая часть модулей: статус с событием, контекст, версия из манифеста.</summary>
public abstract class ModuleBase(ModuleContext context) : IModule
{
    private ModuleStatus _status = context.IsInstalled ? ModuleStatus.Stopped : ModuleStatus.NotInstalled;

    protected ModuleContext Context { get; } = context;

    public string Id => Context.Manifest.Id;

    public ModuleStatus Status => _status;

    public event EventHandler<ModuleStatus>? StatusChanged;

    public virtual string? InstalledVersion => Context.IsInstalled ? Context.Manifest.Version : null;

    public virtual IReadOnlyList<ModuleAction> Actions => [];

    /// <summary>Процесс модуля завершился сам (не по команде каркаса).</summary>
    public event EventHandler? UnexpectedExit;

    protected void SetStatus(ModuleStatus status)
    {
        if (_status == status) return;
        if (_status.State != status.State)
            Core.Log.Info($"{Id}: {_status.State} → {status.State}{(status.Summary is { } s ? " (" + s + ")" : "")}");
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    protected void SetStatus(ModuleState state, string? summary = null, string? detail = null) =>
        SetStatus(new ModuleStatus(state, summary, detail));

    protected void RaiseUnexpectedExit() => UnexpectedExit?.Invoke(this, EventArgs.Empty);

    /// <summary>Вызывается установщиком после установки, обновления или удаления.</summary>
    public virtual void OnInstallationChanged()
    {
        if (!Context.IsInstalled) SetStatus(ModuleStatus.NotInstalled);
        else if (_status.State is ModuleState.NotInstalled or ModuleState.Updating) SetStatus(ModuleStatus.Stopped);
    }

    /// <summary>Отмечает модуль как обновляемый (плитка показывает «Обновление…»).</summary>
    public void MarkUpdating(string? summary) => SetStatus(ModuleState.Updating, summary);

    public abstract Task StartAsync(CancellationToken ct);

    public abstract Task StopAsync(StopReason reason, CancellationToken ct);

    public abstract Task RefreshAsync(CancellationToken ct);

    public virtual object? CreateView() => null;

    /// <summary>
    /// Файл, по которому видно, что программа лежит в своей папке modules\&lt;папка&gt; (путь внутри неё).
    /// По нему All in One подхватывает программу, распакованную вручную. null — не подхватывать.
    /// </summary>
    public virtual string? ProgramMarker => Context.Manifest.Run?.Exe;

    /// <summary>Версия программы по её файлам (для подхваченной вручную). null — неизвестна.</summary>
    public virtual string? ReadProgramVersion()
    {
        if (this is IModuleInstallHooks hooks && hooks.ReadPayloadVersion() is { Length: > 0 } fromHooks) return fromHooks;
        if (ProgramMarker is not { } marker) return null;
        var path = Context.Resolve(marker);
        try
        {
            return File.Exists(path) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Общая развязка «модуль не ответил вовремя»: для fallback=kill — завершить без спроса,
    /// иначе спросить пользователя (подождать ещё, завершить или отменить).
    /// При выключении ПК (таймер) спрашивать некого — за компьютером может никого не быть,
    /// а Windows всё равно завершит процесс, поэтому завершаем сразу.
    /// </summary>
    protected async Task<ForceStopDecision> DecideForceStopAsync(string details, StopReason reason)
    {
        if (Context.Manifest.Stop.Fallback == "kill" || reason == StopReason.SystemShutdown) return ForceStopDecision.Kill;
        return await Context.ConfirmForceStopAsync(Context.Manifest.Name, details);
    }
}

/// <summary>Пользователь отказался завершать модуль принудительно — операция отменяется.</summary>
public sealed class StopCancelledException(string message) : OperationCanceledException(message);
