using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

public interface ITorrentSearchProvider
{
    string Name { get; }
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<List<TorrentSearchResult>> SearchAsync(string query, IEnumerable<int>? categories = null, CancellationToken cancellationToken = default);
}
