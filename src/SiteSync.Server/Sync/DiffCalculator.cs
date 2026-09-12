using SiteSync.Server.Domain;

namespace SiteSync.Server.Sync;

/// <summary>
/// Pure comparison logic: given the discovered source and target trees plus the
/// last sync snapshot, classify every item as New / Modified / Deleted /
/// Conflict / Unchanged with field-level diffs. No I/O — unit-testable.
/// </summary>
public static class DiffCalculator
{
    /// <summary>System fields that change on every save and must not count as content differences.</summary>
    public static readonly HashSet<string> ExcludedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "__Revision", "__Updated", "__Updated by", "__Created", "__Created by",
        "__Owner", "__Lock", "__Workflow state", "__Archive date", "__Archive version date",
        "__Reminder date", "__Reminder recipients", "__Reminder text", "ItemMedialUrl", "ItemMediaUrl"
    };

    public static List<ItemDiff> Compare(
        IReadOnlyDictionary<string, SitecoreItem> source,
        IReadOnlyDictionary<string, SitecoreItem> target,
        SyncSnapshot? snapshot,
        bool detectConflicts = true)
    {
        var diffs = new List<ItemDiff>();

        foreach (var (id, src) in source)
        {
            if (!target.TryGetValue(id, out var tgt))
            {
                diffs.Add(new ItemDiff
                {
                    ItemId = id,
                    Name = src.Name,
                    Path = src.Path,
                    TemplateName = src.TemplateName,
                    IsMedia = src.IsMedia,
                    Status = DiffStatus.New,
                    SourceUpdated = src.Updated,
                    SourceRevision = src.Revision,
                    FieldDiffs = src.Fields
                        .Where(f => !ExcludedFields.Contains(f.Key) && !string.IsNullOrEmpty(f.Value))
                        .Select(f => new FieldDiff { FieldName = f.Key, SourceValue = f.Value, TargetValue = null })
                        .OrderBy(f => f.FieldName)
                        .ToList()
                });
                continue;
            }

            var fieldDiffs = CompareFields(src, tgt);
            if (fieldDiffs.Count == 0)
            {
                diffs.Add(Make(src, tgt, DiffStatus.Unchanged));
                continue;
            }

            var status = DiffStatus.Modified;
            if (detectConflicts && IsConflict(id, src, tgt, snapshot))
                status = DiffStatus.Conflict;

            var d = Make(src, tgt, status);
            d.FieldDiffs = fieldDiffs;
            diffs.Add(d);
        }

        // Items under the synced root that exist only on the target.
        foreach (var (id, tgt) in target)
        {
            if (source.ContainsKey(id)) continue;
            diffs.Add(new ItemDiff
            {
                ItemId = id,
                Name = tgt.Name,
                Path = tgt.Path,
                TemplateName = tgt.TemplateName,
                IsMedia = tgt.IsMedia,
                Status = DiffStatus.Deleted,
                TargetUpdated = tgt.Updated
            });
        }

        return diffs
            .OrderBy(d => d.Status == DiffStatus.Unchanged ? 1 : 0)
            .ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ItemDiff Make(SitecoreItem src, SitecoreItem tgt, DiffStatus status) => new()
    {
        ItemId = src.Id,
        Name = src.Name,
        Path = src.Path,
        TemplateName = src.TemplateName,
        IsMedia = src.IsMedia || tgt.IsMedia,
        Status = status,
        SourceUpdated = src.Updated,
        TargetUpdated = tgt.Updated,
        SourceRevision = src.Revision,
        TargetRevision = tgt.Revision
    };

    public static List<FieldDiff> CompareFields(SitecoreItem src, SitecoreItem tgt)
    {
        var result = new List<FieldDiff>();
        var names = src.Fields.Keys.Union(tgt.Fields.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(n => !ExcludedFields.Contains(n));

        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            src.Fields.TryGetValue(name, out var sv);
            tgt.Fields.TryGetValue(name, out var tv);
            if (string.Equals(sv ?? "", tv ?? "", StringComparison.Ordinal)) continue;
            result.Add(new FieldDiff { FieldName = name, SourceValue = sv, TargetValue = tv });
        }
        return result;
    }

    /// <summary>
    /// Conflict = both sides changed since we last synced this item. With a snapshot we
    /// compare stored revisions; without one (first sync) we fall back to a heuristic:
    /// the target was edited more recently than the source, so blindly pushing the
    /// source would overwrite newer work.
    /// </summary>
    private static bool IsConflict(string id, SitecoreItem src, SitecoreItem tgt, SyncSnapshot? snapshot)
    {
        if (snapshot != null && snapshot.Items.TryGetValue(id, out var pair))
        {
            bool sourceChanged = !string.Equals(pair.SourceRevision, src.Revision, StringComparison.OrdinalIgnoreCase);
            bool targetChanged = !string.Equals(pair.TargetRevision, tgt.Revision, StringComparison.OrdinalIgnoreCase);
            return sourceChanged && targetChanged;
        }

        return src.Updated.HasValue && tgt.Updated.HasValue && tgt.Updated > src.Updated;
    }
}
