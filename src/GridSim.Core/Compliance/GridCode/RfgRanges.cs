using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>A U-Q/Pmax profile corner a user supplies: at <see cref="VoltagePu"/>, the reactive range from
/// <see cref="QMinPerPmax"/> (leading, absorbing, negative) to <see cref="QMaxPerPmax"/> (lagging, producing).</summary>
public sealed record UqCorner(double VoltagePu, double QMinPerPmax, double QMaxPerPmax);

/// <summary>A user U-Q/Pmax profile with the citation its corners were taken from.</summary>
public sealed record UqProfile(IReadOnlyList<UqCorner> Corners, string Citation);

/// <summary>
/// The RfG range checks (Regulation (EU) 2016/631): a TSO's choice must lie inside the range the Regulation gives, or it is
/// refused naming the parameter, the range and the article. Each method returns the violations (empty when the choice is
/// inside every range).
/// </summary>
public static class RfgRanges
{
    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The FRT profile against an RfG table (3.1, 3.2, 7.1, 7.2). A bound named after another parameter is that
    /// parameter's value in the same profile. The extended clearing time (0.14 to 0.25 s, "if system protection and secure
    /// operation so require") is taken only with <paramref name="extendedClearingTime"/>.</summary>
    public static IReadOnlyList<string> Validate(FrtProfile profile, RfgFrtTable table, bool extendedClearingTime = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(table);
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["uret"] = profile.Uret, ["uclear"] = profile.Uclear, ["urec1"] = profile.Urec1, ["urec2"] = profile.Urec2,
            ["tclear"] = profile.Tclear, ["trec1"] = profile.Trec1, ["trec2"] = profile.Trec2, ["trec3"] = profile.Trec3,
        };
        var problems = new List<string>();
        foreach (string name in new[] { "uret", "uclear", "urec1", "urec2", "tclear", "trec1", "trec2", "trec3" })
        {
            if (!table.Parameters.TryGetValue(name, out RfgParameterRange? range)) continue;
            double v = values[name];
            IReadOnlyList<RfgBound> max = extendedClearingTime && range.MaxExtended is { } ext ? ext : range.Max;
            double Bound(RfgBound b) => b.Value ?? values[b.Parameter!];
            bool ok = range.Min.All(b => v >= Bound(b)) && max.All(b => v <= Bound(b));
            if (ok) continue;
            string printed = extendedClearingTime && range.MaxExtended is not null
                ? $"{range.Printed}; extended clearing time taken"
                : range.Printed;
            string bounds = string.Join(" and ", range.Min.Select(b => ">= " + Describe(b, values)).Concat(max.Select(b => "<= " + Describe(b, values))));
            problems.Add($"{name} = {R(v)} is outside the RfG range {printed} ({bounds}), {table.Clause}");
        }
        return problems;
    }

    private static string Describe(RfgBound b, IReadOnlyDictionary<string, double> values) =>
        b.Value is double d ? R(d) : $"{b.Parameter} ({R(values[b.Parameter!])})";

    /// <summary>One value against a closed range [min, max]; one violation naming it, or none.</summary>
    public static IReadOnlyList<string> Range(string name, double value, double min, double max, string unit, string clause)
    {
        if (double.IsFinite(value) && value >= min && value <= max) return Array.Empty<string>();
        return new[] { $"{name} = {R(value)} {unit} is outside the RfG range {R(min)} to {R(max)} {unit}, {clause}" };
    }

    /// <summary>A user U-Q/Pmax profile against the area's row of Table 8 (synchronous) or Table 9 (PPM): its Q/Pmax span
    /// (largest QMax less smallest QMin) and its voltage span (highest less lowest corner voltage) must not exceed the
    /// row's maxima.</summary>
    public static IReadOnlyList<string> ValidateUqProfile(UqProfile profile, ReactiveLimitTable table, string area)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(table);
        ReactiveLimitRow row = table.Rows.FirstOrDefault(r => r.Area == area)
            ?? throw new ArgumentException($"area '{area}' is not a row of {table.Clause} (rows: {string.Join(", ", table.Rows.Select(r => r.Area))}).", nameof(area));
        var problems = new List<string>();
        if (profile.Corners.Count == 0) { problems.Add("uqProfile has no corners"); return problems; }
        double qSpan = profile.Corners.Max(c => c.QMaxPerPmax) - profile.Corners.Min(c => c.QMinPerPmax);
        double vSpan = profile.Corners.Max(c => c.VoltagePu) - profile.Corners.Min(c => c.VoltagePu);
        if (qSpan > row.QSpanPerPmax)
            problems.Add($"uqProfile Q/Pmax span {R(qSpan)} exceeds the maximum {R(row.QSpanPerPmax)} for {row.Printed}, {table.Clause}");
        if (vSpan > row.VoltageSpanPu)
            problems.Add($"uqProfile voltage span {R(vSpan)} pu exceeds the maximum {R(row.VoltageSpanPu)} pu for {row.Printed}, {table.Clause}");
        return problems;
    }
}
