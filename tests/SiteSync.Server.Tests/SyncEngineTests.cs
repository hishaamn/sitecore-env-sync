using SiteSync.Server.Connectors;
using SiteSync.Server.Domain;
using SiteSync.Server.Sync;
using Xunit;

namespace SiteSync.Server.Tests;

/// <summary>End-to-end engine tests over the simulated connector (no HTTP).</summary>
public class SyncEngineTests
{
    private class NullPublisher : IProgressPublisher
    {
        public Task PublishProgressAsync(SyncJobSummary summary) => Task.CompletedTask;
        public Task PublishLogAsync(string jobId, LogEntry entry) => Task.CompletedTask;
    }

    private static SitecoreEnvironment Env(string name) => new()
    {
        Id = name,
        Name = name,
        ConnectorType = ConnectorType.Simulated,
        Databases = new() { "master", "web" }
    };

    private static SyncJob NewJob(string sourceEnv, string sourceDb, string targetEnv, string targetDb) => new()
    {
        Request = new SyncJobRequest
        {
            SourceEnvironmentId = sourceEnv,
            SourceDatabase = sourceDb,
            TargetEnvironmentId = targetEnv,
            TargetDatabase = targetDb,
            Scope = new SyncScope
            {
                Content = true,
                RootPath = "/sitecore/content",
                Media = true,
                Templates = true,
                Layout = true,
                DeleteOrphans = true
            }
        },
        SourceEnvironmentName = sourceEnv,
        TargetEnvironmentName = targetEnv
    };

    [Fact]
    public async Task Preview_between_master_and_web_finds_expected_change_kinds()
    {
        // unique env name per test: the simulated store is process-wide
        var env = Env("engine-test-1");
        var connector = new SimulatedConnector(env);
        var job = NewJob(env.Id, "master", env.Id, "web");

        var engine = new SyncEngine(new NullPublisher());
        await engine.RunPreviewAsync(job, connector, connector, snapshot: null, CancellationToken.None);

        Assert.Equal(JobStatus.AwaitingReview, job.Status);
        Assert.True(job.Counters.New > 0, "expected items that only exist on master");
        Assert.True(job.Counters.Modified > 0, "expected modified items");
        Assert.True(job.Counters.Deleted > 0, "expected the web-only orphan item");
        Assert.True(job.Counters.Conflicts > 0, "expected the Contact page conflict");
        Assert.True(job.Counters.Unchanged > 0, "expected most items unchanged");
    }

    [Fact]
    public async Task Apply_then_repreview_converges_to_no_differences()
    {
        var env = Env("engine-test-2");
        var connector = new SimulatedConnector(env);
        var engine = new SyncEngine(new NullPublisher());

        var job = NewJob(env.Id, "master", env.Id, "web");
        await engine.RunPreviewAsync(job, connector, connector, snapshot: null, CancellationToken.None);

        // resolve every conflict in favour of the source, then apply
        foreach (var d in job.Diffs.Where(d => d.Status == DiffStatus.Conflict))
            d.Resolution = ConflictResolution.UseSource;
        var snapshot = await engine.RunApplyAsync(job, connector, connector, null, CancellationToken.None);

        Assert.Equal(0, job.Counters.Errors);
        Assert.True(job.Counters.Applied > 0);

        var job2 = NewJob(env.Id, "master", env.Id, "web");
        await engine.RunPreviewAsync(job2, connector, connector, snapshot, CancellationToken.None);

        Assert.Equal(0, job2.Counters.New);
        Assert.Equal(0, job2.Counters.Modified);
        Assert.Equal(0, job2.Counters.Deleted);
        Assert.Equal(0, job2.Counters.Conflicts);
    }

    [Fact]
    public async Task Skipped_conflict_is_not_written_to_target()
    {
        var env = Env("engine-test-3");
        var connector = new SimulatedConnector(env);
        var engine = new SyncEngine(new NullPublisher());

        var job = NewJob(env.Id, "master", env.Id, "web");
        await engine.RunPreviewAsync(job, connector, connector, snapshot: null, CancellationToken.None);

        var conflict = job.Diffs.First(d => d.Status == DiffStatus.Conflict);
        var before = await connector.GetItemAsync(conflict.ItemId, "web");

        // leave the conflict Unresolved (defaults to skip)
        await engine.RunApplyAsync(job, connector, connector, null, CancellationToken.None);

        var after = await connector.GetItemAsync(conflict.ItemId, "web");
        Assert.Equal(before!.Revision, after!.Revision);
        Assert.True(job.Counters.Skipped > 0);
    }

    [Fact]
    public async Task Media_blob_is_transferred_on_apply()
    {
        var env = Env("engine-test-4");
        var connector = new SimulatedConnector(env);
        var engine = new SyncEngine(new NullPublisher());

        var job = NewJob(env.Id, "master", env.Id, "web");
        await engine.RunPreviewAsync(job, connector, connector, snapshot: null, CancellationToken.None);

        var mediaDiff = job.Diffs.First(d => d.IsMedia && d.Status != DiffStatus.Unchanged && d.Status != DiffStatus.Deleted);
        await engine.RunApplyAsync(job, connector, connector, null, CancellationToken.None);

        var sourceBlob = await connector.DownloadMediaAsync(mediaDiff.ItemId, "master");
        var targetBlob = await connector.DownloadMediaAsync(mediaDiff.ItemId, "web");
        Assert.NotNull(sourceBlob);
        Assert.Equal(sourceBlob, targetBlob);
    }
}
