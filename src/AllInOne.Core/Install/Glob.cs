using System.Text;
using System.Text.RegularExpressions;

namespace AllInOne.Core.Install;

/// <summary>Шаблоны путей для preserve: «*» — внутри одной папки, «**» — на любую глубину. Разделитель — «/».</summary>
public static class Glob
{
    public static Regex ToRegex(string pattern)
    {
        var p = Normalize(pattern);
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*')
            {
                if (i + 1 < p.Length && p[i + 1] == '*')
                {
                    // «a/**» совпадает и с самой папкой a, и со всем внутри.
                    sb.Append(".*");
                    i++;
                    if (i + 1 < p.Length && p[i + 1] == '/') i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsMatch(string pattern, string relativePath) => ToRegex(pattern).IsMatch(Normalize(relativePath));

    /// <summary>Файлы внутри root, чьи относительные пути подходят под любой из шаблонов.</summary>
    public static IEnumerable<string> Match(string root, IEnumerable<string> patterns)
    {
        if (!Directory.Exists(root)) yield break;
        var regexes = patterns.Select(ToRegex).ToList();
        if (regexes.Count == 0) yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Normalize(Path.GetRelativePath(root, file));
            if (regexes.Any(r => r.IsMatch(rel))) yield return rel;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
