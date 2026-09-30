using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AllInOne.Ui;

namespace AllInOne.Modules.ShutdownTimer;

/// <summary>Окно последней минуты: поверх всех окон, в правом нижнем углу, с отменой и отсрочкой.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly TextBlock _time = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) };

    public CountdownWindow(string action, TimeSpan left)
    {
        Title = "Таймер выключения";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        ShowActivated = true;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = action + " компьютера", Style = UiKit.Style("CardTitle") });
        panel.Children.Add(_time);
        panel.Children.Add(UiKit.Hint("Перед этим каркас бережно остановит модули."));

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(UiKit.AccentButton("Отменить", () => CancelRequested?.Invoke()));
        buttons.Children.Add(UiKit.Button("+10 мин", () => PostponeRequested?.Invoke()));
        panel.Children.Add(buttons);

        Content = new Border
        {
            Background = UiKit.Brush("Card"),
            BorderBrush = UiKit.Brush("Accent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 16, 20, 10),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.5, ShadowDepth = 0 },
            Child = panel,
        };

        Update(left);
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    public event Action? CancelRequested;

    public event Action? PostponeRequested;

    public void Update(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        _time.Text = $"через {(int)left.TotalMinutes}:{left.Seconds:00}";
    }
}
