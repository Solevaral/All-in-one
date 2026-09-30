using System.Diagnostics;
using System.IO.Compression;
using AllInOne.Core.GitHub;

namespace AllInOne.Core.Install;

/// <summary>
/// Самообновление каркаса. Работающий exe нельзя перезаписать, но можно переименовать:
/// AllInOne.exe → AllInOne.old.exe, на его место кладётся новый, он запускается с --post-update
/// и удаляет .old. Модули при этом не останавливаются — новый каркас подключится к ним по IPC.
/// </summary>
public sealed class HostUpdater(GitHubReleasesClient github)
{
    public const string Repo = "Solevaral/All-in-one";

    public string AssetPattern => RuntimeInfo.Flavor == BuildFlavor.Standalone
        ? "AllInOne-*-win-x64-standalone.zip"
        : "AllInOne-*-win-x64-net9.zip";

    public async Task<ReleaseChoice?> CheckAsync(CancellationToken ct)
    {
        var release = await github.GetLatestAsync(Repo, includePrerelease: false, ct);
        if (release is null || !SemVer.IsNewer(release.Version, RuntimeInfo.HostVersionText)) return null;
        var asset = GitHubReleasesClient.FindAsset(release, AssetPattern);
        return asset is null ? null : new ReleaseChoice(release, asset);
    }

    /// <summary>Скачивает и подменяет exe. После успешного вызова каркас должен запустить новый exe и выйти.</summary>
    public async Task<string> DownloadAndSwapAsync(ReleaseChoice choice, IProgress<double>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(AppPaths.Staging, "_host", choice.Version);
        ModuleInstaller.TryDeleteDirectory(staging);
        var zip = Path.Combine(staging, choice.Asset.Name);
        await github.DownloadAsync(choice.Asset, zip, progress, ct);

        var extracted = Path.Combine(staging, "files");
        ZipFile.ExtractToDirectory(zip, extracted);
        var newExe = Directory.EnumerateFiles(extracted, "AllInOne.exe", SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new InvalidDataException("В архиве обновления нет AllInOne.exe.");
        ModuleInstaller.Unblock(extracted);

        var current = AppPaths.HostExe;
        var old = Path.ChangeExtension(current, ".old.exe");
        if (File.Exists(old)) File.Delete(old);
        File.Move(current, old);
        try
        {
            File.Move(newExe, current);
        }
        catch
        {
            File.Move(old, current);
            throw;
        }

        ModuleInstaller.TryDeleteDirectory(staging);
        Log.Info($"Каркас обновлён до {choice.Version}, перезапуск");
        return current;
    }

    public static void LaunchUpdated(string exe) =>
        Process.Start(new ProcessStartInfo(exe, "--post-update") { UseShellExecute = true, WorkingDirectory = AppPaths.Root });

    /// <summary>Вызывается новым exe: убирает старый (он мог ещё не завершиться — пробуем несколько раз).</summary>
    public static async Task CleanupAfterUpdateAsync()
    {
        var old = Path.ChangeExtension(AppPaths.HostExe, ".old.exe");
        for (var i = 0; i < 20 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }
}
