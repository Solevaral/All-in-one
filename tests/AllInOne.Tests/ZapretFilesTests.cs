using System.IO;
using AllInOne.Modules.Zapret;
using Xunit;

namespace AllInOne.Tests;

public sealed class ZapretFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-zapret-" + Guid.NewGuid().ToString("N"));
    private readonly ZapretFiles _files;

    public ZapretFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "lists"));
        Directory.CreateDirectory(Path.Combine(_root, "utils"));
        File.WriteAllText(Path.Combine(_root, "service.bat"), "@echo off\r\nset \"LOCAL_VERSION=1.10.3\"\r\n");
        _files = new ZapretFiles(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ReadsVersionFromServiceBat() => Assert.Equal("1.10.3", _files.ReadVersion());

    [Theory]
    [InlineData("1024-65535", "1024-65535")]
    [InlineData("1024-1934, 1936-65535", "1024-1934,1936-65535")]
    [InlineData("80", "80")]
    [InlineData("0-100", null)]
    [InlineData("100-50", null)]
    [InlineData("70000", null)]
    [InlineData("01-5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ValidatesPortRangesLikeServiceBat(string input, string? expected) =>
        Assert.Equal(expected, ZapretFiles.ValidateRange(input));

    [Fact]
    public void GameFilterRoundTripsInServiceBatFormat()
    {
        Assert.Equal(GameFilterMode.Disabled, _files.ReadGameFilter().Mode);

        _files.WriteGameFilter(new GameFilter(GameFilterMode.Tcp, "1024-2000", "3000-4000"));
        Assert.Equal("mode=tcp\r\ntcp=1024-2000\r\nudp=3000-4000\r\n", File.ReadAllText(Path.Combine(_root, "utils", "game_filter.enabled")));

        var gf = _files.ReadGameFilter();
        Assert.Equal("1024-2000", gf.EffectiveTcp);
        Assert.Equal("12", gf.EffectiveUdp);
    }

    [Fact]
    public void LegacyGameFilterFlagMeansAll()
    {
        File.WriteAllText(Path.Combine(_root, "utils", "game_filter.enabled"), "all\r\n");
        Assert.Equal(GameFilterMode.All, _files.ReadGameFilter().Mode);
    }

    [Fact]
    public void IpsetCyclesThroughModesKeepingLoadedList()
    {
        var list = Path.Combine(_root, "lists", "ipset-all.txt");
        File.WriteAllText(list, "1.2.3.0/24\r\n5.6.7.8/32\r\n");
        Assert.Equal(IpsetMode.Loaded, _files.ReadIpsetMode());

        _files.SetIpsetMode(IpsetMode.None);
        Assert.Equal(IpsetMode.None, _files.ReadIpsetMode());
        Assert.True(_files.HasIpsetBackup);

        _files.SetIpsetMode(IpsetMode.Any);
        Assert.Equal(IpsetMode.Any, _files.ReadIpsetMode());

        _files.SetIpsetMode(IpsetMode.Loaded);
        Assert.Equal("1.2.3.0/24\r\n5.6.7.8/32\r\n", File.ReadAllText(list));
    }

    [Fact]
    public void LoadedWithoutBackupFails()
    {
        File.WriteAllText(Path.Combine(_root, "lists", "ipset-all.txt"), "");
        Assert.Throws<InvalidOperationException>(() => _files.SetIpsetMode(IpsetMode.Loaded));
    }

    [Fact]
    public void UserListsAreCreatedAndNeverLeftEmpty()
    {
        _files.EnsureUserLists();
        Assert.Contains("domain.example.abc", _files.ReadUserList("list-general-user.txt"));

        _files.WriteUserList("list-exclude-user.txt", "  \n\n ");
        Assert.Equal("domain.example.abc\r\n", _files.ReadUserList("list-exclude-user.txt"));

        _files.WriteUserList("list-general-user.txt", "example.com\nfoo.org");
        Assert.Equal("example.com\r\nfoo.org\r\n", _files.ReadUserList("list-general-user.txt"));
    }
}
