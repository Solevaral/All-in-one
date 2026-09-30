using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AllInOne.Sdk;

namespace AllInOne.Core.HostLink;

/// <summary>
/// Клиентская сторона протокола <see cref="HostLinkProtocol"/>. Одно подключение к одному модулю;
/// при разрыве владелец создаёт новое через <see cref="TryConnectAsync"/>.
/// </summary>
public sealed class HostLinkClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HostLinkMessage>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private int _disconnected;

    private HostLinkClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(ReadLoopAsync);
    }

    public bool IsConnected => _disconnected == 0 && _pipe.IsConnected;

    /// <summary>Событие от модуля: имя и данные. Вызывается из фонового потока.</summary>
    public event Action<string, JsonNode?>? EventReceived;

    /// <summary>Канал закрыт (модуль завершился или разорвал связь). Вызывается один раз.</summary>
    public event Action? Disconnected;

    /// <param name="serverDir">
    /// Папка, из которой должен быть запущен процесс-сервер канала (папка модуля). Канал чужого
    /// процесса с тем же именем отвергается — так не подсунуть каркасу подменённый модуль.
    /// </param>
    public static async Task<HostLinkClient?> TryConnectAsync(string pipeName, string? serverDir, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
            if (serverDir is not null && !IsServerUnder(pipe, serverDir))
            {
                Log.Warn($"Канал {pipeName} открыт процессом не из {serverDir} — подключение отклонено");
                await pipe.DisposeAsync();
                return null;
            }
            return new HostLinkClient(pipe);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await pipe.DisposeAsync();
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            return null;
        }
    }

    private static bool IsServerUnder(NamedPipeClientStream pipe, string dir)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var path = Processes.ProcessUtil.TryGetPath(process);
            var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    public async Task<JsonNode?> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected) throw new HostLinkException("Нет связи с модулем.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<HostLinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new HostLinkMessage
        {
            Id = id,
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToNode(parameters, Json.Compact),
        };

        try
        {
            await _writeGate.WaitAsync(ct);
            try
            {
                await _writer.WriteLineAsync(message.Serialize().AsMemory(), ct);
            }
            finally
            {
                _writeGate.Release();
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            cts.CancelAfter(timeout);
            HostLinkMessage response;
            try
            {
                response = await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HostLinkException(_cts.IsCancellationRequested
                    ? "Связь с модулем прервалась."
                    : $"Модуль не ответил на «{method}» за {timeout.TotalSeconds:0} с.");
            }

            if (response.Error is { } error) throw new HostLinkException(error.Message, error.Code);
            return response.Result;
        }
        catch (IOException ex)
        {
            OnDisconnected();
            throw new HostLinkException("Связь с модулем прервалась: " + ex.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T?> CallAsync<T>(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var node = await CallAsync(method, parameters, timeout, ct);
        return node is null ? default : node.Deserialize<T>(Json.Compact);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (line.Length == 0) continue;

                var message = HostLinkMessage.Parse(line);
                if (message is null) continue;

                if (message.Id is { } id && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(message);
                }
                else if (message.Event is { } name)
                {
                    try { EventReceived?.Invoke(name, message.Data); }
                    catch (Exception ex) { Log.Error("Обработчик события модуля упал", ex); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            OnDisconnected();
        }
    }

    private void OnDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        _cts.Cancel();
        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        OnDisconnected();
        await _pipe.DisposeAsync();
        _reader.Dispose();
        _writeGate.Dispose();
        _cts.Dispose();
    }
}

public sealed class HostLinkException(string message, string code = "error") : Exception(message)
{
    public string Code { get; } = code;
}
