namespace AllInOne.Sdk;

/// <summary>
/// Модуль каркаса. Один экземпляр на установленный (или доступный для установки) модуль.
/// Все методы вызываются из UI-потока каркаса; долгую работу модуль уносит в фон сам.
/// </summary>
public interface IModule : IAsyncDisposable
{
    string Id { get; }

    ModuleStatus Status { get; }

    event EventHandler<ModuleStatus>? StatusChanged;

    /// <summary>Запускает модуль. Повторный вызов на работающем модуле ничего не делает.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Бережная остановка. Возвращает управление только когда процесс завершён,
    /// системные ресурсы отпущены (порт, драйвер, системный прокси, гамма, курсор)
    /// и файлы модуля разблокированы. Убивать процесс без спроса нельзя —
    /// сначала <see cref="IModuleContext.ConfirmForceStopAsync"/>.
    /// </summary>
    Task StopAsync(StopReason reason, CancellationToken ct);

    /// <summary>Перечитывает состояние (процесс, служба, порт) без изменения чего-либо.</summary>
    Task RefreshAsync(CancellationToken ct);

    /// <summary>Версия того, что сейчас установлено, или null, если модуль не установлен.</summary>
    string? InstalledVersion { get; }

    /// <summary>Страница модуля (WPF UserControl) или null — тогда каркас покажет общую страницу.</summary>
    object? CreateView();

    /// <summary>Быстрые действия для меню трея и плитки на главной.</summary>
    IReadOnlyList<ModuleAction> Actions { get; }
}

/// <summary>
/// Необязательные хуки установки и обновления. Реализуют модули, которым мало
/// «остановить → заменить папку → запустить» (zapret, tg-ws-proxy).
/// </summary>
public interface IModuleInstallHooks
{
    /// <summary>
    /// Вызывается после бережной остановки и перед заменой файлов: освободить всё,
    /// что держит файлы payload (например, удалить службу и драйвер WinDivert).
    /// Возвращает данные, которые нужно вернуть после замены (например, «служба была установлена»).
    /// </summary>
    Task<IDictionary<string, string>> BeforeReplaceAsync(bool isUpdate, CancellationToken ct);

    /// <summary>Вызывается после распаковки новой версии: донастройка, восстановление состояния.</summary>
    Task AfterReplaceAsync(bool isUpdate, IDictionary<string, string> saved, CancellationToken ct);

    /// <summary>Версия, прочитанная из самих файлов payload (если её можно узнать надёжнее, чем из манифеста).</summary>
    string? ReadPayloadVersion();
}

public enum ModuleState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Updating,
    Error,
}

public enum StopReason
{
    User,
    Update,
    Uninstall,
    HostExit,
    SystemShutdown,
    Restart,
}

/// <param name="State">Состояние модуля.</param>
/// <param name="Summary">Короткая строка для плитки: «Яркость 60 %», «Стратегия: general (ALT)».</param>
/// <param name="Detail">Подробности или текст ошибки.</param>
public sealed record ModuleStatus(ModuleState State, string? Summary = null, string? Detail = null)
{
    public static ModuleStatus NotInstalled { get; } = new(ModuleState.NotInstalled);
    public static ModuleStatus Stopped { get; } = new(ModuleState.Stopped);

    public bool IsActive => State is ModuleState.Running or ModuleState.Starting or ModuleState.Stopping;
}

public sealed record ModuleAction(string Id, string Title, Func<Task> Execute);
