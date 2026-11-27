using System.Globalization;
using System.Text.Json;

namespace GridSim.Core.Analysis.StudyCases;

// RC4 R4-01: read-only result diff. Two result documents are compared member by member in document order; a number is
// the same only when it is the identical IEEE double, and a differing number reports its distance in units in the last
// place (ULP). The first difference in document order is the "first differing quantity".

public enum ResultDiffKind { Changed, Added, Removed, LengthChanged, TypeChanged }

/// <summary>One difference. <see cref="UlpDistance"/> and <see cref="AbsoluteDifference"/> are set for two numbers.</summary>
public sealed record ResultDiffEntry(ResultDiffKind Kind, string Path, string? A, string? B, ulong? UlpDistance, double? AbsoluteDifference);

public sealed record ResultDiffResult(int Differences, IReadOnlyList<ResultDiffEntry> Entries, bool Truncated, bool BytesIdentical)
{
    public bool Equal => Differences == 0;
    public ResultDiffEntry? First => Entries.Count > 0 ? Entries[0] : null;
}

public static class ResultDiff
{
    /// <param name="maxEntries">How many differences to keep (all are counted); 0 keeps every one.</param>
    public static ResultDiffResult Compare(byte[] a, byte[] b, int maxEntries = 100)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (maxEntries < 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        using JsonDocument da = Parse(a, "first result"), db = Parse(b, "second result");
        var entries = new List<ResultDiffEntry>();
        int count = 0;
        void Add(ResultDiffEntry e)
        {
            count++;
            if (maxEntries == 0 || entries.Count < maxEntries) entries.Add(e);
        }
        Walk(da.RootElement, db.RootElement, "$", Add);
        return new ResultDiffResult(count, entries, count > entries.Count, a.AsSpan().SequenceEqual(b));
    }

    private static JsonDocument Parse(byte[] bytes, string what)
    {
        try { return JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes)); }
        catch (JsonException ex) { throw new StudyCaseException($"The {what} is not valid JSON: {ex.Message}"); }
    }

    private static void Walk(JsonElement a, JsonElement b, string path, Action<ResultDiffEntry> add)
    {
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            var mb = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in a.EnumerateObject())
            {
                seen.Add(p.Name);
                string at = path + "." + p.Name;
                if (mb.TryGetValue(p.Name, out JsonElement vb)) Walk(p.Value, vb, at, add);
                else add(new(ResultDiffKind.Removed, at, p.Value.GetRawText(), null, null, null));
            }
            foreach (JsonProperty p in b.EnumerateObject())
                if (!seen.Contains(p.Name)) add(new(ResultDiffKind.Added, path + "." + p.Name, null, p.Value.GetRawText(), null, null));
            return;
        }
        if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] ea = a.EnumerateArray().ToArray(), eb = b.EnumerateArray().ToArray();
            if (ea.Length != eb.Length)
                add(new(ResultDiffKind.LengthChanged, path, ea.Length.ToString(CultureInfo.InvariantCulture), eb.Length.ToString(CultureInfo.InvariantCulture), null, null));
            for (int i = 0; i < Math.Min(ea.Length, eb.Length); i++)
                Walk(ea[i], eb[i], $"{path}[{i}]", add);
            return;
        }
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            double x = a.GetDouble(), y = b.GetDouble();
            if (BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)) return;
            add(new(ResultDiffKind.Changed, path, a.GetRawText(), b.GetRawText(), Ulps(x, y), Math.Abs(y - x)));
            return;
        }
        if (a.ValueKind != b.ValueKind)
        {
            add(new(ResultDiffKind.TypeChanged, path, a.GetRawText(), b.GetRawText(), null, null));
            return;
        }
        if (a.ValueKind == JsonValueKind.String ? !string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal) : false)
            add(new(ResultDiffKind.Changed, path, a.GetRawText(), b.GetRawText(), null, null));
    }

    /// <summary>The number of representable doubles between <paramref name="x"/> and <paramref name="y"/> (0 for the
    /// two zeros). Doubles are mapped onto a monotonic integer line, so adjacent values differ by exactly 1 across zero.</summary>
    public static ulong Ulps(double x, double y)
    {
        long ix = Ordered(x), iy = Ordered(y);
        return ix >= iy ? (ulong)ix - (ulong)iy : (ulong)iy - (ulong)ix;
    }

    private static long Ordered(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        return bits < 0 ? long.MinValue - bits : bits;
    }
}
