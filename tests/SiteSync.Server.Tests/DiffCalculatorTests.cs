using SiteSync.Server.Domain;
using SiteSync.Server.Sync;
using Xunit;

namespace SiteSync.Server.Tests;

public class DiffCalculatorTests
{
    private static SitecoreItem Item(string id, string path, string revision = "rev-1",
        DateTime? updated = null, params (string Name, string Value)[] fields)
    {
        var item = new SitecoreItem
        {
            Id = id,
            Name = path[(path.LastIndexOf('/') + 1)..],
            Path = path,
            TemplateName = "Page",
            Revision = revision,
            Updated = updated ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        foreach (var (name, value) in fields) item.Fields[name] = value;
        return item;
    }

    private static Dictionary<string, SitecoreItem> Tree(params SitecoreItem[] items) =>
        items.ToDictionary(i => i.Id);

    [Fact]
    public void Item_only_on_source_is_New_with_its_fields_listed()
    {
        var source = Tree(Item("a", "/sitecore/content/Home", fields: ("Title", "Hello")));
        var target = Tree();

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        var diff = Assert.Single(diffs);
        Assert.Equal(DiffStatus.New, diff.Status);
        var field = Assert.Single(diff.FieldDiffs);
        Assert.Equal("Title", field.FieldName);
        Assert.Equal("Hello", field.SourceValue);
        Assert.Null(field.TargetValue);
    }

    [Fact]
    public void Item_only_on_target_is_Deleted()
    {
        var source = Tree();
        var target = Tree(Item("b", "/sitecore/content/Home/Old"));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        var diff = Assert.Single(diffs);
        Assert.Equal(DiffStatus.Deleted, diff.Status);
    }

    [Fact]
    public void Identical_items_are_Unchanged()
    {
        var source = Tree(Item("a", "/x", fields: ("Title", "Same")));
        var target = Tree(Item("a", "/x", fields: ("Title", "Same")));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        Assert.Equal(DiffStatus.Unchanged, Assert.Single(diffs).Status);
    }

    [Fact]
    public void Differing_field_marks_item_Modified_and_reports_both_values()
    {
        var source = Tree(Item("a", "/x", updated: new DateTime(2026, 2, 1), fields: ("Title", "New title")));
        var target = Tree(Item("a", "/x", updated: new DateTime(2026, 1, 1), fields: ("Title", "Old title")));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        var diff = Assert.Single(diffs);
        Assert.Equal(DiffStatus.Modified, diff.Status);
        var field = Assert.Single(diff.FieldDiffs);
        Assert.Equal("New title", field.SourceValue);
        Assert.Equal("Old title", field.TargetValue);
    }

    [Fact]
    public void System_fields_do_not_count_as_differences()
    {
        var source = Tree(Item("a", "/x", fields: new[] { ("__Revision", "111"), ("__Updated by", "admin"), ("Title", "Same") }));
        var target = Tree(Item("a", "/x", fields: new[] { ("__Revision", "222"), ("__Updated by", "editor"), ("Title", "Same") }));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        Assert.Equal(DiffStatus.Unchanged, Assert.Single(diffs).Status);
    }

    [Fact]
    public void Without_snapshot_target_newer_than_source_is_Conflict()
    {
        var source = Tree(Item("a", "/x", updated: new DateTime(2026, 1, 10), fields: ("Title", "From source")));
        var target = Tree(Item("a", "/x", updated: new DateTime(2026, 1, 20), fields: ("Title", "Edited on target")));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        Assert.Equal(DiffStatus.Conflict, Assert.Single(diffs).Status);
    }

    [Fact]
    public void Without_snapshot_source_newer_than_target_is_plain_Modified()
    {
        var source = Tree(Item("a", "/x", updated: new DateTime(2026, 1, 20), fields: ("Title", "From source")));
        var target = Tree(Item("a", "/x", updated: new DateTime(2026, 1, 10), fields: ("Title", "Stale")));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        Assert.Equal(DiffStatus.Modified, Assert.Single(diffs).Status);
    }

    [Fact]
    public void With_snapshot_both_sides_changed_is_Conflict()
    {
        var source = Tree(Item("a", "/x", revision: "src-2", fields: ("Title", "Source edit")));
        var target = Tree(Item("a", "/x", revision: "tgt-2", fields: ("Title", "Target edit")));
        var snapshot = new SyncSnapshot
        {
            Items = { ["a"] = new ItemRevisionPair { SourceRevision = "src-1", TargetRevision = "tgt-1" } }
        };

        var diffs = DiffCalculator.Compare(source, target, snapshot);

        Assert.Equal(DiffStatus.Conflict, Assert.Single(diffs).Status);
    }

    [Fact]
    public void With_snapshot_only_source_changed_is_Modified_even_if_target_updated_later()
    {
        // target revision matches the snapshot → target untouched since last sync,
        // so the newer target timestamp must NOT trigger the heuristic
        var source = Tree(Item("a", "/x", revision: "src-2", updated: new DateTime(2026, 1, 10), fields: ("Title", "Source edit")));
        var target = Tree(Item("a", "/x", revision: "tgt-1", updated: new DateTime(2026, 1, 20), fields: ("Title", "Old")));
        var snapshot = new SyncSnapshot
        {
            Items = { ["a"] = new ItemRevisionPair { SourceRevision = "src-1", TargetRevision = "tgt-1" } }
        };

        var diffs = DiffCalculator.Compare(source, target, snapshot);

        Assert.Equal(DiffStatus.Modified, Assert.Single(diffs).Status);
    }

    [Fact]
    public void Field_missing_on_target_is_reported_as_diff()
    {
        var source = Tree(Item("a", "/x", fields: new[] { ("Title", "T"), ("NewField", "added") }));
        var target = Tree(Item("a", "/x", fields: ("Title", "T")));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        var diff = Assert.Single(diffs);
        Assert.Equal(DiffStatus.Modified, diff.Status);
        var field = Assert.Single(diff.FieldDiffs);
        Assert.Equal("NewField", field.FieldName);
        Assert.Null(field.TargetValue);
    }

    [Fact]
    public void Changes_sort_before_unchanged_and_by_path()
    {
        var source = Tree(
            Item("a", "/sitecore/content/A", fields: ("Title", "same")),
            Item("b", "/sitecore/content/B", fields: ("Title", "x")),
            Item("c", "/sitecore/content/C", fields: ("Title", "y")));
        var target = Tree(
            Item("a", "/sitecore/content/A", fields: ("Title", "same")),
            Item("b", "/sitecore/content/B", fields: ("Title", "x-old")),
            Item("c", "/sitecore/content/C", fields: ("Title", "y-old")));

        var diffs = DiffCalculator.Compare(source, target, snapshot: null);

        Assert.Equal(new[] { "/sitecore/content/B", "/sitecore/content/C", "/sitecore/content/A" },
            diffs.Select(d => d.Path).ToArray());
    }
}

public class SyncScopeTests
{
    [Fact]
    public void Roots_include_only_selected_scopes()
    {
        var scope = new SyncScope { Content = true, RootPath = "/sitecore/content/Home", Media = true, Templates = false, Layout = false };
        var roots = SyncEngine.RootsFor(scope);
        Assert.Equal(new[] { "/sitecore/content/Home", "/sitecore/media library" }, roots);
    }

    [Fact]
    public void Nested_root_is_removed_when_parent_root_selected()
    {
        var scope = new SyncScope { Content = true, RootPath = "/sitecore/templates/Project", Templates = true };
        var roots = SyncEngine.RootsFor(scope);
        Assert.Equal(new[] { "/sitecore/templates" }, roots);
    }
}
