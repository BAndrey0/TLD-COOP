using System.Collections.Generic;
using System.Text;

namespace TldCoop;

internal static class GearJsonSlices
{
	internal static IEnumerable<(string Text, bool Last)> Split(string json)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(json);
		if (bytes.Length <= 500)
		{
			yield return (json, true);
			yield break;
		}

		for (int start = 0; start < bytes.Length;)
		{
			int end = System.Math.Min(start + 499, bytes.Length);
			while (end < bytes.Length && (bytes[end] & 0xC0) == 0x80) end--;
			yield return (Encoding.UTF8.GetString(bytes, start, end - start), end == bytes.Length);
			start = end;
		}
	}
}
