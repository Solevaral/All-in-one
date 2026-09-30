using System.Text.Json;
using System.Text.Json.Nodes;

namespace AllInOne.Sdk;

/// <summary>
/// Протокол «каркас ↔ модуль» поверх именованного канала \\.\pipe\AllInOne.&lt;id&gt;.
/// Сервер канала — модуль, клиент — каркас: модуль живёт независимо от каркаса,
/// и каркас после перезапуска просто подключается заново.
/// Формат — JSON-lines (одно сообщение на строку, UTF-8):
///   запрос  {"id":1,"method":"getStatus","params":{}}
///   ответ   {"id":1,"result":{...}}  или  {"id":1,"error":{"code":"...","message":"..."}}
///   событие {"event":"statusChanged","data":{...}}
/// </summary>
public static class HostLinkProtocol
{
    public const int Version = 1;

    public static string PipeName(string moduleId) => $"AllInOne.{moduleId}";

    public static class Methods
    {
        /// <summary>→ <see cref="HelloResult"/></summary>
        public const string Hello = "hello";

        /// <summary>→ <see cref="StatusResult"/></summary>
        public const string GetStatus = "getStatus";

        /// <summary>Показать главное окно программы.</summary>
        public const string ShowWindow = "showWindow";

        /// <summary>params: {"action":"&lt;id&gt;"} — выполнить действие из hello.capabilities.actions.</summary>
        public const string Invoke = "invoke";

        /// <summary>
        /// params: {"reason":"update"}. Модуль отвечает {"accepted":true}, затем освобождает
        /// всё, что держит (системный прокси, гамму, курсор, хуки), и завершает процесс.
        /// </summary>
        public const string Shutdown = "shutdown";
    }

    public static class Events
    {
        /// <summary>data — <see cref="StatusResult"/></summary>
        public const string StatusChanged = "statusChanged";

        /// <summary>data — {"title":"…","text":"…"}: уведомление, которое каркас покажет в своём трее
        /// (в режиме --hosted у программы нет собственной иконки в трее).</summary>
        public const string Notify = "notify";
    }
}

public sealed class HelloResult
{
    public int Protocol { get; set; }
    public string? AppVersion { get; set; }
    public int ProcessId { get; set; }
    public HelloCapabilities Capabilities { get; set; } = new();
}

public sealed class HelloCapabilities
{
    public List<ActionInfo> Actions { get; set; } = [];
}

public sealed class ActionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class StatusResult
{
    /// <summary>running / stopped / error / busy</summary>
    public string State { get; set; } = "running";
    public string? Summary { get; set; }
    public string? Detail { get; set; }
}

public sealed class HostLinkMessage
{
    public long? Id { get; set; }
    public string? Method { get; set; }
    public JsonNode? Params { get; set; }
    public JsonNode? Result { get; set; }
    public HostLinkError? Error { get; set; }
    public string? Event { get; set; }
    public JsonNode? Data { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, Json.Compact);

    public static HostLinkMessage? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<HostLinkMessage>(line, Json.Compact); }
        catch (JsonException) { return null; }
    }
}

public sealed class HostLinkError
{
    public string Code { get; set; } = "error";
    public string Message { get; set; } = "";
}
