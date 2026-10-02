using AllInOne.Core.Install;
using AllInOne.Core.Modules;
using AllInOne.Ui;

namespace AllInOne.Host.UI;

/// <summary>Действия над модулем из интерфейса: подтверждения, прогресс, ошибки.</summary>
internal static class ModuleOps
{
    private static ModuleManager Manager => App.Current.Manager;

    public static Task StartAsync(ModuleEntry entry) =>
        UiKit.RunAsync(() => Manager.StartAsync(entry), $"«{entry.Name}» не запустился");

    public static Task StopAsync(ModuleEntry entry) =>
        UiKit.RunAsync(() => Manager.StopAsync(entry), $"«{entry.Name}» не остановился");

    public static Task RestartAsync(ModuleEntry entry) =>
        UiKit.RunAsync(() => Manager.RestartAsync(entry), $"«{entry.Name}» не перезапустился");

    public static Task InstallAsync(ModuleEntry entry) =>
        WithProgressAsync(entry, p => Manager.InstallAsync(entry, p), $"«{entry.Name}» не установился");

    /// <summary>
    /// GitHub ограничил запросы — предложить «другой способ» (без API). Возвращает true, если пользователь согласился
    /// и способ включён: тогда операцию стоит повторить.
    /// </summary>
    public static async Task<bool> OfferWebFallbackAsync(string what)
    {
        if (Manager.GitHub.UseWeb) return false;
        var go = await Dialog.ConfirmAsync("Лимит запросов GitHub",
            $"{what}: GitHub ограничил число запросов (60 в час без входа на один IP; при VPN или общем IP провайдера лимит делят все).\n\n" +
            "Другой способ: версия берётся со страницы релизов, файл — по прямой ссылке, без лимита. " +
            "Контрольной суммы от GitHub в этом случае нет, проверяется только размер, загрузка идёт по HTTPS. " +
            "Способ действует до перезапуска All in One.",
            "Скачать другим способом", "Отмена");
        if (go)
        {
            Manager.GitHub.UseWeb = true;
            AllInOne.Core.Log.Info("GitHub: включён другой способ скачивания (без API)");
        }
        return go;
    }

    private static bool IsRateLimit(Exception ex) =>
        ex is AllInOne.Core.GitHub.GitHubException { Kind: AllInOne.Core.GitHub.GitHubErrorKind.RateLimit }
        || ex.InnerException is AllInOne.Core.GitHub.GitHubException { Kind: AllInOne.Core.GitHub.GitHubErrorKind.RateLimit };

    public static async Task UpdateAsync(ModuleEntry entry)
    {
        if (entry.Module.Status.IsActive)
        {
            var go = await Dialog.ConfirmAsync($"Обновить «{entry.Name}»?",
                "Модуль будет остановлен, обновлён и запущен снова" +
                (entry.Context.Manifest.Category == "Сеть" ? ", соединение прервётся." : "."),
                "Обновить", "Отмена");
            if (!go) return;
        }
        await WithProgressAsync(entry, p => Manager.UpdateAsync(entry, p), $"«{entry.Name}» не обновился");
    }

    public static async Task UninstallAsync(ModuleEntry entry)
    {
        var choice = await Dialog.ShowAsync($"Удалить «{entry.Name}»?",
            "Модуль будет остановлен, папка программы удалена.",
            "Удалить, сохранить настройки", "Удалить вместе с настройками", "Отмена");
        if (choice is < 0 or 2) return;
        await UiKit.RunAsync(() => Manager.UninstallAsync(entry, removeData: choice == 1), $"«{entry.Name}» не удалился");
    }

    public static async Task WithProgressAsync(ModuleEntry entry, Func<IProgress<InstallProgress>, Task> action, string errorTitle)
    {
        var app = App.Current;
        var progress = new Progress<InstallProgress>(p => app.ReportProgress(entry.Id, p));
        app.ReportProgress(entry.Id, new InstallProgress("Подготовка…"));
        try
        {
            try
            {
                await action(progress);
            }
            catch (Exception ex) when (IsRateLimit(ex))
            {
                app.ReportProgress(entry.Id, null);
                if (!await OfferWebFallbackAsync(errorTitle)) return;
                app.ReportProgress(entry.Id, new InstallProgress("Подготовка…"));
                await UiKit.RunAsync(() => action(progress), errorTitle);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await UiKit.RunAsync(() => Task.FromException(ex), errorTitle);
            }
        }
        finally
        {
            app.ReportProgress(entry.Id, null);
        }
    }
}
