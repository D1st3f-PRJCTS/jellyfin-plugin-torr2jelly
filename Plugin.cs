using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Torr2Jelly;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "Torr2Jelly";

    public override Guid Id => Guid.Parse("b1a7d6e4-4f8a-4c9b-9d3e-7a1b2c3d4e5f");

    public override string Description => "Standalone TorrServer torrent streaming & Prowlarr/Torznab integration for Jellyfin";

    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Configuration?.DeduplicateCategories();
        Configuration?.NormalizeTrackerRules();
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "torr2jelly",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jelly.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "cloud_download",
                DisplayName = "Torr2Jelly"
            },
            new PluginPageInfo
            {
                Name = "torr2jellyjs",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jelly.js"
            },
            new PluginPageInfo
            {
                Name = "torr2jellycss",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jelly.css"
            },
            new PluginPageInfo
            {
                Name = "torr2jellysearch",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jellysearch.js"
            }
        };
    }
}
