using System.IO;
using AllInOne.Core;
using AllInOne.Core.Catalog;
using AllInOne.Core.Install;
using AllInOne.Sdk;
using Xunit;

namespace AllInOne.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("v1.2.0", "1.1.9", 1)]
    [InlineData("1.10.3", "1.9.9", 1)]
    [InlineData("1.0.0", "v1.0.0", 0)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    public void Compares(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SemVer.Compare(a, b)));

    [Fact]
    public void EmptyIsNeverNewer() => Assert.False(SemVer.IsNewer("", "1.0.0"));
}

public class GlobTests
{
    [Theory]
    [InlineData("payload/TgWsProxy_data/**", "payload/TgWsProxy_data/config.json", true)]
    [InlineData("payload/TgWsProxy_data/**", "payload/TgWsProxy_data/sub/x.log", true)]
    [InlineData("payload/TgWsProxy_data/**", "payload/TgWsProxy.exe", false)]
    [InlineData("lists/*-user.txt", "lists/list-general-user.txt", true)]
    [InlineData("lists/*-user.txt", "lists/list-general.txt", false)]
    [InlineData("lists/*-user.txt", "lists/sub/list-user.txt", false)]
    [InlineData("utils/game_filter.enabled", @"utils\game_filter.enabled", true)]
    public void Matches(string pattern, string path, bool expected) => Assert.Equal(expected, Glob.IsMatch(pattern, path));
}

public class CatalogTests
{
    private static ModuleManifest M(string id, string name, string? minHost = null, int schema = ModuleManifest.CurrentSchema) =>
        new() { Id = id, Name = name, MinHostVersion = minHost, Schema = schema };

    [Fact]
    public void RemoteOverridesBuiltinByIdAndKeepsOrder()
    {
        var builtin = new CatalogFile { Modules = [M("a", "A"), M("b", "B")] };
        var remote = new CatalogFile { Modules = [M("b", "B2"), M("c", "C")] };

        var items = CatalogService.Merge(builtin, remote, new Version(1, 0, 0));

        Assert.Equal(["a", "b", "c"], items.Select(i => i.Manifest.Id));
        Assert.Equal("B2", items[1].Manifest.Name);
        Assert.Equal(CatalogOrigin.Remote, items[1].Origin);
    }

    [Fact]
    public void NewerHostRequirementIsFlaggedAndUnknownSchemaSkipped()
    {
        var builtin = new CatalogFile { Modules = [M("a", "A", minHost: "2.0.0"), M("b", "B", schema: 99)] };

        var items = CatalogService.Merge(builtin, null, new Version(1, 5, 0));

        Assert.Single(items);
        Assert.True(items[0].RequiresHostUpdate);
    }

    [Fact]
    public void BuiltinCatalogIsValid()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "builtin-catalog.json"));
        var catalog = CatalogService.Parse(json);

        Assert.NotNull(catalog);
        var ids = catalog!.Modules.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("zapret", ids);
        Assert.Contains("shutdown-timer", ids);
        Assert.All(catalog.Modules.Where(m => m.IsExternal), m =>
        {
            Assert.NotNull(m.Run);
            Assert.DoesNotContain("payload", m.Run!.Exe);
            Assert.Equal(ModuleManifest.CurrentSchema, m.Schema);
            Assert.NotNull(m.Source?.Repo);
        });
    }
}

public class UnpackTests
{
    [Fact]
    public void ZipWithSingleTopFolderIsFlattened()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aio-unpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(tmp, "src", "zapret-1.0");
            Directory.CreateDirectory(Path.Combine(src, "bin"));
            File.WriteAllText(Path.Combine(src, "bin", "winws.exe"), "x");
            File.WriteAllText(Path.Combine(src, "general.bat"), "y");
            var zip = Path.Combine(tmp, "z.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(tmp, "src"), zip);

            var target = Path.Combine(tmp, "payload");
            ModuleInstaller.Unpack(zip, target, new ModuleSource { Layout = "zip" });

            Assert.True(File.Exists(Path.Combine(target, "bin", "winws.exe")));
            Assert.True(File.Exists(Path.Combine(target, "general.bat")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ExeIsSavedUnderStableName()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aio-unpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            var exe = Path.Combine(tmp, "fDimmer-1.3.0-win-x64-net9.exe");
            File.WriteAllText(exe, "x");
            var target = Path.Combine(tmp, "payload");
            ModuleInstaller.Unpack(exe, target, new ModuleSource { SaveAs = "fDimmer.exe" });
            Assert.True(File.Exists(Path.Combine(target, "fDimmer.exe")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}

public class ManifestValidationTests
{
    private static ModuleManifest Make(string id = "app", string? folder = "App", string exe = "App.exe", params string[] preserve) =>
        new() { Id = id, Name = "App", Folder = folder, Kind = "external", Run = new RunSpec { Exe = exe }, Preserve = [.. preserve] };

    [Fact]
    public void AcceptsNormalManifest() => Assert.Null(Make(exe: "bin/App.exe", preserve: ["data/**", "*.json"]).Validate());

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("")]
    [InlineData("мод")]
    public void RejectsBadId(string id) => Assert.NotNull(Make(id: id).Validate());

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:\Windows")]
    public void RejectsBadFolder(string folder) => Assert.NotNull(Make(folder: folder).Validate());

    [Theory]
    [InlineData(@"..\cmd.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData("bin/../../x.exe")]
    public void RejectsExeOutsideFolder(string exe) => Assert.NotNull(Make(exe: exe).Validate());

    [Fact]
    public void RejectsPreserveOutsideFolder() => Assert.NotNull(Make(preserve: ["../../**"]).Validate());

    [Fact]
    public void BuiltinCatalogPathsAreSafe()
    {
        var catalog = CatalogService.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "builtin-catalog.json")))!;
        Assert.All(catalog.Modules, m => Assert.Null(m.Validate()));
    }
}

public class ManualInstallVersionTests
{
    [Theory]
    [InlineData("1.3.1+a751dab", "1.3.1")]
    [InlineData("1.2.4", "1.2.4")]
    [InlineData("v1.10.4", "1.10.4")]
    [InlineData("1.1.2.0", "1.1.2")]
    [InlineData("0.0.0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("unknown", null)]
    public void CleansExeVersion(string? raw, string? expected) =>
        Assert.Equal(expected, AllInOne.Core.Modules.ModuleManager.CleanVersion(raw));
}
