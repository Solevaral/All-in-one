using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllInOne.Sdk;

/// <summary>
/// Описание модуля. Один и тот же формат используется в каталоге (без версии)
/// и в modules\&lt;id&gt;\module.json установленного модуля (с версией).
/// Установленный манифест хранит копию <see cref="Source"/>, поэтому модуль
/// обновляется, даже если запись пропала из каталога.
/// </summary>
public sealed class ModuleManifest
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public string? Category { get; set; }

    public string? Author { get; set; }

    public string? Homepage { get; set; }

    /// <summary>
    /// external — отдельный процесс под управлением общего драйвера;
    /// adapter:&lt;имя&gt; — встроенный в каркас адаптер (zapret, tgwsproxy);
    /// builtin:&lt;имя&gt; — встроенный модуль без payload (таймер выключения).
    /// </summary>
    public string Kind { get; set; } = "external";

    /// <summary>Установленная версия. В каталоге не задаётся — версии берутся из GitHub Releases.</summary>
    public string? Version { get; set; }

    /// <summary>Имя ассета, из которого установлена текущая версия.</summary>
    public string? InstalledAsset { get; set; }

    public string? MinHostVersion { get; set; }

    public ModuleSource? Source { get; set; }

    public RunSpec? Run { get; set; }

    public DetectSpec? Detect { get; set; }

    public IpcSpec? Ipc { get; set; }

    public StopSpec Stop { get; set; } = new();

    /// <summary>
    /// Пути (относительно папки модуля, glob с * и **), которые переносятся
    /// из старой версии в новую при обновлении. data\** сохраняется всегда.
    /// </summary>
    public List<string> Preserve { get; set; } = [];

    /// <summary>Собственный автозапуск программы, который каркас убирает, чтобы не было двойного старта.</summary>
    public LegacyAutostartSpec? LegacyAutostart { get; set; }

    [JsonIgnore]
    public bool IsExternal => Kind == "external";

    [JsonIgnore]
    public bool IsBuiltIn => Kind.StartsWith("builtin:", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsAdapter => Kind.StartsWith("adapter:", StringComparison.Ordinal);

    public ModuleManifest Clone() =>
        JsonSerializer.Deserialize<ModuleManifest>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
}

public sealed class ModuleSource
{
    /// <summary>github — релизы репозитория. none — ставить нечего (встроенный модуль).</summary>
    public string Type { get; set; } = "github";

    /// <summary>owner/repo</summary>
    public string? Repo { get; set; }

    /// <summary>Шаблон имени ассета (* — любая подстрока). Берётся, если нет подходящего варианта ниже.</summary>
    public string? Asset { get; set; }

    /// <summary>Лёгкий ассет для машины с установленным .NET 9 Desktop Runtime.</summary>
    public string? AssetNet9 { get; set; }

    /// <summary>Ассет для Windows на ARM64.</summary>
    public string? AssetArm64 { get; set; }

    /// <summary>
    /// Как разложить скачанное: exe — положить файл в payload под именем <see cref="SaveAs"/>;
    /// zip — распаковать в payload (верхняя общая папка архива отрезается).
    /// По умолчанию определяется по расширению ассета.
    /// </summary>
    public string? Layout { get; set; }

    /// <summary>Имя, под которым exe-ассет кладётся в payload (чтобы путь запуска не менялся от версии к версии).</summary>
    public string? SaveAs { get; set; }

    public bool Prerelease { get; set; }
}

public sealed class RunSpec
{
    /// <summary>Путь к exe относительно папки модуля.</summary>
    public string Exe { get; set; } = "";

    /// <summary>Аргументы; {pipe} заменяется именем канала IPC.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Рабочая папка относительно папки модуля (по умолчанию — папка exe).</summary>
    public string? WorkingDir { get; set; }
}

public sealed class DetectSpec
{
    /// <summary>Имя мьютекса единственного экземпляра программы.</summary>
    public string? Mutex { get; set; }

    /// <summary>Имя процесса (с .exe). Процесс считается нашим, только если путь совпадает с payload.</summary>
    public string? Process { get; set; }

    /// <summary>Порт на 127.0.0.1, который программа слушает, когда работает.</summary>
    public int? Port { get; set; }
}

public sealed class IpcSpec
{
    /// <summary>Имя канала без \\.\pipe\. По умолчанию AllInOne.&lt;id&gt;.</summary>
    public string? Pipe { get; set; }

    public int Protocol { get; set; } = HostLinkProtocol.Version;
}

public sealed class StopSpec
{
    /// <summary>ipc — команда shutdown по каналу; adapter — решает адаптер; processTree — завершить дерево процессов.</summary>
    public string Strategy { get; set; } = "ipc";

    public int TimeoutSec { get; set; } = 15;

    /// <summary>ask — спросить пользователя перед принудительным завершением; kill — можно завершить без спроса.</summary>
    public string Fallback { get; set; } = "ask";
}

public sealed class LegacyAutostartSpec
{
    /// <summary>Имя значения в HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
    public string? RunKey { get; set; }
}

public static class Json
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Однострочный вариант для протокола IPC (JSON-lines).</summary>
    public static JsonSerializerOptions Compact { get; } = new(Options) { WriteIndented = false };
}
