using System.Windows;
using System.Windows.Controls;
using AllInOne.Core;
using AllInOne.Core.Install;
using AllInOne.Core.Modules;
using AllInOne.Ui;

namespace AllInOne.Host.UI.Pages;

/// <summary>Обновления модулей и самого каркаса.</summary>
internal sealed class UpdatesPage : PageBase
{
    private static ReleaseChoice? _hostUpdate;
    private static InstallProgress? _hostProgress;
    private bool _checking;

    public UpdatesPage() => Refresh();

    public override void Refresh()
    {
        Body.Children.Clear();
        Body.Children.Add(UiKit.PageTitle("Обновления"));
        var last = Manager.Settings.LastUpdateCheck is { } t ? "Последняя проверка: " + t.ToString("dd.MM HH:mm") : "Ещё не проверялось";
        Body.Children.Add(UiKit.Hint(last + ". Перед обновлением модуль бережно останавливается, а после — запускается снова; если новая версия не заработает, вернётся прежняя."));

        var checking = _checking || Manager.IsCheckingUpdates;
        var pending = Manager.Installed.Where(e => e.AvailableUpdate is not null).ToList();
        Body.Children.Add(UiKit.Buttons(
            UiKit.Button(checking ? "Проверка…" : "Проверить сейчас", () => _ = CheckAsync()).With(b => b.IsEnabled = !checking),
            UiKit.AccentButton($"Обновить всё ({pending.Count})", () => _ = UpdateAllAsync(pending)).With(b => b.IsEnabled = pending.Count > 0 && !checking)));

        foreach (var error in Manager.LastCheckErrors)
            Body.Children.Add(new TextBlock { Text = error, Foreground = UiKit.Brush("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        // ---- каркас ----
        Body.Children.Add(UiKit.Section("Каркас").With(s => s.Margin = new Thickness(2, 22, 0, 10)));
        var host = new StackPanel();
        host.Children.Add(UiKit.CardTitle($"All-in-one {RuntimeInfo.HostVersionText}"));
        host.Children.Add(UiKit.Hint(RuntimeInfo.Flavor == BuildFlavor.Standalone ? "Сборка standalone (с .NET внутри)" : "Сборка net9 (использует установленный .NET 9)"));
        if (_hostProgress is { } hp)
        {
            host.Children.Add(ProgressView(hp));
        }
        else if (_hostUpdate is { } hu)
        {
            host.Children.Add(UiKit.Text($"Доступна версия {hu.Version}."));
            host.Children.Add(UiKit.Buttons(
                UiKit.AccentButton($"Обновить каркас до {hu.Version}", () => _ = UpdateHostAsync(hu)),
                UiKit.Link("Что нового", () => UiKit.OpenUrl(hu.Release.HtmlUrl ?? "https://github.com/" + HostUpdater.Repo + "/releases"))));
        }
        Body.Children.Add(new Border { Style = UiKit.Style("CardBorder"), Child = host });

        // ---- модули ----
        Body.Children.Add(UiKit.Section("Модули").With(s => s.Margin = new Thickness(2, 10, 0, 10)));
        var withReleases = Manager.Installed.Where(e => e.HasReleases).ToList();
        if (withReleases.Count == 0) Body.Children.Add(UiKit.Hint("Нет установленных модулей с релизами."));
        foreach (var entry in withReleases) Body.Children.Add(BuildRow(entry));
    }

    private static Border BuildRow(ModuleEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(UiKit.CardTitle(entry.Name));
        var current = entry.Context.Manifest.Version ?? "?";
        info.Children.Add(UiKit.Hint(entry.AvailableUpdate is { } u
            ? $"Установлена {current}, доступна {u}" + (entry.UserState.PendingUpdate is not null ? " — будет установлена при следующем запуске каркаса" : "")
            : $"Установлена {current} — последняя"));
        info.Children.Add(UiKit.Toggle("Обновлять автоматически", entry.UserState.AutoUpdate, on =>
        {
            entry.UserState.AutoUpdate = on;
            Manager.States.Save();
        }).With(t => t.HorizontalAlignment = HorizontalAlignment.Left));
        if (ProgressFor(entry) is { } progress) info.Children.Add(progress);
        grid.Children.Add(info);

        if (entry.AvailableUpdate is { } update)
        {
            var button = UiKit.AccentButton($"Обновить до {update}", () => _ = ModuleOps.UpdateAsync(entry)).With(b =>
            {
                b.IsEnabled = !entry.IsBusy;
                b.VerticalAlignment = VerticalAlignment.Top;
            });
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return new Border { Style = UiKit.Style("CardBorder"), Child = grid };
    }

    private async Task CheckAsync()
    {
        _checking = true;
        Refresh();
        try
        {
            await Manager.CheckUpdatesAsync(userInitiated: true);
            if (Manager.Settings.CheckHostUpdates)
            {
                try { _hostUpdate = await new HostUpdater(Manager.GitHub).CheckAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Проверка обновления каркаса: " + ex.Message); }
            }
        }
        finally
        {
            _checking = false;
            Refresh();
        }
    }

    private static async Task UpdateAllAsync(List<ModuleEntry> entries)
    {
        var running = entries.Where(e => e.Module.Status.IsActive).Select(e => e.Name).ToList();
        if (running.Count > 0)
        {
            var go = await Dialog.ConfirmAsync("Обновить всё?",
                $"Работающие модули ({string.Join(", ", running)}) будут по очереди бережно остановлены, обновлены и запущены снова.", "Обновить");
            if (!go) return;
        }
        foreach (var entry in entries)
        {
            await ModuleOps.WithProgressAsync(entry, p => Manager.UpdateAsync(entry, p), $"«{entry.Name}» не обновился");
        }
    }

    private async Task UpdateHostAsync(ReleaseChoice choice)
    {
        var go = await Dialog.ConfirmAsync($"Обновить All-in-one до {choice.Version}?",
            "Каркас перезапустится. Модули продолжат работать — новый каркас подключится к ним сам.", "Обновить");
        if (!go) return;

        _hostProgress = new InstallProgress("Скачивание…", 0);
        Refresh();
        await UiKit.RunAsync(async () =>
        {
            try
            {
                var exe = await new HostUpdater(Manager.GitHub).DownloadAndSwapAsync(choice,
                    new Progress<double>(f => { _hostProgress = new InstallProgress("Скачивание…", f); Refresh(); }), CancellationToken.None);
                HostUpdater.LaunchUpdated(exe);
                await App.Current.ShutdownHostAsync();
            }
            finally
            {
                _hostProgress = null;
                Refresh();
            }
        }, "Каркас не обновился");
    }
}
