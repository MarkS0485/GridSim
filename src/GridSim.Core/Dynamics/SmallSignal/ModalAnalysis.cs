using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.SmallSignal;

/// <summary>One electromechanical mode: its eigenvalue s = sigma + j*omega (rad/s), damping ratio, modal
/// frequency and the per-state participation factors (which state drives the mode).</summary>
/// <param name="Eigenvalue">s = sigma + j*omega (rad/s).</param>
/// <param name="DampingRatio">zeta = -sigma / |s| (0 for a pure rigid/undamped mode).</param>
/// <param name="FrequencyHz">|omega| / 2pi (0 for a non-oscillatory real mode).</param>
/// <param name="IsOscillatory">|omega| above tolerance.</param>
/// <param name="ParticipationByState">Normalised participation p_k (sum 1 over the analysed states);
/// length N. Rigid-body COI modes carry an all-zero vector (participation is defined only on the deflated
/// electromechanical subspace).</param>
public sealed record Mode(
    Complex Eigenvalue,
    double DampingRatio,
    double FrequencyHz,
    bool IsOscillatory,
    IReadOnlyList<double> ParticipationByState);

/// <summary>The modal-analysis result: the sorted modes, the state labels, the RHP eigenvalue count (the
/// small-signal instability count) and the minimum damping ratio over the oscillatory modes.</summary>
public sealed record ModalResult(
    IReadOnlyList<Mode> Modes,
    IReadOnlyList<StateLabel> States,
    int RhpEigenvalueCount,
    double MinDampingRatio)
{
    /// <summary>N011: true when the eigenvector decomposition found the state matrix DEFECTIVE (a Jordan block,
    /// so biorthonormal left/right eigenvectors do not exist) and participation factors could NOT be computed.
    /// The modes then carry eigenvalues only and every <see cref="Mode.ParticipationByState"/> is all zero. This
    /// is a reported outcome, never a silent drop: a caller that shows participation must check this flag and
    /// show <see cref="ParticipationUnavailableReason"/> instead. False on every result where the decomposition
    /// succeeded (those results are unchanged).</summary>
    public bool ParticipationUnavailable { get; init; }

    /// <summary>N011: why participation is unavailable (which path failed, and the decomposition's own
    /// message); null when <see cref="ParticipationUnavailable"/> is false.</summary>
    public string? ParticipationUnavailableReason { get; init; }
}

/// <summary>
/// Modal (small-signal) analysis of an assembled <see cref="SmallSignalModel"/>: eigenvalues, damping
/// ratios, modal frequencies, participation factors and the right-half-plane count. Eigenvalues come from
/// the robust Francis-QR path (no eigenvectors needed). Participation needs biorthonormal left/right
/// vectors; when A is DEFECTIVE (the default undamped swing system's rigid-body COI Jordan block) the
/// right-eigenvector matrix is singular, so we DEFLATE the rigid mode first - a reference-relative
/// reduction onto the electromechanical subspace - and compute participation there. We never fabricate a
/// participation vector for a defective mode.
/// </summary>
public static class ModalAnalysis
{
    /// <summary>
    /// Default right-half-plane threshold (1/s): a mode whose real part exceeds this counts as unstable. It is
    /// ABSOLUTE, not scaled by the size of A. The review (R172) notes that at gb-full scale Francis-QR rounding
    /// on the damped path's simple zero eigenvalue could exceed it; scaling it by a matrix norm would change
    /// which modes are counted, so the value is kept and the question is left for Mark (NOTES-FOR-MAIN.md).
    /// </summary>
    public const double DefaultRhpTolerance = 1e-9;

    /// <summary>A mode whose |s| is at or below this (1/s) is a zero mode: its damping ratio is reported as 0
    /// rather than the undefined -sigma/|s|.</summary>
    public const double ZeroModeMagnitude = 1e-12;

    /// <summary>A mode whose |imaginary part| (rad/s) exceeds this is classed as oscillatory.</summary>
    public const double OscillatoryImagTolerance = 1e-9;

    public static ModalResult Analyze(SmallSignalModel model, double rhpTol = DefaultRhpTolerance)
    {
        double[,] a = model.A;
        int n = model.Order;

        // Eigenpairs with participation. The default undamped swing system is DEFECTIVE (the rigid-body COI
        // double-zero Jordan block): GeneralEigen fails loud, and we deflate the rigid mode - computing the
        // electromechanical modes on the non-defective reduced subspace and appending the COI mode(s) as
        // exact, marginal s = 0. This also keeps the defective double-zero out of the eigenvalue path, where
        // Francis-QR would otherwise split it into a spurious +/- tiny real pair and mis-count the RHP set.
        bool swingOnly = !model.InverterStatesIncluded
            && model.States.All(s => s.Kind is StateKind.SwingAngle or StateKind.SwingSpeed);

        var raw = new List<(Complex Value, double[] Part)>();
        // N011: set when GeneralEigen.Decompose reports a defective matrix on either path, so the result says
        // plainly that participation is missing (it used to crash on the undamped path and be silently dropped on
        // the damped path).
        string? participationFailure = null;
        if (swingOnly && IsUndampedSwing(model))
        {
            // DEFAULT undamped path: A has a DEFECTIVE rigid-body COI double-zero, so its right-eigenvector
            // matrix is singular and Francis-QR would split the double-zero into a spurious +/- tiny real
            // pair (a false RHP). Deflate the rigid mode deterministically: compute the electromechanical
            // modes on the non-defective reference-relative subspace, then append the COI mode(s) as exact,
            // marginal s = 0.
            (double[,] aRed, int[] fullIdx) = DeflateRigidSwing(model);
            // Electromechanical modes on the non-defective reduced subspace; participation is defined on
            // that DEFLATED subspace (relative to the reference machine) and embedded back into the full
            // state vector (the reference machine's states carry the COI datum and read 0).
            // N011: the reduced subspace is non-defective GENERICALLY, not always (for example two machines with
            // no synchronising coupling leave a Jordan block). A defective reduced matrix used to escape as an
            // unhandled exception. It now falls back to eigenvalues only, with the failure recorded on the result.
            // The happy path is the same loop over the same Decompose call, so its numbers are unchanged.
            IReadOnlyList<GeneralEigen.Eigenpair>? pairs = null;
            try
            {
                pairs = GeneralEigen.Decompose(aRed);
            }
            catch (GeneralEigen.DefectiveEigenproblemException ex)
            {
                participationFailure =
                    "undamped swing path: the deflated electromechanical state matrix is defective, so participation " +
                    "factors are undefined and are reported as all zero (eigenvalues only; a split of a repeated " +
                    "eigenvalue may also affect the right-half-plane count). Decomposition said: " + ex.Message;
            }
            if (pairs is not null)
            {
                foreach (GeneralEigen.Eigenpair pr in pairs)
                    raw.Add((pr.Value, EmbedParticipation(pr, fullIdx, n)));
            }
            else
            {
                foreach (Complex s in GeneralEigen.Eigenvalues(aRed)) raw.Add((s, new double[n]));
            }
            int appended = n - 2 * (model.MachineCount - 1);
            for (int i = 0; i < appended; i++) raw.Add((Complex.Zero, new double[n]));
        }
        else
        {
            // Damped swing / inverter-augmented: A has at most a SIMPLE zero (non-defective) - decompose
            // directly. A defective surprise falls back to eigenvalues only, and (N011) the failure is now
            // RECORDED on the result instead of the participation being dropped without a word.
            try
            {
                foreach (GeneralEigen.Eigenpair pr in GeneralEigen.Decompose(a))
                    raw.Add((pr.Value, Participation(pr, n)));
            }
            catch (GeneralEigen.DefectiveEigenproblemException ex)
            {
                // Decompose throws before returning, so nothing was added above; clear defensively anyway.
                raw.Clear();
                participationFailure =
                    "damped / inverter-augmented path: the state matrix is defective, so participation factors are " +
                    "undefined and are reported as all zero (eigenvalues only). Decomposition said: " + ex.Message;
                foreach (Complex s in GeneralEigen.Eigenvalues(a)) raw.Add((s, new double[n]));
            }
        }

        var modes = new List<Mode>(raw.Count);
        int rhp = 0;
        foreach ((Complex s, double[] part) in raw)
        {
            double sigma = s.Real, omega = s.Imaginary;
            double mag = Math.Sqrt(sigma * sigma + omega * omega);
            double zeta = mag > ZeroModeMagnitude ? -sigma / mag : 0.0;
            bool osc = Math.Abs(omega) > OscillatoryImagTolerance;
            if (sigma > rhpTol) rhp++;
            modes.Add(new Mode(s, zeta, Math.Abs(omega) / (2.0 * Math.PI), osc, part));
        }

        // Sort: oscillatory modes first, most-underdamped (smallest zeta) first; then real modes. The two members of a
        // conjugate pair have bit-identical zeta, so they tie, and their order is the sort algorithm's: GridSim's frozen
        // copy of the .NET 10 introsort (FrozenIntroSort), so the reported order (and each member's participation row)
        // is the same on every runtime, and on .NET 10 exactly what List.Sort gave.
        Numerics.FrozenIntroSort.Sort(modes, (x, y) =>
        {
            if (x.IsOscillatory != y.IsOscillatory) return x.IsOscillatory ? -1 : 1;
            if (x.IsOscillatory) return x.DampingRatio.CompareTo(y.DampingRatio);
            return x.Eigenvalue.Real.CompareTo(y.Eigenvalue.Real);
        });

        double minZeta = 1.0;
        bool any = false;
        foreach (Mode m in modes)
            if (m.IsOscillatory) { any = true; if (m.DampingRatio < minZeta) minZeta = m.DampingRatio; }
        if (!any) minZeta = double.NaN;

        return new ModalResult(modes, model.States, rhp, minZeta)
        {
            ParticipationUnavailable = participationFailure is not null,
            ParticipationUnavailableReason = participationFailure,
        };
    }

    // Normalised magnitude participation p_k = |psi_k| |phi_k| / sum_k, sum = 1.
    private static double[] Participation(GeneralEigen.Eigenpair pr, int n)
    {
        var raw = new double[n];
        double sum = 0.0;
        for (int k = 0; k < n; k++) { raw[k] = pr.Left[k].Magnitude * pr.Right[k].Magnitude; sum += raw[k]; }
        if (sum > 0) for (int k = 0; k < n; k++) raw[k] /= sum;
        return raw;
    }

    // Embed a reduced-subspace participation into the full state vector (reference-machine states stay 0).
    private static double[] EmbedParticipation(GeneralEigen.Eigenpair pr, int[] fullIdx, int n)
    {
        int m = fullIdx.Length;
        var full = new double[n];
        double sum = 0.0;
        for (int k = 0; k < m; k++)
        {
            double v = pr.Left[k].Magnitude * pr.Right[k].Magnitude;
            full[fullIdx[k]] = v; sum += v;
        }
        if (sum > 0) for (int k = 0; k < m; k++) full[fullIdx[k]] /= sum;
        return full;
    }

    // Undamped iff the speed-speed sub-block (-M^-1 D) is exactly zero.
    private static bool IsUndampedSwing(SmallSignalModel model)
    {
        int g = model.MachineCount;
        double[,] a = model.A;
        for (int i = 0; i < g; i++)
            for (int j = 0; j < g; j++)
                if (a[g + i, g + j] != 0.0) return false;
        return true;
    }

    /// <summary>
    /// Reference-relative reduction that deflates the rigid-body COI mode of the swing-only system. Picks
    /// as reference the machine with the smallest |A[speed, angle]| = |K_ii / 2H_i| (GR-562: this depends on the
    /// synchronising coefficient as well as on H, so it is not always the largest-inertia machine; the deflation is
    /// exact for any reference, and only which machine's states read zero participation depends on it), forms relative
    /// angles phi_i = d(delta)_i - d(delta)_r and relative speeds nu_i = d(omega)_i - d(omega)_r for i != r,
    /// giving a 2(g-1) system whose eigenvalues are exactly the electromechanical (non-COI) modes and whose
    /// A is (generically) non-defective. Also returns the map reduced-state -> full-state index. Exact for
    /// the undamped case (the only case that triggers this path).
    /// </summary>
    private static (double[,] ARed, int[] FullIdx) DeflateRigidSwing(SmallSignalModel model)
    {
        int g = model.MachineCount;
        // Assembler layout: angle i at index i, speed i at index g+i.
        int Ai(int i) => i;
        int Si(int i) => g + i;
        double[,] a = model.A;
        double ws = a[Ai(0), Si(0)];

        int r = 0; double best = double.PositiveInfinity;
        for (int i = 0; i < g; i++)
        {
            double d = Math.Abs(a[Si(i), Ai(i)]);
            if (d < best) { best = d; r = i; }
        }

        var others = new List<int>(g - 1);
        for (int i = 0; i < g; i++) if (i != r) others.Add(i);
        int gm = others.Count;
        var aRed = new double[2 * gm, 2 * gm];
        var fullIdx = new int[2 * gm];
        for (int k = 0; k < gm; k++)
        {
            int mi = others[k];
            fullIdx[2 * k] = Ai(mi);       // relative angle -> machine angle slot
            fullIdx[2 * k + 1] = Si(mi);   // relative speed -> machine speed slot
            aRed[2 * k, 2 * k + 1] = ws;   // d(phi_i)/dt = ws * nu_i
            for (int l = 0; l < gm; l++)
            {
                int ml = others[l];
                // d(nu_i)/dt = A[Si,Ai_l] d(delta)_l - A[Sr,Ai_l] d(delta)_l, expressed in relative angles.
                aRed[2 * k + 1, 2 * l] = a[Si(mi), Ai(ml)] - a[Si(r), Ai(ml)];
            }
        }
        return (aRed, fullIdx);
    }
}
