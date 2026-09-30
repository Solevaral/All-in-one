using System.IO;
using AllInOne.Modules.Zapret;
using Xunit;

namespace AllInOne.Tests;

public class BatStrategyParserTests
{
    private const string Root = @"C:\AllInOne\modules\zapret\payload";

    private static readonly BatStrategyParser.Variables Vars = new(Root, "12", "12");

    public static IEnumerable<object[]> AllStrategies() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zapret"), "*.bat")
            .Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(AllStrategies))]
    public void EveryStrategyParsesToCleanArguments(string file)
    {
        var args = BatStrategyParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zapret", file), Vars);

        Assert.NotEmpty(args);
        Assert.StartsWith("--wf-", args[0]);
        Assert.All(args, a =>
        {
            Assert.DoesNotContain("%", a);          // все переменные раскрыты
            Assert.DoesNotContain("\"", a);         // кавычки сняты
            Assert.NotEqual("^", a);                 // продолжения строк склеены
            Assert.DoesNotContain("^!", a);         // экранирование cmd снято
            Assert.StartsWith("--", a);             // у winws все аргументы — ключи
        });
        Assert.Contains(args, a => a == "--new");
    }

    [Fact]
    public void GeneralExpandsPathsAndDisabledGameFilter()
    {
        var args = BatStrategyParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zapret", "general.bat"), Vars);

        Assert.Equal("--wf-tcp=80,443,2053,2083,2087,2096,8443,12", args[0]);
        Assert.Equal("--wf-udp=443,19294-19344,50000-50100,12", args[1]);
        Assert.Contains($@"--hostlist={Root}\lists\list-general.txt", args);
        Assert.Contains($@"--dpi-desync-fake-quic={Root}\bin\quic_initial_www_google_com.bin", args);
        Assert.Contains("--filter-tcp=12", args);
        Assert.Equal("--dpi-desync-cutoff=n2", args[^1]);
    }

    [Fact]
    public void GameFilterRangesAreSubstituted()
    {
        var vars = new BatStrategyParser.Variables(Root, "1024-65535", "1024-2000");
        var args = BatStrategyParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zapret", "general.bat"), vars);

        Assert.Equal("--wf-tcp=80,443,2053,2083,2087,2096,8443,1024-65535", args[0]);
        Assert.Contains("--filter-udp=1024-2000", args);
    }

    [Fact]
    public void CaretExclamationBecomesLiteral()
    {
        var args = BatStrategyParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zapret", "general (FAKE TLS AUTO).bat"), Vars);
        Assert.Contains("--dpi-desync-fake-tls=!", args);
        Assert.Contains("--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com", args);
    }

    [Fact]
    public void PathsWithSpacesSurviveTokenizeAndCommandLine()
    {
        var vars = new BatStrategyParser.Variables(@"C:\My Tools\zapret", "12", "12");
        var args = BatStrategyParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zapret", "general.bat"), vars);
        Assert.Contains(@"--hostlist=C:\My Tools\zapret\lists\list-general.txt", args);

        var line = BatStrategyParser.ToCommandLine(args);
        Assert.Contains(@"--hostlist=""C:\My Tools\zapret\lists\list-general.txt""", line);
        Assert.Equal(args, BatStrategyParser.Tokenize(line));
    }

    [Fact]
    public void NaturalSortPutsAlt2BeforeAlt10()
    {
        var sorted = BatStrategyParser.NaturalSort(["general (ALT10)", "general (ALT2)", "general", "general (ALT)"]).ToList();
        Assert.Equal(["general", "general (ALT)", "general (ALT2)", "general (ALT10)"], sorted);
    }

    [Fact]
    public void UnknownVariableIsReported()
    {
        const string bat = "start \"x\" /min \"%BIN%winws.exe\" --wf-tcp=%SomethingNew%";
        Assert.Throws<FormatException>(() => BatStrategyParser.Parse(bat, Vars));
    }
}
