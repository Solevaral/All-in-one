using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AllInOne.Core;
using AllInOne.Core.Modules;
using AllInOne.Host.UI.Pages;
using AllInOne.Ui;

namespace AllInOne.Host.UI;

/// <summary>Главное окно: боковая панель (разделы и установленные модули) и страница справа.</summary>
internal sealed class MainWindow : Window
{
    private readonly StackPanel _nav = new();
    private readonly ContentControl _content = new();
    private string _current = "home";
    private IPage? _page;

    private static ModuleManager Manager => App.Current.Manager;

    public MainWindow()
    {
        Title = "All-in-one";
        Width = 1120;
        Height = 740;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = UiKit.Brush("Bg");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico"));

        var root = new Grid { Background = UiKit.Brush("Bg") };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var sidebar = new Border { Background = UiKit.Brush("Sidebar"), Padding = new Thickness(14, 18, 14, 14), Child = _nav };
        root.Children.Add(sidebar);

        Grid.SetColumn(_content, 1);
        root.Children.Add(_content);
        Content = root;

        Manager.EntriesChanged += OnEntriesChanged;
        Manager.EntryStatusChanged += OnEntryStatusChanged;
        Manager.UpdatesChecked += OnEntriesChanged;
        App.Current.ProgressChanged += OnProgressChanged;

        BuildNav();
        Navigate("home");

        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    public void Navigate(string key)
    {
        _current = key;
        _page = key switch
        {
            "home" => new HomePage(),
            "catalog" => new CatalogPage(),
            "updates" => new UpdatesPage(),
            "settings" => new SettingsPage(),
            _ when Manager.Find(key) is { IsInstalled: true } entry => new ModulePage(entry),
            _ => new HomePage(),
        };
        _content.Content = _page;
        BuildNav();
    }

    private void OnEntriesChanged(object? sender, EventArgs e)
    {
        BuildNav();
        if (_current != "home" && _current is not ("catalog" or "updates" or "settings") && Manager.Find(_current) is not { IsInstalled: true })
            Navigate("home");
        else
            _page?.Refresh();
    }

    private void OnEntryStatusChanged(object? sender, ModuleEntry entry)
    {
        UpdateNavDot(entry);
        _page?.OnEntryChanged(entry);
    }

    private void OnProgressChanged(object? sender, string id)
    {
        if (Manager.Find(id) is { } entry) _page?.OnEntryChanged(entry);
    }

    // ---------- боковая панель ----------

    private readonly Dictionary<string, Ellipse> _dots = new(StringComparer.OrdinalIgnoreCase);

    private void BuildNav()
    {
        _nav.Children.Clear();
        _dots.Clear();

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 22) };
        brand.Children.Add(new Image { Source = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "All-in-one", FontSize = 16, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "версия " + RuntimeInfo.HostVersionText, FontSize = 11, Foreground = UiKit.Brush("SubText") });
        brand.Children.Add(titles);
        _nav.Children.Add(brand);

        _nav.Children.Add(NavItem("home", "Главная"));

        var installed = Manager.Installed.ToList();
        if (installed.Count > 0)
        {
            _nav.Children.Add(new TextBlock { Text = "МОДУЛИ", Style = UiKit.Style("SectionHeader"), Margin = new Thickness(12, 16, 0, 6) });
            foreach (var entry in installed) _nav.Children.Add(ModuleNavItem(entry));
        }

        _nav.Children.Add(new Border { Height = 1, Background = UiKit.Brush("CardStroke"), Margin = new Thickness(6, 14, 6, 10) });
        _nav.Children.Add(NavItem("catalog", "Каталог"));
        var updates = Manager.Installed.Count(e => e.AvailableUpdate is not null);
        _nav.Children.Add(NavItem("updates", updates > 0 ? $"Обновления  ·  {updates}" : "Обновления"));
        _nav.Children.Add(NavItem("settings", "Настройки"));
    }

    private RadioButton NavItem(string key, string text)
    {
        var item = new RadioButton { Style = UiKit.Style("NavItem"), GroupName = "nav", IsChecked = _current == key, Content = new TextBlock { Text = text, Foreground = null } };
        ((TextBlock)item.Content).SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = item });
        item.Checked += (_, _) => { if (_current != key) Navigate(key); };
        return item;
    }

    private RadioButton ModuleNavItem(ModuleEntry entry)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = UiKit.StateBrush(entry.Module.Status.State), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _dots[entry.Id] = dot;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        var text = new TextBlock { Text = entry.Name };
        row.Children.Add(text);

        var item = new RadioButton { Style = UiKit.Style("NavItem"), GroupName = "nav", IsChecked = _current == entry.Id, Content = row };
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = item });
        item.Checked += (_, _) => { if (_current != entry.Id) Navigate(entry.Id); };
        return item;
    }

    private void UpdateNavDot(ModuleEntry entry)
    {
        if (_dots.TryGetValue(entry.Id, out var dot)) dot.Fill = UiKit.StateBrush(entry.Module.Status.State);
    }

    // ---------- окно ----------

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (Manager.Settings.CloseToTray && !e.Cancel)
        {
            // Крестик сворачивает в трей: модули продолжают работать под присмотром каркаса.
            e.Cancel = true;
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Manager.EntriesChanged -= OnEntriesChanged;
        Manager.EntryStatusChanged -= OnEntryStatusChanged;
        Manager.UpdatesChecked -= OnEntriesChanged;
        App.Current.ProgressChanged -= OnProgressChanged;
        base.OnClosed(e);
    }

    /// <summary>Тёмный заголовок окна (Windows 10 20H1+ / 11).</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>Страница главного окна.</summary>
internal interface IPage
{
    /// <summary>Пересобрать целиком (изменился состав модулей).</summary>
    void Refresh();

    /// <summary>Изменился статус или ход операции одного модуля.</summary>
    void OnEntryChanged(ModuleEntry entry);
}
