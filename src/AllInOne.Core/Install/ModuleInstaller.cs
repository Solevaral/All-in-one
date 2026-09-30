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
/// скачать в data\staging → проверить → бережно остановить → подготовить замену (хуки модуля) →
/// перенести папку программы в data\backup → положить новую, вернуть preserve → запустить и проверить →
/// откат при сбое. До переноса в backup установленная версия не тронута, поэтому ошибка раньше безопасна.
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
                      ?? throw new GitHubException(GitHubErrorKind.NoReleases, $"В {source.Repo} нет релизов.");

        var asset = PickAsset(release, source)
                    ?? throw new GitHubException(GitHubErrorKind.AssetMissing,
                        $"В релизе {release.TagName} репозитория {source.Repo} нет файла для этой системы ({source.Asset}).");
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
        if (manifest.Validate() is { } invalid) throw new InvalidDataException("Запись каталога: " + invalid + ".");

        // Встроенный модуль без программы: «установка» — это запись манифеста.
        if (manifest.Source is null || manifest.Source.Type == "none")
        {
            manifest.Version = RuntimeInfo.HostVersion.ToString(3);
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
            var staged = Path.Combine(stagingRoot, "program");
            Unpack(download, staged, manifest.Source);

            manifest.Version = choice.Version;
            manifest.InstalledAsset = choice.Asset.Name;
            await ApplyAsync(module, ctx, manifest, staged, progress, ct);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// Ручная установка из zip: в архиве module.json и папка program (или файлы программы в корне рядом с module.json).
    /// </summary>
    public static ModuleManifest ReadManifestFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("module.json", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("В архиве нет module.json в корне.");
        using var reader = new StreamReader(entry.Open());
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(reader.ReadToEnd(), Json.Options)
                       ?? throw new InvalidDataException("module.json пустой.");
        if (manifest.Schema != ModuleManifest.CurrentSchema)
            throw new InvalidDataException($"module.json схемы {manifest.Schema}, каркас понимает {ModuleManifest.CurrentSchema}.");
        if (manifest.Validate() is { } invalid)
            throw new InvalidDataException("module.json: " + invalid + ".");
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
            var program = Directory.Exists(Path.Combine(extracted, "program")) ? Path.Combine(extracted, "program") : extracted;
            File.Delete(Path.Combine(extracted, "module.json"));
            manifest.Version ??= "0.0.0";
            manifest.InstalledAsset = Path.GetFileName(zipPath);
            await ApplyAsync(module, ctx, manifest, program, progress, ct);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task ApplyAsync(ModuleBase module, ModuleContext ctx, ModuleManifest newManifest, string staged,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var isUpdate = ctx.IsInstalled;
        var oldManifest = isUpdate ? ctx.Manifest.Clone() : null;
        var wasRunning = module.Status.IsActive;
        var hooks = module as IModuleInstallHooks;
        var programDir = ctx.ProgramDir;
        var backupDir = Path.Combine(AppPaths.Backup, newManifest.Id);
        var backupProgram = Path.Combine(backupDir, "program");

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
                progress?.Report(new InstallProgress("Подготовка к замене файлов…"));
                saved = await hooks.BeforeReplaceAsync(isUpdate, ct);
            }

            await EnsureFilesFreeAsync(programDir, ct);

            // Бэкап: переносим текущую папку программы целиком (на одном томе это быстро и атомарно).
            progress?.Report(new InstallProgress("Замена файлов…"));
            TryDeleteDirectory(backupDir);
            Directory.CreateDirectory(backupDir);
            if (Directory.Exists(programDir)) Directory.Move(programDir, backupProgram);
            if (File.Exists(ctx.ManifestPath)) File.Copy(ctx.ManifestPath, Path.Combine(backupDir, "module.json"), overwrite: true);

            // Папка программы могла смениться (другое имя в новом манифесте) — кладём по новому имени.
            ctx.Manifest = newManifest;
            programDir = ctx.ProgramDir;
            Directory.CreateDirectory(AppPaths.Modules);
            Directory.Move(staged, programDir);
            replaced = true;

            // Переносим то, что должно пережить обновление (пути в preserve — внутри папки программы).
            foreach (var rel in Glob.Match(backupProgram, newManifest.Preserve))
            {
                var target = Path.Combine(programDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(backupProgram, rel), target, overwrite: true);
            }

            JsonFile.Write(ctx.ManifestPath, newManifest);

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
            progress?.Report(new InstallProgress("Ошибка, откат на прежнюю версию…"));
            await RollbackAsync(module, ctx, oldManifest, backupProgram, wasRunning, saved);
            throw new InvalidOperationException($"Обновление не удалось, возвращена версия {oldManifest.Version}. {ex.Message}", ex);
        }
        catch
        {
            if (!replaced && oldManifest is not null)
            {
                ctx.Manifest = oldManifest;
                if (Directory.Exists(backupProgram) && !Directory.Exists(ctx.ProgramDir))
                    Directory.Move(backupProgram, ctx.ProgramDir);   // упали между бэкапом и заменой — возвращаем как было
            }
            module.OnInstallationChanged();
            throw;
        }
    }

    private static async Task RollbackAsync(ModuleBase module, ModuleContext ctx, ModuleManifest oldManifest, string backupProgram,
        bool wasRunning, IDictionary<string, string> saved)
    {
        try
        {
            try { await module.StopAsync(StopReason.Update, CancellationToken.None); }
            catch (Exception ex) { Log.Warn($"Откат: остановка новой версии — {ex.Message}"); }

            TryDeleteDirectory(ctx.ProgramDir);
            ctx.Manifest = oldManifest;
            if (Directory.Exists(backupProgram)) Directory.Move(backupProgram, ctx.ProgramDir);
            JsonFile.Write(ctx.ManifestPath, oldManifest);
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
        await EnsureFilesFreeAsync(ctx.ProgramDir, ct);

        TryDeleteDirectory(ctx.ProgramDir);
        if (removeData) TryDeleteDirectory(AppPaths.ModuleDataDir(ctx.Manifest.Id));
        else File.Delete(ctx.ManifestPath);
        TryDeleteDirectory(Path.Combine(AppPaths.Backup, ctx.Manifest.Id));
        module.OnInstallationChanged();
        Log.Info($"{ctx.Manifest.Id}: удалён");
    }

    private static async Task EnsureFilesFreeAsync(string programDir, CancellationToken ct)
    {
        if (!Directory.Exists(programDir)) return;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var busy = ProcessUtil.FindUnder(programDir);
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

    /// <summary>Раскладывает скачанный ассет в папку программы.</summary>
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
