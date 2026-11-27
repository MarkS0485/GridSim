using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.PowerFlow.Equivalents;

namespace GridSim.Core.Dynamics.Equivalents;

/// <summary>A machine's classical (GENCLS) data besides its inertia, on its own MBase: the transient reactance X'd (pu)
/// and the damping D (pu power per pu speed). The inertia is the case's own <see cref="Generator.H"/>.</summary>
public sealed record ClassicalMachineData(double XdpPu, double Damping = 0.0);

/// <summary>An aggregated classical machine on its own base <see cref="SMva"/>: inertia H (s), damping D and transient
/// reactance X'd (pu).</summary>
public sealed record AggregateMachine(double SMva, double InertiaH, double Damping, double XdpPu);

/// <summary>One aggregated group of a <see cref="DynamicEquivalent"/>.</summary>
/// <param name="Name">The equivalent machine's name ("EQ:1", ...).</param>
/// <param name="BusId">The bus the equivalent machine sits on: the common terminal bus when every member shares one,
/// else a new bus.</param>
/// <param name="GeneratorIndices">The members, indices into the full model's <c>Generators</c>, in the order given.</param>
/// <param name="TerminalBusIds">The members' terminal buses, in first-member order.</param>
/// <param name="Machine">The aggregated machine.</param>
/// <param name="VoltagePu">The equivalent bus voltage V_eq at the operating point (pu).</param>
/// <param name="Merged">True when the terminal buses were merged into a new bus through ideal transformers.</param>
public sealed record DynamicEquivalentGroup(string Name, int BusId, IReadOnlyList<int> GeneratorIndices, IReadOnlyList<int> TerminalBusIds,
    AggregateMachine Machine, Complex VoltagePu, bool Merged);

/// <summary>The result of <see cref="InertialAggregation.Build"/>: the reduced case, the classical data of each of its
/// in-service generators (aligned to <c>Model.Generators</c>), the groups, the <c>gridsim-dynamics/2</c> document with a
/// GENCLS template per machine, and notes.</summary>
public sealed record DynamicEquivalent(GridModel Model, IReadOnlyList<ClassicalMachineData> Machines,
    IReadOnlyList<DynamicEquivalentGroup> Groups, string DynamicsJson, IReadOnlyList<string> Notes)
{
    /// <summary>Each in-service generator's X'd on its own MBase, aligned to <c>Model.Generators</c> (the array the
    /// classical preparation takes).</summary>
    public double[] XdpMachineBase() => Machines.Select(m => m.XdpPu).ToArray();

    /// <summary>Each in-service generator's damping, aligned to <c>Model.Generators</c>.</summary>
    public double[] DampingByGenerator() => Machines.Select(m => m.Damping).ToArray();
}

/// <summary>
/// RC5 WP31: the dynamic equivalent of coherent machine groups by inertial aggregation (A. J. Germond and R. Podmore,
/// "Dynamic aggregation of generating unit models", IEEE Trans. PAS-97(4), 1978; the inertial method as summarised in
/// J. H. Chow, R. Galarza, P. Accari and W. W. Price, "Inertial and slow coherency aggregation algorithms for power system
/// dynamic model reduction", IEEE Trans. Power Systems 10(2), 1995).
///
/// <para><b>The network.</b> The machines of a group move to one equivalent bus q at the weighted voltage
/// <c>V_eq = sum S_i V_i / sum S_i</c> (S_i the machine rating), each terminal bus t joined to q by an ideal transformer of
/// complex ratio <c>a_t = V_t / V_eq</c>, which holds V_t = a_t V_q (Podmore's terminal-bus construction). An ideal
/// transformer has no impedance, so it is not written as a branch: it is absorbed into the network, exactly. Each terminal
/// bus t is replaced by q, and every branch at t takes the transformer into its own stamp (MATPOWER pi model with the
/// complex tap T at the from end):
/// <list type="bullet">
///   <item>t at the from end: T' = T / a_t (series impedance and charging unchanged);</item>
///   <item>t at the to end: T' = T a_t, with the series impedance divided and the charging multiplied by |a_t|^2;</item>
///   <item>both ends in the same group: the branch becomes a shunt at q,
///   <c>y = conj(a_f)(Y_ff a_f + Y_ft a_t) + conj(a_t)(Y_tf a_f + Y_tt a_t)</c>;</item>
///   <item>the bus loads at t move to q (constant power: an ideal transformer is lossless) and the bus shunts become
///   |a_t|^2 (Gs + jBs).</item>
/// </list>
/// The current at q is then the sum over t of conj(a_t) times the current at t, which is the power balance of the ideal
/// transformers, so the solved base case (with V_q = V_eq and every other voltage unchanged) solves the reduced case and the
/// power flow is preserved exactly (up to round-off). A group whose members all sit on one bus keeps that bus (a_t = 1),
/// and the network is not changed at all.</para>
///
/// <para><b>The machine.</b> A classical machine (GENCLS) at q, on the base S_eq = sum S_i:
/// <c>H_eq = sum H_i S_i / S_eq</c>, <c>D_eq = sum D_i S_i / S_eq</c>, and X'_eq the parallel combination of the members'
/// X'_i referred to q through their transformers, <c>X'_eq = S_eq / sum (|a_i|^2 S_i / X'_i)</c> on S_eq (with |a_i| = 1 it
/// is <c>(sum 1 / X'_i on the S_eq base)^-1</c>). Its output is the members' solved output, its voltage set-point |V_eq| and
/// its limits the members' sums. Exciters, governors and stabilisers are NOT aggregated: the equivalent is a classical
/// machine. When the members move together (perfect coherency, as identical machines at one bus do) the equivalent is
/// exact; otherwise it is an approximation whose quality follows the coherency.</para>
///
/// <para><b>Refused</b> (fail loud, by name): a non-converged base or one of another model, an empty group, a generator in
/// two groups or out of range, a member that is not a synchronous machine or has no inertia, a machine data list that does
/// not match the generators, a generator outside the group on a group's terminal bus, and, at a merged terminal bus, a ZIP
/// load, an HVDC, FACTS, SVC, induction machine, OLTC, flow control, boundary or zero-sequence mutual coupling.</para>
/// </summary>
public static class InertialAggregation
{
    /// <summary>
    /// The aggregated classical machine of members with ratings <paramref name="sMva"/> (MVA), inertia
    /// <paramref name="inertiaH"/> (s, own base), damping <paramref name="damping"/> and X'd <paramref name="xdpPu"/> (pu,
    /// own base), referred through ideal transformers of ratio magnitude <paramref name="ratioMagnitude"/> (null: 1).
    /// Sums run in the order given.
    /// </summary>
    public static AggregateMachine Aggregate(IReadOnlyList<double> sMva, IReadOnlyList<double> inertiaH, IReadOnlyList<double> damping,
        IReadOnlyList<double> xdpPu, IReadOnlyList<double>? ratioMagnitude = null)
    {
        ArgumentNullException.ThrowIfNull(sMva);
        ArgumentNullException.ThrowIfNull(inertiaH);
        ArgumentNullException.ThrowIfNull(damping);
        ArgumentNullException.ThrowIfNull(xdpPu);
        int n = sMva.Count;
        if (n == 0) throw new ArgumentException("a group needs at least one machine.", nameof(sMva));
        if (inertiaH.Count != n || damping.Count != n || xdpPu.Count != n || (ratioMagnitude is not null && ratioMagnitude.Count != n))
            throw new ArgumentException("one rating, inertia, damping, X'd (and ratio) per machine.", nameof(sMva));
        double sEq = 0, hs = 0, ds = 0, ys = 0;
        for (int i = 0; i < n; i++)
        {
            if (!(sMva[i] > 0) || !double.IsFinite(sMva[i])) throw new ArgumentOutOfRangeException(nameof(sMva), sMva[i], $"machine {i}: the rating must be a finite number above 0.");
            if (!(inertiaH[i] > 0) || !double.IsFinite(inertiaH[i])) throw new ArgumentOutOfRangeException(nameof(inertiaH), inertiaH[i], $"machine {i}: H must be a finite number above 0.");
            if (!double.IsFinite(damping[i])) throw new ArgumentOutOfRangeException(nameof(damping), damping[i], $"machine {i}: D must be finite.");
            if (!(xdpPu[i] > 0) || !double.IsFinite(xdpPu[i])) throw new ArgumentOutOfRangeException(nameof(xdpPu), xdpPu[i], $"machine {i}: X'd must be a finite number above 0.");
            sEq += sMva[i];
            hs += inertiaH[i] * sMva[i];
            ds += damping[i] * sMva[i];
            if (ratioMagnitude is null) ys += sMva[i] / xdpPu[i];
            else
            {
                double a = ratioMagnitude[i];
                if (!(a > 0) || !double.IsFinite(a)) throw new ArgumentOutOfRangeException(nameof(ratioMagnitude), a, $"machine {i}: the ratio must be a finite number above 0.");
                ys += a * a * sMva[i] / xdpPu[i];
            }
        }
        return new AggregateMachine(sEq, hs / sEq, ds / sEq, sEq / ys);
    }

    /// <summary>
    /// The dynamic equivalent of <paramref name="model"/> at the converged operating point <paramref name="solved"/> (a
    /// solve of this model): each group of <paramref name="groups"/> (indices into <c>model.Generators</c>) becomes one
    /// classical machine (see the class summary). <paramref name="machines"/> gives every in-service generator's classical
    /// data (aligned to <c>model.Generators</c>; units that are not machines are not read); <paramref name="provenance"/>
    /// is the tag the dynamics document gives the machine data. Refuses with <see cref="InvalidOperationException"/> naming
    /// the element.
    /// </summary>
    public static DynamicEquivalent Build(GridModel model, PowerFlowResult solved, IReadOnlyList<IReadOnlyList<int>> groups,
        IReadOnlyList<ClassicalMachineData> machines, ProvenanceTag provenance)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(solved);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(machines);

        // ---- The base case ----
        if (!solved.Converged || solved.Status != PowerFlowStatus.Converged)
            throw new InvalidOperationException($"the base case is not converged (status {solved.Status}); a dynamic equivalent needs a solved, converged base.");
        int n = model.BusCount;
        if (solved.BusIds.Count != n || solved.Vm.Length != n || solved.VaDeg.Length != n || solved.PInjMw.Length != n || solved.QInjMvar.Length != n)
            throw new InvalidOperationException($"the base case has {solved.BusIds.Count} buses but the model has {n}; give the solve of this model.");
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
        {
            if (solved.BusIds[i] != model.Buses[i].Id)
                throw new InvalidOperationException($"the base case's bus {i} is {solved.BusIds[i]} but the model's is {model.Buses[i].Id}; give the solve of this model.");
            index[model.Buses[i].Id] = i;
        }
        IReadOnlyList<Generator> gens = model.Generators;
        if (machines.Count != gens.Count)
            throw new InvalidOperationException($"the machine data has {machines.Count} entries but the model has {gens.Count} in-service generators; give one per generator.");
        double baseMva = model.BaseMva;

        // ---- The groups ----
        if (groups.Count == 0) throw new InvalidOperationException("no group to aggregate.");
        var groupOf = new int[gens.Count];
        Array.Fill(groupOf, -1);
        for (int gi = 0; gi < groups.Count; gi++)
        {
            IReadOnlyList<int> gr = groups[gi] ?? throw new InvalidOperationException($"group {gi + 1} is null.");
            if (gr.Count == 0) throw new InvalidOperationException($"group {gi + 1} is empty.");
            foreach (int k in gr)
            {
                if (k < 0 || k >= gens.Count) throw new InvalidOperationException($"group {gi + 1} names generator {k}; the model has {gens.Count} in-service generators.");
                if (groupOf[k] >= 0) throw new InvalidOperationException($"generator {k} (bus {gens[k].Bus}) is in groups {groupOf[k] + 1} and {gi + 1}.");
                Generator g = gens[k];
                string gname = g.Name ?? $"gen@bus {g.Bus}";
                if (g.PowerFlowOnly || g.IsConverterInterfaced)
                    throw new InvalidOperationException($"generator '{gname}' at bus {g.Bus} is not a synchronous machine (converter plant or a power-flow-only unit); it cannot be aggregated.");
                if (!(g.H > 0) || !double.IsFinite(g.H))
                    throw new InvalidOperationException($"generator '{gname}' at bus {g.Bus} has no inertia (H = {g.H.ToString("R", CultureInfo.InvariantCulture)}); a classical machine needs H > 0.");
                groupOf[k] = gi;
            }
        }
        for (int k = 0; k < gens.Count; k++)
        {
            Generator g = gens[k];
            if (g.PowerFlowOnly || g.IsConverterInterfaced) continue;
            ClassicalMachineData d = machines[k] ?? throw new InvalidOperationException($"the machine data of generator {k} is null.");
            if (!(d.XdpPu > 0) || !double.IsFinite(d.XdpPu) || !double.IsFinite(d.Damping))
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"generator {k} (bus {g.Bus}): X'd {d.XdpPu:R} must be a finite number above 0 and D {d.Damping:R} finite."));
        }

        // Terminal buses: every in-service unit at a group's terminal bus must be in that group.
        var terminalGroup = new int[n];
        Array.Fill(terminalGroup, -1);
        var terminals = new List<int>[groups.Count];   // model indices, first-member order
        for (int gi = 0; gi < groups.Count; gi++)
        {
            terminals[gi] = new List<int>();
            foreach (int k in groups[gi])
            {
                int t = index[gens[k].Bus];
                if (!terminals[gi].Contains(t)) terminals[gi].Add(t);
                terminalGroup[t] = gi;
            }
        }
        for (int k = 0; k < gens.Count; k++)
        {
            int t = index[gens[k].Bus];
            if (terminalGroup[t] >= 0 && groupOf[k] != terminalGroup[t])
                throw new InvalidOperationException(
                    $"generator {k} at bus {gens[k].Bus} is not in group {terminalGroup[t] + 1}, which holds machines at that bus; a terminal bus belongs to one group whole.");
        }
        var merged = new bool[groups.Count];
        for (int gi = 0; gi < groups.Count; gi++) merged[gi] = terminals[gi].Count > 1;
        var mergedBus = new bool[n];
        for (int gi = 0; gi < groups.Count; gi++) if (merged[gi]) foreach (int t in terminals[gi]) mergedBus[t] = true;
        RefuseAtMergedBuses(model, index, mergedBus);

        // ---- Each group: V_eq, the ratios, the aggregated machine ----
        var v = new Complex[n];
        for (int i = 0; i < n; i++) v[i] = DetMath.FromPolar(solved.Vm[i], solved.VaDeg[i] * Math.PI / 180.0);
        int maxId = 0;
        foreach (Bus b in model.Buses) maxId = Math.Max(maxId, b.Id);
        int nextId = maxId + 1;
        var ratio = new Complex[n];                    // a_t at each merged terminal bus
        var qBus = new int[groups.Count];              // the equivalent bus id
        var vEq = new Complex[groups.Count];
        var agg = new AggregateMachine[groups.Count];
        var notes = new List<string>();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            IReadOnlyList<int> gr = groups[gi];
            var s = new double[gr.Count];
            var h = new double[gr.Count];
            var d = new double[gr.Count];
            var x = new double[gr.Count];
            for (int q = 0; q < gr.Count; q++)
            {
                Generator g = gens[gr[q]];
                s[q] = MachineBase.EffectiveMva(g, baseMva);
                h[q] = g.H;
                d[q] = machines[gr[q]].Damping;
                x[q] = machines[gr[q]].XdpPu;
            }
            if (!merged[gi])
            {
                int t = terminals[gi][0];
                vEq[gi] = v[t];
                qBus[gi] = model.Buses[t].Id;
                agg[gi] = Aggregate(s, h, d, x);
                continue;
            }
            Complex num = Complex.Zero;
            double den = 0;
            for (int q = 0; q < gr.Count; q++) { num += s[q] * v[index[gens[gr[q]].Bus]]; den += s[q]; }
            vEq[gi] = num / den;
            var mag = new double[gr.Count];
            foreach (int t in terminals[gi]) ratio[t] = v[t] / vEq[gi];
            for (int q = 0; q < gr.Count; q++) mag[q] = ratio[index[gens[gr[q]].Bus]].Magnitude;
            qBus[gi] = nextId++;
            agg[gi] = Aggregate(s, h, d, x, mag);
        }

        // ---- The reduced case: buses ----
        var buses2 = new List<Bus>();
        var qShunt = new Complex[groups.Count];
        var qLoad = new Complex[groups.Count];
        bool[] qSlack = new bool[groups.Count];
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            if (!mergedBus[i]) { buses2.Add(b); continue; }
            int gi = terminalGroup[i];
            if (b.Zip is { VariesWithVoltage: true })
                throw new InvalidOperationException($"terminal bus {b.Id} of group {gi + 1} has a ZIP (voltage-dependent) load; merging it through an ideal transformer would change it.");
            double a2 = WardMath.Norm(ratio[i]);
            qShunt[gi] += a2 * new Complex(b.Gs, b.Bs);
            qLoad[gi] += new Complex(b.Pd, b.Qd);
            if (b.Type == BusType.Slack) qSlack[gi] = true;
        }

        // ---- Branches: the ideal transformers absorbed into each branch at a merged terminal bus ----
        var branches2 = new List<Branch>();
        int intraGroup = 0;
        foreach (Branch br in model.Branches)
        {
            int f = index[br.From], t = index[br.To];
            bool mf = mergedBus[f], mt = mergedBus[t];
            if (!mf && !mt) { branches2.Add(br); continue; }
            if (mf && mt && terminalGroup[f] == terminalGroup[t])
            {
                intraGroup++;
                if (!br.InService) continue;
                BranchStamp st = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
                Complex af = ratio[f], at = ratio[t];
                Complex y = Complex.Conjugate(af) * (st.Yff * af + st.Yft * at) + Complex.Conjugate(at) * (st.Ytf * af + st.Ytt * at);
                qShunt[terminalGroup[f]] += y * baseMva;
                continue;
            }
            double tap = br.TapRatio == 0 ? 1.0 : br.TapRatio;
            Complex T = DetMath.FromPolar(tap, br.PhaseShiftDeg * Math.PI / 180.0);
            double r = br.R, xx = br.X, bc = br.B;
            int from = br.From, to = br.To;
            if (mf) { T /= ratio[f]; from = qBus[terminalGroup[f]]; }
            if (mt)
            {
                Complex at = ratio[t];
                double a2 = WardMath.Norm(at);
                T *= at;
                r /= a2; xx /= a2; bc *= a2;
                to = qBus[terminalGroup[t]];
            }
            branches2.Add(br with
            {
                From = from, To = to, R = r, X = xx, B = bc,
                TapRatio = T.Magnitude, PhaseShiftDeg = DetMath.Phase(T) * 180.0 / Math.PI,
            });
        }

        // ---- The equivalent buses (after the model's buses, in group order) ----
        for (int gi = 0; gi < groups.Count; gi++)
        {
            if (!merged[gi]) continue;
            Bus first = model.Buses[terminals[gi][0]];
            Complex sh = qShunt[gi];
            buses2.Add(new Bus(qBus[gi], qSlack[gi] ? BusType.Slack : BusType.Pv, Pd: qLoad[gi].Real, Qd: qLoad[gi].Imaginary,
                Gs: sh.Real, Bs: sh.Imaginary, BaseKv: first.BaseKv, Vm: vEq[gi].Magnitude, VaDeg: DetMath.Phase(vEq[gi]) * 180.0 / Math.PI,
                Name: "EQ:" + (gi + 1).ToString(CultureInfo.InvariantCulture)));
        }

        // ---- Generators: each group's equivalent at the place of its first member ----
        var gens2 = new List<Generator>();
        var data2 = new List<ClassicalMachineData>();
        var emitted = new bool[groups.Count];
        int inService = -1, droppedOos = 0;
        foreach (Generator g in model.AllGenerators)
        {
            if (!g.InService)
            {
                if (mergedBus[index[g.Bus]]) { droppedOos++; continue; }
                gens2.Add(g);
                continue;
            }
            inService++;
            int gi = groupOf[inService];
            if (gi < 0) { gens2.Add(g); data2.Add(machines[inService]); continue; }
            if (emitted[gi]) continue;
            emitted[gi] = true;
            gens2.Add(EquivalentGenerator(groups[gi], gi, gens, index, solved, model, qBus[gi], vEq[gi], merged[gi], agg[gi]));
            data2.Add(new ClassicalMachineData(agg[gi].XdpPu, agg[gi].Damping));
        }

        var reduced = new GridModel
        {
            BaseMva = model.BaseMva,
            Name = model.Name + "_dynamic",
            FrequencyHz = model.FrequencyHz,
            Buses = buses2,
            Generators = gens2,
            Branches = branches2,
            Oltcs = model.Oltcs,
            Boundaries = model.Boundaries,
            LccLinks = model.LccLinks,
            VscLinks = model.VscLinks,
            MtdcLinks = model.MtdcLinks,
            FactsDevices = model.FactsDevices,
            StaticVarCompensators = model.StaticVarCompensators,
            InductionMachines = model.InductionMachines,
            TransformerFlowControls = model.TransformerFlowControls,
            ZeroSequenceMutuals = model.ZeroSequenceMutuals,
        };

        var report = new List<DynamicEquivalentGroup>(groups.Count);
        for (int gi = 0; gi < groups.Count; gi++)
            report.Add(new DynamicEquivalentGroup("EQ:" + (gi + 1).ToString(CultureInfo.InvariantCulture), qBus[gi], groups[gi].ToList(),
                terminals[gi].Select(t => model.Buses[t].Id).ToList(), agg[gi], vEq[gi], merged[gi]));

        int mergedCount = merged.Count(x => x);
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"{groups.Count} group(s) aggregated into classical machines; {mergedCount} merged their terminal buses through ideal transformers into new buses."));
        if (intraGroup > 0) notes.Add(string.Create(CultureInfo.InvariantCulture, $"{intraGroup} branch(es) between terminal buses of one group became shunts at the equivalent bus."));
        if (droppedOos > 0) notes.Add(string.Create(CultureInfo.InvariantCulture, $"{droppedOos} out-of-service unit(s) at merged terminal buses left out."));
        notes.Add("exciters, governors and stabilisers are not aggregated: each equivalent is a classical machine (GENCLS).");
        if (model.AssetDynamics is not null || model.Protections is not null || model.UnitProtection is not null)
            notes.Add("authored asset dynamics, protections and unit protection are not carried to the reduced case.");

        string json = DynamicsDocument(reduced, data2, provenance);
        return new DynamicEquivalent(reduced, data2, report, json, notes);
    }

    /// <summary>A group's equivalent generator: the members' solved output (merged) or scheduled output (one bus), the
    /// summed limits, |V_eq| as the set-point, the aggregate rating and inertia.</summary>
    private static Generator EquivalentGenerator(IReadOnlyList<int> group, int gi, IReadOnlyList<Generator> gens, Dictionary<int, int> index,
        PowerFlowResult solved, GridModel model, int busId, Complex vEq, bool merged, AggregateMachine agg)
    {
        double pg = 0, qg = 0, qmin = 0, qmax = 0, pmin = 0, pmax = 0;
        foreach (int k in group)
        {
            Generator g = gens[k];
            pg += g.Pg; qg += g.Qg; qmin += g.Qmin; qmax += g.Qmax; pmin += g.Pmin; pmax += g.Pmax;
        }
        double vg = gens[group[0]].Vg;
        if (merged)
        {
            // The members' solved output: each terminal bus's injection plus its load (every unit there is a member).
            pg = 0; qg = 0;
            var seen = new HashSet<int>();
            foreach (int k in group)
            {
                int t = index[gens[k].Bus];
                if (!seen.Add(t)) continue;
                Bus b = model.Buses[t];
                pg += solved.PInjMw[t] + b.Pd;
                qg += solved.QInjMvar[t] + b.Qd;
            }
            vg = vEq.Magnitude;
        }
        return new Generator(busId, Pg: pg, Qg: qg, Vg: vg, Qmin: qmin, Qmax: qmax, Pmin: pmin, Pmax: pmax, MBase: agg.SMva, H: agg.InertiaH,
            Name: "EQ:" + (gi + 1).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The <c>gridsim-dynamics/2</c> document of the reduced case: one GENCLS machine template per in-service
    /// synchronous machine (H from the case, D and X'd from <paramref name="data"/>), bound by bus (and unit index when the
    /// bus has more than one unit). LF line ends, so the same case gives the same bytes on every OS.</summary>
    private static string DynamicsDocument(GridModel reduced, List<ClassicalMachineData> data, ProvenanceTag provenance)
    {
        IReadOnlyList<Generator> gens = reduced.Generators;
        IReadOnlyList<Generator> all = reduced.AllGenerators;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "gridsim-dynamics/2");
            w.WriteString("name", "Dynamic equivalent " + reduced.Name);
            w.WriteString("description", "Classical (GENCLS) machine data of the dynamic equivalent " + reduced.Name
                + ": coherent groups aggregated by the inertial method (Germond and Podmore 1978); exciters, governors and stabilisers are not aggregated.");
            w.WriteStartArray("templates");
            var bindings = new List<(int Bus, int Unit, bool WithUnit, string Ref)>();
            int k = -1;
            for (int j = 0; j < all.Count; j++)
            {
                Generator g = all[j];
                if (!g.InService) continue;
                k++;
                if (g.PowerFlowOnly || g.IsConverterInterfaced) continue;
                if (!(g.H > 0) || !double.IsFinite(g.H))
                    throw new InvalidOperationException($"generator '{g.Name ?? $"gen@bus {g.Bus}"}' at bus {g.Bus} has no inertia (H = {g.H.ToString("R", CultureInfo.InvariantCulture)}); its GENCLS data needs H > 0.");
                int unit = 0, count = 0;
                for (int u = 0; u < all.Count; u++)
                {
                    if (all[u].Bus != g.Bus) continue;
                    if (u < j) unit++;
                    count++;
                }
                string bus = g.Bus.ToString(CultureInfo.InvariantCulture);
                string id = "gridsim.equivalent.b" + bus + ".u" + unit.ToString(CultureInfo.InvariantCulture) + ".machine";
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteNumber("rev", 1);
                w.WriteString("kind", "synchronousMachine");
                w.WriteString("status", "active");
                w.WriteString("title", $"GENCLS, {g.Name ?? "unit"} at bus {bus}");
                w.WriteStartObject("params");
                w.WriteString("model", "GENCLS");
                w.WriteNumber("inertiaH", g.H);
                w.WriteNumber("damping", data[k].Damping);
                w.WriteNumber("xdpPu", data[k].XdpPu);
                w.WriteEndObject();
                w.WriteStartObject("provenance");
                w.WriteStartObject("_default");
                w.WriteString("tag", provenance.ToString());
                w.WriteString("note", g.Name is not null && g.Name.StartsWith("EQ:", StringComparison.Ordinal)
                    ? "Inertial aggregation of a coherent group (Germond and Podmore 1978): H, D and X'd on the aggregate base."
                    : "The machine's own classical data, carried into the dynamic equivalent.");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
                bindings.Add((g.Bus, unit, count > 1, id + "@1"));
            }
            w.WriteEndArray();
            w.WriteStartArray("machines");
            foreach ((int bus, int unit, bool withUnit, string tref) in bindings)
            {
                w.WriteStartObject();
                w.WriteNumber("bus", bus);
                if (withUnit) w.WriteNumber("unit", unit);
                w.WriteString("template", tref);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void RefuseAtMergedBuses(GridModel model, Dictionary<int, int> index, bool[] mergedBus)
    {
        bool At(int bus) => index.TryGetValue(bus, out int i) && mergedBus[i];
        var problems = new List<string>();
        foreach (LccDcLink l in model.LccLinks)
            if (At(l.Rectifier.Bus) || At(l.Inverter.Bus)) problems.Add($"LCC HVDC link '{l.Name}'");
        foreach (VscDcLink l in model.VscLinks)
            if (At(l.First.Bus) || At(l.Second.Bus)) problems.Add($"VSC HVDC link '{l.Name}'");
        foreach (MtdcLink l in model.MtdcLinks)
            if (l.Converters.Any(c => At(c.Bus)) || l.DcBuses.Any(d => At(d.AcBus))) problems.Add($"multi-terminal DC link '{l.Name}'");
        foreach (FactsDevice f in model.FactsDevices)
            if (At(f.SendingBus) || At(f.TerminalBus)) problems.Add($"FACTS device '{f.Name}'");
        foreach (StaticVarCompensator s in model.StaticVarCompensators)
            if (At(s.Bus)) problems.Add($"SVC '{s.Name}' at bus {s.Bus}");
        foreach (InductionMachine im in model.InductionMachines)
            if (At(im.Bus)) problems.Add($"induction machine '{im.Name}' at bus {im.Bus}");
        bool BranchAt(string? name)
        {
            foreach (Branch br in model.Branches)
                if (br.Name is not null && br.Name == name) return At(br.From) || At(br.To);
            return false;
        }
        foreach (Oltc o in model.Oltcs)
            if (BranchAt(o.Transformer) || At(o.ControlsBus)) problems.Add($"OLTC on '{o.Transformer}'");
        foreach (TransformerFlowControl c in model.TransformerFlowControls)
            if (BranchAt(c.Transformer) || At(c.FromBus)) problems.Add($"flow control on '{c.Transformer}'");
        foreach (Boundary b in model.Boundaries)
            if (b.Branches.Any(x => BranchAt(x.Branch))) problems.Add($"boundary '{b.Name}'");
        foreach (ZeroSequenceMutual zm in model.ZeroSequenceMutuals)
            if (At(zm.FromA) || At(zm.ToA) || At(zm.FromB) || At(zm.ToB)) problems.Add($"the zero-sequence mutual coupling of lines {zm.FromA}-{zm.ToA} and {zm.FromB}-{zm.ToB}");
        if (problems.Count > 0)
            throw new InvalidOperationException("elements touch a terminal bus that the dynamic equivalent merges through ideal transformers: " + string.Join("; ", problems) + ".");
    }
}
