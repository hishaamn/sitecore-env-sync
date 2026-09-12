using SiteSync.Server.Domain;

namespace SiteSync.Server.Connectors;

/// <summary>
/// Transport-agnostic access to one Sitecore environment. All operations target a
/// specific database (master, web, ...) so the same environment can act as source
/// or target on any of its databases.
/// </summary>
public interface ISitecoreConnector
{
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Resolve an item by GUID or by full Sitecore path. Returns null when not found.</summary>
    Task<SitecoreItem?> GetItemAsync(string idOrPath, string database, CancellationToken ct = default);

    Task<IReadOnlyList<SitecoreItem>> GetChildrenAsync(string itemId, string database, CancellationToken ct = default);

    /// <summary>Create an item under the given parent with an explicit ID so GUIDs stay stable across environments.</summary>
    Task CreateItemAsync(string parentId, string itemId, string name, string templateId,
        Dictionary<string, string> fields, string database, CancellationToken ct = default);

    Task UpdateItemAsync(string itemId, Dictionary<string, string> fields, string database, CancellationToken ct = default);

    Task DeleteItemAsync(string itemId, string database, CancellationToken ct = default);

    /// <summary>Download the binary blob of a media item. Null when the item has no blob.</summary>
    Task<byte[]?> DownloadMediaAsync(string itemId, string database, CancellationToken ct = default);

    /// <summary>Attach a binary blob to an existing media item.</summary>
    Task UploadMediaAsync(string itemId, byte[] blob, string fileName, string database, CancellationToken ct = default);
}

public record ConnectionTestResult(bool Success, string Message);

public interface IConnectorFactory
{
    ISitecoreConnector Create(SitecoreEnvironment environment);
}
