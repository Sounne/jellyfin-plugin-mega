using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Mega.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Mega;

/// <summary>MEGA cloud storage as Jellyfin libraries: end-to-end decryption, virtual file system, streaming.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "61a62eb4-5383-489b-8150-3b595e6a6400";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "MEGA";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Play your MEGA cloud drive in Jellyfin: end-to-end decryption, virtual libraries, streaming with seeking, MEGA previews.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MEGA",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
            new PluginPageInfo
            {
                Name = "megaconfigjs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js",
            },
        };
    }
}
