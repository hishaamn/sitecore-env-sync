using Microsoft.AspNetCore.SignalR;
using SiteSync.Server.Domain;
using SiteSync.Server.Sync;

namespace SiteSync.Server.Hubs;

/// <summary>Clients subscribe and receive "progress" (job summary) and "log" (job log line) events.</summary>
public class SyncHub : Hub
{
}

public class SignalRProgressPublisher : IProgressPublisher
{
    private readonly IHubContext<SyncHub> _hub;

    public SignalRProgressPublisher(IHubContext<SyncHub> hub) => _hub = hub;

    public Task PublishProgressAsync(SyncJobSummary summary) =>
        _hub.Clients.All.SendAsync("progress", summary);

    public Task PublishLogAsync(string jobId, LogEntry entry) =>
        _hub.Clients.All.SendAsync("log", new { jobId, entry });
}
