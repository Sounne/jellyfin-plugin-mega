using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>What Jellyfin loads from the package: settings pages, catalogue entry.</summary>
[Collection(FakeMegaCollection.Name)]
public sealed class PackagingTests
{
    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, name);
            if (File.Exists(path) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return path;
            }
        }

        throw new FileNotFoundException(name);
    }

    [Fact]
    public async Task SettingsPagesAreEmbedded()
    {
        await using var host = await PluginHost.StartAsync();
        var pages = host.Plugin.GetPages().ToList();
        Assert.Equal(new[] { "MEGA", "megaconfigjs" }, pages.Select(p => p.Name));
        foreach (var page in pages)
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            Assert.True(stream is { Length: > 1000 }, page.EmbeddedResourcePath);
        }

        using var html = new StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream(pages[0].EmbeddedResourcePath)!);
        Assert.Contains("data-controller=\"__plugin/megaconfigjs\"", await html.ReadToEndAsync(), StringComparison.Ordinal);
        Assert.Same(host.Plugin, Plugin.Instance);
        Assert.Equal(Guid.Parse(Plugin.PluginGuid), host.Plugin.Id);
    }

    [Fact]
    public void CatalogueEntry()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("manifest.json")));
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(Plugin.PluginGuid, entry.GetProperty("guid").GetString());
        Assert.Equal("MEGA", entry.GetProperty("name").GetString());

        var versions = entry.GetProperty("versions").EnumerateArray().ToList();
        Assert.NotEmpty(versions);
        Assert.Equal(versions.Count, versions.Select(v => v.GetProperty("version").GetString()).Distinct().Count());
        foreach (var v in versions)
        {
            var version = Version.Parse(v.GetProperty("version").GetString()!);

            // The last number says which Jellyfin the build is for.
            var abi = v.GetProperty("targetAbi").GetString();
            Assert.Equal(version.Revision == 12 ? "12.0.0.0" : "10.11.0.0", abi);
            Assert.Contains(version.Revision, new[] { 11, 12 });

            Assert.Matches("^[0-9a-f]{32}$", v.GetProperty("checksum").GetString());
            var url = v.GetProperty("sourceUrl").GetString()!;
            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.EndsWith("_" + version + ".zip", url, StringComparison.Ordinal);
            Assert.True(DateTimeOffset.TryParse(v.GetProperty("timestamp").GetString(), out _));
            Assert.False(string.IsNullOrWhiteSpace(v.GetProperty("changelog").GetString()));
        }

        // Newest first.
        var order = versions.Select(v => Version.Parse(v.GetProperty("version").GetString()!)).ToList();
        Assert.Equal(order.OrderByDescending(v => new Version(v.Major, v.Minor, v.Build)).ThenByDescending(v => v.Revision), order);
    }

    [Fact]
    public void BuildIsForJellyfin12()
        => Assert.Equal(12, typeof(Plugin).Assembly.GetName().Version!.Revision);
}
