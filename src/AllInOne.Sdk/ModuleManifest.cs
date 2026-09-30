using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllInOne.Sdk;

/// <summary>
/// Описание модуля. Один и тот же формат используется в каталоге (без версии)
/// и в data\modules\&lt;id&gt;\module.json установленного модуля (с версией).
/// Установленный манифест хранит копию <see cref="Source"/>, поэтому модуль
/// обновляется, даже если запись пропала из каталога.
/// </summary>
public sealed class ModuleManifest
{
    /// <summary>2 — раскладка «программа в modules\&lt;Имя&gt;, служебное в data» (каркас 0.2+).</summary>
    public const int CurrentSchema = 2;

    public int Schema { get; set; } = CurrentSchema;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Имя папки программы в modules\. По умолчанию — название модуля.</summary>
    public string? Folder { get; set; }

    public string? Description { get; set; }

    public string? Category { get; set; }

    public string? Author { get; set; }

    public string? Homepage { get; set; }

    /// <summary>
    /// external — отдельный процесс под управлением общего драйвера;
    /// adapter:&lt;имя&gt; — встроенный в каркас адаптер (zapret, tgwsproxy);
    /// builtin:&lt;имя&gt; — встроенный модуль без программы (таймер выключения).
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
    /// Пути внутри папки программы (glob с * и **), которые переносятся из старой версии
    /// в новую при обновлении. Данные в data\modules\&lt;id&gt; сохраняются всегда.
    /// </summary>
    public List<string> Preserve { get; set; } = [];

    /// <summary>Собственный автозапуск программы, который каркас убирает, чтобы не было двойного старта.</summary>
    public LegacyAutostartSpec? LegacyAutostart { get; set; }

    /// <summary>Своё окно программы можно встроить в окно каркаса (экспериментально).</summary>
    public bool Embeddable { get; set; }

    [JsonIgnore]
    public string ProgramFolder => Folder is { Length: > 0 } f ? f : SafeName(Name.Length > 0 ? Name : Id);

    [JsonIgnore]
    public bool IsExternal => Kind == "external";

    [JsonIgnore]
    public bool IsBuiltIn => Kind.StartsWith("builtin:", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsAdapter => Kind.StartsWith("adapter:", StringComparison.Ordinal);

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "module" : clean;
    }

    /// <summary>
    /// Проверка путей: каркас работает от администратора и удаляет папку модуля при удалении,
    /// поэтому id, папка, exe, рабочая папка и preserve не должны выводить за пределы папки модуля.
    /// Возвращает текст ошибки или null.
    /// </summary>
    public string? Validate()
    {
        if (!IsSafeId(Id)) return $"недопустимый id «{Id}» (латиница, цифры, «-», «_», «.»)";
        if (Folder is { Length: > 0 } && !IsSafeName(Folder)) return $"недопустимая папка «{Folder}»";
        if (Run is { } run)
        {
            if (!IsSafeRelative(run.Exe)) return $"run.exe «{run.Exe}» выходит за папку программы";
            if (run.WorkingDir is { Length: > 0 } wd && !IsSafeRelative(wd)) return $"run.workingDir «{wd}» выходит за папку программы";
        }
        foreach (var pattern in Preserve)
            if (!IsSafeRelative(pattern)) return $"preserve «{pattern}» выходит за папку программы";
        if (Source?.SaveAs is { Length: > 0 } saveAs && !IsSafeName(saveAs)) return $"source.saveAs «{saveAs}» — не имя файла";
        return null;
    }

    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id is not ("." or "..") &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');

    /// <summary>Одно имя файла или папки, без разделителей и «..».</summary>
    private static bool IsSafeName(string name) =>
        name.Trim().Length > 0 && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Относительный путь (можно с * и **), ни один сегмент которого не «..».</summary>
    private static bool IsSafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\')) return false;
        return path.Split('/', '\\').All(part => part != "..");
    }

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
    /// Как разложить скачанное: file — положить файл в папку программы под именем <see cref="SaveAs"/>;
    /// zip — распаковать в папку программы (верхняя общая папка архива отрезается).
    /// По умолчанию определяется по расширению ассета.
    /// </summary>
    public string? Layout { get; set; }

    /// <summary>Имя, под которым exe-ассет кладётся в папку программы (чтобы путь запуска не менялся от версии к версии).</summary>
    public string? SaveAs { get; set; }

    public bool Prerelease { get; set; }
}

public sealed class RunSpec
{
    /// <summary>Путь к exe относительно папки программы.</summary>
    public string Exe { get; set; } = "";

    /// <summary>Аргументы; {pipe} заменяется именем канала IPC.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Рабочая папка относительно папки программы (по умолчанию — папка exe).</summary>
    public string? WorkingDir { get; set; }
}

public sealed class DetectSpec
{
    /// <summary>Имя мьютекса единственного экземпляра программы.</summary>
    public string? Mutex { get; set; }

    /// <summary>Имя процесса (с .exe). Процесс считается нашим, только если он запущен из папки программы.</summary>
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
