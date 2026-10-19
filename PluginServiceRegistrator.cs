using System;
using System.Net;
using System.Net.Http;
using MediaBrowser.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.Torr2Jelly.Daemon;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Jellyfin.Plugin.Torr2Jelly.Search;
using Jellyfin.Plugin.Torr2Jelly.Web;

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

        // Adds the search script to the web client's index.html while it is served. The web directory is read-only
        // in the official Docker image and reset by every update, so the file itself cannot be patched there.
        serviceCollection.AddTransient<IStartupFilter, WebIndexInjectionStartupFilter>();
    }
}
