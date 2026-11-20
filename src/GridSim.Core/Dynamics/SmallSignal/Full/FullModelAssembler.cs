using System.Globalization;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.SmallSignal.Full;

/// <summary>The rotor model of a machine in the full small-signal model.</summary>
public enum FullMachineModel
{
    /// <summary>Classical: constant EMF behind X'd, states delta and omega (the model <c>modal</c> uses).</summary>
    Classical,
    /// <summary>One-axis flux decay (Heffron-Phillips, third order): states delta, omega and E'q, saliency Xq != X'd,
    /// armature resistance neglected.</summary>
    FluxDecay,
}

/// <summary>What kind of state a full-model state is (the grouping of participation).</summary>
public enum FullStateCategory
{
    /// <summary>Rotor angle.</summary>
    MachineAngle,
    /// <summary>Rotor speed.</summary>
    MachineSpeed,
    /// <summary>Transient EMF E'q (field flux linkage).</summary>
    MachineFlux,
    /// <summary>A state of the exciter model.</summary>
    Exciter,
    /// <summary>A state of the turbine-governor model.</summary>
    Governor,
    /// <summary>A state of the power-system stabiliser model.</summary>
    Stabiliser,
}

/// <summary>One labelled state of the full model.</summary>
/// <param name="Index">Position in the state vector.</param>
/// <param name="Category">Machine, exciter, governor or stabiliser.</param>
/// <param name="OwnerBus">The machine's bus.</param>
/// <param name="Name">Human name.</param>
public readonly record struct FullStateLabel(int Index, FullStateCategory Category, int OwnerBus, string Name);

/// <summary>
/// The dynamic data and controllers of one generator for the full small-signal model. Reactances are per unit on the
/// machine's own base; <see cref="DampingSystemPu"/> is on the system base (the <c>modal --damping</c> convention).
/// </summary>
/// <param name="GeneratorIndex">Index into <c>GridModel.Generators</c>.</param>
/// <param name="Model">Classical or flux decay.</param>
/// <param name="Xdp">Transient reactance X'd (machine base).</param>
/// <param name="Xd">Synchronous reactance Xd (flux decay).</param>
/// <param name="Xq">Quadrature synchronous reactance Xq (flux decay).</param>
/// <param name="Tdop">Open-circuit field time constant T'd0, seconds (flux decay).</param>
/// <param name="DampingSystemPu">Damping D on the system base (power per unit speed).</param>
/// <param name="InfiniteBus">Treat the unit as an ideal voltage source (angle and EMF fixed, no states): the reference of a
/// single-machine-infinite-bus study.</param>
/// <param name="Exciter">An exciter-role declarative model (flux decay machines only).</param>
/// <param name="Governor">A governor-role declarative model.</param>
/// <param name="Stabiliser">A stabiliser-role declarative model, added to the exciter's error junction (needs an exciter).</param>
/// <param name="CodedControllers">Names of coded controllers the data names that R4-08 cannot express (AcExciter, Esst4b,
/// IEEEG1 with rate limits ...): reported as NOT LINEARISED.</param>
public sealed record FullMachineSpec(
    int GeneratorIndex,
    FullMachineModel Model = FullMachineModel.Classical,
    double Xdp = ClassicalInit.DefaultTransientReactancePu,
    double Xd = 0.0,
    double Xq = 0.0,
    double Tdop = 0.0,
    double DampingSystemPu = 0.0,
    bool InfiniteBus = false,
    UserModel? Exciter = null,
    UserModel? Governor = null,
    UserModel? Stabiliser = null,
    IReadOnlyList<string>? CodedControllers = null);

/// <summary>A controller that is not part of the linearised model, and why.</summary>
/// <param name="Bus">The machine's bus.</param>
/// <param name="GeneratorIndex">The generator.</param>
/// <param name="Controller">Controller or model name.</param>
/// <param name="Reason">Why it is not linearised.</param>
public sealed record NotLinearisedController(int Bus, int GeneratorIndex, string Controller, string Reason);

/// <summary>The operating point of one dynamic machine, per unit on the system base (voltages and angles in pu / rad).</summary>
public sealed record FullMachineOperatingPoint(
    int Bus, int GeneratorIndex, FullMachineModel Model, double DeltaRad, double EqPu, double EqpPu, double IdPu, double IqPu,
    double VdPu, double VqPu, double VtPu, double PePu, double EfdPu, double XdSysPu, double XdpSysPu, double XqSysPu, double TdopSeconds,
    double HSystem, double DampingSystemPu);

/// <summary>
/// The assembled full small-signal model: the real state matrix <c>A</c> (rotor angle and speed, E'q, and the exciter, governor
/// and stabiliser states of every linearised controller), the labelled states and the statement of what was and was not
/// linearised.
/// </summary>
public sealed class FullSmallSignalModel
{
    /// <summary>The state matrix.</summary>
    public double[,] A { get; init; } = new double[0, 0];

    /// <summary>Labelled states.</summary>
    public IReadOnlyList<FullStateLabel> States { get; init; } = Array.Empty<FullStateLabel>();

    /// <summary>Nominal frequency (Hz).</summary>
    public double F0 { get; init; }

    /// <summary>The dynamic machines' operating points (angle nodes in state order).</summary>
    public IReadOnlyList<FullMachineOperatingPoint> Machines { get; init; } = Array.Empty<FullMachineOperatingPoint>();

    /// <summary>Controllers named by the data that are NOT part of A (held at their operating output).</summary>
    public IReadOnlyList<NotLinearisedController> NotLinearised { get; init; } = Array.Empty<NotLinearisedController>();

    /// <summary>The linearised controller blocks used (provenance: model id, role, hash, states).</summary>
    public IReadOnlyList<(int Bus, LinearBlockModel Block)> Blocks { get; init; } = Array.Empty<(int, LinearBlockModel)>();

    /// <summary>Statements about the assembly (operating-point limits, assumptions, defaults taken).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>True when there is no flux state and no controller, so the matrix is the classical swing model produced by
    /// <see cref="SmallSignalAssembler.FromSolvedCase"/> itself (bit for bit).</summary>
    public bool DelegatedToClassical { get; init; }

    /// <summary>The classical model when <see cref="DelegatedToClassical"/> (else null).</summary>
    public SmallSignalModel? Classical { get; init; }

    /// <summary>State count.</summary>
    public int Order => States.Count;

    /// <summary>True when every controller the data names is in A.</summary>
    public bool AllControllersLinearised => NotLinearised.Count == 0;
}

/// <summary>
/// R4-12: assembles the full-model small-signal state matrix of a solved case.
/// <para><b>Machines.</b> A classical machine is a constant EMF behind X'd; a flux-decay machine is the one-axis Heffron-Phillips
/// model <c>T'd0 dE'q/dt = Efd - E'q - (Xd - X'd) id</c> with saliency. Both are presented to the network as an internal voltage
/// <c>E_Q</c> on the q axis behind <c>jXq</c> (for the classical machine Xq := X'd), which is exactly the internal-node form
/// <see cref="ClassicalInit"/> reduces, so the network algebra is eliminated by the same Kron reduction that
/// <see cref="SmallSignalAssembler"/> uses, with Xq in place of X'd: <c>E_Q = E'q + (Xq - X'd) id</c>, <c>Pe = E_Q iq</c>. The
/// resulting loop between E_Q and id is linear and is solved in closed form (one dense real solve), not by iteration.</para>
/// <para><b>Controllers.</b> Exciter, governor and stabiliser declarative models are linearised block by block
/// (<see cref="BlockLinearisation"/>) and wired as the coded studies wire them: the exciter reads <c>Vref - Vt + Vs</c> and the
/// speed deviation; the stabiliser reads the speed deviation; the governor reads the speed deviation and its machine-base output
/// is scaled onto the system base. A controller that cannot be linearised is reported in
/// <see cref="FullSmallSignalModel.NotLinearised"/> and held at its operating output; the study result then says INCOMPLETE.</para>
/// <para><b>Limits stated.</b> Armature resistance, magnetic saturation of the machine, sub-transient dynamics and converter
/// plant are not in this model (<c>modal --enable-inverters</c> keeps the converter archetype); loads are constant impedance, as in
/// <see cref="ClassicalInit"/>.</para>
/// </summary>
public static class FullModelAssembler
{
    /// <summary>Assemble. Machines the specs do not name are classical with X'd = <paramref name="defaultXdpPu"/>.</summary>
    /// <exception cref="ArgumentException">An inconsistent spec (unknown generator, exciter on a classical machine ...).</exception>
    public static FullSmallSignalModel Assemble(GridModel model, PowerFlowResult result, IReadOnlyList<FullMachineSpec> specs,
        double defaultXdpPu = ClassicalInit.DefaultTransientReactancePu)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(specs);
        result.RequireConverged("Full-model small-signal assembly");
        int gcount = model.Generators.Count;
        double baseMva = model.BaseMva;
        double f0 = model.FrequencyHz;
        double ws = 2.0 * Math.PI * f0;

        // ---- specs --------------------------------------------------------------------------------------------------
        var specByGen = new FullMachineSpec?[gcount];
        foreach (FullMachineSpec s in specs)
        {
            if (s.GeneratorIndex < 0 || s.GeneratorIndex >= gcount)
                throw new ArgumentException($"Spec names generator {s.GeneratorIndex}, but the model has {gcount}.", nameof(specs));
            if (specByGen[s.GeneratorIndex] is not null)
                throw new ArgumentException($"Generator {s.GeneratorIndex} is given two specs.", nameof(specs));
            ValidateSpec(model, s);
            specByGen[s.GeneratorIndex] = s;
        }

        var notes = new List<string>();
        var notLin = new List<NotLinearisedController>();
        foreach (FullMachineSpec s in specs)
            foreach (string coded in s.CodedControllers ?? Array.Empty<string>())
                notLin.Add(new NotLinearisedController(model.Generators[s.GeneratorIndex].Bus, s.GeneratorIndex, coded,
                    "a coded controller that the declarative model catalogue cannot express (it reads quantities or has limits outside the block vocabulary); it is not linearised and not approximated."));

        // ---- the network reduction (ClassicalInit, with Xq as the internal reactance) -------------------------------------
        var working = new Generator[gcount];
        var xArr = new double[gcount];
        for (int k = 0; k < gcount; k++)
        {
            Generator gen = model.Generators[k];
            FullMachineSpec? s = specByGen[k];
            if (s is not null && s.InfiniteBus)
                gen = gen with { PowerFlowOnly = false, H = gen.H > 0.0 ? gen.H : 1.0 };
            working[k] = gen;
            xArr[k] = s is null ? defaultXdpPu : s.Model == FullMachineModel.FluxDecay ? s.Xq : s.Xdp;
        }
        GridModel work = model.CopyWith(generators: working);
        ClassicalInit.Prepared prep = ClassicalInit.FromSolvedCase(work, result, xArr, holdNonMachines: true);
        int gm = prep.H.Length;

        // unspecified machines are classical with the default X'd (said once)
        var unspecified = new List<int>();
        for (int k = 0; k < gm; k++)
            if (specByGen[prep.MachineIndex[k]] is null) unspecified.Add(prep.GeneratorBuses[k]);
        if (unspecified.Count > 0)
            notes.Add($"machine(s) at bus {string.Join(", ", unspecified)} have no spec: classical with X'd = {defaultXdpPu.ToString("R", CultureInfo.InvariantCulture)} pu (machine base), no controllers.");

        var dyn = new List<int>();   // prepared index of each dynamic (non-infinite) machine
        for (int k = 0; k < gm; k++)
            if (!(specByGen[prep.MachineIndex[k]]?.InfiniteBus ?? false)) dyn.Add(k);
        int g = dyn.Count;
        if (g == 0) throw new ArgumentException("Every machine is an infinite bus; there is nothing to linearise.", nameof(specs));

        // ---- delegation: no flux, no linearisable controller, no infinite bus -> the incumbent's own matrix -----------------
        bool anyFlux = false, anyBlock = false, anyInfinite = gm != g;
        foreach (int k in dyn)
        {
            FullMachineSpec? s = specByGen[prep.MachineIndex[k]];
            if (s is null) continue;
            if (s.Model == FullMachineModel.FluxDecay) anyFlux = true;
            if (s.Exciter is not null || s.Governor is not null || s.Stabiliser is not null) anyBlock = true;
        }
        if (!anyFlux && !anyBlock && !anyInfinite)
        {
            var damping = new double[gcount];
            for (int k = 0; k < gcount; k++) damping[k] = specByGen[k]?.DampingSystemPu ?? 0.0;
            SmallSignalModel classical = SmallSignalAssembler.FromSolvedCase(model, result, xArr, damping);
            var labels = new List<FullStateLabel>(classical.Order);
            foreach (StateLabel sl in classical.States)
                labels.Add(new FullStateLabel(sl.Index, sl.Kind == StateKind.SwingAngle ? FullStateCategory.MachineAngle : FullStateCategory.MachineSpeed, sl.OwnerId, sl.Name));
            notes.Add("no flux state and no linearisable controller: the matrix is SmallSignalAssembler.FromSolvedCase itself (the classical swing model).");
            var ops = new List<FullMachineOperatingPoint>();
            foreach (int k in dyn) ops.Add(OperatingPoint(model, prep, k, specByGen, baseMva));
            return new FullSmallSignalModel
            {
                A = classical.A, States = labels, F0 = f0, Machines = ops, NotLinearised = notLin, Notes = notes,
                DelegatedToClassical = true, Classical = classical,
            };
        }

        // ---- operating point ---------------------------------------------------------------------------------------------
        var delta = new double[gm]; var eq = new double[gm];
        var ejd = new Complex[gm];
        for (int k = 0; k < gm; k++)
        {
            delta[k] = prep.Delta0[k]; eq[k] = prep.Eq[k];
            (double sn, double cs) = DetMath.SinCos(delta[k]);
            ejd[k] = new Complex(cs, sn);
        }
        var zOp = new Complex[g];
        var id0 = new double[g]; var iq0 = new double[g];
        for (int a = 0; a < g; a++)
        {
            int i = dyn[a];
            Complex cur = Complex.Zero;
            for (int k = 0; k < gm; k++) cur += prep.YReduced[i, k] * (eq[k] * ejd[k]);
            zOp[a] = cur * Complex.Conjugate(ejd[i]);
            iq0[a] = zOp[a].Real;
            id0[a] = -zOp[a].Imaginary;
        }

        // machine constants on the system base
        var spec = new FullMachineSpec[g];
        var ratio = new double[g];      // baseMva / machine base: reactance machine -> system
        var xdS = new double[g]; var xdpS = new double[g]; var xqS = new double[g]; var dx = new double[g];
        var tdop = new double[g]; var h = new double[g]; var dmp = new double[g]; var eqp0 = new double[g]; var efd0 = new double[g];
        var fluxIndex = new int[g];     // -1 for a classical machine, else 0.. in machine order
        int fluxCount = 0;
        for (int a = 0; a < g; a++)
        {
            int k = dyn[a];
            int gi = prep.MachineIndex[k];
            spec[a] = specByGen[gi] ?? new FullMachineSpec(gi, FullMachineModel.Classical, defaultXdpPu);
            double mb = MachineBase.EffectiveMva(model.Generators[gi], baseMva);
            ratio[a] = baseMva / mb;
            h[a] = prep.H[k];
            dmp[a] = spec[a].DampingSystemPu;
            xdpS[a] = spec[a].Xdp * ratio[a];
            if (spec[a].Model == FullMachineModel.FluxDecay)
            {
                xdS[a] = spec[a].Xd * ratio[a];
                xqS[a] = spec[a].Xq * ratio[a];
                dx[a] = xqS[a] - xdpS[a];
                tdop[a] = spec[a].Tdop;
                fluxIndex[a] = fluxCount++;
                eqp0[a] = eq[k] - dx[a] * id0[a];
                efd0[a] = eqp0[a] + (xdS[a] - xdpS[a]) * id0[a];
            }
            else
            {
                xqS[a] = xdpS[a];
                dx[a] = 0.0;
                fluxIndex[a] = -1;
                eqp0[a] = eq[k];
            }
        }

        // ---- the algebra: dE_Q = S_E dE'q + S_d d(delta); ids, iqs, Pe, Vt in the same variables ------------------------------
        var rc = new double[g, g]; var ic = new double[g, g];
        for (int a = 0; a < g; a++)
            for (int b = 0; b < g; b++)
            {
                Complex c = prep.YReduced[dyn[a], dyn[b]] * ejd[dyn[b]] * Complex.Conjugate(ejd[dyn[a]]);
                rc[a, b] = c.Real; ic[a, b] = c.Imaginary;
            }
        var eqD = new double[g];
        for (int a = 0; a < g; a++) eqD[a] = eq[dyn[a]];

        var aidE = new double[g, g]; var aidD = new double[g, g]; var aiqE = new double[g, g]; var aiqD = new double[g, g];
        for (int a = 0; a < g; a++)
            for (int b = 0; b < g; b++)
            {
                aidE[a, b] = -ic[a, b];
                aidD[a, b] = -rc[a, b] * eqD[b] + (a == b ? iq0[a] : 0.0);
                aiqE[a, b] = rc[a, b];
                aiqD[a, b] = -ic[a, b] * eqD[b] + (a == b ? -id0[a] : 0.0);
            }
        var mm = new double[g, g];
        var rhs = new double[g, 2 * g];
        for (int a = 0; a < g; a++)
        {
            for (int b = 0; b < g; b++)
            {
                mm[a, b] = (a == b ? 1.0 : 0.0) - dx[a] * aidE[a, b];
                rhs[a, b] = dx[a] * aidD[a, b];
            }
            rhs[a, g + a] = 1.0;
        }
        double[,] sol = RealLinearAlgebra.SolveMany(mm, rhs, g, 2 * g);
        var sD = new double[g, g]; var sE = new double[g, g];
        for (int a = 0; a < g; a++) for (int b = 0; b < g; b++) { sD[a, b] = sol[a, b]; sE[a, b] = sol[a, g + b]; }
        double[,] idD = Add(Mul(aidE, sD, g), aidD, g), idE = Mul(aidE, sE, g);
        double[,] iqD = Add(Mul(aiqE, sD, g), aiqD, g), iqE = Mul(aiqE, sE, g);
        var peD = new double[g, g]; var peE = new double[g, g]; var vtD = new double[g, g]; var vtE = new double[g, g];
        var vd0 = new double[g]; var vq0 = new double[g]; var vt0 = new double[g]; var pe0 = new double[g];
        for (int a = 0; a < g; a++)
        {
            vd0[a] = xqS[a] * iq0[a];
            vq0[a] = eqD[a] - xqS[a] * id0[a];
            vt0[a] = Math.Sqrt(vd0[a] * vd0[a] + vq0[a] * vq0[a]);
            pe0[a] = eqD[a] * iq0[a];
            for (int b = 0; b < g; b++)
            {
                peD[a, b] = iq0[a] * sD[a, b] + eqD[a] * iqD[a, b];
                peE[a, b] = iq0[a] * sE[a, b] + eqD[a] * iqE[a, b];
                vtD[a, b] = (vd0[a] * xqS[a] * iqD[a, b] + vq0[a] * (sD[a, b] - xqS[a] * idD[a, b])) / vt0[a];
                vtE[a, b] = (vd0[a] * xqS[a] * iqE[a, b] + vq0[a] * (sE[a, b] - xqS[a] * idE[a, b])) / vt0[a];
            }
        }

        // ---- controllers ----------------------------------------------------------------------------------------------------
        var exc = new LinearBlockModel?[g]; var pss = new LinearBlockModel?[g]; var gov = new LinearBlockModel?[g];
        var blocks = new List<(int, LinearBlockModel)>();
        for (int a = 0; a < g; a++)
        {
            int bus = prep.GeneratorBuses[dyn[a]];
            int gi = spec[a].GeneratorIndex;
            if (spec[a].Exciter is { } em)
                exc[a] = TryLinearise(em, new[] { efd0[a] }, bus, gi, "exciter", notLin, notes);
            if (spec[a].Stabiliser is { } pm)
            {
                if (spec[a].Exciter is null)
                    throw new ArgumentException($"Generator {gi} has a stabiliser but no exciter to add it to.", nameof(specs));
                pss[a] = TryLinearise(pm, new[] { 0.0 }, bus, gi, "stabiliser", notLin, notes);
            }
            if (spec[a].Governor is { } gmod)
                gov[a] = TryLinearise(gmod, new[] { pe0[a] * ratio[a] }, bus, gi, "governor", notLin, notes);
            foreach (LinearBlockModel? b in new[] { exc[a], pss[a], gov[a] })
                if (b is not null) { blocks.Add((bus, b)); foreach (string nt in b.Notes) notes.Add($"bus {bus} {b.ModelId}: {nt}"); }
            // a stabiliser whose exciter is not linearised has nowhere to act
            if (pss[a] is not null && exc[a] is null) pss[a] = null;
        }
        // the governor's machine-base pm0 is the system-base power times (Base / MBase)
        for (int a = 0; a < g; a++)
        {
            if (gov[a] is null || spec[a].Governor is null) continue;
            double pmMachine = pe0[a] * ratio[a];    // system-base power x (Base / MBase) = machine-base per unit
            if (Math.Abs(gov[a]!.Y0[0] - pmMachine) > 1e-6 * (1.0 + Math.Abs(pmMachine)))
                throw new InvalidOperationException($"internal consistency: governor output {gov[a]!.Y0[0]} differs from the machine-base power {pmMachine}.");
        }

        // ---- state numbering ----------------------------------------------------------------------------------------------
        var labelsF = new List<FullStateLabel>();
        for (int a = 0; a < g; a++) labelsF.Add(new FullStateLabel(a, FullStateCategory.MachineAngle, prep.GeneratorBuses[dyn[a]], $"d.delta_gen{prep.GeneratorBuses[dyn[a]]}"));
        for (int a = 0; a < g; a++) labelsF.Add(new FullStateLabel(g + a, FullStateCategory.MachineSpeed, prep.GeneratorBuses[dyn[a]], $"d.omega_gen{prep.GeneratorBuses[dyn[a]]}"));
        var fluxCol = new int[g];
        for (int a = 0; a < g; a++)
            if (fluxIndex[a] >= 0)
            {
                fluxCol[a] = labelsF.Count;
                labelsF.Add(new FullStateLabel(labelsF.Count, FullStateCategory.MachineFlux, prep.GeneratorBuses[dyn[a]], $"d.Eqp_gen{prep.GeneratorBuses[dyn[a]]}"));
            }
        var excOff = new int[g]; var pssOff = new int[g]; var govOff = new int[g];
        for (int a = 0; a < g; a++)
        {
            int bus = prep.GeneratorBuses[dyn[a]];
            excOff[a] = labelsF.Count;
            if (exc[a] is { } e) foreach (string nm in e.StateNames) labelsF.Add(new FullStateLabel(labelsF.Count, FullStateCategory.Exciter, bus, $"exc_{e.ModelId}.{nm}_gen{bus}"));
            pssOff[a] = labelsF.Count;
            if (pss[a] is { } p) foreach (string nm in p.StateNames) labelsF.Add(new FullStateLabel(labelsF.Count, FullStateCategory.Stabiliser, bus, $"pss_{p.ModelId}.{nm}_gen{bus}"));
            govOff[a] = labelsF.Count;
            if (gov[a] is { } gv) foreach (string nm in gv.StateNames) labelsF.Add(new FullStateLabel(labelsF.Count, FullStateCategory.Governor, bus, $"gov_{gv.ModelId}.{nm}_gen{bus}"));
        }
        int n = labelsF.Count;
        var A = new double[n, n];

        // ---- rows --------------------------------------------------------------------------------------------------------
        for (int a = 0; a < g; a++)
        {
            A[a, g + a] = ws;                                           // d(delta)/dt = ws * d(omega)
            double inv2h = 1.0 / (2.0 * h[a]);
            for (int b = 0; b < g; b++)
            {
                A[g + a, b] -= peD[a, b] * inv2h;
                if (fluxIndex[b] >= 0) A[g + a, fluxCol[b]] -= peE[a, b] * inv2h;
            }
            A[g + a, g + a] += -dmp[a] * inv2h;
            if (gov[a] is { } gb)
            {
                // dPm_sys = (MBase/Base) * (Cg xg + Dg dOmega)
                double sc = inv2h / ratio[a];
                for (int s = 0; s < gb.StateCount; s++) A[g + a, govOff[a] + s] += sc * gb.C[0, s];
                A[g + a, g + a] += sc * gb.D[0, 0];
                for (int s = 0; s < gb.StateCount; s++)
                {
                    for (int t = 0; t < gb.StateCount; t++) A[govOff[a] + s, govOff[a] + t] += gb.A[s, t];
                    A[govOff[a] + s, g + a] += gb.B[s, 0];
                }
            }
        }
        for (int a = 0; a < g; a++)
        {
            // terminal-voltage row (shared by flux and exciter rows): dVt = sum vtD d(delta) + sum vtE dE'q
            // exciter chain input: verr_total = -dVt + dVs ;  dVs = Cp xp + Dp d(omega)
            LinearBlockModel? e = exc[a], p = pss[a];
            // helper to add  coeff * (row of dVt) to a target row
            void AddVt(int row, double coeff)
            {
                for (int b = 0; b < g; b++)
                {
                    A[row, b] += coeff * vtD[a, b];
                    if (fluxIndex[b] >= 0) A[row, fluxCol[b]] += coeff * vtE[a, b];
                }
            }
            void AddVs(int row, double coeff)
            {
                if (p is null) return;
                for (int s = 0; s < p.StateCount; s++) A[row, pssOff[a] + s] += coeff * p.C[0, s];
                A[row, g + a] += coeff * p.D[0, 0];
            }
            if (e is not null)
            {
                for (int s = 0; s < e.StateCount; s++)
                {
                    int row = excOff[a] + s;
                    for (int t = 0; t < e.StateCount; t++) A[row, excOff[a] + t] += e.A[s, t];
                    AddVt(row, -e.B[s, 0]);
                    AddVs(row, e.B[s, 0]);
                    A[row, g + a] += e.B[s, 1];
                }
            }
            if (p is not null)
            {
                for (int s = 0; s < p.StateCount; s++)
                {
                    int row = pssOff[a] + s;
                    for (int t = 0; t < p.StateCount; t++) A[row, pssOff[a] + t] += p.A[s, t];
                    A[row, g + a] += p.B[s, 0];
                }
            }
            if (fluxIndex[a] >= 0)
            {
                int row = fluxCol[a];
                double invT = 1.0 / tdop[a];
                double xdd = xdS[a] - xdpS[a];
                A[row, row] += -invT;                                       // - E'q
                for (int b = 0; b < g; b++)
                {
                    A[row, b] += -invT * xdd * idD[a, b];                   // - (Xd - X'd) id
                    if (fluxIndex[b] >= 0) A[row, fluxCol[b]] += -invT * xdd * idE[a, b];
                }
                if (e is not null)
                {
                    // dEfd = Ce xe + De0 (-dVt + dVs) + De1 d(omega)
                    for (int s = 0; s < e.StateCount; s++) A[row, excOff[a] + s] += invT * e.C[0, s];
                    AddVt(row, -invT * e.D[0, 0]);
                    AddVs(row, invT * e.D[0, 0]);
                    A[row, g + a] += invT * e.D[0, 1];
                }
            }
        }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!double.IsFinite(A[i, j])) throw new InvalidOperationException($"The assembled state matrix has a non-finite entry at ({i}, {j}).");

        var opsAll = new List<FullMachineOperatingPoint>(g);
        for (int a = 0; a < g; a++)
        {
            int bus = prep.GeneratorBuses[dyn[a]];
            opsAll.Add(new FullMachineOperatingPoint(bus, spec[a].GeneratorIndex, spec[a].Model, delta[dyn[a]], eq[dyn[a]], eqp0[a], id0[a], iq0[a],
                vd0[a], vq0[a], vt0[a], pe0[a], spec[a].Model == FullMachineModel.FluxDecay ? efd0[a] : double.NaN, xdS[a], xdpS[a], xqS[a], tdop[a], h[a], dmp[a]));
        }
        if (anyInfinite) notes.Add("infinite-bus unit(s): ideal voltage source(s) at the solved EMF, no states.");
        return new FullSmallSignalModel
        {
            A = A, States = labelsF, F0 = f0, Machines = opsAll, NotLinearised = notLin, Blocks = blocks, Notes = notes,
        };
    }

    private static FullMachineOperatingPoint OperatingPoint(GridModel model, ClassicalInit.Prepared prep, int k, FullMachineSpec?[] specByGen, double baseMva)
    {
        int gi = prep.MachineIndex[k];
        double mb = MachineBase.EffectiveMva(model.Generators[gi], baseMva);
        double ratio = baseMva / mb;
        FullMachineSpec? s = specByGen[gi];
        double xdp = (s?.Xdp ?? ClassicalInit.DefaultTransientReactancePu) * ratio;
        return new FullMachineOperatingPoint(prep.GeneratorBuses[k], gi, FullMachineModel.Classical, prep.Delta0[k], prep.Eq[k], prep.Eq[k], double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, xdp, xdp, 0.0, prep.H[k], s?.DampingSystemPu ?? 0.0);
    }

    private static void ValidateSpec(GridModel model, FullMachineSpec s)
    {
        Generator gen = model.Generators[s.GeneratorIndex];
        string who = $"generator {s.GeneratorIndex} (bus {gen.Bus})";
        if (!(s.Xdp > 0.0) || !double.IsFinite(s.Xdp)) throw new ArgumentException($"{who}: X'd must be a finite number above 0.");
        if (!double.IsFinite(s.DampingSystemPu)) throw new ArgumentException($"{who}: damping must be finite.");
        if (s.Model == FullMachineModel.FluxDecay)
        {
            if (!(s.Xd >= s.Xdp) || !double.IsFinite(s.Xd)) throw new ArgumentException($"{who}: a flux-decay machine needs Xd >= X'd (Xd = {s.Xd}, X'd = {s.Xdp}).");
            if (!(s.Xq > 0.0) || !double.IsFinite(s.Xq)) throw new ArgumentException($"{who}: a flux-decay machine needs Xq above 0.");
            if (!(s.Tdop > 0.0) || !double.IsFinite(s.Tdop)) throw new ArgumentException($"{who}: a flux-decay machine needs T'd0 above 0.");
        }
        if (s.Exciter is not null)
        {
            if (s.Model != FullMachineModel.FluxDecay) throw new ArgumentException($"{who}: an exciter needs a flux-decay machine (a classical machine has no field to regulate).");
            if (s.Exciter.Role != "exciter") throw new ArgumentException($"{who}: model {s.Exciter.Id} has role '{s.Exciter.Role}', not 'exciter'.");
        }
        if (s.Governor is not null && s.Governor.Role != "governor") throw new ArgumentException($"{who}: model {s.Governor.Id} has role '{s.Governor.Role}', not 'governor'.");
        if (s.Stabiliser is not null && s.Stabiliser.Role != "stabiliser") throw new ArgumentException($"{who}: model {s.Stabiliser.Id} has role '{s.Stabiliser.Role}', not 'stabiliser'.");
        if (s.InfiniteBus && (s.Exciter is not null || s.Governor is not null || s.Stabiliser is not null || s.Model == FullMachineModel.FluxDecay))
            throw new ArgumentException($"{who}: an infinite bus has no machine dynamics or controllers.");
    }

    private static LinearBlockModel? TryLinearise(UserModel m, double[] init, int bus, int gi, string what, List<NotLinearisedController> notLin, List<string> notes)
    {
        try
        {
            LinearBlockModel b = BlockLinearisation.Linearise(m, init);
            int nIn = what == "exciter" ? 2 : 1;
            if (b.InputCount != nIn || b.OutputCount != 1)
                throw new NotLinearisableException("bad-ports", m.Id, "ports", $"a {what} model must have {nIn} input(s) and 1 output (got {b.InputCount} and {b.OutputCount}).");
            return b;
        }
        catch (NotLinearisableException ex)
        {
            notLin.Add(new NotLinearisedController(bus, gi, $"{m.Id} ({what})", $"[{ex.Code}] {ex.Message}"));
            notes.Add($"bus {bus}: {what} {m.Id} is not linearised ({ex.Code}); held at its operating output.");
            return null;
        }
        catch (ArgumentException ex)
        {
            notLin.Add(new NotLinearisedController(bus, gi, $"{m.Id} ({what})", $"[initialisation-refused] {ex.Message}"));
            notes.Add($"bus {bus}: {what} {m.Id} cannot be initialised at the operating point ({ex.Message}); held at its operating output.");
            return null;
        }
    }

    private static double[,] Mul(double[,] a, double[,] b, int n)
    {
        var c = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double aik = a[i, k];
                if (aik == 0.0) continue;
                for (int j = 0; j < n; j++) c[i, j] += aik * b[k, j];
            }
        return c;
    }

    private static double[,] Add(double[,] a, double[,] b, int n)
    {
        var c = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) c[i, j] = a[i, j] + b[i, j];
        return c;
    }
}

internal static class RealLinearAlgebra
{
    /// <summary>Solve <c>M X = R</c> for several right-hand sides by partial-pivot LU.</summary>
    public static double[,] SolveMany(double[,] m, double[,] r, int n, int cols)
    {
        var a = (double[,])m.Clone();
        var x = (double[,])r.Clone();
        for (int k = 0; k < n; k++)
        {
            int p = k; double best = Math.Abs(a[k, k]);
            for (int i = k + 1; i < n; i++) if (Math.Abs(a[i, k]) > best) { best = Math.Abs(a[i, k]); p = i; }
            if (!(best > 1e-300)) throw new InvalidOperationException("The machine-network algebra is singular (E_Q and id loop).");
            if (p != k)
            {
                for (int j = 0; j < n; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);
                for (int j = 0; j < cols; j++) (x[k, j], x[p, j]) = (x[p, j], x[k, j]);
            }
            for (int i = k + 1; i < n; i++)
            {
                double f = a[i, k] / a[k, k];
                if (f == 0.0) continue;
                for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                for (int j = 0; j < cols; j++) x[i, j] -= f * x[k, j];
            }
        }
        for (int k = n - 1; k >= 0; k--)
            for (int j = 0; j < cols; j++)
            {
                double s = x[k, j];
                for (int c = k + 1; c < n; c++) s -= a[k, c] * x[c, j];
                x[k, j] = s / a[k, k];
            }
        return x;
    }
}
