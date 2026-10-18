using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Web;

public static class WebPatcher
{
    private const string ScriptTag = "<script defer=\"defer\" src=\"/Plugins/Torr2Jelly/search.js\"></script>";

    private static readonly string[] CandidateWebPaths =
    {
        "/jellyfin/jellyfin-web/index.html",
        "/usr/share/jellyfin/web/index.html",
        "/var/lib/jellyfin/web/index.html"
    };

    public static void TryPatchWebClient(ILogger logger)
    {
        try
        {
            foreach (var path in CandidateWebPaths)
            {
                if (File.Exists(path))
                {
                    PatchIndexHtml(path, logger);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to inspect or patch Jellyfin web client index.html");
        }
    }

    private static void PatchIndexHtml(string indexPath, ILogger logger)
    {
        try
        {
            var content = File.ReadAllText(indexPath);
            if (content.Contains("torr2jellysearch") || content.Contains("/Plugins/Torr2Jelly/search.js"))
            {
                logger.LogInformation("Jellyfin web client at {Path} already contains Torr2Jelly search script", indexPath);
                return;
            }

            var headEnd = content.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headEnd < 0)
            {
                logger.LogWarning("Could not find </head> tag in {Path}", indexPath);
                return;
            }

            var patched = content.Insert(headEnd, ScriptTag);
            File.WriteAllText(indexPath, patched);
            logger.LogInformation("Successfully injected Torr2Jelly search integration into {Path}", indexPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write patched index.html to {Path} (likely permission restriction)", indexPath);
        }
    }
}
