using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL: the grid as a continuum, viewed through the fluid analogy. The linearised swing
/// dynamics on a network are a DAMPED WAVE EQUATION,
///
///     M d2(delta)/dt2  +  D d(delta)/dt  +  K_L delta  =  P_imbalance,
///
/// structurally the skeleton of Navier-Stokes: an inertial term (M, the 1/2 J w^2 spinning-mass
/// "density"), a dissipative term (D, the damping - the analogue of VISCOSITY, not of inertia), and a
/// restoring term (the synchronising graph-Laplacian K_L, the "elastic"/pressure coupling). The rotor
/// angle delta is the field variable; disturbances propagate across the network as electromechanical
/// waves at a finite speed. The rigorous, checkable content is the MODAL structure: the eigenvalues of
/// the generalised problem K_L phi = w^2 M phi are the network's normal modes - the inter-area
/// electromechanical oscillations (typically 0.1-2 Hz) - and the null mode is the centre-of-inertia.
///
/// PROVENANCE: this is a modelling LENS, not a claim that the grid satisfies Navier-Stokes. It is a
/// LINEARISATION (small-signal, about the operating point) of the validated nonlinear swing model, so
/// it captures oscillation frequencies and mode shapes, NOT large-disturbance transients, loss of
/// synchronism, or turbulence (the grid's nonlinearity is the sin(delta_ij) pendulum coupling, a
/// different beast from Navier-Stokes advection). It is opt-in (the <c>experimental wave</c> verb) and
/// COLLAPSES to the established physics in its degenerate limit: a single machine against an infinite
/// bus reproduces the analytic small-signal frequency w_n = sqrt(w_s P_s / 2H) that
/// <c>ClassicalMultiMachine</c> is validated against. The intrinsic-vs-synthetic inertia separation is
/// preserved (M is the real 1/2 J w^2 mass, never a df/dt emulation).
/// </summary>
public static class ContinuumWaveModel
{
    // ---- Named literals (October 2026 review, R185, H hygiene stream). Values unchanged. ----

    /// <summary>Floor (s) on a machine's inertia constant before it becomes the modal mass M = 2H / ws: it keeps the
    /// symmetrised operator finite for a machine with no inertia.</summary>
    public const double MinInertiaConstantS = 1e-9;

    /// <summary>The frequency (Hz) below which a mode USED TO count as zero. N282 (October 2026, stream B1; ruled by main
    /// under Ruling R1): mode selection no longer uses it. A COI eigenvalue that rounded above it was picked as the
    /// "dominant" mode, and a threshold cannot tell an island's rigid-body mode from a slow real one. The number of zero
    /// (rigid-body) modes is now counted exactly as the number of connected components of the synchronising graph
    /// (<see cref="WaveModes.RigidBodyModeCount"/>). Kept as a named value for callers and for the record.</summary>
    public const double NonZeroModeThresholdHz = 1e-6;

    /// <summary>The damping-ratio estimate is computed only for a dominant mode above this frequency (Hz); at or below it
    /// the ratio would divide by (almost) zero and is reported as 0.</summary>
    public const double MinDampedModeHz = 1e-9;

    /// <summary>The network's electromechanical normal modes.</summary>
    /// <param name="ModeFrequenciesHz">All modal frequencies (Hz), ascending; the first is the ~0 COI mode.</param>
    /// <param name="CoiModeHz">The centre-of-inertia (null) mode - a uniform angle shift, ~0 Hz.</param>
    /// <param name="InterAreaModesHz">The non-zero oscillation modes (the inter-area electromechanical modes).</param>
    /// <param name="DominantInterAreaHz">The slowest non-zero mode - the system-splitting inter-area mode.</param>
    /// <param name="DominantDampingRatio">zeta of the dominant mode: &lt; 1 is underdamped/oscillatory
    /// (the "high Reynolds", wave-like regime), &gt;= 1 overdamped (diffusive). 0 when no damping supplied.</param>
    /// <param name="MeanSynchronisingMwPerRad">Mean edge synchronising coefficient K (the medium's "stiffness"), over the
    /// edges of the SYMMETRIC coupling the modes are computed from. From <see cref="FromSolvedCase"/> it is in MW/rad (the
    /// per-unit coefficients times the case's MVA base; R035, October 2026: it used to be the per-unit figure under an
    /// "MW/rad" name, 100 times too small on a 100 MVA base). From <see cref="Analyze"/> it is in the unit of the k it was
    /// given.</param>
    /// <param name="MeanInertiaSeconds">Mean machine inertia constant H (the medium's "density").</param>
    /// <param name="RigidBodyModeCount">N282: how many zero (rigid-body) modes the network has, one per connected component
    /// of the synchronising graph (one per electrical island of machines; 1 for a connected network). Exactly that many
    /// modes are skipped before the dominant one, and <paramref name="InterAreaModesHz"/> holds the modes after them.</param>
    /// <param name="MaxCouplingAsymmetry">R035 (October 2026): the largest |k_ij - k_ji| over every machine pair, in the
    /// same unit as <paramref name="MeanSynchronisingMwPerRad"/>. The model analyses the symmetric part of the coupling
    /// (see <see cref="Analyze"/>); this is the size of the antisymmetric part it set aside (from a solved case: the
    /// transfer-conductance term 2 |E_i E_j G_ij sin(delta_ij)|). 0 for a symmetric (lossless) coupling.</param>
    public sealed record WaveModes(
        IReadOnlyList<double> ModeFrequenciesHz,
        double CoiModeHz,
        IReadOnlyList<double> InterAreaModesHz,
        double DominantInterAreaHz,
        double DominantDampingRatio,
        double MeanSynchronisingMwPerRad,
        double MeanInertiaSeconds,
        IReadOnlyList<MachineAmplitude> DominantModeShape,
        int RigidBodyModeCount = 1,
        double MaxCouplingAsymmetry = 0.0);

    /// <summary>One machine's participation in a mode: its signed amplitude (normalised so the largest
    /// is +/-1). Machines with opposite signs swing AGAINST each other in that mode - the "areas" the
    /// inter-area oscillation splits the system into.</summary>
    public sealed record MachineAmplitude(int Machine, double Amplitude);

    /// <summary>
    /// Modal analysis from the reduced small-signal inputs directly: <paramref name="h"/> the per-machine
    /// inertia constants (s), <paramref name="k"/> the synchronising-coefficient matrix on the same power base as the
    /// inertia constants (<c>k[i,j]</c> is the coupling of machine i to machine j, d(P_e,i)/d(delta_j) sign-flipped;
    /// diagonal ignored), <paramref name="f0"/> the nominal frequency, and optional per-machine damping
    /// <paramref name="d"/> (MW/rad-per-s equivalent). Negative couplings (anti-synchronising artefacts at a marginal
    /// point) are clamped to zero to keep the Laplacian positive semi-definite.
    ///
    /// <para><b>The symmetric part (R035, October 2026 review; stream B5; Ruling R1: Mark delegated EE calls
    /// 2026-10-04).</b> The coupling of a real network is NOT symmetric: with transfer conductances (always present after
    /// the Kron reduction with load admittances) k_ij = E_i E_j (B_ij cos d_ij - G_ij sin d_ij) and the G sin term flips
    /// sign between (i, j) and (j, i). The eigen-solver here is the cyclic Jacobi method for SYMMETRIC matrices
    /// (<see cref="SymmetricEigen"/>), which reads only the upper triangle; before R035 the asymmetric Laplacian was
    /// handed to it as it was, so it silently diagonalised a different matrix whose rows do not sum to zero: the
    /// centre-of-inertia eigenvalue was lifted off zero (in the review's two-machine example to 0.33 Hz) and the true
    /// inter-area mode came out wrong. Now the coupling is symmetrised EXPLICITLY, k_sym = (k_ij + k_ji) / 2, before the
    /// clamp, and that is the documented approximation of this lens: the lossless (conservative) synchronising part of
    /// the coupling, the standard premise of the energy-function and slow-coherency treatments of the classical model
    /// (the transfer conductances are set aside; Kundur, Power System Stability and Control, 1994, section 12.3 on the
    /// classical model's synchronising coefficients [VERIFY section]). Its rows sum to zero, so the COI null is exact up
    /// to rounding; the antisymmetric part it sets aside is reported (<see cref="WaveModes.MaxCouplingAsymmetry"/>). For
    /// two equal masses the symmetrised frequency equals the true one exactly (the non-zero eigenvalue of
    /// M^-1 K_L is (k_12 + k_21) / M either way). The full non-symmetric small-signal problem is the <c>modal</c>
    /// analysis (<see cref="GridSim.Core.Dynamics.SmallSignal.ModalAnalysis"/>), not this lens.</para>
    ///
    /// <para>Bit-identity: for a symmetric <paramref name="k"/> (every caller before R035 except
    /// <see cref="FromSolvedCase"/>) (k_ij + k_ji) / 2 = (2 k_ij) / 2 = k_ij exactly (a doubling and a halving are exact
    /// in binary floating point), so every number is the old one bit for bit.</para>
    /// </summary>
    public static WaveModes Analyze(double[] h, double[,] k, double f0, double[]? d = null, int[]? machineIds = null)
    {
        int n = h.Length;
        if (n == 0) return new WaveModes(Array.Empty<double>(), 0, Array.Empty<double>(), 0, 0, 0, 0, Array.Empty<MachineAmplitude>(), RigidBodyModeCount: 0);
        if (k.GetLength(0) != n || k.GetLength(1) != n)
            throw new ArgumentException(
                $"The coupling matrix must be {n} x {n} (one row and column per machine), not {k.GetLength(0)} x {k.GetLength(1)}.", nameof(k));
        double ws = 2.0 * Math.PI * f0;
        int[] ids = machineIds ?? Enumerable.Range(0, n).ToArray();

        // Inertial "density" per machine: M_i = 2 H_i / w_s (the coefficient of d2 delta/dt2 in the swing).
        var m = new double[n];
        for (int i = 0; i < n; i++) m[i] = 2.0 * Math.Max(MinInertiaConstantS, h[i]) / ws;

        // Synchronising graph-Laplacian K_L of the SYMMETRIC part of the coupling (R035): off-diagonal -k_sym,ij,
        // diagonal the row sum. This is the discretised -K grad^2 operator on the network (K_L phi = w^2 M phi is the
        // wave dispersion). The largest |k_ij - k_ji| (the antisymmetric part set aside) is recorded on the way.
        var kl = new double[n, n];
        double kSum = 0.0; int kCount = 0;
        double maxAsymmetry = 0.0;
        for (int i = 0; i < n; i++)
        {
            double diag = 0.0;
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                double kij = Math.Max(0.0, 0.5 * (k[i, j] + k[j, i]));   // symmetric part, then the clamp
                kl[i, j] = -kij;
                diag += kij;
                if (j > i && kij > 0) { kSum += kij; kCount++; }
                if (j > i) maxAsymmetry = Math.Max(maxAsymmetry, Math.Abs(k[i, j] - k[j, i]));
            }
            kl[i, i] = diag;
        }

        // Symmetric similarity A = M^-1/2 K_L M^-1/2 has the same eigenvalues w^2 as K_L phi = w^2 M phi.
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = kl[i, j] / Math.Sqrt(m[i] * m[j]);

        (double[] lambda, double[,] psi) = SymmetricEigen.EigenDecomposition(a); // ascending; lambda[0] ~ 0 (COI)
        var freqs = new double[n];
        for (int i = 0; i < n; i++) freqs[i] = Math.Sqrt(Math.Max(0.0, lambda[i])) / (2.0 * Math.PI);

        // N282 (October 2026, stream B1; ruled by main under Ruling R1: Mark delegated EE calls 2026-10-04): the number of
        // zero (rigid-body) modes is the number of connected components of the synchronising graph, one per electrical
        // island of machines (the null space of a graph Laplacian has the dimension of its component count; this is how
        // PSS/E and PowerFactory modal analysis treat the rigid-body modes of an islanded system, and Kundur, Power System
        // Stability and Control, 1994, ch. 12 "Small Signal Stability" (chapter confirmed from the Library of Congress
        // contents, N292), on the zero eigenvalue of the swing model; the section number 12.2 is from memory [VERIFY]). Exactly that many modes are
        // skipped and the first mode after them is the dominant inter-area mode. Before, the first mode above 1e-6 Hz was
        // taken: wrong when the COI eigenvalue's round-off lifted it above the threshold (the COI picked as "dominant") and
        // for an islanded network (an island's rigid-body mode picked). For a connected network whose COI mode rounds below
        // 1e-6 Hz and whose slowest real mode is above it (every case the old rule got right) the selection is unchanged.
        int rigid = SynchronisingComponents(kl);
        double coi = freqs[0];
        var interArea = freqs.Skip(rigid).ToArray();

        // Dominant inter-area mode = the first mode after the rigid-body ones. Its physical shape is phi = M^-1/2 psi
        // (we solved the symmetrised A = M^-1/2 K_L M^-1/2); the sign pattern is the areas' split.
        int domIdx = rigid < n ? rigid : -1;
        double dominant = domIdx >= 0 ? freqs[domIdx] : 0.0;

        var shape = new List<MachineAmplitude>(n);
        if (domIdx >= 0)
        {
            var phi = new double[n];
            double maxAbs = 0.0;
            for (int i = 0; i < n; i++) { phi[i] = psi[i, domIdx] / Math.Sqrt(m[i]); maxAbs = Math.Max(maxAbs, Math.Abs(phi[i])); }
            double norm = maxAbs > 0 ? 1.0 / maxAbs : 1.0;
            // Fix the sign so the largest-magnitude machine is positive (a stable convention).
            int imax = 0; for (int i = 1; i < n; i++) if (Math.Abs(phi[i]) > Math.Abs(phi[imax])) imax = i;
            double sign = phi[imax] < 0 ? -1.0 : 1.0;
            for (int i = 0; i < n; i++) shape.Add(new MachineAmplitude(ids[i], phi[i] * norm * sign));
        }

        // Dominant-mode damping ratio zeta ~ (mean D/M) / (2 w_dom): < 1 underdamped (wave-like), the
        // Reynolds-analogue. An approximation (true modal damping needs the mode shape); 0 without D.
        double zeta = 0.0;
        if (d is not null && dominant > MinDampedModeHz)
        {
            double meanDoverM = 0.0;
            for (int i = 0; i < n; i++) meanDoverM += d[i] / m[i];
            meanDoverM /= n;
            zeta = meanDoverM / (2.0 * (2.0 * Math.PI * dominant));
        }

        double meanK = kCount > 0 ? kSum / kCount : 0.0;
        double meanH = h.Average();
        return new WaveModes(freqs, coi, interArea, dominant, zeta, meanK, meanH, shape, rigid, maxAsymmetry);
    }

    /// <summary>
    /// N282: the number of connected components of the synchronising graph whose Laplacian is <paramref name="kl"/>: machines
    /// i and j are joined when either clamped coupling between them is positive (kl[i, j] or kl[j, i] non-zero). An
    /// isolated machine is a component of its own. Breadth-first search, machines in index order, so deterministic.
    /// </summary>
    internal static int SynchronisingComponents(double[,] kl)
    {
        int n = kl.GetLength(0);
        var seen = new bool[n];
        var queue = new Queue<int>();
        int components = 0;
        for (int start = 0; start < n; start++)
        {
            if (seen[start]) continue;
            components++;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                for (int j = 0; j < n; j++)
                    if (!seen[j] && j != i && (kl[i, j] != 0.0 || kl[j, i] != 0.0))
                    {
                        seen[j] = true;
                        queue.Enqueue(j);
                    }
            }
        }
        return components;
    }

    /// <summary>
    /// False when <see cref="FromSolvedCase"/> (without a reactance table) is CERTAIN to throw
    /// <see cref="InvalidOperationException"/> for this model and result: the solve did not converge, there are no
    /// generators, or a generator has no inertia constant (H not &gt; 0) - checked by
    /// <c>ClassicalInit.CertainNoRotorFailure</c> with the same certainty rule, so a malformed input that would fail
    /// some other way first is never reported here (R018 / N021, October 2026). True means only that the call may
    /// succeed: it can still fail loud for its own reasons. Builds no matrix, so a caller that would only catch the
    /// exception (the EXPERIMENTAL wave field, every frame) can skip the call at no cost and get the same answer.
    /// </summary>
    public static bool CanForm(GridModel model, PowerFlowResult result)
    {
        if (!result.Converged) return false;                 // ClassicalInit: "needs a converged power flow"
        if (model.Generators.Count == 0) return false;      // ClassicalInit: "No generators to initialise"
        return ClassicalInit.CertainNoRotorFailure(model, result, holdNonMachines: false) is null;
    }

    /// <summary>
    /// The <see cref="InvalidOperationException"/> message <see cref="FromSolvedCase"/> (without a reactance table) is
    /// CERTAIN to fail with for this model and result, or null when it is not certain to fail (N155, October 2026). It
    /// is exactly the answer of <see cref="CanForm"/> (null exactly when CanForm is true), with the reason: the very
    /// message the call would throw, so a caller can refuse the case BEFORE the classical reduction builds its dense
    /// network matrices (about 1.46 GB on gb-full) and still say the same thing. Builds no matrix.
    /// </summary>
    public static string? CannotFormReason(GridModel model, PowerFlowResult result)
    {
        // The same three checks as CanForm, in the same order, each giving the message ClassicalInit.FromSolvedCase
        // throws at that point.
        if (!result.Converged) return "Classical init needs a converged power flow.";
        if (model.Generators.Count == 0) return "No generators to initialise.";
        return ClassicalInit.CertainNoRotorFailure(model, result, holdNonMachines: false);
    }

    /// <summary>
    /// Build the small-signal wave model from a solved case: the classical reduction
    /// (<see cref="ClassicalInit.FromSolvedCase"/>) gives the machine inertias, internal EMFs and the
    /// reduced admittance, from which the synchronising coefficients k_ij = |E_i||E_j|
    /// (Im Y_red cos d_ij0 - Re Y_red sin d_ij0) at the equilibrium are formed. Requires per-machine
    /// inertia data (fails loud through <see cref="ClassicalInit"/> for a case with no H).
    ///
    /// <para>R035 (October 2026 review; stream B5; Ruling R1): the coefficients are asymmetric whenever the reduced
    /// admittance has a real part and the angles differ, so <see cref="Analyze"/> analyses their symmetric part (see
    /// there) and reports the part set aside. The coefficients are per unit on the case's MVA base (the base the
    /// inertia constants are on, so the eigenvalues are right as they are); the two REPORTED stiffness figures,
    /// <see cref="WaveModes.MeanSynchronisingMwPerRad"/> and <see cref="WaveModes.MaxCouplingAsymmetry"/>, are scaled by
    /// <see cref="GridModel.BaseMva"/> into the MW/rad their name states (before: the per-unit value, 100 times too small
    /// on a 100 MVA case). No frequency or mode shape depends on that scaling.</para>
    /// </summary>
    public static WaveModes FromSolvedCase(GridModel model, PowerFlowResult result, double[]? xdpMachineBase = null)
    {
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, result, xdpMachineBase);
        int g = p.Eq.Length;
        var k = new double[g, g];
        for (int i = 0; i < g; i++)
            for (int j = 0; j < g; j++)
            {
                if (i == j) continue;
                double dij = p.Delta0[i] - p.Delta0[j];
                Complex y = p.YReduced[i, j];
                // Synchronising coefficient (MW/rad on the system base): d(P_e,i)/d(delta_j) sign-flipped.
                k[i, j] = p.Eq[i] * p.Eq[j] * (y.Imaginary * DetMath.Cos(dij) - y.Real * DetMath.Sin(dij));
            }
        WaveModes w = Analyze(p.H, k, model.FrequencyHz, d: null, machineIds: p.GeneratorBuses.ToArray());
        // R035: report the stiffness in MW/rad (per unit on the case base times the base), as the field names say.
        return w with
        {
            MeanSynchronisingMwPerRad = w.MeanSynchronisingMwPerRad * model.BaseMva,
            MaxCouplingAsymmetry = w.MaxCouplingAsymmetry * model.BaseMva,
        };
    }
}
