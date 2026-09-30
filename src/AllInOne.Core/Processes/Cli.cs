using System.Diagnostics;
using System.Text;

namespace AllInOne.Core.Processes;

/// <summary>Запуск консольных утилит Windows (sc, netsh, schtasks, reg) со сбором вывода.</summary>
public static class Cli
{
    static Cli()
    {
        // sc и netsh пишут в OEM-кодировке консоли (cp866 на русской Windows).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
        public string All => string.IsNullOrWhiteSpace(Error) ? Output : Output + Environment.NewLine + Error;
    }

    public static async Task<Result> RunAsync(string file, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await RunAsync(psi, timeout, ct);
    }

    /// <summary>Вариант с готовой строкой аргументов — для sc.exe, у которого свой разбор «key= value».</summary>
    public static async Task<Result> RunRawAsync(string file, string arguments, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
        };
        return await RunAsync(psi, timeout, ct);
    }

    private static async Task<Result> RunAsync(ProcessStartInfo psi, TimeSpan? timeout, CancellationToken ct)
    {
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            ProcessUtil.KillTree(p);
            throw new TimeoutException($"{Path.GetFileName(psi.FileName)} не ответил вовремя");
        }

        return new Result(p.ExitCode, await stdout, await stderr);
    }

    private static Encoding OemEncoding =>
        Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}
