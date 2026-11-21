using System;
using System.Collections.Generic;
using System.Linq;

namespace TldCoop;

internal static class FurnitureReplicationPolicy
{
    internal static string Stamp(IEnumerable<KeyValuePair<string, long>> versions)
    {
        ulong hash = 14695981039346656037UL;
        int count = 0;
        foreach (var entry in versions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            count++;
            foreach (char value in entry.Key) hash = unchecked((hash ^ value) * 1099511628211UL);
            hash = unchecked((hash ^ (ulong)entry.Value) * 1099511628211UL);
        }
        return FormattableString.Invariant($"{count}:{hash:X16}");
    }

    internal static bool CanCommit(long expectedRevision, long currentRevision) => expectedRevision == currentRevision;

    internal static bool CanCreate(int matchingCandidates, bool hasHistory, int unboundCandidates) =>
        matchingCandidates == 0 && (!hasHistory || unboundCandidates == 0);
}

internal sealed class FurnitureSnapshot<T>
{
    private readonly Dictionary<string, (long Revision, T Item)> entries = new(StringComparer.Ordinal);
    private string expected;
    private int expectedCount;

    internal void Reset()
    {
        entries.Clear();
        expected = null;
        expectedCount = -1;
    }

    internal void Add(string id, long revision, T item)
    {
        if (entries.Count < 2048) entries.TryAdd(id, (revision, item));
    }

    internal void Complete(string stamp)
    {
        int separator = stamp.IndexOf(':');
        if (separator <= 0 || !int.TryParse(stamp.AsSpan(0, separator), out int count) || count is < 0 or > 2048)
            return;
        expected = stamp;
        expectedCount = count;
    }

    internal bool TryRead(out T[] records)
    {
        records = null;
        if (expected == null || expectedCount != entries.Count || expected != FurnitureReplicationPolicy.Stamp(entries.Select(pair =>
            new KeyValuePair<string, long>(pair.Key, pair.Value.Revision)))) return false;
        records = entries.Values.Select(entry => entry.Item).ToArray();
        Reset();
        return true;
    }
}
