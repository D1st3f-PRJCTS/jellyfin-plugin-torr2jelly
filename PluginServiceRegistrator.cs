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
        serviceCollection.AddSingleton<TorrServerDaemonManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TorrServerDaemonManager>());
        serviceCollection.AddSingleton<TorrClientService>();
        serviceCollection.AddSingleton<StrmEngine>();
        serviceCollection.AddSingleton<ProwlarrSearchProvider>();
        serviceCollection.AddSingleton<TorznabSearchProvider>();
        serviceCollection.AddSingleton<TorrentSearchService>();
    }
}
