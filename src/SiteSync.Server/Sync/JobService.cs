using System.Collections.Concurrent;
using SiteSync.Server.Connectors;
using SiteSync.Server.Domain;
using SiteSync.Server.Persistence;

namespace SiteSync.Server.Sync;

/// <summary>
/// Owns the lifecycle of sync jobs: starts previews/applies on background tasks,
/// tracks running jobs in memory, supports cancellation, persists results.
/// </summary>
public class JobService
{
    private readonly ConcurrentDictionary<string, SyncJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly FileStore _store;
    private readonly IConnectorFactory _connectors;
    private readonly IProgressPublisher _publisher;
    private readonly ILogger<JobService> _logger;

    public JobService(FileStore store, IConnectorFactory connectors, IProgressPublisher publisher, ILogger<JobService> logger)
    {
        _store = store;
        _connectors = connectors;
        _publisher = publisher;
        _logger = logger;
        foreach (var job in _store.LoadAllJobs())
        {
            // anything left "running" from a previous process is dead
            if (job.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Applying)
            {
                job.Status = JobStatus.Failed;
                job.Error = "Server restarted while the job was running.";
            }
            _jobs[job.Id] = job;
        }
    }

    public IReadOnlyCollection<SyncJobSummary> ListJobs() =>
        _jobs.Values.OrderByDescending(j => j.Created).Select(SyncJobSummary.From).ToList();

    public SyncJob? GetJob(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public SyncJob StartPreview(SyncJobRequest request)
    {
        var (sourceEnv, targetEnv) = ResolveEnvironments(request);

        var job = new SyncJob
        {
            Request = request,
            SourceEnvironmentName = sourceEnv.Name,
            TargetEnvironmentName = targetEnv.Name,
            Status = JobStatus.Running,
            Started = DateTime.UtcNow
        };
        _jobs[job.Id] = job;
        var cts = new CancellationTokenSource();
        _cancellations[job.Id] = cts;

        _ = Task.Run(async () =>
        {
            var engine = new SyncEngine(_publisher);
            try
            {
                var source = _connectors.Create(sourceEnv);
                var target = _connectors.Create(targetEnv);
                var snapshot = _store.LoadSnapshot(SyncEngine.PairKey(request));
                await engine.RunPreviewAsync(job, source, target, snapshot, cts.Token);
            }
            catch (OperationCanceledException)
            {
                job.Status = JobStatus.Cancelled;
                job.Error = "Cancelled by user.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preview job {JobId} failed", job.Id);
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
            }
            finally
            {
                job.Finished = job.Status == JobStatus.AwaitingReview ? null : DateTime.UtcNow;
                _store.SaveJob(job);
                await _publisher.PublishProgressAsync(SyncJobSummary.From(job));
                _cancellations.TryRemove(job.Id, out _);
            }
        });

        return job;
    }

    /// <summary>Apply a previewed job. Resolutions: itemId → UseSource|Skip for conflicts (and optional skips of any diff).</summary>
    public SyncJob StartApply(string jobId, Dictionary<string, ConflictResolution>? resolutions)
    {
        var job = GetJob(jobId) ?? throw new KeyNotFoundException($"Job {jobId} not found.");
        if (job.Status != JobStatus.AwaitingReview)
            throw new InvalidOperationException($"Job {jobId} is {job.Status}; only previewed jobs awaiting review can be applied.");

        if (resolutions != null)
            foreach (var diff in job.Diffs)
                if (resolutions.TryGetValue(diff.ItemId, out var res))
                    diff.Resolution = res;

        var (sourceEnv, targetEnv) = ResolveEnvironments(job.Request);
        job.Status = JobStatus.Applying;
        var cts = new CancellationTokenSource();
        _cancellations[job.Id] = cts;

        _ = Task.Run(async () =>
        {
            var engine = new SyncEngine(_publisher);
            try
            {
                var source = _connectors.Create(sourceEnv);
                var target = _connectors.Create(targetEnv);
                var previous = _store.LoadSnapshot(SyncEngine.PairKey(job.Request));
                var snapshot = await engine.RunApplyAsync(job, source, target, previous, cts.Token);
                _store.SaveSnapshot(snapshot);
                job.Status = JobStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                job.Status = JobStatus.Cancelled;
                job.Error = "Cancelled by user.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apply job {JobId} failed", job.Id);
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
            }
            finally
            {
                job.Finished = DateTime.UtcNow;
                _store.SaveJob(job);
                await _publisher.PublishProgressAsync(SyncJobSummary.From(job));
                _cancellations.TryRemove(job.Id, out _);
            }
        });

        return job;
    }

    public bool Cancel(string jobId)
    {
        if (!_cancellations.TryGetValue(jobId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    private (SitecoreEnvironment source, SitecoreEnvironment target) ResolveEnvironments(SyncJobRequest request)
    {
        var envs = _store.GetEnvironments();
        var source = envs.FirstOrDefault(e => e.Id == request.SourceEnvironmentId)
            ?? throw new KeyNotFoundException("Source environment not found.");
        var target = envs.FirstOrDefault(e => e.Id == request.TargetEnvironmentId)
            ?? throw new KeyNotFoundException("Target environment not found.");
        if (source.Id == target.Id && string.Equals(request.SourceDatabase, request.TargetDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and target are the same environment and database.");
        return (source, target);
    }
}
