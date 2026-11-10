using Jellyfin.Plugin.Mega.Api;
using Jellyfin.Plugin.Mega.Library;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Subtitles;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Mega;

/// <summary>Registers the plugin services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MegaManager>();
        serviceCollection.AddSingleton<LibrarySynchronizer>();
        serviceCollection.AddSingleton<SubtitlePrefill>();
        serviceCollection.AddHostedService<MegaBackgroundService>();
        serviceCollection.AddHostedService<MegaProbeQueue>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<MegaPlaybackFilter>());
    }
}
