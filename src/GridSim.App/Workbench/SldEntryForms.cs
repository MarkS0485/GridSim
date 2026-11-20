using System.Globalization;
using GridSim.Core.Presentation.Workbench.Sld;

namespace GridSim.App.Workbench;

/// <summary>A generator as typed into the Add generator dialog.</summary>
public sealed record GeneratorEntry(double CapacityMva, double OutputMw, double VoltageSetpointPu);

/// <summary>A load as typed into the Add load dialog.</summary>
public sealed record LoadEntry(double PMw, double QMvar);

/// <summary>A branch as typed into the Add branch dialog. <see cref="BPu"/> applies to a line, <see cref="TapRatio"/> to a transformer.</summary>
public sealed record BranchEntry(SldBranchKind Kind, int From, int To, double RPu, double XPu, double BPu, double RatingMva, double TapRatio);

/// <summary>
/// The typed validation behind the workbench's entry dialogs (A5). Every value is read with the invariant culture and must be a
/// finite number in a sensible range; each parser returns null and the value on success, else the message to show. The edit itself
/// is an <see cref="SldCommand"/> given to the document, which does its own checks (a bus that exists, two different buses).
/// </summary>
public static class SldEntryForms
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Fields: capacity MVA, output P MW, voltage setpoint pu.</summary>
    public static string? TryGenerator(IReadOnlyList<string> v, out GeneratorEntry? entry)
    {
        entry = null;
        if (!Num(v, 0, "Capacity (MVA)", out double cap, out string? e)) return e;
        if (!Num(v, 1, "Output P (MW)", out double p, out e)) return e;
        if (!Num(v, 2, "Voltage setpoint (pu)", out double vset, out e)) return e;
        if (cap <= 0) return "Capacity must be above 0 MVA.";
        if (p < 0 || p > cap) return "Output P must be between 0 and the capacity.";
        if (vset < 0.5 || vset > 1.5) return "Voltage setpoint must be between 0.5 and 1.5 pu.";
        entry = new GeneratorEntry(cap, p, vset);
        return null;
    }

    /// <summary>Fields: P MW, Q MVAr.</summary>
    public static string? TryLoad(IReadOnlyList<string> v, out LoadEntry? entry)
    {
        entry = null;
        if (!Num(v, 0, "P (MW)", out double p, out string? e)) return e;
        if (!Num(v, 1, "Q (MVAr)", out double q, out e)) return e;
        if (p < 0) return "P must not be negative.";
        entry = new LoadEntry(p, q);
        return null;
    }

    /// <summary>Fields: kind (line or transformer), from bus, to bus, R pu, X pu, B pu (a line; may be empty), rating MVA (may be empty), tap ratio (a transformer; may be empty).</summary>
    public static string? TryBranch(IReadOnlyList<string> v, out BranchEntry? entry)
    {
        entry = null;
        string kindText = Get(v, 0).ToLowerInvariant();
        SldBranchKind kind;
        if (kindText is "line" or "l") kind = SldBranchKind.Line;
        else if (kindText is "transformer" or "t" or "xfmr") kind = SldBranchKind.Transformer;
        else return "Kind must be line or transformer.";
        if (!Int(v, 1, "From bus", out int from, out string? e)) return e;
        if (!Int(v, 2, "To bus", out int to, out e)) return e;
        if (from == to) return "A branch needs two different buses.";
        if (!Num(v, 3, "R (pu)", out double r, out e)) return e;
        if (!Num(v, 4, "X (pu)", out double x, out e)) return e;
        if (!Num(v, 5, "B (pu)", out double b, out e, 0)) return e;
        if (!Num(v, 6, "Rating (MVA)", out double rating, out e, 0)) return e;
        if (!Num(v, 7, "Tap ratio", out double tap, out e, 1)) return e;
        if (r < 0) return "R must not be negative.";
        if (x == 0 && r == 0) return "R and X cannot both be 0: the branch would be a short circuit.";
        if (kind == SldBranchKind.Line && x <= 0) return "A line needs X above 0.";
        if (b < 0) return "B must not be negative.";
        if (rating < 0) return "Rating must not be negative.";
        if (tap < 0.5 || tap > 2) return "Tap ratio must be between 0.5 and 2.";
        entry = new BranchEntry(kind, from, to, r, x, kind == SldBranchKind.Line ? b : 0, rating, kind == SldBranchKind.Transformer ? tap : 1);
        return null;
    }

    private static string Get(IReadOnlyList<string> v, int i) => i < v.Count ? v[i].Trim() : "";

    private static bool Num(IReadOnlyList<string> v, int i, string label, out double value, out string? error, double? whenEmpty = null)
    {
        string t = Get(v, i);
        value = 0;
        error = null;
        if (t.Length == 0 && whenEmpty is double d) { value = d; return true; }
        if (!double.TryParse(t, NumberStyles.Float, Inv, out value) || !double.IsFinite(value)) { error = $"{label} must be a number."; return false; }
        return true;
    }

    private static bool Int(IReadOnlyList<string> v, int i, string label, out int value, out string? error)
    {
        error = null;
        if (!int.TryParse(Get(v, i), NumberStyles.Integer, Inv, out value)) { error = $"{label} must be a whole number (a bus id)."; return false; }
        return true;
    }
}
