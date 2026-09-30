namespace AllInOne.Modules.ShutdownTimer;

public enum PowerAction
{
    Shutdown,
    Restart,
    Sleep,
    Hibernate,
    LogOff,
}

/// <summary>Состояние таймера: modules\shutdown-timer\data\state.json. Переживает перезапуск каркаса.</summary>
public sealed class TimerState
{
    /// <summary>Когда сработать (локальное время). null — таймер не взведён.</summary>
    public DateTime? Target { get; set; }

    public PowerAction Action { get; set; } = PowerAction.Shutdown;

    /// <summary>Принудительно закрывать программы (shutdown /f).</summary>
    public bool Force { get; set; }

    /// <summary>Последний выбранный режим в форме: true — «в ЧЧ:ММ», false — «через».</summary>
    public bool AtTimeMode { get; set; }

    public int LastHours { get; set; }

    public int LastMinutes { get; set; } = 30;

    public string LastTime { get; set; } = "23:30";

    public static string Title(PowerAction action) => action switch
    {
        PowerAction.Shutdown => "Выключение",
        PowerAction.Restart => "Перезагрузка",
        PowerAction.Sleep => "Сон",
        PowerAction.Hibernate => "Гибернация",
        PowerAction.LogOff => "Выход из системы",
        _ => action.ToString(),
    };

    /// <summary>Нужно ли перед действием останавливать модули (сон и гибернация процессы не завершают).</summary>
    public static bool EndsSession(PowerAction action) => action is PowerAction.Shutdown or PowerAction.Restart or PowerAction.LogOff;
}
