using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Torr2Jelly.Web;

/// <summary>
/// Puts <see cref="WebIndexInjectionMiddleware"/> at the very start of Jellyfin's request pipeline, ahead of the
/// base URL handling, response compression and static files, so it sees (and can edit) the web client's
/// <c>index.html</c> exactly as it is about to go out.
/// </summary>
public sealed class WebIndexInjectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<WebIndexInjectionMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Adds the Torr2Jelly <c>&lt;script&gt;</c> tag to the web client's <c>index.html</c> while it is being served.
/// Patching the file on disk does not work where the web directory is read-only (the official Docker image) and is
/// undone by every Jellyfin update; editing the response needs neither write access nor a re-patch.
/// </summary>
internal sealed class WebIndexInjectionMiddleware
{
    private const string HtmlEnd = "</head>";

    // Throws on malformed input so the page is passed through byte for byte instead of being mangled.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly RequestDelegate _next;
    private readonly ILogger<WebIndexInjectionMiddleware> _logger;
    private readonly string _scriptTag;

    public WebIndexInjectionMiddleware(RequestDelegate next, ILogger<WebIndexInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _scriptTag = WebPatcher.BuildScriptTag();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsWebIndexRequest(context.Request))
        {
            await _next(context);
            return;
        }

        var request = context.Request;
        var response = context.Response;

        // The page has to reach this middleware as plain text, unchanged and complete: no compression (response
        // compression would otherwise encode what is edited here), no 304 and no partial content.
        request.Headers.Remove(HeaderNames.AcceptEncoding);
        request.Headers.Remove(HeaderNames.IfNoneMatch);
        request.Headers.Remove(HeaderNames.IfModifiedSince);
        request.Headers.Remove(HeaderNames.Range);
        request.Headers.Remove(HeaderNames.IfRange);

        var originalBody = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await _next(context);
        }
        finally
        {
            response.Body = originalBody;
        }

        if (!buffer.TryGetBuffer(out var original))
        {
            original = new ArraySegment<byte>(buffer.ToArray());
        }

        ReadOnlyMemory<byte> payload = original;
        if (IsRewritable(response))
        {
            byte[]? rewritten = null;
            try
            {
                rewritten = InjectScriptTag(original);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not add the Torr2Jelly script to the served web client index.html; serving it unchanged");
            }

            if (rewritten is not null)
            {
                // The validators describe the file on disk, not the edited page.
                response.Headers.Remove(HeaderNames.ETag);
                response.Headers.Remove(HeaderNames.LastModified);
                response.ContentLength = rewritten.Length;
                payload = rewritten;
            }
        }

        // Nothing to write for an empty body (redirect, 404, ...): writing would only force the headers out early.
        if (payload.Length > 0)
        {
            await originalBody.WriteAsync(payload, context.RequestAborted);
        }
    }

    /// <summary>GET of <c>{BaseUrl}/web/</c> or <c>{BaseUrl}/web/index.html</c>, the two URLs that deliver the page.</summary>
    private static bool IsWebIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        // The base URL is part of the path seen here; PathBase is normally empty this early in the pipeline.
        var path = string.Concat(request.PathBase.Value, request.Path.Value);
        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRewritable(HttpResponse response)
    {
        return response.StatusCode == StatusCodes.Status200OK
            && response.ContentType is { } contentType
            && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            && response.Headers.ContentEncoding.Count == 0;
    }

    /// <summary>The page with the script tag added, or <c>null</c> when it already has one or has no <c>&lt;/head&gt;</c>.</summary>
    private byte[]? InjectScriptTag(ArraySegment<byte> page)
    {
        var html = StrictUtf8.GetString(page);
        if (WebPatcher.ExistingScriptTag.IsMatch(html))
        {
            return null;
        }

        var headEnd = html.IndexOf(HtmlEnd, StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
        {
            return null;
        }

        return StrictUtf8.GetBytes(string.Concat(html.AsSpan(0, headEnd), _scriptTag, html.AsSpan(headEnd)));
    }
}
