using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AllInOne.Modules.Zapret;

public enum GameFilterMode
{
    Disabled,
    All,
    Tcp,
    Udp,
}

public enum IpsetMode
{
    /// <summary>Список не применяется (в файле только адрес-заглушка).</summary>
    None,

    /// <summary>Любые адреса (файл пуст).</summary>
    Any,

    /// <summary>Загруженный список адресов.</summary>
    Loaded,
}

public sealed record GameFilter(GameFilterMode Mode, string TcpRange, string UdpRange)
{
    public const string DefaultRange = "1024-65535";

    public string EffectiveTcp => Mode is GameFilterMode.All or GameFilterMode.Tcp ? TcpRange : BatStrategyParser.DisabledPort;
    public string EffectiveUdp => Mode is GameFilterMode.All or GameFilterMode.Udp ? UdpRange : BatStrategyParser.DisabledPort;
}

/// <summary>
/// Файлы zapret-discord-youtube, которыми управляет меню service.bat. Форматы повторяют upstream
/// в точности, чтобы bat-файлы и service.bat, запущенные вручную, видели те же настройки.
/// </summary>
public sealed partial class ZapretFiles(string root)
{
    private const string IpsetDummy = "203.0.113.113/32";

    public string Root { get; } = root;
    public string Bin => Path.Combine(Root, "bin");
    public string Lists => Path.Combine(Root, "lists");
    public string Utils => Path.Combine(Root, "utils");
    public string WinwsExe => Path.Combine(Bin, "winws.exe");
    public string ServiceBat => Path.Combine(Root, "service.bat");

    private string GameFilterFile => Path.Combine(Utils, "game_filter.enabled");
    private string IpsetFile => Path.Combine(Lists, "ipset-all.txt");
    private string IpsetBackup => Path.Combine(Lists, "ipset-all.txt.backup");

    public bool Exists => SafeFile.Exists(WinwsExe);

    // ---------- версия ----------

    /// <summary>set "LOCAL_VERSION=1.10.3" во второй строке service.bat.</summary>
    public string? ReadVersion()
    {
        // Версия — только для показа: недоступный service.bat не ошибка.
        try
        {
            foreach (var line in (SafeFile.ReadLines(ServiceBat) ?? []).Take(20))
            {
                var m = LocalVersion().Match(line);
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (ZapretFileException)
        {
        }
        return null;
    }

    // ---------- стратегии ----------

    public IReadOnlyList<string> Strategies()
    {
        var names = SafeFile.List(Root, "*.bat")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(n => !n.StartsWith("service", StringComparison.OrdinalIgnoreCase));
        return BatStrategyParser.NaturalSort(names).ToList();
    }

    public string StrategyPath(string name) => Path.Combine(Root, name + ".bat");

    public IReadOnlyList<string> BuildArgs(string strategy)
    {
        var gf = ReadGameFilter();
        return BatStrategyParser.ParseFile(StrategyPath(strategy),
            new BatStrategyParser.Variables(Root.TrimEnd('\\'), gf.EffectiveTcp, gf.EffectiveUdp));
    }

    // ---------- пользовательские списки (load_user_lists) ----------

    public static readonly IReadOnlyDictionary<string, string> UserListDefaults = new Dictionary<string, string>
    {
        ["list-general-user.txt"] = "# Never leave this file empty\r\ndomain.example.abc\r\n",
        ["list-exclude-user.txt"] = "domain.example.abc\r\n",
        ["ipset-exclude-user.txt"] = IpsetDummy + "\r\n",
    };

    public void EnsureUserLists()
    {
        foreach (var (name, content) in UserListDefaults)
        {
            var path = Path.Combine(Lists, name);
            if (!SafeFile.Exists(path)) SafeFile.WriteText(path, content);
        }
    }

    public string ReadUserList(string name)
    {
        // Нет файла — значения по умолчанию; файл есть, но не читается — ошибка, а не пустой список
        // (иначе следующая запись затёрла бы содержимое).
        return SafeFile.ReadText(Path.Combine(Lists, name)) ?? UserListDefaults[name];
    }

    /// <summary>Сохраняет список с CRLF. Пустым файл оставлять нельзя — winws перестанет его понимать.</summary>
    public void WriteUserList(string name, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var content = lines.Count == 0 ? UserListDefaults[name] : string.Join("\r\n", lines) + "\r\n";
        SafeFile.WriteText(Path.Combine(Lists, name), content);
    }

    // ---------- Game Filter (utils\game_filter.enabled) ----------

    public GameFilter ReadGameFilter()
    {
        var mode = GameFilterMode.Disabled;
        string? tcp = null, udp = null;
        if (SafeFile.ReadLines(GameFilterFile) is { } fileLines)
        {
            foreach (var raw in fileLines)
            {
                var parts = raw.Split('=', 2);
                var key = parts[0].Trim().ToLowerInvariant();
                var value = parts.Length > 1 ? parts[1].Trim() : "";
                switch (key)
                {
                    case "mode": mode = ParseMode(value); break;
                    case "all": mode = GameFilterMode.All; break;
                    case "tcp" when value.Length == 0: mode = GameFilterMode.Tcp; break;
                    case "udp" when value.Length == 0: mode = GameFilterMode.Udp; break;
                    case "tcp": tcp = value; break;
                    case "udp": udp = value; break;
                }
            }
        }
        return new GameFilter(mode,
            ValidateRange(tcp) ?? GameFilter.DefaultRange,
            ValidateRange(udp) ?? GameFilter.DefaultRange);
    }

    public void WriteGameFilter(GameFilter filter)
    {
        SafeFile.WriteText(GameFilterFile,
            $"mode={filter.Mode.ToString().ToLowerInvariant()}\r\ntcp={filter.TcpRange}\r\nudp={filter.UdpRange}\r\n");
    }

    private static GameFilterMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "all" => GameFilterMode.All,
        "tcp" => GameFilterMode.Tcp,
        "udp" => GameFilterMode.Udp,
        _ => GameFilterMode.Disabled,
    };

    /// <summary>
    /// «1024-1934,1936-65535»: числа без ведущих нулей, не больше 65535, начало не больше конца.
    /// null — строка неверна (как :validate_game_filter_range в service.bat).
    /// </summary>
    public static string? ValidateRange(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Replace(" ", "");
        foreach (var item in text.Split(','))
        {
            var m = RangeItem().Match(item);
            if (!m.Success) return null;
            var from = long.Parse(m.Groups[1].Value);
            var to = m.Groups[2].Success ? long.Parse(m.Groups[2].Value) : from;
            if (from > 65535 || to > 65535 || from > to) return null;
        }
        return text;
    }

    // ---------- IPSet (lists\ipset-all.txt) ----------

    public IpsetMode ReadIpsetMode()
    {
        var lines = SafeFile.ReadLines(IpsetFile);
        if (lines is null || lines.Length == 0) return IpsetMode.Any;
        return lines.Any(l => l.Contains(IpsetDummy, StringComparison.Ordinal)) ? IpsetMode.None : IpsetMode.Loaded;
    }

    public bool HasIpsetBackup => SafeFile.Exists(IpsetBackup);

    /// <summary>Переключение режима с теми же файлами, что у service.bat (загруженный список хранится в .backup).</summary>
    public void SetIpsetMode(IpsetMode target)
    {
        var current = ReadIpsetMode();
        if (current == target) return;

        if (target == IpsetMode.Loaded && !HasIpsetBackup)
            throw new InvalidOperationException("Загруженного списка нет — сначала нажмите «Обновить список IPSet».");
        if (current == IpsetMode.Loaded) SafeFile.Move(IpsetFile, IpsetBackup);

        switch (target)
        {
            case IpsetMode.None:
                SafeFile.WriteText(IpsetFile, IpsetDummy + "\r\n");
                break;
            case IpsetMode.Any:
                SafeFile.WriteText(IpsetFile, "");
                break;
            case IpsetMode.Loaded:
                SafeFile.Move(IpsetBackup, IpsetFile);
                break;
        }
    }

    public const string IpsetUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";

    /// <summary>Скачивает свежий список IPSet (пункт «Update IPSet List») — режим становится «загруженный».</summary>
    public async Task UpdateIpsetAsync(HttpClient http, CancellationToken ct)
    {
        var text = await http.GetStringAsync(IpsetUrl, ct);
        if (text.Split('\n').Count(l => l.Trim().Length > 0) < 10)
            throw new InvalidDataException("Скачанный список IPSet подозрительно короткий — не применён.");
        ct.ThrowIfCancellationRequested();
        SafeFile.WriteText(IpsetFile, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    // ---------- активные фейки (bin\ACTIVE_*.bin) ----------

    public static readonly string[] ActiveFakes = ["ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin"];

    /// <summary>Файлы, которые можно подставить вместо активного фейка.</summary>
    public IReadOnlyList<string> FakeCandidates() =>
        SafeFile.List(Bin, "*.bin").Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase)).Order().ToList();

    /// <summary>Какой файл сейчас скопирован в активный фейк (по sha256), или null.</summary>
    public string? CurrentFake(string activeName)
    {
        var active = Path.Combine(Bin, activeName);
        if (!SafeFile.Exists(active)) return null;
        var hash = Hash(active);
        return FakeCandidates().FirstOrDefault(c => Hash(Path.Combine(Bin, c)) == hash);
    }

    public void SetFake(string activeName, string candidate) =>
        SafeFile.Copy(Path.Combine(Bin, candidate), Path.Combine(Bin, activeName));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(SafeFile.ReadBytes(path)));

    [GeneratedRegex(@"LOCAL_VERSION=([0-9][0-9A-Za-z.\-]*)")]
    private static partial Regex LocalVersion();

    [GeneratedRegex(@"^([1-9][0-9]*)(?:-([1-9][0-9]*))?$")]
    private static partial Regex RangeItem();
}
