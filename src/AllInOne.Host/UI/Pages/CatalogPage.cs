using System.Windows;
using System.Windows.Controls;
using AllInOne.Core.Catalog;
using AllInOne.Core.Modules;
using AllInOne.Ui;
using Microsoft.Win32;

namespace AllInOne.Host.UI.Pages;

/// <summary>Каталог: встроенные и удалённые записи, установка, ручная установка из zip.</summary>
internal sealed class CatalogPage : PageBase
{
    private readonly StackPanel _list = new();
    private readonly Dictionary<string, Border> _cards = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshing;

    public CatalogPage()
    {
        Refresh();
        if (Manager.Catalog.RemoteState is "только встроенный" or "из кэша") _ = RefreshCatalogAsync();
    }

    public override void Refresh()
    {
        Body.Children.Clear();
        Body.Children.Add(UiKit.PageTitle("Каталог"));
        Body.Children.Add(UiKit.Hint($"Программы, которые можно поставить в каркас. Удалённый каталог: {Manager.Catalog.RemoteState}."));
        Body.Children.Add(UiKit.Buttons(
            UiKit.Button(_refreshing ? "Обновление…" : "Обновить каталог", () => _ = RefreshCatalogAsync()).With(b => b.IsEnabled = !_refreshing),
            UiKit.Button("Установить из файла…", () => _ = InstallFromFileAsync(),
                tooltip: "Zip-архив с module.json в корне и папкой payload — для своих модулей.")));

        _list.Children.Clear();
        _cards.Clear();
        _list.Margin = new Thickness(0, 18, 0, 0);
        foreach (var group in Manager.Entries.Where(e => e.CatalogItem is not null || e.IsInstalled)
                     .GroupBy(e => e.Context.Manifest.Category ?? "Другое"))
        {
            _list.Children.Add(UiKit.Section(group.Key));
            foreach (var entry in group)
            {
                var card = BuildCard(entry);
                _cards[entry.Id] = card;
                _list.Children.Add(card);
            }
        }
        Body.Children.Add(_list);
    }

    public override void OnEntryChanged(ModuleEntry entry)
    {
        if (!_cards.TryGetValue(entry.Id, out var old)) return;
        var index = _list.Children.IndexOf(old);
        if (index < 0) return;
        var card = BuildCard(entry);
        _cards[entry.Id] = card;
        _list.Children.RemoveAt(index);
        _list.Children.Insert(index, card);
    }

    private static Border BuildCard(ModuleEntry entry)
    {
        var m = entry.CatalogItem?.Manifest ?? entry.Context.Manifest;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        info.Children.Add(UiKit.CardTitle(m.Name));
        var meta = new List<string>();
        if (m.Author is { } a) meta.Add(a);
        if (entry.IsInstalled) meta.Add("установлен " + entry.Context.Manifest.Version);
        if (entry.CatalogItem?.Origin == CatalogOrigin.Remote) meta.Add("из удалённого каталога");
        if (entry.CatalogItem is null) meta.Add("установлен вручную");
        info.Children.Add(UiKit.Hint(string.Join("  ·  ", meta)));
        if (m.Description is { } d) info.Children.Add(UiKit.Text(d).With(t => t.Foreground = UiKit.Brush("SubText")));
        if (m.Homepage is { } home) info.Children.Add(UiKit.UrlLink(home.Replace("https://", ""), home).With(l => l.Margin = new Thickness(0, 6, 0, 0)));
        if (entry.CatalogItem?.RequiresHostUpdate == true)
            info.Children.Add(new TextBlock { Text = $"Нужна версия каркаса {m.MinHostVersion} или новее.", Foreground = UiKit.Brush("Warn"), Margin = new Thickness(0, 6, 0, 0) });
        if (ProgressFor(entry) is { } progress) info.Children.Add(progress);
        grid.Children.Add(info);

        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        if (!entry.IsInstalled)
        {
            actions.Children.Add(UiKit.AccentButton("Установить", () => _ = ModuleOps.InstallAsync(entry))
                .With(b => b.IsEnabled = !entry.IsBusy && entry.CatalogItem?.RequiresHostUpdate != true));
        }
        else
        {
            actions.Children.Add(UiKit.Button("Открыть", () => App.Current.ShowModule(entry.Id)));
            if (entry.AvailableUpdate is { } u)
                actions.Children.Add(UiKit.AccentButton($"Обновить до {u}", () => _ = ModuleOps.UpdateAsync(entry)).With(b => b.IsEnabled = !entry.IsBusy));
        }
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return new Border { Style = UiKit.Style("CardBorder"), Child = grid };
    }

    private async Task RefreshCatalogAsync()
    {
        _refreshing = true;
        Refresh();
        try
        {
            await Manager.Catalog.RefreshAsync(Manager.Settings.CatalogUrl, CancellationToken.None);
        }
        finally
        {
            _refreshing = false;
            Refresh();
        }
    }

    private static async Task InstallFromFileAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Модуль All-in-one (*.zip)|*.zip", Title = "Установить модуль из файла" };
        if (dialog.ShowDialog() != true) return;
        await UiKit.RunAsync(async () =>
        {
            var manifest = Core.Install.ModuleInstaller.ReadManifestFromZip(dialog.FileName);
            var go = await Dialog.ConfirmAsync("Установить модуль?",
                $"«{manifest.Name}» ({manifest.Id}) {manifest.Version}\n\nМодули из файлов не проверяются каркасом — ставьте только то, чему доверяете.",
                "Установить");
            if (!go) return;
            var app = App.Current;
            var progress = new Progress<Core.Install.InstallProgress>(p => app.ReportProgress(manifest.Id, p));
            try
            {
                await Manager.InstallFromZipAsync(dialog.FileName, progress);
            }
            finally
            {
                app.ReportProgress(manifest.Id, null);
            }
        }, "Модуль не установился");
    }
}
