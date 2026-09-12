using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SiteSync.Server.Domain;

namespace SiteSync.Server.Connectors;

/// <summary>
/// Connector for self-hosted Sitecore XM/XP using the Sitecore Services Client
/// ItemService REST API (/sitecore/api/ssc). Authenticates with the standard
/// cookie-based login endpoint and keeps the session for subsequent calls.
/// </summary>
public class ItemServiceConnector : ISitecoreConnector
{
    private readonly SitecoreEnvironment _env;
    private readonly HttpClient _http;
    private bool _authenticated;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ItemServiceConnector(SitecoreEnvironment env, IHttpClientFactory httpFactory)
    {
        _env = env;
        _http = httpFactory.CreateClient($"sitecore-{env.Id}");
        _http.BaseAddress = new Uri(env.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(100);
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_authenticated) return;
        await _authLock.WaitAsync(ct);
        try
        {
            if (_authenticated) return;
            var payload = JsonSerializer.Serialize(new
            {
                domain = _env.Domain,
                username = _env.Username,
                password = _env.Password
            });
            var resp = await _http.PostAsync("sitecore/api/ssc/auth/login",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
                throw new SitecoreConnectorException(
                    $"Login to {_env.Name} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            _authenticated = true;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            _authenticated = false;
            await EnsureAuthenticatedAsync(ct);
            var root = await GetItemAsync("/sitecore", _env.Databases.FirstOrDefault() ?? "master", ct);
            return root != null
                ? new ConnectionTestResult(true, $"Connected to {_env.BaseUrl} as {_env.Domain}\\{_env.Username}")
                : new ConnectionTestResult(false, "Authenticated, but could not read /sitecore root item.");
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task<SitecoreItem?> GetItemAsync(string idOrPath, string database, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        string url = idOrPath.StartsWith('/')
            ? $"sitecore/api/ssc/item/?path={Uri.EscapeDataString(idOrPath)}&database={database}&includeStandardTemplateFields=true&fields="
            : $"sitecore/api/ssc/item/{NormalizeGuid(idOrPath)}?database={database}&includeStandardTemplateFields=true&fields=";

        var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailed(resp, $"get item {idOrPath}");
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        using var doc = JsonDocument.Parse(json);
        return ParseItem(doc.RootElement);
    }

    public async Task<IReadOnlyList<SitecoreItem>> GetChildrenAsync(string itemId, string database, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var url = $"sitecore/api/ssc/item/{NormalizeGuid(itemId)}/children?database={database}&includeStandardTemplateFields=true";
        var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<SitecoreItem>();
        await ThrowIfFailed(resp, $"get children of {itemId}");
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<SitecoreItem>();
        return doc.RootElement.EnumerateArray().Select(ParseItem).ToList();
    }

    public async Task CreateItemAsync(string parentId, string itemId, string name, string templateId,
        Dictionary<string, string> fields, string database, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var body = new Dictionary<string, object>
        {
            ["ItemName"] = name,
            ["TemplateID"] = NormalizeGuid(templateId),
            // ItemService honors an explicit ID so item GUIDs stay aligned across environments.
            ["ID"] = NormalizeGuid(itemId)
        };
        foreach (var f in fields) body[f.Key] = f.Value;

        var url = $"sitecore/api/ssc/item/{NormalizeGuid(parentId)}?database={database}";
        var resp = await _http.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
        await ThrowIfFailed(resp, $"create item '{name}' under {parentId}");
    }

    public async Task UpdateItemAsync(string itemId, Dictionary<string, string> fields, string database, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var url = $"sitecore/api/ssc/item/{NormalizeGuid(itemId)}?database={database}";
        var resp = await _http.PatchAsync(url,
            new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json"), ct);
        await ThrowIfFailed(resp, $"update item {itemId}");
    }

    public async Task DeleteItemAsync(string itemId, string database, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var url = $"sitecore/api/ssc/item/{NormalizeGuid(itemId)}?database={database}";
        var resp = await _http.DeleteAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // already gone
        await ThrowIfFailed(resp, $"delete item {itemId}");
    }

    public async Task<byte[]?> DownloadMediaAsync(string itemId, string database, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        // The media handler serves blobs by item ID; sc_database selects the database.
        var guid = NormalizeGuid(itemId).Replace("-", "");
        var url = $"~/media/{guid}.ashx?sc_database={database}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length > 0 ? bytes : null;
    }

    public async Task UploadMediaAsync(string itemId, byte[] blob, string fileName, string database, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        // Sitecore Media API: attach/replace the blob on an existing media item.
        var url = $"sitecore/api/ssc/item/{NormalizeGuid(itemId)}/media?database={database}";
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(blob);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        var resp = await _http.PutAsync(url, content, ct);
        await ThrowIfFailed(resp, $"upload media blob for {itemId}");
    }

    private static SitecoreItem ParseItem(JsonElement el)
    {
        var item = new SitecoreItem();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
            switch (prop.Name)
            {
                case "ItemID": item.Id = NormalizeGuid(value); break;
                case "ItemName": item.Name = value; break;
                case "ItemPath": item.Path = value; break;
                case "ParentID": item.ParentId = NormalizeGuid(value); break;
                case "TemplateID": item.TemplateId = NormalizeGuid(value); break;
                case "TemplateName": item.TemplateName = value; break;
                case "HasChildren": item.HasChildren = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                case "ItemLanguage" or "ItemVersion" or "DisplayName" or "ItemUrl" or "CloneSource" or "Category": break;
                case "__Revision": item.Revision = value; fields[prop.Name] = value; break;
                case "__Updated":
                    fields[prop.Name] = value;
                    if (DateTime.TryParse(value, out var dt)) item.Updated = dt;
                    else item.Updated = ParseSitecoreDate(value);
                    break;
                default:
                    fields[prop.Name] = value;
                    break;
            }
        }
        item.Fields = fields;
        item.IsMedia = item.Path.StartsWith("/sitecore/media library", StringComparison.OrdinalIgnoreCase)
                       && (fields.ContainsKey("Blob") || fields.ContainsKey("Extension") || fields.ContainsKey("Mime Type"));
        return item;
    }

    /// <summary>Sitecore ISO dates look like 20240131T120000Z.</summary>
    internal static DateTime? ParseSitecoreDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value, new[] { "yyyyMMddTHHmmssZ", "yyyyMMddTHHmmss" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return null;
    }

    internal static string NormalizeGuid(string id)
    {
        var trimmed = id.Trim('{', '}', ' ');
        return Guid.TryParse(trimmed, out var g) ? g.ToString("D") : id;
    }

    private static async Task ThrowIfFailed(HttpResponseMessage resp, string action)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        if (body.Length > 300) body = body[..300];
        throw new SitecoreConnectorException($"Failed to {action}: {(int)resp.StatusCode} {resp.ReasonPhrase} {body}");
    }
}

public class SitecoreConnectorException : Exception
{
    public SitecoreConnectorException(string message) : base(message) { }
}
