using System.Globalization;
using System.Text;
using GridSim.Core.Analysis;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Matpower;

/// <summary>The text of a written MATPOWER case and the notes on what MATPOWER's format could not carry.</summary>
public sealed record MatpowerWriteResult(string Text, IReadOnlyList<string> Notes);

/// <summary>
/// Writes a <see cref="GridModel"/> as a MATPOWER <c>.m</c> case (version 2) that <see cref="MatpowerParser"/> reads
/// back to the same network (F24, October 2026: the <c>convert</c> verb's MATPOWER side).
///
/// <para><b>Exactness.</b> Every number is written in round-trip form ("R", invariant culture; unlimited limits as
/// <c>Inf</c> / <c>-Inf</c>), and buses, generators and branches keep model order, so the parsed model solves
/// bit-identically to the model written. Generators are the whole fleet (<see cref="GridModel.AllGenerators"/>), an
/// out-of-service unit with status 0 (F16). A branch's ratio column is its tap when it is a transformer, else 0 (a
/// plain line), so the parser reproduces <see cref="Branch.IsTransformer"/> and <see cref="Branch.TapRatio"/>.</para>
///
/// <para><b>What MATPOWER cannot carry</b> is listed in the notes and as comments in the file, never dropped
/// silently: names, layout coordinates, ZIP loads, generator dynamics and fault data, the nominal frequency (a
/// MATPOWER file has none; the parser assumes 50 Hz unless told), OLTCs, boundaries and the asset bundles. A bus with no
/// per-bus voltage limits gets the operational band (0.94 / 1.06 pu) in VMAX / VMIN, the band GridSim's checks fall
/// back to. The parsed case then CARRIES those per-bus limits, so it is judged exactly as before only while the
/// caller's global band is the operational band; a check run with a different global band judges the reloaded case
/// against 0.94 / 1.06 at those buses (the note says how many buses this affects). gencost is written when every generator
/// carries a cost curve (and the reactive rows when every one carries a reactive curve); DC lines are written when
/// given (they come from the parse result, not the model).</para>
/// </summary>
public static class MatpowerWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Write <paramref name="model"/> (and any <paramref name="dcLines"/> from its parse result) as MATPOWER
    /// text. A NaN anywhere, or an infinite value where MATPOWER needs a finite one, is refused with
    /// <see cref="InvalidDataException"/> naming it.</summary>
    public static MatpowerWriteResult Write(GridModel model, IReadOnlyList<MatpowerDcLine>? dcLines = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        GridSim.Core.IO.Json.CaseWriterChecks.RefuseLccLinks(model, "MATPOWER writer");
        var notes = new List<string>();
        var sb = new StringBuilder();

        string fn = FunctionName(model.Name);
        sb.Append("function mpc = ").Append(fn).Append('\n');
        sb.Append('%').Append(fn.ToUpperInvariant()).Append("  Written by GridSim (MatpowerWriter, F24) from model '")
          .Append(model.Name.Replace('\n', ' ').Replace('\r', ' ')).Append("'.\n");
        sb.Append("%   Nominal frequency ").Append(N(model.FrequencyHz, "FrequencyHz"))
          .Append(" Hz (a MATPOWER file carries none: pass it when you load this file).\n\n");
        sb.Append("%% MATPOWER Case Format : Version 2\n");
        sb.Append("mpc.version = '2';\n\n");
        sb.Append("%% system MVA base\n");
        sb.Append("mpc.baseMVA = ").Append(Finite(model.BaseMva, "baseMVA")).Append(";\n\n");

        // --- bus ---------------------------------------------------------------------------------------------------
        int noLimits = 0, named = 0, zip = 0, coords = 0;
        sb.Append("%% bus data\n");
        sb.Append("%\tbus_i\ttype\tPd\tQd\tGs\tBs\tarea\tVm\tVa\tbaseKV\tzone\tVmax\tVmin\n");
        sb.Append("mpc.bus = [\n");
        foreach (Bus b in model.Buses)
        {
            string at = $"bus {b.Id}";
            int type = b.Type switch { BusType.Slack => 3, BusType.Pv => 2, _ => 1 };
            double vmax = b.VmaxPu ?? VoltageLimits.OperationalHighPu;
            double vmin = b.VminPu ?? VoltageLimits.OperationalLowPu;
            if (b.VmaxPu is null || b.VminPu is null) noLimits++;
            if (b.Name is not null) named++;
            if (b.Zip is not null) zip++;
            if (b.X != 0 || b.Y != 0) coords++;
            Row(sb, b.Id.ToString(Inv), type.ToString(Inv),
                Finite(b.Pd, at + " Pd"), Finite(b.Qd, at + " Qd"), Finite(b.Gs, at + " Gs"), Finite(b.Bs, at + " Bs"), "1",
                Finite(b.Vm, at + " Vm"), Finite(b.VaDeg, at + " Va"), Finite(b.BaseKv, at + " baseKV"), "1",
                Finite(vmax, at + " Vmax"), Finite(vmin, at + " Vmin"));
        }
        sb.Append("];\n\n");
        // Invariant culture: the note is written into the .m file, whose bytes must not depend on the machine's culture.
        if (noLimits > 0) notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{noLimits} bus(es) carry no per-bus voltage limits; VMAX / VMIN were written as the operational band {VoltageLimits.OperationalHighPu:R} / {VoltageLimits.OperationalLowPu:R} pu, the band GridSim's checks fall back to."));
        if (named > 0) notes.Add($"{named} bus name(s) not written (MATPOWER's bus columns have no name).");
        if (zip > 0) notes.Add($"{zip} bus(es) carry a ZIP (voltage-dependent) load, which MATPOWER cannot hold: the parsed case is constant power there and solves DIFFERENTLY.");
        if (coords > 0) notes.Add($"{coords} bus layout coordinate pair(s) not written.");

        // --- gen ---------------------------------------------------------------------------------------------------
        int genExtras = 0;
        sb.Append("%% generator data\n");
        sb.Append("%\tbus\tPg\tQg\tQmax\tQmin\tVg\tmBase\tstatus\tPmax\tPmin\n");
        sb.Append("mpc.gen = [\n");
        for (int i = 0; i < model.AllGenerators.Count; i++)
        {
            Generator g = model.AllGenerators[i];
            string at = $"generator {i} (bus {g.Bus})";
            if (g.Name is not null || g.H != 0 || g.Type.Length > 0 || g.DynamicsClass is not null || g.SubtransientXdPu != 0
                || g.NegativeSeqXPu != 0 || g.ConverterInterfaced is not null || g.ConverterFaultCurrentPu is not null || g.PowerFlowOnly
                || g.UnitTransformerXPu != 0 || g.GridForming)
                genExtras++;
            Row(sb, g.Bus.ToString(Inv), Finite(g.Pg, at + " Pg"), Finite(g.Qg, at + " Qg"), Limit(g.Qmax, at + " Qmax"),
                Limit(g.Qmin, at + " Qmin"), Finite(g.Vg, at + " Vg"), Finite(g.MBase, at + " mBase"), g.InService ? "1" : "0",
                Limit(g.Pmax, at + " Pmax"), Limit(g.Pmin, at + " Pmin"));
            // Bug hunt 2026-10-29, finding 48: PMIN is written like PMAX, QMAX and QMIN, so -Inf (which MATPOWER's own
            // cases use, and which the parser reads) round-trips; it used to be refused as "cannot hold".
            // The parser reads mBase 0 as the system base and refuses a negative one, so a negative mBase is refused
            // here too rather than written into a file that cannot be read back.
            if (g.MBase < 0)
                throw new InvalidDataException($"{at} has mBase {N(g.MBase, "mBase")}; a MATPOWER mBase must be 0 (the system base) or above.");
            if (g.MBase == 0) notes.Add($"{at} has mBase 0; the parser reads it as the system base.");
        }
        sb.Append("];\n\n");
        if (genExtras > 0) notes.Add($"{genExtras} generator(s) carry a name, type, inertia H or dynamics / fault data that MATPOWER's gen columns cannot hold; not written.");

        // --- branch ------------------------------------------------------------------------------------------------
        int brOff = 0, brExtras = 0, lineWithTap = 0;
        sb.Append("%% branch data\n");
        sb.Append("%\tfbus\ttbus\tr\tx\tb\trateA\trateB\trateC\tratio\tangle\tstatus\tangmin\tangmax\n");
        sb.Append("mpc.branch = [\n");
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch br = model.Branches[i];
            string at = $"branch {i} ({br.From}-{br.To})";
            if (!br.InService) brOff++;
            if (br.Name is not null || br.DynamicsClass is not null || br.LengthKm != 0 || br.Equipment is not null || br.R0 != 0 || br.X0 != 0
                || br.FromWinding is not null || br.ToWinding is not null || br.FromNeutralXPu != 0 || br.ToNeutralXPu != 0)
                brExtras++;
            // MATPOWER: ratio 0 = a line (tap 1). A transformer writes its tap. A "line" with an off-nominal tap (only a
            // hand-built model can have one) is written as a transformer, which the parser then marks as one.
            bool asTransformer = br.IsTransformer || br.TapRatio != 1.0;
            if (asTransformer && !br.IsTransformer) lineWithTap++;
            Row(sb, br.From.ToString(Inv), br.To.ToString(Inv), Finite(br.R, at + " r"), Finite(br.X, at + " x"), Finite(br.B, at + " b"),
                Finite(br.RateAMva, at + " rateA"), Finite(br.RateBMva, at + " rateB"), Finite(br.RateCMva, at + " rateC"),   // F20: B / C carried
                asTransformer ? Finite(br.TapRatio, at + " ratio") : "0",
                Finite(br.PhaseShiftDeg, at + " angle"), br.InService ? "1" : "0", "-360", "360");
        }
        sb.Append("];\n");
        if (brOff > 0) notes.Add($"{brOff} out-of-service branch(es) written with status 0; the parser drops them, so the parsed case's branch list is shorter (it solves the same).");
        if (brExtras > 0) notes.Add($"{brExtras} branch(es) carry a name, length, equipment, dynamics class or zero-sequence data that MATPOWER's branch columns cannot hold; not written.");
        if (lineWithTap > 0) notes.Add($"{lineWithTap} branch(es) not marked as transformers have an off-nominal tap; written as transformers (the parser will mark them so).");

        // --- gencost -----------------------------------------------------------------------------------------------
        IReadOnlyList<Generator> fleet = model.AllGenerators;
        bool allP = fleet.Count > 0 && fleet.All(g => g.Cost is not null);
        bool allQ = allP && fleet.All(g => g.ReactiveCost is not null);
        if (allP)
        {
            sb.Append("\n%% generator cost data\n");
            sb.Append("%\t1\tstartup\tshutdown\tn\tx1\ty1\t...\txn\tyn\n");
            sb.Append("%\t2\tstartup\tshutdown\tn\tc(n-1)\t...\tc0\n");
            sb.Append("mpc.gencost = [\n");
            foreach (Generator g in fleet) CostRow(sb, g.Cost!, $"generator on bus {g.Bus} cost");
            if (allQ) foreach (Generator g in fleet) CostRow(sb, g.ReactiveCost!, $"generator on bus {g.Bus} reactive cost");
            sb.Append("];\n");
        }
        else if (fleet.Any(g => g.Cost is not null))
            notes.Add("only some generators carry a cost curve; MATPOWER needs one per generator, so mpc.gencost was not written.");
        if (allP && !allQ && fleet.Any(g => g.ReactiveCost is not null))
            notes.Add("only some generators carry a reactive cost curve; the reactive gencost rows were not written.");

        // --- dcline ------------------------------------------------------------------------------------------------
        if (dcLines is { Count: > 0 })
        {
            sb.Append("\n%% dc line data (carried, not modelled by GridSim)\n");
            sb.Append("%\tF_BUS\tT_BUS\tBR_STATUS\tPF\tPT\tQF\tQT\tVF\tVT\tPMIN\tPMAX\tQMINF\tQMAXF\tQMINT\tQMAXT\tLOSS0\tLOSS1\n");
            sb.Append("mpc.dcline = [\n");
            foreach (MatpowerDcLine d in dcLines)
                Row(sb, d.FromBus.ToString(Inv), d.ToBus.ToString(Inv), d.InService ? "1" : "0",
                    Limit(d.PfMw, "dcline PF"), Limit(d.PtMw, "dcline PT"), Limit(d.QfMvar, "dcline QF"), Limit(d.QtMvar, "dcline QT"),
                    Limit(d.VfPu, "dcline VF"), Limit(d.VtPu, "dcline VT"), Limit(d.PminMw, "dcline PMIN"), Limit(d.PmaxMw, "dcline PMAX"),
                    Limit(d.QminFMvar, "dcline QMINF"), Limit(d.QmaxFMvar, "dcline QMAXF"), Limit(d.QminTMvar, "dcline QMINT"),
                    Limit(d.QmaxTMvar, "dcline QMAXT"), Limit(d.Loss0Mw, "dcline LOSS0"), Limit(d.Loss1, "dcline LOSS1"));
            sb.Append("];\n");
        }

        if (model.Oltcs.Count > 0) notes.Add($"{model.Oltcs.Count} on-load tap changer(s) not written (MATPOWER has none): the parsed case holds every tap fixed and its default solve can DIFFER.");
        if (model.Boundaries.Count > 0) notes.Add($"{model.Boundaries.Count} boundary(ies) not written (MATPOWER has none).");
        if (model.AssetDynamics is not null || model.Protections is not null) notes.Add("the asset-dynamics / protection bundles are not written (MATPOWER has none).");

        // The notes go into the file as comments too, so the file says what it lost wherever it travels.
        if (notes.Count > 0)
        {
            sb.Append("\n%% Not carried by this MATPOWER file (GridSim MatpowerWriter notes):\n");
            foreach (string n in notes) sb.Append("%   ").Append(n.Replace('\n', ' ')).Append('\n');
        }
        return new MatpowerWriteResult(sb.ToString(), notes);
    }

    private static void Row(StringBuilder sb, params string[] cells)
    {
        sb.Append('\t').Append(string.Join("\t", cells)).Append(";\n");
    }

    private static void CostRow(StringBuilder sb, GeneratorCost c, string what)
    {
        var cells = new List<string>
        {
            ((int)c.Model).ToString(Inv), Finite(c.StartupCost, what + " startup"), Finite(c.ShutdownCost, what + " shutdown"),
            c.PointCount.ToString(Inv),
        };
        foreach (double v in c.Coefficients) cells.Add(Finite(v, what + " coefficient"));
        Row(sb, cells.ToArray());
    }

    // A finite number in round-trip form; anything else refused, naming it.
    private static string Finite(double v, string what)
    {
        if (!double.IsFinite(v))
            throw new InvalidDataException($"{what} is {v.ToString(Inv)}, which a MATPOWER case cannot hold here.");
        return v.ToString("R", Inv);
    }

    // A value that may be unlimited (MATPOWER Inf / -Inf); NaN refused.
    private static string Limit(double v, string what)
    {
        if (double.IsPositiveInfinity(v)) return "Inf";
        if (double.IsNegativeInfinity(v)) return "-Inf";
        return Finite(v, what);
    }

    private static string N(double v, string what) => double.IsFinite(v) ? v.ToString("R", Inv) : throw new InvalidDataException($"{what} is not finite.");

    // A MATLAB function name: letters, digits and underscores, starting with a letter.
    private static readonly HashSet<string> MatlabKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "classdef", "continue", "else", "elseif", "end", "for", "function", "global", "if",
        "otherwise", "parfor", "persistent", "return", "spmd", "switch", "try", "while",
    };

    private static string FunctionName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        string s = sb.ToString().Trim('_');
        // GR-666: a MATLAB keyword (end, function, if, ...) is not a usable function name either.
        if (s.Length == 0 || !char.IsAsciiLetter(s[0]) || MatlabKeywords.Contains(s)) s = "case_" + s;
        // MATLAB truncates identifiers at 63 characters (namelengthmax); keep within it.
        return s.Length <= 63 ? s : s[..63];
    }
}
