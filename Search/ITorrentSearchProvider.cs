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

    /// <summary>
    /// Asks the indexers for one title by its external id. Indexers that cannot search by id contribute nothing, so
    /// the result supplements a search by name and never replaces it.
    /// </summary>
    Task<List<TorrentSearchResult>> SearchByIdAsync(MediaSearchIds ids, IEnumerable<int>? categories = null, CancellationToken cancellationToken = default);
}
