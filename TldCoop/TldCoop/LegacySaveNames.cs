using System;

namespace TldCoop;

internal static class LegacySaveNames
{
    internal static string PreviousKey(string key) => key.StartsWith("tldcoop_", StringComparison.Ordinal)
        ? "skycoop_" + key.Substring("tldcoop_".Length) : null;

    internal static string UpgradeTypeNames(string data) => data?
        .Replace("SkyCoop.MyMod", "TldCoop.DataStr", StringComparison.Ordinal)
        .Replace("SkyCoop.", "TldCoop.", StringComparison.Ordinal);
}
