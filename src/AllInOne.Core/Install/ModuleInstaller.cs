using System.IO.Compression;
using AllInOne.Core.GitHub;
using AllInOne.Core.Modules;
using AllInOne.Core.Processes;
using AllInOne.Sdk;

namespace AllInOne.Core.Install;

public sealed record InstallProgress(string Stage, double? Fraction = null);

/// <summary>Что лежит в релизе и что из этого подходит этой машине.</summary>
public sealed record ReleaseChoice(GitHubRelease Release, GitHubAsset Asset)
{
    public string Version => Release.Version;
}

/// <summary>
/// Установка, обновление и удаление модулей. Порядок обновления:
/// скачать в staging → проверить → бережно остановить → освободить файлы (хуки) →
/// бэкап payload → замена с переносом preserve → запуск и проверка → откат при сбое.
/// До шага «бэкап» установленная версия не тронута, поэтому любая ошибка раньше безопасна.
/// </summary>
public sealed class ModuleInstaller(GitHubReleasesClient github)
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Последний релиз и подходящий ассет. null — у модуля нет источника (встроенный).</summary>
    public async Task<ReleaseChoice?> ResolveAsync(ModuleManifest manifest, CancellationToken ct)
    {
        var source = manifest.Source;
        if (source is null || source.Type == "none" || source.Repo is null) return null;
        if (source.Type != "github") throw new NotSupportedException($"Источник «{source.Type}» не поддерживается.");

        var release = await github.GetLatestAsync(source.Repo, source.Prerelease, ct)
                      ?? throw new GitHubException($"В {source.Repo} нет релизов.");

        var asset = PickAsset(release, source)
                    ?? throw new GitHubException($"В релизе {release.TagName} ({source.Repo}) нет подходящего файла для этой системы.");
        return new ReleaseChoice(release, asset);
    }

    internal static GitHubAsset? PickAsset(GitHubRelease release, ModuleSource source)
    {
        if (RuntimeInfo.IsArm64 && GitHubReleasesClient.FindAsset(release, source.AssetArm64) is { } arm) return arm;
        if (source.AssetNet9 is not null && RuntimeInfo.HasDesktopRuntime(9) &&
            GitHubReleasesClient.FindAsset(release, source.AssetNet9) is { } light) return light;
        return GitHubReleasesClient.FindAsset(release, source.Asset);
    }

    /// <summary>Устанавливает модуль или обновляет его до последней версии.</summary>
    public async Task InstallFromCatalogAsync(ModuleBase module, ModuleContext ctx, ModuleManifest catalogManifest,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var manifest = catalogManifest.Clone();

        // Встроенный модуль без payload: «установка» — это просто запись манифеста.
        if (manifest.Source is null || manifest.Source.Type == "none")
        {
            manifest.Version = RuntimeInfo.HostVersion.ToString(3);
            Directory.CreateDirectory(ctx.ModuleDir);
            JsonFile.Write(ctx.ManifestPath, manifest);
            ctx.Manifest = manifest;
            module.OnInstallationChanged();
            return;
        }

        progress?.Report(new InstallProgress("Поиск последней версии…"));
        var choice = await ResolveAsync(manifest, ct) ?? throw new InvalidOperationException("Не найден релиз.");

        var stagingRoot = Path.Combine(AppPaths.Staging, manifest.Id, choice.Version);
        TryDeleteDirectory(stagingRoot);
        try
        {
            var download = Path.Combine(stagingRoot, choice.Asset.Name);
            progress?.Report(new InstallProgress($"Скачивание {choice.Asset.Name}…", 0));
            await github.DownloadAsync(choice.Asset, download,
                new Progress<double>(f => progress?.Report(new InstallProgress($"Скачивание {choice.Asset.Name}…", f))), ct);

            progress?.Report(new InstallProgress("Распаковка…"));
            var stagedPayload = Path.Combine(stagingRoot, "payload");
            Unpack(download, stagedPayload, manifest.Source);

            manifest.Version = choice.Version;
            manifest.InstalledAsset = choice.Asset.Name;
            await ApplyAsync(module, ctx, manifest, stagedPayload, progress, ct);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// Ручная установка из zip: в архиве module.json и папка payload (или файлы payload в корне рядом с module.json).
    /// </summary>
    public static ModuleManifest ReadManifestFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("module.json", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("В архиве нет module.json в корне.");
        using var reader = new StreamReader(entry.Open());
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(reader.ReadToEnd(), Json.Options)
                       ?? throw new InvalidDataException("module.json пустой.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("В module.json не задан корректный id.");
        if (manifest.Schema > ModuleManifest.CurrentSchema)
            throw new InvalidDataException("Модуль сделан для более новой версии каркаса.");
        return manifest;
    }

    public async Task InstallFromZipAsync(ModuleBase module, ModuleContext ctx, string zipPath,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var manifest = ReadManifestFromZip(zipPath);
        var stagingRoot = Path.Combine(AppPaths.Staging, manifest.Id, "manual");
        TryDeleteDirectory(stagingRoot);
        try
        {
            progress?.Report(new InstallProgress("Распаковка…"));
            var extracted = Path.Combine(stagingRoot, "zip");
            ZipFile.ExtractToDirectory(zipPath, extracted);
            var payload = Directory.Exists(Path.Combine(extracted, "payload")) ? Path.Combine(extracted, "payload") : extracted;
            File.Delete(Path.Combine(extracted, "module.json"));
            manifest.Version ??= "0.0.0";
            manifest.InstalledAsset = Path.GetFileName(zipPath);
            await ApplyAsync(module, ctx, manifest, payload, progress, ct);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task ApplyAsync(ModuleBase module, ModuleContext ctx, ModuleManifest newManifest, string stagedPayload,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var isUpdate = ctx.IsInstalled;
        var oldManifest = isUpdate ? ctx.Manifest.Clone() : null;
        var wasRunning = module.Status.IsActive;
        var hooks = module as IModuleInstallHooks;
        var payloadDir = ctx.PayloadDir;
        var backupDir = Path.Combine(AppPaths.Backup, newManifest.Id);
        var backupPayload = Path.Combine(backupDir, "payload");

        if (isUpdate)
        {
            progress?.Report(new InstallProgress($"Остановка «{ctx.Manifest.Name}»…"));
            await module.StopAsync(StopReason.Update, ct);   // StopCancelledException — пользователь отказался, ничего не тронуто
        }

        module.MarkUpdating(isUpdate ? "Обновление…" : "Установка…");
        IDictionary<string, string> saved = new Dictionary<string, string>();
        var replaced = false;
        try
        {
            if (hooks is not null)
            {
                progress?.Report(new InstallProgress("Освобождение файлов…"));
                saved = await hooks.BeforeReplaceAsync(isUpdate, ct);
            }

            await EnsureFilesFreeAsync(payloadDir, ct);

            // Бэкап: переносим текущий payload целиком (на одном томе это быстро и атомарно).
            progress?.Report(new InstallProgress("Замена файлов…"));
            TryDeleteDirectory(backupDir);
            Directory.CreateDirectory(backupDir);
            if (Directory.Exists(payloadDir)) Directory.Move(payloadDir, backupPayload);
            if (File.Exists(ctx.ManifestPath)) File.Copy(ctx.ManifestPath, Path.Combine(backupDir, "module.json"), overwrite: true);

            Directory.CreateDirectory(ctx.ModuleDir);
            Directory.Move(stagedPayload, payloadDir);
            replaced = true;

            // Переносим то, что должно пережить обновление (пути в preserve — относительно папки модуля).
            var payloadPatterns = newManifest.Preserve
                .Select(p => p.Replace('\\', '/'))
                .Where(p => p.StartsWith("payload/", StringComparison.OrdinalIgnoreCase))
                .Select(p => p["payload/".Length..])
                .ToList();
            foreach (var rel in Glob.Match(backupPayload, payloadPatterns))
            {
                var target = Path.Combine(payloadDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(backupPayload, rel), target, overwrite: true);
            }

            JsonFile.Write(ctx.ManifestPath, newManifest);
            ctx.Manifest = newManifest;

            if (hooks is not null) await hooks.AfterReplaceAsync(isUpdate, saved, ct);
            module.OnInstallationChanged();

            if (wasRunning)
            {
                progress?.Report(new InstallProgress("Запуск новой версии…"));
                await module.StartAsync(ct);
                if (!await WaitHealthyAsync(module, ct))
                    throw new InvalidOperationException($"Новая версия не запустилась: {module.Status.Detail ?? module.Status.Summary}");
            }

            Log.Info($"{newManifest.Id}: {(isUpdate ? "обновлён" : "установлен")} {oldManifest?.Version} → {newManifest.Version}");
        }
        catch (Exception ex) when (replaced && oldManifest is not null && ex is not OperationCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            Log.Error($"{newManifest.Id}: ошибка после замены файлов, откат на {oldManifest.Version}", ex);
            progress?.Report(new InstallProgress("Ошибка — откат на прежнюю версию…"));
            await RollbackAsync(module, ctx, oldManifest, backupPayload, wasRunning, saved);
            throw new InvalidOperationException($"Обновление не удалось, возвращена версия {oldManifest.Version}. {ex.Message}", ex);
        }
        catch
        {
            if (!replaced && isUpdate && Directory.Exists(backupPayload) && !Directory.Exists(payloadDir))
            {
                // Упали между бэкапом и заменой — возвращаем как было.
                Directory.Move(backupPayload, payloadDir);
            }
            module.OnInstallationChanged();
            throw;
        }
    }

    private static async Task RollbackAsync(ModuleBase module, ModuleContext ctx, ModuleManifest oldManifest, string backupPayload,
        bool wasRunning, IDictionary<string, string> saved)
    {
        try
        {
            try { await module.StopAsync(StopReason.Update, CancellationToken.None); }
            catch (Exception ex) { Log.Warn($"Откат: остановка новой версии — {ex.Message}"); }

            TryDeleteDirectory(ctx.PayloadDir);
            if (Directory.Exists(backupPayload)) Directory.Move(backupPayload, ctx.PayloadDir);
            JsonFile.Write(ctx.ManifestPath, oldManifest);
            ctx.Manifest = oldManifest;
            if (module is IModuleInstallHooks hooks) await hooks.AfterReplaceAsync(true, saved, CancellationToken.None);
            module.OnInstallationChanged();
            if (wasRunning) await module.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("Откат не удался", ex);
        }
    }

    public async Task UninstallAsync(ModuleBase module, ModuleContext ctx, bool removeData, CancellationToken ct)
    {
        if (!ctx.IsInstalled) return;
        await module.StopAsync(StopReason.Uninstall, ct);
        if (module is IModuleInstallHooks hooks) await hooks.BeforeReplaceAsync(false, ct);
        await EnsureFilesFreeAsync(ctx.PayloadDir, ct);

        TryDeleteDirectory(ctx.PayloadDir);
        File.Delete(ctx.ManifestPath);
        if (removeData) TryDeleteDirectory(ctx.ModuleDir);
        TryDeleteDirectory(Path.Combine(AppPaths.Backup, ctx.Manifest.Id));
        module.OnInstallationChanged();
        Log.Info($"{ctx.Manifest.Id}: удалён");
    }

    private static async Task EnsureFilesFreeAsync(string payloadDir, CancellationToken ct)
    {
        if (!Directory.Exists(payloadDir)) return;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var busy = ProcessUtil.FindUnder(payloadDir);
            var names = busy.Select(p => p.ProcessName + ".exe").Distinct().ToList();
            foreach (var p in busy) p.Dispose();
            if (names.Count == 0) return;
            if (DateTime.UtcNow > deadline)
                throw new IOException($"Файлы модуля заняты процессами: {string.Join(", ", names)}.");
            await Task.Delay(300, ct);
        }
    }

    private static async Task<bool> WaitHealthyAsync(ModuleBase module, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HealthTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (module.Status.State == ModuleState.Running) return true;
            if (module.Status.State == ModuleState.Error) return false;
            await Task.Delay(500, ct);
            await module.RefreshAsync(ct);
        }
        return module.Status.State == ModuleState.Running;
    }

    /// <summary>Раскладывает скачанный ассет в папку payload.</summary>
    internal static void Unpack(string downloaded, string targetDir, ModuleSource source)
    {
        Directory.CreateDirectory(targetDir);
        var layout = source.Layout ?? (downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "file");

        if (layout == "zip")
        {
            var tmp = targetDir + ".unzip";
            TryDeleteDirectory(tmp);
            ZipFile.ExtractToDirectory(downloaded, tmp);

            // Архив с одной верхней папкой (zapret-discord-youtube-1.10.3/…) — отрезаем её.
            var root = tmp;
            var entries = Directory.GetFileSystemEntries(root);
            if (entries.Length == 1 && Directory.Exists(entries[0])) root = entries[0];

            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                var dest = Path.Combine(targetDir, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, dest);
                else File.Move(entry, dest, overwrite: true);
            }
            TryDeleteDirectory(tmp);
        }
        else
        {
            var name = source.SaveAs ?? Path.GetFileName(downloaded);
            File.Copy(downloaded, Path.Combine(targetDir, name), overwrite: true);
        }

        Unblock(targetDir);
    }

    /// <summary>Снимает пометку «скачано из интернета» (Zone.Identifier), как «Разблокировать» в свойствах файла.</summary>
    internal static void Unblock(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file + ":Zone.Identifier"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
    }

    internal static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось удалить {dir}: {ex.Message}");
        }
    }
}
