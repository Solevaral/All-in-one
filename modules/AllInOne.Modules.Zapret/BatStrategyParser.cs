using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AllInOne.Modules.Zapret;

/// <summary>
/// Превращает стратегию general*.bat в аргументы winws.exe — то же, что делает service.bat
/// при установке службы, но без cmd: строки с «^» в конце склеиваются, «^!» становится «!»,
/// переменные %BIN%, %LISTS%, %GameFilter…% раскрываются, кавычки снимаются.
/// Запускать сами bat нельзя: они вызывают service.bat (проверка обновлений, pause при
/// установленной службе), а это зависание скрытого окна.
/// </summary>
public static partial class BatStrategyParser
{
    /// <summary>Порт-заглушка, как в service.bat: «выключенный» Game Filter не должен ловить ничего.</summary>
    public const string DisabledPort = "12";

    public sealed record Variables(string Root, string GameFilterTcp, string GameFilterUdp)
    {
        public string Bin => Path.Combine(Root, "bin") + "\\";
        public string Lists => Path.Combine(Root, "lists") + "\\";
    }

    public static IReadOnlyList<string> ParseFile(string batPath, Variables vars) =>
        Parse(File.ReadAllText(batPath, Encoding.UTF8), vars);

    public static IReadOnlyList<string> Parse(string batText, Variables vars)
    {
        var lines = batText.Replace("\r\n", "\n").Split('\n');

        // Ищем строку запуска winws.exe и склеиваем её с продолжениями («^» в конце строки).
        var start = Array.FindIndex(lines, l => l.Contains("winws.exe\"", StringComparison.OrdinalIgnoreCase));
        if (start < 0) throw new FormatException("В файле стратегии нет запуска winws.exe.");

        var sb = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var continues = line.EndsWith('^') && !line.EndsWith("^^", StringComparison.Ordinal);
            if (continues) line = line[..^1];
            sb.Append(line).Append(' ');
            if (!continues) break;
        }

        var command = sb.ToString();
        var at = command.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
        var tail = command[(at + "winws.exe\"".Length)..];

        // Экранирование cmd: «^x» → «x» (в стратегиях встречается «^!»).
        tail = CaretEscape().Replace(tail, "$1");
        tail = Expand(tail, vars);
        return Tokenize(tail);
    }

    private static string Expand(string text, Variables vars)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIN"] = vars.Bin,
            ["LISTS"] = vars.Lists,
            ["~dp0"] = vars.Root.TrimEnd('\\') + "\\",
            ["GameFilter"] = vars.GameFilterTcp != DisabledPort ? vars.GameFilterTcp : vars.GameFilterUdp,
            ["GameFilterTCP"] = vars.GameFilterTcp,
            ["GameFilterUDP"] = vars.GameFilterUdp,
        };

        return VariableRef().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (map.TryGetValue(name, out var value)) return value;
            throw new FormatException($"Стратегия использует неизвестную переменную %{name}% — возможно, формат zapret изменился.");
        });
    }

    /// <summary>Разбор командной строки по правилам Windows: пробелы делят аргументы, кавычки снимаются.</summary>
    internal static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var any = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any || current.Length > 0) result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (any || current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Командная строка для binPath службы: значения с пробелами берутся в кавычки после «=»,
    /// как делает service.bat (--hostlist="C:\path\list.txt").
    /// </summary>
    public static string ToCommandLine(IEnumerable<string> args) =>
        string.Join(' ', args.Select(QuoteArg));

    private static string QuoteArg(string arg)
    {
        if (!arg.Contains(' ') && !arg.Contains('\t')) return arg;
        var eq = arg.IndexOf('=');
        return eq > 0 && arg.StartsWith("--", StringComparison.Ordinal)
            ? $"{arg[..(eq + 1)]}\"{arg[(eq + 1)..]}\""
            : $"\"{arg}\"";
    }

    /// <summary>Сортировка стратегий как в service.bat: числа сравниваются как числа (ALT2 раньше ALT10).</summary>
    public static IEnumerable<string> NaturalSort(IEnumerable<string> names) =>
        names.OrderBy(n => Digits().Replace(n, m => m.Value.PadLeft(8, '0')), StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\^(.)")]
    private static partial Regex CaretEscape();

    [GeneratedRegex(@"%([~A-Za-z0-9_]+)%")]
    private static partial Regex VariableRef();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
