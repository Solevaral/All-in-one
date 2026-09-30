using System.Windows;
using System.Windows.Controls;
using AllInOne.Core.Modules;
using AllInOne.Sdk;
using AllInOne.Ui;

namespace AllInOne.Host.UI.Pages;

/// <summary>
/// Страница установленного модуля: общая шапка (статус, запуск, автозапуск, обновление, удаление)
/// и ниже — собственная страница модуля, встроенное окно программы (экспериментально)
/// или общая страница внешней программы. Шапка обновляется при каждом изменении статуса.
/// </summary>
internal sealed class ModulePage : PageBase
{
    private readonly ModuleEntry _entry;
    private readonly ContentControl _header = new();
    private readonly ContentControl _generic = new();
    private readonly object? _view;
    private EmbeddedWindowHost? _embedded;
    private Border? _embeddedFrame;
    private bool _wasRunning;

    public ModulePage(ModuleEntry entry)
    {
        _entry = entry;
        _view = entry.Module.CreateView();

        Body.Children.Add(_header);
        if (_view is UIElement native) Body.Children.Add(native);
        else if (CanEmbed) Body.Children.Add(new Grid { Children = { _generic, BuildEmbedded() } });
        else Body.Children.Add(_generic);

        Unloaded += (_, _) => _embedded?.Dispose();
        Refresh();
    }

    private bool CanEmbed =>
        Manager.Settings.ExperimentalFeatures && _entry.Context.Manifest.Embeddable && _entry.Module is ExternalModule;

    public override void Refresh()
    {
        _header.Content = BuildHeader();
        if (_view is null) _generic.Content = BuildGeneric();
    }

    public override void OnEntryChanged(ModuleEntry entry)
    {
        if (!ReferenceEquals(entry, _entry)) return;
        Refresh();
        if (_embedded is null) return;
        _embedded.CheckAlive();
        // Встраивание — только при переходе в «Работает»: окно, скрытое самой программой, обратно не вытаскивается.
        var running = entry.Module.Status.State == ModuleState.Running;
        if (running && !_wasRunning && !_embedded.IsAttached) _ = _embedded.AttachAsync();
        _wasRunning = running;
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
            card.Children.Add(DetailView(detail));
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
            card.Children.Add(UiKit.Toggle("Запускать вместе с All in One", e.UserState.Autostart, on =>
            {
                e.UserState.Autostart = on;
                states.Save();
            }));
        }
        if (e.HasReleases)
        {
            card.Children.Add(UiKit.Toggle("Обновлять автоматически", e.UserState.AutoUpdate, on =>
            {
                e.UserState.AutoUpdate = on;
                states.Save();
            }, "Остановленный модуль обновляется сразу, запущенный — при следующем запуске All in One."));
        }
        if (!m.IsBuiltIn && Manager.Settings.ExperimentalFeatures)
        {
            card.Children.Add(UiKit.Toggle("Перезапускать, если модуль завершился неожиданно", e.UserState.RestartOnCrash, on =>
            {
                e.UserState.RestartOnCrash = on;
                states.Save();
            }, "Запустит программу снова и в том случае, если её закрыли не через All in One (например, из её трея)."));
        }

        var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var folder = m.IsBuiltIn ? e.Context.DataDir : e.Context.ProgramDir;
        links.Children.Add(UiKit.Link("Папка программы", () => UiKit.OpenFolder(folder)));
        if (m.Homepage is { } home) links.Children.Add(UiKit.UrlLink("Страница проекта", home));
        links.Children.Add(UiKit.Link("Удалить модуль", () => _ = ModuleOps.UninstallAsync(e)).With(l => l.Foreground = UiKit.Brush("Bad")));
        card.Children.Add(links);

        panel.Children.Add(new Border { Style = UiKit.Style("CardBorder"), Child = card, Margin = new Thickness(0, 10, 0, 14) });
        return panel;
    }

    /// <summary>Длинные подробности (например, командная строка winws) — свёрнуты, раскрываются по клику.</summary>
    private static FrameworkElement DetailView(string detail)
    {
        const int Limit = 160;
        if (detail.Length <= Limit && !detail.Contains('\n'))
            return UiKit.Hint(detail).With(t => t.Margin = new Thickness(16, 4, 0, 0));

        var text = UiKit.Hint(detail[..Math.Min(Limit, detail.Length)] + "…").With(t => t.Margin = new Thickness(16, 4, 0, 0));
        var expanded = false;
        var toggle = UiKit.Link("Показать полностью", () => { });
        toggle.Margin = new Thickness(16, 2, 0, 0);
        toggle.Click += (_, _) =>
        {
            expanded = !expanded;
            text.Text = expanded ? detail : detail[..Math.Min(Limit, detail.Length)] + "…";
            toggle.Content = expanded ? "Свернуть" : "Показать полностью";
        };
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { toggle, UiKit.Link("Копировать", () => Clipboard.SetText(detail)).With(l => l.Margin = new Thickness(12, 2, 0, 0)) } });
        return panel;
    }

    /// <summary>Общая страница внешней программы: действия, которые она объявила по IPC.</summary>
    private FrameworkElement BuildGeneric()
    {
        var e = _entry;
        var m = e.Context.Manifest;
        var panel = new StackPanel();

        var actions = e.Module.Actions;
        var canShowHere = _embedded is not null && e.Module.Status.State == ModuleState.Running;
        if (actions.Count > 0 || canShowHere)
        {
            var buttons = UiKit.Buttons();
            if (canShowHere)
                buttons.Children.Add(UiKit.AccentButton("Показать окно здесь", () => _ = _embedded!.AttachAsync()));
            foreach (var action in actions)
                buttons.Children.Add(UiKit.Button(action.Title, () => _ = UiKit.RunAsync(action.Execute, m.Name)));
            panel.Children.Add(UiKit.Card(UiKit.Section("Действия"), buttons));
        }
        else if (e.Module.Status.State == ModuleState.Running && m.Ipc is not null)
        {
            panel.Children.Add(UiKit.Card(UiKit.Section("Действия"),
                UiKit.Hint("Программа не отвечает по IPC: установлена версия без режима --hosted.")));
        }

        if (m.Description is { } description)
            panel.Children.Add(UiKit.Card(UiKit.Section("О модуле"), UiKit.Text(description)));

        return panel;
    }

    /// <summary>
    /// Экспериментально: окно программы внутри окна All in One. Рамка видна только со встроенным окном;
    /// пока программа не запущена или окно не найдено, на её месте общая страница.
    /// </summary>
    private FrameworkElement BuildEmbedded()
    {
        var module = (ExternalModule)_entry.Module;
        _embedded = new EmbeddedWindowHost(module);
        _embeddedFrame = new Border
        {
            Style = UiKit.Style("CardBorder"),
            Padding = new Thickness(0),
            Height = 640,
            ClipToBounds = true,
            Child = _embedded,
            Visibility = Visibility.Collapsed,
        };
        _embedded.AttachedChanged += () => Dispatcher.BeginInvoke(() =>
        {
            var attached = _embedded.IsAttached;
            _embeddedFrame.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
            _generic.Visibility = attached ? Visibility.Collapsed : Visibility.Visible;
        });
        _wasRunning = module.Status.State == ModuleState.Running;
        if (_wasRunning) _ = _embedded.AttachAsync();
        return _embeddedFrame;
    }
}
