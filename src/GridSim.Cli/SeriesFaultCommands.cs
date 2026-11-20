using System.Globalization;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Cli;

/// <summary>
/// RC4 (last candidate): the two fault verbs the short-circuit family lacked. <c>gridsim fault-series &lt;case&gt; --branch &lt;i&gt; --open 1|2</c>
/// is the open-conductor (series) fault on the series element of one branch; <c>gridsim fault-simultaneous &lt;case&gt; --at &lt;bus&gt;:&lt;type&gt;[,...]</c>
/// is a set of shunt faults at several buses at the same instant. Both run on the operating point (the prefault power flow, as
/// <c>fault</c> does) and read the same sequence networks <c>fault --type</c> reads. The fault maths is Core's
/// <see cref="SeriesFault.OpenConductors"/> and <see cref="SimultaneousFault.Solve"/>; the network side is
/// <see cref="FaultAnalysis.SeriesBreak"/> and <see cref="FaultAnalysis.SequencePorts"/>. This file parses, prints and fills the shared
/// result writer. Exit codes: 0 done; 1 the case cannot be loaded or the study is refused by name; 2 usage; 3 the prefault power flow
/// did not converge, or an earth fault was asked where no earth current can flow (nothing is invented).
/// </summary>
internal static class SeriesFaultCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string R(double v) => v.ToString("R", Inv);

    private sealed class UsageException(string message) : Exception(message);

    private static readonly string[] SeriesFlags = ["--branch", "--open"];
    private static readonly string[] SimultaneousFlags = ["--at"];

    private sealed record Args(string? Positional, Dictionary<string, string> Flags);

    private static Args Parse(string verb, List<string> a, string[] allowed)
    {
        string? pos = null;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < a.Count; i++)
        {
            string t = a[i];
            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                if (pos is not null) throw new UsageException($"{verb} takes one case but got: {pos} {t}.");
                pos = t; continue;
            }
            if (Array.IndexOf(allowed, t) < 0) throw new UsageException($"{verb} does not use {t}. {verb} accepts: {string.Join(", ", allowed)}.");
            if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
            if (!flags.TryAdd(t, a[++i])) throw new UsageException($"{t} was given twice");
        }
        return new Args(pos, flags);
    }

    /// <summary>The entry point: <paramref name="a"/> is the whole argument list with the verb first.</summary>
    public static int Run(List<string> a, Func<string, GridModel> load, Func<GridModel, PowerFlowResult> prefaultSolve)
    {
        string verb = a[0].ToLowerInvariant();
        try
        {
            return verb switch
            {
                "fault-series" => Series(Parse(verb, a, SeriesFlags), load, prefaultSolve),
                "fault-simultaneous" => Simultaneous(Parse(verb, a, SimultaneousFlags), load, prefaultSolve),
                _ => throw new UsageException($"unknown verb '{verb}'"),
            };
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
    }

    private static GridModel? LoadCase(string verb, string? path, Func<string, GridModel> load, out int exit)
    {
        exit = CliExit.Ok;
        if (path is null) { Console.Error.WriteLine($"usage: gridsim {verb} <case> ... (see 'gridsim {verb} --help')"); exit = CliExit.Usage; return null; }
        try { return load(path); }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            Console.Error.WriteLine($"error loading case '{path}': {ex.Message}");
            exit = CliExit.CaseLoadFailure(ex);
            return null;
        }
    }

    private static int Whole(string flag, string v)
    {
        if (!int.TryParse(v, NumberStyles.Integer, Inv, out int x)) throw new UsageException($"{flag} expected a whole number but got '{v}'");
        return x;
    }

    private static PowerFlowResult? Prefault(string verb, GridModel m, Func<GridModel, PowerFlowResult> solve, out int exit)
    {
        exit = CliExit.Ok;
        PowerFlowResult p = solve(m);
        if (!p.Converged)   // never fault a non-converged prefault state, as fault does
        {
            Console.Error.WriteLine($"{verb}: the pre-fault power flow did not converge ({p.Status}, mismatch {p.MaxMismatch:E2}); no fault study is possible.");
            exit = CliExit.Verdict;
            return null;
        }
        return p;
    }

    private static double ToKa(double pu, double baseMva, double baseKv) => baseKv > 0 ? pu * baseMva / (Math.Sqrt(3.0) * baseKv) : double.NaN;

    private static string C(Complex c) => $"{c.Real.ToString("F4", Inv)}{(c.Imaginary < 0 ? "-" : "+")}j{Math.Abs(c.Imaginary).ToString("F4", Inv)}";

    private static readonly string[] PhaseNames = ["a", "b", "c"];

    // ---- fault-series -------------------------------------------------------------------------------------------------

    private static int Series(Args a, Func<string, GridModel> load, Func<GridModel, PowerFlowResult> solve)
    {
        const string usage = "usage: gridsim fault-series <case> --branch <index> --open 1|2 (see 'gridsim fault-series --help')";
        if (!a.Flags.TryGetValue("--branch", out string? branchText) || !a.Flags.TryGetValue("--open", out string? openText)) { Console.Error.WriteLine(usage); return CliExit.Usage; }
        int branch = Whole("--branch", branchText);
        int open = Whole("--open", openText);
        if (open is not (1 or 2)) throw new UsageException($"--open expected 1 or 2 (conductors open) but got '{openText}'");
        GridModel? m = LoadCase("fault-series", a.Positional, load, out int exit);
        if (m is null) return exit;
        if (branch < 0 || branch >= m.Branches.Count)
            throw new UsageException($"--branch {branch}: case '{m.Name}' has branches 0..{m.Branches.Count - 1}.");
        PowerFlowResult? pre = Prefault("fault-series", m, solve, out exit);
        if (pre is null) return exit;

        SeriesBreakNetwork net;
        SeriesFaultResult r;
        try
        {
            net = FaultAnalysis.SeriesBreak(m, pre, branch);
            r = SeriesFault.OpenConductors(net.IPrePositive, net.Zs1, net.Zs2, net.Zs0, open);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine($"fault-series: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }

        Branch br = m.Branches[branch];
        double kv = net.BaseKv;
        string what = open == 1 ? "one conductor open (phase a)" : "two conductors open (phases b and c)";
        Console.WriteLine($"Series fault, {what}  -  {m.Name}  branch {branch} ({br.From}-{br.To})");
        Console.WriteLine($"  prefault current through the element: {net.IPrePositive.Magnitude.ToString("F4", Inv)} pu"
            + (kv > 0 ? $" ({ToKa(net.IPrePositive.Magnitude, m.BaseMva, kv).ToString("F3", Inv)} kA)" : ""));
        Console.WriteLine($"  impedances across the break (pu): Zs1 {C(net.Zs1)}  Zs2 {C(net.Zs2)}  Zs0 {C(net.Zs0)}");
        Complex[] phaseI = [r.Ia, r.Ib, r.Ic], phaseV = [r.Va, r.Vb, r.Vc];
        for (int p = 0; p < 3; p++)
            Console.WriteLine($"  phase {PhaseNames[p]}: current {phaseI[p].Magnitude.ToString("F4", Inv)} pu"
                + (kv > 0 ? $" ({ToKa(phaseI[p].Magnitude, m.BaseMva, kv).ToString("F3", Inv)} kA)" : "")
                + $"   voltage across the break {phaseV[p].Magnitude.ToString("F4", Inv)} pu");
        Console.WriteLine($"  sequence currents |I1| {r.I1.Magnitude.ToString("F4", Inv)}  |I2| {r.I2.Magnitude.ToString("F4", Inv)}  |I0| {r.I0.Magnitude.ToString("F4", Inv)} pu");
        Console.WriteLine("  (generator X''d synthesised at " + FaultAnalysis.DefaultSubtransientXdPu.ToString("F2", Inv) + " pu where not supplied, X2 = X''d where not supplied; "
            + "line Z0 = 3 x Z1 where not supplied; loads constant impedance in the positive and negative sequence, neglected in the zero sequence; "
            + "the break is in the series element, line charging stays at the buses)");

        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", m.Name).Set("buses", m.BusCount).Set("branch", branch).Set("from", br.From).Set("to", br.To)
                .Set("open_conductors", open).Set("base_kv", kv)
                .Set("i_pre_pu", net.IPrePositive.Magnitude).Set("i_pre_ka", ToKa(net.IPrePositive.Magnitude, m.BaseMva, kv))
                .Set("zs1_re_pu", net.Zs1.Real).Set("zs1_im_pu", net.Zs1.Imaginary)
                .Set("zs2_re_pu", net.Zs2.Real).Set("zs2_im_pu", net.Zs2.Imaginary)
                .Set("zs0_re_pu", net.Zs0.Real).Set("zs0_im_pu", net.Zs0.Imaginary)
                .Set("i1_pu", r.I1.Magnitude).Set("i2_pu", r.I2.Magnitude).Set("i0_pu", r.I0.Magnitude);
            CliResultTable t = rep.Table("phases", "phase", "i_re_pu", "i_im_pu", "i_mag_pu", "i_mag_ka", "v_across_re_pu", "v_across_im_pu", "v_across_mag_pu");
            for (int p = 0; p < 3; p++)
                t.Row(PhaseNames[p], phaseI[p].Real, phaseI[p].Imaginary, phaseI[p].Magnitude, ToKa(phaseI[p].Magnitude, m.BaseMva, kv),
                    phaseV[p].Real, phaseV[p].Imaginary, phaseV[p].Magnitude);
            rep.Complete();
        }
        return CliExit.Ok;
    }

    // ---- fault-simultaneous -------------------------------------------------------------------------------------------

    private static bool TryType(string s, out PortFault f, out bool needsEarth, out string label)
    {
        needsEarth = false;
        switch (s.ToLowerInvariant())
        {
            case "3ph": f = PortFault.ThreePhase; label = "three-phase"; return true;
            case "ll": f = PortFault.LineToLineBC; label = "line to line (b-c)"; return true;
            case "slg": f = PortFault.LineToGroundA; needsEarth = true; label = "single line to ground (a)"; return true;
            case "dlg": f = PortFault.DoubleLineToGroundBC; needsEarth = true; label = "double line to ground (b-c-earth)"; return true;
            default: f = PortFault.None; label = ""; return false;
        }
    }

    private static int Simultaneous(Args a, Func<string, GridModel> load, Func<GridModel, PowerFlowResult> solve)
    {
        const string usage = "usage: gridsim fault-simultaneous <case> --at <bus>:<3ph|slg|ll|dlg>[,<bus>:<type>...] (see 'gridsim fault-simultaneous --help')";
        if (!a.Flags.TryGetValue("--at", out string? spec)) { Console.Error.WriteLine(usage); return CliExit.Usage; }
        var buses = new List<int>();
        var faults = new List<PortFault>();
        var earth = new List<bool>();
        var labels = new List<string>();
        var typeText = new List<string>();
        foreach (string part in spec.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split(':');
            if (kv.Length != 2 || !int.TryParse(kv[0], NumberStyles.Integer, Inv, out int bus) || !TryType(kv[1], out PortFault f, out bool needsEarth, out string label))
                throw new UsageException($"--at expected a comma list of <bus>:<3ph|slg|ll|dlg> but got '{part}'");
            if (buses.Contains(bus)) throw new UsageException($"--at names bus {bus} twice; each bus takes one fault.");
            buses.Add(bus); faults.Add(f); earth.Add(needsEarth); labels.Add(label); typeText.Add(kv[1].ToLowerInvariant());
        }
        GridModel? m = LoadCase("fault-simultaneous", a.Positional, load, out int exit);
        if (m is null) return exit;
        foreach (int b in buses)
            if (!m.Buses.Any(x => x.Id == b)) throw new UsageException($"--at: bus {b} is not in case '{m.Name}'.");
        PowerFlowResult? pre = Prefault("fault-simultaneous", m, solve, out exit);
        if (pre is null) return exit;

        SequencePortNetwork net;
        PortFaultResult[] res;
        try
        {
            net = FaultAnalysis.SequencePorts(m, pre, buses);
            for (int p = 0; p < buses.Count; p++)
                if (earth[p] && !net.ZeroSequenceGrounded[p])
                {
                    bool noData = !m.Branches.Any(b => b.FromWinding is not null || b.ToWinding is not null);
                    Console.Error.WriteLine($"fault-simultaneous: bus {buses[p]} reaches no earthed neutral in the zero sequence, so no earth current can flow there and a {typeText[p]} fault is not defined"
                        + (noData ? "; the model carries no transformer winding connections at all (MATPOWER and JSON v1 cannot hold them), so the result would reflect missing data. Exit 3." : ". Exit 3."));
                    return CliExit.Verdict;
                }
            res = SimultaneousFault.Solve(net.Impedances, net.Prefault, faults);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine($"fault-simultaneous: {CliExit.UserMessage(ex)}");
            return CliExit.InputError;
        }

        Console.WriteLine($"Simultaneous faults  -  {m.Name}  {string.Join(", ", buses.Select((b, i) => $"bus {b} {labels[i]}"))}");
        for (int p = 0; p < buses.Count; p++)
        {
            PortFaultResult r = res[p];
            double kv = net.BaseKv[p];
            Console.WriteLine($"  bus {buses[p]}: {labels[p]}");
            Console.WriteLine("    phase currents: " + string.Join("  ", Enumerable.Range(0, 3).Select(ph =>
                $"I{PhaseNames[ph]} {r.PhaseCurrents[ph].Magnitude.ToString("F3", Inv)} pu" + (kv > 0 ? $" ({ToKa(r.PhaseCurrents[ph].Magnitude, m.BaseMva, kv).ToString("F3", Inv)} kA)" : ""))));
            Console.WriteLine($"    earth current |3I0|: {(3.0 * r.I0).Magnitude.ToString("F3", Inv)} pu   sequence |I1| {r.I1.Magnitude.ToString("F3", Inv)}  |I2| {r.I2.Magnitude.ToString("F3", Inv)}  |I0| {r.I0.Magnitude.ToString("F3", Inv)} pu");
            Console.WriteLine("    phase voltages: " + string.Join("  ", Enumerable.Range(0, 3).Select(ph => $"V{PhaseNames[ph]} {r.PhaseVoltages[ph].Magnitude.ToString("F3", Inv)}")) + " pu");
        }
        Console.WriteLine("  (generator X''d synthesised at " + FaultAnalysis.DefaultSubtransientXdPu.ToString("F2", Inv) + " pu where not supplied, X2 = X''d where not supplied; "
            + "line Z0 = 3 x Z1 where not supplied; loads neglected in the zero sequence; converter units keep their prefault current; all faults solid)");

        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", m.Name).Set("buses", m.BusCount).Set("ports", buses.Count);
            CliResultTable t = rep.Table("ports", "bus", "type", "base_kv", "phase_a_pu", "phase_b_pu", "phase_c_pu", "phase_a_ka", "phase_b_ka", "phase_c_ka",
                "earth_current_pu", "i1_pu", "i2_pu", "i0_pu", "va_pu", "vb_pu", "vc_pu", "zero_sequence_grounded");
            for (int p = 0; p < buses.Count; p++)
            {
                PortFaultResult r = res[p];
                double kv = net.BaseKv[p];
                t.Row(buses[p], typeText[p], kv, r.PhaseCurrents[0].Magnitude, r.PhaseCurrents[1].Magnitude, r.PhaseCurrents[2].Magnitude,
                    ToKa(r.PhaseCurrents[0].Magnitude, m.BaseMva, kv), ToKa(r.PhaseCurrents[1].Magnitude, m.BaseMva, kv), ToKa(r.PhaseCurrents[2].Magnitude, m.BaseMva, kv),
                    (3.0 * r.I0).Magnitude, r.I1.Magnitude, r.I2.Magnitude, r.I0.Magnitude,
                    r.PhaseVoltages[0].Magnitude, r.PhaseVoltages[1].Magnitude, r.PhaseVoltages[2].Magnitude, net.ZeroSequenceGrounded[p]);
            }
            rep.Complete();
        }
        return CliExit.Ok;
    }
}
