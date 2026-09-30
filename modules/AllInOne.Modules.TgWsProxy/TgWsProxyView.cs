using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using AllInOne.Sdk;
using AllInOne.Ui;

namespace AllInOne.Modules.TgWsProxy;

/// <summary>Страница TG WS Proxy: подключение Telegram, основные настройки, лог.</summary>
internal sealed class TgWsProxyView : UserControl
{
    private readonly TgWsProxyModule _module;
    private readonly ContentControl _connect = new();
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 260,
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 11,
    };

    public TgWsProxyView(TgWsProxyModule module)
    {
        _module = module;
        var body = new StackPanel();
        body.Children.Add(_connect);
        body.Children.Add(BuildSettings());
        body.Children.Add(UiKit.Card(
            UiKit.Section("Лог"),
            _log,
            UiKit.Buttons(
                UiKit.Button("Обновить", LoadLog),
                UiKit.Button("Открыть папку данных", () => UiKit.OpenFolder(_module.DataDir)))));
        Content = body;

        UpdateConnect();
        LoadLog();
        Loaded += (_, _) => module.StatusChanged += OnStatus;
        Unloaded += (_, _) => module.StatusChanged -= OnStatus;
    }

    private void OnStatus(object? sender, ModuleStatus e) => Dispatcher.BeginInvoke(() => { UpdateConnect(); LoadLog(); });

    private void UpdateConnect()
    {
        var link = _module.TryBuildLink();
        var running = _module.Status.State == ModuleState.Running;
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Подключение Telegram"));
        if (link is null)
        {
            panel.Children.Add(UiKit.Hint("Ссылка появится после первого запуска прокси — он сам создаст секрет."));
        }
        else
        {
            panel.Children.Add(UiKit.Hint("Нажмите «Открыть в Telegram» — Telegram предложит включить прокси. Прокси должен быть запущен."));
            panel.Children.Add(UiKit.Buttons(
                UiKit.AccentButton("Открыть в Telegram", () => TgWsProxyModule.OpenLink(link)).With(b => b.IsEnabled = running),
                UiKit.Button("Скопировать ссылку", () => Clipboard.SetText(link))));
        }
        if (_module.Stats is { } s)
            panel.Children.Add(UiKit.Hint($"Всего соединений: {s.Total}, активных: {s.Active}, отправлено {s.Up}, получено {s.Down}.").With(h => h.Margin = new Thickness(0, 10, 0, 0)));
        _connect.Content = UiKit.Card(panel);
    }

    private Border BuildSettings()
    {
        var c = _module.ReadConfig();
        var host = new TextBox { Text = c["host"]?.GetValue<string>() ?? "127.0.0.1", Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        var port = new TextBox { Text = (c["port"]?.GetValue<int>() ?? 1443).ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        var dcIp = new TextBox
        {
            Text = string.Join(Environment.NewLine, (c["dc_ip"] as JsonArray)?.Select(n => n?.GetValue<string>()) ?? ["2:149.154.167.220", "4:149.154.167.220"]),
            AcceptsReturn = true,
            Height = 70,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var cfproxy = c["cfproxy"]?.GetValue<bool>() ?? true;
        var verbose = c["verbose"]?.GetValue<bool>() ?? false;

        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Настройки прокси"));
        panel.Children.Add(UiKit.Row("Адрес", host));
        panel.Children.Add(UiKit.Row("", UiKit.Hint("127.0.0.1 — только этот компьютер; 0.0.0.0 — и другие устройства в сети.")));
        panel.Children.Add(UiKit.Row("Порт", port));
        panel.Children.Add(UiKit.Row("DC → IP", dcIp));
        panel.Children.Add(UiKit.Row("", UiKit.Hint("По одному на строку в виде «номер DC:IP».")));
        panel.Children.Add(UiKit.Toggle("Запасной путь через Cloudflare (cfproxy)", cfproxy, on => cfproxy = on));
        panel.Children.Add(UiKit.Toggle("Подробный лог", verbose, on => verbose = on));
        panel.Children.Add(UiKit.Hint("Остальные настройки — в окне самой программы (значок TG WS Proxy в трее)."));
        panel.Children.Add(UiKit.Buttons(UiKit.AccentButton("Сохранить и перезапустить", () => _ = SaveAsync())));
        return UiKit.Card(panel);

        async Task SaveAsync()
        {
            if (!int.TryParse(port.Text.Trim(), out var p) || p is < 1 or > 65535)
            {
                await Dialog.AlertAsync("TG WS Proxy", "Порт — число от 1 до 65535.");
                return;
            }
            var lines = dcIp.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Any(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^-?\d+:\d{1,3}(\.\d{1,3}){3}$")))
            {
                await Dialog.AlertAsync("TG WS Proxy", "Строки DC → IP должны выглядеть как «2:149.154.167.220».");
                return;
            }

            await UiKit.RunAsync(async () =>
            {
                var wasRunning = _module.Status.IsActive;
                // Программа читает config.json только при старте — останавливаем, пишем, запускаем.
                if (wasRunning) await _module.StopAsync(StopReason.Restart, CancellationToken.None);
                var cfg = _module.ReadConfig();
                cfg["host"] = host.Text.Trim();
                cfg["port"] = p;
                cfg["dc_ip"] = new JsonArray(lines.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
                cfg["cfproxy"] = cfproxy;
                cfg["verbose"] = verbose;
                _module.WriteConfig(cfg);
                if (wasRunning) await _module.StartAsync(CancellationToken.None);
                UpdateConnect();
            }, "Настройки не применились");
        }
    }

    private void LoadLog()
    {
        var lines = _module.ReadLogTail(out _);
        _log.Text = lines.Count == 0 ? "Лог пуст." : string.Join(Environment.NewLine, lines);
        _log.ScrollToEnd();
    }
}
