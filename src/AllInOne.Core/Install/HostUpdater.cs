using System.Diagnostics;
using AllInOne.Core.GitHub;

namespace AllInOne.Core.Install;

/// <summary>
/// Самообновление All in One через установщик. Скачивается сетап того же варианта (net9 или standalone),
/// запускается в тихом режиме с /UPDATE, а All in One завершается, не останавливая модули.
/// Сетап заменяет AllInOne.exe и запускает его с --post-update; новый экземпляр подключается
/// к работающим модулям по IPC. Папки modules и data сетап не трогает.
/// </summary>
public sealed class HostUpdater(GitHubReleasesClient github)
{
    public const string Repo = "Solevaral/All-in-one";

    public static string AssetPattern => RuntimeInfo.Flavor == BuildFlavor.Standalone
        ? "AllInOne-*-setup-standalone.exe"
        : "AllInOne-*-setup-net9.exe";

    public async Task<ReleaseChoice?> CheckAsync(CancellationToken ct)
    {
        var release = await github.GetLatestAsync(Repo, includePrerelease: false, ct);
        if (release is null || !SemVer.IsNewer(release.Version, RuntimeInfo.HostVersionText)) return null;
        var asset = GitHubReleasesClient.FindAsset(release, AssetPattern);
        return asset is null ? null : new ReleaseChoice(release, asset);
    }

    /// <summary>Скачивает сетап. Вернувшийся путь передаётся в <see cref="LaunchSetup"/>.</summary>
    public async Task<string> DownloadAsync(ReleaseChoice choice, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.Staging, "_host");
        ModuleInstaller.TryDeleteDirectory(dir);
        var setup = Path.Combine(dir, choice.Asset.Name);
        await github.DownloadAsync(choice.Asset, setup, progress, ct);
        return setup;
    }

    /// <summary>
    /// Запускает тихую установку поверх текущей. После вызова All in One должен сразу завершиться
    /// (без остановки модулей): сетап ждёт закрытия AllInOne.exe и запускает новую версию.
    /// </summary>
    public static void LaunchSetup(string setup)
    {
        Log.Info($"Запуск обновления: {setup}");
        Process.Start(new ProcessStartInfo(setup,
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /DIR=\"{AppPaths.Root.TrimEnd('\\')}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(setup)!,
        });
    }

    /// <summary>Вызывается новым экземпляром после обновления: убирает скачанный сетап.</summary>
    public static async Task CleanupAfterUpdateAsync()
    {
        var dir = Path.Combine(AppPaths.Staging, "_host");
        for (var i = 0; i < 20 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
        }
    }
}
