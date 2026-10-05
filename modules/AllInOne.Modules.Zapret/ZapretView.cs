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
    private readonly ContentControl _conflictCard = new();
    private readonly ContentControl _strategyCard = new();
    private readonly ContentControl _gameCard = new();
    private readonly ContentControl _gamesCard = new();
    private readonly ContentControl _ipsetCard = new();
    private readonly ContentControl _listsCard = new();
    private readonly ContentControl _fakesCard = new();
    private readonly ContentControl _diagCard = new();
    private IReadOnlyList<DiagnosticCheck>? _checks;
    private bool _diagRunning;
    private bool _pendingRestart;

    /// <summary>Меняли стратегию или Game Filter, а сравнить с запущенным winws нельзя.</summary>
    private bool _argsTouched;

    public ZapretView(ZapretModule module)
    {
        _module = module;
        var body = new StackPanel();
        body.Children.Add(_conflictCard);
        body.Children.Add(_restartBanner);
        body.Children.Add(_strategyCard);
        body.Children.Add(_gamesCard);
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
        if (!_module.Status.IsActive) _pendingRestart = _argsTouched = false;
        BuildRestartBanner();
        BuildConflict();
    });

    /// <summary>winws.exe держит служба отдельно установленного zapret — удаление этой службы.</summary>
    private void BuildConflict()
    {
        if (_module.ForeignService is not { } service)
        {
            _conflictCard.Content = null;
            return;
        }
        _conflictCard.Content = UiKit.Card(
            UiKit.Section("Отдельная копия zapret"),
            UiKit.Hint($"winws.exe запускает служба Windows «{service}». Пока она работает, zapret из All in One не запустится."),
            UiKit.Buttons(UiKit.AccentButton($"Удалить службу «{service}»", () => _ = UiKit.RunAsync(async () =>
            {
                var go = await Dialog.ConfirmAsync($"Удалить службу «{service}»?",
                    "Служба будет остановлена и удалена, её winws.exe завершён. Файлы той программы останутся на диске.",
                    "Удалить", "Отмена");
                if (!go) return;
                await _module.RemoveForeignServiceAsync(service, CancellationToken.None);
                await _module.RefreshAsync(CancellationToken.None);
            }))));
    }

    private void BuildAll()
    {
        BuildRestartBanner();
        BuildConflict();
        BuildStrategy();
        BuildGames();
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

    /// <summary>
    /// Сменили стратегию или Game Filter. Нужен ли перезапуск, решает сравнение с аргументами
    /// запущенного winws: вернули прежнее значение — баннер пропадает.
    /// </summary>
    private void ArgsChanged()
    {
        _argsTouched = Running;
        BuildRestartBanner();
    }

    private bool NeedsRestart =>
        Running && (_pendingRestart || (_argsTouched && (_module.RunningArgsDiffer() ?? true)));

    private void BuildRestartBanner()
    {
        _restartBanner.Content = NeedsRestart
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
        _argsTouched = false;
        BuildRestartBanner();
    }

    // ---------- стратегия и режим ----------

    private void BuildStrategyCore()
    {
        var strategies = _module.Files.Strategies();
        var current = _module.CurrentStrategy;
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Стратегия"));
        panel.Children.Add(UiKit.Hint("Подбор стратегии под провайдера — тестами внизу страницы."));

        panel.Children.Add(UiKit.Row("Стратегия", UiKit.Combo(strategies.Select(s => (s, s)), current ?? "", s =>
        {
            _module.Settings.Strategy = s;
            _module.SaveSettings();
            ArgsChanged();
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
            ? "Служба Windows «zapret»: работает без All in One, стартует вместе с Windows (как Install Service в service.bat). «Остановить» переводит службу на ручной запуск, «Запустить» возвращает автозапуск."
            : "winws.exe запускает All in One. Запуск при входе в Windows — автозапуск All in One и галочка «Запускать вместе с All in One»."));

        panel.Children.Add(UiKit.Buttons(
            UiKit.Button("Запустить в окне (отладка)", () => _ = UiKit.RunAsync(async () =>
            {
                if (_module.Status.IsActive) await _module.StopAsync(StopReason.Restart, CancellationToken.None);
                _module.RunInConsole();
            }), tooltip: "winws в видимой консоли с выводом ошибок. Закрытие окна останавливает winws."),
            UiKit.Button("Удалить службу Windows", () => _ = UiKit.RunAsync(async () =>
            {
                if (ZapretModule.ReadServiceImagePath() is not { } image)
                {
                    await Dialog.AlertAsync("Служба zapret", "Служба не установлена.");
                    return;
                }
                // Чужая служба (zapret, установленный отдельно) — удаляем только с явного согласия.
                var own = _module.IsOurService();
                var go = await Dialog.ConfirmAsync("Удалить службу «zapret»?",
                    own
                        ? "Служба будет остановлена и удалена."
                        : $"Служба установлена не этим модулем, а из другой папки:\n{image}\n\nСлужба будет остановлена и удалена, файлы той копии zapret останутся.",
                    "Удалить", "Отмена");
                if (!go) return;
                await _module.RemoveServiceAsync(CancellationToken.None);
                await _module.RefreshAsync(CancellationToken.None);
            })),
            UiKit.Button("Папка zapret", () => UiKit.OpenFolder(_module.Files.Root))));

        _strategyCard.Content = UiKit.Card(panel);
    }

    // ---------- ошибки файлов ----------

    private void BuildStrategy() => SafeCard(_strategyCard, "Стратегия", BuildStrategyCore);

    private void BuildGames() => SafeCard(_gamesCard, "Игры", BuildGamesCore);

    private void BuildGameFilter() => SafeCard(_gameCard, "Game Filter", BuildGameFilterCore);

    private void BuildIpset() => SafeCard(_ipsetCard, "IPSet", BuildIpsetCore);

    private void BuildLists() => SafeCard(_listsCard, "Мои списки", BuildListsCore);

    private void BuildFakes() => SafeCard(_fakesCard, "Фейки", BuildFakesCore);

    private void BuildDiagnostics() => SafeCard(_diagCard, "Диагностика", BuildDiagnosticsCore);

    /// <summary>
    /// Строит карточку; если файлы zapret не читаются (нет файла, нет доступа, занят антивирусом),
    /// вместо карточки — текст ошибки и «Повторить», остальная страница работает.
    /// </summary>
    private static void SafeCard(ContentControl target, string title, Action build)
    {
        try
        {
            build();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.IO.InvalidDataException)
        {
            target.Content = UiKit.Card(
                UiKit.Section(title),
                UiKit.Text(ex.Message).With(t => t.Foreground = UiKit.Brush("Bad")),
                UiKit.Buttons(UiKit.Button("Повторить", () => SafeCard(target, title, build))));
        }
    }

    // ---------- фиксы для игр ----------

    private bool _gamesRefreshed;

    /// <summary>
    /// Карточка «Игры»: только включённые фиксы плашками; весь список — в окне выбора с поиском,
    /// чтобы страница не росла вместе с набором игр. Фикс, прописанный вручную, тоже считается включённым.
    /// </summary>
    private void BuildGamesCore()
    {
        var games = _module.Games;
        var now = ZapretGames.Read(_module.Files);
        var enabled = games.Games.Where(g => games.IsOn(now, g)).ToList();

        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Игры"));
        panel.Children.Add(UiKit.Hint(
            "Фикс выставляет Game Filter и IPSet и дописывает строки в «Мои списки»; снятие убирает только добавленное им. " +
            "Работа игры не гарантирована: блокировки различаются у провайдеров и меняются. " +
            "Рассчитано на стандартные настройки VPN-клиента: TryToCatchMe в режиме системного прокси, без TUN."));

        if (enabled.Count == 0)
        {
            panel.Children.Add(UiKit.Hint("Фиксы не включены.").With(t => t.Margin = new Thickness(0, 8, 0, 0)));
        }
        else
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var game in enabled)
                chips.Children.Add(GameChip(game, games.IsManual(now, game)));
            panel.Children.Add(chips);
        }

        panel.Children.Add(UiKit.Buttons(
            UiKit.AccentButton("Выбрать игры…", () => _ = ShowGamesDialogAsync()),
            UiKit.Button("Обновить список", () => _ = UiKit.RunAsync(async () =>
            {
                await _module.Games.RefreshAsync(_module.Http, force: true, CancellationToken.None);
                BuildGames();
            }), tooltip: "Список игр загружается с GitHub сам, не чаще раза в 6 часов.")));

        _gamesCard.Content = UiKit.Card(panel);

        // Набор обновляется с GitHub без выпуска All in One: раз за открытие страницы, не чаще раза в 6 часов.
        if (!_gamesRefreshed)
        {
            _gamesRefreshed = true;
            _ = RefreshGamesAsync();
        }
    }

    /// <summary>Плашка включённой игры с кнопкой выключения.</summary>
    private FrameworkElement GameChip(GameFix game, bool manual)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = game.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = UiKit.Brush("Text") });
        if (manual)
            row.Children.Add(new TextBlock { Text = " · вручную", VerticalAlignment = VerticalAlignment.Center, Foreground = UiKit.Brush("SubText") });
        row.Children.Add(HelpButton(game));
        var remove = UiKit.Link("✕", () => _ = SetGameAsync(game, false));
        remove.Margin = new Thickness(8, 0, 0, 0);
        remove.ToolTip = "Выключить фикс";
        row.Children.Add(remove);
        return new Border
        {
            Background = UiKit.Brush("Control"),
            BorderBrush = UiKit.Brush("ControlStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 8),
            Child = row,
            ToolTip = game.Note,
        };
    }

    /// <summary>Кружок «?»: что включает фикс и что сделать самому.</summary>
    private static FrameworkElement HelpButton(GameFix game)
    {
        var mark = new TextBlock
        {
            Text = "?",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Brush("SubText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var circle = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            BorderBrush = UiKit.Brush("SubText"),
            BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Справка",
            Child = mark,
        };
        circle.MouseEnter += (_, _) => { circle.BorderBrush = UiKit.Brush("Accent"); mark.Foreground = UiKit.Brush("Accent"); };
        circle.MouseLeave += (_, _) => { circle.BorderBrush = UiKit.Brush("SubText"); mark.Foreground = UiKit.Brush("SubText"); };
        circle.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _ = Dialog.AlertAsync(game.Name, ZapretGames.Describe(game));
        };
        return circle;
    }

    /// <summary>Включает или выключает фикс. Прописанный вручную — только после подтверждения.</summary>
    private async Task SetGameAsync(GameFix game, bool on)
    {
        try
        {
            var games = _module.Games;
            if (!on && games.IsManual(ZapretGames.Read(_module.Files), game))
            {
                var remove = await Dialog.ConfirmAsync(game.Name,
                    "Строки и режимы этого фикса прописаны вручную. Убрать их? Game Filter и IPSet станут «выключен», если их не требуют другие фиксы.",
                    "Убрать", "Оставить");
                if (!remove) return;
                games.RemoveManual(_module.Files, game);
            }
            else
            {
                games.Set(_module.Files, game.Id, on);
            }
            // Списки winws перечитывает сам; перезапуск нужен только если сменился Game Filter.
            ArgsChanged();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            await Dialog.AlertAsync("Игры", ex.Message);
        }
        finally
        {
            BuildGames();
            BuildGameFilter();
            BuildIpset();
            BuildLists();
        }
    }

    /// <summary>Окно выбора: поиск и переключатель у каждой игры; изменения применяются сразу.</summary>
    private async Task ShowGamesDialogAsync()
    {
        var games = _module.Games;
        var search = new TextBox { Margin = new Thickness(0, 12, 0, 8) };
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        void Fill()
        {
            list.Children.Clear();
            ZapretGames.Snapshot now;
            try
            {
                now = ZapretGames.Read(_module.Files);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                list.Children.Add(UiKit.Text(ex.Message).With(t => t.Foreground = UiKit.Brush("Bad")));
                return;
            }
            var needle = search.Text.Trim();
            var shown = games.Games
                .Where(g => needle.Length == 0 || g.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => games.IsOn(now, g))
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var game in shown)
            {
                var hint = games.IsManual(now, game) ? $"{game.Note} Прописано вручную.".Trim() : game.Note;
                list.Children.Add(UiKit.Toggle(game.Name, games.IsOn(now, game), on => _ = ToggleInDialogAsync(game, on, Fill), hint));
            }
            if (shown.Count == 0) list.Children.Add(UiKit.Hint("Ничего не найдено."));
        }

        search.TextChanged += (_, _) => Fill();
        Fill();

        var extra = new StackPanel();
        extra.Children.Add(search);
        extra.Children.Add(scroll);
        await Dialog.ShowAsync("Игры", "Поиск по названию. Фикс включается и выключается сразу.", extra, "Готово");
    }

    private async Task ToggleInDialogAsync(GameFix game, bool on, Action refill)
    {
        await SetGameAsync(game, on);
        refill();   // отказ убрать ручной фикс или ошибка — переключатель возвращается к фактическому состоянию
    }

    private async Task RefreshGamesAsync()
    {
        if (await _module.Games.RefreshAsync(_module.Http, force: false, CancellationToken.None))
            await Dispatcher.InvokeAsync(BuildGames);
    }

    // ---------- Game Filter ----------

    private void BuildGameFilterCore()
    {
        var gf = _module.Files.ReadGameFilter();
        var mode = gf.Mode;
        var tcp = new TextBox { Text = gf.TcpRange, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var udp = new TextBox { Text = gf.UdpRange, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };

        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Game Filter"));
        panel.Children.Add(UiKit.Hint("Обход для игр: обработка портов выше 1024. Нагружает систему, возможны конфликты с античитами."));
        // Режим и порты применяются сразу, без кнопки: запись в файл и предложение перезапустить zapret.
        void Apply(GameFilter next)
        {
            try
            {
                _module.Files.WriteGameFilter(next);
                ArgsChanged();
            }
            catch (System.IO.IOException ex)
            {
                _ = Dialog.AlertAsync("Game Filter", ex.Message);
                BuildGameFilter();
            }
        }

        void ApplyPorts(TextBox box, bool isTcp)
        {
            var current = _module.Files.ReadGameFilter();
            var valid = ZapretFiles.ValidateRange(box.Text);
            if (valid is null)
            {
                _ = Dialog.AlertAsync("Game Filter", "Неверные порты: числа от 1 до 65535, диапазоны через дефис, разделитель — запятая.");
                box.Text = isTcp ? current.TcpRange : current.UdpRange;
                return;
            }
            box.Text = valid;
            if (valid != (isTcp ? current.TcpRange : current.UdpRange))
                Apply(isTcp ? current with { TcpRange = valid } : current with { UdpRange = valid });
        }

        tcp.LostFocus += (_, _) => ApplyPorts(tcp, true);
        udp.LostFocus += (_, _) => ApplyPorts(udp, false);
        tcp.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyPorts(tcp, true); };
        udp.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyPorts(udp, false); };

        panel.Children.Add(UiKit.Row("Режим", UiKit.Combo(
            [(GameFilterMode.Disabled, "выключен"), (GameFilterMode.All, "TCP и UDP"), (GameFilterMode.Tcp, "только TCP"), (GameFilterMode.Udp, "только UDP")],
            mode, m => Apply(_module.Files.ReadGameFilter() with { Mode = m }))));
        panel.Children.Add(UiKit.Row("Порты TCP", tcp));
        panel.Children.Add(UiKit.Row("Порты UDP", udp));
        panel.Children.Add(UiKit.Row("", UiKit.Hint("Формат: 1024-65535 или 1024-1934,1936-65535. Порты применяются по Enter или при переходе к другому полю.")));
        _gameCard.Content = UiKit.Card(panel);
    }

    // ---------- IPSet ----------

    private void BuildIpsetCore()
    {
        var files = _module.Files;
        var current = files.ReadIpsetMode();
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("IPSet"));
        panel.Children.Add(UiKit.Hint("Обход по списку IP-адресов."));

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
            panel.Children.Add(UiKit.Hint("«Все адреса» обрабатывает весь трафик на отслеживаемых портах, включая сайты без блокировок."));

        panel.Children.Add(UiKit.Buttons(UiKit.Button("Обновить список IPSet", () => _ = UiKit.RunAsync(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AllInOne");
            await files.UpdateIpsetAsync(http, CancellationToken.None);
            Changed();
            BuildIpset();
        }, "Список не обновлён"), tooltip: "Список из репозитория Flowseal, режим «По списку».")));
        _ipsetCard.Content = UiKit.Card(panel);
    }

    // ---------- свои списки ----------

    private void BuildListsCore()
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
        panel.Children.Add(UiKit.Hint("Один домен или адрес на строку. Поддомены включаются автоматически, «^» в начале — точное совпадение. Сохраняются при обновлениях."));
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

    private void BuildFakesCore()
    {
        var files = _module.Files;
        var candidates = files.FakeCandidates();
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Активные фейки"));
        panel.Children.Add(UiKit.Hint("Заготовленный пакет для голосовых каналов Discord и для игр."));
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

    private void BuildDiagnosticsCore()
    {
        var panel = new StackPanel();
        panel.Children.Add(UiKit.Section("Диагностика"));
        panel.Children.Add(UiKit.Hint("Проверки из service.bat: службы, драйвер, прокси, конфликтующие программы, DoH."));

        if (_checks is not null)
        {
            foreach (var check in _checks) panel.Children.Add(CheckRow(check));
        }

        panel.Children.Add(UiKit.Buttons(
            UiKit.Button(_diagRunning ? "Проверка…" : "Проверить", () => _ = RunDiagnosticsAsync()).With(b => b.IsEnabled = !_diagRunning),
            UiKit.Button("Очистить кэш Discord", () =>
            {
                var n = ZapretDiagnostics.ClearDiscordCache();
                _ = Dialog.AlertAsync("Кэш Discord", n > 0 ? $"Очищено папок: {n}. Нужен перезапуск Discord." : "Кэш не найден или занят запущенным Discord.");
            }),
            UiKit.Button("Запустить тесты стратегий", () => _ = RunTestsAsync(),
                tooltip: "utils\\test zapret.ps1: перебор стратегий с проверкой доступности сайтов, в окне PowerShell.")));

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
                "Тесты запускают стратегии по очереди, zapret будет остановлен.", "Остановить и запустить тесты");
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
