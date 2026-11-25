using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace TldCoop;

// The authoritative container files survive a crash between the container
// commit and the write to the scene's furniture journal.
internal sealed class FurnitureContainerIndex
{
    private const int MaxContentsBytes = 2_000_000;
    private readonly Dictionary<string, HashSet<string>> byContainer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);

    internal static FurnitureContainerIndex Load(string directory, string scene)
    {
        var index = new FurnitureContainerIndex();
        if (!Directory.Exists(directory)) return index;
        string prefix = scene + "_";
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles()
            .Where(file => file.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                Guid.TryParse(file.Name[prefix.Length..], out _) && file.Length <= MaxContentsBytes)
            .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            try { index.Record(file.Name[prefix.Length..], File.ReadAllText(file.FullName)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return index;
    }

    internal string Owner(string id) =>
        Guid.TryParse(id, out var guid) ? owners.GetValueOrDefault(guid.ToString("D")) : null;

    internal void Record(string container, string compressed)
    {
        if (!TryRead(compressed, out var items)) return;
        if (byContainer.TryGetValue(container, out var previous))
            foreach (string id in previous)
                if (!items.Contains(id) && string.Equals(Owner(id), container, StringComparison.OrdinalIgnoreCase))
                    owners.Remove(id);
        byContainer[container] = items;
        foreach (string id in items) owners[id] = container;
    }

    private static bool TryRead(string compressed, out HashSet<string> items)
    {
        items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (compressed == "-") return true;
        if (string.IsNullOrEmpty(compressed) || compressed.Length > MaxContentsBytes) return false;
        try
        {
            var bytes = Convert.FromBase64String(compressed);
            if (bytes.Length < 4) return false;
            int length = BitConverter.ToInt32(bytes);
            if (length <= 0 || length > MaxContentsBytes) return false;
            using var input = new MemoryStream(bytes, 4, bytes.Length - 4);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var json = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = gzip.Read(json, offset, length - offset);
                if (read == 0) return false;
                offset += read;
            }
            if (gzip.ReadByte() != -1) return false;
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("m_SerializedDecorationItems", out var decorations) ||
                decorations.ValueKind == JsonValueKind.Null) return true;
            if (decorations.ValueKind != JsonValueKind.Array) return false;
            foreach (var entry in decorations.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("m_GUID", out var guid) &&
                    guid.ValueKind == JsonValueKind.String && Guid.TryParse(guid.GetString(), out var id))
                    items.Add(id.ToString("D"));
            return true;
        }
        catch (FormatException) { return false; }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }
}
