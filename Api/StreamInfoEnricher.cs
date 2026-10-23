using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Engine;

namespace Jellyfin.Plugin.Torr2Jelly.Api;

/// <summary>Adds TorrServer's size, live swarm numbers and title to the version rows of an item.</summary>
internal static class StreamInfoEnricher
{
    /// <summary>
    /// Fills <see cref="MovieStreamInfoDto.SizeBytes"/>, <see cref="MovieStreamInfoDto.Seeders"/>,
    /// <see cref="MovieStreamInfoDto.Peers"/> and <see cref="MovieStreamInfoDto.TorrentTitle"/> from one torrent list.
    /// Everything stays null for a stream whose hash TorrServer does not know (or when the list is null);
    /// a count of 0 is "no live numbers" and also stays null.
    /// </summary>
    public static void Apply(IEnumerable<MovieStreamInfoDto> streams, IReadOnlyList<TorrTorrent>? torrents)
    {
        if (torrents == null || torrents.Count == 0)
        {
            return;
        }

        var byHash = new Dictionary<string, TorrTorrent>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var torrent in torrents)
        {
            if (torrent != null && !string.IsNullOrWhiteSpace(torrent.Hash))
            {
                byHash.TryAdd(torrent.Hash.Trim(), torrent);
            }
        }

        foreach (var stream in streams)
        {
            if (string.IsNullOrWhiteSpace(stream.TorrentHash) || !byHash.TryGetValue(stream.TorrentHash, out var torrent))
            {
                continue;
            }

            stream.TorrentTitle = string.IsNullOrWhiteSpace(torrent.Title) ? null : torrent.Title.Trim();
            stream.SizeBytes = SizeOf(torrent);
            stream.Seeders = torrent.ConnectedSeeders is > 0 ? torrent.ConnectedSeeders : null;
            stream.Peers = torrent.ActivePeers is > 0
                ? torrent.ActivePeers
                : (torrent.TotalPeers is > 0 ? torrent.TotalPeers : null);
        }
    }

    private static long? SizeOf(TorrTorrent torrent)
    {
        if (torrent.TorrentSize > 0)
        {
            return torrent.TorrentSize;
        }

        var main = torrent.GetEffectiveFiles().Select(f => f.Length).DefaultIfEmpty(0).Max();
        return main > 0 ? main : null;
    }
}
