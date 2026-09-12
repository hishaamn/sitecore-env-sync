using System.Collections.Concurrent;
using SiteSync.Server.Connectors;
using SiteSync.Server.Domain;

namespace SiteSync.Server.Sync;

public interface IProgressPublisher
{
    Task PublishProgressAsync(SyncJobSummary summary);
    Task PublishLogAsync(string jobId, LogEntry entry);
}

/// <summary>
/// Executes the two halves of a sync: Preview (discover both trees, diff,
/// detect conflicts) and Apply (push selected changes source → target, then
/// snapshot revisions for the next run's conflict detection).
/// </summary>
public class SyncEngine
{
    private const int DiscoveryParallelism = 4;
    private readonly IProgressPublisher _publisher;
    private DateTime _lastPublish = DateTime.MinValue;

    public SyncEngine(IProgressPublisher publisher) => _publisher = publisher;

    public static string PairKey(SyncJobRequest r) =>
        $"{r.SourceEnvironmentId}-{r.SourceDatabase}--{r.TargetEnvironmentId}-{r.TargetDatabase}";

    // ---------------------------------------------------------------- preview

    public async Task RunPreviewAsync(SyncJob job, ISitecoreConnector source, ISitecoreConnector target,
        SyncSnapshot? snapshot, CancellationToken ct)
    {
        var req = job.Request;

        job.Phase = SyncPhase.Connecting;
        await Log(job, "info", $"Connecting to {job.SourceEnvironmentName} ({req.SourceDatabase}) and {job.TargetEnvironmentName} ({req.TargetDatabase})…");
        var srcTest = await source.TestConnectionAsync(ct);
        if (!srcTest.Success) throw new InvalidOperationException($"Source connection failed: {srcTest.Message}");
        var tgtTest = await target.TestConnectionAsync(ct);
        if (!tgtTest.Success) throw new InvalidOperationException($"Target connection failed: {tgtTest.Message}");
        await Log(job, "success", "Both environments reachable.");

        var roots = RootsFor(req.Scope);
        await Log(job, "info", $"Scope roots: {string.Join(", ", roots)}");

        job.Phase = SyncPhase.DiscoveringSource;
        var sourceItems = await DiscoverAsync(job, source, req.SourceDatabase, roots, "source", ct);
        job.Counters.SourceItems = sourceItems.Count;
        await Log(job, "info", $"Source: {sourceItems.Count} items discovered.");

        job.Phase = SyncPhase.DiscoveringTarget;
        var targetItems = await DiscoverAsync(job, target, req.TargetDatabase, roots, "target", ct);
        job.Counters.TargetItems = targetItems.Count;
        await Log(job, "info", $"Target: {targetItems.Count} items discovered.");

        job.Phase = SyncPhase.Comparing;
        job.CurrentItem = null;
        await Publish(job, force: true);
        var diffs = DiffCalculator.Compare(sourceItems, targetItems, snapshot);

        job.Diffs = diffs;
        job.Counters.New = diffs.Count(d => d.Status == DiffStatus.New);
        job.Counters.Modified = diffs.Count(d => d.Status == DiffStatus.Modified);
        job.Counters.Deleted = diffs.Count(d => d.Status == DiffStatus.Deleted);
        job.Counters.Conflicts = diffs.Count(d => d.Status == DiffStatus.Conflict);
        job.Counters.Unchanged = diffs.Count(d => d.Status == DiffStatus.Unchanged);

        await Log(job, "success",
            $"Comparison done: {job.Counters.New} new, {job.Counters.Modified} modified, " +
            $"{job.Counters.Deleted} only-on-target, {job.Counters.Conflicts} conflicts, {job.Counters.Unchanged} unchanged.");
        if (job.Counters.Conflicts > 0)
            await Log(job, "warn", $"{job.Counters.Conflicts} conflicting item(s) need a resolution before they are applied.");

        job.Phase = SyncPhase.Done;
        job.Status = JobStatus.AwaitingReview;
        await Publish(job, force: true);
    }

    public static List<string> RootsFor(SyncScope scope)
    {
        var roots = new List<string>();
        if (scope.Content && !string.IsNullOrWhiteSpace(scope.RootPath)) roots.Add(scope.RootPath.TrimEnd('/'));
        if (scope.Media) roots.Add("/sitecore/media library");
        if (scope.Templates) roots.Add("/sitecore/templates");
        if (scope.Layout) roots.Add("/sitecore/layout");
        // remove roots nested under another selected root
        return roots
            .Where(r => !roots.Any(o => o != r && r.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, SitecoreItem>> DiscoverAsync(SyncJob job, ISitecoreConnector conn,
        string database, IReadOnlyList<string> roots, string side, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<string, SitecoreItem>();
        var queue = new ConcurrentQueue<SitecoreItem>();

        foreach (var rootPath in roots)
        {
            var root = await conn.GetItemAsync(rootPath, database, ct);
            if (root == null)
            {
                await Log(job, "warn", $"Root '{rootPath}' not found on {side} ({database}) — skipped.");
                continue;
            }
            if (found.TryAdd(root.Id, root)) queue.Enqueue(root);
        }

        int processed = 0;
        async Task Worker()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!queue.TryDequeue(out var item))
                {
                    // brief idle wait; exit when queue stays empty and others are idle too
                    await Task.Delay(25, ct);
                    if (queue.IsEmpty) return;
                    continue;
                }

                var children = await conn.GetChildrenAsync(item.Id, database, ct);
                foreach (var child in children)
                    if (found.TryAdd(child.Id, child))
                        queue.Enqueue(child);

                var p = Interlocked.Increment(ref processed);
                job.Processed = p;
                job.Total = 0; // discovery size is unknown up front
                job.CurrentItem = item.Path;
                await Publish(job);
            }
        }

        // N workers; the idle-exit heuristic above is safe because GetChildren latency
        // far exceeds the 25ms idle window only when the queue genuinely drains.
        var workers = Enumerable.Range(0, DiscoveryParallelism).Select(_ => Worker()).ToArray();
        await Task.WhenAll(workers);
        return new Dictionary<string, SitecoreItem>(found);
    }

    // ------------------------------------------------------------------ apply

    public async Task<SyncSnapshot> RunApplyAsync(SyncJob job, ISitecoreConnector source, ISitecoreConnector target,
        SyncSnapshot? previousSnapshot, CancellationToken ct)
    {
        var req = job.Request;
        job.Phase = SyncPhase.Applying;

        var toApply = job.Diffs.Where(d => IsApplicable(d, req.Scope)).ToList();
        var creates = toApply.Where(d => d.Status == DiffStatus.New).OrderBy(Depth).ToList();
        var updates = toApply.Where(d => d.Status is DiffStatus.Modified or DiffStatus.Conflict).ToList();
        var deletes = toApply.Where(d => d.Status == DiffStatus.Deleted).OrderByDescending(Depth).ToList();

        job.Total = creates.Count + updates.Count + deletes.Count;
        job.Processed = 0;
        await Log(job, "info", $"Applying {job.Total} change(s): {creates.Count} create, {updates.Count} update, {deletes.Count} delete.");
        await Publish(job, force: true);

        var snapshot = new SyncSnapshot
        {
            PairKey = PairKey(req),
            Taken = DateTime.UtcNow,
            Items = previousSnapshot?.Items != null
                ? new Dictionary<string, ItemRevisionPair>(previousSnapshot.Items)
                : new Dictionary<string, ItemRevisionPair>()
        };

        foreach (var diff in creates.Concat(updates))
        {
            ct.ThrowIfCancellationRequested();
            job.CurrentItem = diff.Path;
            try
            {
                var srcItem = await source.GetItemAsync(diff.ItemId, req.SourceDatabase, ct)
                    ?? throw new InvalidOperationException("Item vanished from source since preview.");

                var fields = srcItem.Fields
                    .Where(f => !DiffCalculator.ExcludedFields.Contains(f.Key))
                    .ToDictionary(f => f.Key, f => f.Value);

                if (diff.Status == DiffStatus.New)
                {
                    await target.CreateItemAsync(srcItem.ParentId, srcItem.Id, srcItem.Name,
                        srcItem.TemplateId, fields, req.TargetDatabase, ct);
                    await Log(job, "success", $"Created {diff.Path}");
                }
                else
                {
                    await target.UpdateItemAsync(diff.ItemId, fields, req.TargetDatabase, ct);
                    await Log(job, "success", $"Updated {diff.Path}" +
                        (diff.Status == DiffStatus.Conflict ? " (conflict resolved with source version)" : ""));
                }

                if (diff.IsMedia)
                {
                    var blob = await source.DownloadMediaAsync(diff.ItemId, req.SourceDatabase, ct);
                    if (blob != null)
                    {
                        var ext = srcItem.Fields.TryGetValue("Extension", out var e) && e.Length > 0 ? e : "bin";
                        await target.UploadMediaAsync(diff.ItemId, blob, $"{srcItem.Name}.{ext}", req.TargetDatabase, ct);
                        await Log(job, "info", $"Transferred media blob for {diff.Path} ({blob.Length:N0} bytes)");
                    }
                }

                var freshTarget = await target.GetItemAsync(diff.ItemId, req.TargetDatabase, ct);
                snapshot.Items[diff.ItemId] = new ItemRevisionPair
                {
                    SourceRevision = srcItem.Revision,
                    TargetRevision = freshTarget?.Revision ?? ""
                };
                job.Counters.Applied++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                job.Counters.Errors++;
                await Log(job, "error", $"Failed on {diff.Path}: {ex.Message}");
            }
            job.Processed++;
            await Publish(job);
        }

        foreach (var diff in deletes)
        {
            ct.ThrowIfCancellationRequested();
            job.CurrentItem = diff.Path;
            try
            {
                await target.DeleteItemAsync(diff.ItemId, req.TargetDatabase, ct);
                snapshot.Items.Remove(diff.ItemId);
                job.Counters.Applied++;
                await Log(job, "success", $"Deleted {diff.Path} (not present on source)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                job.Counters.Errors++;
                await Log(job, "error", $"Failed deleting {diff.Path}: {ex.Message}");
            }
            job.Processed++;
            await Publish(job);
        }

        // Baseline unchanged items too, so future edits on both sides are caught as conflicts.
        job.Phase = SyncPhase.Snapshotting;
        await Publish(job, force: true);
        foreach (var diff in job.Diffs.Where(d => d.Status == DiffStatus.Unchanged))
        {
            snapshot.Items[diff.ItemId] = new ItemRevisionPair
            {
                SourceRevision = diff.SourceRevision,
                TargetRevision = diff.TargetRevision
            };
        }

        var skipped = job.Diffs.Count(d =>
            d.Status is DiffStatus.Conflict && d.Resolution != ConflictResolution.UseSource);
        job.Counters.Skipped = skipped;
        if (skipped > 0)
            await Log(job, "warn", $"{skipped} conflicting item(s) were skipped (no resolution chosen).");

        job.CurrentItem = null;
        job.Phase = SyncPhase.Done;
        await Log(job, job.Counters.Errors == 0 ? "success" : "warn",
            $"Apply finished: {job.Counters.Applied} applied, {skipped} skipped, {job.Counters.Errors} error(s).");
        return snapshot;
    }

    private static bool IsApplicable(ItemDiff d, SyncScope scope) => d.Status switch
    {
        DiffStatus.New or DiffStatus.Modified => d.Resolution != ConflictResolution.Skip,
        DiffStatus.Conflict => d.Resolution == ConflictResolution.UseSource,
        DiffStatus.Deleted => scope.DeleteOrphans && d.Resolution != ConflictResolution.Skip,
        _ => false
    };

    private static int Depth(ItemDiff d) => d.Path.Count(c => c == '/');

    // ------------------------------------------------------------------ infra

    private async Task Log(SyncJob job, string level, string message)
    {
        var entry = new LogEntry { Time = DateTime.UtcNow, Level = level, Message = message };
        lock (job.Log) job.Log.Add(entry);
        await _publisher.PublishLogAsync(job.Id, entry);
    }

    private async Task Publish(SyncJob job, bool force = false)
    {
        // Throttle progress frames so big syncs don't flood the websocket.
        var now = DateTime.UtcNow;
        if (!force && (now - _lastPublish).TotalMilliseconds < 120) return;
        _lastPublish = now;
        await _publisher.PublishProgressAsync(SyncJobSummary.From(job));
    }
}
