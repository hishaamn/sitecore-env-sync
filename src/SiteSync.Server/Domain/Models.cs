using System.Text.Json.Serialization;

namespace SiteSync.Server.Domain;

/// <summary>A configured Sitecore environment (one server, possibly several databases).</summary>
public class SitecoreEnvironment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Domain { get; set; } = "sitecore";
    public List<string> Databases { get; set; } = new() { "master", "web" };
    public ConnectorType ConnectorType { get; set; } = ConnectorType.ItemService;
    public string Color { get; set; } = "#6c5ce7";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorType
{
    ItemService,
    Simulated
}

/// <summary>A Sitecore item as exposed by a connector, normalized across connector types.</summary>
public class SitecoreItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTime? Updated { get; set; }
    public bool HasChildren { get; set; }
    public bool IsMedia { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SitecoreItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Path = Path,
        ParentId = ParentId,
        TemplateId = TemplateId,
        TemplateName = TemplateName,
        Revision = Revision,
        Updated = Updated,
        HasChildren = HasChildren,
        IsMedia = IsMedia,
        Fields = new Dictionary<string, string>(Fields, StringComparer.OrdinalIgnoreCase)
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiffStatus
{
    New,        // exists on source, not on target
    Modified,   // exists on both, fields differ
    Deleted,    // exists on target under root, not on source
    Conflict,   // both sides changed since the last recorded sync
    Unchanged
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConflictResolution
{
    Unresolved,
    UseSource,
    Skip
}

public class FieldDiff
{
    public string FieldName { get; set; } = "";
    public string? SourceValue { get; set; }
    public string? TargetValue { get; set; }
}

public class ItemDiff
{
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public bool IsMedia { get; set; }
    public DiffStatus Status { get; set; }
    public ConflictResolution Resolution { get; set; } = ConflictResolution.Unresolved;
    public List<FieldDiff> FieldDiffs { get; set; } = new();
    /// <summary>Set for Conflict items: when each side last changed.</summary>
    public DateTime? SourceUpdated { get; set; }
    public DateTime? TargetUpdated { get; set; }
    /// <summary>Revisions at preview time; used to baseline the post-apply snapshot.</summary>
    public string SourceRevision { get; set; } = "";
    public string TargetRevision { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Queued,
    Running,
    AwaitingReview, // preview finished, waiting for user to apply
    Applying,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncPhase
{
    Connecting,
    DiscoveringSource,
    DiscoveringTarget,
    Comparing,
    Applying,
    Snapshotting,
    Done
}

public class SyncScope
{
    public bool Content { get; set; } = true;
    public bool Media { get; set; }
    public bool Templates { get; set; }
    public bool Layout { get; set; }
    /// <summary>Root content path, e.g. /sitecore/content/Home. Used when Content=true.</summary>
    public string RootPath { get; set; } = "/sitecore/content";
    /// <summary>When true, items present on target but missing on source are deleted on apply.</summary>
    public bool DeleteOrphans { get; set; }
}

public class SyncJobRequest
{
    public string SourceEnvironmentId { get; set; } = "";
    public string SourceDatabase { get; set; } = "master";
    public string TargetEnvironmentId { get; set; } = "";
    public string TargetDatabase { get; set; } = "web";
    public SyncScope Scope { get; set; } = new();
}

public class SyncCounters
{
    public int SourceItems { get; set; }
    public int TargetItems { get; set; }
    public int New { get; set; }
    public int Modified { get; set; }
    public int Deleted { get; set; }
    public int Conflicts { get; set; }
    public int Unchanged { get; set; }
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
}

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "info"; // info | warn | error | success
    public string Message { get; set; } = "";
}

public class SyncJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public SyncJobRequest Request { get; set; } = new();
    public string SourceEnvironmentName { get; set; } = "";
    public string TargetEnvironmentName { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public SyncPhase Phase { get; set; } = SyncPhase.Connecting;
    public int Processed { get; set; }
    public int Total { get; set; }
    public string? CurrentItem { get; set; }
    public SyncCounters Counters { get; set; } = new();
    public List<ItemDiff> Diffs { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
    public string? Error { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
}

/// <summary>Lightweight job view for lists and progress events (no diffs/log payload).</summary>
public class SyncJobSummary
{
    public string Id { get; set; } = "";
    public string SourceEnvironmentName { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public string TargetEnvironmentName { get; set; } = "";
    public string TargetDatabase { get; set; } = "";
    public string RootPath { get; set; } = "";
    public JobStatus Status { get; set; }
    public SyncPhase Phase { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
    public string? CurrentItem { get; set; }
    public SyncCounters Counters { get; set; } = new();
    public string? Error { get; set; }
    public DateTime Created { get; set; }
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }

    public static SyncJobSummary From(SyncJob job) => new()
    {
        Id = job.Id,
        SourceEnvironmentName = job.SourceEnvironmentName,
        SourceDatabase = job.Request.SourceDatabase,
        TargetEnvironmentName = job.TargetEnvironmentName,
        TargetDatabase = job.Request.TargetDatabase,
        RootPath = job.Request.Scope.RootPath,
        Status = job.Status,
        Phase = job.Phase,
        Processed = job.Processed,
        Total = job.Total,
        CurrentItem = job.CurrentItem,
        Counters = job.Counters,
        Error = job.Error,
        Created = job.Created,
        Started = job.Started,
        Finished = job.Finished
    };
}

/// <summary>Per item-pair revision snapshot taken after a successful apply; powers conflict detection.</summary>
public class SyncSnapshot
{
    public string PairKey { get; set; } = "";
    public DateTime Taken { get; set; }
    /// <summary>ItemId → revisions on each side at the moment the item was last synced.</summary>
    public Dictionary<string, ItemRevisionPair> Items { get; set; } = new();
}

public class ItemRevisionPair
{
    public string SourceRevision { get; set; } = "";
    public string TargetRevision { get; set; } = "";
}
