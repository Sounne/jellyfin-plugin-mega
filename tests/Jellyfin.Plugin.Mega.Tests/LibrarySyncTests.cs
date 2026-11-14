using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Tests.Support;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>Virtual libraries: MEGA folders mirrored as <c>.strm</c> shortcuts and side files, kept in step with MEGA.</summary>
[Collection(FakeMegaCollection.Name)]
public sealed class LibrarySyncTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly byte[] Srt = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nBonjour\n");

    private static DateTime Unix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    private static List<string> Files(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();

    private sealed record Films(string Folder, string Movie, string Subtitle, string Extras, string Trailer, string DupMkv, string DupMp4);

    private static Films AddFilms(FakeMega m)
    {
        var films = m.AddFolder(null, "Films");
        var movie = m.AddFile(films, "Movie (2020).mkv", TestData.Bytes(4000), mtime: 1_700_000_000, preview: true);
        var srt = m.AddFile(films, "Movie (2020).fr.srt", Srt, mtime: 1_700_000_100);
        m.AddFile(films, "poster.jpg", FakeMega.NewJpeg());
        m.AddFile(films, "movie.nfo", "<movie><title>Movie</title></movie>"u8.ToArray());
        m.AddFile(films, "notes.txt", "not a subtitle"u8.ToArray());
        m.AddFile(films, "IMG_2041.jpg", FakeMega.NewJpeg());
        var dupMkv = m.AddFile(films, "Dup.mkv", TestData.Bytes(10, 2));
        var dupMp4 = m.AddFile(films, "Dup.mp4", TestData.Bytes(10, 3));
        m.AddFile(films, "CON.mkv", TestData.Bytes(10, 4));
        m.AddFile(films, "Part: One?.mkv", TestData.Bytes(10, 5));
        var extras = m.AddFolder(films, "Extras");
        var trailer = m.AddFile(extras, "Trailer.mp4", TestData.Bytes(10, 6));
        return new Films(films, movie, srt, extras, trailer, dupMkv, dupMp4);
    }

    [Fact]
    public async Task FoldersBecomeStrmShortcutsAndSideFiles()
    {
        await using var host = await PluginHost.StartAsync();
        var f = AddFilms(host.Mega);
        var lib = host.AddLibrary("/Films", "MEGA Films");
        var progress = new List<double>();

        await host.Sync.SyncAllAsync(false, new SyncProgress(progress), Ct);

        var dir = host.LibraryPath(lib);
        Assert.Equal(Path.Combine(host.Manager.RootPath, "libraries", "MEGA Films"), dir);
        Assert.Equal(
            new[]
            {
                "Dup (2).strm", "Dup.strm", "Extras/Trailer.strm", "Movie (2020).fr.srt", "Movie (2020).strm", "Part_ One_.strm",
                "_CON.strm", "movie.nfo", "poster.jpg",
            },
            Files(dir));

        // Shortcuts: one signed URL each, dated like the MEGA file.
        var strm = Path.Combine(dir, "Movie (2020).strm");
        Assert.Equal(host.StreamUrl(f.Movie) + "\n", File.ReadAllText(strm));
        Assert.StartsWith("http://127.0.0.1:8096/MegaFs/stream/link1/" + f.Movie + "/", File.ReadAllText(strm), StringComparison.Ordinal);
        Assert.Equal(Unix(1_700_000_000), File.GetLastWriteTimeUtc(strm));
        Assert.Contains("/" + f.DupMkv + "/", File.ReadAllText(Path.Combine(dir, "Dup.strm")), StringComparison.Ordinal);
        Assert.Contains("/" + f.DupMp4 + "/", File.ReadAllText(Path.Combine(dir, "Dup (2).strm")), StringComparison.Ordinal);
        Assert.Contains("/" + f.Trailer + "/", File.ReadAllText(Path.Combine(dir, "Extras", "Trailer.strm")), StringComparison.Ordinal);

        // Side files: real, decrypted copies.
        var srt = Path.Combine(dir, "Movie (2020).fr.srt");
        Assert.Equal(Srt, File.ReadAllBytes(srt));
        Assert.Equal(Unix(1_700_000_100), File.GetLastWriteTimeUtc(srt));
        Assert.Equal(0xFF, File.ReadAllBytes(Path.Combine(dir, "poster.jpg"))[0]);
        Assert.StartsWith("<movie>", File.ReadAllText(Path.Combine(dir, "movie.nfo")), StringComparison.Ordinal);
        Assert.False(Directory.EnumerateFiles(dir, "*.megatmp", SearchOption.AllDirectories).Any());

        var status = host.Sync.GetStatus(lib.Id)!;
        Assert.Null(status.Error);
        Assert.Equal(6, status.MediaFiles);
        Assert.Equal(3, status.SideFiles);
        Assert.Equal(1, status.Folders);
        Assert.NotNull(host.Sync.LastSync);
        Assert.False(host.Sync.IsSyncing);
        Assert.Equal(100, progress.Last());

        // The Jellyfin library is created once, then scanned.
        var created = Assert.Single(host.Library.Calls("AddVirtualFolder"));
        Assert.Equal("MEGA Films", created[0]);
        Assert.Equal(CollectionTypeOptions.movies, created[1]);
        var options = (LibraryOptions)created[2]!;
        Assert.Equal(dir, Assert.Single(options.PathInfos).Path);
        Assert.False(options.EnableRealtimeMonitor);
        Assert.False(options.EnableChapterImageExtraction);
        Assert.False(options.SaveLocalMetadata);
        Assert.Single(host.Library.Calls("QueueLibraryScan"));

        // MEGA previews are fetched ahead, for the library scan.
        var preview = Path.Combine(host.Manager.CachePath, "previews", "link1", BlockDiskCache.SafeName(f.Movie) + ".jpg");
        Assert.Equal(host.Mega.Preview(f.Movie), File.ReadAllBytes(preview));
    }

    [Fact]
    public async Task NothingChangedNothingDone()
    {
        await using var host = await PluginHost.StartAsync();
        AddFilms(host.Mega);
        var lib = host.AddLibrary("/Films", "Films");
        await host.Sync.SyncAllAsync(false, null, Ct);
        var dir = host.LibraryPath(lib);
        var times = Files(dir).ToDictionary(p => p, p => File.GetLastWriteTimeUtc(Path.Combine(dir, p)));
        int downloads = host.Mega.StorageRequests;
        int previews = host.Mega.AttributeRequests;
        var lastSync = host.Sync.LastSync;

        // A poll that finds nothing does not touch the libraries.
        await host.Sync.CheckForChangesAsync(Ct);
        Assert.Equal(lastSync, host.Sync.LastSync);

        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.Equal(times, Files(dir).ToDictionary(p => p, p => File.GetLastWriteTimeUtc(Path.Combine(dir, p))));
        Assert.Equal(downloads, host.Mega.StorageRequests);
        Assert.Equal(previews, host.Mega.AttributeRequests);
        Assert.Single(host.Library.Calls("AddVirtualFolder"));
        Assert.Single(host.Library.Calls("QueueLibraryScan"));
        Assert.Equal(1, host.Mega.Count("f"));
    }

    [Fact]
    public async Task ChangesOnMegaReachTheLibrary()
    {
        await using var host = await PluginHost.StartAsync();
        var f = AddFilms(host.Mega);
        var lib = host.AddLibrary("/Films", "Films");
        await host.Sync.SyncAllAsync(false, null, Ct);
        var dir = host.LibraryPath(lib);

        var added = host.Mega.AddFile(f.Folder, "New (2021).mkv", TestData.Bytes(100), live: true);
        host.Mega.Rename(f.Movie, "Movie (2020) Remastered.mkv", live: true);
        host.Mega.Delete(f.Extras, live: true);
        host.Mega.Delete(f.Subtitle, live: true);
        await host.Sync.CheckForChangesAsync(Ct);

        var files = Files(dir);
        Assert.Contains("New (2021).strm", files);
        Assert.Contains("Movie (2020) Remastered.strm", files);
        Assert.DoesNotContain("Movie (2020).strm", files);
        Assert.DoesNotContain("Movie (2020).fr.srt", files);
        Assert.False(Directory.Exists(Path.Combine(dir, "Extras")));
        Assert.Contains("/" + added + "/", File.ReadAllText(Path.Combine(dir, "New (2021).strm")), StringComparison.Ordinal);
        Assert.Contains("/" + f.Movie + "/", File.ReadAllText(Path.Combine(dir, "Movie (2020) Remastered.strm")), StringComparison.Ordinal);
        Assert.Equal(2, host.Library.Calls("QueueLibraryScan").Count);
        Assert.Equal(1, host.Mega.Count("f"));
    }

    [Fact]
    public async Task FullResyncDownloadsTheTreeAgain()
    {
        await using var host = await PluginHost.StartAsync();
        var f = AddFilms(host.Mega);
        var lib = host.AddLibrary("/Films", "Films");
        await host.Sync.SyncAllAsync(false, null, Ct);

        // Renamed without a change packet (missed while offline): only the full resync sees it.
        host.Mega.Rename(f.Trailer, "Teaser.mp4");
        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.Contains("Extras/Trailer.strm", Files(host.LibraryPath(lib)));

        await host.Sync.SyncAllAsync(true, null, Ct);
        Assert.Contains("Extras/Teaser.strm", Files(host.LibraryPath(lib)));
        Assert.DoesNotContain("Extras/Trailer.strm", Files(host.LibraryPath(lib)));
        Assert.Equal(2, host.Mega.Count("f"));
    }

    [Theory]
    [InlineData("tvshows", "tvshows")]
    [InlineData("music", "music")]
    [InlineData("mixed", null)]
    [InlineData("unknown", null)]
    public async Task LibraryType(string contentType, string? expected)
    {
        await using var host = await PluginHost.StartAsync();
        host.Mega.AddFolder(null, "Media");
        host.AddLibrary("/Media", "Media", contentType);
        await host.Sync.SyncAllAsync(false, null, Ct);
        var type = Assert.Single(host.Library.Calls("AddVirtualFolder"))[1];
        Assert.Equal(expected, type?.ToString());
    }

    [Fact]
    public async Task ShowFoldersAndSingleShows()
    {
        await using var host = await PluginHost.StartAsync();
        var m = host.Mega;
        var shows = m.AddFolder(null, "Shows");
        var showA = m.AddFolder(shows, "Show A");
        m.AddFile(m.AddFolder(showA, "Season 1"), "S01E01.mkv", TestData.Bytes(10));
        var single = m.AddFolder(null, "Breaking Bad");
        m.AddFile(m.AddFolder(single, "Saison 2"), "S02E01.mkv", TestData.Bytes(10));
        var flat = m.AddFolder(null, "Sherlock");
        m.AddFile(flat, "Sherlock S01E01.mkv", TestData.Bytes(10));

        var all = host.AddLibrary("/Shows", "Shows", "tvshows");
        var one = host.AddLibrary("/Breaking Bad", "Breaking Bad", "tvshows");
        var episodes = host.AddLibrary("/Sherlock", "Sherlock", "tvshows");
        await host.Sync.SyncAllAsync(false, null, Ct);

        Assert.Equal(new[] { "Show A/Season 1/S01E01.strm" }, Files(host.LibraryPath(all)));

        // A library pointed at one show: Jellyfin wants show folders at its root.
        Assert.Equal(new[] { "Breaking Bad/Saison 2/S02E01.strm" }, Files(host.LibraryPath(one)));
        Assert.Equal(new[] { "Sherlock/Sherlock S01E01.strm" }, Files(host.LibraryPath(episodes)));
    }

    [Fact]
    public async Task ExistingJellyfinLibraryGetsTheFolder()
    {
        await using var host = await PluginHost.StartAsync();
        host.Mega.AddFolder(null, "Films");
        host.VirtualFolders.Add(new VirtualFolderInfo { Name = "films", Locations = new[] { "/srv/films" } });
        var lib = host.AddLibrary("/Films", "Films");
        var manual = host.AddLibrary("/Films", "Manual");
        manual.CreateLibrary = false;
        await host.Sync.SyncAllAsync(false, null, Ct);

        Assert.Empty(host.Library.Calls("AddVirtualFolder"));
        var added = Assert.Single(host.Library.Calls("AddMediaPath"));
        Assert.Equal("films", added[0]);
        Assert.Equal(host.LibraryPath(lib), ((MediaPathInfo)added[1]!).Path);
        Assert.True(Directory.Exists(host.LibraryPath(manual)));
    }

    [Fact]
    public async Task ProblemsAreReportedPerLibrary()
    {
        await using var host = await PluginHost.StartAsync();
        host.Mega.AddFile(host.Mega.AddFolder(null, "Films"), "Movie.mkv", TestData.Bytes(10));
        var good = host.AddLibrary("/Films", "Good");
        var missing = host.AddLibrary("/Nope", "Missing");
        var file = host.AddLibrary("/Films/Movie.mkv", "File");
        var orphan = host.AddLibrary("/Films", "Orphan");
        orphan.AccountId = "deleted";
        var disabled = host.AddLibrary("/Films", "Disabled");
        disabled.Enabled = false;

        await host.Sync.SyncAllAsync(false, null, Ct);

        Assert.Null(host.Sync.GetStatus(good.Id)!.Error);
        Assert.Equal(1, host.Sync.GetStatus(good.Id)!.MediaFiles);
        Assert.Equal("MEGA folder not found: /Nope", host.Sync.GetStatus(missing.Id)!.Error);
        Assert.Equal("MEGA folder not found: /Films/Movie.mkv", host.Sync.GetStatus(file.Id)!.Error);
        Assert.Equal("MEGA account not found", host.Sync.GetStatus(orphan.Id)!.Error);
        Assert.Null(host.Sync.GetStatus(disabled.Id));
        Assert.False(Directory.Exists(host.LibraryPath(disabled)));
    }

    [Fact]
    public async Task DeletedLibraryLosesItsFolder()
    {
        await using var host = await PluginHost.StartAsync();
        host.Mega.AddFile(host.Mega.AddFolder(null, "Films"), "Movie.mkv", TestData.Bytes(10));
        var keep = host.AddLibrary("/Films", "Keep");
        var drop = host.AddLibrary("/Films", "Drop");
        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.True(Directory.Exists(host.LibraryPath(drop)));

        host.Config.Libraries.Remove(drop);
        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.False(Directory.Exists(host.LibraryPath(drop)));
        Assert.Equal(new[] { "Movie.strm" }, Files(host.LibraryPath(keep)));
    }

    [Fact]
    public async Task LibrariesStayWhenMegaIsUnreachable()
    {
        await using var host = await PluginHost.StartAsync();
        AddFilms(host.Mega);
        var lib = host.AddLibrary("/Films", "Films");
        await host.Sync.SyncAllAsync(false, null, Ct);
        var before = Files(host.LibraryPath(lib));

        host.Mega.ApiDown = true;
        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.Equal(before, Files(host.LibraryPath(lib)));
        Assert.Null(host.Sync.GetStatus(lib.Id)!.Error);

        // A full resync cannot run: reported, the files stay.
        await host.Sync.SyncAllAsync(true, null, Ct);
        Assert.Contains("HTTP 400", host.Sync.GetStatus(lib.Id)!.Error, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", host.Source.LastError, StringComparison.Ordinal);
        Assert.Equal(before, Files(host.LibraryPath(lib)));
    }

    [Fact]
    public async Task FailedCopyIsRetriedNextTime()
    {
        await using var host = await PluginHost.StartAsync();
        var films = host.Mega.AddFolder(null, "Films");
        host.Mega.AddFile(films, "Movie.srt", Srt);
        var lib = host.AddLibrary("/Films", "Films");
        host.Mega.StorageFaults.Enqueue(500);
        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.Empty(Files(host.LibraryPath(lib)));
        Assert.Null(host.Sync.GetStatus(lib.Id)!.Error);

        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.Equal(Srt, File.ReadAllBytes(Path.Combine(host.LibraryPath(lib), "Movie.srt")));
    }

    [Fact]
    public async Task LocalEditsAreUndone()
    {
        await using var host = await PluginHost.StartAsync();
        var films = host.Mega.AddFolder(null, "Films");
        var movie = host.Mega.AddFile(films, "Movie.mkv", TestData.Bytes(10));
        host.Mega.AddFile(films, "Movie.srt", Srt);
        var lib = host.AddLibrary("/Films", "Films");
        await host.Sync.SyncAllAsync(false, null, Ct);
        var dir = host.LibraryPath(lib);

        File.WriteAllText(Path.Combine(dir, "Movie.strm"), "http://elsewhere/\n");
        File.WriteAllText(Path.Combine(dir, "Movie.srt"), "changed");
        File.WriteAllText(Path.Combine(dir, "stray.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "Stray folder"));
        await host.Sync.SyncAllAsync(false, null, Ct);

        Assert.Equal(new[] { "Movie.srt", "Movie.strm" }, Files(dir));
        Assert.Equal(host.StreamUrl(movie) + "\n", File.ReadAllText(Path.Combine(dir, "Movie.strm")));
        Assert.Equal(Srt, File.ReadAllBytes(Path.Combine(dir, "Movie.srt")));
        Assert.False(Directory.Exists(Path.Combine(dir, "Stray folder")));
    }

    [Fact]
    public async Task AccountLibraries()
    {
        await using var host = await PluginHost.StartAsync(account: true);
        var movie = host.Mega.AddFile(host.Mega.AddFolder(null, "Films"), "Movie.mkv", TestData.Bytes(10));
        var lib = host.AddLibrary("/Films", "Films");
        await host.Sync.SyncAllAsync(false, null, Ct);
        Assert.Contains("/MegaFs/stream/acc1/" + movie + "/", File.ReadAllText(Path.Combine(host.LibraryPath(lib), "Movie.strm")), StringComparison.Ordinal);
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
