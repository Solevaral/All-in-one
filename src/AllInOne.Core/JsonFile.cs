using System.Text.Json;
using AllInOne.Sdk;

namespace AllInOne.Core;

/// <summary>Чтение и атомарная запись JSON-файлов: сначала во временный файл, затем замена.</summary>
public static class JsonFile
{
    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать {path}: {ex.Message}");
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool TryWrite<T>(string path, T value)
    {
        try
        {
            Write(path, value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось записать {path}: {ex.Message}");
            return false;
        }
    }
}
