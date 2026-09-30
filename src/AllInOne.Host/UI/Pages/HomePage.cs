using System.Windows;
using System.Windows.Controls;
using AllInOne.Core;
using AllInOne.Core.Modules;
using AllInOne.Ui;

namespace AllInOne.Host.UI.Pages;

/// <summary>Плитки установленных модулей: статус, запуск и остановка, переход к модулю.</summary>
internal sealed class HomePage : PageBase
{
    private readonly WrapPanel _tiles = new();
    private readonly Dictionary<string, Border> _tileById = new(StringComparer.OrdinalIgnoreCase);

    public HomePage() => Refresh();

    public override void Refresh()
    {
        Body.Children.Clear();
        Body.Children.Add(UiKit.PageTitle("Главная"));

        if (AppPaths.CheckRootPath() is { Count: > 0 } problems)
        {
            Body.Children.Add(UiKit.Card(
                UiKit.CardTitle("Неподходящая папка установки"),
                UiKit.Hint(string.Join("\n", problems) + $"\n{AppPaths.Root}")));
        }

        _tiles.Children.Clear();
        _tileById.Clear();
        _tiles.Margin = new Thickness(0, 10, -14, 0);
        var installed = Manager.Installed.ToList();
        foreach (var entry in installed)
        {
            var tile = BuildTile(entry);
            _tileById[entry.Id] = tile;
            _tiles.Children.Add(tile);
        }
        Body.Children.Add(_tiles);

        if (installed.Count == 0)
        {
            Body.Children.Add(UiKit.Card(
                UiKit.CardTitle("Модули не установлены"),
                UiKit.Buttons(UiKit.AccentButton("Каталог", () => App.Current.ShowModule("catalog")))));
        }
    }

    public override void OnEntryChanged(ModuleEntry entry)
    {
        var index = _tileById.TryGetValue(entry.Id, out var old) ? _tiles.Children.IndexOf(old) : -1;
        if (index < 0 || !entry.IsInstalled)
        {
            // Модуль только что установлен или удалён — состав плиток изменился.
            Refresh();
            return;
        }
        var tile = BuildTile(entry);
        _tileById[entry.Id] = tile;
        _tiles.Children.RemoveAt(index);
        _tiles.Children.Insert(index, tile);
    }

    private static Border BuildTile(ModuleEntry entry)
    {
        var status = entry.Module.Status;
        var panel = new StackPanel();

        var head = new DockPanel();
        var open = UiKit.Link("Открыть", () => App.Current.ShowModule(entry.Id));
        open.Margin = new Thickness(0);
        DockPanel.SetDock(open, Dock.Right);
        head.Children.Add(open);
        head.Children.Add(UiKit.CardTitle(entry.Name));
        panel.Children.Add(head);

        panel.Children.Add(UiKit.StatusLine(status));
        if (entry.AvailableUpdate is { } update)
            panel.Children.Add(new TextBlock { Text = $"Обновление {update}", Foreground = UiKit.Brush("Accent"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        if (entry.LastError is { } error)
            panel.Children.Add(new TextBlock { Text = error, Foreground = UiKit.Brush("Bad"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        if (ProgressFor(entry) is { } progress)
        {
            panel.Children.Add(progress);
        }
        else
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, -8) };
            if (!entry.Context.Manifest.IsBuiltIn)
            {
                if (status.IsActive)
                    buttons.Children.Add(UiKit.Button("Остановить", () => _ = ModuleOps.StopAsync(entry)).With(b => b.IsEnabled = !entry.IsBusy));
                else
                    buttons.Children.Add(UiKit.AccentButton("Запустить", () => _ = ModuleOps.StartAsync(entry)).With(b => b.IsEnabled = !entry.IsBusy));
            }
            foreach (var action in entry.Module.Actions.Take(2))
                buttons.Children.Add(UiKit.Button(action.Title, () => _ = UiKit.RunAsync(action.Execute, entry.Name)));
            panel.Children.Add(buttons);
        }

        return new Border
        {
            Style = UiKit.Style("CardBorder"),
            Width = 262,
            MinHeight = 132,
            Margin = new Thickness(0, 0, 14, 14),
            Child = panel,
        };
    }
}
