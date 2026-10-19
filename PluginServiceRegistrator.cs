using System;
using System.Net;
using System.Net.Http;
using MediaBrowser.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.Torr2Jelly.Daemon;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Jellyfin.Plugin.Torr2Jelly.Search;

namespace Jellyfin.Plugin.Torr2Jelly;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();

        // Link resolution must see a redirect to a magnet link instead of following it (nothing can fetch "magnet:").
        serviceCollection.AddHttpClient(TorrentLinkResolver.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            });

        serviceCollection.AddSingleton<TorrServerDaemonManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TorrServerDaemonManager>());
        serviceCollection.AddSingleton<TorrClientService>();
        serviceCollection.AddSingleton<TorrentLinkResolver>();
        serviceCollection.AddSingleton<StrmEngine>();
        serviceCollection.AddSingleton<ProwlarrSearchProvider>();
        serviceCollection.AddSingleton<TorznabSearchProvider>();
        serviceCollection.AddSingleton<TorrentSearchService>();
    }
}
