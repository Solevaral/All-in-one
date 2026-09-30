namespace AllInOne.Core;

/// <summary>Настройки каркаса: data\host.json.</summary>
public sealed class HostSettings
{
    /// <summary>Сворачиваться в трей при запуске (при автозапуске — всегда).</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Интервал фоновой проверки обновлений, часы. 0 — только при запуске.</summary>
    public int UpdateCheckHours { get; set; } = 6;

    /// <summary>Проверять обновления самого каркаса.</summary>
    public bool CheckHostUpdates { get; set; } = true;

    /// <summary>Что делать с модулями при выходе из каркаса.</summary>
    public ExitBehavior OnExit { get; set; } = ExitBehavior.Ask;

    /// <summary>Закрытие окна крестиком сворачивает в трей, а не завершает каркас.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Пользователь уже видел предупреждение о пути установки.</summary>
    public bool PathWarningShown { get; set; }

    /// <summary>
    /// Экспериментальные функции: перезапуск модуля после неожиданного завершения,
    /// встраивание окна программы в окно All in One.
    /// </summary>
    public bool ExperimentalFeatures { get; set; }

    /// <summary>Адрес удалённого каталога. Пусто — адрес по умолчанию.</summary>
    public string? CatalogUrl { get; set; }

    public DateTime? LastUpdateCheck { get; set; }

    public static HostSettings Load() => JsonFile.Read<HostSettings>(AppPaths.HostSettingsFile) ?? new HostSettings();

    public void Save() => JsonFile.TryWrite(AppPaths.HostSettingsFile, this);
}

public enum ExitBehavior
{
    /// <summary>Спросить при выходе.</summary>
    Ask,

    /// <summary>Бережно остановить все модули.</summary>
    StopModules,

    /// <summary>Оставить модули работать (каркас подключится к ним при следующем запуске).</summary>
    KeepRunning,
}
