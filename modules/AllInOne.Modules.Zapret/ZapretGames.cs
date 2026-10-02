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

    /// <summary>Откуда набор и проверен ли он.</summary>
    public string? Note { get; set; }

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

    /// <summary>Встроенный набор, поверх — скачанный (записи с тем же id заменяются).</summary>
    public IReadOnlyList<GameFix> Games
    {
        get
        {
            var list = Parse(ReadBuiltin())?.Games ?? [];
            if (File.Exists(CachePath) && Parse(File.ReadAllText(CachePath)) is { } cached)
            {
                foreach (var game in cached.Games)
                {
                    var i = list.FindIndex(g => g.Id == game.Id);
                    if (i >= 0) list[i] = game;
                    else list.Add(game);
                }
            }
            return list;
        }
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
        && g.Lines.All(l => l.Line.Length is > 0 and < 256 && !l.Line.Any(char.IsWhiteSpace));

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
        Apply(files);
        SaveState();
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

            State.Added[file] = owned;
            files.WriteUserList(file, string.Join("\r\n", lines));
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
