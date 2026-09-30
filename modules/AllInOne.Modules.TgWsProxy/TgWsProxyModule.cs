using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AllInOne.Core.Modules;
using AllInOne.Core.Processes;
using AllInOne.Sdk;

namespace AllInOne.Modules.TgWsProxy;

public sealed class TgWsProxyFactory : IModuleFactory
{
    public string Kind => "adapter:tgwsproxy";

    public ModuleBase Create(ModuleContext context) => new TgWsProxyModule(context);
}

/// <summary>
/// Адаптер над готовым exe TG WS Proxy (трей-приложение на Python). IPC у программы нет,
/// поэтому состояние определяется по процессу, порту и proxy.log.
/// Программа работает в портативном режиме: папка TgWsProxy_data рядом с exe.
/// Её собственные самообновление и автозапуск выключены — этим занимается каркас.
/// Остановка — завершение дерева процессов: программа не меняет состояние системы, только слушает порт.
/// </summary>
public sealed class TgWsProxyModule(ModuleContext context) : ModuleBase(context), IModuleInstallHooks
{
    private const string ExeName = "TgWsProxy.exe";
    private DateTime? _startedAt;

    internal string ExePath => Path.Combine(Context.ProgramDir, ExeName);
    internal string DataDir => Path.Combine(Context.ProgramDir, "TgWsProxy_data");
    internal string ConfigPath => Path.Combine(DataDir, "config.json");
    internal string LogPath => Path.Combine(DataDir, "proxy.log");

    /// <summary>Последняя строка статистики из лога (раз в 60 с).</summary>
    internal ProxyStats? Stats { get; private set; }

    public override IReadOnlyList<ModuleAction> Actions =>
        Status.State == ModuleState.Running && TryBuildLink() is { } link
            ? [new ModuleAction("open", "Открыть в Telegram", () => { OpenLink(link); return Task.CompletedTask; })]
            : [];

    // ---------- конфигурация ----------

    internal JsonObject ReadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath) && JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject obj) return obj;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Context.Log.Warn("config.json не читается: " + ex.Message);
        }
        return [];
    }

    internal void WriteConfig(JsonObject config)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>
    /// Портативный режим, без самообновления и без собственного автозапуска.
    /// Остальное (секрет, порт по умолчанию) программа допишет сама при первом старте.
    /// </summary>
    private void EnsureHostedConfig()
    {
        Directory.CreateDirectory(DataDir);
        var config = ReadConfig();
        config["check_updates"] = false;
        config["autostart"] = false;
        WriteConfig(config);

        // Без этого флага при первом запуске открывается мастер — ссылку показывает каркас.
        var flag = Path.Combine(DataDir, ".first_run_done_mtproto");
        if (!File.Exists(flag)) File.WriteAllText(flag, "");
    }

    internal (string Host, int Port) Endpoint()
    {
        var c = ReadConfig();
        var host = c["host"]?.GetValue<string>() ?? "127.0.0.1";
        var port = c["port"]?.GetValue<int>() ?? 1443;
        return (host, port);
    }

    /// <summary>tg://proxy?server=…&amp;port=…&amp;secret=dd&lt;secret&gt; — как в самой программе.</summary>
    internal string? TryBuildLink()
    {
        var c = ReadConfig();
        if (c["secret"]?.GetValue<string>() is not { Length: 32 } secret) return null;
        var (host, port) = Endpoint();
        if (host == "0.0.0.0") host = "127.0.0.1";
        return $"tg://proxy?server={host}&port={port}&secret=dd{secret}";
    }

    /// <summary>Telegram, если он не запущен, стартует от имени пользователя, а не администратора.</summary>
    internal static void OpenLink(string link) => UserShell.Open(link);

    // ---------- жизненный цикл ----------

    public override async Task StartAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        if (Status.State == ModuleState.Running) return;

        if (ProcessUtil.MutexExists(@"Local\TgWsProxy_SingleInstance") && FindProcesses().Count == 0)
            throw new InvalidOperationException("TG WS Proxy запущен не из All in One. Закройте его через трей.");

        EnsureHostedConfig();
        var (_, port) = Endpoint();
        if (ProcessUtil.IsPortListening(port))
            throw new InvalidOperationException($"Порт {port} занят другой программой.");

        SetStatus(ModuleState.Starting, "Запуск…");
        _startedAt = DateTime.UtcNow;
        using (Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Context.ProgramDir })) { }

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500, ct);
            await RefreshAsync(ct);
            if (Status.State is ModuleState.Running or ModuleState.Error) return;
        }
    }

    public override async Task StopAsync(StopReason reason, CancellationToken ct)
    {
        var processes = FindProcesses();
        if (processes.Count == 0)
        {
            SetStatus(ModuleStatus.Stopped);
            return;
        }

        SetStatus(ModuleState.Stopping, "Остановка…");
        try
        {
            // PyInstaller onefile — два процесса (загрузчик и сама программа), завершаем дерево целиком.
            foreach (var p in processes) ProcessUtil.KillTree(p);
            if (!await ProcessUtil.WaitForExitAsync(processes, TimeSpan.FromSeconds(10), ct))
                throw new InvalidOperationException("TG WS Proxy не завершился.");

            var (_, port) = Endpoint();
            for (var i = 0; i < 20 && ProcessUtil.IsPortListening(port); i++) await Task.Delay(250, ct);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        _startedAt = null;
        SetStatus(ModuleStatus.Stopped);
    }

    public override async Task RefreshAsync(CancellationToken ct)
    {
        if (!Context.IsInstalled)
        {
            SetStatus(ModuleStatus.NotInstalled);
            return;
        }

        var processes = FindProcesses();
        var running = processes.Count > 0;
        foreach (var p in processes) p.Dispose();

        if (!running)
        {
            if (Status.State == ModuleState.Running)
            {
                SetStatus(ModuleState.Stopped, "Завершился");
                RaiseUnexpectedExit();
            }
            else if (Status.State is not (ModuleState.Stopping or ModuleState.Updating))
            {
                SetStatus(ModuleStatus.Stopped);
            }
            return;
        }

        var (host, port) = Endpoint();
        var listening = await ProcessUtil.ProbePortAsync(host, port, TimeSpan.FromMilliseconds(700), ct);
        ReadLogTail(out var crash);
        if (listening)
        {
            var summary = $"{(host == "0.0.0.0" ? "0.0.0.0" : "127.0.0.1")}:{port}";
            if (Stats is { } s) summary += $" · соединений {s.Active}, ↑{s.Up} ↓{s.Down}";
            SetStatus(ModuleState.Running, summary);
        }
        else if (crash is not null)
        {
            // Трей жив, а поток прокси упал — программа сама этого не покажет.
            SetStatus(ModuleState.Error, "Прокси упал", crash);
        }
        else if (_startedAt is { } started && DateTime.UtcNow - started < TimeSpan.FromSeconds(15))
        {
            SetStatus(ModuleState.Starting, "Запуск…");
        }
        else
        {
            SetStatus(ModuleState.Error, "Порт не слушается", $"Процесс работает, порт {port} закрыт. Подробности в логе.");
        }
    }

    private List<Process> FindProcesses() => ProcessUtil.Find(ExeName, ExePath);

    // ---------- лог ----------

    /// <summary>Последние строки proxy.log (файл пересоздаётся при каждом запуске программы).</summary>
    internal IReadOnlyList<string> ReadLogTail(out string? crash, int lines = 200)
    {
        crash = null;
        if (!File.Exists(LogPath)) return [];
        try
        {
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 256 * 1024) stream.Seek(-256 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

            var lastListen = all.FindLastIndex(l => l.Contains("Listening on", StringComparison.Ordinal));
            var lastCrash = all.FindLastIndex(l => l.Contains("Proxy thread crashed", StringComparison.Ordinal));
            if (lastCrash > lastListen) crash = all[lastCrash];

            if (all.LastOrDefault(l => l.Contains("stats:", StringComparison.Ordinal)) is { } stats)
                Stats = ProxyStats.Parse(stats);

            return all.Count > lines ? all[^lines..] : all;
        }
        catch (IOException)
        {
            return [];
        }
    }

    // ---------- установка ----------

    public Task<IDictionary<string, string>> BeforeReplaceAsync(bool isUpdate, CancellationToken ct) =>
        Task.FromResult<IDictionary<string, string>>(new Dictionary<string, string>());

    public Task AfterReplaceAsync(bool isUpdate, IDictionary<string, string> saved, CancellationToken ct)
    {
        EnsureHostedConfig();
        return Task.CompletedTask;
    }

    public string? ReadPayloadVersion() =>
        File.Exists(ExePath) ? FileVersionInfo.GetVersionInfo(ExePath).ProductVersion : null;

    public override object? CreateView() => new TgWsProxyView(this);

    /// <summary>Строка вида «stats: total=.. active=.. ws=.. … up=.. down=..».</summary>
    internal sealed record ProxyStats(string Total, string Active, string Up, string Down)
    {
        public static ProxyStats? Parse(string line)
        {
            string Get(string key) => Regex.Match(line, $@"\b{key}=(\S+)") is { Success: true } m ? m.Groups[1].Value : "?";
            return line.Contains("stats:") ? new ProxyStats(Get("total"), Get("active"), Get("up"), Get("down")) : null;
        }
    }
}
