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
            using var response = await _http.SendAsync(request, ct);
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
            else if (cached is not null)
            {
                // Лимит запросов или сбой GitHub — работаем с последним известным ответом.
                Log.Warn($"GitHub {url}: {(int)response.StatusCode}, используется кэш");
                body = cached.Body;
            }
            else
            {
                throw new GitHubException($"GitHub ответил {(int)response.StatusCode} {response.ReasonPhrase}" +
                    (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                        ? " — превышен лимит запросов, попробуйте позже."
                        : ""));
            }
        }
        catch (HttpRequestException ex) when (cached is not null)
        {
            Log.Warn($"GitHub {url} недоступен ({ex.Message}), используется кэш");
            body = cached.Body;
        }

        return JsonSerializer.Deserialize<T>(body, GitHubJson.Options);
    }

    /// <summary>Скачивает ассет в файл и сверяет размер и sha256 (если GitHub его отдал).</summary>
    public async Task DownloadAsync(GitHubAsset asset, string targetFile, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        var part = targetFile + ".part";

        using (var response = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
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
            throw new GitHubException($"Размер {asset.Name} не совпал: {length} вместо {asset.Size}.");
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
                throw new GitHubException($"Контрольная сумма {asset.Name} не совпала — файл повреждён или подменён.");
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

    private sealed class CacheEntry
    {
        public string? ETag { get; set; }
        public string Body { get; set; } = "";
    }
}

public sealed class GitHubException(string message) : Exception(message);

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
