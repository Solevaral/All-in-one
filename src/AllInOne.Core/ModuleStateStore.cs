namespace AllInOne.Core;

/// <summary>Пользовательские флаги модулей: data\modules.json.</summary>
public sealed class ModuleStateStore
{
    public Dictionary<string, ModuleUserState> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ModuleUserState Get(string id)
    {
        if (!Modules.TryGetValue(id, out var state))
        {
            state = new ModuleUserState();
            Modules[id] = state;
        }
        return state;
    }

    public static ModuleStateStore Load()
    {
        var store = JsonFile.Read<ModuleStateStore>(AppPaths.ModuleStateFile) ?? new ModuleStateStore();
        store.Modules = new Dictionary<string, ModuleUserState>(store.Modules, StringComparer.OrdinalIgnoreCase);
        return store;
    }

    public void Save() => JsonFile.TryWrite(AppPaths.ModuleStateFile, this);
}

public sealed class ModuleUserState
{
    /// <summary>Запускать вместе с каркасом.</summary>
    public bool Autostart { get; set; }

    /// <summary>Ставить обновления автоматически (работающий модуль — при следующем запуске каркаса).</summary>
    public bool AutoUpdate { get; set; }

    /// <summary>Перезапускать, если процесс модуля неожиданно завершился (экспериментально).</summary>
    public bool RestartOnCrash { get; set; }

    /// <summary>Отложенное автообновление: версия, которую нужно поставить при следующем запуске каркаса.</summary>
    public string? PendingUpdate { get; set; }

    /// <summary>Последняя известная версия в релизах (для значка «есть обновление» без сети).</summary>
    public string? LatestKnown { get; set; }
}
