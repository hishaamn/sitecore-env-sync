using System.Text.Json;
using System.Text.Json.Serialization;
using SiteSync.Server.Domain;

namespace SiteSync.Server.Persistence;

/// <summary>
/// Simple JSON-file persistence under ./data: environments, finished jobs and
/// per-pair revision snapshots. Deliberately dependency-free for v1; swap for a
/// database if job volume grows.
/// </summary>
public class FileStore
{
    private readonly string _root;
    private readonly object _envLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "jobs"));
        Directory.CreateDirectory(Path.Combine(_root, "snapshots"));
    }

    // ---- environments -------------------------------------------------------

    private string EnvFile => Path.Combine(_root, "environments.json");

    public List<SitecoreEnvironment> GetEnvironments()
    {
        lock (_envLock)
        {
            if (!File.Exists(EnvFile)) return new();
            return JsonSerializer.Deserialize<List<SitecoreEnvironment>>(File.ReadAllText(EnvFile), JsonOpts) ?? new();
        }
    }

    public void SaveEnvironments(List<SitecoreEnvironment> environments)
    {
        lock (_envLock)
        {
            File.WriteAllText(EnvFile, JsonSerializer.Serialize(environments, JsonOpts));
        }
    }

    // ---- jobs ----------------------------------------------------------------

    public void SaveJob(SyncJob job)
    {
        var file = Path.Combine(_root, "jobs", job.Id + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(job, JsonOpts));
    }

    public SyncJob? LoadJob(string id)
    {
        var file = Path.Combine(_root, "jobs", Path.GetFileName(id) + ".json");
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<SyncJob>(File.ReadAllText(file), JsonOpts);
    }

    public List<SyncJob> LoadAllJobs() =>
        Directory.EnumerateFiles(Path.Combine(_root, "jobs"), "*.json")
            .Select(f => JsonSerializer.Deserialize<SyncJob>(File.ReadAllText(f), JsonOpts))
            .Where(j => j != null)
            .Select(j => j!)
            .OrderByDescending(j => j.Created)
            .ToList();

    // ---- snapshots -------------------------------------------------------------

    private static string SafeKey(string pairKey) =>
        string.Concat(pairKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    public SyncSnapshot? LoadSnapshot(string pairKey)
    {
        var file = Path.Combine(_root, "snapshots", SafeKey(pairKey) + ".json");
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<SyncSnapshot>(File.ReadAllText(file), JsonOpts);
    }

    public void SaveSnapshot(SyncSnapshot snapshot)
    {
        var file = Path.Combine(_root, "snapshots", SafeKey(snapshot.PairKey) + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, JsonOpts));
    }
}
