using System.Windows;
using System.Windows.Controls;
using AllInOne.Core;
using AllInOne.Core.Install;
using AllInOne.Core.Modules;
using AllInOne.Ui;

namespace AllInOne.Host.UI.Pages;

/// <summary>Обновления модулей и самого All in One.</summary>
internal sealed class UpdatesPage : PageBase
{
    private static InstallProgress? _hostProgress;
    private bool _checking;

    public UpdatesPage() => Refresh();

    public override void Refresh()
    {
        Body.Children.Clear();
        Body.Children.Add(UiKit.PageTitle("Обновления"));
        Body.Children.Add(UiKit.Hint(Manager.Settings.LastUpdateCheck is { } t
            ? "Последняя проверка: " + t.ToString("dd.MM HH:mm")
            : "Проверки не было"));

        var checking = _checking || Manager.IsCheckingUpdates;
        var pending = Manager.Installed.Where(e => e.AvailableUpdate is not null).ToList();
        Body.Children.Add(UiKit.Buttons(
            UiKit.Button(checking ? "Проверка…" : "Проверить", () => _ = CheckAsync()).With(b => b.IsEnabled = !checking),
            UiKit.AccentButton($"Обновить всё ({pending.Count})", () => _ = UpdateAllAsync(pending)).With(b => b.IsEnabled = pending.Count > 0 && !checking)));

        // Недоступность источника — отдельной плашкой: версии ниже могут быть устаревшими.
        if (Manager.GitHub.LastError is { } sourceError)
        {
            Body.Children.Add(new Border
            {
                Style = UiKit.Style("CardBorder"),
                Margin = new Thickness(0, 14, 0, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Источник обновлений недоступен", Foreground = UiKit.Brush("Warn"), FontWeight = FontWeights.SemiBold },
                        UiKit.Hint(sourceError.Message),
                        sourceError.Kind == AllInOne.Core.GitHub.GitHubErrorKind.RateLimit && !Manager.GitHub.UseWeb
                            ? UiKit.Buttons(UiKit.AccentButton("Проверить другим способом", () => _ = CheckViaWebAsync()))
                            : new StackPanel(),
                    },
                },
            });
        }
        foreach (var error in Manager.LastCheckErrors.Where(e => Manager.GitHub.LastError is null || !e.Contains(Manager.GitHub.LastError.Message)))
            Body.Children.Add(new TextBlock { Text = error, Foreground = UiKit.Brush("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        // ---- All in One ----
        Body.Children.Add(UiKit.Section("All in One").With(s => s.Margin = new Thickness(2, 22, 0, 10)));
        var host = new StackPanel();
        host.Children.Add(UiKit.CardTitle($"All in One {RuntimeInfo.HostVersionText}"));
        host.Children.Add(UiKit.Hint(RuntimeInfo.Flavor == BuildFlavor.Standalone ? "Сборка standalone" : "Сборка net9"));
        if (_hostProgress is { } hp)
        {
            host.Children.Add(ProgressView(hp));
        }
        else if (Manager.HostUpdate is { } hu)
        {
            host.Children.Add(UiKit.Text($"Доступна версия {hu.Version}."));
            host.Children.Add(UiKit.Buttons(
                UiKit.AccentButton($"Обновить до {hu.Version}", () => _ = UpdateHostAsync(hu)),
                UiKit.Link("Изменения", () => UiKit.OpenUrl(hu.Release.HtmlUrl ?? "https://github.com/" + HostUpdater.Repo + "/releases"))));
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
            ? $"Установлена {current}, доступна {u}" + (entry.UserState.PendingUpdate is not null ? ", установится при следующем запуске All in One" : "")
            : $"Установлена {current}, последняя"));
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
        }
        finally
        {
            _checking = false;
            Refresh();
        }
    }

    /// <summary>Лимит API — проверка без него (версия со страницы релизов), после согласия пользователя.</summary>
    private async Task CheckViaWebAsync()
    {
        if (await ModuleOps.OfferWebFallbackAsync("Проверка обновлений")) await CheckAsync();
    }

    private static async Task UpdateAllAsync(List<ModuleEntry> entries)
    {
        var running = entries.Where(e => e.Module.Status.IsActive).Select(e => e.Name).ToList();
        if (running.Count > 0)
        {
            var go = await Dialog.ConfirmAsync("Обновить всё?",
                $"Запущенные модули ({string.Join(", ", running)}) будут остановлены, обновлены и запущены снова.", "Обновить");
            if (!go) return;
        }
        foreach (var entry in entries)
        {
            await ModuleOps.WithProgressAsync(entry, p => Manager.UpdateAsync(entry, p), $"«{entry.Name}» не обновился");
        }
    }

    /// <summary>
    /// Скачивает сетап, запускает тихую установку и закрывает All in One, не останавливая модули.
    /// Сетап запускает новую версию, она подключается к работающим модулям.
    /// </summary>
    private async Task UpdateHostAsync(ReleaseChoice choice)
    {
        var go = await Dialog.ConfirmAsync($"Обновить All in One до {choice.Version}?",
            "All in One закроется и запустится после установки. Модули продолжат работать.", "Обновить");
        if (!go) return;

        _hostProgress = new InstallProgress("Скачивание…", 0);
        Refresh();
        await UiKit.RunAsync(async () =>
        {
            try
            {
                var setup = await new HostUpdater(Manager.GitHub).DownloadAsync(choice,
                    new Progress<double>(f => { _hostProgress = new InstallProgress("Скачивание…", f); Refresh(); }), CancellationToken.None);
                HostUpdater.LaunchSetup(setup);
                await App.Current.ShutdownHostAsync();
            }
            finally
            {
                _hostProgress = null;
                Refresh();
            }
        }, "All in One не обновился");
    }
}
