using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AllInOne.Sdk;

namespace AllInOne.Ui;

/// <summary>
/// Сборка интерфейса в коде (как в fDimmer): карточки, заголовки, кнопки, переключатели.
/// Стили берутся из темы каркаса (Theme/Dark.xaml), поэтому модули выглядят так же, как каркас.
/// </summary>
public static class UiKit
{
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static Border Card(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var c in children) panel.Children.Add(c);
        return new Border { Style = Style("CardBorder"), Child = panel };
    }

    public static TextBlock PageTitle(string text) => new() { Text = text, Style = Style("PageTitle") };

    public static TextBlock CardTitle(string text) => new() { Text = text, Style = Style("CardTitle"), TextWrapping = TextWrapping.Wrap };

    public static TextBlock Section(string text) => new() { Text = text, Style = Style("SectionHeader") };

    public static TextBlock Hint(string text) => new() { Text = text, Style = Style("Hint") };

    public static TextBlock Text(string text, double size = 13) =>
        new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Mono(string text) => new() { Text = text, Style = Style("Value"), HorizontalAlignment = HorizontalAlignment.Left, TextWrapping = TextWrapping.Wrap };

    public static Button Button(string text, Action onClick, string? style = null, string? tooltip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), ToolTip = tooltip };
        if (style is not null) b.Style = Style(style);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button AccentButton(string text, Action onClick, string? tooltip = null) => Button(text, onClick, "AccentButton", tooltip);

    public static Button Link(string text, Action onClick) =>
        new Button { Content = text, Style = Style("LinkButton"), Margin = new Thickness(0, 0, 12, 0) }.With(b => b.Click += (_, _) => onClick());

    public static Button UrlLink(string text, string url) => Link(text, () => OpenUrl(url));

    public static WrapPanel Buttons(params UIElement[] children)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, -8) };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    /// <summary>Переключатель с подписью слева и необязательной подсказкой под ним.</summary>
    public static FrameworkElement Toggle(string label, bool isChecked, Action<bool> onChanged, string? hint = null)
    {
        var toggle = new CheckBox
        {
            Style = Style("Toggle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsChecked = isChecked,
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(0, 4, 0, hint is null ? 4 : 0),
        };
        toggle.Checked += (_, _) => onChanged(true);
        toggle.Unchecked += (_, _) => onChanged(false);
        if (hint is null) return toggle;

        var panel = new StackPanel();
        panel.Children.Add(toggle);
        panel.Children.Add(Hint(hint));
        return panel;
    }

    /// <summary>Строка «подпись — элемент справа».</summary>
    public static Grid Row(string label, UIElement element, double labelWidth = 200)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, Style = Style("Label"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(element, 1);
        grid.Children.Add(element);
        return grid;
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Title)> items, T selected, Action<T> onChanged)
    {
        var combo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, title) in items)
        {
            var item = new ComboBoxItem { Content = title, Tag = value };
            combo.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, selected)) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T v }) onChanged(v);
        };
        return combo;
    }

    public static Brush StateBrush(ModuleState state) => state switch
    {
        ModuleState.Running => Brush("Good"),
        ModuleState.Starting or ModuleState.Stopping or ModuleState.Updating => Brush("Warn"),
        ModuleState.Error => Brush("Bad"),
        _ => Brush("Idle"),
    };

    public static string StateText(ModuleState state) => state switch
    {
        ModuleState.NotInstalled => "Не установлен",
        ModuleState.Stopped => "Остановлен",
        ModuleState.Starting => "Запуск…",
        ModuleState.Running => "Работает",
        ModuleState.Stopping => "Остановка…",
        ModuleState.Updating => "Обновление…",
        ModuleState.Error => "Ошибка",
        _ => state.ToString(),
    };

    /// <summary>Точка статуса + текст.</summary>
    public static StackPanel StatusLine(ModuleStatus status)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = StateBrush(status.State), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        // Своя строка модуля («Яркость 60 %», «Таймер не задан») понятнее общего «Работает».
        var text = status.Summary is { Length: > 0 } s && status.State != ModuleState.NotInstalled ? s : StateText(status.State);
        panel.Children.Add(new TextBlock { Text = text, Foreground = Brush("SubText"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    /// <summary>Ссылка или папка открывается от имени пользователя, без прав администратора.</summary>
    public static void OpenUrl(string url) => AllInOne.Core.Processes.UserShell.Open(url);

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        OpenUrl(path);
    }

    /// <summary>
    /// Выполняет действие из интерфейса: ошибки показываются диалогом, отмена пользователем
    /// (отказ завершать модуль принудительно) — молча.
    /// </summary>
    public static async Task RunAsync(Func<Task> action, string errorTitle = "Ошибка")
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dialog.AlertAsync(errorTitle, ex is AggregateException agg ? string.Join("\n", agg.InnerExceptions.Select(e => e.Message)) : ex.Message);
        }
    }

    public static T With<T>(this T element, Action<T> setup)
    {
        setup(element);
        return element;
    }
}
