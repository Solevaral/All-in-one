using System.Net.Http;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using AllInOne.Sdk;
using AllInOne.Ui;

namespace AllInOne.Modules.Zapret;

/// <summary>Страница zapret: всё, что делает меню service.bat, плюс выбор стратегии.</summary>
internal sealed class ZapretView : UserControl
{
    private readonly ZapretModule _module;
    private readonly ContentControl _restartBanner = new();
    private readonly ContentControl _strategyCard = new();
    private readonly ContentControl _gameCard = new();
    private readonly ContentControl _ipsetCard = new();
    private readonly ContentControl _listsCard = new();
    private readonly ContentControl _fakesCard = new();
    private readonly ContentControl _diagCard = new();
    private IReadOnlyList<DiagnosticCheck>? _checks;
    private bool _diagRunning;
    private bool _pendingRestart;

    public ZapretView(ZapretModule module)
    {
        _module = module;
        var body = new StackPanel();
        body.Children.Add(_restartBanner);
        body.Children.Add(_strategyCard);
        body.Children.Add(_gameCard);
        body.Children.Add(_ipsetCard);
        body.Children.Add(_listsCard);
        body.Children.Add(_fakesCard);
        body.Children.Add(_diagCard);
        Content = body;

        BuildAll();
        Loaded += (_, _) => module.StatusChanged += OnStatus;
        Unloaded += (_, _) => module.StatusChanged -= OnStatus;
    }

    private void OnStatus(object? sender, ModuleStatus e) => Dispatcher.BeginInvoke(() =>
    {
        if (!_module.Status.IsActive) _pendingRestart = false;
        BuildRestartBanner();
    });

    private void BuildAll()
    {
        BuildRestartBanner();
        BuildStrategy();
        BuildGameFilter();
        BuildIpset();
        BuildLists();
        BuildFakes();
        BuildDiagnostics();
    }

    private bool Running => _module.Status.State == ModuleState.Running;

    /// <summary>Настройка изменена, а zapret работает — изменения вступят в силу после перезапуска.</summary>
    private void Changed()
    {
        _pendingRestart = Running;
        BuildRestartBanner();
    }

    private void BuildRestartBanner()
    {
        _restartBanner.Content = _pendingRestart && Running
            ? UiKit.Card(
                UiKit.Text("Изменения применятся после перезапуска zapret."),
                UiKit.Buttons(UiKit.AccentButton("Перезапустить сейчас", () => _ = RestartAsync())))
            : null;
    }

    private async Task RestartAsync()
    {
        await UiKit.RunAsync(async () =>
        {
            await _module.StopAsync(StopReason.Restart, CancellationToken.None);
            await _module.StartAsync(CancellationToken.None);
        }, "zapret не перезапустился");
        _pendingRestart = false;
        BuildRestartBanner();
    }

    // ---------- стратегия и режим ----------

    private void BuildStrategy()
    {
        var strategies = _module.Files.Strategies();
        var current = _module.CurrentStrategy;
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Стратегия"));
        panel.Children.Add(UiKit.Hint("Разные стратегии по-разному обходят блокировки у разных провайдеров. Если Discord или YouTube не работают — попробуйте другую или запустите тесты внизу страницы."));

        panel.Children.Add(UiKit.Row("Стратегия", UiKit.Combo(strategies.Select(s => (s, s)), current ?? "", s =>
        {
            _module.Settings.Strategy = s;
            _module.SaveSettings();
            Changed();
        })));

        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, title) in new[] { (RunMode.Process, "Процесс"), (RunMode.Service, "Служба Windows") })
        {
            var rb = new RadioButton { Content = title, Style = UiKit.Style("Segment"), GroupName = "zapret-mode", IsChecked = _module.Settings.Mode == mode };
            rb.Checked += (_, _) =>
            {
                if (_module.Settings.Mode == mode) return;
                _module.Settings.Mode = mode;
                _module.SaveSettings();
                Changed();
                BuildStrategy();
            };
            modes.Children.Add(rb);
        }
        panel.Children.Add(UiKit.Row("Режим запуска", new Border { Style = UiKit.Style("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Left, Child = modes }));
        panel.Children.Add(UiKit.Hint(_module.Settings.Mode == RunMode.Service
            ? "Служба «zapret» работает без каркаса и стартует вместе с Windows (как Install Service в service.bat). Остановка модуля останавливает службу, удалить её можно кнопкой ниже."
            : "Каркас сам запускает winws.exe и следит за ним. Чтобы zapret включался при входе в Windows, включите автозапуск каркаса и галочку «Запускать вместе с каркасом»."));

        panel.Children.Add(UiKit.Buttons(
            UiKit.Button("Запустить в окне (отладка)", () => _ = UiKit.RunAsync(async () =>
            {
                if (_module.Status.IsActive) await _module.StopAsync(StopReason.Restart, CancellationToken.None);
                _module.RunInConsole();
            }), tooltip: "Запускает winws в видимой консоли — видно, почему он не стартует. Закройте окно, чтобы остановить."),
            UiKit.Button("Удалить службу Windows", () => _ = UiKit.RunAsync(async () =>
            {
                if (ZapretModule.ReadServiceImagePath() is not { } image)
                {
                    await Dialog.AlertAsync("Служба zapret", "Служба «zapret» не установлена.");
                    return;
                }
                // Чужая служба (zapret, установленный отдельно) — удаляем только с явного согласия.
                var own = _module.IsOurService();
                var go = await Dialog.ConfirmAsync("Удалить службу «zapret»?",
                    own
                        ? "Служба этого модуля будет остановлена и удалена. Обход перестанет запускаться вместе с Windows."
                        : $"Эта служба установлена НЕ этим модулем, а из другой папки:\n{image}\n\nОна будет остановлена и удалена. Сам отдельный zapret останется на диске.",
                    "Удалить", "Отмена");
                if (!go) return;
                await _module.RemoveServiceAsync(CancellationToken.None);
                await _module.RefreshAsync(CancellationToken.None);
            })),
            UiKit.Button("Папка zapret", () => UiKit.OpenFolder(_module.Files.Root))));

        _strategyCard.Content = UiKit.Card(panel);
    }

    // ---------- Game Filter ----------

    private void BuildGameFilter()
    {
        var gf = _module.Files.ReadGameFilter();
        var mode = gf.Mode;
        var tcp = new TextBox { Text = gf.TcpRange, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var udp = new TextBox { Text = gf.UdpRange, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };

        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Game Filter"));
        panel.Children.Add(UiKit.Hint("Обход и для игр: обрабатывает порты выше 1024. Включайте, только если нужен — лишняя нагрузка и риск для античитов."));
        panel.Children.Add(UiKit.Row("Режим", UiKit.Combo(
            [(GameFilterMode.Disabled, "выключен"), (GameFilterMode.All, "TCP и UDP"), (GameFilterMode.Tcp, "только TCP"), (GameFilterMode.Udp, "только UDP")],
            mode, m => mode = m)));
        panel.Children.Add(UiKit.Row("Порты TCP", tcp));
        panel.Children.Add(UiKit.Row("Порты UDP", udp));
        panel.Children.Add(UiKit.Row("", UiKit.Hint("Например: 1024-65535 или 1024-1934,1936-65535.")));
        panel.Children.Add(UiKit.Buttons(UiKit.AccentButton("Сохранить", () =>
        {
            var t = ZapretFiles.ValidateRange(tcp.Text);
            var u = ZapretFiles.ValidateRange(udp.Text);
            if (t is null || u is null)
            {
                _ = Dialog.AlertAsync("Game Filter", "Порты указаны неверно: числа от 1 до 65535, диапазоны через дефис, через запятую.");
                return;
            }
            _module.Files.WriteGameFilter(new GameFilter(mode, t, u));
            Changed();
            BuildGameFilter();
        })));
        _gameCard.Content = UiKit.Card(panel);
    }

    // ---------- IPSet ----------

    private void BuildIpset()
    {
        var files = _module.Files;
        var current = files.ReadIpsetMode();
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("IPSet"));
        panel.Children.Add(UiKit.Hint("Обход по списку IP-адресов (для сервисов, которых нет в списках доменов)."));

        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, title) in new[] { (IpsetMode.None, "Выключен"), (IpsetMode.Loaded, "По списку"), (IpsetMode.Any, "Все адреса") })
        {
            var rb = new RadioButton { Content = title, Style = UiKit.Style("Segment"), GroupName = "ipset", IsChecked = current == mode };
            rb.Checked += (_, _) =>
            {
                if (files.ReadIpsetMode() == mode) return;
                try
                {
                    files.SetIpsetMode(mode);
                    Changed();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
                {
                    _ = Dialog.AlertAsync("IPSet", ex.Message);
                }
                BuildIpset();
            };
            modes.Children.Add(rb);
        }
        panel.Children.Add(UiKit.Row("Режим", new Border { Style = UiKit.Style("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Left, Child = modes }));
        if (current == IpsetMode.Any)
            panel.Children.Add(UiKit.Hint("«Все адреса» обрабатывает любой трафик на отслеживаемых портах — может ломать сайты, которые и так работают."));

        panel.Children.Add(UiKit.Buttons(UiKit.Button("Обновить список IPSet", () => _ = UiKit.RunAsync(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AllInOne");
            await files.UpdateIpsetAsync(http, CancellationToken.None);
            Changed();
            BuildIpset();
        }, "Список не обновлён"), tooltip: "Скачивает свежий список из репозитория Flowseal и включает режим «По списку».")));
        _ipsetCard.Content = UiKit.Card(panel);
    }

    // ---------- свои списки ----------

    private void BuildLists()
    {
        var files = _module.Files;
        var lists = new[]
        {
            ("list-general-user.txt", "Свои домены для обхода"),
            ("list-exclude-user.txt", "Домены-исключения"),
            ("ipset-exclude-user.txt", "IP-исключения"),
        };
        var selected = lists[0].Item1;
        var editor = new TextBox
        {
            AcceptsReturn = true,
            Height = 160,
            TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = files.Exists ? files.ReadUserList(selected) : "",
        };

        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Мои списки"));
        panel.Children.Add(UiKit.Hint("По одному домену или адресу на строку. Поддомены включаются сами; «^» в начале — только точное совпадение. Эти файлы сохраняются при обновлениях."));
        panel.Children.Add(UiKit.Row("Список", UiKit.Combo(lists.Select(l => (l.Item1, l.Item2)), selected, name =>
        {
            selected = name;
            editor.Text = files.ReadUserList(name);
        })));
        panel.Children.Add(editor);
        panel.Children.Add(UiKit.Buttons(UiKit.AccentButton("Сохранить список", () =>
        {
            files.EnsureUserLists();
            files.WriteUserList(selected, editor.Text);
            editor.Text = files.ReadUserList(selected);
            Changed();
        })));
        _listsCard.Content = UiKit.Card(panel);
    }

    // ---------- фейки ----------

    private void BuildFakes()
    {
        var files = _module.Files;
        var candidates = files.FakeCandidates();
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Активные фейки"));
        panel.Children.Add(UiKit.Hint("Какой заготовленный пакет подставлять для голосовых Discord и для игр. Меняйте, только если советуют в обсуждениях zapret."));
        foreach (var (active, title) in new[] { ("ACTIVE_DISCORD_UDP.bin", "Discord (UDP)"), ("ACTIVE_GAME_UDP.bin", "Игры (UDP)") })
        {
            var current = files.CurrentFake(active) ?? "";
            var items = candidates.Select(c => (c, c)).ToList();
            if (current == "") items.Insert(0, ("", "(свой файл)"));
            panel.Children.Add(UiKit.Row(title, UiKit.Combo(items, current, c =>
            {
                if (c == "") return;
                files.SetFake(active, c);
                Changed();
            })));
        }
        _fakesCard.Content = UiKit.Card(panel);
    }

    // ---------- диагностика ----------

    private void BuildDiagnostics()
    {
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Диагностика"));
        panel.Children.Add(UiKit.Hint("Проверки из service.bat: службы, драйвер, прокси, конфликтующие программы, DoH. Ничего не меняется без вашей команды."));

        if (_checks is not null)
        {
            foreach (var check in _checks) panel.Children.Add(CheckRow(check));
        }

        panel.Children.Add(UiKit.Buttons(
            UiKit.Button(_diagRunning ? "Проверка…" : "Проверить", () => _ = RunDiagnosticsAsync()).With(b => b.IsEnabled = !_diagRunning),
            UiKit.Button("Очистить кэш Discord", () =>
            {
                var n = ZapretDiagnostics.ClearDiscordCache();
                _ = Dialog.AlertAsync("Кэш Discord", n > 0 ? $"Очищено папок: {n}. Перезапустите Discord." : "Нечего очищать или Discord запущен — закройте его и попробуйте снова.");
            }),
            UiKit.Button("Запустить тесты стратегий", () => _ = RunTestsAsync(),
                tooltip: "utils\\test zapret.ps1 — перебирает стратегии и проверяет доступность сайтов. Откроется окно PowerShell.")));

        _diagCard.Content = UiKit.Card(panel);
    }

    private FrameworkElement CheckRow(DiagnosticCheck check)
    {
        var color = check.Level switch
        {
            CheckLevel.Ok => "Good",
            CheckLevel.Info => "Accent",
            CheckLevel.Warning => "Warn",
            _ => "Bad",
        };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = UiKit.Brush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = check.Title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(UiKit.Hint(check.Detail).With(h => h.Margin = new Thickness(0, 1, 0, 0)));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (check.Fix is { } fix)
        {
            var b = UiKit.Button(check.FixTitle ?? "Исправить", () => _ = UiKit.RunAsync(async () =>
            {
                await fix();
                await RunDiagnosticsAsync();
            }));
            b.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }
        return grid;
    }

    private async Task RunDiagnosticsAsync()
    {
        _diagRunning = true;
        BuildDiagnostics();
        try
        {
            _checks = await ZapretDiagnostics.RunAsync(_module, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _checks = [new DiagnosticCheck("Диагностика", CheckLevel.Problem, ex.Message)];
        }
        finally
        {
            _diagRunning = false;
            BuildDiagnostics();
        }
    }

    private async Task RunTestsAsync()
    {
        var script = System.IO.Path.Combine(_module.Files.Utils, "test zapret.ps1");
        if (!System.IO.File.Exists(script))
        {
            await Dialog.AlertAsync("Тесты", "В этой версии zapret нет utils\\test zapret.ps1.");
            return;
        }
        if (_module.Status.IsActive)
        {
            var go = await Dialog.ConfirmAsync("Запустить тесты?",
                "Тесты сами запускают стратегии по очереди, поэтому zapret будет остановлен. После тестов запустите его снова с лучшей стратегией.", "Остановить и запустить тесты");
            if (!go) return;
        }
        await UiKit.RunAsync(async () =>
        {
            if (_module.Status.IsActive) await _module.StopAsync(StopReason.User, CancellationToken.None);
            // Тест отказывается работать при установленной службе zapret.
            await _module.RemoveServiceAsync(CancellationToken.None);
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = _module.Files.Root,
            });
        });
    }
}
