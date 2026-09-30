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
            await UiKit.RunAsync(() => action(progress), errorTitle);
        }
        finally
        {
            app.ReportProgress(entry.Id, null);
        }
    }
}
