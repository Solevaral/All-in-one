using System.Windows;
using System.Windows.Controls;
using AllInOne.Core;
using AllInOne.Core.Catalog;
using AllInOne.Ui;

namespace AllInOne.Host.UI.Pages;

internal sealed class SettingsPage : PageBase
{
    private bool? _autostart;

    public SettingsPage()
    {
        Refresh();
        _ = LoadAutostartAsync();
    }

    private async Task LoadAutostartAsync()
    {
        _autostart = await HostAutostart.IsEnabledAsync();
        Refresh();
    }

    public override void Refresh()
    {
        var s = Manager.Settings;
        Body.Children.Clear();
        Body.Children.Add(UiKit.PageTitle("Настройки"));

        var start = new StackPanel();
        start.Children.Add(UiKit.Section("Запуск"));
        if (_autostart is { } autostart)
        {
            start.Children.Add(UiKit.Toggle("Запускать All in One при входе в Windows", autostart, on => _ = SetAutostartAsync(on),
                "Задача Планировщика с наивысшими правами, без окна UAC. Модули запускаются по галочке «Запускать вместе с All in One»."));
        }
        else
        {
            start.Children.Add(UiKit.Hint("Проверка автозапуска…"));
        }
        start.Children.Add(UiKit.Toggle("Запускаться свёрнутым в трей", s.StartMinimized, on => { s.StartMinimized = on; s.Save(); }));
        start.Children.Add(UiKit.Toggle("Закрытие окна сворачивает в трей", s.CloseToTray, on => { s.CloseToTray = on; s.Save(); }));
        start.Children.Add(UiKit.Row("При выходе из All in One", UiKit.Combo(
            [(ExitBehavior.Ask, "спрашивать"), (ExitBehavior.StopModules, "останавливать модули"), (ExitBehavior.KeepRunning, "оставлять модули работать")],
            s.OnExit, v => { s.OnExit = v; s.Save(); })));
        Body.Children.Add(new Border { Style = UiKit.Style("CardBorder"), Child = start });

        var updates = new StackPanel();
        updates.Children.Add(UiKit.Section("Обновления"));
        updates.Children.Add(UiKit.Row("Проверять обновления", UiKit.Combo(
            [(1, "каждый час"), (3, "каждые 3 часа"), (6, "каждые 6 часов"), (12, "каждые 12 часов"), (24, "раз в сутки"), (0, "только вручную")],
            s.UpdateCheckHours, v => { s.UpdateCheckHours = v; s.Save(); })));
        updates.Children.Add(UiKit.Toggle("Проверять обновления All in One", s.CheckHostUpdates, on => { s.CheckHostUpdates = on; s.Save(); }));
        var url = new TextBox { Text = s.CatalogUrl ?? "", ToolTip = "Пусто — " + CatalogService.DefaultRemoteUrl };
        url.LostFocus += (_, _) => { s.CatalogUrl = string.IsNullOrWhiteSpace(url.Text) ? null : url.Text.Trim(); s.Save(); };
        updates.Children.Add(UiKit.Row("Адрес каталога на GitHub", url));
        updates.Children.Add(UiKit.Hint("Пусто — Solevaral/All-in-one-modules."));
        Body.Children.Add(new Border { Style = UiKit.Style("CardBorder"), Child = updates });

        var experimental = new StackPanel();
        experimental.Children.Add(UiKit.Section("Экспериментальные функции"));
        experimental.Children.Add(UiKit.Toggle("Включить экспериментальные функции", s.ExperimentalFeatures, on =>
        {
            s.ExperimentalFeatures = on;
            s.Save();
        }, "Перезапуск модуля после неожиданного завершения; окно TryToCatchMe внутри окна All in One."));
        Body.Children.Add(new Border { Style = UiKit.Style("CardBorder"), Child = experimental });

        var about = new StackPanel();
        about.Children.Add(UiKit.Section("О программе"));
        about.Children.Add(UiKit.Text($"All in One {RuntimeInfo.HostVersionText} ({(RuntimeInfo.Flavor == BuildFlavor.Standalone ? "standalone" : "net9")})"));
        about.Children.Add(UiKit.Mono(AppPaths.Root).With(t => t.Margin = new Thickness(0, 4, 0, 0)));
        foreach (var problem in AppPaths.CheckRootPath())
            about.Children.Add(new TextBlock { Text = problem, Foreground = UiKit.Brush("Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        about.Children.Add(UiKit.Buttons(
            UiKit.Button("Папка модулей", () => UiKit.OpenFolder(AppPaths.Modules)),
            UiKit.Button("Папка данных", () => UiKit.OpenFolder(AppPaths.Data)),
            UiKit.Button("Логи", () => UiKit.OpenFolder(AppPaths.Logs)),
            UiKit.Button("GitHub", () => UiKit.OpenUrl("https://github.com/Solevaral/All-in-one"))));
        Body.Children.Add(new Border { Style = UiKit.Style("CardBorder"), Child = about });
    }

    private async Task SetAutostartAsync(bool on)
    {
        await UiKit.RunAsync(() => HostAutostart.SetAsync(on), "Автозапуск не изменён");
        _autostart = await HostAutostart.IsEnabledAsync();
        Refresh();
    }
}
