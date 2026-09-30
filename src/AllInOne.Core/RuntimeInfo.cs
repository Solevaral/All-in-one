using System.Reflection;
using System.Runtime.InteropServices;

namespace AllInOne.Core;

public enum BuildFlavor
{
    /// <summary>Framework-dependent: нужен установленный .NET 9 Desktop Runtime.</summary>
    Net9,

    /// <summary>Self-contained: .NET внутри exe.</summary>
    Standalone,
}

public static class RuntimeInfo
{
    public static Version HostVersion { get; } =
        typeof(RuntimeInfo).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 1, 0);

    public static string HostVersionText { get; } =
        (typeof(RuntimeInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? HostVersion.ToString(3))
        .Split('+')[0];

    /// <summary>Вариант сборки задаётся свойством MSBuild AllInOneFlavor при публикации.</summary>
    public static BuildFlavor Flavor { get; } =
        typeof(RuntimeInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AllInOneFlavor")?.Value == "standalone"
            ? BuildFlavor.Standalone
            : BuildFlavor.Net9;

    public static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>
    /// Установлен ли .NET Desktop Runtime нужной major-версии. Для Net9-варианта каркаса ответ
    /// всегда «да» — иначе он бы не запустился.
    /// </summary>
    public static bool HasDesktopRuntime(int major)
    {
        if (Flavor == BuildFlavor.Net9 && major == Environment.Version.Major) return true;

        foreach (var root in DotnetRoots())
        {
            var dir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(dir)) continue;
            if (Directory.EnumerateDirectories(dir).Any(d => Path.GetFileName(d).StartsWith(major + ".", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> DotnetRoots()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } env) yield return env;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }

    /// <summary>WebView2 Runtime (нужен TryToCatchMe). В Windows 11 есть всегда.</summary>
    public static bool HasWebView2()
    {
        const string clientKey = @"\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node" + clientKey, @"SOFTWARE" + clientKey })
        {
            using var lm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (lm?.GetValue("pv") is string v && v != "0.0.0.0") return true;
            using var cu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            if (cu?.GetValue("pv") is string u && u != "0.0.0.0") return true;
        }
        return false;
    }
}
