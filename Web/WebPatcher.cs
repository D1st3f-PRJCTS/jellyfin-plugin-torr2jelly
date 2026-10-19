using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Web;

public static class WebPatcher
{
    // Web client folders to try when the host does not report one (typical Docker / Debian / package layouts).
    private static readonly string[] CandidateWebDirs =
    {
        "/jellyfin/jellyfin-web",
        "/usr/share/jellyfin/web",
        "/var/lib/jellyfin/web"
    };

    // Every script tag an earlier release (or this one) injected, whatever its attributes or URL form.
    internal static readonly Regex ExistingScriptTag = new(
        @"<script\b[^>]*(?:/Plugins/Torr2Jelly/search\.js|torr2jellysearch)[^>]*>\s*</script>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The tag that loads the search integration. The URL is relative to <c>index.html</c> (<c>/web/index.html</c>),
    /// so it resolves to <c>{BaseUrl}/Plugins/Torr2Jelly/search.js</c> whatever base URL Jellyfin runs under; the
    /// version query makes a browser fetch a new script after a plugin update.
    /// </summary>
    internal static string BuildScriptTag()
    {
        var version = typeof(WebPatcher).Assembly.GetName().Version?.ToString() ?? "0";
        return $"<script defer src=\"../Plugins/Torr2Jelly/search.js?v={version}\"></script>";
    }

    public static void TryPatchWebClient(ILogger logger, string? webPath = null)
    {
        try
        {
            foreach (var dir in GetCandidateDirs(webPath))
            {
                var indexPath = Path.Combine(dir, "index.html");
                if (File.Exists(indexPath))
                {
                    PatchIndexHtml(indexPath, logger);
                    return;
                }
            }

            logger.LogInformation("Jellyfin web client index.html was not found; the Torr2Jelly search integration is not injected");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to inspect or patch Jellyfin web client index.html");
        }
    }

    private static IEnumerable<string> GetCandidateDirs(string? webPath)
    {
        if (!string.IsNullOrWhiteSpace(webPath))
        {
            yield return webPath;
        }

        foreach (var dir in CandidateWebDirs)
        {
            yield return dir;
        }
    }

    /// <summary>
    /// Returns <paramref name="html"/> with exactly one <paramref name="tag"/>: an older Torr2Jelly script tag is
    /// replaced (further copies are removed), otherwise the tag is added before <c>&lt;/head&gt;</c>. Returns
    /// <c>null</c> when there is nowhere to put it. <paramref name="changed"/> says whether the text differs.
    /// </summary>
    internal static string? ApplyScriptTag(string html, string tag, out bool changed)
    {
        changed = false;

        var matches = ExistingScriptTag.Matches(html);
        if (matches.Count == 1 && string.Equals(matches[0].Value, tag, StringComparison.Ordinal))
        {
            return html;
        }

        if (matches.Count > 0)
        {
            var first = true;
            var replaced = ExistingScriptTag.Replace(html, _ =>
            {
                if (first)
                {
                    first = false;
                    return tag;
                }

                return string.Empty;
            });

            changed = true;
            return replaced;
        }

        var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
        {
            return null;
        }

        changed = true;
        return html.Insert(headEnd, tag);
    }

    private static void PatchIndexHtml(string indexPath, ILogger logger)
    {
        try
        {
            var content = File.ReadAllText(indexPath);
            var patched = ApplyScriptTag(content, BuildScriptTag(), out var changed);
            if (patched == null)
            {
                logger.LogWarning("Could not find </head> tag in {Path}", indexPath);
                return;
            }

            if (!changed)
            {
                logger.LogInformation("Jellyfin web client at {Path} already contains the current Torr2Jelly search script", indexPath);
                return;
            }

            try
            {
                File.WriteAllText(indexPath, patched);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Expected on the official Docker image: the web directory is read-only and reset by every update.
                // WebIndexInjectionMiddleware adds the script while the page is served, so this is not a problem.
                logger.LogInformation("{Path} is read-only; the Torr2Jelly script is injected when the page is served instead", indexPath);
                return;
            }

            logger.LogInformation("Successfully injected Torr2Jelly search integration into {Path}", indexPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write patched index.html to {Path} (likely permission restriction)", indexPath);
        }
    }
}
