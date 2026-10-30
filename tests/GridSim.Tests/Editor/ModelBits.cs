using System.Collections;
using System.Reflection;
using GridSim.Core.Model;

namespace GridSim.Tests.Editor;

/// <summary>
/// Bit-level comparison of two built <see cref="GridModel"/>s for the case editor proofs (P5, P6): every double by
/// <see cref="BitConverter.DoubleToInt64Bits(double)"/> (so 0.0 and -0.0 differ), enums, ints, bools and strings
/// exactly, lists element by element, nested GridSim records recursively by reflection. Covered: the name, base,
/// frequency, buses (with their ZIP blocks), generators, branches, OLTCs and boundaries.
/// </summary>
internal static class ModelBits
{
    /// <summary>Every difference as "path: a X, b Y" (empty = bit-identical).</summary>
    public static List<string> Differences(GridModel a, GridModel b, int limit = 50)
    {
        var diffs = new List<string>();
        Compare(a.Name, b.Name, "$.Name", diffs, limit, 0);
        Compare(a.BaseMva, b.BaseMva, "$.BaseMva", diffs, limit, 0);
        Compare(a.FrequencyHz, b.FrequencyHz, "$.FrequencyHz", diffs, limit, 0);
        Compare(a.Buses, b.Buses, "$.Buses", diffs, limit, 0);
        Compare(a.Generators, b.Generators, "$.Generators", diffs, limit, 0);
        Compare(a.Branches, b.Branches, "$.Branches", diffs, limit, 0);
        Compare(a.Oltcs, b.Oltcs, "$.Oltcs", diffs, limit, 0);
        Compare(a.Boundaries, b.Boundaries, "$.Boundaries", diffs, limit, 0);
        return diffs;
    }

    private static void Compare(object? x, object? y, string path, List<string> diffs, int limit, int depth)
    {
        if (diffs.Count >= limit) return;
        if (x is null || y is null)
        {
            if (x is not null || y is not null) diffs.Add($"{path}: a {Show(x)}, b {Show(y)}");
            return;
        }
        if (x.GetType() != y.GetType()) { diffs.Add($"{path}: a type {x.GetType().Name}, b type {y.GetType().Name}"); return; }
        switch (x)
        {
            case double dx:
                if (BitConverter.DoubleToInt64Bits(dx) != BitConverter.DoubleToInt64Bits((double)y)) diffs.Add($"{path}: a {Show(x)}, b {Show(y)}");
                return;
            case string or int or long or bool or Enum:
                if (!x.Equals(y)) diffs.Add($"{path}: a {Show(x)}, b {Show(y)}");
                return;
            case IList lx:
                var ly = (IList)y;
                if (lx.Count != ly.Count) { diffs.Add($"{path}: a {lx.Count} items, b {ly.Count} items"); return; }
                for (int i = 0; i < lx.Count; i++) Compare(lx[i], ly[i], $"{path}[{i}]", diffs, limit, depth + 1);
                return;
        }
        Type t = x.GetType();
        if (depth > 8 || t.Namespace is null || !t.Namespace.StartsWith("GridSim.", StringComparison.Ordinal))
        {
            if (!Equals(x, y)) diffs.Add($"{path}: a {Show(x)}, b {Show(y)}");
            return;
        }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name == "EqualityContract") continue;
            Compare(p.GetValue(x), p.GetValue(y), $"{path}.{p.Name}", diffs, limit, depth + 1);
        }
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + $" (bits {BitConverter.DoubleToInt64Bits(d):X16})",
        string s => "\"" + s + "\"",
        _ => v.ToString() ?? "?",
    };
}
