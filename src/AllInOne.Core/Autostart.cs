using System.Security;
using System.Security.Principal;
using AllInOne.Core.Processes;
using Microsoft.Win32;

namespace AllInOne.Core;

/// <summary>
/// Автозапуск каркаса — задача Планировщика с наивысшими правами при входе пользователя.
/// Так каркас стартует от администратора без окна UAC (ярлык в Run-ключе так не умеет).
/// </summary>
public static class HostAutostart
{
    public const string TaskName = "AllInOne";

    public static async Task<bool> IsEnabledAsync()
    {
        var r = await Cli.RunAsync(Cli.System32("schtasks.exe"), ["/Query", "/TN", TaskName, "/XML"]);
        return r.Ok && r.Output.Contains(AppPaths.HostExe, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task SetAsync(bool enabled)
    {
        if (!enabled)
        {
            await Cli.RunAsync(Cli.System32("schtasks.exe"), ["/Delete", "/TN", TaskName, "/F"]);
            return;
        }

        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Запуск All in One при входе в систему</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(AppPaths.HostExe)}</Command>
                  <Arguments>--autostart</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(AppPaths.Root)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), "AllInOne.task.xml");
        await File.WriteAllTextAsync(file, xml, System.Text.Encoding.Unicode);
        try
        {
            var r = await Cli.RunAsync(Cli.System32("schtasks.exe"), ["/Create", "/TN", TaskName, "/XML", file, "/F"]);
            if (!r.Ok) throw new InvalidOperationException("Планировщик задач отказал: " + r.All.Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>Собственный автозапуск программ-модулей (HKCU\...\Run), который мешает запуску через каркас.</summary>
public static class LegacyAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string? Get(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(valueName) as string;
    }

    public static void Remove(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
