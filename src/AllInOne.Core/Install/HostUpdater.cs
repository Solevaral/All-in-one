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
        using var process = Process.Start(new ProcessStartInfo(setup,
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /DIR=\"{AppPaths.Root.TrimEnd('\\')}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(setup)!,
        }) ?? throw new InvalidOperationException("Установщик не запустился.");
        StartWatchdog(process.Id);
    }

    /// <summary>
    /// Страховка: установщик упал или не запустил новую версию — через 30 с после его завершения
    /// поднимается AllInOne.exe (новый или прежний, какой есть). Сторож — скрытый PowerShell,
    /// запущенный от All in One и потому с правами администратора: exe стартует без окна UAC.
    /// </summary>
    private static void StartWatchdog(int setupPid)
    {
        var exe = AppPaths.HostExe.Replace("'", "''");
        var script =
            $"Wait-Process -Id {setupPid} -Timeout 900 -ErrorAction SilentlyContinue; " +
            "Start-Sleep -Seconds 30; " +
            $"$exe = '{exe}'; " +
            "$running = Get-Process -Name AllInOne -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }; " +
            "if (-not $running -and (Test-Path -LiteralPath $exe)) { Start-Process -FilePath $exe -ArgumentList '--post-update' }";
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", script })
                psi.ArgumentList.Add(arg);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Сторож обновления не запустился: " + ex.Message);
        }
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
