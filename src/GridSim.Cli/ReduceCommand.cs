using System.Globalization;
using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Equivalents;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.PowerFlow.Equivalents;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Cli;

/// <summary>
/// RC5 WP9: <c>reduce</c>, the Ward, extended Ward and REI network equivalents (GridSim.Core.PowerFlow.Equivalents). The
/// CLI reads flags, solves the base case, prints and writes; all arithmetic is in Core. The base case is solved with
/// reactive limits off (PV buses hold their set-points, the condition the equivalence is stated for), and the reduced case
/// is then solved from a flat start with the same options: the printed base-case check is the largest |V_reduced - V_full|
/// at the boundary. <c>--out</c> writes the reduced case as MATPOWER (.m) or a v2 JSON case (.json) through the existing
/// writers. The shared --json/--csv writer (RC5 WP11) writes the printed values: the boundary, the equivalent elements and the
/// base-case check. Exit codes: 0 done; 1 the case cannot be loaded or the reduction is refused; 2 usage; 3 the base case or
/// the reduced case does not converge.
/// <para>RC5 WP31: <c>--method sc</c> (the short-circuit equivalent, <see cref="ShortCircuitEquivalent"/>; no power flow, the
/// check is the IEC 60909 three-phase Ik'' at the boundary on the full and the reduced case) and <c>--method dynamic</c> (the
/// coherency-based dynamic equivalent, <see cref="InertialAggregation"/>, with <c>--groups FILE</c> or <c>--coherency R</c>
/// and <c>--xdp X</c>; <c>--out</c> also writes the <c>gridsim-dynamics/2</c> document beside the case). Both run in their
/// own functions below; the Ward, extended Ward and REI path is unchanged.</para>
/// <para>RC5 (SC equivalent writing): <c>--method sc --out</c> writes a JSON v2 case with every sequence field
/// (<c>V2CaseWriter.WriteWithFaultSequence</c>), reads it back and prints the reload check (the four fault types at every
/// kept bus); a <c>.m</c> output is refused by name, because MATPOWER holds no X''d and no sequence data.</para>
/// </summary>
internal static class ReduceCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The power-flow options of both solves: reactive limits off, a tight tolerance.</summary>
    internal static readonly PowerFlowOptions BaseOptions = new() { EnforceQLimits = false, Tolerance = 1e-10 };

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(List<string> a, Func<string, GridModel> load)
    {
        const string verb = "reduce";
        try
        {
            string? casePath = null, keep = null, method = null, groups = null, outPath = null;
            string? groupFile = null, coherency = null, xdp = null;   // RC5 WP31: --method dynamic
            for (int i = 1; i < a.Count; i++)
            {
                string t = a[i];
                if (!t.StartsWith("--", StringComparison.Ordinal))
                {
                    if (casePath is not null) throw new UsageException($"reduce takes one case argument but got: {casePath} {t}.");
                    casePath = t;
                    continue;
                }
                if (t is not ("--keep" or "--method" or "--rei-groups" or "--out" or "--groups" or "--coherency" or "--xdp"))
                    throw new UsageException($"reduce does not use {t}. reduce accepts: --keep, --method, --rei-groups, --groups, --coherency, --xdp, --out.");
                if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"{t} expected a value but none was given");
                string val = a[++i];
                switch (t)
                {
                    case "--keep": if (keep is not null) throw new UsageException("--keep was given twice"); keep = val; break;
                    case "--method": if (method is not null) throw new UsageException("--method was given twice"); method = val; break;
                    case "--rei-groups": if (groups is not null) throw new UsageException("--rei-groups was given twice"); groups = val; break;
                    case "--groups": if (groupFile is not null) throw new UsageException("--groups was given twice"); groupFile = val; break;
                    case "--coherency": if (coherency is not null) throw new UsageException("--coherency was given twice"); coherency = val; break;
                    case "--xdp": if (xdp is not null) throw new UsageException("--xdp was given twice"); xdp = val; break;
                    default: if (outPath is not null) throw new UsageException("--out was given twice"); outPath = val; break;
                }
            }
            if (casePath is null) throw new UsageException("usage: gridsim reduce <case> --keep <ids|file> --method ward|xward|rei [--rei-groups gen|gen,load] [--out <case.m|case.json>]"
                + ", or gridsim reduce <case> --keep <ids|file> --method sc [--out <case.json>]"
                + ", or gridsim reduce <case> --method dynamic --groups <file> | --coherency <r> --xdp <pu> [--out <case.m|case.json>]");
            bool isDynamic = string.Equals(method, "dynamic", StringComparison.OrdinalIgnoreCase);
            if (keep is null && !isDynamic) throw new UsageException("--keep is required: the bus ids to keep (a comma list, or a file of ids).");
            EquivalentMethod m = (method ?? throw new UsageException("--method is required: ward, xward, rei, sc or dynamic.")).ToLowerInvariant() switch
            {
                "ward" => EquivalentMethod.Ward,
                "xward" => EquivalentMethod.ExtendedWard,
                "rei" => EquivalentMethod.Rei,
                "sc" => EquivalentMethod.ShortCircuit,
                "dynamic" => EquivalentMethod.Dynamic,
                var other => throw new UsageException($"--method expected ward, xward, rei, sc or dynamic but got '{other}'"),
            };
            if (!isDynamic && (groupFile is not null || coherency is not null || xdp is not null))
                throw new UsageException("--groups, --coherency and --xdp apply to --method dynamic only");
            ReiGrouping g = ReiGrouping.Generators;
            if (groups is not null)
            {
                if (m != EquivalentMethod.Rei) throw new UsageException("--rei-groups applies to --method rei only");
                g = groups.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch
                {
                    "gen" => ReiGrouping.Generators,
                    "gen,load" => ReiGrouping.GeneratorsAndLoads,
                    var other => throw new UsageException($"--rei-groups expected gen or gen,load but got '{other}'"),
                };
            }
            if (outPath is not null)
            {
                string ext = Path.GetExtension(outPath).ToLowerInvariant();
                if (ext is not (".m" or ".json")) throw new UsageException($"--out expected a .m or .json file but got '{outPath}'");
                if (File.Exists(outPath)) throw new UsageException($"--out '{outPath}' already exists; name a new file (it is never overwritten)");
                // RC5 (SC equivalent writing): MATPOWER holds no X''d and no sequence data, so a written short-circuit
                // equivalent would give other fault currents (even three-phase: the fictitious sources would reload with
                // the default X''d). Refused by name, before anything is loaded.
                if (m == EquivalentMethod.ShortCircuit && ext == ".m")
                    throw new UsageException($"--method sc cannot write MATPOWER ('{outPath}'): MATPOWER holds no X''d, winding connections or "
                        + "zero-sequence data, so the written case's fault currents would differ from the equivalent's. Write a JSON v2 case (.json), which carries them exactly");
            }
            if (m == EquivalentMethod.Dynamic)
            {
                if (keep is not null) throw new UsageException("--keep does not apply to --method dynamic: every bus is kept and the groups name the machines to aggregate");
                return RunDynamic(casePath, groupFile, coherency, xdp, outPath, load);
            }
            IReadOnlyList<int> ids = ParseKeep(keep!);
            if (m == EquivalentMethod.ShortCircuit) return RunShortCircuit(casePath, ids, outPath, load);

            GridModel model;
            try { model = load(casePath); }
            catch (Exception ex) when (ex is not GateRefusedException)
            {
                Console.Error.WriteLine($"error loading case '{casePath}': {ex.Message}");
                return CliExit.CaseLoadFailure(ex);
            }

            PowerFlowResult full = SolverSelector.CreateNewton(model).Solve(model, BaseOptions);
            if (!full.Converged)
            {
                Console.Error.WriteLine($"error: reduce: the base case did not converge (status {full.Status}); nothing was reduced.");
                return CliExit.Verdict;
            }
            ReducedNetwork r;
            try { r = NetworkReduction.Reduce(model, full, new ReductionSpec(ids, m, g)); }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"error: reduce: {ex.Message}");
                return CliExit.InputError;
            }

            GridModel red = r.Model;
            PowerFlowResult solved = SolverSelector.CreateNewton(red).Solve(red, BaseOptions with { FlatStart = true });
            string methodName = m switch { EquivalentMethod.Ward => "ward", EquivalentMethod.ExtendedWard => "xward", _ => "rei" };
            Console.WriteLine($"reduce - {methodName} equivalent of {model.Name}: {model.BusCount} buses -> {red.BusCount} buses " +
                $"({ids.Count} kept, {r.ExternalBusIds.Count} external removed)");
            Console.WriteLine("  boundary buses: " + string.Join(", ", r.BoundaryBusIds.Select(b => b.ToString(Inv))));
            Console.WriteLine("  elements (pu on the system base):");
            foreach (EquivalentElement e in r.Elements)
                Console.WriteLine($"    {e.Kind,-20} {e.Name,-16} buses {string.Join("-", e.BusIds.Select(b => b.ToString(Inv))),-12} " +
                    $"{e.Value.Real.ToString("R", Inv)} + j({e.Value.Imaginary.ToString("R", Inv)})");
            foreach (string note in r.Notes) Console.WriteLine("  note: " + note);

            if (!solved.Converged)
            {
                Console.Error.WriteLine($"error: reduce: the reduced case did not converge from a flat start (status {solved.Status}).");
                return CliExit.Verdict;
            }
            double dv = NetworkReduction.MaxVoltageDifference(full, solved, r.BoundaryBusIds);
            Console.WriteLine($"  base-case check: max |V_reduced - V_full| at the boundary = {dv.ToString("E3", Inv)} pu " +
                $"(both solved to {BaseOptions.Tolerance.ToString("R", Inv)} pu, reactive limits off; reduced case from a flat start)");

            if (outPath is not null)
            {
                IReadOnlyList<string> wnotes;
                if (Path.GetExtension(outPath).Equals(".m", StringComparison.OrdinalIgnoreCase))
                {
                    MatpowerWriteResult w = MatpowerWriter.Write(red);
                    string fullPath = Path.GetFullPath(outPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write))
                    using (var sw = new StreamWriter(fs))
                        sw.Write(w.Text);
                    wnotes = w.Notes;
                }
                else
                {
                    wnotes = GridSim.Core.IO.Json.V2CaseWriter.Write(red, outPath).Notes;
                }
                Console.WriteLine($"  reduced case written to {outPath}");
                foreach (string note in wnotes) Console.WriteLine("  writer note: " + note);
            }
            CliResult? rep = CliOutput.Report;
            if (rep is not null)
            {
                rep.Set("case", model.Name).Set("method", methodName).Set("buses", model.BusCount).Set("reduced_buses", red.BusCount)
                   .Set("kept", ids.Count).Set("external_removed", r.ExternalBusIds.Count).Set("max_boundary_dv_pu", dv)
                   .Set("tolerance_pu", BaseOptions.Tolerance).Set("reduced_case", outPath is null ? null : Path.GetFileName(outPath));
                CliResultTable et = rep.Table("elements", "kind", "name", "buses", "re_pu", "im_pu");
                foreach (EquivalentElement e in r.Elements)
                    et.Row(e.Kind.ToString(), e.Name, string.Join("-", e.BusIds.Select(b => b.ToString(Inv))), e.Value.Real, e.Value.Imaginary);
                CliResultTable bt = rep.Table("boundary", "bus");
                foreach (int b in r.BoundaryBusIds) bt.Row(b);
                CliResultTable nt = rep.Table("notes", "note");
                foreach (string note in r.Notes) nt.Row(note);
                rep.Complete();
            }
            return CliExit.Ok;
        }
        catch (UsageException ex) { Console.Error.WriteLine($"error: {verb}: {ex.Message}"); return CliExit.Usage; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {verb}: {ex.Message}");
            return CliExit.InputError;
        }
    }

    /// <summary>
    /// RC5 WP31: <c>--method sc</c>. No power flow: the sequence networks are reduced (<see cref="ShortCircuitEquivalent"/>),
    /// the elements, notes and the external network's Thevenin impedances are printed, and the check is the IEC 60909
    /// three-phase Ik'' at every boundary bus with a base kV, on the full case and on the reduced case.
    /// </summary>
    private static int RunShortCircuit(string casePath, IReadOnlyList<int> ids, string? outPath, Func<string, GridModel> load)
    {
        GridModel model;
        try { model = load(casePath); }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            Console.Error.WriteLine($"error loading case '{casePath}': {ex.Message}");
            return CliExit.CaseLoadFailure(ex);
        }
        ShortCircuitEquivalentResult r;
        try { r = ShortCircuitEquivalent.Reduce(model, ids); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"error: reduce: {ex.Message}");
            return CliExit.InputError;
        }
        GridModel red = r.Network.Model;
        Console.WriteLine($"reduce - sc equivalent of {model.Name}: {model.BusCount} buses -> {red.BusCount} buses " +
            $"({ids.Count} kept, {r.Network.ExternalBusIds.Count} external removed)");
        Console.WriteLine("  boundary buses: " + string.Join(", ", r.Network.BoundaryBusIds.Select(b => b.ToString(Inv))));
        Console.WriteLine("  elements (pu on the system base; SC0 = zero sequence):");
        foreach (EquivalentElement e in r.Network.Elements)
            Console.WriteLine($"    {e.Kind,-20} {e.Name,-16} buses {string.Join("-", e.BusIds.Select(b => b.ToString(Inv))),-12} " +
                $"{e.Value.Real.ToString("R", Inv)} + j({e.Value.Imaginary.ToString("R", Inv)})");
        Console.WriteLine("  Thevenin impedances of the external network at the boundary (pu on the system base):");
        foreach (SequenceTheveninImpedance t in r.Thevenin)
            Console.WriteLine($"    bus {t.BusId.ToString(Inv)}: Z1 = {Z(t.Z1Pu)}, Z2 = {Z(t.Z2Pu)}, Z0 = {Z(t.Z0Pu)}");
        foreach (string note in r.Network.Notes) Console.WriteLine("  note: " + note);

        // The check: IEC 60909 three-phase Ik'' at each boundary bus with a base kV, full against reduced.
        double worst = double.NaN;
        string? notRun = null;
        try
        {
            foreach (int b in r.Network.BoundaryBusIds)
            {
                if (!(model.Buses.First(x => x.Id == b).BaseKv > 0)) continue;
                double full = Iec60909.ThreePhase(model, b).IkppKa;
                double reduced = Iec60909.ThreePhase(red, b).IkppKa;
                double rel = Math.Abs(reduced - full) / full;
                if (double.IsNaN(worst) || rel > worst || double.IsNaN(rel)) worst = rel;
            }
            if (double.IsNaN(worst)) notRun = "no boundary bus has a base kV";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            notRun = ex.Message;
            worst = double.NaN;
        }
        Console.WriteLine(notRun is null
            ? $"  check: max relative |Ik''_reduced - Ik''_full| (IEC 60909 three-phase, c max) at the boundary = {worst.ToString("E3", Inv)}"
            : $"  check: NOT RUN ({notRun})");

        double reloadWorst = double.NaN;
        string? reloadNotRun = null;
        if (outPath is not null)
        {
            // RC5: the JSON v2 case with every sequence field (lineWindings, machineZeroSequence, zeroSequenceMutuals),
            // then loaded back and checked: the three fault types at every kept bus with a base kV, bit for bit expected.
            IReadOnlyList<string> wnotes;
            try { wnotes = GridSim.Core.IO.Json.V2CaseWriter.WriteWithFaultSequence(red, outPath).Notes; }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"error: reduce: the short-circuit equivalent cannot be written as a JSON v2 case: {ex.Message}");
                return CliExit.InputError;
            }
            Console.WriteLine($"  reduced case written to {outPath}");
            foreach (string note in wnotes) Console.WriteLine("  writer note: " + note);
            (reloadWorst, reloadNotRun) = ReloadCheck(red, outPath, ids);
            Console.WriteLine(reloadNotRun is null
                ? $"  reload check: max relative |Ik''_reloaded - Ik''_reduced| (IEC 60909 three-phase, line-to-line, single- and double-line-to-earth, c max) at the kept buses = {reloadWorst.ToString("E3", Inv)}"
                : $"  reload check: NOT RUN ({reloadNotRun})");
        }
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", model.Name).Set("method", "sc").Set("buses", model.BusCount).Set("reduced_buses", red.BusCount)
               .Set("kept", ids.Count).Set("external_removed", r.Network.ExternalBusIds.Count)
               .Set("max_boundary_ikpp_rel", notRun is null ? (object)worst : null)
               .Set("reduced_case", outPath is null ? null : Path.GetFileName(outPath))
               .Set("max_reload_ikpp_rel", outPath is not null && reloadNotRun is null ? (object)reloadWorst : null);
            CliResultTable et = rep.Table("elements", "kind", "name", "buses", "re_pu", "im_pu");
            foreach (EquivalentElement e in r.Network.Elements)
                et.Row(e.Kind.ToString(), e.Name, string.Join("-", e.BusIds.Select(b => b.ToString(Inv))), e.Value.Real, e.Value.Imaginary);
            CliResultTable tt = rep.Table("thevenin", "bus", "z1_re_pu", "z1_im_pu", "z2_re_pu", "z2_im_pu", "z0_re_pu", "z0_im_pu");
            foreach (SequenceTheveninImpedance t in r.Thevenin)
                tt.Row(t.BusId, t.Z1Pu.Real, t.Z1Pu.Imaginary, t.Z2Pu.Real, t.Z2Pu.Imaginary, t.Z0Pu.Real, t.Z0Pu.Imaginary);
            CliResultTable bt = rep.Table("boundary", "bus");
            foreach (int b in r.Network.BoundaryBusIds) bt.Row(b);
            CliResultTable nt = rep.Table("notes", "note");
            foreach (string note in r.Network.Notes) nt.Row(note);
            rep.Complete();
        }
        return CliExit.Ok;
    }

    /// <summary>
    /// RC5 WP31: <c>--method dynamic</c>. The base case is solved as for Ward; the groups come from <c>--groups</c> (a file,
    /// one group per line, each a list of bus ids whose in-service units form the group) or from the slow-coherency
    /// grouping with <c>--coherency R</c> groups; every machine takes the classical X'd <c>--xdp</c> (pu on its own base)
    /// and no damping, so the machine data is tagged Synthetic. The reduced case is solved from a flat start; the check is
    /// the largest |V_reduced - V_full| over the buses kept as they are and the equivalent buses (against V_eq).
    /// </summary>
    private static int RunDynamic(string casePath, string? groupFile, string? coherency, string? xdp, string? outPath, Func<string, GridModel> load)
    {
        if ((groupFile is null) == (coherency is null))
            throw new UsageException("--method dynamic needs exactly one of --groups <file> (the coherent groups) and --coherency <r> (the number of groups to find)");
        if (xdp is null) throw new UsageException("--xdp is required for --method dynamic: the classical transient reactance X'd (pu on each machine's own base)");
        if (!double.TryParse(xdp, NumberStyles.Float, Inv, out double xdpPu) || !(xdpPu > 0) || !double.IsFinite(xdpPu))
            throw new UsageException($"--xdp expected a number above 0 but got '{xdp}'");
        int r = 0;
        if (coherency is not null && (!int.TryParse(coherency, NumberStyles.Integer, Inv, out r) || r < 1))
            throw new UsageException($"--coherency expected a whole number of groups, 1 or more, but got '{coherency}'");
        string? dynOut = null;
        if (outPath is not null)
        {
            dynOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, Path.GetFileNameWithoutExtension(outPath) + ".dynamics.json");
            if (File.Exists(dynOut)) throw new UsageException($"the dynamics file '{dynOut}' that --out writes beside the case already exists; name a new case file");
        }

        GridModel model;
        try { model = load(casePath); }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            Console.Error.WriteLine($"error loading case '{casePath}': {ex.Message}");
            return CliExit.CaseLoadFailure(ex);
        }
        PowerFlowResult full = SolverSelector.CreateNewton(model).Solve(model, BaseOptions);
        if (!full.Converged)
        {
            Console.Error.WriteLine($"error: reduce: the base case did not converge (status {full.Status}); nothing was reduced.");
            return CliExit.Verdict;
        }
        var machines = model.Generators.Select(_ => new ClassicalMachineData(xdpPu)).ToList();
        IReadOnlyList<IReadOnlyList<int>> groups;
        DynamicEquivalent eq;
        try
        {
            if (groupFile is not null) groups = ParseGroups(groupFile, model);
            else
            {
                double[] xdpArray = machines.Select(x => x.XdpPu).ToArray();
                groups = CoherencyGrouping.FromSolvedCase(model, full, xdpArray, r).Groups;
            }
            eq = InertialAggregation.Build(model, full, groups, machines, ProvenanceTag.Synthetic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: reduce: {ex.Message}");
            return CliExit.InputError;
        }

        GridModel red = eq.Model;
        PowerFlowResult solved = SolverSelector.CreateNewton(red).Solve(red, BaseOptions with { FlatStart = true });
        Console.WriteLine($"reduce - dynamic equivalent of {model.Name}: {model.Generators.Count} generators -> {red.Generators.Count} generators " +
            $"({eq.Groups.Count} group(s); {model.BusCount} buses -> {red.BusCount} buses)");
        Console.WriteLine($"  machine data: classical, X'd = {xdpPu.ToString("R", Inv)} pu on each machine base, no damping, H from the case (tagged Synthetic)");
        foreach (DynamicEquivalentGroup g in eq.Groups)
            Console.WriteLine($"    {g.Name,-6} bus {g.BusId.ToString(Inv),-6} members at buses {string.Join(",", g.GeneratorIndices.Select(k => model.Generators[k].Bus.ToString(Inv)))}: " +
                $"S {g.Machine.SMva.ToString("R", Inv)} MVA, H {g.Machine.InertiaH.ToString("R", Inv)} s, X'd {g.Machine.XdpPu.ToString("R", Inv)} pu, " +
                $"D {g.Machine.Damping.ToString("R", Inv)}, |V_eq| {g.VoltagePu.Magnitude.ToString("R", Inv)} pu{(g.Merged ? " (terminal buses merged)" : "")}");
        foreach (string note in eq.Notes) Console.WriteLine("  note: " + note);
        if (!solved.Converged)
        {
            Console.Error.WriteLine($"error: reduce: the reduced case did not converge from a flat start (status {solved.Status}).");
            return CliExit.Verdict;
        }
        var modelIds = new HashSet<int>(model.Buses.Select(b => b.Id));
        var keptIds = red.Buses.Where(b => modelIds.Contains(b.Id)).Select(b => b.Id).ToList();
        double dv = NetworkReduction.MaxVoltageDifference(full, solved, keptIds);
        foreach (DynamicEquivalentGroup g in eq.Groups)
        {
            if (!g.Merged) continue;
            int i = solved.BusIds.ToList().IndexOf(g.BusId);
            Complex vq = GridSim.Core.Numerics.DetMath.FromPolar(solved.Vm[i], solved.VaDeg[i] * Math.PI / 180.0);
            double d = (vq - g.VoltagePu).Magnitude;
            if (d > dv || double.IsNaN(d)) dv = d;
        }
        Console.WriteLine($"  base-case check: max |V_reduced - V_full| (V_eq at an equivalent bus) = {dv.ToString("E3", Inv)} pu " +
            $"(both solved to {BaseOptions.Tolerance.ToString("R", Inv)} pu, reactive limits off; reduced case from a flat start)");

        if (outPath is not null)
        {
            WriteCase(red, outPath);
            using (var fs = new FileStream(dynOut!, FileMode.CreateNew, FileAccess.Write))
            using (var sw = new StreamWriter(fs))
                sw.Write(eq.DynamicsJson);
            Console.WriteLine($"  machine dynamics (gridsim-dynamics/2) written to {dynOut}");
        }
        CliResult? rep = CliOutput.Report;
        if (rep is not null)
        {
            rep.Set("case", model.Name).Set("method", "dynamic").Set("generators", model.Generators.Count).Set("reduced_generators", red.Generators.Count)
               .Set("groups", eq.Groups.Count).Set("xdp_pu", xdpPu).Set("max_dv_pu", dv).Set("tolerance_pu", BaseOptions.Tolerance)
               .Set("reduced_case", outPath is null ? null : Path.GetFileName(outPath));
            CliResultTable gt = rep.Table("groups", "name", "bus", "member_buses", "s_mva", "h_s", "xdp_pu", "d", "v_eq_pu", "merged");
            foreach (DynamicEquivalentGroup g in eq.Groups)
                gt.Row(g.Name, g.BusId, string.Join(" ", g.GeneratorIndices.Select(k => model.Generators[k].Bus.ToString(Inv))), g.Machine.SMva,
                    g.Machine.InertiaH, g.Machine.XdpPu, g.Machine.Damping, g.VoltagePu.Magnitude, g.Merged ? "yes" : "no");
            CliResultTable nt = rep.Table("notes", "note");
            foreach (string note in eq.Notes) nt.Row(note);
            rep.Complete();
        }
        return CliExit.Ok;
    }

    /// <summary>
    /// RC5 (SC equivalent writing): read the written JSON v2 case back through the v2 loader (with the schemas the
    /// <c>validate</c> verb finds; not through the case-argument loader, so the written file is not recorded as an input)
    /// and compare the IEC 60909 Ik'' of the three-phase, line-to-line, single- and double-line-to-earth faults at every
    /// kept bus with a base kV, on the in-memory equivalent and on the loaded-back case. The written numbers are the
    /// shortest round-trip forms, so the expected difference is 0. Returns the largest relative difference (an Ik'' of 0
    /// on both sides counts 0), or the reason the check could not run.
    /// </summary>
    private static (double Worst, string? NotRun) ReloadCheck(GridModel red, string outPath, IReadOnlyList<int> ids)
    {
        string? schemas = Gated.FindV2Schemas(AppContext.BaseDirectory);
        if (schemas is null) return (double.NaN, "the v2 schemas (schemas/v2 beside the program or above it) were not found, so the written case was not read back");
        GridModel back;
        try { back = GridSim.Core.IO.Json.V2CaseLoader.Load(outPath, schemas); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return (double.NaN, "the written case could not be read back: " + ex.Message);
        }
        var faults = new Func<GridModel, int, double>[]
        {
            (mm, b) => Iec60909.ThreePhase(mm, b).IkppKa,
            (mm, b) => Iec60909.LineToLine(mm, b).IkppKa,
            (mm, b) => Iec60909.SingleLineToGround(mm, b).IkppKa,
            (mm, b) => Iec60909.DoubleLineToGround(mm, b).IkppKa,
        };
        double worst = 0.0;
        bool any = false;
        try
        {
            foreach (int b in ids)
            {
                if (!(red.Buses.First(x => x.Id == b).BaseKv > 0)) continue;
                foreach (Func<GridModel, int, double> f in faults)
                {
                    double before = f(red, b), after = f(back, b);
                    double rel = before == after ? 0.0 : Math.Abs(after - before) / Math.Abs(before);
                    if (double.IsNaN(rel) || rel > worst) worst = double.IsNaN(rel) ? double.PositiveInfinity : rel;
                    any = true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            return (double.NaN, ex.Message);
        }
        return any ? (worst, null) : (double.NaN, "no kept bus has a base kV");
    }

    /// <summary>A complex impedance for the console: "a + j(b)", or "infinite".</summary>
    private static string Z(Complex z) => double.IsInfinity(z.Real) || double.IsInfinity(z.Imaginary)
        ? "infinite" : $"{z.Real.ToString("R", Inv)} + j({z.Imaginary.ToString("R", Inv)})";

    /// <summary>Write the reduced case as MATPOWER (.m) or a v2 JSON case (.json), never over an existing file.</summary>
    private static void WriteCase(GridModel red, string outPath)
    {
        IReadOnlyList<string> wnotes;
        if (Path.GetExtension(outPath).Equals(".m", StringComparison.OrdinalIgnoreCase))
        {
            MatpowerWriteResult w = MatpowerWriter.Write(red);
            string fullPath = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write))
            using (var sw = new StreamWriter(fs))
                sw.Write(w.Text);
            wnotes = w.Notes;
        }
        else
        {
            wnotes = GridSim.Core.IO.Json.V2CaseWriter.Write(red, outPath).Notes;
        }
        Console.WriteLine($"  reduced case written to {outPath}");
        foreach (string note in wnotes) Console.WriteLine("  writer note: " + note);
    }

    /// <summary>The <c>--groups</c> file: one group per line, each a list of bus ids (commas, spaces or tabs between them);
    /// blank lines and lines starting with # are skipped. A group is every in-service unit at its buses, in case order.</summary>
    internal static IReadOnlyList<IReadOnlyList<int>> ParseGroups(string path, GridModel model)
    {
        if (!File.Exists(path)) throw new UsageException($"--groups file '{path}' does not exist");
        var groups = new List<IReadOnlyList<int>>();
        int line = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            line++;
            string text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var members = new List<int>();
            foreach (string tok in text.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(tok, NumberStyles.Integer, Inv, out int bus))
                    throw new InvalidDataException($"--groups file '{path}' line {line}: expected bus ids but got '{tok}'.");
                int before = members.Count;
                for (int k = 0; k < model.Generators.Count; k++)
                    if (model.Generators[k].Bus == bus && !members.Contains(k)) members.Add(k);
                if (members.Count == before)
                    throw new InvalidDataException($"--groups file '{path}' line {line}: bus {bus} has no in-service unit (or is named twice).");
            }
            members.Sort();
            groups.Add(members);
        }
        if (groups.Count == 0) throw new InvalidDataException($"--groups file '{path}' names no group.");
        return groups;
    }

    /// <summary>The kept bus ids: a comma list ("1,2,3"), or the path of a file of ids separated by commas, spaces or
    /// new lines.</summary>
    internal static IReadOnlyList<int> ParseKeep(string keep)
    {
        string text = File.Exists(keep) ? File.ReadAllText(keep) : keep;
        var ids = new List<int>();
        foreach (string tok in text.Split(new[] { ',', ' ', '\t', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(tok, NumberStyles.Integer, Inv, out int id))
                throw new UsageException($"--keep expected bus ids but got '{tok}'");
            ids.Add(id);
        }
        if (ids.Count == 0) throw new UsageException("--keep names no bus");
        return ids;
    }
}
