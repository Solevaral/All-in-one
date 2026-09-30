using System.Text;
using AllInOne.Sdk;

namespace AllInOne.Core;

/// <summary>Простой потокобезопасный файловый лог data\logs\host.log с ротацией по размеру.</summary>
public static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string FilePath => Path.Combine(AppPaths.Logs, "host.log");

    public static void Info(string message) => Write("INFO", null, message, null);
    public static void Warn(string message) => Write("WARN", null, message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", null, message, ex);

    public static IModuleLog For(string source) => new SourceLog(source);

    internal static void Write(string level, string? source, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("  ")
            .Append(level.PadRight(5)).Append("  ");
        if (source is not null) line.Append('[').Append(source).Append("] ");
        line.Append(message);
        if (ex is not null) line.AppendLine().Append(ex);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }
                File.AppendAllText(FilePath, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Лог не критичен: без него каркас продолжает работать.
            }
        }
    }

    private sealed class SourceLog(string source) : IModuleLog
    {
        public void Info(string message) => Write("INFO", source, message, null);
        public void Warn(string message) => Write("WARN", source, message, null);
        public void Error(string message, Exception? ex = null) => Write("ERROR", source, message, ex);
    }
}
