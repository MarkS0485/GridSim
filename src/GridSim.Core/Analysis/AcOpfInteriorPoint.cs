using System.Globalization;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>The settings of <see cref="AcOpfInteriorPoint.Solve"/>. The defaults are MATPOWER's MIPS defaults
/// (Wang, Murillo-Sanchez, Zimmerman and Thomas, "On computational issues of market-based optimal power flow", IEEE
/// Trans. Power Systems 22(3), 2007; MATPOWER's <c>mips.m</c> and <c>mipsopf_solver.m</c>), so a run here and a MATPOWER
/// <c>runopf</c> stop on the same conditions.</summary>
public sealed record AcOpfIpmOptions
{
    /// <summary>Iteration cap (MIPS <c>max_it</c>, 150).</summary>
    public int MaxIterations { get; init; } = 150;

    /// <summary>Feasibility condition tolerance (MIPS <c>feastol</c>, 1e-6).</summary>
    public double FeasibilityTolerance { get; init; } = 1e-6;

    /// <summary>Gradient (stationarity) condition tolerance (MIPS <c>gradtol</c>, 1e-6).</summary>
    public double GradientTolerance { get; init; } = 1e-6;

    /// <summary>Complementarity condition tolerance (MIPS <c>comptol</c>, 1e-6).</summary>
    public double ComplementarityTolerance { get; init; } = 1e-6;

    /// <summary>Cost change condition tolerance (MIPS <c>costtol</c>, 1e-6).</summary>
    public double CostTolerance { get; init; } = 1e-6;

    /// <summary>The fraction of the step to the boundary that a step may take (MIPS <c>xi</c>, 0.99995).</summary>
    public double StepToBoundary { get; init; } = 0.99995;

    /// <summary>The centring parameter (MIPS <c>sigma</c>, 0.1).</summary>
    public double Centring { get; init; } = 0.1;

    /// <summary>The objective scale used inside the iteration (MATPOWER <c>mipsopf_solver</c> uses 1e-4); every number
    /// returned is unscaled.</summary>
    public double CostScale { get; init; } = 1e-4;
}

/// <summary>How <see cref="AcOpfInteriorPoint.Solve"/> ended.</summary>
public enum AcOpfIpmStatus
{
    /// <summary>All four conditions (feasibility, gradient, complementarity, cost change) met.</summary>
    Converged,
    /// <summary>The iteration cap was reached first.</summary>
    IterationCap,
    /// <summary>The iteration broke down: a singular Newton (KKT) system, a value that is not a number, a step length
    /// below 1e-8, or a barrier parameter outside [eps, 1/eps] (MIPS's own numerical-failure tests).</summary>
    NumericalFailure,
}

/// <summary>One iteration of the interior point method, for the log: the objective (unscaled, cost per hour), MIPS's four
/// relative conditions (feasibility, gradient, complementarity, cost change; dimensionless), the barrier parameter gamma of
/// the scaled problem, and the primal and dual step lengths taken.</summary>
public sealed record AcOpfIpmIteration(
    int Iteration, double Objective, double Feasibility, double Gradient, double Complementarity, double CostChange,
    double Barrier, double StepPrimal, double StepDual);

/// <summary>A bus at the optimum: voltage, and the nodal prices (the multipliers of its active and reactive power
/// balance, in cost per MWh and cost per MVArh) and the shadow prices of its voltage limits (cost per pu per hour).</summary>
public sealed record AcOpfBusResult(int BusId, double VmPu, double VaDeg, double LmpP, double LmpQ, double MuVmin, double MuVmax);

/// <summary>A generator at the optimum: its output and the shadow prices of its limits (cost per MW or MVAr per hour).
/// <see cref="Index"/> is its position in <see cref="GridModel.Generators"/>.</summary>
public sealed record AcOpfGenResult(
    int Index, int Bus, string? Name, double PgMw, double QgMvar, double MuPmin, double MuPmax, double MuQmin, double MuQmax);

/// <summary>A branch at the optimum: the power entering it at each end and, for a rated branch, the shadow price of its
/// MVA limit at each end (cost per MVA per hour; 0 when unrated or not binding). <see cref="Index"/> is its position in
/// <see cref="GridModel.Branches"/>.</summary>
public sealed record AcOpfBranchResult(
    int Index, int From, int To, string? Name, double PfMw, double QfMvar, double PtMw, double QtMvar, double RateMva,
    double MuSf, double MuSt);

/// <summary>A binding constraint at the optimum: what it is, which element, and its shadow price (in the units of the
/// matching result record).</summary>
public sealed record AcOpfBinding(string Kind, string Element, double ShadowPrice);

/// <summary>The result of <see cref="AcOpfInteriorPoint.Solve"/>. When <see cref="Converged"/> is false every number is
/// the state the iteration stopped in, not an optimum: a caller must report the failure (the CLI exits 3 and prints
/// <see cref="Log"/>), never the numbers as a result.</summary>
public sealed record AcOpfIpmResult(
    AcOpfIpmStatus Status,
    int Iterations,
    double ObjectivePerHour,
    double TotalGenerationMw,
    double TotalLoadMw,
    IReadOnlyList<AcOpfBusResult> Buses,
    IReadOnlyList<AcOpfGenResult> Generators,
    IReadOnlyList<AcOpfBranchResult> Branches,
    IReadOnlyList<AcOpfBinding> Binding,
    IReadOnlyList<AcOpfIpmIteration> Log,
    string Message)
{
    /// <summary>True when the method met all four conditions.</summary>
    public bool Converged => Status == AcOpfIpmStatus.Converged;

    /// <summary>Active power losses at the optimum (MW): generation minus load.</summary>
    public double LossesMw => TotalGenerationMw - TotalLoadMw;
}

/// <summary>
/// A full AC optimal power flow, solved by a primal-dual interior point method (MATPOWER's MIPS, step for step). Unlike
/// <see cref="AcOptimalPowerFlow"/> (a DC dispatch with an AC loss loop, left exactly as it was), this optimises the
/// whole AC state at once.
///
/// <para><b>The problem.</b> Variables: every bus voltage angle and magnitude and every in-service generator's active and
/// reactive output (per unit on the system base, angles in radians). Minimise the sum of the generators' polynomial
/// cost curves (<see cref="Generator.Cost"/>, MATPOWER <c>gencost</c> model 2; any order) subject to:</para>
/// <list type="bullet">
/// <item>the AC power balance at every bus (active and reactive), with constant-power loads and the bus shunts and line
/// charging inside the admittance matrix (<see cref="YBus"/>);</item>
/// <item>each rated branch's apparent power at both ends within <see cref="Branch.RateAMva"/> (MATPOWER's default flow
/// limit, on the square of |S|; a rating of 0 means unrated);</item>
/// <item>each bus voltage within its own limits (<see cref="Bus.VminPu"/> / <see cref="Bus.VmaxPu"/>, else the
/// operational band of <see cref="VoltageLimits"/>);</item>
/// <item>each generator within its P and Q limits (an infinite limit is no constraint);</item>
/// <item>every slack bus angle fixed at its case value (the reference).</item>
/// </list>
/// <para>Transformer taps and phase shifts are fixed at their case values; OLTCs are not moved.</para>
///
/// <para><b>The method.</b> The Newton step of the perturbed KKT conditions, with the exact analytic Jacobian and Hessian
/// (derived below), solved by the sparse LU (<see cref="SparseLu"/>) on the reduced KKT system, then a fraction-to-boundary
/// step and the barrier update gamma = sigma z'mu / niq. The starting point, the treatment of variable bounds, the
/// convergence conditions and the failure tests are MIPS's. Every loop runs in a fixed order and every trigonometric
/// value comes from <see cref="DetMath"/>, so the result is bit-identical from run to run and from CPU to CPU; there is
/// one linear-algebra path (the sparse LU), so there is no dense/sparse seam to differ across.</para>
///
/// <para><b>Refused, naming the cause (never approximated):</b> an in-service generator with no cost curve or a
/// piecewise-linear one, a dispatchable load (MATPOWER's Pmin below 0 with Pmax 0, which it holds at a constant power
/// factor) (none of the repository's MATPOWER cases uses one; MATPOWER handles them with extra variables,
/// not built here), a voltage-dependent (ZIP) load, limits that cross (Pmin above Pmax and so on), a limit that is not a
/// number, a branch with zero impedance, a network with no slack bus, and a network in more than one island. Branch angle
/// difference limits are not part of <see cref="GridModel"/> (the MATPOWER parser notes them when a file has them), so
/// they are not enforced.</para>
/// </summary>
public static class AcOpfInteriorPoint
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

    /// <summary>Solve the AC optimal power flow of <paramref name="model"/>. Throws <see cref="InvalidDataException"/>
    /// for a model it refuses (see the class note); otherwise returns a result whose <see cref="AcOpfIpmResult.Status"/>
    /// says whether it converged.</summary>
    public static AcOpfIpmResult Solve(GridModel model, AcOpfIpmOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        AcOpfIpmOptions opt = options ?? new AcOpfIpmOptions();
        ValidateOptions(opt);
        var p = new Problem(model);
        return Run(p, opt);
    }

    private static void ValidateOptions(AcOpfIpmOptions o)
    {
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

    /// <summary>A rated branch's flow limit: the end buses (indices), the end's own admittance (Yaa) and the transfer
    /// admittance (Yab) for each end, and the square of the limit in pu.</summary>
    private readonly record struct FlowLimit(
        int Branch, int F, int T, double Gff, double Bff, double Gft, double Bft, double Gtt, double Btt, double Gtf, double Btf,
        double SmaxPu, double Smax2);

    /// <summary>A bound row of h: h = Sign * (x[Var] - Limit) &lt;= 0 (Sign +1 an upper bound, -1 a lower bound).</summary>
    private readonly record struct BoundRow(int Var, int Sign, double Limit);

    private sealed class Problem
    {
        public readonly GridModel Model;
        public readonly int Nb, Ng, Nx;
        public readonly double Base;
        public readonly double[] Gd, Bd;        // diagonal G, B of each bus row
        public readonly Cell[][] Row;           // off-diagonal cells of each bus row, column order
        public readonly double[] PdPu, QdPu;
        public readonly int[] GenBus;           // bus index of each generator
        public readonly double[][] Poly;        // each generator's polynomial, highest order first (MW, cost per hour)
        public readonly FlowLimit[] Flows;
        public readonly double[] XMin, XMax;
        public readonly BoundRow[] Bounds;      // MIPS order: upper-only, lower-only, then both (all uppers, then all lowers)
        public readonly (int Var, double Value)[] Fixed;   // variables with equal bounds: linear equality rows
        public readonly int RefBus;             // the first slack bus (its angle starts every angle)

        public readonly int VaOffset;           // the angle block starts the variable vector (offset 0)
        public int Va(int i) => VaOffset + i;
        public int Vm(int i) => Nb + i;
        public int Pg(int g) => 2 * Nb + g;
        public int Qg(int g) => 2 * Nb + Ng + g;

        public Problem(GridModel m)
        {
            Model = m;
            Nb = m.BusCount;
            Ng = m.Generators.Count;
            Nx = 2 * Nb + 2 * Ng;
            VaOffset = 0;
            Base = m.BaseMva;
            if (Nb == 0) throw new InvalidDataException("AC-OPF: the case has no buses.");
            if (Ng == 0) throw new InvalidDataException("AC-OPF: the case has no in-service generators to dispatch.");
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

            // Admittance matrix (the same stamps and accumulation as every other study), split into rows.
            SparseYBus y = YBus.BuildSparse(m);
            Gd = new double[Nb];
            Bd = new double[Nb];
            var rows = new List<Cell>[Nb];
            for (int i = 0; i < Nb; i++) rows[i] = new List<Cell>();
            foreach (KeyValuePair<(int Row, int Col), Complex> c in y.Cells)
            {
                if (c.Key.Row == c.Key.Col) { Gd[c.Key.Row] = c.Value.Real; Bd[c.Key.Row] = c.Value.Imaginary; }
                else rows[c.Key.Row].Add(new Cell(c.Key.Col, c.Value.Real, c.Value.Imaginary));
            }
            Row = new Cell[Nb][];
            for (int i = 0; i < Nb; i++)
            {
                Cell[] r = rows[i].ToArray();
                Array.Sort(r, (a, b) => a.K.CompareTo(b.K));   // column order: no dependence on dictionary order
                Row[i] = r;
            }

            CheckOneIsland(m, index);

            // Slack buses: at least one; every slack angle is fixed (MATPOWER fixes every reference bus).
            RefBus = -1;
            for (int i = 0; i < Nb; i++)
                if (m.Buses[i].Type == BusType.Slack) { RefBus = i; break; }
            if (RefBus < 0) throw new InvalidDataException("AC-OPF: the case has no slack bus to fix the reference angle.");

            // Generators: limits and cost curves.
            GenBus = new int[Ng];
            Poly = new double[Ng][];
            for (int g = 0; g < Ng; g++)
            {
                Generator gen = m.Generators[g];
                string who = $"generator {g} at bus {gen.Bus}{(gen.Name is null ? "" : $" ({gen.Name})")}";
                if (!index.TryGetValue(gen.Bus, out int gb))
                    throw new InvalidDataException($"AC-OPF: {who} sits on a bus the case does not have.");
                GenBus[g] = gb;
                if (double.IsNaN(gen.Pmin) || double.IsNaN(gen.Pmax) || double.IsNaN(gen.Qmin) || double.IsNaN(gen.Qmax))
                    throw new InvalidDataException($"AC-OPF: {who} has a limit that is not a number.");
                if (gen.Pmin > gen.Pmax)
                    throw new InvalidDataException($"AC-OPF: {who} has Pmin {Fmt(gen.Pmin)} MW above Pmax {Fmt(gen.Pmax)} MW.");
                if (gen.Qmin > gen.Qmax)
                    throw new InvalidDataException($"AC-OPF: {who} has Qmin {Fmt(gen.Qmin)} MVAr above Qmax {Fmt(gen.Qmax)} MVAr.");
                // MATPOWER treats a unit with Pmin below 0 and Pmax 0 as a dispatchable load and adds a constant power
                // factor constraint for it; that constraint is not built here, so such a unit is refused, not solved
                // as an ordinary generator (which would give a different optimum from MATPOWER's).
                if (gen.Pmin < 0 && gen.Pmax == 0)
                    throw new InvalidDataException($"AC-OPF: {who} is a dispatchable load (Pmin {Fmt(gen.Pmin)} MW, Pmax 0); its constant power factor constraint is not modelled.");
                Poly[g] = CostPolynomial(gen.Cost, who);
            }

            // Variable bounds (pu, radians).
            XMin = new double[Nx];
            XMax = new double[Nx];
            for (int i = 0; i < Nb; i++)
            {
                Bus b = m.Buses[i];
                bool slack = b.Type == BusType.Slack;
                double va = b.VaDeg * Math.PI / 180.0;
                XMin[Va(i)] = slack ? va : double.NegativeInfinity;
                XMax[Va(i)] = slack ? va : double.PositiveInfinity;
                double lo = b.VminPu ?? VoltageLimits.OperationalLowPu;
                double hi = b.VmaxPu ?? VoltageLimits.OperationalHighPu;
                if (!double.IsFinite(lo) || !double.IsFinite(hi) || !(lo > 0) || lo > hi)
                    throw new InvalidDataException($"AC-OPF: bus {b.Id} has voltage limits {Fmt(lo)} .. {Fmt(hi)} pu; they must be finite, above 0 and in order.");
                XMin[Vm(i)] = lo;
                XMax[Vm(i)] = hi;
            }
            for (int g = 0; g < Ng; g++)
            {
                Generator gen = m.Generators[g];
                XMin[Pg(g)] = gen.Pmin / Base; XMax[Pg(g)] = gen.Pmax / Base;
                XMin[Qg(g)] = gen.Qmin / Base; XMax[Qg(g)] = gen.Qmax / Base;
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

            // Rated branches (MATPOWER: 0 or 1e10 and above is unrated).
            var flows = new List<FlowLimit>();
            for (int k = 0; k < m.Branches.Count; k++)
            {
                Branch br = m.Branches[k];
                if (!br.InService) continue;
                if (double.IsNaN(br.RateAMva) || br.RateAMva < 0)
                    throw new InvalidDataException($"AC-OPF: branch {br.From}-{br.To} has rating {Fmt(br.RateAMva)} MVA; a rating must be 0 (unrated) or above.");
                if (br.RateAMva == 0 || br.RateAMva >= NoBound) continue;
                BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
                double smax = br.RateAMva / Base;
                flows.Add(new FlowLimit(k, index[br.From], index[br.To],
                    s.Yff.Real, s.Yff.Imaginary, s.Yft.Real, s.Yft.Imaginary,
                    s.Ytt.Real, s.Ytt.Imaginary, s.Ytf.Real, s.Ytf.Imaginary, smax, smax * smax));
            }
            Flows = flows.ToArray();
        }

        private void CheckOneIsland(GridModel m, Dictionary<int, int> index)
        {
            var seen = new bool[Nb];
            var stack = new Stack<int>();
            seen[0] = true; stack.Push(0);
            int count = 1;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                foreach (Cell c in Row[i])
                    if (!seen[c.K]) { seen[c.K] = true; count++; stack.Push(c.K); }
            }
            if (count != Nb)
            {
                int first = Array.IndexOf(seen, false);
                throw new InvalidDataException(
                    $"AC-OPF: the network is in more than one island (bus {m.Buses[first].Id} is not connected to bus {m.Buses[0].Id}); "
                    + "solve each island as its own case.");
            }
        }
    }

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

    /// <summary>The objective (scaled), its gradient and (on request) its Hessian diagonal, all in pu variables.</summary>
    private static double Cost(Problem p, double[] x, double scale, double[]? grad, double[]? hessDiag)
    {
        double f = 0;
        for (int g = 0; g < p.Ng; g++)
        {
            double[] k = p.Poly[g];
            double mw = x[p.Pg(g)] * p.Base;
            // Horner for the value, the first and the second derivative (in MW).
            double v = 0, d1 = 0, d2 = 0;
            for (int i = 0; i < k.Length; i++)
            {
                d2 = d2 * mw + 2 * d1;
                d1 = d1 * mw + v;
                v = v * mw + k[i];
            }
            f += v;
            if (grad is not null) grad[p.Pg(g)] += scale * d1 * p.Base;
            if (hessDiag is not null) hessDiag[p.Pg(g)] += scale * d2 * p.Base * p.Base;
        }
        return scale * f;
    }

    /// <summary>
    /// The pair term T = Va Vb (wP p0 + wQ q0) with p0 = g cos(t) + b sin(t), q0 = g sin(t) - b cos(t), t = theta_a - theta_b,
    /// which is the part of an injection or a branch-end power that couples two buses (the active part with wP = 1, wQ = 0;
    /// the reactive part with wP = 0, wQ = 1; a multiplier-weighted sum for the Hessian). Returns phi = wP p0 + wQ q0 and
    /// phi' = d phi / d t = -wP q0 + wQ p0 (and phi'' = -phi).
    /// </summary>
    private static (double Phi, double DPhi) Pair(double ta, double tb, double g, double b, double wP, double wQ)
    {
        (double s, double c) = DetMath.SinCos(ta - tb);
        double p0 = g * c + b * s;
        double q0 = g * s - b * c;
        return (wP * p0 + wQ * q0, -wP * q0 + wQ * p0);
    }

    /// <summary>
    /// Add the Hessian of T = Va Vb phi(theta_a - theta_b) to <paramref name="h"/> over the variables
    /// (theta_a, theta_b, Va, Vb) = (ia, ib, ja, jb):
    /// d2/dta2 = d2/dtb2 = -Va Vb phi, d2/dta dtb = Va Vb phi, d2/dta dVa = Vb phi', d2/dta dVb = Va phi',
    /// d2/dtb dVa = -Vb phi', d2/dtb dVb = -Va phi', d2/dVa dVb = phi, d2/dVa2 = d2/dVb2 = 0.
    /// </summary>
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

    /// <summary>The power balance mismatches g (2 nb rows: active then reactive, pu) and, on request, their Jacobian
    /// rows as sparse lists (row = constraint, entries (variable, value)).</summary>
    private static void Balance(Problem p, double[] x, double[] g, List<(int Var, double Val)>[]? jac)
    {
        int nb = p.Nb;
        for (int i = 0; i < nb; i++)
        {
            double ti = x[p.Va(i)], vi = x[p.Vm(i)];
            double pi = vi * vi * p.Gd[i];
            double qi = -vi * vi * p.Bd[i];
            double dPdti = 0, dPdvi = 2 * vi * p.Gd[i], dQdti = 0, dQdvi = -2 * vi * p.Bd[i];
            List<(int, double)>? jp = jac?[i], jq = jac?[nb + i];
            foreach (Cell c in p.Row[i])
            {
                int k = c.K;
                double tk = x[p.Va(k)], vk = x[p.Vm(k)];
                (double s, double co) = DetMath.SinCos(ti - tk);
                double p0 = c.G * co + c.B * s;
                double q0 = c.G * s - c.B * co;
                pi += vi * vk * p0;
                qi += vi * vk * q0;
                if (jac is null) continue;
                // dP/dti += -Vi Vk q0, dP/dtk = Vi Vk q0, dP/dVi += Vk p0, dP/dVk = Vi p0
                // dQ/dti +=  Vi Vk p0, dQ/dtk = -Vi Vk p0, dQ/dVi += Vk q0, dQ/dVk = Vi q0
                dPdti += -vi * vk * q0;
                dPdvi += vk * p0;
                dQdti += vi * vk * p0;
                dQdvi += vk * q0;
                jp!.Add((p.Va(k), vi * vk * q0));
                jp.Add((p.Vm(k), vi * p0));
                jq!.Add((p.Va(k), -vi * vk * p0));
                jq.Add((p.Vm(k), vi * q0));
            }
            if (jac is not null)
            {
                jp!.Add((p.Va(i), dPdti)); jp.Add((p.Vm(i), dPdvi));
                jq!.Add((p.Va(i), dQdti)); jq.Add((p.Vm(i), dQdvi));
            }
            g[i] = pi + p.PdPu[i];
            g[nb + i] = qi + p.QdPu[i];
        }
        for (int gg = 0; gg < p.Ng; gg++)
        {
            int b = p.GenBus[gg];
            g[b] -= x[p.Pg(gg)];
            g[nb + b] -= x[p.Qg(gg)];
            if (jac is not null) { jac[b].Add((p.Pg(gg), -1.0)); jac[nb + b].Add((p.Qg(gg), -1.0)); }
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
    /// The functions of the problem at a point: the objective f (scaled), the equality constraints g (power balance, then
    /// the fixed variables), the inequality constraints h (flow limits from, flow limits to, then the bound rows), their
    /// first derivatives, the gradient of the Lagrangian Lx = df + dg' lam + dh' mu, and its Hessian Lxx. Shared by the
    /// iteration and by the internal derivative check the tests run against finite differences.
    /// </summary>
    private sealed class Evaluator
    {
        public readonly Problem P;
        public readonly int Nx, Neq, Niq, NFlow;
        public readonly double Scale;
        public readonly double[] G, H, Df, Lx;
        public readonly List<(int Var, double Val)>[] JacG;
        public readonly double[,] FlowGrad;          // gradient of each flow row over its four variables
        private readonly double[] _dP = new double[4], _dQ = new double[4];

        public Evaluator(Problem p, double scale)
        {
            P = p;
            Scale = scale;
            Nx = p.Nx;
            NFlow = p.Flows.Length;
            Niq = 2 * NFlow + p.Bounds.Length;
            Neq = 2 * p.Nb + p.Fixed.Length;
            G = new double[Neq];
            H = new double[Niq];
            Df = new double[Nx];
            Lx = new double[Nx];
            JacG = new List<(int, double)>[Neq];
            for (int r = 0; r < Neq; r++) JacG[r] = new List<(int, double)>();
            FlowGrad = new double[Math.Max(2 * NFlow, 1), 4];
        }

        /// <summary>The branch end of flow row <paramref name="row"/>: (a, b) bus indices and the admittances.</summary>
        private (int A, int B, double G0, double B0, double G, double Bb) End(int row)
        {
            FlowLimit fl = P.Flows[row % NFlow];
            return row < NFlow ? (fl.F, fl.T, fl.Gff, fl.Bff, fl.Gft, fl.Bft) : (fl.T, fl.F, fl.Gtt, fl.Btt, fl.Gtf, fl.Btf);
        }

        /// <summary>The variables of flow row <paramref name="row"/>: (theta_a, theta_b, Va, Vb).</summary>
        public (int, int, int, int) FlowVars(int row)
        {
            (int a, int b, _, _, _, _) = End(row);
            return (P.Va(a), P.Va(b), P.Vm(a), P.Vm(b));
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
                G[2 * p.Nb + r] = x[v] - val;
                JacG[2 * p.Nb + r].Add((v, 1.0));
            }
            for (int row = 0; row < 2 * NFlow; row++)
            {
                (int a, int b, double g0, double b0, double g, double bb) = End(row);
                (double pp, double qq) = EndPower(x[p.Va(a)], x[p.Va(b)], x[p.Vm(a)], x[p.Vm(b)], g0, b0, g, bb, _dP, _dQ);
                H[row] = pp * pp + qq * qq - p.Flows[row % NFlow].Smax2;
                for (int q = 0; q < 4; q++) FlowGrad[row, q] = 2 * pp * _dP[q] + 2 * qq * _dQ[q];
            }
            for (int r = 0; r < p.Bounds.Length; r++)
            {
                BoundRow br = p.Bounds[r];
                H[2 * NFlow + r] = br.Sign * (x[br.Var] - br.Limit);
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
                (int i0, int i1, int i2, int i3) = FlowVars(row);
                double m = mu[row];
                Lx[i0] += FlowGrad[row, 0] * m; Lx[i1] += FlowGrad[row, 1] * m;
                Lx[i2] += FlowGrad[row, 2] * m; Lx[i3] += FlowGrad[row, 3] * m;
            }
            for (int r = 0; r < P.Bounds.Length; r++)
                Lx[P.Bounds[r].Var] += P.Bounds[r].Sign * mu[2 * NFlow + r];
        }

        /// <summary>The Hessian of the Lagrangian at <paramref name="x"/>: the cost, the power balance weighted by
        /// <paramref name="lam"/> and the flow limits weighted by <paramref name="mu"/> (the bound rows are linear).</summary>
        public HessianAccumulator Hessian(double[] x, double[] lam, double[] mu)
        {
            Problem p = P;
            int nb = p.Nb;
            var h = new HessianAccumulator();
            var costHess = new double[Nx];
            Cost(p, x, Scale, null, costHess);
            for (int j = 0; j < Nx; j++) h.Add(j, j, costHess[j]);
            for (int i = 0; i < nb; i++)
            {
                double wP = lam[i], wQ = lam[nb + i];
                if (wP == 0 && wQ == 0) continue;
                double vi = x[p.Vm(i)];
                // The diagonal term Vi^2 (wP Gii - wQ Bii): d2/dVi2 = 2 (wP Gii - wQ Bii).
                h.Add(p.Vm(i), p.Vm(i), 2 * (wP * p.Gd[i] - wQ * p.Bd[i]));
                foreach (Cell c in p.Row[i])
                {
                    (double phi, double dphi) = Pair(x[p.Va(i)], x[p.Va(c.K)], c.G, c.B, wP, wQ);
                    AddPairHessian(h, p.Va(i), p.Va(c.K), p.Vm(i), p.Vm(c.K), vi, x[p.Vm(c.K)], phi, dphi);
                }
            }
            var vars = new int[4];
            for (int row = 0; row < 2 * NFlow; row++)
            {
                double m = mu[row];
                if (m == 0) continue;
                (int a, int b, double g0, double b0, double g, double bb) = End(row);
                (double pp, double qq) = EndPower(x[p.Va(a)], x[p.Va(b)], x[p.Vm(a)], x[p.Vm(b)], g0, b0, g, bb, _dP, _dQ);
                vars[0] = p.Va(a); vars[1] = p.Va(b); vars[2] = p.Vm(a); vars[3] = p.Vm(b);
                // d2h = 2 (dP dP' + dQ dQ') + 2 P d2P + 2 Q d2Q, times mu; the pair parts of d2P and d2Q are one pair
                // Hessian with weights (2 P mu, 2 Q mu), and their own-end parts (2 g0 and -2 b0 on d2/dVa2) follow it.
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        h.Add(vars[r], vars[c], m * 2 * (_dP[r] * _dP[c] + _dQ[r] * _dQ[c]));
                (double phi, double dphi) = Pair(x[p.Va(a)], x[p.Va(b)], g, bb, m * 2 * pp, m * 2 * qq);
                AddPairHessian(h, vars[0], vars[1], vars[2], vars[3], x[p.Vm(a)], x[p.Vm(b)], phi, dphi);
                h.Add(vars[2], vars[2], m * (2 * pp * 2 * g0 - 2 * qq * 2 * b0));
            }
            return h;
        }
    }

    // ---- Internal hooks for the derivative check (tests compare these with central finite differences) -------------

    /// <summary>The number of variables, equality rows and inequality rows of <paramref name="model"/>'s problem.</summary>
    internal static (int Nx, int Neq, int Niq) Dimensions(GridModel model)
    {
        var e = new Evaluator(new Problem(model), 1.0);
        return (e.Nx, e.Neq, e.Niq);
    }

    /// <summary>MIPS's starting point for <paramref name="model"/>.</summary>
    internal static double[] StartingPoint(GridModel model) => Start(new Problem(model));

    /// <summary>The Lagrangian L = f + lam' g + mu' h at <paramref name="x"/> (unscaled cost).</summary>
    internal static double Lagrangian(GridModel model, double[] x, double[] lam, double[] mu)
    {
        var e = new Evaluator(new Problem(model), 1.0);
        double l = e.Evaluate(x);
        for (int r = 0; r < e.Neq; r++) l += lam[r] * e.G[r];
        for (int r = 0; r < e.Niq; r++) l += mu[r] * e.H[r];
        return l;
    }

    /// <summary>The analytic gradient of <see cref="Lagrangian"/> at <paramref name="x"/>.</summary>
    internal static double[] LagrangianGradient(GridModel model, double[] x, double[] lam, double[] mu)
    {
        var e = new Evaluator(new Problem(model), 1.0);
        e.Evaluate(x);
        e.Gradient(lam, mu);
        return (double[])e.Lx.Clone();
    }

    /// <summary>The analytic Hessian of <see cref="Lagrangian"/> at <paramref name="x"/>, dense.</summary>
    internal static double[,] LagrangianHessian(GridModel model, double[] x, double[] lam, double[] mu)
    {
        var e = new Evaluator(new Problem(model), 1.0);
        var d = new double[e.Nx, e.Nx];
        foreach ((int r, int c, double v) in e.Hessian(x, lam, mu).Entries) d[r, c] += v;
        return d;
    }

    // ---- The interior point iteration ---------------------------------------------------------------------------------

    /// <summary>Starting point (MATPOWER mipsopf_solver): the middle of each variable's bounds (an infinite bound counts
    /// as +/-1e10, so a variable with neither bound starts at 0); every angle at the first slack bus's angle. A variable
    /// with one finite bound starts at that bound (the middle would be 5e9).</summary>
    private static double[] Start(Problem p)
    {
        var x = new double[p.Nx];
        for (int j = 0; j < p.Nx; j++)
        {
            double lo = p.XMin[j], hi = p.XMax[j];
            bool hasLo = lo > -NoBound, hasHi = hi < NoBound;
            x[j] = hasLo && hasHi ? (lo + hi) / 2 : hasLo ? lo : hasHi ? hi : 0.0;
        }
        double refVa = p.XMin[p.Va(p.RefBus)];
        for (int i = 0; i < p.Nb; i++) x[p.Va(i)] = refVa;
        return x;
    }

    private static AcOpfIpmResult Run(Problem p, AcOpfIpmOptions opt)
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

        int n = nx + neq;
        var rhs = new double[n];
        var dz = new double[niq];
        var dmu = new double[niq];
        while (!converged && it < opt.MaxIterations)
        {
            it++;

            // Hessian of the Lagrangian: cost, power balance (weighted by lam), flow limits (weighted by mu).
            HessianAccumulator h = ev.Hessian(x, lam, mu);

            // M = Lxx + dh diag(mu / z) dh';  N = Lx + dh ((mu .* h + gamma) ./ z).
            var nVec = (double[])lx.Clone();
            for (int row = 0; row < 2 * nFlow; row++)
            {
                (int i0, int i1, int i2, int i3) = ev.FlowVars(row);
                int[] vars = { i0, i1, i2, i3 };
                double w = mu[row] / z[row];
                double t = (mu[row] * hVec[row] + gamma) / z[row];
                for (int r = 0; r < 4; r++)
                {
                    nVec[vars[r]] += flowGrad[row, r] * t;
                    for (int c = 0; c < 4; c++) h.Add(vars[r], vars[c], w * flowGrad[row, r] * flowGrad[row, c]);
                }
            }
            for (int r = 0; r < p.Bounds.Length; r++)
            {
                int row = 2 * nFlow + r;
                BoundRow br = p.Bounds[r];
                h.Add(br.Var, br.Var, mu[row] / z[row]);   // Sign^2 = 1
                nVec[br.Var] += br.Sign * (mu[row] * hVec[row] + gamma) / z[row];
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
                (int i0, int i1, int i2, int i3) = ev.FlowVars(row);
                double dhdx = flowGrad[row, 0] * sol[i0] + flowGrad[row, 1] * sol[i1] + flowGrad[row, 2] * sol[i2] + flowGrad[row, 3] * sol[i3];
                dz[row] = -hVec[row] - z[row] - dhdx;
            }
            for (int r = 0; r < p.Bounds.Length; r++)
            {
                int row = 2 * nFlow + r;
                dz[row] = -hVec[row] - z[row] - p.Bounds[r].Sign * sol[p.Bounds[r].Var];
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
            message = $"converged in {it} iteration(s)";

        // MIPS: zero the multiplier of an inequality that is clearly slack and has a negligible multiplier.
        for (int r = 0; r < niq; r++)
            if (hVec[r] < -opt.FeasibilityTolerance && mu[r] < MuThreshold) mu[r] = 0;

        return BuildResult(p, x, lam, mu, f / scale, scale, status, it, log, message);
    }

    /// <summary>
    /// Solve the KKT system K d = r through a symmetric power-of-two equilibration (Ruiz): D K D y = D r, d = D y, with
    /// each D_i a power of two chosen so every row's largest entry of D K D lies near 1. Near the optimum the barrier
    /// terms mu / z of the binding limits grow without bound (1e8 and more) while the network terms stay near 1, and the
    /// sparse LU's singularity test is relative to the largest entry of the matrix (<see cref="NumericTolerances.RelativePivotFloor"/>),
    /// so the raw system was refused as singular on the PEGASE cases. Scaling by powers of two is exact (no rounding), so
    /// it changes only the pivoting the LU sees, never the system solved. Returns null when the LU finds it singular.
    /// </summary>
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
                // The power of two nearest 1 / sqrt(m) (integer arithmetic on the exponent: exact and deterministic).
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

    private static AcOpfIpmResult BuildResult(Problem p, double[] x, double[] lam, double[] mu,
        double objective, double scale, AcOpfIpmStatus status, int iterations, List<AcOpfIpmIteration> log, string message)
    {
        GridModel m = p.Model;
        int nb = p.Nb, nFlow = p.Flows.Length;
        double bse = p.Base;

        // Unscaled multipliers per bound: lower and upper of every variable.
        var muLo = new double[p.Nx];
        var muHi = new double[p.Nx];
        for (int r = 0; r < p.Bounds.Length; r++)
        {
            BoundRow br = p.Bounds[r];
            double v = mu[2 * nFlow + r] / scale;
            if (br.Sign > 0) muHi[br.Var] = v; else muLo[br.Var] = v;
        }

        var buses = new AcOpfBusResult[nb];
        for (int i = 0; i < nb; i++)
            buses[i] = new AcOpfBusResult(m.Buses[i].Id, x[p.Vm(i)], x[p.Va(i)] * 180.0 / Math.PI,
                lam[i] / scale / bse, lam[nb + i] / scale / bse, muLo[p.Vm(i)], muHi[p.Vm(i)]);

        var gens = new AcOpfGenResult[p.Ng];
        double totalGen = 0;
        for (int g = 0; g < p.Ng; g++)
        {
            Generator gen = m.Generators[g];
            double pg = x[p.Pg(g)] * bse;
            totalGen += pg;
            gens[g] = new AcOpfGenResult(g, gen.Bus, gen.Name, pg, x[p.Qg(g)] * bse,
                muLo[p.Pg(g)] / bse, muHi[p.Pg(g)] / bse, muLo[p.Qg(g)] / bse, muHi[p.Qg(g)] / bse);
        }
        double totalLoad = 0;
        foreach (Bus b in m.Buses) totalLoad += b.Pd;

        // Branch flows at the optimum for every in-service branch; the flow-limit multipliers of the rated ones
        // (MATPOWER: the limit is on |S|^2, so the price on |S| is 2 mu Smax, per MVA: / base).
        var muSf = new double[m.Branches.Count];
        var muSt = new double[m.Branches.Count];
        for (int l = 0; l < nFlow; l++)
        {
            FlowLimit fl = p.Flows[l];
            muSf[fl.Branch] = 2 * (mu[l] / scale) * fl.SmaxPu / bse;
            muSt[fl.Branch] = 2 * (mu[nFlow + l] / scale) * fl.SmaxPu / bse;
        }
        var index = new Dictionary<int, int>(nb);
        for (int i = 0; i < nb; i++) index[m.Buses[i].Id] = i;
        var branches = new List<AcOpfBranchResult>();
        var dP = new double[4];
        var dQ = new double[4];
        for (int k = 0; k < m.Branches.Count; k++)
        {
            Branch br = m.Branches[k];
            if (!br.InService) continue;
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            int f = index[br.From], t = index[br.To];
            (double pf, double qf) = EndPower(x[p.Va(f)], x[p.Va(t)], x[p.Vm(f)], x[p.Vm(t)], s.Yff.Real, s.Yff.Imaginary, s.Yft.Real, s.Yft.Imaginary, dP, dQ);
            (double pt, double qt) = EndPower(x[p.Va(t)], x[p.Va(f)], x[p.Vm(t)], x[p.Vm(f)], s.Ytt.Real, s.Ytt.Imaginary, s.Ytf.Real, s.Ytf.Imaginary, dP, dQ);
            branches.Add(new AcOpfBranchResult(k, br.From, br.To, br.Name, pf * bse, qf * bse, pt * bse, qt * bse, br.RateAMva, muSf[k], muSt[k]));
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

        return new AcOpfIpmResult(status, iterations, objective, totalGen, totalLoad, buses, gens, branches, binding, log, message);
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
