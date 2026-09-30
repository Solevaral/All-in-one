using System.IO;
using System.Text.RegularExpressions;
using AllInOne.Core;
using AllInOne.Core.Processes;
using Microsoft.Win32;

namespace AllInOne.Modules.Zapret;

public enum CheckLevel
{
    Ok,
    Info,
    Warning,
    Problem,
}

/// <param name="Fix">Что сделать по кнопке «Исправить»; null — исправить автоматически нельзя.</param>
public sealed record DiagnosticCheck(string Title, CheckLevel Level, string Detail, string? FixTitle = null, Func<Task>? Fix = null);

/// <summary>Проверки из пункта «Run Diagnostics» service.bat — только чтение, исправления по кнопке.</summary>
internal static class ZapretDiagnostics
{
    private static readonly string[] ConflictServices = ["GoodbyeDPI", "discordfix_zapret", "winws1", "winws2"];

    public static async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(ZapretModule module, CancellationToken ct)
    {
        var checks = new List<DiagnosticCheck>();
        var files = module.Files;

        // Base Filtering Engine — без неё WinDivert не работает.
        var bfe = await ServiceUtil.QueryAsync("BFE", ct);
        checks.Add(bfe == ServiceState.Running
            ? new("Служба Base Filtering Engine", CheckLevel.Ok, "Работает.")
            : new("Служба Base Filtering Engine", CheckLevel.Problem, "Не запущена — без неё WinDivert не работает.",
                "Запустить", () => ServiceUtil.StartAsync("BFE")));

        // Системный прокси.
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
        {
            if (key?.GetValue("ProxyEnable") is int enabled && enabled != 0)
            {
                var server = key.GetValue("ProxyServer") as string ?? "?";
                checks.Add(new("Системный прокси", CheckLevel.Info,
                    $"Включён ({server}). Если это VPN-клиент (например, TryToCatchMe) — всё нормально; иначе прокси может мешать zapret."));
            }
            else
            {
                checks.Add(new("Системный прокси", CheckLevel.Ok, "Выключен."));
            }
        }

        // TCP timestamps.
        var show = await Cli.RunAsync(Cli.System32("netsh.exe"), ["interface", "tcp", "show", "global"], ct: ct);
        var tsLine = show.Output.Split('\n').FirstOrDefault(l => l.Contains("timestamps", StringComparison.OrdinalIgnoreCase) || l.Contains("метки времени", StringComparison.OrdinalIgnoreCase));
        var tsOn = tsLine is not null && (tsLine.Contains("enabled", StringComparison.OrdinalIgnoreCase) || tsLine.Contains("включ", StringComparison.OrdinalIgnoreCase));
        checks.Add(tsOn
            ? new("TCP timestamps", CheckLevel.Ok, "Включены.")
            : new("TCP timestamps", CheckLevel.Warning, "Выключены — стратегии с fooling=ts работать не будут.",
                "Включить", async () => await ZapretModule.EnableTcpTimestampsAsync(CancellationToken.None)));

        // Файлы.
        checks.Add(File.Exists(Path.Combine(files.Bin, "WinDivert64.sys"))
            ? new("Драйвер WinDivert64.sys", CheckLevel.Ok, "Файл на месте.")
            : new("Драйвер WinDivert64.sys", CheckLevel.Problem, "Файла нет — скорее всего, его удалил антивирус. Добавьте папку модуля в исключения и переустановите модуль."));

        var pathProblems = AppPaths.CheckRootPath();
        checks.Add(pathProblems.Count == 0
            ? new("Путь установки", CheckLevel.Ok, files.Root)
            : new("Путь установки", CheckLevel.Problem, string.Join(" ", pathProblems)));

        // Службы и процессы, которые мешают.
        var services = await ListServicesAsync(ct);
        foreach (var name in ConflictServices)
        {
            if (!services.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var captured = name;
            checks.Add(new($"Служба {name}", CheckLevel.Problem, "Конфликтует с zapret (другой обход блокировок).",
                "Удалить службу", async () =>
                {
                    await ServiceUtil.StopAndWaitAsync(captured, TimeSpan.FromSeconds(10));
                    await ServiceUtil.DeleteAsync(captured);
                }));
        }

        foreach (var (pattern, title) in new[]
                 {
                     ("Killer", "Killer Network Service"),
                     ("Intel.*Connectivity.*Network", "Intel Connectivity Network Service"),
                     ("TracSrvWrapper|EPWD", "Check Point"),
                     ("SmartByte", "SmartByte"),
                 })
        {
            if (services.Any(s => Regex.IsMatch(s.Name + " " + s.Display, pattern, RegexOptions.IgnoreCase)))
                checks.Add(new(title, CheckLevel.Warning, "Известно, что мешает zapret. Если обход не работает — отключите эту службу."));
        }

        var vpn = services.Where(s => s.Name.Contains("VPN", StringComparison.OrdinalIgnoreCase) || s.Display.Contains("VPN", StringComparison.OrdinalIgnoreCase)).ToList();
        if (vpn.Count > 0)
            checks.Add(new("VPN-службы", CheckLevel.Info, "Найдены: " + string.Join(", ", vpn.Select(v => v.Display)) + ". Одновременная работа с VPN может мешать zapret."));

        if (ProcessUtil.Find("AdguardSvc.exe").Count > 0)
            checks.Add(new("Adguard", CheckLevel.Warning, "Adguard может мешать работе Discord вместе с zapret."));

        // WinDivert работает, а winws нет — драйвер «застрял» (часто после GoodbyeDPI).
        var ours = module.OurProcesses();
        var anyWinws = ProcessUtil.Find("winws.exe").Count > 0;
        foreach (var p in ours) p.Dispose();
        foreach (var driver in ZapretModule.DriverServices)
        {
            var state = await ServiceUtil.QueryAsync(driver, ct);
            if (state is ServiceState.Running or ServiceState.StopPending && !anyWinws)
            {
                checks.Add(new($"Драйвер {driver}", CheckLevel.Warning, "Загружен, хотя winws не работает — его держит другая программа или он завис.",
                    "Выгрузить драйвер", () => ZapretModule.RemoveDriverAsync(CancellationToken.None)));
            }
        }

        // DoH.
        checks.Add(HasDoh()
            ? new("Защищённый DNS (DoH)", CheckLevel.Ok, "Включён.")
            : new("Защищённый DNS (DoH)", CheckLevel.Info, "Не найден. Рекомендуется включить DoH в параметрах сети Windows или в браузере — без него часть сайтов может не открыться."));

        // hosts.
        var hosts = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (File.Exists(hosts) && File.ReadAllText(hosts).Contains("youtube", StringComparison.OrdinalIgnoreCase))
            checks.Add(new("Файл hosts", CheckLevel.Warning, "В hosts есть записи про youtube — они могут мешать. Проверьте файл вручную.",
                "Открыть hosts", () => { UiOpen(hosts); return Task.CompletedTask; }));

        return checks;
    }

    private static bool HasDoh()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
        if (root is null) return false;
        return Search(root, 0);

        static bool Search(RegistryKey key, int depth)
        {
            if (key.GetValue("DohFlags") is long l && l > 0) return true;
            if (key.GetValue("DohFlags") is int i && i > 0) return true;
            if (depth > 5) return false;
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub is not null && Search(sub, depth + 1)) return true;
            }
            return false;
        }
    }

    internal sealed record ServiceInfo(string Name, string Display);

    /// <summary>Все службы и драйверы из реестра — вывод sc.exe локализован, реестр нет.</summary>
    private static Task<List<ServiceInfo>> ListServicesAsync(CancellationToken ct)
    {
        var result = new List<ServiceInfo>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null) return Task.FromResult(result);
        foreach (var name in root.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("Type") is not int) continue;   // не служба, а просто ветка параметров
            var display = key.GetValue("DisplayName") as string ?? name;
            if (display.StartsWith('@')) display = name;       // ссылка на ресурс dll — показываем имя
            result.Add(new ServiceInfo(name, display));
        }
        return Task.FromResult(result);
    }

    /// <summary>Кэш Discord (Cache, Code Cache, GPUCache) — помогает, когда Discord «не видит» обход.</summary>
    public static int ClearDiscordCache()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cleared = 0;
        foreach (var flavor in new[] { "discord", "discordptb", "discordcanary", "discorddevelopment" })
        {
            foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                var dir = Path.Combine(appData, flavor, cache);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    cleared++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Discord запущен и держит файлы — пропускаем.
                }
            }
        }
        return cleared;
    }

    private static void UiOpen(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });

}
