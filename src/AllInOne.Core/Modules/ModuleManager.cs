using AllInOne.Core.Catalog;
using AllInOne.Core.GitHub;
using AllInOne.Core.Install;
using AllInOne.Sdk;

namespace AllInOne.Core.Modules;

/// <summary>Создаёт модуль нужного вида: adapter:zapret, builtin:shutdown-timer и т. п.</summary>
public interface IModuleFactory
{
    string Kind { get; }

    ModuleBase Create(ModuleContext context);
}

/// <summary>Модуль в списке каркаса: установленный или доступный в каталоге.</summary>
public sealed class ModuleEntry(ModuleContext context, ModuleBase module, ModuleUserState userState)
{
    public ModuleContext Context { get; } = context;
    public ModuleBase Module { get; } = module;
    public ModuleUserState UserState { get; } = userState;
    public CatalogItem? CatalogItem { get; set; }

    public string Id => Context.Manifest.Id;
    public string Name => Context.Manifest.Name;
    public bool IsInstalled => Context.IsInstalled;

    /// <summary>Последняя версия в релизах, если она новее установленной.</summary>
    public string? AvailableUpdate =>
        IsInstalled && SemVer.IsNewer(UserState.LatestKnown, Context.Manifest.Version) ? UserState.LatestKnown : null;

    /// <summary>Модулю можно ставить обновления (есть источник релизов).</summary>
    public bool HasReleases => Context.Manifest.Source is { Type: "github" };

    /// <summary>Идёт операция (установка, обновление, запуск, остановка) — кнопки блокируются.</summary>
    public bool IsBusy { get; internal set; }

    public string? LastError { get; internal set; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed class ModuleManager : IHostServices, IAsyncDisposable
{
    private readonly IHostUi _ui;
    private readonly Dictionary<string, IModuleFactory> _factories;
    private readonly List<ModuleEntry> _entries = [];
    private readonly CancellationTokenSource _cts = new();

    public ModuleManager(IHostUi ui, string builtinCatalogJson, IEnumerable<IModuleFactory> factories)
    {
        _ui = ui;
        _factories = factories.ToDictionary(f => f.Kind, StringComparer.OrdinalIgnoreCase);

        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"AllInOne/{RuntimeInfo.HostVersionText}");

        Settings = HostSettings.Load();
        States = ModuleStateStore.Load();
        GitHub = new GitHubReleasesClient(Http);
        Installer = new ModuleInstaller(GitHub);
        Catalog = new CatalogService(Http, builtinCatalogJson, RuntimeInfo.HostVersion);
    }

    public HttpClient Http { get; }
    public HostSettings Settings { get; }
    public ModuleStateStore States { get; }
    public GitHubReleasesClient GitHub { get; }
    public ModuleInstaller Installer { get; }
    public CatalogService Catalog { get; }

    public Version HostVersion => RuntimeInfo.HostVersion;

    public IReadOnlyList<ModuleEntry> Entries => _entries;

    public IEnumerable<ModuleEntry> Installed => _entries.Where(e => e.IsInstalled);

    /// <summary>Состав или состояние списка изменились (установка, обновление, каталог).</summary>
    public event EventHandler? EntriesChanged;

    /// <summary>Статус конкретного модуля изменился.</summary>
    public event EventHandler<ModuleEntry>? EntryStatusChanged;

    // ---------- сборка списка ----------

    /// <summary>Собирает список из установленных модулей и каталога (без сети).</summary>
    public void Load()
    {
        Catalog.LoadOffline();
        Rebuild();
        Catalog.Changed += (_, _) => _ = InvokeOnUiAsync(Rebuild);
    }

    private void Rebuild()
    {
        var installed = new Dictionary<string, ModuleManifest>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(AppPaths.Modules))
        {
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.Modules))
            {
                var manifest = JsonFile.Read<ModuleManifest>(Path.Combine(dir, "module.json"));
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id)) installed[manifest.Id] = manifest;
            }
        }

        var ids = installed.Keys.Concat(Catalog.Items.Select(i => i.Manifest.Id)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var catalogItem = Catalog.Find(id);
            var existing = _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.CatalogItem = catalogItem;
                continue;
            }

            var manifest = installed.TryGetValue(id, out var m) ? m : catalogItem!.Manifest.Clone();
            if (!TryCreate(manifest, out var entry)) continue;
            entry.CatalogItem = catalogItem;
            _entries.Add(entry);
        }

        // Порядок как в каталоге, затем установленные вручную.
        var order = Catalog.Items.Select((item, i) => (item.Manifest.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);
        _entries.Sort((a, b) => (order.GetValueOrDefault(a.Id, int.MaxValue), a.Name).CompareTo((order.GetValueOrDefault(b.Id, int.MaxValue), b.Name)));

        RaiseEntries();
    }

    private bool TryCreate(ModuleManifest manifest, out ModuleEntry entry)
    {
        entry = null!;
        var context = new ModuleContext(manifest, _ui, this, Http);
        ModuleBase module;
        if (manifest.IsExternal)
        {
            module = new ExternalModule(context);
        }
        else if (_factories.TryGetValue(manifest.Kind, out var factory))
        {
            module = factory.Create(context);
        }
        else
        {
            Log.Warn($"Модуль {manifest.Id}: вид «{manifest.Kind}» не поддерживается этой версией каркаса");
            return false;
        }

        entry = new ModuleEntry(context, module, States.Get(manifest.Id));
        var captured = entry;
        module.StatusChanged += (_, _) => RaiseStatus(captured);
        module.UnexpectedExit += (_, _) => OnUnexpectedExit(captured);
        return true;
    }

    private void RaiseStatus(ModuleEntry entry) => Safe(() => EntryStatusChanged?.Invoke(this, entry));

    private void RaiseEntries() => Safe(() => EntriesChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>Ошибка в подписчике (интерфейсе) не должна срывать установку или остановку модуля.</summary>
    private static void Safe(Action raise)
    {
        try { raise(); }
        catch (Exception ex) { Log.Error("Ошибка в обработчике события интерфейса", ex); }
    }

    public ModuleEntry? Find(string id) => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---------- жизненный цикл каркаса ----------

    /// <summary>
    /// Старт каркаса: применить отложенные автообновления (до запуска модулей),
    /// затем запустить модули с автозапуском (или просто подключиться к уже работающим).
    /// </summary>
    public async Task StartupAsync()
    {
        await RefreshAllAsync();

        foreach (var entry in Installed.Where(e => e.UserState.PendingUpdate is not null).ToList())
        {
            if (entry.Module.Status.IsActive)
            {
                // Модуль уже работает (каркас перезапускался без остановки модулей) — не рвём его.
                continue;
            }
            try { await RunAsync(entry, "Обновление", ct => UpdateCoreAsync(entry, null, ct), raiseChanged: true); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _ui.Notify(entry.Name, "Отложенное обновление не удалось: " + ex.Message); }
        }

        // «Запускать вместе с каркасом» — при любом старте каркаса, не только из автозагрузки Windows.
        foreach (var entry in Installed.Where(e => e.UserState.Autostart && !e.Module.Status.IsActive).ToList())
        {
            try { await StartAsync(entry); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _ui.Notify(entry.Name, "Не запустился: " + ex.Message); }
        }

        _ = Task.Run(() => PollLoopAsync(_cts.Token));
        _ = Task.Run(() => UpdateLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await InvokeOnUiAsync(() => _ = RefreshAllAsync());
        }
    }

    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                await InvokeOnUiAsync(() => _ = CheckUpdatesAsync(userInitiated: false));
                var hours = Math.Max(1, Settings.UpdateCheckHours);
                await Task.Delay(TimeSpan.FromHours(Settings.UpdateCheckHours <= 0 ? 24 * 365 : hours), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RefreshAllAsync()
    {
        foreach (var entry in Installed.Where(e => !e.IsBusy).ToList())
        {
            try { await entry.Module.RefreshAsync(_cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"{entry.Id}: опрос — {ex.Message}"); }
        }
    }

    // ---------- операции над модулем ----------

    public Task StartAsync(ModuleEntry entry) =>
        RunAsync(entry, "Запуск", async ct =>
        {
            RemoveLegacyAutostart(entry);
            await entry.Module.StartAsync(ct);
        });

    public Task StopAsync(ModuleEntry entry, StopReason reason = StopReason.User) =>
        RunAsync(entry, "Остановка", ct => entry.Module.StopAsync(reason, ct));

    public Task RestartAsync(ModuleEntry entry) =>
        RunAsync(entry, "Перезапуск", async ct =>
        {
            await entry.Module.StopAsync(StopReason.Restart, ct);
            await entry.Module.StartAsync(ct);
        });

    public Task InstallAsync(ModuleEntry entry, IProgress<InstallProgress>? progress) =>
        RunAsync(entry, "Установка", async ct =>
        {
            var source = entry.CatalogItem?.Manifest ?? entry.Context.Manifest;
            if (entry.CatalogItem?.RequiresHostUpdate == true)
                throw new InvalidOperationException("Этому модулю нужна более новая версия каркаса. Обновите All-in-one.");
            await Installer.InstallFromCatalogAsync(entry.Module, entry.Context, source, progress, ct);
            entry.UserState.LatestKnown = entry.Context.Manifest.Version;
            entry.UserState.PendingUpdate = null;
            States.Save();
            RemoveLegacyAutostart(entry);
        }, raiseChanged: true);

    public Task UpdateAsync(ModuleEntry entry, IProgress<InstallProgress>? progress) =>
        RunAsync(entry, "Обновление", ct => UpdateCoreAsync(entry, progress, ct), raiseChanged: true);

    private async Task UpdateCoreAsync(ModuleEntry entry, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        // Берём свежий манифест из каталога (адрес мог поменяться), но только если он того же вида.
        var source = entry.CatalogItem?.Manifest is { } cm && cm.Kind == entry.Context.Manifest.Kind ? cm : entry.Context.Manifest;
        await Installer.InstallFromCatalogAsync(entry.Module, entry.Context, source, progress, ct);
        entry.UserState.LatestKnown = entry.Context.Manifest.Version;
        entry.UserState.PendingUpdate = null;
        States.Save();
    }

    public Task InstallFromZipAsync(string zipPath, IProgress<InstallProgress>? progress)
    {
        var manifest = ModuleInstaller.ReadManifestFromZip(zipPath);
        var entry = Find(manifest.Id);
        if (entry is null)
        {
            if (!TryCreate(manifest, out entry))
                throw new InvalidOperationException($"Вид модуля «{manifest.Kind}» не поддерживается этой версией каркаса.");
            _entries.Add(entry);
        }
        return RunAsync(entry, "Установка", ct => Installer.InstallFromZipAsync(entry.Module, entry.Context, zipPath, progress, ct), raiseChanged: true);
    }

    public Task UninstallAsync(ModuleEntry entry, bool removeData) =>
        RunAsync(entry, "Удаление", async ct =>
        {
            await Installer.UninstallAsync(entry.Module, entry.Context, removeData, ct);
            entry.UserState.Autostart = false;
            entry.UserState.PendingUpdate = null;
            States.Save();
            if (entry.CatalogItem is null)
            {
                _entries.Remove(entry);
                await entry.Module.DisposeAsync();
            }
        }, raiseChanged: true);

    /// <summary>
    /// Выполняет операцию над модулем по одной за раз. Ошибка сохраняется в LastError и пробрасывается
    /// вызывающему (UI покажет её), отмена пользователем — тоже пробрасывается.
    /// </summary>
    private async Task RunAsync(ModuleEntry entry, string operation, Func<CancellationToken, Task> action, bool raiseChanged = false)
    {
        if (!await entry.Gate.WaitAsync(0))
            throw new InvalidOperationException($"С модулем «{entry.Name}» уже выполняется операция.");
        entry.IsBusy = true;
        entry.LastError = null;
        RaiseStatus(entry);
        try
        {
            await action(_cts.Token);
        }
        catch (StopCancelledException ex)
        {
            Log.Info($"{entry.Id}: {operation} отменена — {ex.Message}");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entry.LastError = ex.Message;
            Log.Error($"{entry.Id}: {operation} — ошибка", ex);
            throw;
        }
        finally
        {
            entry.IsBusy = false;
            entry.Gate.Release();
            RaiseStatus(entry);
            if (raiseChanged) RaiseEntries();
        }
    }

    private void RemoveLegacyAutostart(ModuleEntry entry)
    {
        if (entry.Context.Manifest.LegacyAutostart?.RunKey is not { } name) return;
        if (LegacyAutostart.Get(name) is null) return;
        LegacyAutostart.Remove(name);
        Log.Info($"{entry.Id}: убран собственный автозапуск «{name}» — теперь модуль запускает каркас");
    }

    private void OnUnexpectedExit(ModuleEntry entry)
    {
        Log.Warn($"{entry.Id}: процесс завершился сам");
        if (!entry.UserState.RestartOnCrash || entry.IsBusy) return;
        _ = InvokeOnUiAsync(async () =>
        {
            try { await StartAsync(entry); }
            catch (Exception ex) { _ui.Notify(entry.Name, "Не удалось перезапустить: " + ex.Message); }
        });
    }

    // ---------- обновления ----------

    public event EventHandler? UpdatesChecked;

    public bool IsCheckingUpdates { get; private set; }

    /// <summary>
    /// Проверяет релизы всех установленных модулей. Автообновление: остановленный модуль
    /// обновляется сразу, работающий — откладывается до следующего запуска каркаса.
    /// </summary>
    public async Task CheckUpdatesAsync(bool userInitiated)
    {
        if (IsCheckingUpdates) return;
        IsCheckingUpdates = true;
        var errors = new List<string>();
        try
        {
            await Catalog.RefreshAsync(Settings.CatalogUrl, _cts.Token);

            foreach (var entry in Installed.Where(e => e.HasReleases).ToList())
            {
                try
                {
                    var choice = await Installer.ResolveAsync(entry.Context.Manifest, _cts.Token);
                    if (choice is null) continue;
                    entry.UserState.LatestKnown = choice.Version;
                }
                catch (Exception ex) when (ex is GitHubException or HttpRequestException or TaskCanceledException)
                {
                    errors.Add($"{entry.Name}: {ex.Message}");
                }
            }

            Settings.LastUpdateCheck = DateTime.Now;
            Settings.Save();

            foreach (var entry in Installed.Where(e => e.AvailableUpdate is not null).ToList())
            {
                if (!entry.UserState.AutoUpdate)
                {
                    if (!userInitiated) _ui.Notify("Доступно обновление", $"{entry.Name} {entry.AvailableUpdate}");
                    continue;
                }

                if (entry.Module.Status.IsActive)
                {
                    if (entry.UserState.PendingUpdate != entry.AvailableUpdate)
                    {
                        entry.UserState.PendingUpdate = entry.AvailableUpdate;
                        _ui.Notify("Обновление отложено",
                            $"{entry.Name} {entry.AvailableUpdate} будет установлено при следующем запуске каркаса. Чтобы обновить сейчас, откройте «Обновления».");
                    }
                    continue;
                }

                try
                {
                    await RunAsync(entry, "Автообновление", ct => UpdateCoreAsync(entry, null, ct), raiseChanged: true);
                    _ui.Notify("Модуль обновлён", $"{entry.Name} {entry.Context.Manifest.Version}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{entry.Name}: {ex.Message}");
                }
            }

            States.Save();
        }
        finally
        {
            IsCheckingUpdates = false;
            LastCheckErrors = errors;
            Safe(() => UpdatesChecked?.Invoke(this, EventArgs.Empty));
            RaiseEntries();
        }
    }

    public IReadOnlyList<string> LastCheckErrors { get; private set; } = [];

    // ---------- IHostServices ----------

    public async Task StopAllModulesAsync(StopReason reason, string? exceptId, CancellationToken ct)
    {
        // VPN — первым: он держит системный прокси, остальным это не мешает.
        var active = Installed
            .Where(e => e.Module.Status.IsActive && !string.Equals(e.Id, exceptId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Context.Manifest.Category == "Сеть" ? 0 : 1)
            .ToList();

        var failures = new List<string>();
        foreach (var entry in active)
        {
            try
            {
                await RunAsync(entry, "Остановка", c => entry.Module.StopAsync(reason, c));
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("Не все модули остановились:\n" + string.Join("\n", failures));
    }

    public Task InvokeOnUiAsync(Action action) => _ui.InvokeOnUiAsync(action);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        foreach (var entry in _entries) await entry.Module.DisposeAsync();
        Http.Dispose();
    }
}
