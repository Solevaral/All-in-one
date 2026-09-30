using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using AllInOne.Core;
using AllInOne.Core.Install;
using AllInOne.Core.Modules;
using AllInOne.Host.Interop;
using AllInOne.Host.UI;
using AllInOne.Sdk;
using AllInOne.Ui;

namespace AllInOne.Host;

public partial class App : Application, IHostUi
{
#if DEBUG
    // Отладочная сборка не мешает установленному All in One: свои имена мьютекса и сигналов.
    private const string InstanceSuffix = ".Dev";
#else
    private const string InstanceSuffix = "";
#endif
    private const string SingleInstanceName = @"Local\AllInOne.SingleInstance" + InstanceSuffix;
    private const string ShowSignalName = @"Local\AllInOne.Show" + InstanceSuffix;
    private const string ExitSignalName = @"Local\AllInOne.Exit" + InstanceSuffix;
    private const string StopAllSignalName = @"Local\AllInOne.StopAll" + InstanceSuffix;

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _exitSignal;
    private EventWaitHandle? _stopAllSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    internal new static App Current => (App)Application.Current;

    internal ModuleManager Manager { get; private set; } = null!;

    /// <summary>Идёт выход: окно закрывается по-настоящему, а не сворачивается в трей.</summary>
    internal bool IsExiting => _exiting;

    /// <summary>Ход длительных операций (установка, обновление) по id модуля — для страниц.</summary>
    internal Dictionary<string, InstallProgress> Progress { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal event EventHandler<string>? ProgressChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();

        _mutex = new Mutex(initiallyOwned: true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Уже запущен: команды установщика (--exit, --stop-all) передаются работающему экземпляру,
            // иначе он просто показывает окно.
            var signalName = args.Contains("--stop-all") ? StopAllSignalName : args.Contains("--exit") ? ExitSignalName : ShowSignalName;
            if (EventWaitHandle.TryOpenExisting(signalName, out var signal)) signal.Set();
            if (signalName != ShowSignalName) WaitForFirstInstanceExit(TimeSpan.FromMinutes(2));
            Shutdown();
            return;
        }

        if (args.Contains("--exit"))
        {
            // Установщик закрывает All in One перед обновлением — а он и не запущен.
            Shutdown();
            return;
        }
        var autostart = args.Contains("--autostart");

        AppPaths.EnsureCreated();
        Log.Info($"Запуск All in One {RuntimeInfo.HostVersionText} ({RuntimeInfo.Flavor}), {AppPaths.Root}, аргументы: {string.Join(' ', e.Args)}");

        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Error("Необработанная ошибка UI", ev.Exception);
            ev.Handled = true;
            _ = Dialog.AlertAsync("All in One", "Ошибка: " + ev.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Необработанная ошибка", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Незамеченная ошибка задачи", ev.Exception); ev.SetObserved(); };

        base.OnStartup(e);

        if (args.Contains("--post-update")) _ = HostUpdater.CleanupAfterUpdateAsync();

        Manager = new ModuleManager(this, ReadBuiltinCatalog(), ModuleFactories.All());
        Manager.Load();

        if (args.Contains("--stop-all"))
        {
            // Удаление программы: подключиться к работающим модулям, остановить их и выйти — без окна и трея.
            await StopAllAndExitAsync();
            return;
        }
        Manager.EntryStatusChanged += (_, _) => UpdateTray();
        Manager.EntriesChanged += (_, _) => UpdateTray();

        _tray = new TrayIcon();
        _tray.LeftClick += (_, _) => ShowMainWindow();
        _tray.RightClick += (_, _) => ShowTrayMenu();
        _tray.BalloonClick += (_, _) => ShowMainWindow();
        UpdateTray();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        _stopAllSignal = new EventWaitHandle(false, EventResetMode.AutoReset, StopAllSignalName);
        new Thread(() =>
        {
            var handles = new WaitHandle[] { _showSignal, _exitSignal, _stopAllSignal };
            while (true)
            {
                switch (WaitHandle.WaitAny(handles))
                {
                    case 0: Dispatcher.BeginInvoke(ShowMainWindow); break;
                    // Установщик обновляет All in One: выходим, модули остаются работать.
                    case 1: Dispatcher.BeginInvoke(() => _ = ShutdownHostAsync()); return;
                    // Удаление программы: останавливаем модули и выходим.
                    case 2: Dispatcher.BeginInvoke(() => _ = StopAllAndExitAsync()); return;
                }
            }
        }) { IsBackground = true, Name = "AllInOne.Signal" }.Start();

        if (!autostart && !Manager.Settings.StartMinimized) ShowMainWindow();

        await Manager.StartupAsync();

#if DEBUG
        await RunDevScenarioAsync(e.Args);
#endif
    }

#if DEBUG
    /// <summary>
    /// Отладочный сценарий: --dev-install id1,id2 ставит модули из каталога, --dev-screenshots &lt;папка&gt;
    /// снимает страницы, --dev-exit завершает каркас (модули бережно останавливаются).
    /// </summary>
    private async Task RunDevScenarioAsync(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        if (Arg("--dev-install") is { } ids)
        {
            foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Manager.Find(id) is not { IsInstalled: false } entry) continue;
                try { await Manager.InstallAsync(entry, new Progress<InstallProgress>(p => Log.Info($"[dev] {id}: {p.Stage} {p.Fraction:P0}"))); }
                catch (Exception ex) { Log.Error($"[dev] установка {id}", ex); }
            }
        }

        if (Arg("--dev-cycle") is { } cycleIds)
        {
            // Запуск → снимок страницы → бережная остановка, с записью статусов в лог.
            ShowMainWindow();
            var shotDir = Arg("--dev-screenshots");
            if (shotDir is not null) System.IO.Directory.CreateDirectory(shotDir);
            foreach (var id in cycleIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Manager.Find(id) is not { IsInstalled: true } entry) continue;
                try
                {
                    await Manager.StartAsync(entry);
                    Log.Info($"[dev] {id} после запуска: {entry.Module.Status}");
                    await Task.Delay(8000);
                    await Manager.RefreshAllAsync();
                    Log.Info($"[dev] {id} через 8 с: {entry.Module.Status}");
                    if (shotDir is not null)
                    {
                        _window!.Navigate(id);
                        await Task.Delay(1500);
                        DevScreenshots.Save(_window, System.IO.Path.Combine(shotDir, id + "-running.png"));
                    }
                    await Manager.StopAsync(entry);
                    Log.Info($"[dev] {id} после остановки: {entry.Module.Status}");
                    for (var i = 1; i <= 3; i++)
                    {
                        await Task.Delay(2000);
                        await Manager.RefreshAllAsync();
                        Log.Info($"[dev] {id} через {i * 2} с после остановки: {entry.Module.Status}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[dev] цикл {id}", ex);
                }
            }
        }

        if (Arg("--dev-screenshots") is { } dir)
        {
            ShowMainWindow();
            var pages = new List<string> { "home", "catalog", "updates", "settings" };
            pages.AddRange(Manager.Installed.Select(m => m.Id));
            await DevScreenshots.RunAsync(_window!, dir, pages);
            Log.Info($"[dev] снимки сохранены в {dir}");
        }

        if (args.Contains("--dev-exit"))
        {
            await Manager.StopAllModulesAsync(StopReason.HostExit, null, CancellationToken.None);
            await ShutdownHostAsync();
        }
    }
#endif

    private async Task StopAllAndExitAsync()
    {
        _exiting = true;
        try
        {
            await Manager.RefreshAllAsync();
            await Manager.StopAllModulesAsync(StopReason.Uninstall, exceptId: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("Остановка модулей перед удалением", ex);
        }
        await ShutdownHostAsync();
    }

    /// <summary>Ждёт, пока первый экземпляр освободит мьютекс (закроется).</summary>
    private static void WaitForFirstInstanceExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!Mutex.TryOpenExisting(SingleInstanceName, out var existing)) return;
                existing.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
            }
            Thread.Sleep(300);
        }
    }

    private static string ReadBuiltinCatalog()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("AllInOne.builtin-catalog.json");
        if (stream is null) return "{}";
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal void ShowMainWindow()
    {
        if (_exiting) return;
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
            MainWindow = _window;
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    internal void ShowModule(string id)
    {
        ShowMainWindow();
        _window?.Navigate(id);
    }

    internal void ReportProgress(string moduleId, InstallProgress? progress)
    {
        if (progress is null) Progress.Remove(moduleId);
        else Progress[moduleId] = progress;
        ProgressChanged?.Invoke(this, moduleId);
    }

    // ---------- трей ----------

    private void UpdateTray()
    {
        if (_tray is null) return;
        var installed = Manager.Installed.ToList();
        var running = installed.Count(e => e.Module.Status.State == ModuleState.Running);
        var errors = installed.Count(e => e.Module.Status.State == ModuleState.Error);
        var tip = $"All in One — работает {running} из {installed.Count}";
        if (errors > 0) tip += $", ошибок: {errors}";
        _tray.Update(tip, attention: errors > 0);
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        var open = new MenuItem { Header = "Открыть All in One", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        var installed = Manager.Installed.ToList();
        if (installed.Count > 0) menu.Items.Add(new Separator());
        foreach (var entry in installed)
        {
            if (entry.Context.Manifest.IsBuiltIn) continue;
            var active = entry.Module.Status.IsActive;
            var item = new MenuItem { Header = entry.Name, IsChecked = active, IsEnabled = !entry.IsBusy };
            item.Click += (_, _) => _ = UiKit.RunAsync(() => active ? Manager.StopAsync(entry) : Manager.StartAsync(entry), entry.Name);
            menu.Items.Add(item);
        }

        // Быстрые действия встроенных модулей (таймер выключения).
        foreach (var entry in installed.Where(e => e.Context.Manifest.IsBuiltIn && e.Module.Actions.Count > 0))
        {
            menu.Items.Add(new Separator());
            foreach (var action in entry.Module.Actions)
            {
                var item = new MenuItem { Header = action.Title };
                item.Click += (_, _) => _ = UiKit.RunAsync(action.Execute, entry.Name);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => _ = ExitAsync();
        menu.Items.Add(exit);

        menu.Opened += (_, _) =>
        {
            // Без активации меню не закрывается по клику мимо него.
            if (PresentationSource.FromVisual(menu) is HwndSource source)
                NativeMethods.SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    // ---------- выход ----------

    /// <summary>
    /// Выход из каркаса. Модули либо бережно останавливаются, либо остаются работать
    /// (каркас подключится к ним при следующем запуске) — по настройке или по ответу пользователя.
    /// </summary>
    internal async Task ExitAsync()
    {
        if (_exiting) return;

        var active = Manager.Installed.Where(e => e.Module.Status.IsActive && !e.Context.Manifest.IsBuiltIn).ToList();
        var stopModules = Manager.Settings.OnExit == ExitBehavior.StopModules;
        if (active.Count > 0 && Manager.Settings.OnExit == ExitBehavior.Ask)
        {
            var names = string.Join(", ", active.Select(a => a.Name));
            var choice = await Dialog.ShowAsync("Выход из All in One",
                $"Запущены: {names}.\n\nОставленные модули продолжат работать, All in One подключится к ним при следующем запуске.",
                "Остановить и выйти", "Выйти, оставив модули", "Отмена");
            if (choice is < 0 or 2) return;
            stopModules = choice == 0;
        }

        _exiting = true;
        if (stopModules && active.Count > 0)
        {
            try
            {
                await Manager.StopAllModulesAsync(StopReason.HostExit, exceptId: null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                var go = await Dialog.ConfirmAsync("Не все модули остановились", ex.Message + "\n\nВсё равно выйти?", "Выйти", "Остаться");
                if (!go)
                {
                    _exiting = false;
                    return;
                }
            }
        }

        await ShutdownHostAsync();
    }

    /// <summary>Завершает каркас без вопросов (после самообновления или подтверждённого выхода).</summary>
    internal async Task ShutdownHostAsync()
    {
        _exiting = true;
        Log.Info("Выход из All in One");
        _window?.Close();
        _tray?.Dispose();
        await Manager.DisposeAsync();
        try { _mutex?.ReleaseMutex(); }
        catch (ApplicationException) { }   // не тот поток — мьютекс освободится вместе с процессом
        Shutdown();
    }

    // ---------- IHostUi ----------

    public void Notify(string title, string text) =>
        Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(title, text));

    public async Task<ForceStopDecision> ConfirmForceStopAsync(string moduleName, string details)
    {
        var choice = await Dialog.ShowAsync($"«{moduleName}» не отвечает", details,
            "Подождать ещё", "Завершить принудительно", "Отменить");
        return choice switch
        {
            0 => ForceStopDecision.Wait,
            1 => ForceStopDecision.Kill,
            _ => ForceStopDecision.Cancel,
        };
    }

    public Task InvokeOnUiAsync(Action action) => Dispatcher.InvokeAsync(action).Task;
}
