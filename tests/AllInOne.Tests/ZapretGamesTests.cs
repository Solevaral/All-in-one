using System.IO;
using AllInOne.Modules.Zapret;
using Xunit;

namespace AllInOne.Tests;

public sealed class ZapretGamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-games-" + Guid.NewGuid().ToString("N"));
    private readonly ZapretFiles _files;
    private readonly ZapretGames _games;

    public ZapretGamesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "program", "lists"));
        Directory.CreateDirectory(Path.Combine(_root, "program", "utils"));
        _files = new ZapretFiles(Path.Combine(_root, "program"));
        _files.SetIpsetMode(IpsetMode.None);
        _games = new ZapretGames(Path.Combine(_root, "data"));
        _games.LoadState();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string[] Lines(string file) =>
        _files.ReadUserList(file).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void BuiltinSetParses()
    {
        Assert.Contains(_games.Games, g => g.Id == "ubisoft");
        Assert.All(_games.Games, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
    }

    [Fact]
    public void UbisoftFixSetsModesAndExcludesAndUndoesThem()
    {
        _games.Set(_files, "ubisoft", true);
        Assert.Equal(GameFilterMode.All, _files.ReadGameFilter().Mode);
        Assert.Equal(IpsetMode.Any, _files.ReadIpsetMode());
        Assert.Contains("ubisoft.com", Lines(ZapretGames.ListExcludeFile));

        _games.Set(_files, "ubisoft", false);
        Assert.Equal(GameFilterMode.Disabled, _files.ReadGameFilter().Mode);
        Assert.Equal(IpsetMode.None, _files.ReadIpsetMode());
        Assert.DoesNotContain("ubisoft.com", Lines(ZapretGames.ListExcludeFile));
        Assert.Contains("domain.example.abc", Lines(ZapretGames.ListExcludeFile));
    }

    [Fact]
    public void UserLinesSurviveUnchecking()
    {
        _files.EnsureUserLists();
        _files.WriteUserList(ZapretGames.ListExcludeFile, "my.site\r\nubi.com");
        _games.Set(_files, "ubisoft", true);
        _games.Set(_files, "ubisoft", false);

        var lines = Lines(ZapretGames.ListExcludeFile);
        Assert.Contains("my.site", lines);
        Assert.Contains("ubi.com", lines);           // была до фикса — осталась
        Assert.DoesNotContain("uplay.com", lines);   // добавил фикс — убрана
    }

    [Fact]
    public void ManualModeChangeIsNotRolledBack()
    {
        _games.Set(_files, "ubisoft", true);
        _files.WriteGameFilter(_files.ReadGameFilter() with { Mode = GameFilterMode.Udp });
        _games.Set(_files, "ubisoft", false);
        Assert.Equal(GameFilterMode.Udp, _files.ReadGameFilter().Mode);
    }

    [Fact]
    public void StatePersists()
    {
        _games.Set(_files, "ss14", true);
        var again = new ZapretGames(Path.Combine(_root, "data"));
        again.LoadState();
        Assert.Contains("ss14", again.State.Enabled);
        again.Set(_files, "ss14", false);
        Assert.DoesNotContain("spacestation14.com", Lines(ZapretGames.ListGeneralFile));
    }

    [Theory]
    [InlineData("""{"schema":1,"games":[{"id":"x","name":"X","gameFilter":"bogus"}]}""")]
    [InlineData("""{"schema":1,"games":[{"id":"x","name":"X","listGeneral":["a b"]}]}""")]
    public void RejectsInvalidEntries(string json) => Assert.Empty(ZapretGames.Parse(json)!.Games);

    [Fact]
    public void RejectsUnknownSchema() => Assert.Null(ZapretGames.Parse("""{"schema":2,"games":[]}"""));
}
