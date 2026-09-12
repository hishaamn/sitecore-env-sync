using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SiteSync.Server.Domain;

namespace SiteSync.Server.Connectors;

/// <summary>
/// In-memory Sitecore stand-in for demos and tests. Each (environment, database)
/// pair gets its own seeded tree with deliberate differences (new, modified,
/// deleted and conflicting items) so a sync between any two pairs is meaningful.
/// Mutations are kept for the lifetime of the process, so after an apply a
/// re-preview shows the trees converging — same as against real Sitecore.
/// </summary>
public class SimulatedConnector : ISitecoreConnector
{
    private static readonly ConcurrentDictionary<string, SimTree> Trees = new();
    private readonly SitecoreEnvironment _env;
    // Small artificial latency so progress reporting resembles a real remote sync.
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(8);

    public SimulatedConnector(SitecoreEnvironment env) => _env = env;

    private SimTree Tree(string database) =>
        Trees.GetOrAdd($"{_env.Name}::{database}", _ => SimTreeSeeder.Seed(_env.Name, database));

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        await Task.Delay(200, ct);
        var counts = string.Join(", ", _env.Databases.Select(db => $"{db}: {Tree(db).Items.Count} items"));
        return new ConnectionTestResult(true, $"Simulated environment '{_env.Name}' ready ({counts}).");
    }

    public async Task<SitecoreItem?> GetItemAsync(string idOrPath, string database, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);
        var tree = Tree(database);
        if (idOrPath.StartsWith('/'))
            return tree.Items.Values.FirstOrDefault(i =>
                string.Equals(i.Path, idOrPath, StringComparison.OrdinalIgnoreCase))?.Clone();
        return tree.Items.TryGetValue(ItemServiceConnector.NormalizeGuid(idOrPath), out var item) ? item.Clone() : null;
    }

    public async Task<IReadOnlyList<SitecoreItem>> GetChildrenAsync(string itemId, string database, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);
        var id = ItemServiceConnector.NormalizeGuid(itemId);
        return Tree(database).Items.Values
            .Where(i => i.ParentId == id)
            .OrderBy(i => i.Name)
            .Select(i => i.Clone())
            .ToList();
    }

    public async Task CreateItemAsync(string parentId, string itemId, string name, string templateId,
        Dictionary<string, string> fields, string database, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);
        var tree = Tree(database);
        var pid = ItemServiceConnector.NormalizeGuid(parentId);
        if (!tree.Items.TryGetValue(pid, out var parent))
            throw new SitecoreConnectorException($"Simulated: parent {parentId} not found in {database}.");
        var id = ItemServiceConnector.NormalizeGuid(itemId);
        var item = new SitecoreItem
        {
            Id = id,
            Name = name,
            Path = $"{parent.Path}/{name}",
            ParentId = pid,
            TemplateId = ItemServiceConnector.NormalizeGuid(templateId),
            TemplateName = fields.TryGetValue("__TemplateName", out var tn) ? tn : "Item",
            Revision = Guid.NewGuid().ToString("D"),
            Updated = DateTime.UtcNow,
            Fields = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)
        };
        item.IsMedia = item.Path.StartsWith("/sitecore/media library", StringComparison.OrdinalIgnoreCase)
                       && item.Fields.ContainsKey("Extension");
        parent.HasChildren = true;
        tree.Items[id] = item;
    }

    public async Task UpdateItemAsync(string itemId, Dictionary<string, string> fields, string database, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);
        var tree = Tree(database);
        var id = ItemServiceConnector.NormalizeGuid(itemId);
        if (!tree.Items.TryGetValue(id, out var item))
            throw new SitecoreConnectorException($"Simulated: item {itemId} not found in {database}.");
        foreach (var f in fields) item.Fields[f.Key] = f.Value;
        item.Revision = Guid.NewGuid().ToString("D");
        item.Updated = DateTime.UtcNow;
    }

    public async Task DeleteItemAsync(string itemId, string database, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);
        var tree = Tree(database);
        var id = ItemServiceConnector.NormalizeGuid(itemId);
        if (!tree.Items.TryGetValue(id, out var item)) return;
        // remove the whole subtree, like Sitecore does
        var toRemove = tree.Items.Values
            .Where(i => i.Path.StartsWith(item.Path + "/", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Id)
            .Append(id)
            .ToList();
        foreach (var rid in toRemove) tree.Items.TryRemove(rid, out _);
    }

    public async Task<byte[]?> DownloadMediaAsync(string itemId, string database, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);
        var id = ItemServiceConnector.NormalizeGuid(itemId);
        return Tree(database).Blobs.TryGetValue(id, out var blob) ? blob : null;
    }

    public async Task UploadMediaAsync(string itemId, byte[] blob, string fileName, string database, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct);
        Tree(database).Blobs[ItemServiceConnector.NormalizeGuid(itemId)] = blob;
    }
}

public class SimTree
{
    public ConcurrentDictionary<string, SitecoreItem> Items { get; } = new();
    public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();
}

public static class SimTreeSeeder
{
    private static readonly DateTime BaseDate = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Builds a tree for one (environment, database) pair. The 'master' variant is the
    /// richest/newest; 'web' lags behind (missing items, stale values, one orphan and one
    /// item edited later than master so the conflict heuristic fires). Environment name
    /// shifts a few values so master↔master across environments also has differences.
    /// </summary>
    public static SimTree Seed(string envName, string database)
    {
        var tree = new SimTree();
        bool rich = string.Equals(database, "master", StringComparison.OrdinalIgnoreCase);
        int envSeed = StableHash(envName);
        var rnd = new Random(envSeed ^ StableHash(database));

        SitecoreItem Add(string path, string templateName, Dictionary<string, string>? fields = null,
            DateTime? updated = null, bool media = false)
        {
            var id = DeterministicGuid(path);
            var parentPath = path[..path.LastIndexOf('/')];
            var item = new SitecoreItem
            {
                Id = id,
                Name = path[(path.LastIndexOf('/') + 1)..],
                Path = path,
                ParentId = parentPath.Length == 0 ? "" : DeterministicGuid(parentPath),
                TemplateId = DeterministicGuid("template:" + templateName),
                TemplateName = templateName,
                Revision = DeterministicGuid(path + "|rev0"),
                Updated = updated ?? BaseDate,
                IsMedia = media,
                Fields = new Dictionary<string, string>(fields ?? new(), StringComparer.OrdinalIgnoreCase)
            };
            tree.Items[id] = item;
            if (item.ParentId != "" && tree.Items.TryGetValue(item.ParentId, out var p)) p.HasChildren = true;
            return item;
        }

        void Touch(SitecoreItem item, string revSalt, DateTime when)
        {
            item.Revision = DeterministicGuid(item.Path + "|" + revSalt);
            item.Updated = when;
        }

        // ---- skeleton -------------------------------------------------------
        Add("/sitecore", "Root");
        Add("/sitecore/content", "Main section");
        Add("/sitecore/media library", "Main section");
        Add("/sitecore/templates", "Main section");
        Add("/sitecore/layout", "Main section");

        // ---- templates ------------------------------------------------------
        Add("/sitecore/templates/Project", "Template Folder");
        foreach (var t in new[] { "Page", "Article", "Product", "Settings" })
        {
            Add($"/sitecore/templates/Project/{t}", "Template",
                new() { ["__Base template"] = "Standard Template" });
        }
        if (rich) // a template added recently on master only
            Add("/sitecore/templates/Project/Campaign", "Template",
                new() { ["__Base template"] = "Standard Template" }, BaseDate.AddDays(40));

        // ---- layout ---------------------------------------------------------
        Add("/sitecore/layout/Renderings", "Node");
        Add("/sitecore/layout/Renderings/Project", "Rendering Folder");
        foreach (var r in new[] { "Header", "Footer", "Hero", "ProductCard", "ArticleList" })
        {
            var f = new Dictionary<string, string> { ["Path"] = $"/Views/Project/{r}.cshtml" };
            var rendering = Add($"/sitecore/layout/Renderings/Project/{r}", "View rendering", f);
            if (rich && r == "Hero")
            {
                rendering.Fields["Parameters"] = "variant=full-bleed";
                Touch(rendering, "rev-hero-2", BaseDate.AddDays(35));
            }
        }

        // ---- content --------------------------------------------------------
        Add("/sitecore/content/Home", "Page", new()
        {
            ["Title"] = rich ? $"Welcome to {envName}" : "Welcome",
            ["Body"] = "<p>Homepage body copy.</p>"
        }, rich ? BaseDate.AddDays(30) : BaseDate);

        Add("/sitecore/content/Home/About", "Page", new()
        {
            ["Title"] = "About us",
            ["Body"] = "<p>Company history and mission.</p>"
        });

        // Contact: edited on BOTH sides after the base date → conflict candidate.
        Add("/sitecore/content/Home/Contact", "Page", new()
        {
            ["Title"] = "Contact",
            ["Email"] = rich ? "hello@brand.com" : "support@brand.com"
        }, rich ? BaseDate.AddDays(20) : BaseDate.AddDays(28));
        if (!rich) Touch(tree.Items[DeterministicGuid("/sitecore/content/Home/Contact")], "rev-web-edit", BaseDate.AddDays(28));

        // Products
        Add("/sitecore/content/Home/Products", "Page", new() { ["Title"] = "Products" });
        int productCount = rich ? 24 : 20; // 4 new products on master
        for (int i = 1; i <= productCount; i++)
        {
            var fields = new Dictionary<string, string>
            {
                ["Title"] = $"Product {i:00}",
                ["Price"] = (49 + i * 10 + (rich && i % 5 == 0 ? 5 : 0)).ToString(), // price bumps on master
                ["Sku"] = $"SKU-{envSeed % 97:00}-{i:000}"
            };
            var updated = rich && i % 5 == 0 ? BaseDate.AddDays(25) : BaseDate;
            var p = Add($"/sitecore/content/Home/Products/Product-{i:00}", "Product", fields, updated);
            if (rich && i % 5 == 0) Touch(p, "rev-price-2", updated);
        }

        // News
        Add("/sitecore/content/Home/News", "Page", new() { ["Title"] = "News" });
        int articleCount = rich ? 30 : 24; // 6 unsynced articles on master
        for (int i = 1; i <= articleCount; i++)
        {
            Add($"/sitecore/content/Home/News/Article-{i:000}", "Article", new()
            {
                ["Title"] = $"Article {i:000}",
                ["Body"] = $"<p>Body of article {i}. {LoremFor(rnd)}</p>",
                ["Date"] = BaseDate.AddDays(-i).ToString("yyyyMMddTHHmmssZ")
            }, BaseDate.AddDays(rich && i > 24 ? 32 : 0));
        }

        // An item that only exists on the lagging side → shows up as orphan/Deleted.
        if (!rich)
            Add("/sitecore/content/Home/Legacy-Promo", "Page", new()
            {
                ["Title"] = "Legacy promotion (removed on master)"
            });

        // ---- media ----------------------------------------------------------
        Add("/sitecore/media library/Images", "Media folder");
        int mediaCount = rich ? 14 : 11;
        for (int i = 1; i <= mediaCount; i++)
        {
            var path = $"/sitecore/media library/Images/banner-{i:00}";
            Add(path, "Jpeg", new()
            {
                ["Extension"] = "jpg",
                ["Mime Type"] = "image/jpeg",
                ["Alt"] = $"Banner {i:00}" + (rich && i == 3 ? " (updated crop)" : ""),
                ["Size"] = (10_000 + i * 137).ToString()
            }, rich && i == 3 ? BaseDate.AddDays(22) : BaseDate, media: true);
            // deterministic fake blob, slightly different content where Alt differs
            var blobSeed = path + (rich && i == 3 ? "v2" : "v1");
            tree.Blobs[DeterministicGuid(path)] = SHA256.HashData(Encoding.UTF8.GetBytes(blobSeed))
                .Concat(new byte[256]).ToArray();
            if (rich && i == 3) Touch(tree.Items[DeterministicGuid(path)], "rev-crop-2", BaseDate.AddDays(22));
        }

        return tree;
    }

    private static string LoremFor(Random rnd)
    {
        string[] words = { "sitecore", "content", "delivers", "value", "across", "channels", "with", "structured", "data" };
        return string.Join(' ', Enumerable.Range(0, 8).Select(_ => words[rnd.Next(words.Length)]));
    }

    /// <summary>Same path → same GUID, so item IDs line up across simulated environments.</summary>
    public static string DeterministicGuid(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
        return new Guid(hash).ToString("D");
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }
}
