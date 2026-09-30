using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AllInOne.Core.GitHub;

/// <summary>
/// Последний релиз репозитория через api.github.com с ETag-кэшем (ответ 304 не тратит лимит
/// в 60 запросов в час без токена) и скачивание ассетов с проверкой sha256 из поля digest.
/// </summary>
public sealed class GitHubReleasesClient
{
    private readonly HttpClient _http;
    private readonly Lock _cacheGate = new();
    private Dictionary<string, CacheEntry> _cache;

    public GitHubReleasesClient(HttpClient http)
    {
        _http = http;
        _cache = JsonFile.Read<Dictionary<string, CacheEntry>>(AppPaths.GitHubCacheFile) ?? [];
    }

    /// <summary>
    /// Последняя ошибка обращения к GitHub, даже если ответ удалось взять из кэша.
    /// Сбрасывается при первом успешном запросе.
    /// </summary>
    public GitHubException? LastError { get; private set; }

    public async Task<GitHubRelease?> GetLatestAsync(string repo, bool includePrerelease, CancellationToken ct)
    {
        if (!includePrerelease)
        {
            return await GetJsonAsync<GitHubRelease>($"https://api.github.com/repos/{repo}/releases/latest", ct);
        }

        var list = await GetJsonAsync<List<GitHubRelease>>($"https://api.github.com/repos/{repo}/releases?per_page=10", ct);
        return list?.Where(r => !r.Draft).OrderByDescending(r => r.PublishedAt).FirstOrDefault();
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        CacheEntry? cached;
        lock (_cacheGate) _cache.TryGetValue(url, out cached);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        string body;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                body = cached.Body;
            }
            else if (response.IsSuccessStatusCode)
            {
                body = await response.Content.ReadAsStringAsync(ct);
                var entry = new CacheEntry { ETag = response.Headers.ETag?.Tag, Body = body };
                lock (_cacheGate)
                {
                    _cache[url] = entry;
                    JsonFile.TryWrite(AppPaths.GitHubCacheFile, _cache);
                }
            }
            else
            {
                var error = FromStatus(response.StatusCode, url);
                if (cached is null || error.Kind == GitHubErrorKind.NotFound) throw Remember(error);
                // Лимит запросов или сбой GitHub — работаем с последним известным ответом, но ошибку помним.
                Remember(error);
                Log.Warn($"GitHub {url}: {(int)response.StatusCode}, используется кэш");
                body = cached.Body;
                return JsonSerializer.Deserialize<T>(body, GitHubJson.Options);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var error = Remember(Network(ex, "api.github.com"));
            if (cached is null) throw error;
            Log.Warn($"GitHub {url} недоступен ({ex.Message}), используется кэш");
            return JsonSerializer.Deserialize<T>(cached.Body, GitHubJson.Options);
        }

        LastError = null;
        return JsonSerializer.Deserialize<T>(body, GitHubJson.Options);
    }

    /// <summary>Скачивает ассет в файл и сверяет размер и sha256 (если GitHub его отдал).</summary>
    public async Task DownloadAsync(GitHubAsset asset, string targetFile, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            await DownloadCoreAsync(asset, targetFile, progress, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw Remember(Network(ex, "github.com", $"Не удалось скачать {asset.Name}"));
        }
    }

    private async Task DownloadCoreAsync(GitHubAsset asset, string targetFile, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        var part = targetFile + ".part";

        using (var response = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw Remember(response.StatusCode == HttpStatusCode.NotFound
                    ? new GitHubException(GitHubErrorKind.AssetMissing, $"Файл {asset.Name} удалён из релиза на GitHub.")
                    : FromStatus(response.StatusCode, asset.BrowserDownloadUrl));
            var total = response.Content.Headers.ContentLength ?? asset.Size;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(part);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var length = new FileInfo(part).Length;
        if (asset.Size > 0 && length != asset.Size)
        {
            File.Delete(part);
            throw new GitHubException(GitHubErrorKind.Corrupted, $"Файл {asset.Name} скачался не полностью: {length} байт вместо {asset.Size}.");
        }

        if (asset.Sha256 is { } expected)
        {
            string actual;
            await using (var stream = File.OpenRead(part))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            }
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new GitHubException(GitHubErrorKind.Corrupted, $"Контрольная сумма {asset.Name} не совпала: файл повреждён или подменён.");
            }
        }

        File.Move(part, targetFile, overwrite: true);
    }

    /// <summary>
    /// Выбирает ассет по шаблону (* — любая подстрока). Если по шаблону подходит несколько,
    /// берётся первый по порядку в релизе.
    /// </summary>
    public static GitHubAsset? FindAsset(GitHubRelease release, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
        return release.Assets.FirstOrDefault(a => regex.IsMatch(a.Name));
    }

    private GitHubException Remember(GitHubException error)
    {
        LastError = error;
        return error;
    }

    private static GitHubException Network(Exception ex, string host, string? what = null)
    {
        var reason = ex is TaskCanceledException ? "нет ответа" : (ex.InnerException?.Message ?? ex.Message);
        var prefix = what is null ? $"Нет связи с {host}" : $"{what}: нет связи с {host}";
        return new GitHubException(GitHubErrorKind.Network,
            $"{prefix} ({reason}). Нет интернета, либо GitHub недоступен или заблокирован.");
    }

    private static GitHubException FromStatus(HttpStatusCode status, string url) => status switch
    {
        HttpStatusCode.NotFound => new GitHubException(GitHubErrorKind.NotFound,
            $"Не найдено на GitHub: {RepoOf(url)}. Репозиторий удалён, переименован или стал приватным."),
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new GitHubException(GitHubErrorKind.RateLimit,
            "GitHub ограничил число запросов (60 в час без входа). Повторите позже."),
        HttpStatusCode.UnavailableForLegalReasons => new GitHubException(GitHubErrorKind.Blocked,
            $"GitHub закрыл доступ к {RepoOf(url)}."),
        _ => new GitHubException(GitHubErrorKind.Server, $"GitHub ответил ошибкой {(int)status}."),
    };

    private static string RepoOf(string url)
    {
        const string marker = "/repos/";
        var i = url.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return url;
        var parts = url[(i + marker.Length)..].Split('/');
        return parts.Length >= 2 ? parts[0] + "/" + parts[1] : url;
    }

    private sealed class CacheEntry
    {
        public string? ETag { get; set; }
        public string Body { get; set; } = "";
    }
}

public enum GitHubErrorKind
{
    /// <summary>Нет связи: нет интернета, DNS, таймаут, блокировка.</summary>
    Network,

    /// <summary>Лимит запросов API.</summary>
    RateLimit,

    /// <summary>Репозиторий не найден (удалён, переименован, приватный).</summary>
    NotFound,

    /// <summary>Доступ закрыт (451).</summary>
    Blocked,

    /// <summary>Ошибка на стороне GitHub.</summary>
    Server,

    /// <summary>В репозитории нет релизов.</summary>
    NoReleases,

    /// <summary>В релизе нет подходящего файла.</summary>
    AssetMissing,

    /// <summary>Файл скачался повреждённым.</summary>
    Corrupted,
}

public sealed class GitHubException(GitHubErrorKind kind, string message) : Exception(message)
{
    public GitHubErrorKind Kind { get; } = kind;
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];

    [JsonIgnore] public string Version => SemVer.Normalize(TagName);
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";

    /// <summary>«sha256:&lt;hex&gt;» — GitHub заполняет для ассетов, загруженных после середины 2025 года.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }

    [JsonIgnore]
    public string? Sha256 => Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
}

internal static class GitHubJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true };
}
