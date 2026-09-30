using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AllInOne.Ui;

namespace AllInOne.Modules.ShutdownTimer;

/// <summary>Страница таймера: текущий отсчёт и форма нового таймера.</summary>
internal sealed class ShutdownTimerView : UserControl
{
    private readonly ShutdownTimerModule _module;
    private readonly ContentControl _current = new();
    private readonly TextBox _hours = new() { Width = 64, Text = "0" };
    private readonly TextBox _minutes = new() { Width = 64, Text = "30" };
    private readonly TextBox _time = new() { Width = 90, Text = "23:30" };
    private readonly StackPanel _inPanel = new();
    private readonly StackPanel _atPanel = new();
    private PowerAction _action;
    private bool _force;
    private bool _atMode;

    public ShutdownTimerView(ShutdownTimerModule module)
    {
        _module = module;
        var s = module.State;
        _action = s.Action;
        _force = s.Force;
        _atMode = s.AtTimeMode;
        _hours.Text = s.LastHours.ToString(CultureInfo.InvariantCulture);
        _minutes.Text = s.LastMinutes.ToString(CultureInfo.InvariantCulture);
        _time.Text = s.LastTime;

        var body = new StackPanel();
        body.Children.Add(_current);
        body.Children.Add(BuildForm());
        Content = body;

        UpdateCurrent();
        // Подписка только пока страница на экране: модуль живёт дольше страницы.
        Loaded += (_, _) => { module.StateChanged += OnStateChanged; UpdateCurrent(); };
        Unloaded += (_, _) => module.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e) => UpdateCurrent();

    private void UpdateCurrent()
    {
        var s = _module.State;
        if (s.Target is { } target)
        {
            var title = new TextBlock { Text = $"{TimerState.Title(s.Action)} в {target:HH:mm}" + (target.Date != DateTime.Today ? $" ({target:dd.MM})" : ""), Style = UiKit.Style("CardTitle") };
            var left = new TextBlock { Text = "через " + ShutdownTimerModule.FormatSpan(target - DateTime.Now), FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };
            _current.Content = UiKit.Card(
                UiKit.Section("Таймер взведён"),
                title,
                left,
                UiKit.Hint(TimerState.EndsSession(s.Action)
                    ? "Перед этим каркас бережно остановит все модули. За минуту появится окно с отменой."
                    : "Модули останутся работать. За минуту появится окно с отменой."),
                UiKit.Buttons(
                    UiKit.AccentButton("Отменить", _module.Cancel),
                    UiKit.Button("+10 мин", () => _module.Postpone(TimeSpan.FromMinutes(10))),
                    UiKit.Button("+30 мин", () => _module.Postpone(TimeSpan.FromMinutes(30))),
                    UiKit.Button("+1 ч", () => _module.Postpone(TimeSpan.FromHours(1)))));
        }
        else
        {
            _current.Content = UiKit.Card(UiKit.Section("Таймер"), UiKit.Hint("Таймер не задан. Задайте время ниже или воспользуйтесь меню в трее."));
        }
    }

    private Border BuildForm()
    {
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Новый таймер"));

        // Режим: «через» / «в время».
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        var inMode = new RadioButton { Content = "Через", Style = UiKit.Style("Segment"), GroupName = "mode", IsChecked = !_atMode };
        var atMode = new RadioButton { Content = "В заданное время", Style = UiKit.Style("Segment"), GroupName = "mode", IsChecked = _atMode };
        inMode.Checked += (_, _) => SetMode(false);
        atMode.Checked += (_, _) => SetMode(true);
        modes.Children.Add(inMode);
        modes.Children.Add(atMode);
        panel.Children.Add(UiKit.Row("Когда", new Border { Style = UiKit.Style("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Left, Child = modes }));

        // Через Ч ч М мин + пресеты.
        var span = new StackPanel { Orientation = Orientation.Horizontal };
        span.Children.Add(_hours);
        span.Children.Add(new TextBlock { Text = "ч", Margin = new Thickness(8, 0, 16, 0) });
        span.Children.Add(_minutes);
        span.Children.Add(new TextBlock { Text = "мин", Margin = new Thickness(8, 0, 0, 0) });
        _inPanel.Children.Add(UiKit.Row("Через", span));
        var presets = new WrapPanel();
        foreach (var (minutes, title) in new[] { (15, "15 мин"), (30, "30 мин"), (60, "1 ч"), (90, "1,5 ч"), (120, "2 ч"), (180, "3 ч") })
        {
            presets.Children.Add(UiKit.Button(title, () =>
            {
                _hours.Text = (minutes / 60).ToString(CultureInfo.InvariantCulture);
                _minutes.Text = (minutes % 60).ToString(CultureInfo.InvariantCulture);
            }).With(b => b.Padding = new Thickness(10, 4, 10, 4)));
        }
        _inPanel.Children.Add(UiKit.Row("", presets));

        _atPanel.Children.Add(UiKit.Row("Время (ЧЧ:ММ)", _time));
        _atPanel.Children.Add(UiKit.Row("", UiKit.Hint("Если это время сегодня уже прошло, таймер сработает завтра.")));

        panel.Children.Add(_inPanel);
        panel.Children.Add(_atPanel);
        SetMode(_atMode);

        panel.Children.Add(UiKit.Row("Действие", UiKit.Combo(
            Enum.GetValues<PowerAction>().Select(a => (a, TimerState.Title(a))),
            _action, a => _action = a)));
        panel.Children.Add(UiKit.Toggle("Закрывать программы принудительно", _force, on => _force = on,
            "Не ждать программ, которые спрашивают «сохранить изменения?». Несохранённые данные будут потеряны."));

        panel.Children.Add(UiKit.Buttons(UiKit.AccentButton("Запустить таймер", Arm)));
        return UiKit.Card(panel);
    }

    private void SetMode(bool at)
    {
        _atMode = at;
        _inPanel.Visibility = at ? Visibility.Collapsed : Visibility.Visible;
        _atPanel.Visibility = at ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Arm()
    {
        if (_atMode)
        {
            if (!TimeOnly.TryParseExact(_time.Text.Trim(), ["H:mm", "HH:mm", "H.mm", "HH.mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                _ = Dialog.AlertAsync("Таймер выключения", "Время указано неверно. Пример: 23:30.");
                return;
            }
            _module.SaveFormDefaults(s => { s.AtTimeMode = true; s.LastTime = time.ToString("HH:mm"); s.Force = _force; });
            _module.ArmAt(time, _action);
        }
        else
        {
            if (!int.TryParse(_hours.Text.Trim(), out var h) || !int.TryParse(_minutes.Text.Trim(), out var m) || h < 0 || m < 0 || h * 60 + m < 1 || h > 99)
            {
                _ = Dialog.AlertAsync("Таймер выключения", "Укажите интервал: часы от 0 до 99 и минуты, всего не меньше минуты.");
                return;
            }
            _module.SaveFormDefaults(s => { s.AtTimeMode = false; s.LastHours = h; s.LastMinutes = m; s.Force = _force; });
            _module.ArmIn(TimeSpan.FromMinutes(h * 60 + m), _action);
        }
    }
}
