namespace AllInOne.Core;

/// <summary>
/// Раскладка папки установки (по умолчанию C:\Program Files\All in One):
///   AllInOne.exe
///   modules\&lt;Имя модуля&gt;\   — сами программы, их можно запускать и без каркаса
///   data\                      — настройки, каталог, логи, манифесты и данные модулей, staging, backup
/// Корень можно переопределить переменной окружения ALLINONE_ROOT (для отладки и тестов).
/// </summary>
public static class AppPaths
{
    public static string Root { get; private set; } = ResolveRoot();

    public static string Data => Path.Combine(Root, "data");
    public static string Logs => Path.Combine(Data, "logs");
    public static string Modules => Path.Combine(Root, "modules");
    public static string ModulesData => Path.Combine(Data, "modules");
    public static string Staging => Path.Combine(Data, "staging");
    public static string Backup => Path.Combine(Data, "backup");

    public static string HostSettingsFile => Path.Combine(Data, "host.json");
    public static string ModuleStateFile => Path.Combine(Data, "modules.json");
    public static string CatalogCacheFile => Path.Combine(Data, "catalog.cache.json");
    public static string GitHubCacheFile => Path.Combine(Data, "gh-cache.json");

    /// <summary>Манифест и данные модуля: data\modules\&lt;id&gt;.</summary>
    public static string ModuleDataDir(string id) => Path.Combine(ModulesData, id);

    /// <summary>Папка программы: modules\&lt;имя&gt;.</summary>
    public static string ProgramDir(string folder) => Path.Combine(Modules, folder);

    /// <summary>Путь к exe каркаса. У single-file сборки Assembly.Location пуст, поэтому берём путь процесса.</summary>
    public static string HostExe => Environment.ProcessPath ?? Path.Combine(Root, "AllInOne.exe");

    public static void UseRoot(string root) => Root = Path.GetFullPath(root);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Modules);
        Directory.CreateDirectory(ModulesData);
    }

    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("ALLINONE_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Проблемы пути установки. zapret (cygwin) не работает из путей с кириллицей,
    /// OneDrive блокирует и подменяет файлы во время синхронизации.
    /// </summary>
    public static IReadOnlyList<string> CheckRootPath()
    {
        var problems = new List<string>();
        if (Root.Any(c => c > 127))
            problems.Add("В пути есть не-латинские символы — zapret из такого пути не запустится.");
        if (Root.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            problems.Add("Папка в OneDrive — синхронизация блокирует файлы модулей.");
        return problems;
    }

    /// <summary>Имя папки из названия модуля: без символов, запрещённых в путях.</summary>
    public static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "module" : clean;
    }
}
