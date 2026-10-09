using System.IO;
using System.Net.Http;
using System.Text.Json;
using AllInOne.Core;
using AllInOne.Sdk;

namespace AllInOne.Modules.Zapret;

/// <summary>Набор настроек zapret под игру: Game Filter, IPSet и строки в пользовательских списках.</summary>
public sealed class GameFix
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Короткая подпись: что пишет игра.</summary>
    public string? Note { get; set; }

    /// <summary>Что сделать самому, кроме того, что выставляет фикс (например, режим VPN-клиента).</summary>
    public string? Help { get; set; }

    /// <summary>all / tcp / udp — нужный режим Game Filter; null — не трогать.</summary>
    public string? GameFilter { get; set; }

    /// <summary>any / loaded — нужный режим IPSet; null — не трогать.</summary>
    public string? Ipset { get; set; }

    public List<string> ListGeneral { get; set; } = [];

    public List<string> ListExclude { get; set; } = [];

    public List<string> IpsetExclude { get; set; } = [];

    internal IEnumerable<(string File, string Line)> Lines =>
        ListGeneral.Select(l => (ZapretGames.ListGeneralFile, l))
            .Concat(ListExclude.Select(l => (ZapretGames.ListExcludeFile, l)))
            .Concat(IpsetExclude.Select(l => (ZapretGames.IpsetExcludeFile, l)));
}

public sealed class GameFixCatalog
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public List<GameFix> Games { get; set; } = [];
}

/// <summary>Что включено и что именно добавлено — чтобы снятие галочки убирало только своё.</summary>
public sealed class GameFixState
{
    public List<string> Enabled { get; set; } = [];

    /// <summary>Файл списка → строки, которые добавили фиксы (строки, бывшие в файле раньше, сюда не попадают).</summary>
    public Dictionary<string, List<string>> Added { get; set; } = [];

    /// <summary>Режимы до первого включённого фикса — возвращаются, когда фиксов не остаётся.</summary>
    public GameFilterMode? PreviousGameFilter { get; set; }

    public IpsetMode? PreviousIpset { get; set; }

    /// <summary>Режимы, выставленные фиксами: вручную изменённый режим при снятии не откатывается.</summary>
    public GameFilterMode? AppliedGameFilter { get; set; }

    public IpsetMode? AppliedIpset { get; set; }
}

/// <summary>
/// Фиксы для игр. Набор встроен в All in One и обновляется из репозитория каталога
/// (zapret-games.json в All-in-one-modules): новая игра добавляется без выпуска All in One.
/// </summary>
internal sealed class ZapretGames(string dataDir)
{
    public const string ListGeneralFile = "list-general-user.txt";
    public const string ListExcludeFile = "list-exclude-user.txt";
    public const string IpsetExcludeFile = "ipset-exclude-user.txt";

    public const string RemoteUrl = "https://raw.githubusercontent.com/Solevaral/All-in-one-modules/main/zapret-games.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private string CachePath => Path.Combine(dataDir, "games.cache.json");
    private string StatePath => Path.Combine(dataDir, "games.json");

    public GameFixState State { get; private set; } = new();

    public void LoadState() => State = JsonFile.Read<GameFixState>(StatePath) ?? new GameFixState();

    private void SaveState() => JsonFile.TryWrite(StatePath, State);

    /// <summary>
    /// Скачанный с GitHub набор целиком (так игру можно и убрать из набора), без него — встроенный.
    /// </summary>
    public IReadOnlyList<GameFix> Games
    {
        get
        {
            try
            {
                if (File.Exists(CachePath) && Parse(File.ReadAllText(CachePath)) is { Games.Count: > 0 } cached)
                    return cached.Games;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return Parse(ReadBuiltin())?.Games ?? [];
        }
    }

    /// <summary>
    /// Включённые фиксы игр, которых больше нет в наборе: выключаются, их строки убираются из списков,
    /// режимы возвращаются. true — что-то изменилось.
    /// </summary>
    public bool Prune(ZapretFiles files)
    {
        var known = Games.Select(g => g.Id).ToHashSet();
        var gone = State.Enabled.Where(id => !known.Contains(id)).ToList();
        if (gone.Count == 0) return false;
        State.Enabled.RemoveAll(gone.Contains);
        try
        {
            Apply(files);
        }
        finally
        {
            SaveState();
        }
        Log.Info($"zapret: фиксы {string.Join(", ", gone)} убраны из набора игр и выключены");
        return true;
    }

    /// <summary>Справка для кружка «?»: подпись, что выставляет фикс и что сделать самому.</summary>
    public static string Describe(GameFix game)
    {
        var parts = new List<string>();
        if (game.Note is { Length: > 0 } note) parts.Add(note);

        var sets = new List<string>();
        if (game.GameFilter is { } gf)
            sets.Add("Game Filter — " + (gf switch { "all" => "TCP и UDP", "tcp" => "только TCP", _ => "только UDP" }));
        if (game.Ipset is { } ipset)
            sets.Add("IPSet — " + (ipset == "any" ? "«Все адреса»" : "«По списку»"));
        if (game.ListGeneral.Count > 0) sets.Add("в обход: " + string.Join(", ", game.ListGeneral));
        if (game.ListExclude.Count > 0) sets.Add("исключения доменов: " + string.Join(", ", game.ListExclude));
        if (game.IpsetExclude.Count > 0) sets.Add("исключения IP: " + string.Join(", ", game.IpsetExclude));
        if (sets.Count > 0) parts.Add("Фикс выставляет:\n• " + string.Join("\n• ", sets));

        if (game.Help is { Length: > 0 } help) parts.Add(help);
        parts.Add("После включения — перезапуск zapret.");
        return string.Join("\n\n", parts);
    }

    internal static GameFixCatalog? Parse(string json)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<GameFixCatalog>(json, Json.Options);
            if (catalog is null || catalog.Schema != GameFixCatalog.CurrentSchema) return null;
            catalog.Games = catalog.Games.Where(Valid).ToList();
            return catalog;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Строка списка — домен или адрес без пробелов; режимы — из известных значений.</summary>
    private static bool Valid(GameFix g) =>
        g.Id.Length > 0 && g.Name.Length > 0
        && g.GameFilter is null or "all" or "tcp" or "udp"
        && g.Ipset is null or "any" or "loaded"
        && g.Lines.All(l => l.Line.Length is > 0 and < 256 && !l.Line.Any(char.IsWhiteSpace))
        && (g.Help?.Length ?? 0) < 2000 && (g.Note?.Length ?? 0) < 500;

    private static string ReadBuiltin()
    {
        using var stream = typeof(ZapretGames).Assembly.GetManifestResourceStream("AllInOne.Modules.Zapret.zapret-games.json");
        if (stream is null) return "{}";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Обновляет набор с GitHub, если сохранённой копии больше 6 часов. Ошибка — остаётся прежний набор.</summary>
    public async Task<bool> RefreshAsync(HttpClient http, bool force, CancellationToken ct)
    {
        if (!force && File.Exists(CachePath) && DateTime.Now - File.GetLastWriteTime(CachePath) < MaxAge) return false;
        try
        {
            var json = await http.GetStringAsync(RemoteUrl, ct);
            if (Parse(json) is not { Games.Count: > 0 }) return false;
            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(CachePath, json, ct);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warn($"zapret: набор фиксов для игр не обновлён — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Включает или выключает фикс и приводит файлы zapret к набору включённых фиксов:
    /// недостающие строки дописываются, строки снятых фиксов (и только добавленные ими) удаляются,
    /// Game Filter и IPSet — самый широкий режим из нужных, без фиксов — прежний.
    /// </summary>
    public void Set(ZapretFiles files, string id, bool enabled)
    {
        if (enabled && !State.Enabled.Contains(id)) State.Enabled.Add(id);
        if (!enabled) State.Enabled.Remove(id);
        try
        {
            Apply(files);
        }
        finally
        {
            // Часть файлов могла записаться до ошибки — сохраняем то, что уже применено.
            SaveState();
        }
    }

    /// <summary>Что сейчас в файлах zapret — для проверки фиксов, прописанных вручную.</summary>
    public sealed record Snapshot(Dictionary<string, HashSet<string>> Lines, GameFilterMode GameFilter, IpsetMode Ipset);

    public static Snapshot Read(ZapretFiles files)
    {
        var lines = new Dictionary<string, HashSet<string>>();
        foreach (var file in new[] { ListGeneralFile, ListExcludeFile, IpsetExcludeFile })
        {
            lines[file] = files.ReadUserList(file).Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return new Snapshot(lines, files.ReadGameFilter().Mode, files.ReadIpsetMode());
    }

    /// <summary>Всё, что нужно фиксу, уже есть в файлах (прописано вручную или другим фиксом).</summary>
    public static bool IsSatisfied(Snapshot now, GameFix game)
    {
        if (!game.Lines.Any() && game.GameFilter is null && game.Ipset is null) return false;
        if (!game.Lines.All(l => now.Lines[l.File].Contains(l.Line))) return false;
        var gameFilterOk = game.GameFilter switch
        {
            null => true,
            "all" => now.GameFilter == GameFilterMode.All,
            "tcp" => now.GameFilter is GameFilterMode.All or GameFilterMode.Tcp,
            _ => now.GameFilter is GameFilterMode.All or GameFilterMode.Udp,
        };
        var ipsetOk = game.Ipset switch
        {
            null => true,
            "any" => now.Ipset == IpsetMode.Any,
            _ => now.Ipset is IpsetMode.Any or IpsetMode.Loaded,
        };
        return gameFilterOk && ipsetOk;
    }

    /// <summary>Фикс включён галочкой или прописан вручную.</summary>
    public bool IsOn(Snapshot now, GameFix game) => State.Enabled.Contains(game.Id) || IsSatisfied(now, game);

    public bool IsManual(Snapshot now, GameFix game) => !State.Enabled.Contains(game.Id) && IsSatisfied(now, game);

    /// <summary>
    /// Снять фикс, прописанный вручную: его строки удаляются (кроме нужных включённым фиксам),
    /// Game Filter и IPSet возвращаются в «выключен», если их не требуют другие фиксы.
    /// </summary>
    public void RemoveManual(ZapretFiles files, GameFix game)
    {
        // Как будто фикс с самого начала был включён галочкой и всё это добавил он.
        foreach (var (file, line) in game.Lines)
        {
            if (!State.Added.TryGetValue(file, out var owned)) State.Added[file] = owned = [];
            if (!owned.Contains(line, StringComparer.OrdinalIgnoreCase)) owned.Add(line);
        }
        if (game.GameFilter is not null)
        {
            State.PreviousGameFilter ??= GameFilterMode.Disabled;
            State.AppliedGameFilter = files.ReadGameFilter().Mode;
        }
        if (game.Ipset is not null)
        {
            State.PreviousIpset ??= IpsetMode.None;
            State.AppliedIpset = files.ReadIpsetMode();
        }
        if (!State.Enabled.Contains(game.Id)) State.Enabled.Add(game.Id);
        Set(files, game.Id, false);
    }

    private void Apply(ZapretFiles files)
    {
        files.EnsureUserLists();
        var games = Games.Where(g => State.Enabled.Contains(g.Id)).ToList();
        ApplyLines(files, games);
        ApplyGameFilter(files, games);
        ApplyIpset(files, games);
    }

    private void ApplyLines(ZapretFiles files, List<GameFix> games)
    {
        foreach (var file in new[] { ListGeneralFile, ListExcludeFile, IpsetExcludeFile })
        {
            var required = games.SelectMany(g => g.Lines).Where(l => l.File == file).Select(l => l.Line)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var owned = State.Added.TryGetValue(file, out var o) ? o : [];
            var lines = files.ReadUserList(file).Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            // Убираем свои строки, которые больше не нужны ни одному фиксу.
            var drop = owned.Where(l => !required.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
            lines.RemoveAll(l => drop.Contains(l, StringComparer.OrdinalIgnoreCase));
            owned.RemoveAll(l => drop.Contains(l, StringComparer.OrdinalIgnoreCase));

            // Дописываем недостающие; бывшие в файле раньше строки своими не считаются.
            foreach (var line in required.Where(r => !lines.Contains(r, StringComparer.OrdinalIgnoreCase)))
            {
                lines.Add(line);
                owned.Add(line);
            }

            files.WriteUserList(file, string.Join("\r\n", lines));
            State.Added[file] = owned;
        }
    }

    private void ApplyGameFilter(ZapretFiles files, List<GameFix> games)
    {
        var tcp = games.Any(g => g.GameFilter is "all" or "tcp");
        var udp = games.Any(g => g.GameFilter is "all" or "udp");
        var current = files.ReadGameFilter();

        if (!tcp && !udp)
        {
            // Фиксов с Game Filter не осталось: возвращаем прежний режим, если его не меняли вручную.
            if (State.PreviousGameFilter is { } previous && current.Mode == State.AppliedGameFilter)
                files.WriteGameFilter(current with { Mode = previous });
            State.PreviousGameFilter = null;
            State.AppliedGameFilter = null;
            return;
        }

        State.PreviousGameFilter ??= current.Mode;
        tcp |= current.Mode is GameFilterMode.All or GameFilterMode.Tcp;
        udp |= current.Mode is GameFilterMode.All or GameFilterMode.Udp;
        var mode = tcp && udp ? GameFilterMode.All : tcp ? GameFilterMode.Tcp : GameFilterMode.Udp;
        if (mode != current.Mode) files.WriteGameFilter(current with { Mode = mode });
        State.AppliedGameFilter = mode;
    }

    private void ApplyIpset(ZapretFiles files, List<GameFix> games)
    {
        IpsetMode? required = games.Any(g => g.Ipset == "any") ? IpsetMode.Any
            : games.Any(g => g.Ipset == "loaded") ? IpsetMode.Loaded
            : null;
        var current = files.ReadIpsetMode();

        if (required is null)
        {
            if (State.PreviousIpset is { } previous && current == State.AppliedIpset)
            {
                try { files.SetIpsetMode(previous); }
                catch (InvalidOperationException) { }   // прежнего загруженного списка уже нет — остаётся текущий режим
            }
            State.PreviousIpset = null;
            State.AppliedIpset = null;
            return;
        }

        State.PreviousIpset ??= current;
        // «Все адреса» шире «По списку»: если уже стоит «Все адреса», не сужаем.
        var target = current == IpsetMode.Any ? IpsetMode.Any : required.Value;
        if (target != current) files.SetIpsetMode(target);
        State.AppliedIpset = target;
    }
}
