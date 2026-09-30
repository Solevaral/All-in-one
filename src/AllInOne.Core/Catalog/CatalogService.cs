using System.Text.Json;
using AllInOne.Sdk;

namespace AllInOne.Core.Catalog;

/// <summary>
/// Гибридный каталог. Встроенный (ресурс exe) работает офлайн и при первом запуске,
/// удалённый (репо All-in-one-modules) добавляет модули без выпуска новой версии каркаса.
/// Версий в каталоге нет — только где искать модуль, поэтому рассинхрона версий не бывает.
/// </summary>
public sealed class CatalogService(HttpClient http, string builtinJson, Version hostVersion)
{
    public const string DefaultRemoteUrl =
        "https://raw.githubusercontent.com/Solevaral/All-in-one-modules/main/catalog.json";

    public IReadOnlyList<CatalogItem> Items { get; private set; } = [];

    /// <summary>Откуда взят удалённый каталог при последнем обновлении.</summary>
    public string RemoteState { get; private set; } = "не загружен";

    public event EventHandler? Changed;

    /// <summary>Собирает каталог из встроенного и кэша удалённого — без сети.</summary>
    public void LoadOffline()
    {
        var builtin = Parse(builtinJson) ?? new CatalogFile();
        var cached = JsonFile.Read<CatalogFile>(AppPaths.CatalogCacheFile);
        Items = Merge(builtin, cached, hostVersion);
        RemoteState = cached is null ? "не загружен, используется встроенный" : "из кэша";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(string? remoteUrl, CancellationToken ct)
    {
        var builtin = Parse(builtinJson) ?? new CatalogFile();
        CatalogFile? remote = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var url = string.IsNullOrWhiteSpace(remoteUrl) ? DefaultRemoteUrl : remoteUrl;
            var json = await http.GetStringAsync(url + (url.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks, timeout.Token);
            remote = Parse(json);
            if (remote is not null)
            {
                JsonFile.TryWrite(AppPaths.CatalogCacheFile, remote);
                RemoteState = "обновлён " + DateTime.Now.ToString("HH:mm");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warn($"Удалённый каталог недоступен: {ex.Message}");
        }

        if (remote is null)
        {
            remote = JsonFile.Read<CatalogFile>(AppPaths.CatalogCacheFile);
            RemoteState = remote is null ? "недоступен, используется встроенный" : "недоступен, используется кэш";
        }

        Items = Merge(builtin, remote, hostVersion);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public CatalogItem? Find(string id) => Items.FirstOrDefault(i => string.Equals(i.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    internal static CatalogFile? Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Json.Options);
            if (file is null) return null;
            if (file.Schema > CatalogFile.CurrentSchema)
            {
                // Формат новее каркаса: читаем то, что понимаем, остальное игнорируется десериализатором.
                Log.Warn($"Каталог схемы {file.Schema}, каркас понимает {CatalogFile.CurrentSchema}");
            }
            return file;
        }
        catch (JsonException ex)
        {
            Log.Warn($"Каталог повреждён: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Слияние по id: удалённая запись перекрывает встроенную. Записи другой схемы
    /// манифеста пропускаются, записи с minHostVersion новее каркаса помечаются.
    /// </summary>
    internal static IReadOnlyList<CatalogItem> Merge(CatalogFile builtin, CatalogFile? remote, Version hostVersion)
    {
        var result = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Add(IEnumerable<ModuleManifest> modules, CatalogOrigin origin)
        {
            foreach (var m in modules)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || m.Schema != ModuleManifest.CurrentSchema) continue;
                var requiresHost = m.MinHostVersion is { } min && SemVer.Compare(min, hostVersion.ToString(3)) > 0;
                if (!result.ContainsKey(m.Id)) order.Add(m.Id);
                result[m.Id] = new CatalogItem(m, origin, requiresHost);
            }
        }

        Add(builtin.Modules, CatalogOrigin.Builtin);
        if (remote is not null) Add(remote.Modules, CatalogOrigin.Remote);

        return order.Select(id => result[id]).ToList();
    }
}

public sealed class CatalogFile
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public List<ModuleManifest> Modules { get; set; } = [];
}

public enum CatalogOrigin
{
    Builtin,
    Remote,
}

/// <param name="Manifest">Описание модуля (без версии).</param>
/// <param name="Origin">Откуда запись.</param>
/// <param name="RequiresHostUpdate">Модулю нужна более новая версия каркаса — установка заблокирована.</param>
public sealed record CatalogItem(ModuleManifest Manifest, CatalogOrigin Origin, bool RequiresHostUpdate);
