using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AllInOne.Ui;

namespace AllInOne.Modules.ShutdownTimer;

/// <summary>Окно последней минуты: поверх всех окон, в правом нижнем углу, с отменой и отсрочкой.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly CountdownDial _dial;
    private readonly DateTime _target;
    private readonly DateTime _start;

    public CountdownWindow(string action, DateTime target, DateTime start, CountdownFace face)
    {
        _target = target;
        _start = start;
        Title = "Таймер выключения";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        ShowActivated = true;

        _dial = new CountdownDial(target, start, face, size: 180) { Margin = new Thickness(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = action + " компьютера", Style = UiKit.Style("CardTitle"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(_dial);
        panel.Children.Add(UiKit.Hint("Перед этим All in One остановит модули.").With(t => t.HorizontalAlignment = HorizontalAlignment.Center));

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
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

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
        };
        SizeChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            var area = SystemParameters.WorkArea;
            Top = area.Bottom - ActualHeight - 8;
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    public event Action? CancelRequested;

    public event Action? PostponeRequested;

    public void SetFace(CountdownFace face) => _dial.Set(_target, _start, face);
}
