using System.Globalization;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Opf;

/// <summary>What <see cref="AcOpfExtended.Solve"/> minimises.</summary>
public enum AcOpfObjective
{
    /// <summary>The units' polynomial cost curves, as <see cref="AcOpfInteriorPoint"/>.</summary>
    Cost,
    /// <summary>The active power losses: the sum of every unit's output (MW). The demand is fixed, so this is the losses
    /// plus a constant (a linear cost of 1 per MW on every unit; Wood and Wollenberg, ch. 8).</summary>
    Losses,
    /// <summary>Reactive power optimisation: the losses, with every unit that is not at a slack bus held at its case
    /// output. The controls are the units' reactive output (through the voltages), the taps and the shunts.</summary>
    ReactivePower,
}

/// <summary>The settings of <see cref="AcOpfExtended.Solve"/>.</summary>
public sealed record AcOpfExtendedOptions
{
    /// <summary>The objective (default <see cref="AcOpfObjective.Cost"/>).</summary>
    public AcOpfObjective Objective { get; init; } = AcOpfObjective.Cost;

    /// <summary>The interior point settings, or null for <see cref="AcOpfExtended.DefaultIpm"/> of the objective.</summary>
    public AcOpfIpmOptions? Ipm { get; init; }

    /// <summary>Continuous controls (taps and shunts) to optimise, or null for none.</summary>
    public OpfControlSet? Controls { get; init; }

    /// <summary>Fix every control at these values instead of optimising it: one per control in variable order (taps as
    /// the ratio tau, shunts in MVAr, phase-shifter angles in degrees). Used by the discrete rounding re-solve
    /// (<see cref="OpfRounding"/>).</summary>
    public IReadOnlyList<double>? FixedControlValues { get; init; }
}

/// <summary>A continuous control at the optimum. A tap's <see cref="Value"/>, <see cref="Min"/> and <see cref="Max"/> are
/// ratios (tau, on the from side); a shunt's are MVAr at 1 pu (capacitive positive); a phase shifter's ("pst") are
/// degrees. <see cref="MuMin"/> and
/// <see cref="MuMax"/> are the shadow prices of the two limits in objective units per hour per unit of the variable
/// (0 when slack).</summary>
public sealed record OpfControlValue(string Kind, string Element, double Value, double CaseValue, double Min, double Max, double MuMin, double MuMax);

/// <summary>One post-outage network block of an AC-SCOPF: the outage and the state of that block. With
/// <see cref="AcScopfOptions.Contingencies"/> <see cref="Outage"/> is the outaged branch; with
/// <see cref="AcScopfOptions.Outages"/> it is the outage's position in that list and <see cref="OutageLabel"/> names it.</summary>
public sealed record AcOpfBlockResult(
    int Outage, double TotalGenerationMw, IReadOnlyList<AcOpfBusResult> Buses, IReadOnlyList<AcOpfGenResult> Generators,
    IReadOnlyList<AcOpfBranchResult> Branches, IReadOnlyList<AcOpfBinding> Binding)
{
    /// <summary>The outage's label (<see cref="ScopfOutage.Label"/>) when the problem was given
    /// <see cref="AcScopfOptions.Outages"/>; null with <see cref="AcScopfOptions.Contingencies"/>.</summary>
    public string? OutageLabel { get; init; }
}

/// <summary>The result of <see cref="AcOpfExtended.Solve"/>. <see cref="Base"/> is the base-case network in the record
/// of <see cref="AcOpfInteriorPoint"/> (status, iterations, objective, buses, units, branches, binding limits and the
/// log). When it did not converge every number is the state the iteration stopped in, never an optimum.</summary>
public sealed record AcOpfExtendedResult(
    AcOpfObjective Objective,
    AcOpfIpmResult Base,
    IReadOnlyList<OpfControlValue> Controls,
    IReadOnlyList<AcOpfBlockResult> Outages,
    IReadOnlyList<AcOpfBinding> CouplingBinding)
{
    /// <summary>True when the interior point method met all four conditions.</summary>
    public bool Converged => Base.Converged;

    /// <summary>Base-case losses (MW).</summary>
    public double LossesMw => Base.LossesMw;
}

/// <summary>How the post-outage blocks of an AC-SCOPF are coupled to the base case.</summary>
internal enum AcOpfCoupling
{
    /// <summary>Every unit not at a slack bus keeps its base-case output after the outage.</summary>
    Preventive,
    /// <summary>Every unit not at a slack bus may move by at most its ramp after the outage.</summary>
    Corrective,
}

/// <summary>The network blocks and coupling of a stacked (security-constrained) problem: one block per outage (the
/// branches and units it removes), the id each block reports (the branch index for a one-branch contingency, else the
/// outage's list position), and, for outages given as <see cref="ScopfOutage"/> records, their labels.</summary>
internal sealed record AcOpfBlocks(
    IReadOnlyList<ScopfOutage> Outages, IReadOnlyList<int> Ids, AcOpfCoupling Coupling, IReadOnlyList<double>? RampMw, PostFaultRating PostRating)
{
    /// <summary>The label of each block's outage, or null (the outages are branch contingencies, named by index).</summary>
    public IReadOnlyList<string>? Labels { get; init; }
}

/// <summary>
/// The AC optimal power flow of <see cref="AcOpfInteriorPoint"/> (MATPOWER's MIPS, step for step), copied and generalised:
/// <list type="bullet">
/// <item>the objective is the cost, the losses, or the losses with the units off the slack bus held (reactive power
/// optimisation);</item>
/// <item>continuous transformer taps (u = 1/tau on the from side) and bus shunts (b in pu) are extra variables after the
/// units' Q, taps first (Zimmerman, Murillo-Sanchez and Thomas, IEEE Trans. PWRS 26(1), 2011, the MATPOWER branch model:
/// Yff = u^2 (ys + j bc/2), Yft = -u ys e^{j phi}, Ytf = -u ys e^{-j phi}, Ytt = ys + j bc/2);</item>
/// <item>phase-shifter angles phi (radians inside, degrees outside) as further variables after the shunts: the branch
/// keeps its case ratio tau, and phi enters only the transfer terms (Yft = -ys e^{j phi} / tau, Ytf = -ys e^{-j phi} / tau),
/// so the power entering at the from end is that of a fixed branch at the angle difference theta_f - theta_t - phi, and
/// at the to end theta_t - theta_f + phi;</item>
/// <item>several network blocks (a base case and outages of branches and units) stacked in one problem with coupling
/// rows, for <see cref="AcScopf"/>.</item>
/// </list>
/// <para>With one block, no controls and the cost objective, every loop, row and variable is in the order of
/// <see cref="AcOpfInteriorPoint"/>, so the two give the same bits (pinned by the tests). The original is not edited.</para>
/// </summary>
public static class AcOpfExtended
{
    // A bound at or beyond this magnitude is no bound (MIPS's own threshold).
    private const double NoBound = 1e10;

    // MIPS's step-length floor and its multiplier threshold for zeroing a non-binding inequality's multiplier.
    private const double MinStep = 1e-8;
    private const double MuThreshold = 1e-5;

    // Equilibration passes of the KKT system (see SolveScaled); it settles in a few.
    private const int RuizPasses = 10;

    // MATLAB's eps (2^-52): MIPS's test for equal bounds and for a barrier parameter out of range.
    private const double MachineEpsilon = 2.220446049250313e-16;

    /// <summary>The stopping tolerance of the losses and reactive power objectives (feasibility, gradient,
    /// complementarity and cost change).</summary>
    public const double LossTolerance = 1e-12;

    /// <summary>
    /// The interior point settings used when none are given. Cost: MATPOWER's MIPS defaults (tolerances 1e-6), so the
    /// cost objective is <see cref="AcOpfInteriorPoint"/> bit for bit. Losses and reactive power: the same with every
    /// tolerance <see cref="LossTolerance"/>. The objective is scaled by 1e-4 inside the iteration, so with MIPS's 1e-6 the
    /// complementarity test allows a duality gap of up to 1e-6 (1 + |x|inf) scaled, 1e-2 (1 + |x|inf) MW: case9's loss
    /// minimum stopped 1.3e-5 MW above the SciPy optimum. With 1e-12 the bound is 1e-8 (1 + |x|inf) MW, and the barrier
    /// parameter falls by about ten per iteration (sigma 0.1), so the error falls with it (case9: 1.2e-10 MW).
    /// </summary>
    public static AcOpfIpmOptions DefaultIpm(AcOpfObjective objective) => objective == AcOpfObjective.Cost
        ? new AcOpfIpmOptions()
        : new AcOpfIpmOptions
        {
            FeasibilityTolerance = LossTolerance, GradientTolerance = LossTolerance,
            ComplementarityTolerance = LossTolerance, CostTolerance = LossTolerance,
        };

    /// <summary>Solve the AC-OPF of <paramref name="model"/> with the objective and controls of
    /// <paramref name="options"/>. Throws <see cref="InvalidDataException"/> for a model or control set it refuses.</summary>
    public static AcOpfExtendedResult Solve(GridModel model, AcOpfExtendedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        AcOpfExtendedOptions opt = options ?? new AcOpfExtendedOptions();
        AcOpfIpmOptions ipm = opt.Ipm ?? DefaultIpm(opt.Objective);
        ValidateOptions(ipm);
        var p = new Problem(model, opt, null);
        return Run(p, ipm);
    }

    /// <summary>Solve a stacked problem: the base case plus one block per outage, with the coupling rows.</summary>
    internal static AcOpfExtendedResult SolveBlocks(GridModel model, AcOpfExtendedOptions opt, AcOpfBlocks blocks)
    {
        ArgumentNullException.ThrowIfNull(model);
        AcOpfIpmOptions ipm = opt.Ipm ?? DefaultIpm(opt.Objective);
        ValidateOptions(ipm);
        if (opt.Controls is { Count: > 0 })
            throw new InvalidDataException("AC-SCOPF: tap, shunt and phase-shifter controls are not modelled in the security-constrained problem.");
        var p = new Problem(model, opt, blocks);
        return Run(p, ipm);
    }

    private static void ValidateOptions(AcOpfIpmOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(o), o.MaxIterations, "The AC-OPF needs at least one iteration.");
        foreach ((string name, double v) in new[]
        {
            ("feasibility tolerance", o.FeasibilityTolerance), ("gradient tolerance", o.GradientTolerance),
            ("complementarity tolerance", o.ComplementarityTolerance), ("cost tolerance", o.CostTolerance),
            ("cost scale", o.CostScale),
        })
            if (!(v > 0) || !double.IsFinite(v))
                throw new ArgumentOutOfRangeException(nameof(o), v, $"The AC-OPF {name} must be a finite number above 0.");
        if (!(o.StepToBoundary > 0 && o.StepToBoundary < 1))
            throw new ArgumentOutOfRangeException(nameof(o), o.StepToBoundary, "The AC-OPF step-to-boundary fraction must lie strictly between 0 and 1.");
        if (!(o.Centring > 0 && o.Centring < 1))
            throw new ArgumentOutOfRangeException(nameof(o), o.Centring, "The AC-OPF centring parameter must lie strictly between 0 and 1.");
    }

    // ---- The problem data ------------------------------------------------------------------------------------------

    /// <summary>One off-diagonal admittance cell of a bus row: the other bus and G + jB.</summary>
    private readonly record struct Cell(int K, double G, double B);

    /// <summary>A rated branch's flow limit in one block: the end buses (indices), the end's own admittance (Yaa) and the
    /// transfer admittance (Yab) for each end, the square of the limit in pu, and the tap control on it (-1 for none;
    /// with a tap the admittances are those at u = 1 and the u dependence is applied when evaluated).</summary>
    private readonly record struct FlowLimit(
        int Block, int Branch, int F, int T, double Gff, double Bff, double Gft, double Bft, double Gtt, double Btt, double Gtf, double Btf,
        double SmaxPu, double Smax2, int Tap, int Pst = -1);

    /// <summary>A bound row of h: h = Sign * (x[Var] - Limit) &lt;= 0 (Sign +1 an upper bound, -1 a lower bound).</summary>
    private readonly record struct BoundRow(int Var, int Sign, double Limit);

    /// <summary>A linear coupling row Ca x[A] + Cb x[B] (+ the <see cref="Extra"/> terms, in order) - Rhs (= 0 or &lt;= 0).
    /// A row without extra terms is evaluated exactly as before they existed.</summary>
    private readonly record struct LinRow(int A, double Ca, int B, double Cb, double Rhs, int Gen, int Block)
    {
        /// <summary>Further terms (variable, coefficient): the governor-droop share of the units a block's outage removes.</summary>
        public (int Var, double C)[]? Extra { get; init; }

        /// <summary>Ca x[A] + Cb x[B] plus the extra terms (left to right), before the right-hand side.</summary>
        public double Value(double[] x)
        {
            double v = Ca * x[A] + Cb * x[B];
            if (Extra is { } e) foreach ((int j, double c) in e) v += c * x[j];
            return v;
        }

        /// <summary>Every (variable, coefficient) of the row: A, B, then the extra terms.</summary>
        public IEnumerable<(int Var, double C)> Terms()
        {
            yield return (A, Ca);
            yield return (B, Cb);
            if (Extra is { } e) foreach ((int, double) t in e) yield return t;
        }
    }

    /// <summary>A tap control: the branch, its end buses, the stamp at u = 1 (tau = 1, the case phase shift), the
    /// variable, the ratio range and step, and the case ratio.</summary>
    private sealed record TapCtl(int Branch, string Name, int F, int T, double Gff, double Bff, double Gft, double Bft,
        double Gtt, double Btt, double Gtf, double Btf, int Var, double MinTap, double MaxTap, double CaseTap);

    /// <summary>A shunt control at bus index <see cref="Bus"/>.</summary>
    private sealed record ShuntCtl(int Bus, int BusId, int Var, double BMinMvar, double BMaxMvar, double InitialMvar);

    /// <summary>A phase-shifter angle control: the branch, its end buses, the stamp at the case ratio and zero shift, the
    /// variable (phi in radians), the range in degrees and the case angle in degrees.</summary>
    private sealed record PstCtl(int Branch, string Name, int F, int T, double Gff, double Bff, double Gft, double Bft,
        double Gtt, double Btt, double Gtf, double Btf, int Var, double MinDeg, double MaxDeg, double CaseDeg);

    /// <summary>One network block: the diagonal and off-diagonal admittance of each bus row, and the branch out.</summary>
    private sealed class Net
    {
        public required double[] Gd, Bd;
        public required Cell[][] Row;
        public required int Outage;              // the id the block reports (-1 for the base case)
        public required GridModel Model;
        public bool[]? BranchOut;                // the branches the block's outage removes (null: none)
        public bool[]? GenOut;                   // the units the block's outage removes (null: none)
        public string? Label;                    // the outage's label (null: a branch contingency, named by index)
        public AcScopf.OutageResponse? Response; // a unit outage: who balances the block and each unit's droop share (null: none)

        public bool IsOut(int k) => BranchOut is not null && BranchOut[k];
        public bool UnitOut(int g) => GenOut is not null && GenOut[g];
    }

    private sealed class Problem
    {
        public readonly GridModel Model;
        public readonly AcOpfObjective Objective;
        public readonly int Nb, Ng, K, NxB, Nx;
        public readonly double Base;
        public readonly Net[] Nets;
        public readonly double[] PdPu, QdPu;
        public readonly int[] GenBus;           // bus index of each generator
        public readonly bool[] GenAtSlack;
        public readonly double[][]? Poly;       // each generator's polynomial, highest order first (Cost only)
        public readonly FlowLimit[] Flows;
        public readonly TapCtl[] Taps;
        public readonly ShuntCtl[] Shunts;
        public readonly PstCtl[] Psts;
        public readonly double[] XMin, XMax;
        public readonly BoundRow[] Bounds;      // MIPS order: upper-only, lower-only, then both (all uppers, then all lowers)
        public readonly (int Var, double Value)[] Fixed;   // variables with equal bounds: linear equality rows
        public readonly LinRow[] LinEq, LinIq;
        public readonly int RefBus;             // the first slack bus (its angle starts every angle)

        public int Va(int b, int i) => b * NxB + i;
        public int Vm(int b, int i) => b * NxB + Nb + i;
        public int Pg(int b, int g) => b * NxB + 2 * Nb + g;
        public int Qg(int b, int g) => b * NxB + 2 * Nb + Ng + g;

        public Problem(GridModel m, AcOpfExtendedOptions opt, AcOpfBlocks? blocks)
        {
            Model = m;
            Objective = opt.Objective;
            if (!Enum.IsDefined(Objective)) throw new ArgumentOutOfRangeException(nameof(opt), Objective, "Unknown AC-OPF objective.");
            Nb = m.BusCount;
            Ng = m.Generators.Count;
            K = 1 + (blocks?.Outages.Count ?? 0);
            NxB = 2 * Nb + 2 * Ng;
            Base = m.BaseMva;
            if (Nb == 0) throw new InvalidDataException("AC-OPF: the case has no buses.");
            if (Ng == 0) throw new InvalidDataException("AC-OPF: the case has no in-service generators to dispatch.");
            AcOpfInteriorPoint.RefuseUnmodelledDevices(m);   // ANA-06
            if (!(Base > 0) || !double.IsFinite(Base))
                throw new InvalidDataException($"AC-OPF: the system base {Fmt(Base)} MVA is not a finite number above 0.");

            var index = new Dictionary<int, int>(Nb);
            for (int i = 0; i < Nb; i++) index[m.Buses[i].Id] = i;

            // Loads: constant power only.
            PdPu = new double[Nb];
            QdPu = new double[Nb];
            for (int i = 0; i < Nb; i++)
            {
                Bus b = m.Buses[i];
                if (b.Zip is { VariesWithVoltage: true })
                    throw new InvalidDataException($"AC-OPF: bus {b.Id} has a voltage-dependent (ZIP) load; this AC-OPF models constant-power loads only.");
                if (!double.IsFinite(b.Pd) || !double.IsFinite(b.Qd))
                    throw new InvalidDataException($"AC-OPF: bus {b.Id} has a load that is not a finite number.");
                PdPu[i] = b.Pd / Base;
                QdPu[i] = b.Qd / Base;
            }

            // Branches: zero impedance refused (its admittance is infinite); connectivity checked below.
            foreach (Branch br in m.Branches)
                if (br.InService && br.R == 0 && br.X == 0)
                    throw new InvalidDataException($"AC-OPF: branch {br.From}-{br.To} has zero impedance; merge its buses or give it an impedance.");

            // Controls: taps (variables after the blocks, in control-set order), then shunts.
            OpfControlSet controls = opt.Controls ?? OpfControlSet.None;
            int nCtl = controls.Count;
            Nx = K * NxB + nCtl;
            if (opt.FixedControlValues is { } fixedVals && fixedVals.Count != nCtl)
                throw new ArgumentException($"AC-OPF: {fixedVals.Count} fixed control value(s) given for {nCtl} control(s).", nameof(opt));
            var tapBranches = new HashSet<int>();
            Taps = new TapCtl[controls.Taps.Count];
            for (int j = 0; j < Taps.Length; j++)
            {
                OpfTapControl tc = controls.Taps[j];
                if (tc.Branch < 0 || tc.Branch >= m.Branches.Count)
                    throw new InvalidDataException($"AC-OPF: tap control {j} names branch {tc.Branch}; the case has branches 0..{m.Branches.Count - 1}.");
                Branch br = m.Branches[tc.Branch];
                string who = $"branch {tc.Branch} ({br.From}-{br.To}{(br.Name is null ? "" : ", " + br.Name)})";
                if (!br.InService) throw new InvalidDataException($"AC-OPF: tap control on {who}, which is out of service.");
                if (!tapBranches.Add(tc.Branch)) throw new InvalidDataException($"AC-OPF: {who} has two tap controls.");
                if (!(tc.MinTap > 0) || !double.IsFinite(tc.MinTap) || !double.IsFinite(tc.MaxTap) || !(tc.MaxTap > tc.MinTap))
                    throw new InvalidDataException($"AC-OPF: tap control on {who} has range {Fmt(tc.MinTap)} .. {Fmt(tc.MaxTap)}; it must be finite, above 0 and in order.");
                if (!(tc.StepPu > 0) || !double.IsFinite(tc.StepPu))
                    throw new InvalidDataException($"AC-OPF: tap control on {who} has step {Fmt(tc.StepPu)}; it must be a finite number above 0.");
                // The OPF stamps the case R and X at every tap. A transformer whose impedance follows its tap (an impedance
                // correction table) would be optimised on the wrong impedance, so it is refused.
                if (br.Name is not null)
                {
                    foreach (Oltc o in m.Oltcs)
                        if (o.ImpedanceCorrection is not null && string.Equals(o.Transformer, br.Name, StringComparison.Ordinal))
                            throw new InvalidDataException($"AC-OPF: tap control on {who}, whose OLTC has an impedance correction table; the OPF holds the impedance at its case value, so it cannot optimise this tap.");
                    foreach (TransformerFlowControl fc in m.TransformerFlowControls)
                        if (fc.Kind == TransformerFlowKind.ReactivePower && fc.ImpedanceCorrection is not null && string.Equals(fc.Transformer, br.Name, StringComparison.Ordinal))
                            throw new InvalidDataException($"AC-OPF: tap control on {who}, whose tap flow control has an impedance correction table; the OPF holds the impedance at its case value, so it cannot optimise this tap.");
                }
                BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, 1.0, br.PhaseShiftDeg);
                double caseTap = br.TapRatio == 0 ? 1.0 : br.TapRatio;
                Taps[j] = new TapCtl(tc.Branch, br.Name ?? $"branch {tc.Branch}", index[br.From], index[br.To],
                    s.Yff.Real, s.Yff.Imaginary, s.Yft.Real, s.Yft.Imaginary, s.Ytt.Real, s.Ytt.Imaginary, s.Ytf.Real, s.Ytf.Imaginary,
                    K * NxB + j, tc.MinTap, tc.MaxTap, caseTap);
            }
            Shunts = new ShuntCtl[controls.Shunts.Count];
            var shuntBuses = new HashSet<int>();
            for (int j = 0; j < Shunts.Length; j++)
            {
                OpfShuntControl sc = controls.Shunts[j];
                if (!index.TryGetValue(sc.BusId, out int bi))
                    throw new InvalidDataException($"AC-OPF: shunt control {j} names bus {sc.BusId}, which the case does not have.");
                if (!shuntBuses.Add(sc.BusId)) throw new InvalidDataException($"AC-OPF: bus {sc.BusId} has two shunt controls.");
                if (!double.IsFinite(sc.BMinMvar) || !double.IsFinite(sc.BMaxMvar) || !(sc.BMaxMvar > sc.BMinMvar))
                    throw new InvalidDataException($"AC-OPF: shunt control at bus {sc.BusId} has range {Fmt(sc.BMinMvar)} .. {Fmt(sc.BMaxMvar)} MVAr; it must be finite and in order.");
                if (!(sc.StepMvar > 0) || !double.IsFinite(sc.StepMvar))
                    throw new InvalidDataException($"AC-OPF: shunt control at bus {sc.BusId} has step {Fmt(sc.StepMvar)} MVAr; it must be a finite number above 0.");
                if (!double.IsFinite(sc.InitialMvar))
                    throw new InvalidDataException($"AC-OPF: shunt control at bus {sc.BusId} has an initial value that is not a finite number.");
                Shunts[j] = new ShuntCtl(bi, sc.BusId, K * NxB + Taps.Length + j, sc.BMinMvar, sc.BMaxMvar, sc.InitialMvar);
            }
            // Phase-shifter angles (after the shunts). The branch keeps its case ratio; a branch with a tap control too is
            // refused (the two would share one stamp).
            Psts = new PstCtl[controls.PhaseShifts.Count];
            var pstBranches = new HashSet<int>();
            for (int j = 0; j < Psts.Length; j++)
            {
                OpfPhaseShiftControl pc = controls.PhaseShifts[j];
                if (pc.Branch < 0 || pc.Branch >= m.Branches.Count)
                    throw new InvalidDataException($"AC-OPF: phase-shifter control {j} names branch {pc.Branch}; the case has branches 0..{m.Branches.Count - 1}.");
                Branch br = m.Branches[pc.Branch];
                string who = $"branch {pc.Branch} ({br.From}-{br.To}{(br.Name is null ? "" : ", " + br.Name)})";
                if (!br.InService) throw new InvalidDataException($"AC-OPF: phase-shifter control on {who}, which is out of service.");
                if (!pstBranches.Add(pc.Branch)) throw new InvalidDataException($"AC-OPF: {who} has two phase-shifter controls.");
                if (tapBranches.Contains(pc.Branch))
                    throw new InvalidDataException($"AC-OPF: {who} has a tap control and a phase-shifter control; this AC-OPF moves one of the two on a branch.");
                if (!double.IsFinite(pc.MinDeg) || !double.IsFinite(pc.MaxDeg) || !(pc.MaxDeg > pc.MinDeg))
                    throw new InvalidDataException($"AC-OPF: phase-shifter control on {who} has range {Fmt(pc.MinDeg)} .. {Fmt(pc.MaxDeg)} degrees; it must be finite and in order.");
                if (!(pc.StepDeg >= 0) || !double.IsFinite(pc.StepDeg))
                    throw new InvalidDataException($"AC-OPF: phase-shifter control on {who} has step {Fmt(pc.StepDeg)} degrees; it must be 0 (continuous) or a finite number above 0.");
                if (br.Name is not null)
                    foreach (TransformerFlowControl fc in m.TransformerFlowControls)
                        if (fc.Kind == TransformerFlowKind.ActivePower && fc.ImpedanceCorrection is not null && string.Equals(fc.Transformer, br.Name, StringComparison.Ordinal))
                            throw new InvalidDataException($"AC-OPF: phase-shifter control on {who}, whose angle flow control has an impedance correction table; the OPF holds the impedance at its case value, so it cannot optimise this angle.");
                BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, 0.0);
                Psts[j] = new PstCtl(pc.Branch, br.Name ?? $"branch {pc.Branch}", index[br.From], index[br.To],
                    s.Yff.Real, s.Yff.Imaginary, s.Yft.Real, s.Yft.Imaginary, s.Ytt.Real, s.Ytt.Imaginary, s.Ytf.Real, s.Ytf.Imaginary,
                    K * NxB + Taps.Length + Shunts.Length + j, pc.MinDeg, pc.MaxDeg, br.PhaseShiftDeg);
            }

            // The networks: block 0 is the case (with the tap- and angle-controlled branches stamped by the controls
            // instead); block c has the branches of its outage out of service as well.
            Nets = new Net[K];
            for (int b = 0; b < K; b++)
            {
                int outage = b == 0 ? -1 : blocks!.Ids[b - 1];
                bool[]? branchOut = null, genOut = null;
                if (b > 0)
                {
                    ScopfOutage so = blocks!.Outages[b - 1];
                    branchOut = new bool[m.Branches.Count];
                    foreach (int k in so.Branches)
                    {
                        if (k < 0 || k >= m.Branches.Count || !m.Branches[k].InService)
                            throw new InvalidDataException($"AC-SCOPF: outage {k} is not an in-service branch of the case.");
                        branchOut[k] = true;
                    }
                    if (so.Generators.Count > 0)
                    {
                        genOut = new bool[Ng];
                        foreach (int g in so.Generators)
                        {
                            if (g < 0 || g >= Ng) throw new InvalidDataException($"AC-SCOPF: unit outage {g} is not an in-service unit of the case (0..{Ng - 1}).");
                            genOut[g] = true;
                        }
                    }
                }
                GridModel bm = m;
                if (b > 0 || tapBranches.Count > 0 || pstBranches.Count > 0)
                {
                    var brs = new Branch[m.Branches.Count];
                    for (int k = 0; k < brs.Length; k++)
                        brs[k] = ((branchOut is not null && branchOut[k]) || tapBranches.Contains(k) || pstBranches.Contains(k)) ? m.Branches[k] with { InService = false } : m.Branches[k];
                    bm = m.CopyWith(branches: brs);
                }
                // Admittance matrix (the same stamps and accumulation as every other study), split into rows.
                SparseYBus y = YBus.BuildSparse(bm);
                var gd = new double[Nb];
                var bd = new double[Nb];
                var rows = new List<Cell>[Nb];
                for (int i = 0; i < Nb; i++) rows[i] = new List<Cell>();
                foreach (KeyValuePair<(int Row, int Col), Complex> c in y.Cells)
                {
                    if (c.Key.Row == c.Key.Col) { gd[c.Key.Row] = c.Value.Real; bd[c.Key.Row] = c.Value.Imaginary; }
                    else rows[c.Key.Row].Add(new Cell(c.Key.Col, c.Value.Real, c.Value.Imaginary));
                }
                var row = new Cell[Nb][];
                for (int i = 0; i < Nb; i++)
                {
                    Cell[] r = rows[i].ToArray();
                    Array.Sort(r, (a, bb) => a.K.CompareTo(bb.K));   // column order: no dependence on dictionary order
                    row[i] = r;
                }
                Nets[b] = new Net
                {
                    Gd = gd, Bd = bd, Row = row, Outage = outage, Model = bm, BranchOut = branchOut, GenOut = genOut,
                    Label = b > 0 ? blocks!.Labels?[b - 1] : null,
                    Response = genOut is not null ? AcScopf.Response(m, blocks!.Outages[b - 1]) : null,
                };
                CheckOneIsland(m, Nets[b], b == 0 ? Taps.Select(t => (t.F, t.T)).Concat(Psts.Select(t => (t.F, t.T))).ToArray() : Array.Empty<(int, int)>());
            }

            // Slack buses: at least one; every slack angle is fixed (MATPOWER fixes every reference bus).
            RefBus = -1;
            for (int i = 0; i < Nb; i++)
                if (m.Buses[i].Type == BusType.Slack) { RefBus = i; break; }
            if (RefBus < 0) throw new InvalidDataException("AC-OPF: the case has no slack bus to fix the reference angle.");

            // Generators: limits and (for the cost objective) cost curves.
            GenBus = new int[Ng];
            GenAtSlack = new bool[Ng];
            Poly = Objective == AcOpfObjective.Cost ? new double[Ng][] : null;
            for (int g = 0; g < Ng; g++)
            {
                Generator gen = m.Generators[g];
                string who = $"generator {g} at bus {gen.Bus}{(gen.Name is null ? "" : $" ({gen.Name})")}";
                if (!index.TryGetValue(gen.Bus, out int gb))
                    throw new InvalidDataException($"AC-OPF: {who} sits on a bus the case does not have.");
                GenBus[g] = gb;
                GenAtSlack[g] = m.Buses[gb].Type == BusType.Slack;
                if (double.IsNaN(gen.Pmin) || double.IsNaN(gen.Pmax) || double.IsNaN(gen.Qmin) || double.IsNaN(gen.Qmax))
                    throw new InvalidDataException($"AC-OPF: {who} has a limit that is not a number.");
                if (gen.Pmin > gen.Pmax)
                    throw new InvalidDataException($"AC-OPF: {who} has Pmin {Fmt(gen.Pmin)} MW above Pmax {Fmt(gen.Pmax)} MW.");
                if (gen.Qmin > gen.Qmax)
                    throw new InvalidDataException($"AC-OPF: {who} has Qmin {Fmt(gen.Qmin)} MVAr above Qmax {Fmt(gen.Qmax)} MVAr.");
                if (gen.Pmin < 0 && gen.Pmax == 0)
                    throw new InvalidDataException($"AC-OPF: {who} is a dispatchable load (Pmin {Fmt(gen.Pmin)} MW, Pmax 0); its constant power factor constraint is not modelled.");
                if (Poly is not null) Poly[g] = CostPolynomial(gen.Cost, who);
                if (Objective == AcOpfObjective.ReactivePower && !GenAtSlack[g] && !double.IsFinite(gen.Pg))
                    throw new InvalidDataException($"AC-OPF: {who} has a case output that is not a finite number; reactive power optimisation holds it there.");
            }

            // Variable bounds (pu, radians), block by block, then the controls.
            XMin = new double[Nx];
            XMax = new double[Nx];
            for (int bk = 0; bk < K; bk++)
            {
                for (int i = 0; i < Nb; i++)
                {
                    Bus b = m.Buses[i];
                    bool slack = b.Type == BusType.Slack;
                    double va = b.VaDeg * Math.PI / 180.0;
                    XMin[Va(bk, i)] = slack ? va : double.NegativeInfinity;
                    XMax[Va(bk, i)] = slack ? va : double.PositiveInfinity;
                    double lo = b.VminPu ?? VoltageLimits.OperationalLowPu;
                    double hi = b.VmaxPu ?? VoltageLimits.OperationalHighPu;
                    if (!double.IsFinite(lo) || !double.IsFinite(hi) || !(lo > 0) || lo > hi)
                        throw new InvalidDataException($"AC-OPF: bus {b.Id} has voltage limits {Fmt(lo)} .. {Fmt(hi)} pu; they must be finite, above 0 and in order.");
                    XMin[Vm(bk, i)] = lo;
                    XMax[Vm(bk, i)] = hi;
                }
                for (int g = 0; g < Ng; g++)
                {
                    Generator gen = m.Generators[g];
                    if (Objective == AcOpfObjective.ReactivePower && !GenAtSlack[g])
                    {
                        XMin[Pg(bk, g)] = gen.Pg / Base; XMax[Pg(bk, g)] = gen.Pg / Base;
                    }
                    else
                    {
                        XMin[Pg(bk, g)] = gen.Pmin / Base; XMax[Pg(bk, g)] = gen.Pmax / Base;
                    }
                    XMin[Qg(bk, g)] = gen.Qmin / Base; XMax[Qg(bk, g)] = gen.Qmax / Base;
                    if (Nets[bk].UnitOut(g))
                    {
                        // A unit the block's outage removes: its variables stay (the layout is unchanged) and are held at 0.
                        XMin[Pg(bk, g)] = 0; XMax[Pg(bk, g)] = 0;
                        XMin[Qg(bk, g)] = 0; XMax[Qg(bk, g)] = 0;
                    }
                }
            }
            for (int j = 0; j < Taps.Length; j++)
            {
                int v = Taps[j].Var;
                if (opt.FixedControlValues is { } fv)
                {
                    double tau = fv[j];
                    if (!(tau > 0) || !double.IsFinite(tau)) throw new ArgumentException($"AC-OPF: fixed tap {Fmt(tau)} is not a ratio above 0.", nameof(opt));
                    XMin[v] = 1.0 / tau; XMax[v] = 1.0 / tau;
                }
                else { XMin[v] = 1.0 / Taps[j].MaxTap; XMax[v] = 1.0 / Taps[j].MinTap; }
            }
            for (int j = 0; j < Shunts.Length; j++)
            {
                int v = Shunts[j].Var;
                if (opt.FixedControlValues is { } fv)
                {
                    double bm = fv[Taps.Length + j];
                    if (!double.IsFinite(bm)) throw new ArgumentException("AC-OPF: a fixed shunt value is not a finite number.", nameof(opt));
                    XMin[v] = bm / Base; XMax[v] = bm / Base;
                }
                else { XMin[v] = Shunts[j].BMinMvar / Base; XMax[v] = Shunts[j].BMaxMvar / Base; }
            }
            for (int j = 0; j < Psts.Length; j++)
            {
                int v = Psts[j].Var;
                if (opt.FixedControlValues is { } fv)
                {
                    double deg = fv[Taps.Length + Shunts.Length + j];
                    if (!double.IsFinite(deg)) throw new ArgumentException("AC-OPF: a fixed phase-shifter angle is not a finite number.", nameof(opt));
                    XMin[v] = deg * Math.PI / 180.0; XMax[v] = deg * Math.PI / 180.0;
                }
                else { XMin[v] = Psts[j].MinDeg * Math.PI / 180.0; XMax[v] = Psts[j].MaxDeg * Math.PI / 180.0; }
            }

            // MIPS: equal bounds become equality rows; the rest become inequality rows, upper-only first, then
            // lower-only, then the two-sided ones (all their upper rows, then all their lower rows).
            var fixedRows = new List<(int, double)>();
            var upperOnly = new List<BoundRow>();
            var lowerOnly = new List<BoundRow>();
            var bothUpper = new List<BoundRow>();
            var bothLower = new List<BoundRow>();
            for (int j = 0; j < Nx; j++)
            {
                double lo = XMin[j], hi = XMax[j];
                bool hasLo = lo > -NoBound, hasHi = hi < NoBound;
                if (hasLo && hasHi && Math.Abs(hi - lo) <= MachineEpsilon) { fixedRows.Add((j, hi)); continue; }
                if (hasHi && !hasLo) upperOnly.Add(new BoundRow(j, +1, hi));
                else if (hasLo && !hasHi) lowerOnly.Add(new BoundRow(j, -1, lo));
                else if (hasLo && hasHi) { bothUpper.Add(new BoundRow(j, +1, hi)); bothLower.Add(new BoundRow(j, -1, lo)); }
            }
            Fixed = fixedRows.ToArray();
            Bounds = upperOnly.Concat(lowerOnly).Concat(bothUpper).Concat(bothLower).ToArray();

            // Coupling rows (blocks 1..K-1) for each unit not at a slack bus whose output is not already fixed. In a block
            // whose outage removes units, the units at the block's reference bus are free instead, and every other unit's
            // row carries its governor-droop share of the removed units' base-case output: Pg_c - Pg_0 - alpha_g sum Pg_0,r.
            var linEq = new List<LinRow>();
            var linIq = new List<LinRow>();
            if (blocks is not null)
            {
                if (blocks.Coupling == AcOpfCoupling.Corrective && (blocks.RampMw is null || blocks.RampMw.Count != Ng))
                    throw new ArgumentException("AC-SCOPF: corrective coupling needs one ramp per in-service unit.", nameof(blocks));
                for (int bk = 1; bk < K; bk++)
                    for (int g = 0; g < Ng; g++)
                    {
                        AcScopf.OutageResponse? resp = Nets[bk].Response;
                        if (resp is null ? GenAtSlack[g] : resp.Balancing[g]) continue;
                        if (Math.Abs(XMax[Pg(0, g)] - XMin[Pg(0, g)]) <= MachineEpsilon) continue;   // held by its own bounds
                        if (Nets[bk].UnitOut(g)) continue;                                            // out in this block
                        (int, double)[]? share = null, shareNeg = null;
                        if (resp is not null && resp.Alpha[g] != 0)
                        {
                            var terms = new List<(int, double)>();
                            var neg = new List<(int, double)>();
                            for (int r = 0; r < Ng; r++)
                                if (Nets[bk].UnitOut(r)) { terms.Add((Pg(0, r), -resp.Alpha[g])); neg.Add((Pg(0, r), resp.Alpha[g])); }
                            share = terms.ToArray();
                            shareNeg = neg.ToArray();
                        }
                        if (blocks.Coupling == AcOpfCoupling.Preventive)
                            linEq.Add(new LinRow(Pg(bk, g), 1.0, Pg(0, g), -1.0, 0.0, g, bk) { Extra = share });
                        else
                        {
                            double r = blocks.RampMw![g];
                            if (double.IsNaN(r) || r < 0) throw new ArgumentException($"AC-SCOPF: unit {g} has ramp {Fmt(r)} MW; a ramp must be 0 or above.", nameof(blocks));
                            if (double.IsPositiveInfinity(r)) continue;
                            linIq.Add(new LinRow(Pg(bk, g), 1.0, Pg(0, g), -1.0, r / Base, g, bk) { Extra = share });
                            linIq.Add(new LinRow(Pg(bk, g), -1.0, Pg(0, g), 1.0, r / Base, g, bk) { Extra = shareNeg });
                        }
                    }
            }
            LinEq = linEq.ToArray();
            LinIq = linIq.ToArray();

            // Rated branches (MATPOWER: 0 or 1e10 and above is unrated), block by block. A post-outage block uses the
            // post-outage rating (RateB or RateC where given, else RateA).
            var tapOf = new Dictionary<int, int>();
            for (int j = 0; j < Taps.Length; j++) tapOf[Taps[j].Branch] = j;
            var pstOf = new Dictionary<int, int>();
            for (int j = 0; j < Psts.Length; j++) pstOf[Psts[j].Branch] = j;
            var flows = new List<FlowLimit>();
            for (int bk = 0; bk < K; bk++)
                for (int k = 0; k < m.Branches.Count; k++)
                {
                    Branch br = m.Branches[k];
                    if (!br.InService || Nets[bk].IsOut(k)) continue;
                    if (double.IsNaN(br.RateAMva) || br.RateAMva < 0)
                        throw new InvalidDataException($"AC-OPF: branch {br.From}-{br.To} has rating {Fmt(br.RateAMva)} MVA; a rating must be 0 (unrated) or above.");
                    double rate = bk == 0 ? br.RateAMva : PostRating(br, blocks!.PostRating);
                    if (rate == 0 || rate >= NoBound) continue;
                    double smax = rate / Base;
                    if (tapOf.TryGetValue(k, out int tj))
                    {
                        TapCtl t = Taps[tj];
                        flows.Add(new FlowLimit(bk, k, t.F, t.T, t.Gff, t.Bff, t.Gft, t.Bft, t.Gtt, t.Btt, t.Gtf, t.Btf, smax, smax * smax, tj));
                        continue;
                    }
                    if (pstOf.TryGetValue(k, out int pj))
                    {
                        PstCtl t = Psts[pj];
                        flows.Add(new FlowLimit(bk, k, t.F, t.T, t.Gff, t.Bff, t.Gft, t.Bft, t.Gtt, t.Btt, t.Gtf, t.Btf, smax, smax * smax, -1, pj));
                        continue;
                    }
                    BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
                    flows.Add(new FlowLimit(bk, k, index[br.From], index[br.To],
                        s.Yff.Real, s.Yff.Imaginary, s.Yft.Real, s.Yft.Imaginary,
                        s.Ytt.Real, s.Ytt.Imaginary, s.Ytf.Real, s.Ytf.Imaginary, smax, smax * smax, -1));
                }
            Flows = flows.ToArray();
        }

        private void CheckOneIsland(GridModel m, Net net, (int F, int T)[] controlled)
        {
            var extra = new List<int>[Nb];
            foreach ((int f, int t) in controlled)
            {
                (extra[f] ??= new List<int>()).Add(t);
                (extra[t] ??= new List<int>()).Add(f);
            }
            var seen = new bool[Nb];
            var stack = new Stack<int>();
            seen[0] = true; stack.Push(0);
            int count = 1;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                foreach (Cell c in net.Row[i])
                    if (!seen[c.K]) { seen[c.K] = true; count++; stack.Push(c.K); }
                if (extra[i] is { } ex)
                    foreach (int k in ex)
                        if (!seen[k]) { seen[k] = true; count++; stack.Push(k); }
            }
            if (count != Nb)
            {
                int first = Array.IndexOf(seen, false);
                string after = net.Label is not null ? $" after outage {net.Label}" : net.Outage >= 0 ? $" after the outage of branch {net.Outage}" : "";
                throw new InvalidDataException(
                    $"AC-OPF: the network{after} is in more than one island (bus {m.Buses[first].Id} is not connected to bus {m.Buses[0].Id}); "
                    + "solve each island as its own case.");
            }
        }
    }

    /// <summary>The post-outage rating of a branch: RateB or RateC as asked, falling back to RateA where it is 0 (the
    /// rule of <see cref="DcScopf"/> and the AC contingency screen).</summary>
    internal static double PostRating(Branch br, PostFaultRating rule) => rule switch
    {
        PostFaultRating.RateB when br.RateBMva > 0 => br.RateBMva,
        PostFaultRating.RateC when br.RateCMva > 0 => br.RateCMva,
        _ => br.RateAMva,
    };

    /// <summary>The polynomial (highest order first) of a generator's cost, refusing anything else.</summary>
    private static double[] CostPolynomial(GeneratorCost? cost, string who)
    {
        if (cost is null)
            throw new InvalidDataException($"AC-OPF: {who} has no cost curve; every in-service generator needs one (a MATPOWER mpc.gencost row, or a cost file).");
        if (cost.Model == GeneratorCostModel.PiecewiseLinear)
            throw new InvalidDataException($"AC-OPF: {who} has a piecewise-linear cost; this AC-OPF optimises polynomial costs only.");
        if (cost.Model != GeneratorCostModel.Polynomial)
            throw new InvalidDataException($"AC-OPF: {who} has an unknown cost model {(int)cost.Model}.");
        if (cost.Coefficients.Count < 1)
            throw new InvalidDataException($"AC-OPF: {who} has a polynomial cost with no coefficients.");
        var k = new double[cost.Coefficients.Count];
        for (int i = 0; i < k.Length; i++)
        {
            k[i] = cost.Coefficients[i];
            if (!double.IsFinite(k[i]))
                throw new InvalidDataException($"AC-OPF: {who} has a cost coefficient that is not a finite number.");
        }
        return k;
    }

    // ---- Evaluation --------------------------------------------------------------------------------------------------

    /// <summary>The objective (scaled), its gradient and (on request) its Hessian diagonal, all in pu variables. Only the
    /// base-case block's units are in the objective.</summary>
    private static double Cost(Problem p, double[] x, double scale, double[]? grad, double[]? hessDiag)
    {
        double f = 0;
        if (p.Poly is null)
        {
            // Losses (and reactive power optimisation): f = sum of Pg in MW; gradient base per unit, Hessian 0.
            for (int g = 0; g < p.Ng; g++)
            {
                f += x[p.Pg(0, g)] * p.Base;
                if (grad is not null) grad[p.Pg(0, g)] += scale * p.Base;
            }
            return scale * f;
        }
        for (int g = 0; g < p.Ng; g++)
        {
            double[] k = p.Poly[g];
            double mw = x[p.Pg(0, g)] * p.Base;
            // Horner for the value, the first and the second derivative (in MW).
            double v = 0, d1 = 0, d2 = 0;
            for (int i = 0; i < k.Length; i++)
            {
                d2 = d2 * mw + 2 * d1;
                d1 = d1 * mw + v;
                v = v * mw + k[i];
            }
            f += v;
            if (grad is not null) grad[p.Pg(0, g)] += scale * d1 * p.Base;
            if (hessDiag is not null) hessDiag[p.Pg(0, g)] += scale * d2 * p.Base * p.Base;
        }
        return scale * f;
    }

    /// <summary>
    /// The pair term T = Va Vb (wP p0 + wQ q0) with p0 = g cos(t) + b sin(t), q0 = g sin(t) - b cos(t), t = theta_a - theta_b.
    /// Returns phi = wP p0 + wQ q0 and phi' = d phi / d t = -wP q0 + wQ p0 (and phi'' = -phi).
    /// </summary>
    private static (double Phi, double DPhi) Pair(double ta, double tb, double g, double b, double wP, double wQ)
    {
        (double s, double c) = DetMath.SinCos(ta - tb);
        double p0 = g * c + b * s;
        double q0 = g * s - b * c;
        return (wP * p0 + wQ * q0, -wP * q0 + wQ * p0);
    }

    /// <summary>Add the Hessian of T = Va Vb phi(theta_a - theta_b) over (theta_a, theta_b, Va, Vb) = (ia, ib, ja, jb).</summary>
    private static void AddPairHessian(HessianAccumulator h, int ia, int ib, int ja, int jb, double va, double vb, double phi, double dphi)
    {
        double vv = va * vb;
        h.Add(ia, ia, -vv * phi);
        h.Add(ib, ib, -vv * phi);
        h.AddSym(ia, ib, vv * phi);
        h.AddSym(ia, ja, vb * dphi);
        h.AddSym(ia, jb, va * dphi);
        h.AddSym(ib, ja, -vb * dphi);
        h.AddSym(ib, jb, -va * dphi);
        h.AddSym(ja, jb, phi);
    }

    /// <summary>
    /// The power entering a tap-controlled branch at end a, as a function of u = 1/tau:
    /// P = d(u) Va^2 g0 + u Va Vb p0, Q = -d(u) Va^2 b0 + u Va Vb q0, with d(u) = u^2 at the from (tapped) end and 1 at
    /// the to end, (g0, b0) the end's own admittance and (g, b) the transfer admittance at u = 1. Gradient over
    /// (theta_a, theta_b, Va, Vb, u).
    /// </summary>
    private static (double P, double Q) TapEndPower(double ta, double tb, double va, double vb, double u, bool fromEnd,
        double g0, double b0, double g, double b, Span<double> dP, Span<double> dQ)
    {
        (double s, double c) = DetMath.SinCos(ta - tb);
        double p0 = g * c + b * s;
        double q0 = g * s - b * c;
        double vv = va * vb;
        double d = fromEnd ? u * u : 1.0, dd = fromEnd ? 2 * u : 0.0;
        dP[0] = -u * vv * q0; dP[1] = u * vv * q0; dP[2] = 2 * d * va * g0 + u * vb * p0; dP[3] = u * va * p0; dP[4] = dd * va * va * g0 + vv * p0;
        dQ[0] = u * vv * p0; dQ[1] = -u * vv * p0; dQ[2] = -2 * d * va * b0 + u * vb * q0; dQ[3] = u * va * q0; dQ[4] = -dd * va * va * b0 + vv * q0;
        return (d * va * va * g0 + u * vv * p0, -d * va * va * b0 + u * vv * q0);
    }

    /// <summary>
    /// Add the Hessian of wP P + wQ Q of <see cref="TapEndPower"/> over vars (theta_a, theta_b, Va, Vb, u): the pair part
    /// is u times the pair Hessian, plus the cross terms d2(uT)/du dz = dT/dz; the own part d(u) Va^2 w0 (w0 = wP g0 -
    /// wQ b0) gives 2 d w0 on Va Va and, at the from end, 2 Va^2 w0 on u u and 4 u Va w0 on u Va.
    /// </summary>
    private static void AddTapEndHessian(HessianAccumulator h, int[] v, double ta, double tb, double va, double vb, double u, bool fromEnd,
        double g0, double b0, double g, double b, double wP, double wQ)
    {
        (double phi, double dphi) = Pair(ta, tb, g, b, wP, wQ);
        AddPairHessian(h, v[0], v[1], v[2], v[3], va, vb, u * phi, u * dphi);
        double vv = va * vb;
        h.AddSym(v[4], v[0], vv * dphi);
        h.AddSym(v[4], v[1], -vv * dphi);
        h.AddSym(v[4], v[2], vb * phi);
        h.AddSym(v[4], v[3], va * phi);
        double w0 = wP * g0 - wQ * b0;
        double d = fromEnd ? u * u : 1.0;
        h.Add(v[2], v[2], 2 * d * w0);
        if (fromEnd)
        {
            h.Add(v[4], v[4], 2 * va * va * w0);
            h.AddSym(v[4], v[2], 4 * u * va * w0);
        }
    }

    /// <summary>
    /// The power entering a phase-shifter branch at end a: P = Va^2 g0 + Va Vb p0(t), Q = -Va^2 b0 + Va Vb q0(t), with
    /// t = theta_a - theta_b + sg phi (sg = -1 at the from end, +1 at the to end), (g0, b0) the end's own admittance and
    /// (g, b) the transfer admittance at phi = 0. Gradient over (theta_a, theta_b, Va, Vb, phi): the phi entry is sg times
    /// the theta_a entry (dYft/dphi = j Yft, dYtf/dphi = -j Ytf).
    /// </summary>
    private static (double P, double Q) PstEndPower(double ta, double tb, double va, double vb, double phi, double sg,
        double g0, double b0, double g, double b, Span<double> dP, Span<double> dQ)
    {
        (double s, double c) = DetMath.SinCos(ta - tb + sg * phi);
        double p0 = g * c + b * s;
        double q0 = g * s - b * c;
        double vv = va * vb;
        dP[0] = -vv * q0; dP[1] = vv * q0; dP[2] = 2 * va * g0 + vb * p0; dP[3] = va * p0; dP[4] = sg * -vv * q0;
        dQ[0] = vv * p0; dQ[1] = -vv * p0; dQ[2] = -2 * va * b0 + vb * q0; dQ[3] = va * q0; dQ[4] = sg * vv * p0;
        return (va * va * g0 + vv * p0, -va * va * b0 + vv * q0);
    }

    /// <summary>
    /// Add the Hessian of wP P + wQ Q of <see cref="PstEndPower"/> over vars (theta_a, theta_b, Va, Vb, phi): the pair
    /// Hessian at t, plus phi as sg times theta_a (d2/dphi2 = -Va Vb psi, d2/dphi dtheta_a = -sg Va Vb psi,
    /// d2/dphi dtheta_b = sg Va Vb psi, d2/dphi dVa = sg Vb psi', d2/dphi dVb = sg Va psi', with psi = wP p0 + wQ q0), and
    /// the own part 2 (wP g0 - wQ b0) on Va Va.
    /// </summary>
    private static void AddPstEndHessian(HessianAccumulator h, int[] v, double ta, double tb, double va, double vb, double phi, double sg,
        double g0, double b0, double g, double b, double wP, double wQ)
    {
        (double s, double c) = DetMath.SinCos(ta - tb + sg * phi);
        double p0 = g * c + b * s;
        double q0 = g * s - b * c;
        double psi = wP * p0 + wQ * q0, dpsi = -wP * q0 + wQ * p0;
        AddPairHessian(h, v[0], v[1], v[2], v[3], va, vb, psi, dpsi);
        double vv = va * vb;
        h.Add(v[4], v[4], -vv * psi);
        h.AddSym(v[4], v[0], sg * -vv * psi);
        h.AddSym(v[4], v[1], sg * vv * psi);
        h.AddSym(v[4], v[2], sg * vb * dpsi);
        h.AddSym(v[4], v[3], sg * va * dpsi);
        h.Add(v[2], v[2], 2 * (wP * g0 - wQ * b0));
    }

    /// <summary>The power balance mismatches g (2 nb rows per block: active then reactive, pu) and, on request, their
    /// Jacobian rows as sparse lists (row = constraint, entries (variable, value)).</summary>
    private static void Balance(Problem p, double[] x, double[] g, List<(int Var, double Val)>[]? jac)
    {
        int nb = p.Nb;
        for (int bk = 0; bk < p.K; bk++)
        {
            Net net = p.Nets[bk];
            int rb = bk * 2 * nb;
            for (int i = 0; i < nb; i++)
            {
                double ti = x[p.Va(bk, i)], vi = x[p.Vm(bk, i)];
                double pi = vi * vi * net.Gd[i];
                double qi = -vi * vi * net.Bd[i];
                double dPdti = 0, dPdvi = 2 * vi * net.Gd[i], dQdti = 0, dQdvi = -2 * vi * net.Bd[i];
                List<(int, double)>? jp = jac?[rb + i], jq = jac?[rb + nb + i];
                foreach (Cell c in net.Row[i])
                {
                    int k = c.K;
                    double tk = x[p.Va(bk, k)], vk = x[p.Vm(bk, k)];
                    (double s, double co) = DetMath.SinCos(ti - tk);
                    double p0 = c.G * co + c.B * s;
                    double q0 = c.G * s - c.B * co;
                    pi += vi * vk * p0;
                    qi += vi * vk * q0;
                    if (jac is null) continue;
                    dPdti += -vi * vk * q0;
                    dPdvi += vk * p0;
                    dQdti += vi * vk * p0;
                    dQdvi += vk * q0;
                    jp!.Add((p.Va(bk, k), vi * vk * q0));
                    jp.Add((p.Vm(bk, k), vi * p0));
                    jq!.Add((p.Va(bk, k), -vi * vk * p0));
                    jq.Add((p.Vm(bk, k), vi * q0));
                }
                if (jac is not null)
                {
                    jp!.Add((p.Va(bk, i), dPdti)); jp.Add((p.Vm(bk, i), dPdvi));
                    jq!.Add((p.Va(bk, i), dQdti)); jq.Add((p.Vm(bk, i), dQdvi));
                }
                g[rb + i] = pi + p.PdPu[i];
                g[rb + nb + i] = qi + p.QdPu[i];
            }
            for (int gg = 0; gg < p.Ng; gg++)
            {
                int b = p.GenBus[gg];
                g[rb + b] -= x[p.Pg(bk, gg)];
                g[rb + nb + b] -= x[p.Qg(bk, gg)];
                if (jac is not null) { jac[rb + b].Add((p.Pg(bk, gg), -1.0)); jac[rb + nb + b].Add((p.Qg(bk, gg), -1.0)); }
            }
        }

        // Tap-controlled branches (block 0): both ends' injections.
        Span<double> dP = stackalloc double[5];
        Span<double> dQ = stackalloc double[5];
        foreach (TapCtl t in p.Taps)
        {
            double u = x[t.Var];
            for (int end = 0; end < 2; end++)
            {
                bool from = end == 0;
                int a = from ? t.F : t.T, b = from ? t.T : t.F;
                (double pp, double qq) = from
                    ? TapEndPower(x[p.Va(0, a)], x[p.Va(0, b)], x[p.Vm(0, a)], x[p.Vm(0, b)], u, true, t.Gff, t.Bff, t.Gft, t.Bft, dP, dQ)
                    : TapEndPower(x[p.Va(0, a)], x[p.Va(0, b)], x[p.Vm(0, a)], x[p.Vm(0, b)], u, false, t.Gtt, t.Btt, t.Gtf, t.Btf, dP, dQ);
                g[a] += pp;
                g[nb + a] += qq;
                if (jac is null) continue;
                int[] vars = { p.Va(0, a), p.Va(0, b), p.Vm(0, a), p.Vm(0, b), t.Var };
                for (int q = 0; q < 5; q++)
                {
                    jac[a].Add((vars[q], dP[q]));
                    jac[nb + a].Add((vars[q], dQ[q]));
                }
            }
        }

        // Shunt controls (block 0): -b Vi^2 in the reactive balance, the sign of a Bus.Bs stamp.
        foreach (ShuntCtl s in p.Shunts)
        {
            double vi = x[p.Vm(0, s.Bus)], b = x[s.Var];
            g[nb + s.Bus] += -b * vi * vi;
            if (jac is null) continue;
            jac[nb + s.Bus].Add((p.Vm(0, s.Bus), -2 * b * vi));
            jac[nb + s.Bus].Add((s.Var, -vi * vi));
        }

        // Phase-shifter branches (block 0): both ends' injections.
        foreach (PstCtl t in p.Psts)
        {
            double phi = x[t.Var];
            for (int end = 0; end < 2; end++)
            {
                bool from = end == 0;
                int a = from ? t.F : t.T, b = from ? t.T : t.F;
                (double pp, double qq) = from
                    ? PstEndPower(x[p.Va(0, a)], x[p.Va(0, b)], x[p.Vm(0, a)], x[p.Vm(0, b)], phi, -1.0, t.Gff, t.Bff, t.Gft, t.Bft, dP, dQ)
                    : PstEndPower(x[p.Va(0, a)], x[p.Va(0, b)], x[p.Vm(0, a)], x[p.Vm(0, b)], phi, +1.0, t.Gtt, t.Btt, t.Gtf, t.Btf, dP, dQ);
                g[a] += pp;
                g[nb + a] += qq;
                if (jac is null) continue;
                int[] vars = { p.Va(0, a), p.Va(0, b), p.Vm(0, a), p.Vm(0, b), t.Var };
                for (int q = 0; q < 5; q++)
                {
                    jac[a].Add((vars[q], dP[q]));
                    jac[nb + a].Add((vars[q], dQ[q]));
                }
            }
        }
    }

    /// <summary>The power entering a branch at end a: P = Va^2 g0 + Va Vb p0, Q = -Va^2 b0 + Va Vb q0, with its gradient
    /// over (theta_a, theta_b, Va, Vb).</summary>
    private static (double P, double Q) EndPower(double ta, double tb, double va, double vb, double g0, double b0, double g, double b,
        Span<double> dP, Span<double> dQ)
    {
        (double s, double c) = DetMath.SinCos(ta - tb);
        double p0 = g * c + b * s;
        double q0 = g * s - b * c;
        double vv = va * vb;
        dP[0] = -vv * q0; dP[1] = vv * q0; dP[2] = 2 * va * g0 + vb * p0; dP[3] = va * p0;
        dQ[0] = vv * p0; dQ[1] = -vv * p0; dQ[2] = -2 * va * b0 + vb * q0; dQ[3] = va * q0;
        return (va * va * g0 + vv * p0, -va * va * b0 + vv * q0);
    }

    /// <summary>Accumulates a symmetric sparse matrix as (row, col, value) triplets in a fixed order.</summary>
    private sealed class HessianAccumulator
    {
        public readonly List<(int R, int C, double V)> Entries = new();
        public void Add(int r, int c, double v) { if (v != 0.0) Entries.Add((r, c, v)); }
        public void AddSym(int r, int c, double v) { if (v != 0.0) { Entries.Add((r, c, v)); Entries.Add((c, r, v)); } }
    }

    // ---- The interior point iteration ---------------------------------------------------------------------------------

    /// <summary>
    /// The functions of the problem at a point: the objective f (scaled), the equality constraints g (power balance of
    /// every block, then the fixed variables, then the coupling equalities), the inequality constraints h (flow limits
    /// from, flow limits to, the bound rows, then the coupling inequalities), their first derivatives, the gradient of the
    /// Lagrangian and its Hessian.
    /// </summary>
    private sealed class Evaluator
    {
        public readonly Problem P;
        public readonly int Nx, Neq, Niq, NFlow, NBal, NBound;
        public readonly double Scale;
        public readonly double[] G, H, Df, Lx;
        public readonly List<(int Var, double Val)>[] JacG;
        public readonly double[,] FlowGrad;          // gradient of each flow row over its four (or, with a tap, five) variables
        public readonly int[][] FlowVarList;
        private readonly double[] _dP = new double[5], _dQ = new double[5];

        public Evaluator(Problem p, double scale)
        {
            P = p;
            Scale = scale;
            Nx = p.Nx;
            NFlow = p.Flows.Length;
            NBound = p.Bounds.Length;
            Niq = 2 * NFlow + p.Bounds.Length + p.LinIq.Length;
            NBal = 2 * p.Nb * p.K;
            Neq = NBal + p.Fixed.Length + p.LinEq.Length;
            G = new double[Neq];
            H = new double[Niq];
            Df = new double[Nx];
            Lx = new double[Nx];
            JacG = new List<(int, double)>[Neq];
            for (int r = 0; r < Neq; r++) JacG[r] = new List<(int, double)>();
            FlowGrad = new double[Math.Max(2 * NFlow, 1), 5];
            FlowVarList = new int[2 * NFlow][];
            for (int row = 0; row < 2 * NFlow; row++)
            {
                FlowLimit fl = p.Flows[row % NFlow];
                (int a, int b) = row < NFlow ? (fl.F, fl.T) : (fl.T, fl.F);
                FlowVarList[row] = fl.Pst >= 0
                    ? new[] { p.Va(fl.Block, a), p.Va(fl.Block, b), p.Vm(fl.Block, a), p.Vm(fl.Block, b), p.Psts[fl.Pst].Var }
                    : fl.Tap < 0
                    ? new[] { p.Va(fl.Block, a), p.Va(fl.Block, b), p.Vm(fl.Block, a), p.Vm(fl.Block, b) }
                    : new[] { p.Va(fl.Block, a), p.Va(fl.Block, b), p.Vm(fl.Block, a), p.Vm(fl.Block, b), p.Taps[fl.Tap].Var };
            }
        }

        /// <summary>The branch end of flow row <paramref name="row"/>: (a, b) bus indices, the admittances, and whether
        /// it is the from end.</summary>
        private (int A, int B, double G0, double B0, double G, double Bb, bool From) End(int row)
        {
            FlowLimit fl = P.Flows[row % NFlow];
            return row < NFlow ? (fl.F, fl.T, fl.Gff, fl.Bff, fl.Gft, fl.Bft, true) : (fl.T, fl.F, fl.Gtt, fl.Btt, fl.Gtf, fl.Btf, false);
        }

        /// <summary>The end power of flow row <paramref name="row"/> at <paramref name="x"/>, with its gradient in
        /// _dP/_dQ.</summary>
        private (double P, double Q) RowPower(int row, double[] x)
        {
            Problem p = P;
            FlowLimit fl = p.Flows[row % NFlow];
            (int a, int b, double g0, double b0, double g, double bb, bool from) = End(row);
            int blk = fl.Block;
            if (fl.Pst >= 0)
                return PstEndPower(x[p.Va(blk, a)], x[p.Va(blk, b)], x[p.Vm(blk, a)], x[p.Vm(blk, b)], x[p.Psts[fl.Pst].Var], from ? -1.0 : 1.0, g0, b0, g, bb, _dP, _dQ);
            if (fl.Tap < 0)
                return EndPower(x[p.Va(blk, a)], x[p.Va(blk, b)], x[p.Vm(blk, a)], x[p.Vm(blk, b)], g0, b0, g, bb, _dP, _dQ);
            return TapEndPower(x[p.Va(blk, a)], x[p.Va(blk, b)], x[p.Vm(blk, a)], x[p.Vm(blk, b)], x[p.Taps[fl.Tap].Var], from, g0, b0, g, bb, _dP, _dQ);
        }

        /// <summary>Evaluate f (returned), g, h, df, the Jacobian of g and the flow-row gradients at <paramref name="x"/>.</summary>
        public double Evaluate(double[] x)
        {
            Problem p = P;
            Array.Clear(Df);
            double fv = Cost(p, x, Scale, Df, null);
            for (int r = 0; r < Neq; r++) JacG[r].Clear();
            Balance(p, x, G, JacG);
            for (int r = 0; r < p.Fixed.Length; r++)
            {
                (int v, double val) = p.Fixed[r];
                G[NBal + r] = x[v] - val;
                JacG[NBal + r].Add((v, 1.0));
            }
            for (int r = 0; r < p.LinEq.Length; r++)
            {
                LinRow lr = p.LinEq[r];
                int row = NBal + p.Fixed.Length + r;
                G[row] = lr.Value(x) - lr.Rhs;
                foreach ((int v, double c) in lr.Terms()) JacG[row].Add((v, c));
            }
            for (int row = 0; row < 2 * NFlow; row++)
            {
                (double pp, double qq) = RowPower(row, x);
                H[row] = pp * pp + qq * qq - p.Flows[row % NFlow].Smax2;
                int nv = FlowVarList[row].Length;
                for (int q = 0; q < nv; q++) FlowGrad[row, q] = 2 * pp * _dP[q] + 2 * qq * _dQ[q];
            }
            for (int r = 0; r < p.Bounds.Length; r++)
            {
                BoundRow br = p.Bounds[r];
                H[2 * NFlow + r] = br.Sign * (x[br.Var] - br.Limit);
            }
            for (int r = 0; r < p.LinIq.Length; r++)
            {
                LinRow lr = p.LinIq[r];
                H[2 * NFlow + NBound + r] = lr.Value(x) - lr.Rhs;
            }
            return fv;
        }

        /// <summary>Lx = df + dg' lam + dh' mu at the point of the last <see cref="Evaluate"/>.</summary>
        public void Gradient(double[] lam, double[] mu)
        {
            Array.Copy(Df, Lx, Nx);
            for (int r = 0; r < Neq; r++)
            {
                double lr = lam[r];
                if (lr == 0) continue;
                foreach ((int v, double val) in JacG[r]) Lx[v] += val * lr;
            }
            for (int row = 0; row < 2 * NFlow; row++)
            {
                int[] vars = FlowVarList[row];
                double m = mu[row];
                for (int q = 0; q < vars.Length; q++) Lx[vars[q]] += FlowGrad[row, q] * m;
            }
            for (int r = 0; r < P.Bounds.Length; r++)
                Lx[P.Bounds[r].Var] += P.Bounds[r].Sign * mu[2 * NFlow + r];
            for (int r = 0; r < P.LinIq.Length; r++)
            {
                LinRow lr = P.LinIq[r];
                double m = mu[2 * NFlow + NBound + r];
                foreach ((int v, double c) in lr.Terms()) Lx[v] += c * m;
            }
        }

        /// <summary>The Hessian of the Lagrangian at <paramref name="x"/>: the cost, the power balance weighted by
        /// <paramref name="lam"/> and the flow limits weighted by <paramref name="mu"/> (the bound and coupling rows are
        /// linear).</summary>
        public HessianAccumulator Hessian(double[] x, double[] lam, double[] mu)
        {
            Problem p = P;
            int nb = p.Nb;
            var h = new HessianAccumulator();
            var costHess = new double[Nx];
            Cost(p, x, Scale, null, costHess);
            for (int j = 0; j < Nx; j++) h.Add(j, j, costHess[j]);
            for (int bk = 0; bk < p.K; bk++)
            {
                Net net = p.Nets[bk];
                int rb = bk * 2 * nb;
                for (int i = 0; i < nb; i++)
                {
                    double wP = lam[rb + i], wQ = lam[rb + nb + i];
                    if (wP == 0 && wQ == 0) continue;
                    double vi = x[p.Vm(bk, i)];
                    h.Add(p.Vm(bk, i), p.Vm(bk, i), 2 * (wP * net.Gd[i] - wQ * net.Bd[i]));
                    foreach (Cell c in net.Row[i])
                    {
                        (double phi, double dphi) = Pair(x[p.Va(bk, i)], x[p.Va(bk, c.K)], c.G, c.B, wP, wQ);
                        AddPairHessian(h, p.Va(bk, i), p.Va(bk, c.K), p.Vm(bk, i), p.Vm(bk, c.K), vi, x[p.Vm(bk, c.K)], phi, dphi);
                    }
                }
            }
            foreach (TapCtl t in p.Taps)
            {
                double u = x[t.Var];
                for (int end = 0; end < 2; end++)
                {
                    bool from = end == 0;
                    int a = from ? t.F : t.T, b = from ? t.T : t.F;
                    double wP = lam[a], wQ = lam[nb + a];
                    if (wP == 0 && wQ == 0) continue;
                    int[] vars = { p.Va(0, a), p.Va(0, b), p.Vm(0, a), p.Vm(0, b), t.Var };
                    if (from) AddTapEndHessian(h, vars, x[vars[0]], x[vars[1]], x[vars[2]], x[vars[3]], u, true, t.Gff, t.Bff, t.Gft, t.Bft, wP, wQ);
                    else AddTapEndHessian(h, vars, x[vars[0]], x[vars[1]], x[vars[2]], x[vars[3]], u, false, t.Gtt, t.Btt, t.Gtf, t.Btf, wP, wQ);
                }
            }
            foreach (ShuntCtl s in p.Shunts)
            {
                double wQ = lam[nb + s.Bus];
                if (wQ == 0) continue;
                int vmv = p.Vm(0, s.Bus);
                h.Add(vmv, vmv, wQ * (-2 * x[s.Var]));
                h.AddSym(vmv, s.Var, wQ * (-2 * x[vmv]));
            }
            foreach (PstCtl t in p.Psts)
            {
                double phi = x[t.Var];
                for (int end = 0; end < 2; end++)
                {
                    bool from = end == 0;
                    int a = from ? t.F : t.T, b = from ? t.T : t.F;
                    double wP = lam[a], wQ = lam[nb + a];
                    if (wP == 0 && wQ == 0) continue;
                    int[] vars = { p.Va(0, a), p.Va(0, b), p.Vm(0, a), p.Vm(0, b), t.Var };
                    if (from) AddPstEndHessian(h, vars, x[vars[0]], x[vars[1]], x[vars[2]], x[vars[3]], phi, -1.0, t.Gff, t.Bff, t.Gft, t.Bft, wP, wQ);
                    else AddPstEndHessian(h, vars, x[vars[0]], x[vars[1]], x[vars[2]], x[vars[3]], phi, +1.0, t.Gtt, t.Btt, t.Gtf, t.Btf, wP, wQ);
                }
            }
            for (int row = 0; row < 2 * NFlow; row++)
            {
                double m = mu[row];
                if (m == 0) continue;
                FlowLimit fl = p.Flows[row % NFlow];
                int blk = fl.Block;
                (int a, int b, double g0, double b0, double g, double bb, bool from) = End(row);
                (double pp, double qq) = RowPower(row, x);
                int[] vars = FlowVarList[row];
                int nv = vars.Length;
                for (int r = 0; r < nv; r++)
                    for (int c = 0; c < nv; c++)
                        h.Add(vars[r], vars[c], m * 2 * (_dP[r] * _dP[c] + _dQ[r] * _dQ[c]));
                if (fl.Tap >= 0)
                {
                    AddTapEndHessian(h, vars, x[vars[0]], x[vars[1]], x[vars[2]], x[vars[3]], x[vars[4]], from, g0, b0, g, bb, m * 2 * pp, m * 2 * qq);
                    continue;
                }
                if (fl.Pst >= 0)
                {
                    AddPstEndHessian(h, vars, x[vars[0]], x[vars[1]], x[vars[2]], x[vars[3]], x[vars[4]], from ? -1.0 : 1.0, g0, b0, g, bb, m * 2 * pp, m * 2 * qq);
                    continue;
                }
                (double phi, double dphi) = Pair(x[p.Va(blk, a)], x[p.Va(blk, b)], g, bb, m * 2 * pp, m * 2 * qq);
                AddPairHessian(h, vars[0], vars[1], vars[2], vars[3], x[p.Vm(blk, a)], x[p.Vm(blk, b)], phi, dphi);
                h.Add(vars[2], vars[2], m * (2 * pp * 2 * g0 - 2 * qq * 2 * b0));
            }
            return h;
        }
    }

    // ---- Internal hooks for the derivative check (tests compare these with central finite differences) -------------

    /// <summary>The number of variables, equality rows and inequality rows of the problem.</summary>
    internal static (int Nx, int Neq, int Niq) Dimensions(GridModel model, AcOpfExtendedOptions opt)
    {
        var e = new Evaluator(new Problem(model, opt, null), 1.0);
        return (e.Nx, e.Neq, e.Niq);
    }

    /// <summary>MIPS's starting point.</summary>
    internal static double[] StartingPoint(GridModel model, AcOpfExtendedOptions opt) => Start(new Problem(model, opt, null));

    /// <summary>The Lagrangian L = f + lam' g + mu' h at <paramref name="x"/> (unscaled objective).</summary>
    internal static double Lagrangian(GridModel model, AcOpfExtendedOptions opt, double[] x, double[] lam, double[] mu)
    {
        var e = new Evaluator(new Problem(model, opt, null), 1.0);
        double l = e.Evaluate(x);
        for (int r = 0; r < e.Neq; r++) l += lam[r] * e.G[r];
        for (int r = 0; r < e.Niq; r++) l += mu[r] * e.H[r];
        return l;
    }

    /// <summary>The analytic gradient of <see cref="Lagrangian"/>.</summary>
    internal static double[] LagrangianGradient(GridModel model, AcOpfExtendedOptions opt, double[] x, double[] lam, double[] mu)
    {
        var e = new Evaluator(new Problem(model, opt, null), 1.0);
        e.Evaluate(x);
        e.Gradient(lam, mu);
        return (double[])e.Lx.Clone();
    }

    /// <summary>The analytic Hessian of <see cref="Lagrangian"/>, dense.</summary>
    internal static double[,] LagrangianHessian(GridModel model, AcOpfExtendedOptions opt, double[] x, double[] lam, double[] mu)
    {
        var e = new Evaluator(new Problem(model, opt, null), 1.0);
        var d = new double[e.Nx, e.Nx];
        foreach ((int r, int c, double v) in e.Hessian(x, lam, mu).Entries) d[r, c] += v;
        return d;
    }

    // ---- The interior point iteration ---------------------------------------------------------------------------------

    /// <summary>Starting point (MATPOWER mipsopf_solver): the middle of each variable's bounds (a variable with one finite
    /// bound starts at that bound, with none at 0); every angle of every block at the first slack bus's angle.</summary>
    private static double[] Start(Problem p)
    {
        var x = new double[p.Nx];
        for (int j = 0; j < p.Nx; j++)
        {
            double lo = p.XMin[j], hi = p.XMax[j];
            bool hasLo = lo > -NoBound, hasHi = hi < NoBound;
            x[j] = hasLo && hasHi ? (lo + hi) / 2 : hasLo ? lo : hasHi ? hi : 0.0;
        }
        double refVa = p.XMin[p.Va(0, p.RefBus)];
        for (int bk = 0; bk < p.K; bk++)
            for (int i = 0; i < p.Nb; i++) x[p.Va(bk, i)] = refVa;
        return x;
    }

    private static AcOpfExtendedResult Run(Problem p, AcOpfIpmOptions opt)
    {
        double scale = opt.CostScale;
        var ev = new Evaluator(p, scale);
        int nx = ev.Nx, neq = ev.Neq, niq = ev.Niq, nFlow = ev.NFlow;
        double[] gVec = ev.G, hVec = ev.H, lx = ev.Lx;
        List<(int Var, double Val)>[] jacG = ev.JacG;
        double[,] flowGrad = ev.FlowGrad;

        double[] x = Start(p);
        var lam = new double[neq];
        var z = new double[niq];
        var mu = new double[niq];
        var log = new List<AcOpfIpmIteration>();

        double f = ev.Evaluate(x);

        // MIPS initial multipliers and slacks: z = 1 (or -h where h < -1), mu = 1, lam = 0, gamma = 1.
        double gamma = 1.0;
        const double z0 = 1.0;
        for (int r = 0; r < niq; r++)
        {
            z[r] = hVec[r] < -z0 ? -hVec[r] : z0;
            mu[r] = z0;
            if (gamma / z[r] > z0) mu[r] = gamma / z[r];
        }
        ev.Gradient(lam, mu);

        double f0 = f;
        (double feas, double grad, double comp, double costc) = Conditions(x, z, lam, mu, gVec, hVec, lx, f, f0, first: true);
        log.Add(new AcOpfIpmIteration(0, f / scale, feas, grad, comp, costc, gamma, 0, 0));

        AcOpfIpmStatus status = AcOpfIpmStatus.IterationCap;
        string message = "";
        int it = 0;
        bool converged = feas < opt.FeasibilityTolerance && grad < opt.GradientTolerance && comp < opt.ComplementarityTolerance && costc < opt.CostTolerance;
        if (converged) status = AcOpfIpmStatus.Converged;
        bool barrierFloor = false;
        string restoreMessage = "";

        int n = nx + neq;
        int linBase = 2 * nFlow + p.Bounds.Length;
        var rhs = new double[n];
        var dz = new double[niq];
        var dmu = new double[niq];
        while (!converged && it < opt.MaxIterations)
        {
            it++;

            HessianAccumulator h = ev.Hessian(x, lam, mu);

            // M = Lxx + dh diag(mu / z) dh';  N = Lx + dh ((mu .* h + gamma) ./ z).
            var nVec = (double[])lx.Clone();
            for (int row = 0; row < 2 * nFlow; row++)
            {
                int[] vars = ev.FlowVarList[row];
                int nv = vars.Length;
                double w = mu[row] / z[row];
                double t = (mu[row] * hVec[row] + gamma) / z[row];
                for (int r = 0; r < nv; r++)
                {
                    nVec[vars[r]] += flowGrad[row, r] * t;
                    for (int c = 0; c < nv; c++) h.Add(vars[r], vars[c], w * flowGrad[row, r] * flowGrad[row, c]);
                }
            }
            for (int r = 0; r < p.Bounds.Length; r++)
            {
                int row = 2 * nFlow + r;
                BoundRow br = p.Bounds[r];
                h.Add(br.Var, br.Var, mu[row] / z[row]);   // Sign^2 = 1
                nVec[br.Var] += br.Sign * (mu[row] * hVec[row] + gamma) / z[row];
            }
            for (int r = 0; r < p.LinIq.Length; r++)
            {
                int row = linBase + r;
                LinRow lr = p.LinIq[r];
                double w = mu[row] / z[row];
                double t = (mu[row] * hVec[row] + gamma) / z[row];
                if (lr.Extra is null)
                {
                    nVec[lr.A] += lr.Ca * t;
                    nVec[lr.B] += lr.Cb * t;
                    h.Add(lr.A, lr.A, w * lr.Ca * lr.Ca);
                    h.Add(lr.A, lr.B, w * lr.Ca * lr.Cb);
                    h.Add(lr.B, lr.A, w * lr.Cb * lr.Ca);
                    h.Add(lr.B, lr.B, w * lr.Cb * lr.Cb);
                    continue;
                }
                (int Var, double C)[] terms = lr.Terms().ToArray();
                foreach ((int v, double c) in terms) nVec[v] += c * t;
                foreach ((int a, double ca) in terms)
                    foreach ((int b, double cb) in terms) h.Add(a, b, w * ca * cb);
            }

            // The reduced KKT system [M J'; J 0] [dx; dlam] = [-N; -g].
            var kkt = new CsrMatrix.Builder(n);
            foreach ((int r, int c, double v) in h.Entries) kkt.Add(r, c, v);
            for (int r = 0; r < neq; r++)
                foreach ((int v, double val) in jacG[r])
                {
                    kkt.Add(nx + r, v, val);
                    kkt.Add(v, nx + r, val);
                }
            for (int j = 0; j < nx; j++) rhs[j] = -nVec[j];
            for (int r = 0; r < neq; r++) rhs[nx + r] = -gVec[r];
            double[]? sol = SolveScaled(kkt.Build(), rhs);
            if (sol is null || Array.Exists(sol, v => !double.IsFinite(v)))
            {
                status = AcOpfIpmStatus.NumericalFailure;
                message = $"the Newton (KKT) system of iteration {it} is singular or gave a value that is not a number";
                break;
            }

            // dz = -h - z - dh' dx;  dmu = -mu + (gamma - mu .* dz) ./ z.
            for (int row = 0; row < 2 * nFlow; row++)
            {
                int[] vars = ev.FlowVarList[row];
                double dhdx = flowGrad[row, 0] * sol[vars[0]] + flowGrad[row, 1] * sol[vars[1]] + flowGrad[row, 2] * sol[vars[2]] + flowGrad[row, 3] * sol[vars[3]];
                if (vars.Length == 5) dhdx += flowGrad[row, 4] * sol[vars[4]];
                dz[row] = -hVec[row] - z[row] - dhdx;
            }
            for (int r = 0; r < p.Bounds.Length; r++)
            {
                int row = 2 * nFlow + r;
                dz[row] = -hVec[row] - z[row] - p.Bounds[r].Sign * sol[p.Bounds[r].Var];
            }
            for (int r = 0; r < p.LinIq.Length; r++)
            {
                int row = linBase + r;
                LinRow lr = p.LinIq[r];
                dz[row] = -hVec[row] - z[row] - lr.Value(sol);
            }
            for (int r = 0; r < niq; r++) dmu[r] = -mu[r] + (gamma - mu[r] * dz[r]) / z[r];

            // Step lengths: the fraction xi of the step to the boundary, at most 1.
            double alphaP = 1.0, alphaD = 1.0;
            for (int r = 0; r < niq; r++)
            {
                if (dz[r] < 0) alphaP = Math.Min(alphaP, opt.StepToBoundary * (z[r] / -dz[r]));
                if (dmu[r] < 0) alphaD = Math.Min(alphaD, opt.StepToBoundary * (mu[r] / -dmu[r]));
            }
            for (int j = 0; j < nx; j++) x[j] += alphaP * sol[j];
            for (int r = 0; r < niq; r++) { z[r] += alphaP * dz[r]; mu[r] += alphaD * dmu[r]; }
            for (int r = 0; r < neq; r++) lam[r] += alphaD * sol[nx + r];
            if (niq > 0)
            {
                double zmu = 0;
                for (int r = 0; r < niq; r++) zmu += z[r] * mu[r];
                gamma = opt.Centring * zmu / niq;
            }

            f0 = f;
            f = ev.Evaluate(x);
            ev.Gradient(lam, mu);
            (feas, grad, comp, costc) = Conditions(x, z, lam, mu, gVec, hVec, lx, f, f0, first: false);
            log.Add(new AcOpfIpmIteration(it, f / scale, feas, grad, comp, costc, gamma, alphaP, alphaD));

            // MIPS order: the convergence test first, then the breakdown tests.
            converged = feas < opt.FeasibilityTolerance && grad < opt.GradientTolerance && comp < opt.ComplementarityTolerance && costc < opt.CostTolerance;
            if (converged) { status = AcOpfIpmStatus.Converged; break; }
            // The barrier floor: with many inequalities the barrier gamma = sigma z'mu / niq falls below machine epsilon
            // while the complementarity test (z'mu / (1 + |x|inf) below its tolerance) has not yet passed, because both
            // read the same z'mu. When every other test has passed and the state is finite, that is the end of the
            // iteration at the barrier floor, not a breakdown. Runs that converge are not touched: this test comes after
            // theirs.
            if (gamma < MachineEpsilon && feas < opt.FeasibilityTolerance && grad < opt.GradientTolerance && costc < opt.CostTolerance
                && !Array.Exists(x, v => !double.IsFinite(v)))
            {
                status = AcOpfIpmStatus.Converged;
                converged = true;
                barrierFloor = true;
                break;
            }
            // The losses and reactive power objectives only: the barrier floor with every test passed but feasibility.
            // A stacked (AC-SCOPF) problem's outage block has no objective of its own, so the free directions of its
            // state (a unit's voltage after the outage, say) are fixed by the barrier alone. In such a direction the
            // primal-dual step moves only sigma of the Newton step to the analytic centre (the dual update absorbs the
            // rest), so the error there falls by (1 - sigma) = 0.9 per iteration, while the full step leaves an equality
            // residual of the order |Y| |dx|^2, falling by 0.81, and the barrier falls by sigma = 0.1. The barrier reaches
            // machine epsilon (about iteration 16 to 18) with the residual near 1e-8: 1e-12 would need about 130
            // iterations. That is the iteration's own rate, not a limit of double precision, so the tolerance stays and
            // the point is projected onto g = 0 (RestoreFeasibility) and then held to the same four tests.
            if (p.Objective != AcOpfObjective.Cost && gamma < MachineEpsilon && !(feas < opt.FeasibilityTolerance)
                && grad < opt.GradientTolerance && costc < opt.CostTolerance && !Array.Exists(x, v => !double.IsFinite(v)))
            {
                double fFloor = f;
                var xFloor = (double[])x.Clone();
                int steps = RestoreFeasibility(p, ev, x, z, mu, opt);
                f = ev.Evaluate(x);
                ev.Gradient(lam, mu);
                (feas, grad, comp, costc) = Conditions(x, z, lam, mu, gVec, hVec, lx, f, fFloor, first: false);
                double moved = 0;
                for (int j = 0; j < nx; j++) moved = Math.Max(moved, Math.Abs(x[j] - xFloor[j]));
                if (steps >= 0 && feas < opt.FeasibilityTolerance && grad < opt.GradientTolerance && costc < opt.CostTolerance
                    && !Array.Exists(x, v => !double.IsFinite(v)))
                {
                    status = AcOpfIpmStatus.Converged;
                    converged = true;
                    barrierFloor = true;
                    restoreMessage = $", after a feasibility correction ({steps} Newton step(s), largest change {Fmt(moved)}, feasibility {Fmt(feas)})";
                    break;
                }
                status = AcOpfIpmStatus.NumericalFailure;
                message = $"iteration {it} reached the barrier floor with feasibility {Fmt(feas)} and the feasibility correction did not "
                    + $"meet the tolerance {Fmt(opt.FeasibilityTolerance)} (gradient {Fmt(grad)}, cost change {Fmt(costc)})";
                break;
            }
            if (Array.Exists(x, v => !double.IsFinite(v)) || alphaP < MinStep || alphaD < MinStep
                || gamma < MachineEpsilon || gamma > 1 / MachineEpsilon)
            {
                status = AcOpfIpmStatus.NumericalFailure;
                message = $"iteration {it} broke down (primal step {Fmt(alphaP)}, dual step {Fmt(alphaD)}, barrier {Fmt(gamma)})";
                break;
            }
        }
        if (status == AcOpfIpmStatus.IterationCap)
            message = $"no convergence within {opt.MaxIterations} iteration(s)";
        else if (status == AcOpfIpmStatus.Converged)
            message = barrierFloor ? $"converged in {it} iteration(s) at the barrier floor{restoreMessage}" : $"converged in {it} iteration(s)";
        else if (status == AcOpfIpmStatus.NumericalFailure && InfeasibleSignature(log, opt) is { } window)
        {
            // The signature of an infeasible problem: when the constraints cannot all be met, the feasibility measure stops
            // falling while the multipliers of the constraints that conflict grow without bound, until the Newton system
            // is singular or the steps vanish. The signature is evidence, not proof, so the message says "likely
            // infeasible" and names the constraints with the largest final multipliers, instead of a numerical failure.
            status = AcOpfIpmStatus.Infeasible;
            message = $"likely infeasible (the signature of a problem with no feasible point): from iteration {window.From} to {window.To} the feasibility measure stayed at "
                + $"{Fmt(window.FeasFrom)} .. {Fmt(window.FeasTo)} while the complementarity grew from {Fmt(window.CompFrom)} to {Fmt(window.CompTo)}, "
                + $"then the iteration stopped ({message}); the constraints with the largest multipliers: "
                + string.Join(", ", GrowingConstraints(p, mu))
                + (p.Objective == AcOpfObjective.ReactivePower ? "; reactive power optimisation holds every unit off the slack bus at its case output" : "");
        }

        // MIPS: zero the multiplier of an inequality that is clearly slack and has a negligible multiplier.
        for (int r = 0; r < niq; r++)
            if (hVec[r] < -opt.FeasibilityTolerance && mu[r] < MuThreshold) mu[r] = 0;

        return BuildResult(p, x, lam, mu, f / scale, scale, status, it, log, message);
    }

    // The infeasibility signature is read over this many iterations before the breakdown.
    private const int InfeasibleWindow = 4;

    /// <summary>
    /// The signature of an infeasible problem in the log of a run that broke down: over the last
    /// <see cref="InfeasibleWindow"/> iterations the feasibility condition did not fall below half its value and stayed above
    /// its tolerance, and the complementarity condition grew (the multipliers of the conflicting constraints diverge: an
    /// interior point method's mark of an empty feasible set, Nocedal and Wright, Numerical Optimization, 2nd ed., 19.1).
    /// Returns the window, or null when the log does not show it.
    /// </summary>
    private static (int From, int To, double FeasFrom, double FeasTo, double CompFrom, double CompTo)? InfeasibleSignature(
        List<AcOpfIpmIteration> log, AcOpfIpmOptions opt)
    {
        if (log.Count <= InfeasibleWindow) return null;
        AcOpfIpmIteration a = log[^(InfeasibleWindow + 1)], b = log[^1];
        bool stalled = b.Feasibility >= opt.FeasibilityTolerance && b.Feasibility >= 0.5 * a.Feasibility;
        bool growing = b.Complementarity > a.Complementarity;
        return stalled && growing ? (a.Iteration, b.Iteration, a.Feasibility, b.Feasibility, a.Complementarity, b.Complementarity) : null;
    }

    /// <summary>The inequality constraints with the largest multipliers (at least a thousandth of the largest, at most
    /// eight, largest first, ties in row order), named as the binding list names them.</summary>
    private static List<string> GrowingConstraints(Problem p, double[] mu)
    {
        double max = 0;
        foreach (double v in mu) if (double.IsFinite(v)) max = Math.Max(max, v);
        var rows = new List<int>();
        for (int r = 0; r < mu.Length; r++) if (double.IsFinite(mu[r]) && max > 0 && mu[r] >= 1e-3 * max) rows.Add(r);
        rows.Sort((a, b) => mu[b] != mu[a] ? mu[b].CompareTo(mu[a]) : a.CompareTo(b));
        var names = new List<string>();
        foreach (int r in rows.Take(8)) names.Add(RowName(p, r));
        return names;
    }

    /// <summary>The name of inequality row <paramref name="row"/>: a flow limit, a bound or a coupling row.</summary>
    private static string RowName(Problem p, int row)
    {
        int nFlow = p.Flows.Length;
        string After(int bk) => bk == 0 ? "" : p.Nets[bk].Label is { } l ? $" after outage {l}" : $" after outage of branch {p.Nets[bk].Outage}";
        if (row < 2 * nFlow)
        {
            FlowLimit fl = p.Flows[row % nFlow];
            Branch br = p.Model.Branches[fl.Branch];
            return $"{(row < nFlow ? "Sf" : "St")} branch {fl.Branch} ({br.From}-{br.To}){After(fl.Block)}";
        }
        int bRow = row - 2 * nFlow;
        if (bRow < p.Bounds.Length)
        {
            BoundRow br = p.Bounds[bRow];
            string side = br.Sign > 0 ? "max" : "min";
            if (br.Var >= p.K * p.NxB)
            {
                int j = br.Var - p.K * p.NxB;
                return j < p.Taps.Length ? $"tap {side} {p.Taps[j].Name}"
                    : j < p.Taps.Length + p.Shunts.Length ? $"shunt {side} bus {p.Shunts[j - p.Taps.Length].BusId}"
                    : $"pst {side} {p.Psts[j - p.Taps.Length - p.Shunts.Length].Name}";
            }
            int bk = br.Var / p.NxB, i = br.Var % p.NxB;
            if (i < p.Nb) return $"Va{side} bus {p.Model.Buses[i].Id}{After(bk)}";
            if (i < 2 * p.Nb) return $"V{side} bus {p.Model.Buses[i - p.Nb].Id}{After(bk)}";
            int g = i < 2 * p.Nb + p.Ng ? i - 2 * p.Nb : i - 2 * p.Nb - p.Ng;
            string q = i < 2 * p.Nb + p.Ng ? "P" : "Q";
            return $"{q}{side} generator {g} at bus {p.Model.Generators[g].Bus}{After(bk)}";
        }
        LinRow lr = p.LinIq[bRow - p.Bounds.Length];
        return $"{(lr.Ca > 0 ? "RampUp" : "RampDown")} generator {lr.Gen}{After(lr.Block)}";
    }

    // Newton steps allowed to the feasibility correction. It converges quadratically: a residual r leaves about
    // |Y| (r / s)^2 after one step (s the smallest singular value of the equality Jacobian), so a residual near 1e-6 pu
    // needs two or three steps.
    private const int RestoreSteps = 8;

    /// <summary>
    /// Project <paramref name="x"/> onto the equality constraints g(x) = 0 by Newton steps of the weighted least-change
    /// problem: min 1/2 d' W d subject to g + J d = 0, that is [W J'; J 0] [d; y] = [0; -g]. W = I + dh' diag(mu / z) dh,
    /// the identity plus the barrier weights of the inequality rows, so a variable on a binding limit (mu / z very large)
    /// stays put and a free one takes the correction. Stops when the feasibility condition holds. Returns the number of
    /// steps taken, or -1 when a step is singular or not finite (<paramref name="x"/> is then the last finite point).
    /// </summary>
    private static int RestoreFeasibility(Problem p, Evaluator ev, double[] x, double[] z, double[] mu, AcOpfIpmOptions opt)
    {
        int nx = ev.Nx, neq = ev.Neq, nFlow = ev.NFlow, n = nx + neq;
        int linBase = 2 * nFlow + p.Bounds.Length;
        var rhs = new double[n];
        for (int step = 0; ; step++)
        {
            ev.Evaluate(x);
            double maxH = double.NegativeInfinity;
            foreach (double v in ev.H) maxH = Math.Max(maxH, v);
            double feas = Math.Max(NormInf(ev.G), maxH) / (1 + Math.Max(NormInf(x), NormInf(z)));
            if (feas < opt.FeasibilityTolerance) return step;
            if (step == RestoreSteps) return -1;

            var w = new HessianAccumulator();
            for (int j = 0; j < nx; j++) w.Add(j, j, 1.0);
            for (int row = 0; row < 2 * nFlow; row++)
            {
                int[] vars = ev.FlowVarList[row];
                double wr = mu[row] / z[row];
                for (int r = 0; r < vars.Length; r++)
                    for (int c = 0; c < vars.Length; c++) w.Add(vars[r], vars[c], wr * ev.FlowGrad[row, r] * ev.FlowGrad[row, c]);
            }
            for (int r = 0; r < p.Bounds.Length; r++)
            {
                int row = 2 * nFlow + r;
                w.Add(p.Bounds[r].Var, p.Bounds[r].Var, mu[row] / z[row]);
            }
            for (int r = 0; r < p.LinIq.Length; r++)
            {
                int row = linBase + r;
                LinRow lr = p.LinIq[r];
                double wr = mu[row] / z[row];
                if (lr.Extra is null)
                {
                    w.Add(lr.A, lr.A, wr * lr.Ca * lr.Ca);
                    w.Add(lr.A, lr.B, wr * lr.Ca * lr.Cb);
                    w.Add(lr.B, lr.A, wr * lr.Cb * lr.Ca);
                    w.Add(lr.B, lr.B, wr * lr.Cb * lr.Cb);
                    continue;
                }
                (int Var, double C)[] terms = lr.Terms().ToArray();
                foreach ((int a, double ca) in terms)
                    foreach ((int b, double cb) in terms) w.Add(a, b, wr * ca * cb);
            }
            var kkt = new CsrMatrix.Builder(n);
            foreach ((int r, int c, double v) in w.Entries) kkt.Add(r, c, v);
            for (int r = 0; r < neq; r++)
                foreach ((int v, double val) in ev.JacG[r])
                {
                    kkt.Add(nx + r, v, val);
                    kkt.Add(v, nx + r, val);
                }
            Array.Clear(rhs);
            for (int r = 0; r < neq; r++) rhs[nx + r] = -ev.G[r];
            double[]? sol = SolveScaled(kkt.Build(), rhs);
            if (sol is null || Array.Exists(sol, v => !double.IsFinite(v))) return -1;
            for (int j = 0; j < nx; j++) x[j] += sol[j];
        }
    }

    /// <summary>Solve K d = r through a symmetric power-of-two equilibration (Ruiz), exactly as
    /// <see cref="AcOpfInteriorPoint"/>. Returns null when the LU finds it singular.</summary>
    private static double[]? SolveScaled(CsrMatrix k, double[] r)
    {
        int n = k.N;
        int[] rowPtr = k.RowPtr, col = k.ColIdx;
        double[] val = k.Val;
        var d = new double[n];
        Array.Fill(d, 1.0);
        var e = new int[n];
        for (int pass = 0; pass < RuizPasses; pass++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                double m = 0;
                for (int q = rowPtr[i]; q < rowPtr[i + 1]; q++) m = Math.Max(m, Math.Abs(val[q]));
                e[i] = m > 0 && double.IsFinite(m) ? -(Math.ILogB(m) / 2) : 0;
                if (e[i] != 0) changed = true;
            }
            if (!changed) break;
            for (int i = 0; i < n; i++)
            {
                d[i] = Math.ScaleB(d[i], e[i]);
                for (int q = rowPtr[i]; q < rowPtr[i + 1]; q++) val[q] = Math.ScaleB(val[q], e[i] + e[col[q]]);
            }
        }
        var rs = new double[n];
        for (int i = 0; i < n; i++) rs[i] = r[i] * d[i];
        double[]? y = SparseLu.Solve(k, rs);
        if (y is null) return null;
        for (int i = 0; i < n; i++) y[i] *= d[i];
        return y;
    }

    private static (double Feas, double Grad, double Comp, double Cost) Conditions(
        double[] x, double[] z, double[] lam, double[] mu, double[] g, double[] h, double[] lx, double f, double f0, bool first)
    {
        double xInf = NormInf(x), zInf = NormInf(z), lamInf = NormInf(lam), muInf = NormInf(mu);
        double maxH = double.NegativeInfinity;
        foreach (double v in h) maxH = Math.Max(maxH, v);
        double feas = Math.Max(NormInf(g), maxH) / (1 + Math.Max(xInf, zInf));
        double grad = NormInf(lx) / (1 + Math.Max(lamInf, muInf));
        double zmu = 0;
        for (int r = 0; r < z.Length; r++) zmu += z[r] * mu[r];
        double comp = zmu / (1 + xInf);
        double cost = first ? 1.0 : Math.Abs(f - f0) / (1 + Math.Abs(f0));
        return (feas, grad, comp, cost);
    }

    private static double NormInf(double[] v)
    {
        double m = 0;
        foreach (double e in v) m = Math.Max(m, Math.Abs(e));
        return m;
    }

    private static AcOpfExtendedResult BuildResult(Problem p, double[] x, double[] lam, double[] mu,
        double objective, double scale, AcOpfIpmStatus status, int iterations, List<AcOpfIpmIteration> log, string message)
    {
        GridModel m = p.Model;
        int nFlow = p.Flows.Length;

        // Unscaled multipliers per bound: lower and upper of every variable.
        var muLo = new double[p.Nx];
        var muHi = new double[p.Nx];
        for (int r = 0; r < p.Bounds.Length; r++)
        {
            BoundRow br = p.Bounds[r];
            double v = mu[2 * nFlow + r] / scale;
            if (br.Sign > 0) muHi[br.Var] = v; else muLo[br.Var] = v;
        }
        double totalLoad = 0;
        foreach (Bus b in m.Buses) totalLoad += b.Pd;

        var blocks = new List<AcOpfBlockResult>();
        AcOpfIpmResult? baseResult = null;
        for (int bk = 0; bk < p.K; bk++)
        {
            (AcOpfBusResult[] buses, AcOpfGenResult[] gens, List<AcOpfBranchResult> branches, List<AcOpfBinding> binding, double totalGen)
                = BuildBlock(p, bk, x, lam, mu, muLo, muHi, scale, status);
            if (bk == 0) baseResult = new AcOpfIpmResult(status, iterations, objective, totalGen, totalLoad, buses, gens, branches, binding, log, message);
            else blocks.Add(new AcOpfBlockResult(p.Nets[bk].Outage, totalGen, buses, gens, branches, binding) { OutageLabel = p.Nets[bk].Label });
        }

        var controls = new List<OpfControlValue>();
        foreach (TapCtl t in p.Taps)
        {
            double u = x[t.Var];
            // u <= 1/MinTap is tau >= MinTap; u >= 1/MaxTap is tau <= MaxTap. The multipliers are on the bounds of u = 1/tau;
            // d(1/tau)/dtau = -1/tau^2, so the price per unit of tau is mu_u u^2.
            controls.Add(new OpfControlValue("tap", t.Name, 1.0 / u, t.CaseTap, t.MinTap, t.MaxTap, muHi[t.Var] * u * u, muLo[t.Var] * u * u));
        }
        foreach (ShuntCtl s in p.Shunts)
            controls.Add(new OpfControlValue("shunt", $"bus {s.BusId}", x[s.Var] * p.Base, s.InitialMvar, s.BMinMvar, s.BMaxMvar,
                muLo[s.Var] / p.Base, muHi[s.Var] / p.Base));
        // Angles: the variable is in radians, so a price per radian times pi/180 is the price per degree.
        foreach (PstCtl t in p.Psts)
            controls.Add(new OpfControlValue("pst", t.Name, x[t.Var] * 180.0 / Math.PI, t.CaseDeg, t.MinDeg, t.MaxDeg,
                muLo[t.Var] * Math.PI / 180.0, muHi[t.Var] * Math.PI / 180.0));

        var coupling = new List<AcOpfBinding>();
        if (status == AcOpfIpmStatus.Converged)
        {
            int linBase = 2 * nFlow + p.Bounds.Length;
            for (int r = 0; r < p.LinIq.Length; r++)
            {
                double v = mu[linBase + r] / scale / p.Base;
                if (v > 0)
                {
                    LinRow lr = p.LinIq[r];
                    Net net = p.Nets[lr.Block];
                    coupling.Add(new AcOpfBinding(lr.Ca > 0 ? "RampUp" : "RampDown",
                        net.Label is not null ? $"generator {lr.Gen} after outage {net.Label}" : $"generator {lr.Gen} after outage of branch {net.Outage}", v));
                }
            }
        }
        return new AcOpfExtendedResult(p.Objective, baseResult!, controls, blocks, coupling);
    }

    private static (AcOpfBusResult[], AcOpfGenResult[], List<AcOpfBranchResult>, List<AcOpfBinding>, double) BuildBlock(
        Problem p, int bk, double[] x, double[] lam, double[] mu, double[] muLo, double[] muHi, double scale, AcOpfIpmStatus status)
    {
        GridModel m = p.Model;
        int nb = p.Nb, nFlow = p.Flows.Length;
        double bse = p.Base;
        int rb = bk * 2 * nb;

        var buses = new AcOpfBusResult[nb];
        for (int i = 0; i < nb; i++)
            buses[i] = new AcOpfBusResult(m.Buses[i].Id, x[p.Vm(bk, i)], x[p.Va(bk, i)] * 180.0 / Math.PI,
                lam[rb + i] / scale / bse, lam[rb + nb + i] / scale / bse, muLo[p.Vm(bk, i)], muHi[p.Vm(bk, i)]);

        var gens = new AcOpfGenResult[p.Ng];
        double totalGen = 0;
        for (int g = 0; g < p.Ng; g++)
        {
            Generator gen = m.Generators[g];
            double pg = x[p.Pg(bk, g)] * bse;
            totalGen += pg;
            gens[g] = new AcOpfGenResult(g, gen.Bus, gen.Name, pg, x[p.Qg(bk, g)] * bse,
                muLo[p.Pg(bk, g)] / bse, muHi[p.Pg(bk, g)] / bse, muLo[p.Qg(bk, g)] / bse, muHi[p.Qg(bk, g)] / bse);
        }

        // Branch flows for every in-service branch of the block; the flow-limit multipliers of the rated ones.
        var muSf = new double[m.Branches.Count];
        var muSt = new double[m.Branches.Count];
        for (int l = 0; l < nFlow; l++)
        {
            FlowLimit fl = p.Flows[l];
            if (fl.Block != bk) continue;
            muSf[fl.Branch] = 2 * (mu[l] / scale) * fl.SmaxPu / bse;
            muSt[fl.Branch] = 2 * (mu[nFlow + l] / scale) * fl.SmaxPu / bse;
        }
        var index = new Dictionary<int, int>(nb);
        for (int i = 0; i < nb; i++) index[m.Buses[i].Id] = i;
        var tapOf = new Dictionary<int, TapCtl>();
        if (bk == 0) foreach (TapCtl t in p.Taps) tapOf[t.Branch] = t;
        var pstOf = new Dictionary<int, PstCtl>();
        if (bk == 0) foreach (PstCtl t in p.Psts) pstOf[t.Branch] = t;
        var branches = new List<AcOpfBranchResult>();
        var dP = new double[5];
        var dQ = new double[5];
        for (int k = 0; k < m.Branches.Count; k++)
        {
            Branch br = m.Branches[k];
            if (!br.InService || p.Nets[bk].IsOut(k)) continue;
            int f = index[br.From], t = index[br.To];
            double pf, qf, pt, qt;
            if (pstOf.TryGetValue(k, out PstCtl? pc))
            {
                double phi = x[pc.Var];
                (pf, qf) = PstEndPower(x[p.Va(bk, f)], x[p.Va(bk, t)], x[p.Vm(bk, f)], x[p.Vm(bk, t)], phi, -1.0, pc.Gff, pc.Bff, pc.Gft, pc.Bft, dP, dQ);
                (pt, qt) = PstEndPower(x[p.Va(bk, t)], x[p.Va(bk, f)], x[p.Vm(bk, t)], x[p.Vm(bk, f)], phi, +1.0, pc.Gtt, pc.Btt, pc.Gtf, pc.Btf, dP, dQ);
            }
            else if (tapOf.TryGetValue(k, out TapCtl? tc))
            {
                double u = x[tc.Var];
                (pf, qf) = TapEndPower(x[p.Va(bk, f)], x[p.Va(bk, t)], x[p.Vm(bk, f)], x[p.Vm(bk, t)], u, true, tc.Gff, tc.Bff, tc.Gft, tc.Bft, dP, dQ);
                (pt, qt) = TapEndPower(x[p.Va(bk, t)], x[p.Va(bk, f)], x[p.Vm(bk, t)], x[p.Vm(bk, f)], u, false, tc.Gtt, tc.Btt, tc.Gtf, tc.Btf, dP, dQ);
            }
            else
            {
                BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
                (pf, qf) = EndPower(x[p.Va(bk, f)], x[p.Va(bk, t)], x[p.Vm(bk, f)], x[p.Vm(bk, t)], s.Yff.Real, s.Yff.Imaginary, s.Yft.Real, s.Yft.Imaginary, dP, dQ);
                (pt, qt) = EndPower(x[p.Va(bk, t)], x[p.Va(bk, f)], x[p.Vm(bk, t)], x[p.Vm(bk, f)], s.Ytt.Real, s.Ytt.Imaginary, s.Ytf.Real, s.Ytf.Imaginary, dP, dQ);
            }
            double rate = bk == 0 ? br.RateAMva : RatingOfBlock(p, k);
            branches.Add(new AcOpfBranchResult(k, br.From, br.To, br.Name, pf * bse, qf * bse, pt * bse, qt * bse, rate, muSf[k], muSt[k]));
        }

        // Binding constraints: a multiplier left after the MIPS zeroing (in the matching result units).
        var binding = new List<AcOpfBinding>();
        if (status == AcOpfIpmStatus.Converged)
        {
            foreach (AcOpfBusResult b in buses)
            {
                if (b.MuVmin > 0) binding.Add(new AcOpfBinding("Vmin", $"bus {b.BusId}", b.MuVmin));
                if (b.MuVmax > 0) binding.Add(new AcOpfBinding("Vmax", $"bus {b.BusId}", b.MuVmax));
            }
            foreach (AcOpfGenResult g in gens)
            {
                string who = $"generator {g.Index} at bus {g.Bus}";
                if (g.MuPmin > 0) binding.Add(new AcOpfBinding("Pmin", who, g.MuPmin));
                if (g.MuPmax > 0) binding.Add(new AcOpfBinding("Pmax", who, g.MuPmax));
                if (g.MuQmin > 0) binding.Add(new AcOpfBinding("Qmin", who, g.MuQmin));
                if (g.MuQmax > 0) binding.Add(new AcOpfBinding("Qmax", who, g.MuQmax));
            }
            foreach (AcOpfBranchResult br in branches)
            {
                if (br.MuSf > 0) binding.Add(new AcOpfBinding("Sf", $"branch {br.Index} ({br.From}-{br.To})", br.MuSf));
                if (br.MuSt > 0) binding.Add(new AcOpfBinding("St", $"branch {br.Index} ({br.From}-{br.To})", br.MuSt));
            }
        }
        return (buses, gens, branches, binding, totalGen);
    }

    /// <summary>The rating a post-outage block holds branch <paramref name="k"/> to (its flow row's limit, else the
    /// branch's RateA when it has no flow row).</summary>
    private static double RatingOfBlock(Problem p, int k)
    {
        foreach (FlowLimit fl in p.Flows)
            if (fl.Block != 0 && fl.Branch == k) return fl.SmaxPu * p.Base;
        return p.Model.Branches[k].RateAMva;
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
