namespace AllInOne.Sdk;

/// <summary>Что каркас даёт модулю: пути, лог, уведомления, диалоги и сервисы каркаса.</summary>
public interface IModuleContext
{
    ModuleManifest Manifest { get; }

    /// <summary>modules\&lt;id&gt;</summary>
    string ModuleDir { get; }

    /// <summary>modules\&lt;id&gt;\payload — файлы программы, заменяются при обновлении.</summary>
    string PayloadDir { get; }

    /// <summary>modules\&lt;id&gt;\data — данные модуля, переживают обновления.</summary>
    string DataDir { get; }

    IModuleLog Log { get; }

    HttpClient Http { get; }

    /// <summary>Уведомление в трее.</summary>
    void Notify(string title, string text);

    /// <summary>Модуль не завершился вовремя: спрашивает пользователя, что делать дальше.</summary>
    Task<ForceStopDecision> ConfirmForceStopAsync(string moduleName, string details);

    /// <summary>Сервисы каркаса, доступные модулям.</summary>
    IHostServices Host { get; }
}

public enum ForceStopDecision
{
    /// <summary>Подождать ещё столько же.</summary>
    Wait,

    /// <summary>Завершить процесс принудительно.</summary>
    Kill,

    /// <summary>Отменить операцию (обновление, остановку), модуль продолжает работать.</summary>
    Cancel,
}

public interface IHostServices
{
    Version HostVersion { get; }

    /// <summary>Бережно останавливает все запущенные модули, кроме вызывающего (для таймера выключения).</summary>
    Task StopAllModulesAsync(StopReason reason, string? exceptId, CancellationToken ct);

    /// <summary>Выполняет действие в UI-потоке каркаса.</summary>
    Task InvokeOnUiAsync(Action action);
}

public interface IModuleLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
