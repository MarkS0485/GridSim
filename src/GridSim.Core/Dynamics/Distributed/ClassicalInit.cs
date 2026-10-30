using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Models;   // CctBracket, shared with the RMS CCT (N042)
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// Builds the reduced classical-model inputs (<see cref="ClassicalMultiMachine"/>) from a solved power
/// flow. Follows the standard classical transient-stability preparation:
///   1. loads -> constant shunt admittances <c>y = conj(S)/|V|^2</c> folded onto the bus diagonal;
///   2. each generator -> an internal node behind its transient reactance X'd (added as a reactance
///      branch to its terminal bus);
///   3. Kron-reduce the augmented network to the generator internal nodes -> the reduced admittance Y;
///   4. internal EMF <c>E' = V_t + jX'd*I_t</c> gives |E'| and the initial rotor angle delta0;
///   5. mechanical power set to the electrical output at delta0, so an undisturbed run is stationary.
/// Everything is placed on the system base: X'd, H convert from the machine base by the MBase/BaseMva
/// ratio. This is deliberately kept separate from the kernel so the kernel stays validatable against
/// closed-form references without a solve.
/// </summary>
public static class ClassicalInit
{
    public sealed record Prepared(
        double[] H, double[] Pm, double[] Eq, double[] Delta0, Complex[,] YReduced,
        IReadOnlyList<int> GeneratorBuses,
        Complex[,]? YReducedFaulted = null,
        IReadOnlyList<int>? MachineIndices = null)
    {
        /// <summary>Each machine's index in <c>model.Generators</c> (identity unless non-machines were held).</summary>
        public IReadOnlyList<int> MachineIndex => MachineIndices ?? Enumerable.Range(0, H.Length).ToArray();
    }

    /// <summary>
    /// True when <paramref name="gen"/> is not an integrable machine for the classical swing: converter-interfaced plant
    /// (no rotor), a power-flow-only device (a slack balancer, a voltage-control unit) or a unit with no inertia
    /// (H &lt;= 0, or NaN). With <c>holdNonMachines: true</c> such a unit is held as a constant admittance at its bus.
    ///
    /// <para>D-DOC-133 (docs-session finding, Ruling R1: Mark delegated 2026-10-04): the one classification, used by
    /// <see cref="FromSolvedCase"/>, <see cref="CertainNoRotorFailure"/> and the CLI's stability screen header (which
    /// used to print every generator as a machine, held units included). The expression is exactly the one both loops
    /// here wrote out before, so the split of every case is unchanged.</para>
    /// </summary>
    public static bool IsNonMachine(Generator gen)
    {
        ArgumentNullException.ThrowIfNull(gen);
        return gen.PowerFlowOnly || gen.IsConverterInterfaced || !(gen.H > 0.0);
    }

    /// <summary>The message <see cref="FromSolvedCase"/> fails with when no generator is integrable as a machine.</summary>
    internal const string NoMachinesMessage =
        "No synchronous machines with inertia: the classical stability model has no rotor to integrate.";

    /// <summary>The message <see cref="FromSolvedCase"/> fails with for a machine with no inertia constant.</summary>
    internal static string NoInertiaMessage(Generator gen) =>
        $"Generator at bus {gen.Bus} has no inertia constant (H = {gen.H}); the classical stability " +
        "model needs H > 0 for every machine. This case carries no per-machine inertia data " +
        "(e.g. a MATPOWER case whose loader defaults H = 0) and cannot be screened.";

    /// <summary>
    /// The <see cref="InvalidOperationException"/> message that <see cref="FromSolvedCase"/> is CERTAIN to fail with
    /// for lack of a rotor - no integrable machine at all, or a machine with no inertia constant - or null when it is
    /// not certain (R018 / N021, October 2026). Reads the model and result only; builds no matrix.
    ///
    /// <para>"Certain" is literal. The main path reaches those two throws only after steps that could throw first
    /// on a malformed input: reading the result's per-bus arrays, building the YBus (which looks up both ends of
    /// every in-service branch) and, for the no-inertia throw, looking up the terminal bus of every machine ahead of
    /// the offending one. This returns a message only when every one of those steps provably succeeds, so a caller
    /// that would only catch the exception can skip the call and lose nothing (and the message is exactly the one the
    /// call would throw); for any input on which the main path would fail differently it returns null. The converged
    /// and generator-count checks are not repeated here: <see cref="FromSolvedCase"/> makes them first, and
    /// <see cref="GridSim.Core.Physics.Experimental.ContinuumWaveModel.CanForm"/> checks them before calling this.</para>
    /// </summary>
    internal static string? CertainNoRotorFailure(GridModel model, PowerFlowResult result, bool holdNonMachines)
    {
        // The same machine classification as FromSolvedCase: the first machine with no inertia, and the machine count.
        int machines = 0;
        int firstNoInertia = -1;
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator x = model.Generators[k];
            if (holdNonMachines && IsNonMachine(x)) continue;   // held as an admittance, not a machine
            machines++;
            if (firstNoInertia < 0 && !(x.H > 0.0)) firstNoInertia = k;
        }
        if (machines > 0 && firstNoInertia < 0) return null;   // every machine has inertia: no rotor failure ahead

        // Steps the main path takes before either throw. The per-bus result arrays are read for every BusIds entry.
        int nb = result.BusIds.Count;
        if (result.Vm is null || result.VaDeg is null || result.PInjMw is null || result.QInjMvar is null) return null;
        if (result.Vm.Length < nb || result.VaDeg.Length < nb || result.PInjMw.Length < nb || result.QInjMvar.Length < nb) return null;

        // YBus.Build resolves both ends of every in-service branch to a bus.
        var busIds = new HashSet<int>();
        foreach (Bus b in model.Buses) busIds.Add(b.Id);
        foreach (Branch br in model.Branches)
            if (br.InService && (!busIds.Contains(br.From) || !busIds.Contains(br.To))) return null;

        if (machines == 0) return NoMachinesMessage;

        // The machine loop looks up each earlier machine's terminal bus before it reaches the one with no inertia.
        // (Only reached with holdNonMachines false: holding sends every H <= 0 unit to the held list.)
        for (int k = 0; k < firstNoInertia; k++)
            if (!busIds.Contains(model.Generators[k].Bus)) return null;
        return NoInertiaMessage(model.Generators[firstNoInertia]);
    }

    /// <summary>
    /// Prepare from a solved <paramref name="result"/>. <paramref name="xdpMachineBase"/> is each
    /// generator's transient reactance on its own MBase (aligned to <c>model.Generators</c>); when null a
    /// conservative 0.30 pu default (<see cref="DefaultTransientReactancePu"/>) is used and flagged by the caller. Damping is left to the kernel
    /// (defaults to zero) - the classical model carries none intrinsically.
    /// </summary>
    /// <param name="holdNonMachines">When true, generators that are not integrable machines - converter-interfaced
    /// plant (no rotor), power-flow-only devices and units with no inertia (H &lt;= 0) - are held as constant
    /// admittances at their bus instead of failing the study (the same treatment as the RMS engine), and
    /// <see cref="Prepared.MachineIndices"/> maps the machines back to <c>model.Generators</c>. Default false keeps
    /// the historical all-generators-are-machines behaviour (and its H &gt; 0 check) exactly.</param>
    public static Prepared FromSolvedCase(GridModel model, PowerFlowResult result, double[]? xdpMachineBase = null,
        int? faultBusId = null, bool holdNonMachines = false)
    {
        // R007 (stream P, October 2026): the work that does not depend on the fault bus (Augment: the augmented network,
        // the EMFs and the unfaulted reduction) is split from the faulted reduction, so a screen over many fault buses
        // (FaultScreen) does it once. For one call the steps run in the order they always did; the only reordering is
        // that the mechanical powers (a pure computation that cannot throw) are now formed before the faulted reduction
        // instead of after it, which changes no value and no exception.
        Augmented a = Augment(model, result, xdpMachineBase, holdNonMachines);
        return a.ToPrepared(faultBusId is int fb ? a.FaultedReduction(fb) : null, copyArrays: false);
    }

    /// <summary>
    /// R007 (stream P, October 2026): everything <see cref="FromSolvedCase"/> computes that does not depend on the fault
    /// bus. The body is the historical FromSolvedCase body, step for step, with the network held as its nonzero cells
    /// instead of the dense (n + g) x (n + g) array (see the comments at each step for why every value is the same).
    /// </summary>
    private static Augmented Augment(GridModel model, PowerFlowResult result, double[]? xdpMachineBase, bool holdNonMachines)
    {
        if (!result.Converged) throw new InvalidOperationException("Classical init needs a converged power flow.");
        int g = model.Generators.Count;
        if (g == 0) throw new InvalidOperationException("No generators to initialise.");
        if (xdpMachineBase is not null && xdpMachineBase.Length != g)
            throw new ArgumentException("xdpMachineBase must align to model.Generators.", nameof(xdpMachineBase));

        // R018 / N021 / N153 (October 2026): the two "no rotor to integrate" failures below (no machine at all when
        // holding non-machines, or a machine with no inertia constant) used to surface only AFTER the dense YBus and the
        // dense (n + g)^2 augmented admittance had been built and filled (1,456,645,640 B per call on gb-full), only to
        // be thrown away. Ruling R1 (Mark delegated 2026-10-04, N153): fail BEFORE the dense build. CertainNoRotorFailure
        // returns a message only when every step the main path takes before those throws provably succeeds, and the
        // message is exactly the one the main path would throw, so the outcome of every call is unchanged (same exception
        // type, same text); only the wasted allocation goes. This also spares the direct callers (the CLI `experimental`
        // verb, small-signal, the CCT search), which paid the ~1.46 GB build on gb-full before failing.
        if (CertainNoRotorFailure(model, result, holdNonMachines) is string noRotor)
            throw new InvalidOperationException(noRotor);

        double baseMva = model.BaseMva;
        // R007: the admittance cells without the dense n x n array; each cell is bit for bit the dense build's entry (same
        // stamps in the same order), a position without a cell is the dense array's (+0, +0), and a branch on a bus the
        // model does not hold fails here exactly as the dense build failed (the same lookup in the same order).
        SparseYBus ybus = YBus.BuildSparse(model);
        int n = model.BusCount;

        // Bus voltage (pu, complex) and SOLVED net injection (generation - demand, MW/MVAr) by bus id.
        var vByBus = new Dictionary<int, Complex>(result.BusIds.Count);
        var pInjByBus = new Dictionary<int, double>(result.BusIds.Count);
        var qInjByBus = new Dictionary<int, double>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count; i++)
        {
            vByBus[result.BusIds[i]] = DetMath.FromPolar(result.Vm[i], result.VaDeg[i] * Math.PI / 180.0);
            pInjByBus[result.BusIds[i]] = result.PInjMw[i];
            qInjByBus[result.BusIds[i]] = result.QInjMvar[i];
        }

        // Scheduled generation per bus, to split the SOLVED bus output across co-located machines by their
        // scheduled share (equal split when the bus's scheduled generation is ~0). Canonical validation
        // cases are one-machine-per-bus, so the share is 1.0 and each machine gets its exact solved output.
        var schedPByBus = new Dictionary<int, double>();
        var genCountByBus = new Dictionary<int, int>();
        var loadByBus = new Dictionary<int, (double Pd, double Qd)>();
        foreach (Bus bb in model.Buses) loadByBus[bb.Id] = (bb.Pd, bb.Qd);
        foreach (Generator gg in model.Generators)
        {
            schedPByBus[gg.Bus] = schedPByBus.GetValueOrDefault(gg.Bus) + gg.Pg;
            genCountByBus[gg.Bus] = genCountByBus.GetValueOrDefault(gg.Bus) + 1;
        }

        // Which generators are machines; the rest (when holding) become constant admittances below.
        var machineIdx = new List<int>(g);
        var heldIdx = new List<int>();
        for (int k = 0; k < g; k++)
        {
            Generator x = model.Generators[k];
            if (holdNonMachines && IsNonMachine(x)) heldIdx.Add(k); else machineIdx.Add(k);
        }
        if (machineIdx.Count == 0)
            throw new InvalidOperationException(NoMachinesMessage);
        int gm = machineIdx.Count;

        // Augmented admittance: n network buses then one internal node per machine.
        // R007 (stream P, October 2026): held as its nonzero cells instead of a dense m x m array (on gb-full m = 4,491:
        // 320 MB per copy, with up to five copies alive per call). The dense array started as a copy of the dense YBus,
        // whose entries are the cells (positions without a cell (+0, +0)); every stamp below was "ya[r, c] += v" (or -=),
        // which on a position without a cell computed (+0, +0) + v (or - v). The stamps do exactly that: cur + v on an
        // existing cell, Complex.Zero + v on a new one (Complex.Zero is (+0, +0), the default the dense array held), in the
        // same order, so every cell is bit for bit the dense entry and every other position is still (+0, +0).
        int m = n + gm;
        var ya = new Dictionary<(int Row, int Col), Complex>(ybus.Cells);
        void StampAdd(int r, int c, Complex v) => ya[(r, c)] = ya.TryGetValue((r, c), out Complex cur) ? cur + v : Complex.Zero + v;
        void StampSubtract(int r, int c, Complex v) => ya[(r, c)] = ya.TryGetValue((r, c), out Complex cur) ? cur - v : Complex.Zero - v;

        // Loads -> constant shunt admittance on the bus diagonal.
        for (int i = 0; i < n; i++)
        {
            Bus bus = model.Buses[i];
            if (bus.Pd == 0 && bus.Qd == 0) continue;
            Complex v = vByBus.TryGetValue(bus.Id, out Complex vv) ? vv : Complex.One;
            double v2 = Math.Max(MinVoltageSquaredPu, v.Magnitude * v.Magnitude);
            Complex sLoadPu = new Complex(bus.Pd, bus.Qd) / baseMva;      // consumed
            StampAdd(i, i, Complex.Conjugate(sLoadPu) / v2);               // y = conj(S)/|V|^2 (was ya[i, i] += ...)
        }

        var h = new double[gm];
        var eq = new double[gm];
        var delta0 = new double[gm];
        var genBuses = new int[gm];
        var keep = new int[gm];

        // A unit's share of its bus's SOLVED generation. Active power splits across co-located units by scheduled share.
        // GR-083: reactive power splits by reactive capability (Qmax - Qmin, else MBase), the rule SubtransientInit uses,
        // so a 0 MW voltage-control unit beside a generating one carries its part of the bus's MVAr instead of none. A
        // lone unit takes the whole bus output either way (count 1: qShare = share), so one-machine-per-bus cases are bit
        // for bit unchanged.
        static double QWeight(Generator x)
        {
            double range = x.Qmax - x.Qmin;
            return double.IsFinite(range) && range > 0 ? range : Math.Max(x.MBase, SubtransientInit.MinMBaseWeightMva);
        }
        var qWeightByBus = new Dictionary<int, double>();
        foreach (Generator gg in model.Generators) qWeightByBus[gg.Bus] = qWeightByBus.GetValueOrDefault(gg.Bus) + QWeight(gg);
        Complex SolvedShare(Generator gen)
        {
            (double pdBus, double qdBus) = loadByBus.GetValueOrDefault(gen.Bus);
            double busGenP = pInjByBus.GetValueOrDefault(gen.Bus) + pdBus;
            double busGenQ = qInjByBus.GetValueOrDefault(gen.Bus) + qdBus;
            double schedP = schedPByBus.GetValueOrDefault(gen.Bus);
            int count = genCountByBus.GetValueOrDefault(gen.Bus, 1);
            double share = Math.Abs(schedP) > MinScheduledShareMw ? gen.Pg / schedP : 1.0 / Math.Max(1, count);
            double qShare = count <= 1 ? share : QWeight(gen) / qWeightByBus[gen.Bus];
            return new Complex(busGenP * share, busGenQ * qShare) / baseMva;
        }

        // Held units: their solved injection as a constant (negative-load) admittance at their bus.
        foreach (int kh in heldIdx)
        {
            Generator gen = model.Generators[kh];
            int bi = ybus.IndexOf(gen.Bus);
            Complex vt = vByBus.TryGetValue(gen.Bus, out Complex vh) ? vh : Complex.One;
            double v2 = Math.Max(MinVoltageSquaredPu, vt.Magnitude * vt.Magnitude);
            StampSubtract(bi, bi, Complex.Conjugate(SolvedShare(gen)) / v2);   // was ya[bi, bi] -= ...
        }

        for (int k = 0; k < gm; k++)
        {
            Generator gen = model.Generators[machineIdx[k]];

            // The classical swing divides by 2*H; a zero/negative/non-finite inertia constant makes every
            // step 0/0 -> NaN, which the synchronism test can no longer distinguish from stability (see the
            // ClassicalMultiMachine guard). Reject it here - fail loud rather than emit confidently-wrong
            // clearing times. MATPOWER .m cases carry no H (the loader defaults H=0), so they legitimately
            // cannot be screened without dynamic data.
            if (!(gen.H > 0.0))
                throw new InvalidOperationException(NoInertiaMessage(gen));

            genBuses[k] = gen.Bus;
            int ti = ybus.IndexOf(gen.Bus);
            int intNode = n + k;
            keep[k] = intNode;

            // D-DOC-055 (docs-session finding, Ruling R1: Mark delegated 2026-10-04): the machine base comes from the
            // one shared rule, MachineBase.EffectiveMva - a positive MBase is the base, a zero or negative MBase means
            // "not given" and the system base is used (as SubtransientInit and the MATPOWER parser already did), a NaN
            // or infinite MBase is refused. Before, this line floored the divisor at 1e-6 MVA (a zero MBase made X'd on
            // the system base 1e8 times the machine value) and the H line below scaled by the raw MBase (a zero MBase
            // gave H = 0 on the system base, which the swing divides by). For every positive MBase of at least 1e-6 MVA
            // (every machine any bundled case reaches here) both values are bit for bit what they were.
            double mBase = Models.MachineBase.EffectiveMva(gen, baseMva);
            double xdpMachine = xdpMachineBase?[machineIdx[k]] ?? DefaultTransientReactancePu;
            // GR-542: X'd is a finite reactance above 0; zero gave a 1/(j0) admittance and a negative value an
            // anti-physical machine, and the NaN Kron reduction then read as "unstable at zero" (CCT = 0).
            if (!(xdpMachine > 0) || !double.IsFinite(xdpMachine))
                throw new ArgumentOutOfRangeException(nameof(xdpMachineBase), xdpMachine,
                    $"Machine {k} (bus {gen.Bus}) has X'd {xdpMachine} pu; a transient reactance must be a finite number above 0.");
            double xdpSys = xdpMachine * baseMva / mBase;
            Complex yGen = Complex.One / new Complex(0.0, xdpSys);        // 1/(jX'd) = -j/X'd

            StampAdd(ti, ti, yGen);                 // was ya[ti, ti] += yGen
            StampAdd(intNode, intNode, yGen);       // was ya[intNode, intNode] += yGen
            StampSubtract(ti, intNode, yGen);       // was ya[ti, intNode] -= yGen
            StampSubtract(intNode, ti, yGen);       // was ya[intNode, ti] -= yGen

            // Internal EMF from the terminal condition: E' = V_t + jX'd*I_t, I_t = conj(S_gen/V_t).
            // S_gen is the SOLVED generator output - the slack picks up losses and PV-bus Qg is set by the
            // solve, so the scheduled Pg/Qg would build the EMF at a shifted dispatch. Solved bus generation
            // = net injection + local load; split across co-located machines by scheduled share.
            Complex vt = vByBus.TryGetValue(gen.Bus, out Complex vg) ? vg : Complex.One;
            Complex sGenPu = SolvedShare(gen);
            Complex it = Complex.Conjugate(sGenPu / vt);
            Complex ePrime = vt + new Complex(0.0, xdpSys) * it;
            eq[k] = ePrime.Magnitude;
            delta0[k] = DetMath.Phase(ePrime);

            h[k] = gen.H * mBase / baseMva;                              // H on the system base (same base rule as X'd)
        }

        // R007: the sparse reduction, bit for bit KronReduction.Reduce on the dense array of the same cells (the proof is on
        // SparseKronReduction), with the same exception and message when a pivot is near-singular.
        Complex[,] yRed = SparseKronReduction.Reduce(m, ya, keep);

        // Mechanical power = electrical output at delta0, so an undisturbed case is a fixed point.
        var pm = new double[gm];
        for (int i = 0; i < gm; i++)
        {
            double pe = eq[i] * eq[i] * yRed[i, i].Real;
            for (int j = 0; j < gm; j++)
            {
                if (j == i) continue;
                double dij = delta0[i] - delta0[j];
                (double sinD, double cosD) = DetMath.SinCos(dij);
                pe += eq[i] * eq[j] * (yRed[i, j].Real * cosD + yRed[i, j].Imaginary * sinD);
            }
            pm[i] = pe;
        }

        return new Augmented(ybus, ya, m, keep, h, pm, eq, delta0, yRed, genBuses, holdNonMachines ? machineIdx : null);
    }

    /// <summary>
    /// R007 (stream P, October 2026): the fault-independent part of a classical preparation (see <see cref="Augment"/>):
    /// the augmented network's cells, the kept internal nodes, the machine quantities and the unfaulted reduction.
    /// </summary>
    private sealed class Augmented(
        SparseYBus ybus, Dictionary<(int Row, int Col), Complex> cells, int size, int[] keep,
        double[] h, double[] pm, double[] eq, double[] delta0, Complex[,] yRed, int[] genBuses, List<int>? machineIdx)
    {
        /// <summary>
        /// The reduced network during a bolted three-phase fault at <paramref name="faultBusId"/>: the faulted bus clamped
        /// with a large shunt admittance before reducing, so the reduced network during the fault has a collapsed transfer
        /// to that bus (Pe ~ 0 for machines close to it). Cleared by reverting to the unfaulted reduction. As before R007,
        /// on a copy of the augmented network with <see cref="BoltedFaultAdmittancePu"/> added to the bus's self admittance
        /// (it was "yaF[fi, fi] += ..." on a dense copy: cur + v, or (+0, +0) + v where the dense entry was the default),
        /// then the same reduction. An unknown bus fails here, at the same lookup as before.
        /// </summary>
        public Complex[,] FaultedReduction(int faultBusId)
        {
            int fi = ybus.IndexOf(faultBusId);
            var yaF = new Dictionary<(int Row, int Col), Complex>(cells);
            var shunt = new Complex(BoltedFaultAdmittancePu, 0.0);   // near short-circuit to ground
            yaF[(fi, fi)] = yaF.TryGetValue((fi, fi), out Complex cur) ? cur + shunt : Complex.Zero + shunt;
            return SparseKronReduction.Reduce(size, yaF, keep);
        }

        /// <summary>
        /// The <see cref="Prepared"/> record, with <paramref name="faulted"/> as its faulted reduction. With
        /// <paramref name="copyArrays"/> every array is a fresh copy (a <see cref="FaultScreen"/> hands out one Prepared per
        /// fault bus and each must own its arrays, as each FromSolvedCase call always did); FromSolvedCase itself passes
        /// false (this object is discarded after the call).
        /// </summary>
        public Prepared ToPrepared(Complex[,]? faulted, bool copyArrays) => copyArrays
            ? new Prepared((double[])h.Clone(), (double[])pm.Clone(), (double[])eq.Clone(), (double[])delta0.Clone(),
                (Complex[,])yRed.Clone(), (int[])genBuses.Clone(), faulted, machineIdx is null ? null : new List<int>(machineIdx))
            : new Prepared(h, pm, eq, delta0, yRed, genBuses, faulted, machineIdx);
    }

    /// <summary>
    /// R007 (stream P, October 2026): a classical preparation reused across fault buses. The CCT screen (CLI
    /// <c>stability --screen</c>) and the single study (which prepares, then searches the CCT at the same bus) used to call
    /// <see cref="FromSolvedCase"/> once per fault bus, rebuilding the augmented network and redoing the identical
    /// UNFAULTED reduction every time; only the faulted reduction depends on the bus.
    ///
    /// <para><see cref="ForFault"/> returns exactly <c>FromSolvedCase(model, result, xdpMachineBase, faultBusId,
    /// holdNonMachines)</c>, bit for bit, with its own fresh arrays: the fault-independent part is computed once, on the
    /// first call, by the very code FromSolvedCase runs, and only the faulted reduction (the same code again) is done per
    /// bus; the latest bus's faulted reduction is kept, so asking for the same bus again (the single study) costs a copy.
    /// A call that fails leaves nothing cached, so a later call fails again with the same exception, as every
    /// FromSolvedCase call would. Making the screen computes nothing and cannot fail on the model, so a caller that never
    /// asks for a fault (a model with no generator bus) behaves as before.</para>
    ///
    /// <para>The model and the solved result must not change while the screen is in use (they are read when the first
    /// fault is asked for); the X'd array is copied when the screen is made. Safe to use from several threads (a
    /// lock).</para>
    ///
    /// <para>Not done, because it is not bit-identical: a bolted fault changes one diagonal entry, so the faulted
    /// reduction could be had from the unfaulted one by a rank-one (Sherman-Morrison) update, or by resuming a saved
    /// elimination state at the fault bus; both round differently from the full reduction.</para>
    /// </summary>
    public sealed class FaultScreen
    {
        private readonly GridModel _model;
        private readonly PowerFlowResult _result;
        private readonly double[]? _xdp;
        private readonly bool _hold;
        private readonly object _gate = new();
        private Augmented? _augmented;
        private int _lastFaultBus;
        private Complex[,]? _lastFaulted;

        internal FaultScreen(GridModel model, PowerFlowResult result, double[]? xdpMachineBase, bool holdNonMachines)
        {
            _model = model;
            _result = result;
            _xdp = xdpMachineBase is null ? null : (double[])xdpMachineBase.Clone();
            _hold = holdNonMachines;
        }

        /// <summary>The model the screen prepares.</summary>
        public GridModel Model => _model;

        /// <summary>Exactly <see cref="FromSolvedCase"/> for this screen's inputs and <paramref name="faultBusId"/>, bit for bit.</summary>
        public Prepared ForFault(int faultBusId)
        {
            lock (_gate)
            {
                Augmented a = _augmented ??= Augment(_model, _result, _xdp, _hold);   // not cached when it throws
                if (_lastFaulted is null || _lastFaultBus != faultBusId)
                {
                    _lastFaulted = null;                          // a failed reduction leaves nothing cached
                    Complex[,] faulted = a.FaultedReduction(faultBusId);
                    _lastFaultBus = faultBusId;
                    _lastFaulted = faulted;
                }
                return a.ToPrepared((Complex[,])_lastFaulted.Clone(), copyArrays: true);
            }
        }
    }

    /// <summary>R007 (stream P, October 2026): a <see cref="FaultScreen"/> over <paramref name="model"/> and its solved
    /// <paramref name="result"/>, with the arguments <see cref="FromSolvedCase"/> takes. Computes nothing until the first
    /// fault is asked for.</summary>
    public static FaultScreen PrepareFaultScreen(GridModel model, PowerFlowResult result, double[]? xdpMachineBase = null,
        bool holdNonMachines = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        return new FaultScreen(model, result, xdpMachineBase, holdNonMachines);
    }

    // ---- Named literals (October 2026 review, R158, H hygiene stream). Each value is unchanged. ----

    /// <summary>Transient reactance X'd (pu, machine base) assumed for a machine when the caller passes no
    /// <c>xdpMachineBase</c>: a conservative typical value; studies that rely on it say so and label the result
    /// indicative. The CLI's synthetic machine data (<c>CliDefaults.SyntheticXdpPu</c>) carries the same value as its own
    /// literal, because the v2 kind spec (schemas/v2/kinds/synchronousMachine.kind.json) cites that source line; a test
    /// (HHygieneTests.R158) pins the two equal.</summary>
    public const double DefaultTransientReactancePu = 0.30;

    /// <summary>Floor (pu^2) on |V|^2 when a constant-power load or held injection becomes a constant admittance
    /// S* / |V|^2 (the same floor as <c>SubtransientInit.MinVoltageSquaredPu</c>).</summary>
    public const double MinVoltageSquaredPu = 1e-9;

    /// <summary>A bus whose scheduled machine output is at or below this (MW) splits its solved generation equally
    /// between its machines instead of in proportion to schedule (the same threshold as
    /// <c>SubtransientInit.MinScheduledPowerMw</c>).</summary>
    public const double MinScheduledShareMw = 1e-9;

    /// <summary>Shunt admittance (pu, system base) stamped at the faulted bus for a bolted three-phase fault: a near
    /// short-circuit to ground (the same value as <c>SubtransientInit.BoltedFaultShuntPu</c>).</summary>
    public const double BoltedFaultAdmittancePu = 1e7;

    /// <summary>Default cap on the classical CCT bisection halvings (the same 18 as the RMS search; only a safety
    /// limit since N042: the default 500-step window reaches step resolution in 9).</summary>
    public const int CctDefaultBisectionIterations = 18;

    /// <summary>Fault onset (s) of every classical CCT trial: a short pre-fault steady stretch. The same 0.1 s
    /// the RMS CCT and the CLI's single classical study use (<c>CliDefaults.FaultOnsetS</c>).</summary>
    public const double CctOnsetSeconds = 0.1;

    /// <summary>Each classical CCT trial runs until this long (s) after the fault clears: long enough for a
    /// first-swing loss of synchronism to show as a slip past the limit. The same 5 s the CLI study uses.</summary>
    public const double CctPostClearWindowSeconds = 5.0;

    /// <summary>Default integration step (s) of the classical CCT search: the grid the reported CCT lies on (its
    /// resolution). The same 2 ms as the RMS CCT and the CLI's classical study. Value unchanged by N042.</summary>
    public const double CctDefaultStepSeconds = 0.002;

    /// <summary>Default upper end (s) of the classical CCT search window. Value unchanged by N042.</summary>
    public const double CctDefaultMaxSeconds = 1.0;

    /// <summary>
    /// Critical clearing time (seconds) for a self-clearing three-phase fault at <paramref name="faultBusId"/>,
    /// quantised to the integration step (N042, the classical twin of R106 / ruling Q15). This is
    /// <see cref="CriticalClearingBracket"/>'s <see cref="CctBracket.StableSeconds"/>: the LONGEST clearing time
    /// on the step grid (a whole number of <paramref name="stepSeconds"/> steps) after which the classical fleet
    /// still holds synchronism. When the search reached step resolution (always, with the default cap and
    /// window), clearing one step later loses synchronism. Returns 0 if even an instantaneous fault is unstable,
    /// or the last grid point inside <paramref name="maxSeconds"/> if no clearing time in the window
    /// destabilises it (for the default 1 s window at 2 ms that is exactly 1.0, as before).
    ///
    /// <para>Why step-quantised: before N042 this ran 18 halvings of a continuous bisection and reported the
    /// midpoint of a 3.8 microsecond bracket, far below the 2 ms step. The classical kernel only looks at the
    /// fault window at its RK4 sample points (the start, middle and end of each step), so a clearing time
    /// inside one step changes nothing but which of those samples see the faulted network; digits below the
    /// step reflect that sampling, not the physics, and are no longer reported. The search now runs over whole
    /// steps and states its bracket, exactly as the RMS engine does.</para>
    ///
    /// <para><paramref name="holdNonMachines"/> is passed to <see cref="FromSolvedCase"/> unchanged.</para>
    ///
    /// <para><paramref name="f0"/> is the nominal frequency (Hz) the swing is integrated at; null (the default) takes the
    /// model's own <see cref="GridModel.FrequencyHz"/> (R238, see <see cref="CriticalClearingBracket"/>).</para>
    /// </summary>
    public static double CriticalClearingTimeSeconds(GridModel model, PowerFlowResult result,
        double[]? xdpMachineBase, int faultBusId,
        double? f0 = null, double stepSeconds = CctDefaultStepSeconds, double maxSeconds = CctDefaultMaxSeconds, int iterations = CctDefaultBisectionIterations,
        bool holdNonMachines = false, int scanSteps = CctSearch.DefaultScanSteps,
        double postClearWindowSeconds = CctPostClearWindowSeconds)
        => CriticalClearingBracket(model, result, xdpMachineBase, faultBusId, f0, stepSeconds, maxSeconds,
            iterations, holdNonMachines, scanSteps, postClearWindowSeconds).StableSeconds;

    /// <summary>
    /// The step-quantised critical-clearing bracket of the classical model (N042; the same contract as
    /// <see cref="Models.RmsTransientStability.CriticalClearingBracket"/>). The search runs over whole
    /// integration steps m = 0 .. M, where M is the number of whole steps inside <paramref name="maxSeconds"/>.
    /// A trial with m steps clears the fault at exactly <c>m * stepSeconds</c> after the onset
    /// (<see cref="CctOnsetSeconds"/>), built as the fault window <c>[onset, onset + m * stepSeconds)</c>: the
    /// very window a single study at that clearing time builds (the CLI's <c>stability --clear</c>), so feeding
    /// the reported value back into one study with the same step gives the same verdict, bit for bit. Integer
    /// bisection keeps a stable step count <c>lo</c> and an unstable one <c>hi</c> and stops as soon as they are
    /// adjacent (or at the <paramref name="iterations"/> cap, which the default 18 never reaches for windows up
    /// to 2^18 steps; the default 500-step window needs 9 halvings, so a CCT costs about half the runs it did).
    /// No floating-point midpoint is ever formed, so every CPU visits exactly the same clearing times.
    ///
    /// <para>Edge cases: if the instantaneous clear (m = 0) already loses synchronism the bracket is [0, 0]
    /// steps with <see cref="CctBracket.UnstableAtZero"/> set; if the whole window survives, the stable end is M
    /// and <see cref="CctBracket.UnstableSteps"/> is null.</para>
    ///
    /// <para>N048: stability is NOT always monotone in the clearing time. A multi-swing case can have a stable pocket
    /// above the first loss (WSCC-9 bus 2 under the CLI setup, on the 2 ms grid: 171 steps holds, 172 to 174 lose
    /// synchronism, 175 (350 ms) holds again, 176 to 187 lose it; D-DOC-191, measured at 1.95.203), and a bisection can
    /// land on the pocket's upper edge. The search
    /// is the shared <see cref="CctSearch"/>: the bisection, then a downward verification of
    /// <paramref name="scanSteps"/> steps below the stable end (re-searching lower when one loses synchronism) and an
    /// upward probe of as many above the unstable end, so the reported CCT is the first loss from below and a pocket
    /// is flagged (<see cref="CctBracket.NonMonotone"/>). 0 gives the bisection alone.</para>
    ///
    /// <para>Fail loud: a step or window that is not positive and finite, a window shorter than one step or
    /// holding more than int.MaxValue steps, and <paramref name="iterations"/> below 1 all throw
    /// <see cref="ArgumentOutOfRangeException"/> (before N042 a bad step or window ran a meaningless search).</para>
    ///
    /// <para><b>Nominal frequency (R238, D2, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04).</b> The
    /// synchronous speed ws = 2 * pi * f0 multiplies d(delta)/dt directly, so the CCT depends on f0. This used to default
    /// to a literal 50.0 whatever the model said, and neither CLI caller passed one, so a 60 Hz system (the WSCC 9-bus,
    /// N017) had its clearing time computed as if it ran at 50 Hz, while the CLI's single classical study, the small-signal
    /// assembler and the impedance scan all used the model's frequency. <paramref name="f0"/> is now nullable: null (the
    /// default) takes <see cref="GridModel.FrequencyHz"/>, which is the right quantity, and an explicit value still
    /// overrides it. For every 50 Hz model this is the same number as before, bit for bit (the same 50.0 reaches the
    /// kernel). A given or resolved f0 that is not finite and above 0 Hz throws <see cref="ArgumentOutOfRangeException"/>.</para>
    ///
    /// <para><b>Post-clear window (N341, FB1, October 2026; Ruling R1).</b> Each trial integrates until
    /// <paramref name="postClearWindowSeconds"/> after the fault clears (default <see cref="CctPostClearWindowSeconds"/>,
    /// 5 s: the value every search always used, so omitting the argument is bit for bit the search it was). The undamped
    /// classical swing holds every swing it starts, so the verdict of a trial can depend on how many swings the window
    /// holds (WSCC-9 bus 7 at 60 Hz: 5 s gives 268-275 steps stable, 276-278 lost, 279 stable; 20 s loses from 268 up).
    /// The argument lets a caller state the window, and it is what makes the frequency scaling exact: the undamped swing
    /// (2H / ws) d2(delta)/dt2 = Pm - Pe(delta) depends on time only through t * sqrt(ws), so a search at f0 = 60 Hz with
    /// step h and window W visits the same trajectories as a search at 50 Hz with step h * sqrt(1.2) and window
    /// W * sqrt(1.2) (the 0.1 s pre-fault stretch is the steady state, where nothing moves), and the two brackets (in
    /// steps) are equal: measured at 1.95.259 at every WSCC-9 bus and at windows 5 / sqrt(1.2), 5, 5 x sqrt(1.2) and 20 s,
    /// with the same trial count (Fb1FinalBatchFollowUpsTests.N341). Fail loud: a window that is not positive and finite
    /// throws <see cref="ArgumentOutOfRangeException"/>.</para>
    /// </summary>
    public static CctBracket CriticalClearingBracket(GridModel model, PowerFlowResult result,
        double[]? xdpMachineBase, int faultBusId,
        double? f0 = null, double stepSeconds = CctDefaultStepSeconds, double maxSeconds = CctDefaultMaxSeconds, int iterations = CctDefaultBisectionIterations,
        bool holdNonMachines = false, int scanSteps = CctSearch.DefaultScanSteps,
        double postClearWindowSeconds = CctPostClearWindowSeconds)
        => CriticalClearingBracketCore(model, () => FromSolvedCase(model, result, xdpMachineBase, faultBusId, holdNonMachines),
            f0, stepSeconds, maxSeconds, iterations, scanSteps, machineDamping: null, postClearWindowSeconds);

    /// <summary>
    /// R007 (stream P, October 2026): the step-quantised CCT bracket of the overload above (model, result, reactances),
    /// on a <see cref="FaultScreen"/>, so a screen over many fault buses (and a study that first prepares, then searches the
    /// same bus) builds the fault-independent part once. Bit for bit the same bracket: the same checks in the same order,
    /// then <see cref="FaultScreen.ForFault"/> (exactly FromSolvedCase) where the other overload calls FromSolvedCase, then
    /// the same search. The screen carries the model, the solved result, the reactances and the hold flag.
    ///
    /// <para><paramref name="machineDamping"/> (N314, LF5, October 2026; Ruling R1): the damping D of each generator,
    /// aligned to <c>model.Generators</c> (the convention of <c>SmallSignalAssembler.FromSolvedCase</c>), per unit on the
    /// system base in the swing <c>d omegabar / dt = (Pm - Pe - D omegabar) / 2H</c> that
    /// <see cref="ClassicalMultiMachine.Simulate"/> integrates. Each integrated machine takes its own generator's D
    /// (<see cref="Prepared.MachineIndex"/>); held units take none. Null (the default) is the undamped swing every trial
    /// always ran (a fresh all-zero array), bit for bit. Each value must be finite (else
    /// <see cref="ArgumentException"/>), and the array must have one entry per generator.</para>
    /// </summary>
    public static CctBracket CriticalClearingBracket(FaultScreen screen, int faultBusId,
        double? f0 = null, double stepSeconds = CctDefaultStepSeconds, double maxSeconds = CctDefaultMaxSeconds, int iterations = CctDefaultBisectionIterations,
        int scanSteps = CctSearch.DefaultScanSteps, double[]? machineDamping = null,
        double postClearWindowSeconds = CctPostClearWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return CriticalClearingBracketCore(screen.Model, () => screen.ForFault(faultBusId), f0, stepSeconds, maxSeconds, iterations, scanSteps,
            machineDamping, postClearWindowSeconds);
    }

    /// <summary>
    /// N314 (LF5, October 2026): the per-machine damping of a prepared classical system: entry i is
    /// <paramref name="machineDamping"/>[<c>p.MachineIndex[i]</c>], the D of the generator machine i stands for. Null gives
    /// null (the caller's undamped all-zero array). Fail loud: an array whose length is not the model's generator count, or
    /// a value that is not finite, throws <see cref="ArgumentException"/> naming the generator.
    /// </summary>
    public static double[]? DampingByMachine(GridModel model, Prepared p, double[]? machineDamping)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(p);
        if (machineDamping is null) return null;
        if (machineDamping.Length != model.Generators.Count)
            throw new ArgumentException($"machineDamping has {machineDamping.Length} entries but the model has {model.Generators.Count} generators; "
                + "give one damping per generator, aligned to model.Generators.", nameof(machineDamping));
        var d = new double[p.H.Length];
        for (int i = 0; i < d.Length; i++)
        {
            int k = p.MachineIndex[i];
            if (!double.IsFinite(machineDamping[k]))
                throw new ArgumentException($"machineDamping[{k}] is {machineDamping[k]}; damping must be finite.", nameof(machineDamping));
            d[i] = machineDamping[k];
        }
        return d;
    }

    /// <summary>The CCT search shared by both overloads: the argument checks, then <paramref name="prepare"/> (at the
    /// point the search always prepared), then the search. The body is the historical CriticalClearingBracket body with
    /// its FromSolvedCase call replaced by <paramref name="prepare"/>.</summary>
    private static CctBracket CriticalClearingBracketCore(GridModel model, Func<Prepared> prepare,
        double? f0, double stepSeconds, double maxSeconds, int iterations, int scanSteps, double[]? machineDamping,
        double postClearWindowSeconds)
    {
        // Pedantic gates: the step grid must exist and hold at least one whole step, or the search is meaningless.
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), $"The CCT step must be positive and finite (got {stepSeconds}).");
        if (!(maxSeconds > 0.0) || !double.IsFinite(maxSeconds))
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window must be positive and finite (got {maxSeconds}).");
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations), $"The CCT bisection needs at least one halving (got {iterations}).");
        if (scanSteps < 0)
            throw new ArgumentOutOfRangeException(nameof(scanSteps), $"The CCT scan depth must be 0 or more steps (got {scanSteps}).");
        // N341: the post-clear window is how long each trial keeps integrating after the fault clears. A window that is
        // not positive and finite would judge stability on no swing at all (or never stop), so it is refused.
        if (!(postClearWindowSeconds > 0.0) || !double.IsFinite(postClearWindowSeconds))
            throw new ArgumentOutOfRangeException(nameof(postClearWindowSeconds),
                $"The CCT post-clear window must be positive and finite (got {postClearWindowSeconds} s).");

        // R238: the nominal frequency is the model's own unless the caller overrides it. Resolved once, here, so every
        // trial of the search integrates at exactly the same synchronous speed.
        ArgumentNullException.ThrowIfNull(model);
        double nominalHz = f0 ?? model.FrequencyHz;
        if (!(nominalHz > 0.0) || !double.IsFinite(nominalHz))
            throw new ArgumentOutOfRangeException(nameof(f0), $"The CCT nominal frequency must be positive and finite (got {nominalHz} Hz"
                + (f0 is null ? ", the model's FrequencyHz)." : ")."));

        // M = the number of whole steps inside the window. The millionth-of-a-step tolerance (the RMS
        // integrator's stage tolerance, shared so the two engines count the same steps) absorbs the rounding of
        // maxSeconds / stepSeconds (1.0 / 0.002 is not exactly 500 in binary), so a window that is a whole
        // number of steps keeps its last step.
        double stepsInWindow = maxSeconds / stepSeconds + Models.MultiMachineSubtransient.StageTimeToleranceSteps;
        if (stepsInWindow < 1.0)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s is shorter than "
                + $"one integration step ({stepSeconds} s); there is no clearing time to search.");
        if (stepsInWindow > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), $"The CCT window {maxSeconds} s holds more than "
                + $"{int.MaxValue} steps of {stepSeconds} s.");
        int maxSteps = (int)Math.Floor(stepsInWindow);

        Prepared p = prepare();   // FromSolvedCase (or a FaultScreen's ForFault, the same bits)
        if (p.YReducedFaulted is null) throw new InvalidOperationException("faulted reduction unavailable.");
        // N314: the machines' damping, mapped once (null = the undamped swing: a fresh zero array per trial, as always).
        double[]? damping = DampingByMachine(model, p, machineDamping);

        // One trial: the fault is on from the onset and cleared after exactly `steps` whole steps. The clearing
        // time is the product steps * stepSeconds (the value reported), and the window is built from it exactly
        // as a single study at that clearing time builds it.
        bool Survives(int steps)
        {
            double clear = steps * stepSeconds;
            var fault = new ClassicalMultiMachine.Fault(CctOnsetSeconds, CctOnsetSeconds + clear, p.YReducedFaulted);
            return ClassicalMultiMachine.Simulate(
                p.H, damping ?? new double[p.H.Length], p.Pm, p.Eq, p.Delta0, p.YReduced,
                nominalHz, stepSeconds, CctOnsetSeconds + clear + postClearWindowSeconds, fault).StayedInSynchronism;
        }

        // The shared search (N048): the unchanged bisection, then the downward verification and the upward probe.
        return CctSearch.Run(Survives, maxSteps, stepSeconds, iterations, scanSteps);
    }
}
