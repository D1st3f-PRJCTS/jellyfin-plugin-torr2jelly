using Jellyfin.Plugin.Torr2Jelly.Search.Models;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

public sealed record CheckedSearchResult(List<TorrentSearchResult> Results, bool HadFailures);
