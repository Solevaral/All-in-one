using System.IO;

namespace AllInOne.Modules.Zapret;

/// <summary>Ошибка работы с файлом zapret: текст уже понятен пользователю.</summary>
public sealed class ZapretFileException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Чтение и запись файлов zapret с понятными ошибками. На разных компьютерах файл может отсутствовать,
/// быть только для чтения, заблокирован антивирусом или «Контролируемым доступом к папкам» Защитника,
/// занят другой программой, а диск — заполнен. Запись идёт через временный файл: при сбое прежний
/// файл остаётся целым. Занятый файл повторяется несколько раз с паузой.
/// </summary>
internal static class SafeFile
{
    private const int Attempts = 4;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(150);

    /// <summary>Текст файла или null, если файла нет.</summary>
    public static string? ReadText(string path) =>
        Run(path, "чтения", () => File.Exists(path) ? File.ReadAllText(path) : null);

    public static string[]? ReadLines(string path) =>
        Run(path, "чтения", () => File.Exists(path) ? File.ReadAllLines(path) : null);

    public static byte[] ReadBytes(string path) => Run(path, "чтения", () => File.ReadAllBytes(path));

    public static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Записывает файл целиком: временный файл, затем замена. Снимает «только чтение».</summary>
    public static void WriteText(string path, string content) =>
        Run(path, "записи", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".aio-tmp";
            try
            {
                File.WriteAllText(tmp, content);
                ClearReadOnly(path);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return true;
        });

    public static void Move(string from, string to) =>
        Run(from, "переноса", () =>
        {
            ClearReadOnly(to);
            File.Move(from, to, overwrite: true);
            return true;
        });

    public static void Copy(string from, string to) =>
        Run(to, "записи", () =>
        {
            ClearReadOnly(to);
            File.Copy(from, to, overwrite: true);
            return true;
        });

    /// <summary>Список файлов папки; нет папки или доступа — пустой список.</summary>
    public static IEnumerable<string> List(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static T Run<T>(string path, string verb, Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (IsBusy(ex) && attempt < Attempts)
            {
                Thread.Sleep(Pause);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       && ex is not ZapretFileException)
            {
                throw new ZapretFileException(Describe(ex, path, verb), ex);
            }
        }
    }

    // HRESULT: 0x20 — файл занят, 0x21 — заблокирована часть файла, 0x70 и 0x27 — нет места на диске.
    private static int Code(Exception ex) => ex.HResult & 0xFFFF;

    private static bool IsBusy(IOException ex) => Code(ex) is 0x20 or 0x21;

    internal static string Describe(Exception ex, string path, string verb)
    {
        var name = Path.GetFileName(path);
        return ex switch
        {
            DirectoryNotFoundException =>
                $"Нет папки {Path.GetDirectoryName(path)}: модуль zapret повреждён или папку удалил антивирус. Переустановите модуль.",
            FileNotFoundException =>
                $"Нет файла {name}: его удалил антивирус или он не входит в эту версию zapret. Переустановите модуль.",
            UnauthorizedAccessException =>
                $"Нет доступа к {name}: файл заблокирован антивирусом или защищён «Контролируемым доступом к папкам» Защитника Windows. " +
                "Добавьте папку All in One в исключения.",
            PathTooLongException => $"Слишком длинный путь: {path}.",
            IOException io when IsBusy(io) => $"{name} занят другой программой. Повторите через несколько секунд.",
            IOException io when Code(io) is 0x70 or 0x27 => $"Нет места на диске для {verb} {name}.",
            _ => $"Ошибка {verb} {name}: {ex.Message}",
        };
    }
}
