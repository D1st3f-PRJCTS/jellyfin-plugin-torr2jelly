using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Api;

/// <summary>
/// The part of "add a torrent" that is the same whether the torrent came from a link or from an uploaded .torrent file:
/// everything after the torrent has been obtained from TorrServer (category, sync, result), plus the checks of an
/// uploaded file.
/// </summary>
internal static class TorrentIntake
{
    public const int MaxTitleLength = 300;

    private static readonly Regex ImdbIdRegex = new(@"^tt\d{1,10}$", RegexOptions.Compiled);

    /// <summary>Obtains the torrent from TorrServer: (title, poster, category) → torrent, or null.</summary>
    public delegate Task<TorrTorrent?> AddTorrent(string? title, string? poster, string? category, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a torrent to TorrServer through <paramref name="add"/> and, when asked, syncs it into the library.
    /// Responses and status codes are those of <c>POST Torrents</c>.
    /// </summary>
    public static async Task<ActionResult> AddAsync(
        TorrClientService torrClient,
        StrmEngine engine,
        ILogger logger,
        bool isAdministrator,
        AddTorrent add,
        string? requestedCategory,
        string? requestedTitle,
        string? posterUrl,
        int? requestedTmdbId,
        string? requestedImdbId,
        bool syncNow,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Only a configured category is passed on; anything else lets the engine route by what the release is.
        var category = config.Categories
            .FirstOrDefault(c => c != null && c.Name.Equals(requestedCategory?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;

        var title = string.IsNullOrWhiteSpace(requestedTitle) ? null : requestedTitle.Trim();
        if (title != null && title.Length > MaxTitleLength)
        {
            title = title.Substring(0, MaxTitleLength);
        }

        var tmdbId = requestedTmdbId > 0 ? requestedTmdbId : null;
        var imdbId = !string.IsNullOrWhiteSpace(requestedImdbId) && ImdbIdRegex.IsMatch(requestedImdbId.Trim()) ? requestedImdbId.Trim() : null;

        try
        {
            var torrent = await add(title, posterUrl, category, cancellationToken);
            if (torrent == null)
            {
                return Error(StatusCodes.Status502BadGateway, "TorrServer did not accept the torrent.");
            }

            if (category != null)
            {
                await torrClient.SetTorrentCategoryAsync(torrent.Hash, category, torrent.Title, torrent.Poster, cancellationToken);
            }

            if (syncNow)
            {
                var syncResult = await engine.SyncTorrentAsync(
                    torrent: torrent,
                    category: category,
                    customTitle: title,
                    posterUrl: posterUrl,
                    tmdbId: tmdbId,
                    imdbId: imdbId,
                    forceCategory: category != null,
                    cancellationToken: cancellationToken);

                // The torrent is in TorrServer, but without .strm files it is not in the library: that is a failure.
                // "Metadata not ready" is the exception: the files follow with the next sync, so it stays a 200.
                if (!syncResult.Success &&
                    !string.Equals(syncResult.ErrorMessage, StrmEngine.MetadataNotReadyMessage, StringComparison.Ordinal))
                {
                    return Error(
                        StatusCodes.Status500InternalServerError,
                        syncResult.ErrorMessage ?? "Failed to write the STRM files; see the Jellyfin log for details.");
                }

                return new OkObjectResult(isAdministrator ? syncResult : WithoutServerPaths(syncResult));
            }

            return new OkObjectResult(new StrmSyncResult
            {
                TorrentHash = torrent.Hash,
                TorrentTitle = torrent.Title,
                Success = true
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding torrent");
            return Error(StatusCodes.Status500InternalServerError, "Failed to add the torrent; see the Jellyfin log for details.");
        }
    }

    // Ordinary users get the outcome, not the layout of the server's file system.
    public static StrmSyncResult WithoutServerPaths(StrmSyncResult result)
    {
        return new StrmSyncResult
        {
            TorrentHash = result.TorrentHash,
            TorrentTitle = result.TorrentTitle,
            Category = result.Category,
            VideoFilesCount = result.VideoFilesCount,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage
        };
    }

    /// <summary>
    /// Reads and checks an uploaded .torrent file: extension, size (at most <see cref="TorrentFile.MaxBytes"/>) and
    /// bencode structure. The client's file name is only logged, sanitised and truncated. Returns the bytes, or a
    /// 400 result with a stable <c>code</c>.
    /// </summary>
    public static async Task<(byte[]? Data, ObjectResult? Error)> ReadTorrentFileAsync(IFormFile? file, ILogger logger, CancellationToken cancellationToken)
    {
        if (file == null)
        {
            return (null, FileError(TorrentFile.CodeRequired));
        }

        if (file.Length > TorrentFile.MaxBytes)
        {
            return (null, FileError(TorrentFile.CodeTooLarge));
        }

        byte[] data;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                if (buffer.Length + read > TorrentFile.MaxBytes)
                {
                    return (null, FileError(TorrentFile.CodeTooLarge));
                }

                buffer.Write(chunk, 0, read);
            }

            data = buffer.ToArray();
        }

        var code = TorrentFile.Validate(file.FileName, data);
        if (code != null)
        {
            logger.LogInformation("Rejected an uploaded torrent file '{Name}' ({Bytes} bytes): {Code}", LogName(file.FileName), data.Length, code);
            return (null, FileError(code));
        }

        logger.LogInformation("Received torrent file '{Name}' ({Bytes} bytes)", LogName(file.FileName), data.Length);
        return (data, null);
    }

    private static string LogName(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).Take(64).ToArray());
        return clean;
    }

    private static ObjectResult FileError(string code)
    {
        var message = code switch
        {
            TorrentFile.CodeRequired => "A .torrent file is required.",
            TorrentFile.CodeTooLarge => "The .torrent file is too large (4 MiB at most).",
            _ => "The file is not a valid .torrent file."
        };
        return new ObjectResult(new { error = message, code }) { StatusCode = StatusCodes.Status400BadRequest };
    }

    private static ObjectResult Error(int status, string message)
    {
        return new ObjectResult(new { error = message }) { StatusCode = status };
    }
}
