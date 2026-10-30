using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// Builds the <see cref="MultiMachineSubtransient"/> inputs from a solved power flow - the subtransient
/// analogue of <see cref="ClassicalInit"/>. It computes the self-consistent six-state fixed point so an
/// undisturbed run seeded from a real dispatch is stationary:
///   1. each generator's terminal voltage and current come from the solved case (<c>I_t = conj(S_gen/V_t)</c>);
///   2. the rotor angle is the angle of the q-axis phasor <c>V_t + jXq*I_t</c> - the classic salient-machine
///      construction, which forces <c>Vd = Xq*Iq</c> and hence makes every flux-decay derivative vanish;
///   3. the dq terminal quantities then give the transient and subtransient EMFs directly
///      (<c>E'q = Vq + X'd*Id</c>, <c>E'd = Vd - X'q*Iq</c>, <c>E''q = Vq + X''d*Id</c>,
///      <c>E''d = Vd - X''q*Iq</c>);
///   4. the field voltage is set to hold <c>E'q</c> (<c>Efd = E'q + (Xd - X'd)*Id + E''q*Se(|E''|)</c>, the GENROU
///      d-axis saturation increment since N045; it used to read <c>Se(E'q)</c>), and the mechanical power to the
///      electrical output (<c>Pm = Vd*Id + Vq*Iq</c>). With saturation the q-axis behaves as the saturated reactance
///      (scale <c>(Xq - Xl)/(Xd - Xl)</c>, see the N045 note below), so step 2 uses that reactance;
///   5. each machine is folded into the network by its round subtransient reactance <c>X''</c> and the
///      augmented admittance is Kron-reduced to the internal nodes - the reduced admittance the swing uses.
/// Reactances and inertia convert from the machine base by the <c>MBase/BaseMva</c> ratio.
///
/// <para><b>Converter plant.</b> A converter-interfaced generator (<see cref="Generator.IsConverterInterfaced"/>)
/// is not a machine: it has no rotor, inertia or EMF behind <c>X''</c>. By default it is taken out of the machine
/// fleet and its terminal bus is retained after the machine internal nodes, as an
/// <see cref="RmsConverterUnit"/> current source initialised on its share of the solved bus injection
/// (co-located converters are aggregated into one unit per bus). Its <c>MachineData</c> entry is ignored.
/// <c>convertersAsCurrentSources: false</c> restores the old treatment (every generator a machine).</para>
///
/// <para><b>Power-flow-only units</b> (<see cref="Generator.PowerFlowOnly"/>: a slack balancer, a voltage-control
/// device) are neither machines nor converters: their solved injection becomes a constant admittance at their
/// bus. So is a synchronous-typed unit with no inertia (<c>H &lt;= 0</c>), which cannot be integrated; those are
/// listed in <see cref="Prepared.HeldUnits"/>. An island with no integrable machine but a slack or PV bus gets a
/// stiff synthetic reference source at that bus (<see cref="Prepared.IslandReferences"/>) - otherwise the converter
/// current sources and held admittances there would have no voltage reference and float.</para>
///
/// <para><b>One network engine at every size</b> (N271, October 2026; ruled by main under Mark's determinism rule
/// 2026-10-04, Ruling R1: Mark delegated EE calls 2026-10-04). Every case is prepared on the sparse full network
/// (<see cref="SparseRmsNetwork"/>: machines as Norton sources behind <c>X''</c>, factored once per stage with the same
/// sparse LU as the fault network, N233). Before, a case of up to 1,500 buses was prepared on a Kron-reduced dense
/// admittance and only larger ones (gb-full) on the sparse network. The two are the same linear model, but they are
/// different arithmetic, so a 9-bus case and gb-full did not run the same engine and their answers differed in the last
/// bits: two answers to one question, which the determinism rule forbids. The size switch is gone, exactly as the fault
/// network lost it at N233 and the power flow at N057; cases above 1,500 buses were already sparse and are bit for bit
/// unchanged. The dense Kron path survives only as a TEST ORACLE (<see cref="DenseKronReferenceScope"/>): an independent
/// algorithm the tests cross-check the sparse answers against; no production code can select it.</para>
/// <para>A unit's <see cref="Generator.UnitTransformerXPu"/> is added to every machine reactance (the machine
/// referred to the HV bus).</para>
/// </summary>
public static class SubtransientInit
{
    /// <summary>
    /// N271 (October 2026): TEST ORACLE ONLY. While a test holds <see cref="DenseKronReferenceScope"/> on its own async
    /// flow, <see cref="FromSolvedCase"/> on that flow prepares the case on the historical dense Kron-reduced admittance
    /// (<see cref="Prepared.YReduced"/> / <see cref="Prepared.YReducedFaulted"/>, run through
    /// <see cref="DenseMachineNetwork"/> or <see cref="ConverterNetwork"/>) instead of the sparse full network, so a test
    /// can compare the production (sparse) answer with an independent algorithm, or exercise the dense kernel entry
    /// points. An AsyncLocal, so tests running in parallel never see each other's setting. Production code never sets it
    /// (default false: always the sparse network). It replaces the public <c>sparseNetwork</c> argument and the 1,500-bus
    /// size switch (<c>DenseKronBusLimit</c>), both removed.
    /// </summary>
    private static readonly AsyncLocal<bool> DenseKronReferenceFlow = new();

    /// <summary>N271: prepare on the dense Kron test oracle on the current async flow until the returned scope is
    /// disposed. Tests only: the dense Kron path is not bit-identical to the sparse engine and production never uses
    /// it. The previous setting is restored on dispose, so scopes nest.</summary>
    internal static IDisposable DenseKronReferenceScope()
    {
        bool previous = DenseKronReferenceFlow.Value;
        DenseKronReferenceFlow.Value = true;
        return new RestoreDenseKronFlow(previous);
    }

    private sealed class RestoreDenseKronFlow(bool previous) : IDisposable
    {
        public void Dispose() => DenseKronReferenceFlow.Value = previous;
    }

    /// <summary>Per-machine dynamic data on the machine's own base (aligned to <c>model.Generators</c>); the
    /// round subtransient reactance folded into the network is <see cref="Xdpp"/> (= <see cref="Xqpp"/>).
    /// <para><see cref="Governor"/> (R061, governor part; October 2026, stream B3): the machine's turbine-governor
    /// parameters, or null for a constant mechanical power (the historical study). The RMS study builds a fresh governor
    /// from it for every run (<see cref="RmsTransientStability"/>); this initialisation does not read it.</para>
    /// <para><see cref="Xl"/> (N045): the stator leakage reactance (machine base), read only by the GENROU q-axis
    /// saturation scale <c>(Xq - Xl) / (Xd - Xl)</c>; 0 (the default) is the leakage-free limit. It must be below
    /// X''d (checked when the machine is saturated).</para></summary>
    public sealed record MachineData(
        double H, double Damping,
        double Xd, double Xdp, double Xdpp, double Xq, double Xqp, double Xqpp,
        double Tdop, double Tdopp, double Tqop, double Tqopp,
        SubtransientMachine.SaturationCurve? Saturation = null,
        GovernorData? Governor = null,
        double Xl = 0.0)
    {
        /// <summary>
        /// WECC (expansion): the unit's WECC second-generation renewable model chain (REGC_A with its electrical control),
        /// bound from a PSS/E DYR file; null (the default) for every unit without one. A unit that carries a chain is
        /// converter plant in the RMS study whatever its case type (<see cref="IsConverterUnit"/>: PSS/E RAW does not
        /// carry the wind/renewable machine mode into the model, so a RAW renewable is typed as a synchronous unit), is
        /// never aggregated with other converters of its bus, and is driven by <see cref="Wecc.WeccConverterState"/>. The
        /// machine fields of the record are not read for it.
        /// </summary>
        public Wecc.WeccPlant? Renewable { get; init; }
    }

    /// <summary>
    /// WECC (expansion): true when generator <paramref name="gen"/> with dynamic data <paramref name="data"/> is converter
    /// plant in the RMS study: a converter-interfaced unit of the case, or a unit whose data carries a WECC model chain.
    /// For a unit with no chain this is exactly <see cref="Generator.IsConverterInterfaced"/>, so every study without WECC
    /// data classifies its fleet as before.
    /// </summary>
    public static bool IsConverterUnit(Generator gen, MachineData? data)
    {
        ArgumentNullException.ThrowIfNull(gen);
        return gen.IsConverterInterfaced || data?.Renewable is not null;
    }

    public sealed record Prepared(
        IReadOnlyList<MultiMachineSubtransient.Machine> Machines,
        double[] H, double[] Damping, double[] Pm, double[] Efd,
        double[] Delta0, double[] Eqp0, double[] Edp0, double[] Eqpp0, double[] Edpp0,
        Complex[,] YReduced, IReadOnlyList<int> GeneratorBuses,
        Complex[,]? YReducedFaulted = null,
        IReadOnlyList<RmsConverterUnit>? Converters = null,
        IReadOnlyList<string>? HeldUnits = null,
        IRmsNetwork? Network = null,
        IRmsNetwork? NetworkFaulted = null,
        IReadOnlyList<string>? IslandReferences = null)
    {
        /// <summary>True when the case was prepared on the sparse full network (<see cref="Network"/> /
        /// <see cref="NetworkFaulted"/>); <see cref="YReduced"/> is then empty and must not be used. Since N271 this is
        /// always true outside the test-only <see cref="DenseKronReferenceScope"/>.</summary>
        public bool IsSparse => Network is not null;

        /// <summary>The retained converter units (empty when the fleet is all machines). When present,
        /// <see cref="YReduced"/> / <see cref="YReducedFaulted"/> are square over the machine internal nodes
        /// followed by these converter buses.</summary>
        public IReadOnlyList<RmsConverterUnit> ConverterUnits => Converters ?? Array.Empty<RmsConverterUnit>();

        /// <summary>R061 rest (CF4, October 2026): the pieces <see cref="Network"/> was assembled from, so an RMS event
        /// study (<see cref="RmsTransientStability.StudyEvents"/>) can build the network of each event stage (a branch
        /// out, a load stepped, a machine tripped, a fault on or off) from the same stamps. Null on the dense test oracle.
        /// Reading it changes nothing: <see cref="Network"/> is built exactly as before.</summary>
        public SparseNetworkBasis? Basis { get; init; }
    }

    /// <summary>
    /// R061 rest (CF4): the stamps of the sparse RMS network, split so an event stage can be rebuilt. The network is
    /// <see cref="BranchCells"/> (the Y-bus of the in-service branches and bus shunts, exactly as
    /// <see cref="YBus.BuildSparse"/> gave it) followed by <see cref="OtherStamps"/> (load admittances, each machine's
    /// Norton admittance, held units, grid-forming converter admittances), in the order the base network received them.
    /// <see cref="MachineStampIndex"/>[i] is where machine i's Norton stamp sits in <see cref="OtherStamps"/> (a tripped
    /// machine's stamp is left out of a later stage).
    /// </summary>
    public sealed record SparseNetworkBasis(
        int BusCount,
        IReadOnlyDictionary<int, int> BusIndex,
        IReadOnlyList<(int Row, int Col, Complex V)> BranchCells,
        IReadOnlyList<(int Row, int Col, Complex V)> OtherStamps,
        IReadOnlyList<int> MachineStampIndex,
        IReadOnlyList<int> MachineTerminal,
        IReadOnlyList<Complex> MachineAdmittance,
        IReadOnlyList<int> ConverterBusIndex,
        IReadOnlyDictionary<int, Complex> InitialVoltageByBus);

    /// <summary>Prepare the fixed point from a solved <paramref name="result"/> and per-machine
    /// <paramref name="data"/> (machine base, aligned to <c>model.Generators</c>). When
    /// <paramref name="faultBusId"/> is given, a bolted three-phase fault at that bus is also reduced to the
    /// internal nodes (a large shunt to ground before reduction), for the faulted stage of a stability study.
    /// <para>N271: always prepared on the sparse full network (<see cref="Prepared.Network"/> /
    /// <see cref="Prepared.NetworkFaulted"/>); the old <c>sparseNetwork</c> argument is gone (see the class summary).</para></summary>
    public static Prepared FromSolvedCase(GridModel model, PowerFlowResult result, IReadOnlyList<MachineData> data,
        int? faultBusId = null, bool convertersAsCurrentSources = true)
    {
        // The convergence contract (October 2026, WS4): a non-converged state throws NonConvergedStateException (an
        // InvalidOperationException) here. D-DOC-213: a second check, "if (!result.Converged) throw ...", used to follow
        // this line; it could never fire (RequireConverged has already thrown for exactly that condition) and is gone.
        result.RequireConverged("Subtransient initialisation");
        ArgumentNullException.ThrowIfNull(data);
        int g = model.Generators.Count;
        if (g == 0) throw new InvalidOperationException("No generators to initialise.");
        if (data.Count != g) throw new ArgumentException("data must align to model.Generators.", nameof(data));

        double baseMva = model.BaseMva;
        int n = model.BusCount;
        // N271: one engine at every size, the sparse full network (no dense Ybus, no dense augmented matrix, no Kron
        // reduction). The dense Kron path is taken only under the test-only DenseKronReferenceScope.
        bool sparse = !DenseKronReferenceFlow.Value;
        YBus? ybus = sparse ? null : YBus.Build(model);
        var busIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) busIndex[model.Buses[i].Id] = i;

        var vByBus = new Dictionary<int, Complex>(result.BusIds.Count);
        var pInjByBus = new Dictionary<int, double>(result.BusIds.Count);
        var qInjByBus = new Dictionary<int, double>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count; i++)
        {
            vByBus[result.BusIds[i]] = DetMath.FromPolar(result.Vm[i], result.VaDeg[i] * Math.PI / 180.0);
            pInjByBus[result.BusIds[i]] = result.PInjMw[i];
            qInjByBus[result.BusIds[i]] = result.QInjMvar[i];
        }

        var schedPByBus = new Dictionary<int, double>();
        var genCountByBus = new Dictionary<int, int>();
        var loadByBus = new Dictionary<int, (double Pd, double Qd)>();
        foreach (Bus bb in model.Buses) loadByBus[bb.Id] = (bb.Pd, bb.Qd);
        foreach (Generator gg in model.Generators)
        {
            schedPByBus[gg.Bus] = schedPByBus.GetValueOrDefault(gg.Bus) + gg.Pg;
            genCountByBus[gg.Bus] = genCountByBus.GetValueOrDefault(gg.Bus) + 1;
        }

        // Loads -> constant shunt admittance, then each machine folded in by its subtransient reactance X''.
        // WECC (expansion): a unit carrying a WECC chain is converter plant (IsConverterUnit); with no chain anywhere this
        // is the historical test, unit for unit. A chain on a power-flow-only unit, or with converters taken as machines,
        // has no meaning and is refused rather than ignored.
        for (int k = 0; k < g; k++)
        {
            if (data[k]?.Renewable is null) continue;
            if (model.Generators[k].PowerFlowOnly)
                throw new ArgumentException($"Generator {k} at bus {model.Generators[k].Bus} is a power-flow-only unit but its dynamic data carries a WECC model chain ({data[k].Renewable!.Chain}); a power-flow-only unit has no dynamic model.", nameof(data));
            if (!convertersAsCurrentSources)
                throw new ArgumentException($"Generator {k} at bus {model.Generators[k].Bus} carries a WECC model chain, which needs converters as current sources (convertersAsCurrentSources: true).", nameof(data));
        }
        bool IsConverterAt(int k) => convertersAsCurrentSources && IsConverterUnit(model.Generators[k], data[k]);
        // A unit that is neither a converter nor power-flow-only but has no inertia cannot be integrated as a
        // machine: it is held as a constant injection (and reported), rather than failing the whole study.
        bool IsHeld(int k) => model.Generators[k].PowerFlowOnly
            || (!IsConverterAt(k) && !(data[k].H > 0.0));
        // The machine fleet (converter plant split out unless asked to treat it as machines).
        var machineIdx = new List<int>(g);
        var convIdx = new List<int>();
        var pfOnlyIdx = new List<int>();
        for (int k = 0; k < g; k++)
        {
            if (IsHeld(k)) pfOnlyIdx.Add(k);
            else if (IsConverterAt(k)) convIdx.Add(k);
            else machineIdx.Add(k);
        }

        // Islands with no integrable machine have no voltage reference in the dynamic model (converters are
        // current sources, held units constant admittances). If such an island has a slack or PV bus - the bus
        // the power flow held its voltage at - it gets a stiff synthetic reference source there.
        if (machineIdx.Count == 0)
            throw new InvalidOperationException("No synchronous machines: the RMS engine needs at least one machine "
                + "(converter-interfaced plant and power-flow-only units have no rotor to integrate).");
        List<int> islandRefs = IslandReferenceBuses(model, busIndex, machineIdx.Select(k => model.Generators[k].Bus));

        int machineCount = machineIdx.Count + islandRefs.Count;
        int m = n + machineCount;   // one internal node per machine (converters sit on their terminal bus)
        Complex[,]? ya = null;
        List<(int Row, int Col, Complex V)>? trip = null;
        int branchCellCount = 0;   // R061 rest (CF4): the number of leading Y-bus cells in trip
        if (sparse)
        {
            trip = new List<(int, int, Complex)>();
            foreach (KeyValuePair<(int Row, int Col), Complex> cell in YBus.BuildSparse(model).Cells)
                trip.Add((cell.Key.Row, cell.Key.Col, cell.Value));
            branchCellCount = trip.Count;   // R061 rest (CF4): where the non-branch stamps begin (the event basis)
        }
        else
        {
            ya = new Complex[m, m];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    ya[i, j] = ybus!.Y[i, j];
        }
        void Stamp(int r, int c, Complex v)
        {
            if (ya is not null) ya[r, c] += v; else trip!.Add((r, c, v));
        }
        for (int i = 0; i < n; i++)
        {
            Bus bus = model.Buses[i];
            if (bus.Pd == 0 && bus.Qd == 0) continue;
            Complex v = vByBus.TryGetValue(bus.Id, out Complex vv) ? vv : Complex.One;
            double v2 = Math.Max(MinVoltageSquaredPu, v.Magnitude * v.Magnitude);
            Stamp(i, i, Complex.Conjugate(new Complex(bus.Pd, bus.Qd) / baseMva) / v2);
        }

        int gm = machineCount;

        var machines = new MultiMachineSubtransient.Machine[gm];
        var h = new double[gm];
        var damping = new double[gm];
        var pm = new double[gm];
        var efd = new double[gm];
        var delta0 = new double[gm];
        var eqp0 = new double[gm];
        var edp0 = new double[gm];
        var eqpp0 = new double[gm];
        var edpp0 = new double[gm];
        var genBuses = new int[gm];
        var keep = new List<int>(gm + convIdx.Count);
        var terminal = new int[gm];
        var yGenAt = new Complex[gm];
        var machineStampAt = new int[gm];   // R061 rest (CF4): each machine's Norton stamp position (sparse)
        var synthetics = new List<string>();

        // A unit's share of its bus's solved generation. Active power splits by scheduled P; reactive power by
        // reactive capability (Qmax - Qmin, else MBase) - a voltage-control unit scheduled at 0 MW must still
        // carry the bus's MVAr rather than handing it all to a small co-located machine. A lone unit takes all.
        double QWeight(Generator x)
        {
            double range = x.Qmax - x.Qmin;
            return double.IsFinite(range) && range > 0 ? range : Math.Max(x.MBase, MinMBaseWeightMva);
        }
        var qWeightByBus = new Dictionary<int, double>();
        foreach (Generator gg in model.Generators) qWeightByBus[gg.Bus] = qWeightByBus.GetValueOrDefault(gg.Bus) + QWeight(gg);
        (Complex Vt, double P, double Q) Terminal(Generator gen)
        {
            Complex vt = vByBus.TryGetValue(gen.Bus, out Complex vg) ? vg : Complex.One;
            (double pdBus, double qdBus) = loadByBus.GetValueOrDefault(gen.Bus);
            double busGenP = pInjByBus.GetValueOrDefault(gen.Bus) + pdBus;
            double busGenQ = qInjByBus.GetValueOrDefault(gen.Bus) + qdBus;
            double schedP = schedPByBus.GetValueOrDefault(gen.Bus);
            int count = genCountByBus.GetValueOrDefault(gen.Bus, 1);
            double share = Math.Abs(schedP) > MinScheduledPowerMw ? gen.Pg / schedP : 1.0 / Math.Max(1, count);
            double qShare = count <= 1 ? share : QWeight(gen) / qWeightByBus[gen.Bus];
            return (vt, busGenP * share / baseMva, busGenQ * qShare / baseMva);
        }

        for (int mi = 0; mi < gm; mi++)
        {
            int k = mi;
            bool synthetic = mi >= machineIdx.Count;
            Generator gen;
            MachineData md;
            Complex vt;
            double pShare, qShare;
            if (!synthetic)
            {
                gen = model.Generators[machineIdx[mi]];
                md = data[machineIdx[mi]];
                // R250: only X''d is folded into the network (the dense internal node or the sparse Norton
                // admittance), but the initial E''d is built with X''q. With X''d != X''q the network sees
                // Vd = E''d + X''d Iq while the initialisation assumed X''q, so the "self-consistent fixed point"
                // is not stationary and the run starts with a spurious transient. Enforce the equality the fold
                // assumes (MachineData documents it) instead of silently mixing the two.
                double xppScale = Math.Max(Math.Abs(md.Xdpp), Math.Abs(md.Xqpp));
                if (!(Math.Abs(md.Xdpp - md.Xqpp) <= SubtransientSaliencyRelativeTolerance * xppScale))
                    throw new ArgumentException(
                        $"Generator {machineIdx[mi]} at bus {gen.Bus}: X''d = {md.Xdpp} and X''q = {md.Xqpp} differ. The subtransient " +
                        "network interface folds a single round-rotor X'' into the network, so X''d must equal X''q.", nameof(data));
                (vt, pShare, qShare) = Terminal(gen);
            }
            else
            {
                // Island reference: a stiff, high-inertia source holding the power-flow voltage and supplying
                // whatever the bus's own units do not (the injection the power flow's slack/PV bus carried).
                int rb = islandRefs[mi - machineIdx.Count];
                gen = new Generator(rb, 0.0, MBase: baseMva, H: IslandReferenceH, Name: $"island reference @ bus {rb}");
                md = new MachineData(IslandReferenceH, 0.0, IslandReferenceX, IslandReferenceX, IslandReferenceX,
                    IslandReferenceX, IslandReferenceX, IslandReferenceX,
                    IslandReferenceTdop, IslandReferenceTdopp, IslandReferenceTqop, IslandReferenceTqopp);
                vt = vByBus.TryGetValue(rb, out Complex vr) ? vr : Complex.One;
                (double pdBus, double qdBus) = loadByBus.GetValueOrDefault(rb);
                double assignedP = 0, assignedQ = 0;
                foreach (Generator other in model.Generators)
                    if (other.Bus == rb) { (_, double po, double qo) = Terminal(other); assignedP += po; assignedQ += qo; }
                pShare = (pInjByBus.GetValueOrDefault(rb) + pdBus) / baseMva - assignedP;
                qShare = (qInjByBus.GetValueOrDefault(rb) + qdBus) / baseMva - assignedQ;
                synthetics.Add(gen.Name!);
            }
            genBuses[k] = gen.Bus;
            // D-DOC-055: the one machine-base rule shared with ClassicalInit (MachineBase.EffectiveMva): a positive
            // MBase, else the system base (this line's rule before, unchanged for every finite MBase); a NaN or
            // infinite MBase is now refused instead of turning this machine's data into NaN.
            double mBase = MachineBase.EffectiveMva(gen, baseMva);
            double toSys = baseMva / mBase;

            // Reactances and inertia on the system base. A unit transformer is referred into every machine
            // reactance, so the machine is seen from the HV bus the network models.
            double xt = Math.Max(0.0, gen.UnitTransformerXPu);
            double xd = (md.Xd + xt) * toSys, xdp = (md.Xdp + xt) * toSys, xdpp = (md.Xdpp + xt) * toSys;
            double xq = (md.Xq + xt) * toSys, xqp = (md.Xqp + xt) * toSys, xqpp = (md.Xqpp + xt) * toSys;
            // N045: the GENROU q-axis saturation scale, on the machine's OWN reactances (no unit transformer in it).
            double? satScale = md.Saturation is null ? null
                : SubtransientMachine.QAxisSaturationRatio(md.Xd, md.Xq, md.Xdpp, md.Xl);
            machines[k] = new MultiMachineSubtransient.Machine(
                xd, xdp, xdpp, xq, xqp, xqpp, md.Tdop, md.Tdopp, md.Tqop, md.Tqopp, md.Saturation, satScale);
            h[k] = md.H * mBase / baseMva;
            // The damping D in MachineData is on the machine's own base (pu power per pu speed on MBase, the PSS/E
            // GENROU / GENSAL convention the DYR reader maps straight in). The kernel's swing equation
            // (Pm - Pe - D*omega) / (2H) has Pm, Pe and H on the SYSTEM base, so D must be carried to the system base
            // with the same MBase / BaseMva factor as H. Passing it raw gave a 600 MVA unit on a 100 MVA base six
            // times too little damping torque. When MBase equals the system base the factor is exactly 1.0 and every
            // number is bit-for-bit what it was.
            damping[k] = md.Damping * mBase / baseMva;

            // Fold X'' into the network: dense - an internal node behind X'' (Kron-reduced below); sparse - a
            // Norton admittance on the terminal (its source current is injected per solve).
            int ti = busIndex[gen.Bus];
            Complex yGen = Complex.One / new Complex(0.0, xdpp);
            terminal[k] = ti; yGenAt[k] = yGen;
            if (sparse)
            {
                machineStampAt[k] = trip!.Count - branchCellCount;   // R061 rest (CF4): recorded, not changed
                Stamp(ti, ti, yGen);
            }
            else
            {
                int intNode = n + k;
                keep.Add(intNode);
                Stamp(ti, ti, yGen); Stamp(intNode, intNode, yGen); Stamp(ti, intNode, -yGen); Stamp(intNode, ti, -yGen);
            }

            // Solved terminal condition, split across co-located units by scheduled share.
            Complex it = Complex.Conjugate(new Complex(pShare, qShare) / vt);

            // Bug hunt 2026-10-29, finding 23 (decided, documented): with a unit transformer folded in, X'' here already holds
            // UnitTransformerXPu, so |E''| = |V_t + jX''I_t| is the EMF behind X'' + Xt, not the machine's air-gap flux,
            // and Se(|E''|) is over-stated by the transformer drop. The seeded point is still an exact equilibrium (the
            // kernel uses the same expression), so this is a modelling approximation of the GENROU saturation input, not an
            // inconsistency; a machine with saturation data and a unit transformer is affected, nothing else.
            // Rotor angle from the q-axis phasor E_q = V_t + jXq*I_t (this forces Vd = Xq*Iq).
            // N045 (October 2026, stream B3): with GENROU saturation the q-axis flux balance carries
            // - E''d * s, s = Se(|E''|) * (Xq - Xl)/(Xd - Xl), so at equilibrium E''d (1 + s) = (Xq - X''q) Iq and the q-axis
            // behaves as the SATURATED reactance Xq,sat = X''q + (Xq - X''q) / (1 + s): the angle is that of
            // V_t + jXq,sat*I_t. |E''| = |V_t + jX''I_t| does not depend on the angle (X'' is round), so s is known before the
            // angle and no iteration is needed. Unsaturated, or below the curve's knee (s = 0): the historical Xq, bit for bit.
            double xqAngle = xq;
            if (md.Saturation is { } satCurve)
            {
                Complex ePP = vt + new Complex(0.0, xdpp) * it;
                double s = satCurve.Se(ePP.Magnitude) * satScale!.Value;
                if (s > 0.0) xqAngle = xqpp + (xq - xqpp) / (1.0 + s);
            }
            Complex eqPhasor = vt + new Complex(0.0, xqAngle) * it;
            double delta = DetMath.Phase(eqPhasor);
            (double sin, double cos) = DetMath.SinCos(delta);

            // dq terminal quantities (network -> machine frame).
            double vq = vt.Real * cos + vt.Imaginary * sin;
            double vd = vt.Real * sin - vt.Imaginary * cos;
            double iq = it.Real * cos + it.Imaginary * sin;
            double id = it.Real * sin - it.Imaginary * cos;

            double eqp = vq + xdp * id;
            double edp = vd - xqp * iq;
            double eqpp = vq + xdpp * id;
            double edpp = vd - xqpp * iq;
            // Saturation field-current increment: N045, the GENROU d-axis term E''q*Se(|E''|) (it was E'q*Se(E'q), d-axis
            // only); the RMS integrator subtracts exactly the same expression, so the seeded field is an equilibrium.
            double se = md.Saturation is { } satD ? satD.GenrouIncrements(eqpp, edpp, satScale!.Value).D : 0.0;

            delta0[k] = delta;
            eqp0[k] = eqp; edp0[k] = edp; eqpp0[k] = eqpp; edpp0[k] = edpp;
            efd[k] = eqp + (xd - xdp) * id + se;
            pm[k] = vd * id + vq * iq;
        }

        // Power-flow-only devices (slack balancer, voltage-control units) and zero-inertia units are not
        // integrable plant: their solved injection is held as a constant (negative-load) admittance at their
        // bus, so the initial state stays exact.
        foreach (int k in pfOnlyIdx)
        {
            Generator gen = model.Generators[k];
            (Complex vt, double pShare, double qShare) = Terminal(gen);
            int bi = busIndex[gen.Bus];
            double v2 = Math.Max(MinVoltageSquaredPu, vt.Magnitude * vt.Magnitude);
            Stamp(bi, bi, -(Complex.Conjugate(new Complex(pShare, qShare)) / v2));
        }

        // Converter plant: aggregate per bus and class (current-limited VSC vs blocking line-commutated, which
        // must not share a current limit), and retain each converter bus once after the machine internal nodes.
        var units = new List<RmsConverterUnit>();
        var convNodes = new List<int>();
        // WECC (expansion): a unit with a WECC chain is its own group (Wecc = its generator index; -1 for every other unit,
        // so without WECC data the groups and their order are exactly the historical ones) and is a current source, never
        // grid-forming: its group is not Forming and gets no Norton stamp.
        foreach (IGrouping<(int Bus, bool Blocks, bool Forming, int Wecc), int> grp in convIdx
            .GroupBy(k => data[k].Renewable is not null
                ? (model.Generators[k].Bus, false, false, k)
                : (model.Generators[k].Bus, model.Generators[k].EffectiveConverterFaultCurrentPu <= 0.0,
                    model.Generators[k].GridForming, -1))
            .OrderBy(gp => gp.Key.Bus))
        {
            double rating = 0, p0 = 0, q0 = 0, kWeighted = 0;
            Complex v0 = Complex.One;
            var names = new List<string>();
            foreach (int k in grp)
            {
                Generator gen = model.Generators[k];
                double r = MachineBase.EffectiveMva(gen, baseMva) / baseMva;   // D-DOC-055: the shared base rule
                (Complex vt, double pShare, double qShare) = Terminal(gen);
                rating += r; p0 += pShare; q0 += qShare; v0 = vt;
                kWeighted += gen.EffectiveConverterFaultCurrentPu * r;
                names.Add(gen.Name ?? $"converter@bus {gen.Bus}");
            }
            double kPu = rating > 0 ? kWeighted / rating : Generator.DefaultConverterFaultCurrentPu;
            string name = names.Count == 1 ? names[0] : $"{names.Count} converters @ bus {grp.Key.Bus}";
            RmsConverterParameters cp = grp.Key.Forming ? RmsConverterParameters.GridFormingWithLimit(kPu)
                : RmsConverterParameters.WithLimit(grp.Key.Blocks ? 0.0 : kPu);
            // R196 / R238 (D2 nominal frequency, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): a grid-forming
            // unit turns its virtual speed into an angle rate with its NominalHz, which defaulted to the GB 50 Hz whatever
            // the network ran at. The converters belong to this model, so they take its nominal frequency, the same value
            // the machines' swing now integrates at (RmsTransientStability.ResolveNominalHz). For a 50 Hz model this is
            // the same 50.0 as the default, so every existing study is bit-identical.
            cp = cp with { NominalHz = model.FrequencyHz };
            var unit = grp.Key.Wecc >= 0
                ? new RmsConverterUnit(grp.Key.Bus, name, rating, p0, q0, v0, new RmsConverterParameters { NominalHz = model.FrequencyHz }, BaseMva: baseMva)
                    { Wecc = data[grp.Key.Wecc].Renewable, WeccMeasurement = WeccMeasurementOf(model, data[grp.Key.Wecc].Renewable!, grp.Key.Bus, busIndex, vByBus) }
                : new RmsConverterUnit(grp.Key.Bus, name, rating, p0, q0, v0, cp, BaseMva: baseMva);
            units.Add(unit);
            if (!convNodes.Contains(grp.Key.Bus)) { convNodes.Add(grp.Key.Bus); keep.Add(busIndex[grp.Key.Bus]); }
            // A grid-forming unit's virtual reactance is part of the network (its Norton admittance).
            // WECC: a voltage-source model (REGC_B, REGFM_A1) stamps its source admittance the same way; every other WECC
            // unit has none, so without such a unit the stamps are exactly the historical ones.
            if (grp.Key.Forming || unit.Wecc?.NortonImpedance is not null) { int bi = busIndex[grp.Key.Bus]; Stamp(bi, bi, unit.NortonAdmittance); }
        }

        // WECC: a plant controller's regulated bus and flow-branch buses are retained as converter nodes with no injection,
        // after every unit bus, in RmsConverterUnit.NodeBuses order (the order the kernel uses). Without WECC plant
        // controllers there are none and the nodes are exactly the unit buses above.
        foreach (int b in RmsConverterUnit.NodeBuses(units))
            if (!convNodes.Contains(b)) { convNodes.Add(b); keep.Add(busIndex[b]); }

        var held = pfOnlyIdx.Where(k => !model.Generators[k].PowerFlowOnly)
            .Select(k => model.Generators[k].Name ?? $"gen@bus {model.Generators[k].Bus}").ToList();

        if (sparse)
        {
            int[] convBus = convNodes.Select(b => busIndex[b]).ToArray();
            var net = new SparseRmsNetwork(n, trip!, terminal, yGenAt, convBus);
            SparseRmsNetwork? netF = null;
            if (faultBusId is int fbs)
            {
                var tripF = new List<(int Row, int Col, Complex V)>(trip!) { (busIndex[fbs], busIndex[fbs], new Complex(BoltedFaultShuntPu, 0.0)) };
                netF = new SparseRmsNetwork(n, tripF, terminal, yGenAt, convBus);
            }
            // R061 rest (CF4): the event basis is a copy of the stamps the networks above were built from; building it
            // reads them only, so the base and faulted networks are exactly what they were.
            var basis = new SparseNetworkBasis(n, busIndex,
                trip!.GetRange(0, branchCellCount).ToArray(),
                trip.GetRange(branchCellCount, trip.Count - branchCellCount).ToArray(),
                machineStampAt, (int[])terminal.Clone(), (Complex[])yGenAt.Clone(), (int[])convBus.Clone(),
                new Dictionary<int, Complex>(vByBus));
            return new Prepared(machines, h, damping, pm, efd, delta0, eqp0, edp0, eqpp0, edpp0, new Complex[0, 0],
                genBuses, null, units.Count > 0 ? units : null, held.Count > 0 ? held : null, net, netF,
                synthetics.Count > 0 ? synthetics : null) { Basis = basis };
        }

        Complex[,] yRed = KronReduction.Reduce(ya!, keep);

        Complex[,]? yRedFault = null;
        if (faultBusId is int fb)
        {
            var yaF = (Complex[,])ya!.Clone();
            int fi = busIndex[fb];
            yaF[fi, fi] += new Complex(BoltedFaultShuntPu, 0.0);   // near short-circuit to ground, as ClassicalInit
            yRedFault = KronReduction.Reduce(yaF, keep);
        }

        return new Prepared(machines, h, damping, pm, efd, delta0, eqp0, edp0, eqpp0, edpp0, yRed, genBuses, yRedFault,
            units.Count > 0 ? units : null, held.Count > 0 ? held : null, IslandReferences: synthetics.Count > 0 ? synthetics : null);
    }

    /// <summary>
    /// WECC: resolve a unit's plant controller (REPC_A) measurement points against the case: the regulated bus (0 = the
    /// unit's own terminal) and the flow branch (BUS1 to BUS2, by its resolved index or, when none was resolved, the one
    /// in-service branch between the two buses), with the branch's own pi-model stamp oriented from BUS1. Null when the
    /// unit has no plant controller. A bus the case does not hold, a branch that does not join BUS1 and BUS2, an
    /// out-of-service branch or an ambiguous pair of parallel branches is refused, naming it.
    /// </summary>
    private static Wecc.WeccMeasurement? WeccMeasurementOf(GridModel model, Wecc.WeccPlant plant, int unitBus,
        Dictionary<int, int> busIndex, Dictionary<int, Complex> vByBus)
    {
        if (plant.Repc is not { } rp) return null;
        string who = $"WECC unit at bus {unitBus} ({plant.Chain})";
        Complex V(int bus) => vByBus.TryGetValue(bus, out Complex v) ? v : Complex.One;
        int reg = rp.RegulatedBus == unitBus ? 0 : rp.RegulatedBus;
        if (reg != 0 && !busIndex.ContainsKey(reg))
            throw new ArgumentException($"{who}: REPC_A regulated bus {reg} is not a bus of the case.");
        if (rp.FlowFromBus == 0)
            return new Wecc.WeccMeasurement(reg, 0, 0, Complex.Zero, Complex.Zero, reg != 0 ? V(reg) : Complex.Zero, Complex.Zero, Complex.Zero);
        int f = rp.FlowFromBus, t = rp.FlowToBus;
        foreach (int b in new[] { f, t })
            if (!busIndex.ContainsKey(b)) throw new ArgumentException($"{who}: REPC_A flow branch bus {b} is not a bus of the case.");
        Branch? br = null;
        if (rp.FlowBranchIndex >= 0)
        {
            if (rp.FlowBranchIndex >= model.Branches.Count) throw new ArgumentException($"{who}: REPC_A flow branch index {rp.FlowBranchIndex} is outside the case's branches.");
            br = model.Branches[rp.FlowBranchIndex];
        }
        else
        {
            var joining = model.Branches.Where(b => b.InService && ((b.From == f && b.To == t) || (b.From == t && b.To == f))).ToList();
            if (joining.Count != 1)
                throw new ArgumentException($"{who}: REPC_A flow branch {f}-{t}: the case has {joining.Count} in-service branches between those buses; exactly one is needed.");
            br = joining[0];
        }
        if (!((br.From == f && br.To == t) || (br.From == t && br.To == f)))
            throw new ArgumentException($"{who}: REPC_A flow branch {br.From}-{br.To} does not join BUS1 = {f} and BUS2 = {t}.");
        if (!br.InService) throw new ArgumentException($"{who}: REPC_A flow branch {br.From}-{br.To} is out of service.");
        PowerFlow.BranchStamp st = PowerFlow.AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
        // The current leaving BUS1 into the branch: Yff V(from) + Yft V(to) at the branch's from end, Ytt V(to) + Ytf V(from)
        // at its to end.
        (Complex y11, Complex y12) = br.From == f ? (st.Yff, st.Yft) : (st.Ytt, st.Ytf);
        return new Wecc.WeccMeasurement(reg, f, t, y11, y12, reg != 0 ? V(reg) : Complex.Zero, V(f), V(t));
    }

    /// <summary>Inertia (s, on the system base) of a synthetic island reference - effectively an infinite bus.</summary>
    public const double IslandReferenceH = 1e4;

    /// <summary>Relative tolerance on |X''d - X''q| (R250): the two must be equal for the round-rotor network
    /// fold; this only absorbs representation rounding of values authored as equal.</summary>
    public const double SubtransientSaliencyRelativeTolerance = 1e-9;

    /// <summary>Reactance (pu, system base) of a synthetic island reference: stiff, and flat across the axes so
    /// it has no flux dynamics.</summary>
    public const double IslandReferenceX = 1e-3;

    // ---- Named literals (October 2026 review, WS13 R169). Each value is unchanged.

    /// <summary>Open-circuit time constants (s) of a synthetic island reference. Its reactances are flat
    /// (<see cref="IslandReferenceX"/>) so these never shape a trajectory; they are typical round-rotor values
    /// (T'do 8 s, T''do 0.03 s, T'qo 0.4 s, T''qo 0.05 s) so the machine is well posed.</summary>
    public const double IslandReferenceTdop = 8.0;
    /// <summary>See <see cref="IslandReferenceTdop"/>.</summary>
    public const double IslandReferenceTdopp = 0.03;
    /// <summary>See <see cref="IslandReferenceTdop"/>.</summary>
    public const double IslandReferenceTqop = 0.4;
    /// <summary>See <see cref="IslandReferenceTdop"/>.</summary>
    public const double IslandReferenceTqopp = 0.05;

    /// <summary>Shunt admittance (pu, system base) stamped at the faulted bus to model a bolted three-phase fault:
    /// a near short-circuit to ground. The same value is used by the classical model's ClassicalInit (which lives
    /// in the deferred Distributed area and still carries its own literal; recorded in NOTES-FOR-MAIN.md).</summary>
    public const double BoltedFaultShuntPu = 1e7;

    /// <summary>Floor (pu^2) on |V|^2 when a constant-power load or generator injection is turned into a
    /// constant admittance S*/|V|^2.</summary>
    public const double MinVoltageSquaredPu = 1e-9;

    /// <summary>Floor (MVA) on a machine's MBase when it is used as its reactive-sharing weight (no finite Q range).</summary>
    public const double MinMBaseWeightMva = 1e-9;

    /// <summary>A bus whose scheduled machine output is at or below this (MW) shares its injection equally between
    /// its machines instead of in proportion to schedule.</summary>
    public const double MinScheduledPowerMw = 1e-9;

    /// <summary>For each connected island (over in-service branches) containing none of
    /// <paramref name="machineBuses"/>, its slack bus - else its first PV bus - where a reference is needed.
    /// Islands with neither have no voltage-controlled bus to anchor and are left as they are.</summary>
    private static List<int> IslandReferenceBuses(GridModel model, Dictionary<int, int> busIndex, IEnumerable<int> machineBuses)
    {
        int n = model.BusCount;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            if (!busIndex.TryGetValue(br.From, out int a) || !busIndex.TryGetValue(br.To, out int b)) continue;
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }
        var covered = new HashSet<int>();
        foreach (int bus in machineBuses) if (busIndex.TryGetValue(bus, out int i)) covered.Add(Find(i));

        var anchor = new Dictionary<int, (int Bus, int Rank)>();   // island root -> best anchor (slack 0, PV 1)
        foreach (Bus bus in model.Buses)
        {
            int root = Find(busIndex[bus.Id]);
            if (covered.Contains(root)) continue;
            int rank = bus.Type == BusType.Slack ? 0 : bus.Type == BusType.Pv ? 1 : 2;
            if (rank == 2) continue;
            if (!anchor.TryGetValue(root, out var cur) || rank < cur.Rank) anchor[root] = (bus.Id, rank);
        }
        return anchor.Values.Select(a => a.Bus).OrderBy(b => b).ToList();
    }
}
