using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.SmallSignal;

/// <summary>
/// The assembled small-signal (linearised) state model of a solved case: the real state matrix A whose
/// eigenvalues are the electromechanical modes, the kind-tagged state labels (real vs synthetic), and the
/// synchronising matrix among the angle nodes.
/// </summary>
/// <param name="A">N x N real state matrix - the eigen input.</param>
/// <param name="States">Length-N kind-tagged labels, aligned to A's rows/columns.</param>
/// <param name="F0">Nominal frequency (Hz).</param>
/// <param name="AngleNodeBuses">The angle-node bus ids (machine internal nodes; inverters couple to the
/// centre of inertia in the archetype path, so they are not separate angle nodes).</param>
/// <param name="Kext">The synchronising Laplacian among the angle nodes, in per-unit power per radian on the system
/// base (R085: documented as MW/rad, which is BaseMva times larger), row sums ~0 - the TRUE
/// asymmetric K (transfer conductances retained, no negative clamp).</param>
/// <param name="InverterStatesIncluded">Whether the opt-in inverter control states are present.</param>
public sealed record SmallSignalModel(
    double[,] A,
    IReadOnlyList<StateLabel> States,
    double F0,
    IReadOnlyList<int> AngleNodeBuses,
    double[,] Kext,
    bool InverterStatesIncluded)
{
    /// <summary>State count N.</summary>
    public int Order => States.Count;

    /// <summary>The number of synchronous machines (real-inertia swing pairs).</summary>
    public int MachineCount => AngleNodeBuses.Count;
}

/// <summary>
/// Assembles a <see cref="SmallSignalModel"/> from a solved power flow, reusing the validated classical
/// reduction (<see cref="ClassicalInit.FromSolvedCase"/>) for the electrical part. <see cref="ClassicalInit"/>
/// is consumed only through its PUBLIC surface and is not modified, so every existing path stays
/// byte-identical.
/// </summary>
public static class SmallSignalAssembler
{
    /// <summary>
    /// Swing-only (default) small-signal model. Machine core, 2g x 2g, ordered [d(delta) 1..g | d(omega) 1..g]:
    /// <code>
    /// A = [   0            ws*I_g ]
    ///     [ -M^-1 K_L    -M^-1 D  ]
    /// </code>
    /// M = diag(2H_i), D = diag(D_i) (default 0), and K_L the TRUE synchronising Laplacian
    /// (<c>K[i,j] = |E_i||E_j|(B_ij cos d_ij0 - G_ij sin d_ij0)</c>, row sums zero, NO negative clamp - the
    /// transfer-conductance asymmetry is retained, which is precisely why a non-symmetric eigensolver is
    /// needed). The undamped case has, by construction, a DEFECTIVE double-zero eigenvalue (the rigid-body
    /// COI mode); <see cref="ModalAnalysis"/> deflates it before computing participation.
    /// </summary>
    public static SmallSignalModel FromSolvedCase(GridModel model, PowerFlowResult result,
        double[]? xdpMachineBase = null, double[]? machineDamping = null)
    {
        result.RequireConverged("Small-signal assembly");   // the convergence contract (October 2026, WS4)
        ClassicalInit.Prepared p = PrepareMachines(model, result, xdpMachineBase);
        double f0 = model.FrequencyHz;
        double ws = 2.0 * Math.PI * f0;
        int g = p.Eq.Length;

        double[,] kl = SynchronisingLaplacian(p);
        var mArr = new double[g];
        for (int i = 0; i < g; i++) mArr[i] = 2.0 * p.H[i];   // 2H on the system base
        double[] dArr = MachineDamping(model, p, machineDamping);

        int n = 2 * g;
        var a = new double[n, n];
        for (int i = 0; i < g; i++)
        {
            a[i, g + i] = ws;                                  // d(delta)_i' = ws * d(omega)_i
            for (int j = 0; j < g; j++)
                a[g + i, j] = -kl[i, j] / mArr[i];             // -M^-1 K_L
            a[g + i, g + i] += -dArr[i] / mArr[i];             // -M^-1 D
        }

        var states = new StateLabel[n];
        for (int i = 0; i < g; i++)
        {
            int bus = p.GeneratorBuses[i];
            states[i] = new StateLabel(i, StateKind.SwingAngle, bus, $"d.delta_gen{bus}");
            states[g + i] = new StateLabel(g + i, StateKind.SwingSpeed, bus, $"d.omega_gen{bus}");
        }

        return new SmallSignalModel(a, states, f0, p.GeneratorBuses.ToArray(), kl, InverterStatesIncluded: false);
    }

    /// <summary>
    /// OPT-IN: the swing-only machine core plus linearised inverter control states from the case's bound
    /// asset classes. Inverters couple to the system CENTRE OF INERTIA (a documented archetype: they sense
    /// the COI frequency/df-dt and inject back into the COI power balance) - so the machine swing sub-block
    /// is IDENTICAL to <see cref="FromSolvedCase"/> and no synthetic-inertia coefficient is ever folded
    /// into a machine H/E. A GFM at <c>synInertia = 0</c> (the code default) reduces to a pure first-order
    /// droop actuator with no singularity. Fails loud if the case binds no grid-forming/following inverter.
    /// </summary>
    public static SmallSignalModel FromSolvedCaseWithInverters(GridModel model, PowerFlowResult result,
        double[]? xdpMachineBase = null, double[]? machineDamping = null, bool includeSoc = false)
    {
        result.RequireConverged("Small-signal assembly");   // the convergence contract (October 2026, WS4)
        // R085: includeSoc used to be accepted and silently discarded. No state-of-charge state is implemented (it
        // would be a decoupled origin pole, excluded from the stability matrix by design), so asking for one fails
        // loud instead of returning a model identical to the one without it.
        if (includeSoc)
            throw new NotSupportedException(
                "Small-signal state of charge is not implemented: an SoC integrator is a decoupled origin pole and is " +
                "excluded from the stability matrix by design. Run without it.");
        AssetDynamicsModel spec = model.AssetDynamics
            ?? throw new InvalidOperationException("--enable-inverters needs bound asset dynamics (AssetDynamics is null).");

        ClassicalInit.Prepared p = PrepareMachines(model, result, xdpMachineBase);
        double f0 = model.FrequencyHz;
        double ws = 2.0 * Math.PI * f0;
        int g = p.Eq.Length;
        double baseMva = model.BaseMva;

        double[,] kl = SynchronisingLaplacian(p);
        var mArr = new double[g];
        double mT = 0.0;
        for (int i = 0; i < g; i++) { mArr[i] = 2.0 * p.H[i]; mT += mArr[i]; }
        double[] dArr = MachineDamping(model, p, machineDamping);

        // Resolve inverter blocks from the bound classes. R009: a generator bound to an inverter class was
        // excluded from the machine set by PrepareMachines (held as a constant admittance in the network
        // reduction), so its control block below is its ONLY dynamic representation - never a second copy
        // of a unit already counted as a swing machine.
        var machineSet = new HashSet<int>(p.MachineIndex);
        var blocks = new List<InverterLinearBlock>();
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator gen = model.Generators[k];
            AssetClass? cls = BoundInverterClass(spec, gen);
            if (cls is null) continue;
            if (machineSet.Contains(k))
                throw new InvalidOperationException(
                    $"Generator {k} at bus {gen.Bus} is bound to inverter class '{cls.Name}' but is still a swing machine; " +
                    "it would be counted twice (internal consistency failure).");
            InverterLinearBlock? block = cls.Kind switch
            {
                AssetKind.GridFormingInverter => LinearInverterModel.ForGfm(cls, gen.Bus, gen.MBase, f0),
                AssetKind.GridFollowingInverter => LinearInverterModel.ForGfl(cls, gen.Bus, gen.MBase, f0),
                _ => null,
            };
            if (block is not null) blocks.Add(block);
        }
        if (blocks.Count == 0)
            throw new InvalidOperationException(
                "--enable-inverters: the case binds no grid-forming/following inverter class; nothing to add.");

        int invCount = blocks.Sum(b => b.Order);
        int n = 2 * g + invCount;
        var a = new double[n, n];

        // Machine swing sub-block (IDENTICAL to swing-only).
        for (int i = 0; i < g; i++)
        {
            a[i, g + i] = ws;
            for (int j = 0; j < g; j++) a[g + i, j] = -kl[i, j] / mArr[i];
            a[g + i, g + i] += -dArr[i] / mArr[i];
        }

        // COI measurement rows over the machine states (length n, only machine entries populated).
        // d(f) [Hz]      = f0 * sum_i M_i d(omega)_i / M_T
        // d(f)/dt [Hz/s] = f0/M_T * sum_i M_i (d(omega)_i/dt), machine-driven part only (breaks the algebraic loop):
        //                = f0/M_T * ( -sum_j (col-sum K_L)_j d(delta)_j  -  sum_i D_i d(omega)_i )
        var dfRow = new double[n];
        var rocofRow = new double[n];
        for (int i = 0; i < g; i++) dfRow[g + i] = f0 * mArr[i] / mT;
        for (int j = 0; j < g; j++)
        {
            double colSum = 0.0;
            for (int i = 0; i < g; i++) colSum += kl[i, j];
            rocofRow[j] = -f0 / mT * colSum;
        }
        for (int i = 0; i < g; i++) rocofRow[g + i] += -f0 / mT * dArr[i];

        // Place inverter blocks and wire the COI coupling.
        var states = new List<StateLabel>(n);
        for (int i = 0; i < g; i++)
        {
            int bus = p.GeneratorBuses[i];
            states.Add(new StateLabel(i, StateKind.SwingAngle, bus, $"d.delta_gen{bus}"));
        }
        for (int i = 0; i < g; i++)
        {
            int bus = p.GeneratorBuses[i];
            states.Add(new StateLabel(g + i, StateKind.SwingSpeed, bus, $"d.omega_gen{bus}"));
        }

        int off = 2 * g;
        foreach (InverterLinearBlock b in blocks)
        {
            for (int r = 0; r < b.Order; r++)
            {
                // Internal dynamics.
                for (int c = 0; c < b.Order; c++) a[off + r, off + c] += b.Aself[r, c];
                // df / df-dt feedthrough of the COI measurement.
                if (b.FreqInputRow[r] != 0.0)
                    for (int j = 0; j < n; j++) a[off + r, j] += b.FreqInputRow[r] * dfRow[j];
                if (b.RocofInputRow[r] != 0.0)
                    for (int j = 0; j < n; j++) a[off + r, j] += b.RocofInputRow[r] * rocofRow[j];
                states.Add(new StateLabel(off + r, b.StateKinds[r], b.Bus, $"{b.StateKinds[r]}_inv{b.Bus}"));
            }
            // Injected power feeds the COI: each machine's d(omega)_i/dt += (1/M_T) * dP_inv_pu.
            for (int r = 0; r < b.Order; r++)
            {
                double coeffPu = b.PowerOutputRow[r] / baseMva;
                if (coeffPu == 0.0) continue;
                for (int i = 0; i < g; i++) a[g + i, off + r] += coeffPu / mT;
            }
            off += b.Order;
        }

        return new SmallSignalModel(a, states, f0, p.GeneratorBuses.ToArray(), kl, InverterStatesIncluded: true);
    }

    /// <summary>
    /// The classical machine preparation shared by both assemblers (R009). Two rules decide which generators
    /// are swing machines:
    /// <list type="number">
    /// <item>Generators that are not integrable machines - converter-interfaced plant, power-flow-only devices
    /// and units with no inertia (H &lt;= 0) - are HELD as constant admittances at their bus (ClassicalInit's
    /// <c>holdNonMachines</c>, the same treatment the CCT/stability verbs and the RMS engine use). Before this,
    /// every generator was a machine and the first H = 0 unit (any wind/solar/storage) threw, so modal analysis
    /// could not run on gb-full at all.</item>
    /// <item>A generator bound to a grid-forming or grid-following inverter class is an inverter by
    /// declaration, not a rotor, so it is held too - whatever its H. In the inverter assembler its control
    /// block is then its only dynamic representation (it used to be a swing machine AND a COI droop block).</item>
    /// </list>
    /// When every generator is an unbound machine (the canonical validation cases) the model passed to
    /// ClassicalInit is the caller's own object and every number is bit-for-bit what it was.
    /// </summary>
    private static ClassicalInit.Prepared PrepareMachines(GridModel model, PowerFlowResult result, double[]? xdpMachineBase)
    {
        GridModel working = model;
        AssetDynamicsModel? spec = model.AssetDynamics;
        if (spec is not null)
        {
            bool anyBound = false;
            var gens = new Generator[model.Generators.Count];
            for (int k = 0; k < gens.Length; k++)
            {
                Generator gen = model.Generators[k];
                if (BoundInverterClass(spec, gen) is not null && !gen.PowerFlowOnly)
                {
                    // Held through ClassicalInit's public surface: a power-flow-only unit is a constant
                    // admittance at its solved injection. Only this private working copy is changed; the
                    // generator order (and so xdpMachineBase alignment) is untouched.
                    gens[k] = gen with { PowerFlowOnly = true };
                    anyBound = true;
                }
                else gens[k] = gen;
            }
            if (anyBound) working = model.CopyWith(generators: gens);
        }
        return ClassicalInit.FromSolvedCase(working, result, xdpMachineBase, holdNonMachines: true);
    }

    /// <summary>The inverter class a generator is bound to, or null when it binds no grid-forming/following
    /// class. A binding that names a class missing from the library fails loud (Library.Get throws).</summary>
    private static AssetClass? BoundInverterClass(AssetDynamicsModel spec, Generator gen)
    {
        string? className = spec.Bindings.Resolve(gen.DynamicsClass, gen.Type);
        if (className is null) return null;
        AssetClass cls = spec.Library.Get(className);
        return cls.Kind is AssetKind.GridFormingInverter or AssetKind.GridFollowingInverter ? cls : null;
    }

    /// <summary>
    /// Per-machine damping D_i, aligned to the swing machines. <paramref name="machineDamping"/> is aligned to
    /// <c>model.Generators</c> (one entry per generator, as the CLI builds it); the entries of held units are
    /// not used, because a held unit has no rotor speed for a damping torque to act on. Every entry must be
    /// finite. Null means zero damping (the classical model carries none intrinsically).
    /// </summary>
    private static double[] MachineDamping(GridModel model, ClassicalInit.Prepared p, double[]? machineDamping)
    {
        IReadOnlyList<int> machines = p.MachineIndex;
        var dArr = new double[machines.Count];
        if (machineDamping is null) return dArr;
        if (machineDamping.Length != model.Generators.Count)
            throw new ArgumentException(
                $"machineDamping has {machineDamping.Length} entries but the model has {model.Generators.Count} generators; " +
                "it must hold one entry per generator (held non-machines are ignored).", nameof(machineDamping));
        for (int k = 0; k < machineDamping.Length; k++)
            if (!double.IsFinite(machineDamping[k]))
                throw new ArgumentException($"machineDamping[{k}] is {machineDamping[k]}; damping must be finite.", nameof(machineDamping));
        for (int i = 0; i < machines.Count; i++) dArr[i] = machineDamping[machines[i]];
        return dArr;
    }

    /// <summary>
    /// Build a swing-only <see cref="SmallSignalModel"/> directly from per-machine inertia and the
    /// OFF-DIAGONAL synchronising coefficients k (the ContinuumWave 'k' convention: <c>k[i,j]</c> couples i
    /// and j, per-unit power per radian on the system base (not MW/rad; R085), diagonal ignored). The Laplacian is <c>K_L[i,i] = sum_{j!=i} k[i,j]</c>,
    /// <c>K_L[i,j] = -k[i,j]</c>. Used for analytic tests (SMIB) and the lossless collapse invariant.
    /// </summary>
    public static SmallSignalModel BuildSwingFromCoupling(double[] h, double[,] kOffDiag, IReadOnlyList<int> buses,
        double f0, double[]? d = null)
    {
        int g = h.Length;
        double ws = 2.0 * Math.PI * f0;
        var kl = new double[g, g];
        for (int i = 0; i < g; i++)
        {
            double diag = 0.0;
            for (int j = 0; j < g; j++)
            {
                if (i == j) continue;
                kl[i, j] = -kOffDiag[i, j];
                diag += kOffDiag[i, j];
            }
            kl[i, i] = diag;
        }

        int n = 2 * g;
        var a = new double[n, n];
        for (int i = 0; i < g; i++)
        {
            double m = 2.0 * h[i];
            a[i, g + i] = ws;
            for (int j = 0; j < g; j++) a[g + i, j] = -kl[i, j] / m;
            if (d is not null) a[g + i, g + i] += -d[i] / m;
        }
        var states = new StateLabel[n];
        for (int i = 0; i < g; i++)
        {
            int bus = buses[i];
            states[i] = new StateLabel(i, StateKind.SwingAngle, bus, $"d.delta_gen{bus}");
            states[g + i] = new StateLabel(g + i, StateKind.SwingSpeed, bus, $"d.omega_gen{bus}");
        }
        return new SmallSignalModel(a, states, f0, buses.ToArray(), kl, InverterStatesIncluded: false);
    }

    /// <summary>The TRUE synchronising Laplacian K_L among the machine internal nodes (per-unit power per radian on the
    /// system base; R085: documented as MW/rad): off-diagonal
    /// <c>-K[i,j]</c>, diagonal the row sum, where <c>K[i,j] = |E_i||E_j|(B_ij cos d_ij0 - G_ij sin d_ij0)</c>.
    /// No negative clamp; the transfer-conductance asymmetry is retained.</summary>
    private static double[,] SynchronisingLaplacian(ClassicalInit.Prepared p)
    {
        int g = p.Eq.Length;
        var k = new double[g, g];
        for (int i = 0; i < g; i++)
        {
            double diag = 0.0;
            for (int j = 0; j < g; j++)
            {
                if (i == j) continue;
                double dij = p.Delta0[i] - p.Delta0[j];
                Complex y = p.YReduced[i, j];
                double kij = p.Eq[i] * p.Eq[j] * (y.Imaginary * DetMath.Cos(dij) - y.Real * DetMath.Sin(dij));
                k[i, j] = -kij;
                diag += kij;
            }
            k[i, i] = diag;
        }
        return k;
    }

    /// <summary>
    /// The LOSSLESS symmetrised synchronising Laplacian: transfer conductances G zeroed and no clamp. This
    /// is the object the collapse invariant compares against - with G = 0 the K become symmetric and the
    /// swing-only modal frequencies coincide with an unclamped symmetric <c>ContinuumWaveModel</c>. It is
    /// NOT used for the stability A (that keeps the true asymmetric K); it exists only to state the collapse
    /// test honestly.
    /// </summary>
    public static double[,] LosslessSynchronisingCoupling(GridModel model, PowerFlowResult result, double[]? xdpMachineBase = null)
    {
        result.RequireConverged("Synchronising coupling");   // the convergence contract (October 2026, WS4)
        ClassicalInit.Prepared p = PrepareMachines(model, result, xdpMachineBase);   // same machine set as the stability A (R009)
        int g = p.Eq.Length;
        var k = new double[g, g];   // off-diagonal synchronising coefficients (ContinuumWave 'k' convention)
        for (int i = 0; i < g; i++)
            for (int j = 0; j < g; j++)
            {
                if (i == j) continue;
                double dij = p.Delta0[i] - p.Delta0[j];
                Complex y = p.YReduced[i, j];
                k[i, j] = p.Eq[i] * p.Eq[j] * (y.Imaginary * DetMath.Cos(dij)); // G zeroed => -G sin term dropped
            }
        return k;
    }
}
