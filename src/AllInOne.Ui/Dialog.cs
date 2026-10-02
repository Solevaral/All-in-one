using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AllInOne.Ui;

/// <summary>Диалог в стиле каркаса вместо системного MessageBox. Первая кнопка — основная.</summary>
public sealed class Dialog : Window
{
    private int _result = -1;

    private Dialog(string title, string message, IReadOnlyList<string> buttons, UIElement? extra)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = Application.Current.MainWindow is not { IsVisible: true };
        Topmost = ShowInTaskbar;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = UiKit.Style("CardTitle"), Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = UiKit.Brush("SubText"), LineHeight = 19 });
        if (extra is not null) panel.Children.Add(extra);

        // Кнопки — всегда одним рядом справа: окно расширяется под них, а не переносит на вторую строку.
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
            if (i == 0) b.Style = UiKit.Style("AccentButton");
            b.Click += (_, _) => { _result = index; Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Поля окна: отступ 12 и внутренний отступ 22 с каждой стороны, плюс рамка.
        Width = Math.Max(Width, row.DesiredSize.Width + 2 * (12 + 22) + 2);

        Content = new Border
        {
            Background = UiKit.Brush("Card"),
            BorderBrush = UiKit.Brush("ControlStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.45, ShadowDepth = 0 },
            Child = panel,
        };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    /// <summary>Показывает диалог и возвращает индекс нажатой кнопки (-1 — закрыт без выбора).</summary>
    public static Task<int> ShowAsync(string title, string message, params string[] buttons) =>
        ShowAsync(title, message, null, buttons);

    public static async Task<int> ShowAsync(string title, string message, UIElement? extra, params string[] buttons)
    {
        var app = Application.Current;
        return await app.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Dialog(title, message, buttons.Length == 0 ? ["OK"] : buttons, extra);
            if (app.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._result;
        });
    }

    public static Task AlertAsync(string title, string message) => ShowAsync(title, message, "OK");

    public static async Task<bool> ConfirmAsync(string title, string message, string yes = "Да", string no = "Отмена") =>
        await ShowAsync(title, message, yes, no) == 0;
}
