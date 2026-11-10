using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Streaming;
using MediaBrowser.Controller.Extensions;
using JellyfinConfig = MediaBrowser.Controller.Extensions.ConfigurationExtensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>Short ffmpeg analysis for MEGA items only, through Jellyfin's own configuration lookup.</summary>
public sealed class FfmpegAnalysisTests
{
    private static readonly Dictionary<string, string?> JellyfinDefaults = new()
    {
        [JellyfinConfig.FfmpegAnalyzeDurationKey] = "200M",
        [JellyfinConfig.FfmpegProbeSizeKey] = "1G",
    };

    private static IConfiguration Make(string kind)
    {
        if (kind == "root")
        {
            // What the generic host builds (Jellyfin's IConfiguration).
            return new ConfigurationBuilder().AddInMemoryCollection(JellyfinDefaults).Build();
        }

        var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(JellyfinDefaults);
        return manager;
    }

    [Fact]
    public void SameKeyAsJellyfin() => Assert.Equal(JellyfinConfig.FfmpegAnalyzeDurationKey, FfmpegAnalysis.AnalyzeDurationKey);

    [Theory]
    [InlineData("root")]
    [InlineData("manager")]
    public void OnlyInsideTheScope(string kind)
    {
        var config = Make(kind);
        Assert.True(FfmpegAnalysis.Install(config, () => 10, NullLogger.Instance));
        Assert.True(FfmpegAnalysis.Install(config, () => 10, NullLogger.Instance));

        Assert.Equal("200M", config.GetFFmpegAnalyzeDuration());
        using (FfmpegAnalysis.Enter())
        {
            Assert.True(FfmpegAnalysis.IsActive);
            Assert.Equal("10000000", config.GetFFmpegAnalyzeDuration());
            Assert.Equal("10000000", config["ffmpeg:ANALYZEDURATION"]);
            Assert.Equal("1G", config.GetFFmpegProbeSize());
            using (FfmpegAnalysis.Enter())
            {
                Assert.Equal("10000000", config.GetFFmpegAnalyzeDuration());
            }

            Assert.Equal("10000000", config.GetFFmpegAnalyzeDuration());
        }

        Assert.False(FfmpegAnalysis.IsActive);
        Assert.Equal("200M", config.GetFFmpegAnalyzeDuration());
        Assert.Equal(2, config.GetSection("FFmpeg").GetChildren().CountItems());

        // Settings written at run time still work.
        config[JellyfinConfig.FfmpegAnalyzeDurationKey] = "300M";
        Assert.Equal("300M", config.GetFFmpegAnalyzeDuration());
    }

    [Fact]
    public void ZeroKeepsJellyfinsSetting()
    {
        var config = Make("root");
        int seconds = 0;
        FfmpegAnalysis.Install(config, () => seconds, NullLogger.Instance);
        using (FfmpegAnalysis.Enter())
        {
            Assert.Equal("200M", config.GetFFmpegAnalyzeDuration());
            seconds = 5;
            Assert.Equal("5000000", config.GetFFmpegAnalyzeDuration());
        }
    }

    [Fact]
    public async Task ConcurrentRequestsDoNotLeak()
    {
        var config = Make("root");
        FfmpegAnalysis.Install(config, () => 10, NullLogger.Instance);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string?> Mega()
        {
            using var scope = FfmpegAnalysis.Enter();
            await go.Task;
            await Task.Yield();
            return config.GetFFmpegAnalyzeDuration();
        }

        async Task<string?> Local()
        {
            await go.Task;
            await Task.Yield();
            return config.GetFFmpegAnalyzeDuration();
        }

        var mega = new List<Task<string?>>();
        var local = new List<Task<string?>>();
        for (int i = 0; i < 20; i++)
        {
            mega.Add(Task.Run(Mega));
            local.Add(Task.Run(Local));
        }

        go.SetResult();
        Assert.All(await Task.WhenAll(mega), v => Assert.Equal("10000000", v));
        Assert.All(await Task.WhenAll(local), v => Assert.Equal("200M", v));
    }
}

internal static class EnumerableCount
{
    public static int CountItems<T>(this IEnumerable<T> items)
    {
        int n = 0;
        foreach (var unused in items)
        {
            n++;
        }

        return n;
    }
}
