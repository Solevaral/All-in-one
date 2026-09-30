using System.Windows;
using System.Windows.Controls;
using AllInOne.Core.Modules;
using AllInOne.Sdk;
using AllInOne.Ui;

namespace AllInOne.Host.UI.Pages;

/// <summary>
/// Страница установленного модуля: общая шапка (статус, запуск, автозапуск, обновление, удаление)
/// и ниже — собственная страница модуля или общая для внешних программ.
/// Шапка обновляется при каждом изменении статуса, страница модуля создаётся один раз.
/// </summary>
internal sealed class ModulePage : PageBase
{
    private readonly ModuleEntry _entry;
    private readonly ContentControl _header = new();
    private readonly ContentControl _generic = new();
    private readonly object? _view;

    public ModulePage(ModuleEntry entry)
    {
        _entry = entry;
        _view = entry.Module.CreateView();

        Body.Children.Add(_header);
        if (_view is UIElement native) Body.Children.Add(native);
        else Body.Children.Add(_generic);

        Refresh();
    }

    public override void Refresh()
    {
        _header.Content = BuildHeader();
        if (_view is null) _generic.Content = BuildGeneric();
    }

    public override void OnEntryChanged(ModuleEntry entry)
    {
        if (ReferenceEquals(entry, _entry)) Refresh();
    }

    private FrameworkElement BuildHeader()
    {
        var e = _entry;
        var m = e.Context.Manifest;
        var status = e.Module.Status;
        var panel = new StackPanel();

        panel.Children.Add(UiKit.PageTitle(m.Name));
        var meta = new List<string>();
        if (m.Version is { } v) meta.Add("версия " + v);
        if (m.Category is { } c) meta.Add(c);
        if (m.Author is { } a) meta.Add(a);
        panel.Children.Add(UiKit.Hint(string.Join("  ·  ", meta)));

        var card = new StackPanel();
        card.Children.Add(UiKit.StatusLine(status));
        if (status.Detail is { Length: > 0 } detail)
            card.Children.Add(UiKit.Hint(detail).With(t => t.Margin = new Thickness(16, 4, 0, 0)));
        if (e.LastError is { } error)
            card.Children.Add(new TextBlock { Text = error, Foreground = UiKit.Brush("Bad"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        if (ProgressFor(e) is { } progress)
        {
            card.Children.Add(progress);
        }
        else
        {
            var buttons = UiKit.Buttons();
            if (!m.IsBuiltIn)
            {
                if (status.IsActive)
                {
                    buttons.Children.Add(UiKit.Button("Остановить", () => _ = ModuleOps.StopAsync(e)));
                    buttons.Children.Add(UiKit.Button("Перезапустить", () => _ = ModuleOps.RestartAsync(e)));
                }
                else
                {
                    buttons.Children.Add(UiKit.AccentButton("Запустить", () => _ = ModuleOps.StartAsync(e)));
                }
            }
            if (e.AvailableUpdate is { } update)
                buttons.Children.Add(UiKit.AccentButton($"Обновить до {update}", () => _ = ModuleOps.UpdateAsync(e)));
            foreach (UIElement b in buttons.Children) b.IsEnabled = !e.IsBusy;
            card.Children.Add(buttons);
        }

        card.Children.Add(new Border { Height = 1, Background = UiKit.Brush("CardStroke"), Margin = new Thickness(0, 14, 0, 8) });

        var states = Manager.States;
        if (!m.IsBuiltIn)
        {
            card.Children.Add(UiKit.Toggle("Запускать вместе с каркасом", e.UserState.Autostart, on =>
            {
                e.UserState.Autostart = on;
                states.Save();
            }));
            card.Children.Add(UiKit.Toggle("Перезапускать, если модуль неожиданно завершился", e.UserState.RestartOnCrash, on =>
            {
                e.UserState.RestartOnCrash = on;
                states.Save();
            }));
        }
        if (e.HasReleases)
        {
            card.Children.Add(UiKit.Toggle("Обновлять автоматически", e.UserState.AutoUpdate, on =>
            {
                e.UserState.AutoUpdate = on;
                states.Save();
            }, "Остановленный модуль обновляется сразу, работающий — при следующем запуске каркаса, чтобы не прерывать работу."));
        }

        var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        links.Children.Add(UiKit.Link("Папка модуля", () => UiKit.OpenFolder(e.Context.ModuleDir)));
        if (m.Homepage is { } home) links.Children.Add(UiKit.UrlLink("Страница проекта", home));
        links.Children.Add(UiKit.Link("Удалить модуль", () => _ = ModuleOps.UninstallAsync(e)).With(l => l.Foreground = UiKit.Brush("Bad")));
        card.Children.Add(links);

        panel.Children.Add(new Border { Style = UiKit.Style("CardBorder"), Child = card, Margin = new Thickness(0, 10, 0, 14) });
        return panel;
    }

    /// <summary>Общая страница внешней программы: действия, которые она объявила по IPC.</summary>
    private FrameworkElement BuildGeneric()
    {
        var e = _entry;
        var m = e.Context.Manifest;
        var panel = new StackPanel();

        var actions = e.Module.Actions;
        if (actions.Count > 0)
        {
            var buttons = UiKit.Buttons();
            foreach (var action in actions)
                buttons.Children.Add(UiKit.Button(action.Title, () => _ = UiKit.RunAsync(action.Execute, m.Name)));
            panel.Children.Add(UiKit.Card(UiKit.Section("Действия"), buttons));
        }
        else if (e.Module.Status.State == ModuleState.Running && m.Ipc is not null)
        {
            panel.Children.Add(UiKit.Card(UiKit.Section("Действия"),
                UiKit.Hint("Программа работает, но не отвечает каркасу. Скорее всего, установлена версия без поддержки режима --hosted — обновите модуль.")));
        }

        if (m.Description is { } description)
            panel.Children.Add(UiKit.Card(UiKit.Section("О модуле"), UiKit.Text(description)));

        return panel;
    }
}
