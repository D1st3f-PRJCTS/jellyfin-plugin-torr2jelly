using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Api;

[ApiController]
[Route("Plugins/Torr2Jelly/Discover")]
[Authorize(Policy = ApiPolicies.RequiresElevation)]
public sealed class MovieDiscoveryController(MovieDiscoveryService discovery, DiscoveryJobs jobs, ILogger<MovieDiscoveryController> logger) : ControllerBase
{
    [HttpGet("Movies")]
    public Task<ActionResult<List<DiscoveryMovieDto>>> Movies([FromQuery] string window = "week", CancellationToken cancellationToken = default)
        => Run(async ct =>
        {
            if (window is not ("day" or "week")) throw new DiscoveryException("invalid_window");
            return await discovery.TrendingAsync(window, ct);
        }, cancellationToken);

    [HttpGet("Movies/{id:int}")]
    public Task<ActionResult<DiscoveryMovieDto>> Details(int id, CancellationToken cancellationToken)
        => Run(async ct =>
        {
            if (id <= 0) throw new DiscoveryException("invalid_id");
            var movie = await discovery.DetailsAsync(id, ct);
            movie.ActiveJobId = jobs.ActiveFor(id);
            return movie;
        }, cancellationToken);

    [HttpPost("Movies/{id:int}/Add")]
    public Task<ActionResult<DiscoveryJobDto>> Add(int id, CancellationToken cancellationToken)
        => Run(_ => Task.FromResult(jobs.Start(id)), cancellationToken, accepted: true);

    [HttpGet("Jobs/{jobId:guid}")]
    public ActionResult<DiscoveryJobDto> Job(Guid jobId)
        => jobs.Get(jobId) is { } job ? Ok(job) : NotFound(new { code = "job_not_found" });

    private async Task<ActionResult<T>> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct, bool accepted = false)
    {
        try { var result = await action(ct); return accepted ? Accepted(result) : Ok(result); }
        catch (TmdbException ex) { return StatusCode(ex.Code == TmdbClient.CodeKeyMissing ? 503 : 502, new { code = ex.Code }); }
        catch (DiscoveryException ex) { return StatusCode(ex.Code.StartsWith("invalid_", StringComparison.Ordinal) ? 400 : 503, new { code = ex.Code }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Movie discovery operation failed");
            return StatusCode(500, new { code = "discovery_failed" });
        }
    }
}
