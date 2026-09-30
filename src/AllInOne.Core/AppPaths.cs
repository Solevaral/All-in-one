namespace AllInOne.Core;

/// <summary>
/// Портативная раскладка: всё лежит рядом с exe каркаса.
/// Корень можно переопределить переменной окружения ALLINONE_ROOT (для отладки и тестов).
/// </summary>
public static class AppPaths
{
    public static string Root { get; private set; } = ResolveRoot();

    public static string Data => Path.Combine(Root, "data");
    public static string Logs => Path.Combine(Data, "logs");
    public static string Modules => Path.Combine(Root, "modules");
    public static string Staging => Path.Combine(Root, "staging");
    public static string Backup => Path.Combine(Root, "backup");

    public static string HostSettingsFile => Path.Combine(Data, "host.json");
    public static string ModuleStateFile => Path.Combine(Data, "modules.json");
    public static string CatalogCacheFile => Path.Combine(Data, "catalog.cache.json");
    public static string GitHubCacheFile => Path.Combine(Data, "gh-cache.json");

    public static string ModuleDir(string id) => Path.Combine(Modules, id);

    /// <summary>Путь к exe каркаса. У single-file сборки Assembly.Location пуст, поэтому берём путь процесса.</summary>
    public static string HostExe => Environment.ProcessPath ?? Path.Combine(Root, "AllInOne.exe");

    public static void UseRoot(string root) => Root = Path.GetFullPath(root);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Modules);
    }

    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("ALLINONE_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Проблемы пути установки. zapret (cygwin) не работает из путей с кириллицей,
    /// а OneDrive блокирует и подменяет файлы во время синхронизации.
    /// </summary>
    public static IReadOnlyList<string> CheckRootPath()
    {
        var problems = new List<string>();
        if (Root.Any(c => c > 127))
            problems.Add("В пути есть не-латинские символы (например, кириллица) — zapret из такого пути не запустится.");
        if (Root.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            problems.Add("Папка лежит в OneDrive — синхронизация может блокировать файлы модулей.");
        if (Root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            problems.Add("Папка лежит в Program Files — лучше выбрать отдельную папку, например C:\\AllInOne.");
        return problems;
    }
}
