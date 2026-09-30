namespace AllInOne.Core;

/// <summary>
/// Сравнение версий из тегов релизов: «v1.2.0», «1.10.3», «1.1.2-beta».
/// Нечисловые хвосты (-beta) считаются младше той же версии без хвоста.
/// </summary>
public static class SemVer
{
    public static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var t = tag.Trim();
        if (t.Length > 1 && (t[0] == 'v' || t[0] == 'V') && char.IsDigit(t[1])) t = t[1..];
        return t;
    }

    /// <summary>Отрицательное, если a младше b; ноль, если равны; положительное, если a новее.</summary>
    public static int Compare(string? a, string? b)
    {
        var (na, sa) = Split(Normalize(a));
        var (nb, sb) = Split(Normalize(b));

        for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            var x = i < na.Length ? na[i] : 0;
            var y = i < nb.Length ? nb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        // 1.0.0-beta < 1.0.0
        if (sa.Length == 0 && sb.Length > 0) return 1;
        if (sa.Length > 0 && sb.Length == 0) return -1;
        return string.CompareOrdinal(sa, sb);
    }

    public static bool IsNewer(string? candidate, string? current) =>
        !string.IsNullOrEmpty(Normalize(candidate)) && Compare(candidate, current) > 0;

    private static (long[] Numbers, string Suffix) Split(string v)
    {
        var dash = v.IndexOfAny(['-', '+', ' ']);
        var core = dash >= 0 ? v[..dash] : v;
        var suffix = dash >= 0 ? v[(dash + 1)..] : "";
        var numbers = core.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => long.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();
        return (numbers, suffix);
    }
}
