using System.Threading.Channels;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>Short HTTP requests start/poll bounded background work. A proxy timeout cannot cancel an intake.</summary>
public sealed class DiscoveryJobs(MovieDiscoveryService discovery, ILogger<DiscoveryJobs> logger) : BackgroundService
{
    private sealed class Job(int movieId)
    {
        public Guid Id = Guid.NewGuid();
        public int MovieId = movieId;
        public string Status = "queued";
        public DateTime At = DateTime.UtcNow;
        public DiscoveryAddResultDto? Result;
        public string? Code;
        public DiscoveryJobDto Dto() => new(Id, MovieId, Status, Result, Code);
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(16);

    public DiscoveryJobDto Start(int id)
    {
        if (id <= 0) throw new DiscoveryException("invalid_id");
        lock (_gate)
        {
            var active = _jobs.Values.FirstOrDefault(j => j.MovieId == id && j.Status is "queued" or "running");
            if (active != null) return active.Dto();
            foreach (var key in _jobs.Where(p => p.Value.Status is "completed" or "failed" && DateTime.UtcNow - p.Value.At > TimeSpan.FromHours(1)).Select(p => p.Key).ToList()) _jobs.Remove(key);
            if (_jobs.Count >= 128) throw new DiscoveryException("discovery_busy");
            var job = new Job(id);
            _jobs.Add(job.Id, job);
            if (!_queue.Writer.TryWrite(job)) { _jobs.Remove(job.Id); throw new DiscoveryException("discovery_busy"); }
            return job.Dto();
        }
    }

    public DiscoveryJobDto? Get(Guid id) { lock (_gate) return _jobs.GetValueOrDefault(id)?.Dto(); }
    public Guid? ActiveFor(int movie) { lock (_gate) return _jobs.Values.FirstOrDefault(j => j.MovieId == movie && j.Status is "queued" or "running")?.Id; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup recovery runs before new jobs; retry periodic cleanup while the queue is idle too.
        var nextRecovery = DateTime.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextRecovery)
                {
                    try { await discovery.RecoverAsync(stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "Discovery recovery could not finish; it will be retried"); }
                    nextRecovery = DateTime.UtcNow.AddMinutes(5);
                }
                if (!_queue.Reader.TryRead(out var job)) { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); continue; }
                lock (_gate) job.Status = "running";
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    var result = await discovery.AddAsync(job.MovieId, budget.Token);
                    lock (_gate) { job.Result = result; job.Status = "completed"; job.At = DateTime.UtcNow; }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var code = ex is TmdbException tmdb ? tmdb.Code : ex is DiscoveryException failure ? failure.Code : ex is OperationCanceledException ? "discovery_timeout" : "discovery_failed";
                    lock (_gate) { job.Code = code; job.Status = "failed"; job.At = DateTime.UtcNow; }
                    logger.LogWarning("Discovery job {Job} ended with {Code}", job.Id, code);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
