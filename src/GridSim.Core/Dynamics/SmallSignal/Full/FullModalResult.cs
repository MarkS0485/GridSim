using System.Globalization;
using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.SmallSignal.Full;

/// <summary>Whether a full modal result can be relied on as a statement about the whole requested set.</summary>
public enum FullModalStatus
{
    /// <summary>Every returned eigenpair met the residual gate, every controller was linearised and (Arnoldi) the iteration converged.</summary>
    Complete,
    /// <summary>At least one eigenpair failed the residual gate, the Arnoldi iteration did not converge, or a controller was not
    /// linearised. The reasons are listed on the result; the numbers are shown but are not a complete statement.</summary>
    Incomplete,
}

/// <summary>A rectangle of the complex s-plane, given by two opposite corners (real part, imaginary part).</summary>
/// <param name="ReMin">Smaller real part.</param>
/// <param name="ImMin">Smaller imaginary part.</param>
/// <param name="ReMax">Larger real part.</param>
/// <param name="ImMax">Larger imaginary part.</param>
public sealed record ModalRegion(double ReMin, double ImMin, double ReMax, double ImMax)
{
    /// <summary>Normalise two corners given in any order.</summary>
    public static ModalRegion FromCorners(double re1, double im1, double re2, double im2)
    {
        foreach (double v in new[] { re1, im1, re2, im2 })
            if (!double.IsFinite(v)) throw new ArgumentException("A region corner must be a finite number.");
        return new ModalRegion(Math.Min(re1, re2), Math.Min(im1, im2), Math.Max(re1, re2), Math.Max(im1, im2));
    }

    /// <summary>Centre of the rectangle.</summary>
    public Complex Centre => new((ReMin + ReMax) / 2.0, (ImMin + ImMax) / 2.0);

    /// <summary>Whether <paramref name="s"/> lies in the closed rectangle.</summary>
    public bool Contains(Complex s) => s.Real >= ReMin && s.Real <= ReMax && s.Imaginary >= ImMin && s.Imaginary <= ImMax;

    /// <summary>Largest distance from the centre to a corner.</summary>
    public double CornerRadius => Math.Sqrt(((ReMax - ReMin) / 2.0) * ((ReMax - ReMin) / 2.0) + ((ImMax - ImMin) / 2.0) * ((ImMax - ImMin) / 2.0));
}

/// <summary>Options of <see cref="FullModalAnalysis"/>.</summary>
/// <param name="Region">Rectangle of interest; null means all eigenvalues (dense) or the electromechanical band (Arnoldi).</param>
/// <param name="Count">Eigenvalues wanted by the Arnoldi path (nearest the centre of the region).</param>
/// <param name="DenseOrderLimit">States up to this order go to the dense <see cref="GeneralEigen"/>; above it, Arnoldi.</param>
/// <param name="ResidualGate">Relative residual gate <c>||A v - lambda v|| / (||A||_1 ||v||)</c> every returned pair must meet.</param>
/// <param name="ForceMethod">0 automatic, 1 dense, 2 Arnoldi.</param>
/// <param name="RhpTolerance">A real part above this counts as right-half-plane.</param>
/// <param name="Seed">Seed of the Arnoldi start vector.</param>
/// <param name="Ncv">Krylov basis size (0 automatic).</param>
/// <param name="MaxRestarts">Cap on implicit restarts.</param>
public sealed record FullModalOptions(
    ModalRegion? Region = null,
    int Count = 20,
    int DenseOrderLimit = FullModalOptions.DefaultDenseOrderLimit,
    double ResidualGate = 1e-9,
    int ForceMethod = 0,
    double RhpTolerance = ModalAnalysis.DefaultRhpTolerance,
    ulong Seed = ArnoldiOptions.DefaultSeed,
    int Ncv = 0,
    int MaxRestarts = 300)
{
    /// <summary>The stated order above which Arnoldi replaces the dense solver.</summary>
    public const int DefaultDenseOrderLimit = 200;

    /// <summary>The band searched by the Arnoldi path when no region is given: real part from -5 to +1 per second, 0.1 to 3 Hz.</summary>
    public static ModalRegion DefaultBand { get; } = new(-5.0, 2.0 * Math.PI * 0.1, 1.0, 2.0 * Math.PI * 3.0);
}

/// <summary>One mode of the full model.</summary>
/// <param name="Mode">The shared vocabulary: eigenvalue, damping ratio, frequency, oscillatory flag, participation of every state.</param>
/// <param name="InRightHalfPlane">Real part above the right-half-plane tolerance.</param>
/// <param name="RelativeResidual"><c>||A v - lambda v|| / (||A||_1 ||v||)</c>.</param>
/// <param name="ResidualPassed">The residual met the gate.</param>
/// <param name="ParticipationAvailable">False when the left and right eigenvectors are not biorthogonal (a defective mode).</param>
/// <param name="ParticipationByCategory">Participation summed by <see cref="FullStateCategory"/> (machine angle, speed, flux, exciter, governor, stabiliser).</param>
public sealed record FullMode(Mode Mode, bool InRightHalfPlane, double RelativeResidual, bool ResidualPassed, bool ParticipationAvailable,
    IReadOnlyList<double> ParticipationByCategory);

/// <summary>The result of <see cref="FullModalAnalysis"/>.</summary>
public sealed class FullModalResult
{
    /// <summary>Modes, oscillatory first (least damped first), then real modes, as <see cref="ModalAnalysis"/> orders them.</summary>
    public IReadOnlyList<FullMode> Modes { get; init; } = Array.Empty<FullMode>();

    /// <summary>The labelled states.</summary>
    public IReadOnlyList<FullStateLabel> States { get; init; } = Array.Empty<FullStateLabel>();

    /// <summary>State count.</summary>
    public int Order { get; init; }

    /// <summary><c>dense GeneralEigen</c> or <c>shift-invert Arnoldi</c>.</summary>
    public string Method { get; init; } = "";

    /// <summary>COMPLETE or INCOMPLETE.</summary>
    public FullModalStatus Status { get; init; }

    /// <summary>Why the result is INCOMPLETE (empty when complete).</summary>
    public IReadOnlyList<string> IncompleteReasons { get; init; } = Array.Empty<string>();

    /// <summary>Eigenvalues with real part above the tolerance among the modes reported.</summary>
    public int RhpCount { get; init; }

    /// <summary>Smallest damping ratio among the oscillatory modes reported (NaN if none).</summary>
    public double MinDampingRatio { get; init; } = double.NaN;

    /// <summary>The requested region (null: none).</summary>
    public ModalRegion? Region { get; init; }

    /// <summary>True when only the eigenvalues in a region (or the nearest few) were computed, so the list is NOT the whole spectrum.</summary>
    public bool PartialSpectrum { get; init; }

    /// <summary>Plain statement of what the list covers (for example "the 20 eigenvalues nearest ... are complete within radius ...").</summary>
    public string Coverage { get; init; } = "";

    /// <summary>Arnoldi shift (centre of the search).</summary>
    public Complex? Centre { get; init; }

    /// <summary>Within this distance of <see cref="Centre"/> the listing is complete (Arnoldi, converged).</summary>
    public double CoverageRadius { get; init; }

    /// <summary>Largest relative residual among the reported pairs.</summary>
    public double MaxRelativeResidual { get; init; }

    /// <summary>The gate used.</summary>
    public double ResidualGate { get; init; }

    /// <summary>Participation could not be computed for at least one mode (a defective matrix).</summary>
    public bool ParticipationUnavailable { get; init; }

    /// <summary>Why participation is unavailable (null when available everywhere).</summary>
    public string? ParticipationUnavailableReason { get; init; }

    /// <summary>Controllers named by the data that are not in A.</summary>
    public IReadOnlyList<NotLinearisedController> NotLinearised { get; init; } = Array.Empty<NotLinearisedController>();

    /// <summary>Statements from the assembly and the solver.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Arnoldi restarts (0 for dense).</summary>
    public int Restarts { get; init; }

    /// <summary>Arnoldi operator applications (0 for dense).</summary>
    public int MatVecs { get; init; }

    /// <summary>Arnoldi basis size (0 for dense).</summary>
    public int KrylovDimension { get; init; }

    /// <summary>The seed of the Arnoldi start vector (R4-02 stream, domain <see cref="ArnoldiOptions.StartDomain"/>).</summary>
    public ulong StartSeed { get; init; }

    /// <summary>The request for the number of eigenvalues (Arnoldi).</summary>
    public int RequestedCount { get; init; }

    /// <summary>SHA-256 of the eigenvalue bit patterns in reported order (a determinism witness).</summary>
    public string EigenvalueDigest { get; init; } = "";
}

/// <summary>
/// R4-12: modal analysis of the full small-signal model. The order of operations is: dense <see cref="GeneralEigen"/> below the
/// stated order, shift-invert Arnoldi above it (or when forced); a residual check <c>||A v - lambda v||</c> for EVERY returned
/// pair; participation of every state; the report. An eigenpair failing the residual gate, an Arnoldi iteration that did not
/// converge, or a controller that was not linearised makes the result INCOMPLETE. Nothing is hidden: failing pairs are listed and
/// marked.
/// </summary>
public static class FullModalAnalysis
{
    private const double ParticipationBiorthogonalityFloor = 1e-8;

    // inverse iteration factorises A - (lambda + eps) I on purpose: only an exactly zero pivot is a failure
    private const double NearSingularRatio = 1e-15;

    /// <summary>Analyse an assembled full model.</summary>
    public static FullModalResult Analyze(FullSmallSignalModel model, FullModalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new FullModalOptions();
        FullModalResult r = model.DelegatedToClassical && model.Classical is { } classical && options.ForceMethod == 0
            ? Delegated(classical, model.States, options)
            : AnalyzeMatrix(model.A, model.States, options);
        var reasons = r.IncompleteReasons.ToList();
        foreach (NotLinearisedController nl in model.NotLinearised)
            reasons.Add($"not linearised: bus {nl.Bus} {nl.Controller} - {nl.Reason}");
        return new FullModalResult
        {
            Modes = r.Modes, States = r.States, Order = r.Order, Method = r.Method,
            Status = reasons.Count == 0 ? FullModalStatus.Complete : FullModalStatus.Incomplete,
            IncompleteReasons = reasons, RhpCount = r.RhpCount, MinDampingRatio = r.MinDampingRatio, Region = r.Region,
            PartialSpectrum = r.PartialSpectrum, Coverage = r.Coverage, Centre = r.Centre, CoverageRadius = r.CoverageRadius,
            MaxRelativeResidual = r.MaxRelativeResidual, ResidualGate = r.ResidualGate, ParticipationUnavailable = r.ParticipationUnavailable,
            ParticipationUnavailableReason = r.ParticipationUnavailableReason, NotLinearised = model.NotLinearised,
            Notes = r.Notes.Concat(model.Notes).ToList(), Restarts = r.Restarts, MatVecs = r.MatVecs, KrylovDimension = r.KrylovDimension,
            StartSeed = r.StartSeed, RequestedCount = r.RequestedCount, EigenvalueDigest = r.EigenvalueDigest,
        };
    }

    /// <summary>Analyse a state matrix with its labels.</summary>
    public static FullModalResult AnalyzeMatrix(double[,] a, IReadOnlyList<FullStateLabel> states, FullModalOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(options);
        int n = a.GetLength(0);
        if (n != a.GetLength(1) || states.Count != n) throw new ArgumentException("The matrix must be square and match the state labels.", nameof(a));
        if (!(options.ResidualGate > 0)) throw new ArgumentOutOfRangeException(nameof(options), "The residual gate must be above 0.");
        bool dense = options.ForceMethod == 1 || (options.ForceMethod == 0 && n <= options.DenseOrderLimit);
        return dense ? Dense(a, states, options, n) : Krylov(a, states, options, n);
    }

    // ---- the classical swing model: the incumbent's own modal analysis ----------------------------------------------------------

    private static FullModalResult Delegated(SmallSignalModel classical, IReadOnlyList<FullStateLabel> states, FullModalOptions options)
    {
        int n = classical.Order;
        ModalResult mr = ModalAnalysis.Analyze(classical, options.RhpTolerance);
        var op = new DenseArnoldiOperator(classical.A);
        var modes = new List<FullMode>(mr.Modes.Count);
        double maxRes = 0.0;
        foreach (Mode m in mr.Modes)
        {
            Complex[] v = InverseIterationRight(op, m.Eigenvalue, n, ArnoldiOptions.DefaultSeed);
            double res = RelativeResidual(op, m.Eigenvalue, v);
            maxRes = Math.Max(maxRes, res);
            bool avail = !mr.ParticipationUnavailable && m.ParticipationByState.Any(x => x != 0.0);
            var cat = new double[Enum.GetValues<FullStateCategory>().Length];
            for (int k = 0; k < n; k++) cat[(int)states[k].Category] += m.ParticipationByState[k];
            modes.Add(new FullMode(m, m.Eigenvalue.Real > options.RhpTolerance, res, res <= options.ResidualGate, avail, cat));
        }
        ModalRegion? region = options.Region;
        int total = modes.Count;
        if (region is not null) modes = modes.Where(x => region.Contains(x.Mode.Eigenvalue)).ToList();
        var notes = new List<string> { "classical swing model: eigenvalues and participation are ModalAnalysis.Analyze's own (the rigid-body mode is deflated there); residuals are checked here with inverse-iteration vectors." };
        string coverage = region is null ? $"all {total} eigenvalues" : $"all {total} eigenvalues were computed; {modes.Count} lie in the requested region and are listed";
        return Finish(modes, states, n, "classical swing (ModalAnalysis)", options, region, region is not null, coverage, null, double.PositiveInfinity, maxRes,
            mr.ParticipationUnavailable, mr.ParticipationUnavailableReason, notes, 0, 0, 0, 0, new List<string>(), 0, presorted: true);
    }

    // ---- dense ---------------------------------------------------------------------------------------------------------------

    private static FullModalResult Dense(double[,] a, IReadOnlyList<FullStateLabel> states, FullModalOptions options, int n)
    {
        var op = new DenseArnoldiOperator(a);
        var notes = new List<string>();
        var raw = new List<(Complex Value, Complex[] Right, double[]? Part)>();
        string? declined = null;
        try
        {
            foreach (GeneralEigen.Eigenpair pr in GeneralEigen.Decompose(a))
            {
                double[] part = new double[n];
                double sum = 0.0;
                for (int k = 0; k < n; k++) { part[k] = ComplexLinearAlgebra.Abs(pr.Left[k]) * ComplexLinearAlgebra.Abs(pr.Right[k]); sum += part[k]; }
                if (sum > 0) for (int k = 0; k < n; k++) part[k] /= sum;
                raw.Add((pr.Value, pr.Right.ToArray(), part));
            }
        }
        catch (GeneralEigen.DefectiveEigenproblemException ex)
        {
            declined = ex.Message;
            raw.Clear();
            double zeroFloor = 1e-6 * Math.Max(op.Norm1, 1.0);
            int collapsed = 0;
            foreach (Complex s0 in GeneralEigen.Eigenvalues(a))
            {
                // a defective zero (the undamped rigid-body mode) splits into a spurious pair of size ~sqrt(eps)||A||; it is reported as the marginal 0
                Complex s = ComplexLinearAlgebra.Abs(s0) < zeroFloor ? Complex.Zero : s0;
                if (s == Complex.Zero && s0 != Complex.Zero) collapsed++;
                Complex[] v = InverseIterationRight(op, s, n, ArnoldiOptions.DefaultSeed);
                double[] part = new double[n];
                raw.Add((s, v, Participation(op, s, v, n, part) ? part : null));
            }
            if (collapsed > 0) notes.Add($"{collapsed} eigenvalue(s) of size below 1e-6 ||A||_1 in a defective decomposition are reported as exactly 0 (the numerical split of a defective zero); they are not counted as right-half-plane.");
            notes.Add("GeneralEigen.Decompose declined this matrix (" + declined + "); eigenvalues are from GeneralEigen.Eigenvalues and the eigenvectors of every mode from shift-invert inverse iteration, so participation is computed per mode and is unavailable only where the left and right vectors are not biorthogonal.");
        }

        var modes = new List<FullMode>(raw.Count);
        double maxRes = 0.0;
        int unavailable = 0;
        foreach ((Complex lam, Complex[] right, double[]? part) in raw)
        {
            double res = RelativeResidual(op, lam, right);
            maxRes = Math.Max(maxRes, res);
            if (part is null) unavailable++;
            modes.Add(MakeMode(lam, part ?? new double[n], res, options, states, part is not null));
        }
        string? partFail = unavailable > 0 ? $"{unavailable} mode(s) are defective or nearly so (left and right eigenvectors not biorthogonal), so their participation factors are undefined and reported as unavailable." : null;

        ModalRegion? region = options.Region;
        int total = modes.Count;
        if (region is not null) modes = modes.Where(m => region.Contains(m.Mode.Eigenvalue)).ToList();
        bool partial = region is not null;
        string coverage = region is null
            ? $"all {total} eigenvalues (dense decomposition)"
            : $"all {total} eigenvalues were computed (dense decomposition); {modes.Count} lie in the requested region [{R(region.ReMin)}, {R(region.ImMin)}] to [{R(region.ReMax)}, {R(region.ImMax)}] and are listed";
        return Finish(modes, states, n, "dense GeneralEigen", options, region, partial, coverage, null, double.PositiveInfinity, maxRes,
            partFail is not null, partFail, notes, 0, 0, 0, 0, new List<string>(), unavailable);
    }

    // ---- shift-invert Arnoldi -------------------------------------------------------------------------------------------------

    private static FullModalResult Krylov(double[,] a, IReadOnlyList<FullStateLabel> states, FullModalOptions options, int n)
    {
        var op = new DenseArnoldiOperator(a);
        ModalRegion region = options.Region ?? FullModalOptions.DefaultBand;
        bool defaultBand = options.Region is null;
        Complex centre = region.Centre;
        var notes = new List<string>();
        if (defaultBand) notes.Add($"no region given: the electromechanical band real part [{R(region.ReMin)}, {R(region.ReMax)}] 1/s, imaginary part [{R(region.ImMin)}, {R(region.ImMax)}] rad/s (0.1 to 3 Hz) is searched.");

        int count = Math.Max(1, options.Count);
        ArnoldiResult res = ArnoldiEigen.Compute(op, centre, new ArnoldiOptions(count, options.Ncv, options.MaxRestarts, options.ResidualGate, options.Seed));
        var incomplete = new List<string>();
        if (!res.Converged) incomplete.Add($"the Arnoldi iteration did not converge all {count} requested pairs within its restart cap ({res.Restarts} restarts).");
        if (res.ShiftPerturbed) notes.Add("the requested centre was an eigenvalue of A, so the shift was moved by the fixed rule to " + R(res.Shift.Real) + " + j" + R(res.Shift.Imaginary) + ".");

        var modes = new List<FullMode>();
        double maxRes = 0.0, radius = 0.0;
        int unavailable = 0;
        foreach (ArnoldiPair p in res.Pairs)
        {
            maxRes = Math.Max(maxRes, p.RelativeResidual);
            radius = Math.Max(radius, ComplexLinearAlgebra.Abs(p.Value - centre));
            double[] part = new double[n];
            bool avail = Participation(op, p, n, part);
            if (!avail) unavailable++;
            modes.Add(MakeMode(p.Value, part, p.RelativeResidual, options, states, avail));
        }
        int failing = res.Pairs.Count(p => !p.PassesGate);
        if (failing > 0) incomplete.Add(FormattableString.Invariant($"{failing} returned eigenpair(s) failed the residual gate {options.ResidualGate:E1} (largest relative residual {maxRes:E2})."));

        bool allIn = modes.All(m => region.Contains(m.Mode.Eigenvalue));
        modes = modes.Where(m => region.Contains(m.Mode.Eigenvalue)).ToList();
        bool covered = res.Converged && failing == 0 && radius >= region.CornerRadius;
        string coverage = $"{res.Pairs.Count} eigenvalue(s) nearest the centre {R(centre.Real)} + j{R(centre.Imaginary)} were computed; the listing is complete within a distance of {R(radius)} of the centre"
            + (covered ? ", which covers the whole region" : $", which does NOT cover the whole region (its corners are {R(region.CornerRadius)} from the centre): eigenvalues in the rest of the region may exist and are not listed; raise --count")
            + (allIn ? "" : "; eigenvalues outside the region that were found are not listed")
            + ". A real matrix has conjugate pairs: the conjugate of a listed eigenvalue is also an eigenvalue.";
        string? partReason = unavailable > 0 ? $"{unavailable} mode(s) have left and right eigenvectors that are not biorthogonal (a defective or nearly defective mode); participation is unavailable for them." : null;
        return Finish(modes, states, n, "shift-invert Arnoldi", options, region, true, coverage, centre, radius, maxRes,
            unavailable > 0, partReason, notes, res.Restarts, res.MatVecs, res.Ncv, count, incomplete, unavailable);
    }

    // ---- shared ----------------------------------------------------------------------------------------------------------------

    private static FullModalResult Finish(List<FullMode> modes, IReadOnlyList<FullStateLabel> states, int n, string method, FullModalOptions options,
        ModalRegion? region, bool partial, string coverage, Complex? centre, double radius, double maxRes, bool partUnavailable, string? partReason,
        List<string> notes, int restarts, int matvecs, int ncv, int requested, List<string> incomplete, int unavailableModes, bool presorted = false)
    {
        _ = unavailableModes;
        var list = modes.ToList();
        if (!presorted) FrozenIntroSort.Sort(list, (x, y) =>
        {
            if (x.Mode.IsOscillatory != y.Mode.IsOscillatory) return x.Mode.IsOscillatory ? -1 : 1;
            if (x.Mode.IsOscillatory) return x.Mode.DampingRatio.CompareTo(y.Mode.DampingRatio);
            return x.Mode.Eigenvalue.Real.CompareTo(y.Mode.Eigenvalue.Real);
        });
        int rhp = list.Count(m => m.InRightHalfPlane);
        double minZ = double.NaN;
        foreach (FullMode m in list)
            if (m.Mode.IsOscillatory && (double.IsNaN(minZ) || m.Mode.DampingRatio < minZ)) minZ = m.Mode.DampingRatio;
        foreach (FullMode m in list)
            if (!m.ResidualPassed && !incomplete.Any(s => s.Contains("residual gate", StringComparison.Ordinal)))
                incomplete.Add(FormattableString.Invariant($"an eigenpair failed the residual gate {options.ResidualGate:E1}."));
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            list.SelectMany(m => new[] { BitConverter.GetBytes(m.Mode.Eigenvalue.Real), BitConverter.GetBytes(m.Mode.Eigenvalue.Imaginary) }).SelectMany(b => b).ToArray())).ToLowerInvariant();
        return new FullModalResult
        {
            Modes = list, States = states, Order = n, Method = method,
            Status = incomplete.Count == 0 ? FullModalStatus.Complete : FullModalStatus.Incomplete,
            IncompleteReasons = incomplete, RhpCount = rhp, MinDampingRatio = minZ, Region = region, PartialSpectrum = partial,
            Coverage = coverage, Centre = centre, CoverageRadius = radius, MaxRelativeResidual = maxRes, ResidualGate = options.ResidualGate,
            ParticipationUnavailable = partUnavailable, ParticipationUnavailableReason = partReason, Notes = notes, Restarts = restarts,
            MatVecs = matvecs, KrylovDimension = ncv, StartSeed = options.Seed, RequestedCount = requested, EigenvalueDigest = digest,
        };
    }

    private static FullMode MakeMode(Complex s, double[] part, double residual, FullModalOptions options, IReadOnlyList<FullStateLabel> states, bool avail)
    {
        double sigma = s.Real, omega = s.Imaginary;
        double mag = Math.Sqrt(sigma * sigma + omega * omega);
        double zeta = mag > ModalAnalysis.ZeroModeMagnitude ? -sigma / mag : 0.0;
        bool osc = Math.Abs(omega) > ModalAnalysis.OscillatoryImagTolerance;
        var cat = new double[Enum.GetValues<FullStateCategory>().Length];
        for (int k = 0; k < part.Length; k++) cat[(int)states[k].Category] += part[k];
        return new FullMode(new Mode(s, zeta, Math.Abs(omega) / (2.0 * Math.PI), osc, part), sigma > options.RhpTolerance, residual,
            residual <= options.ResidualGate, avail, cat);
    }

    internal static double RelativeResidual(IArnoldiOperator op, Complex lam, Complex[] v)
    {
        int n = op.Order;
        var av = new Complex[n];
        op.Multiply(v, av);
        double r = 0.0, vn = 0.0;
        for (int i = 0; i < n; i++)
        {
            Complex d = av[i] - lam * v[i];
            r += d.Real * d.Real + d.Imaginary * d.Imaginary;
            vn += v[i].Real * v[i].Real + v[i].Imaginary * v[i].Imaginary;
        }
        return Math.Sqrt(r) / (Math.Max(op.Norm1, 1e-300) * Math.Max(Math.Sqrt(vn), 1e-300));
    }

    private static Complex[] InverseIterationRight(IArnoldiOperator op, Complex lam, int n, ulong seed)
    {
        Complex shift = Perturb(lam);
        IShiftedSolver solver = op.Factor(shift, NearSingularRatio);
        return Iterate(solver, n, seed, 4);
    }

    private static Complex Perturb(Complex lam) => lam + new Complex(1e-7 * (1.0 + ComplexLinearAlgebra.Abs(lam)), 0.0);

    private static Complex[] Iterate(IShiftedSolver solver, int n, ulong seed, int sweeps)
    {
        var stream = new Stochastic.CounterStream(new Stochastic.StreamKey(seed, "modal-full/inverse-iteration"));
        var x = new Complex[n];
        for (int i = 0; i < n; i++) x[i] = new Complex(stream.Uniform((ulong)(2 * i)) - 0.5, stream.Uniform((ulong)(2 * i + 1)) - 0.5);
        Normalise(x);
        var y = new Complex[n];
        for (int s = 0; s < sweeps; s++)
        {
            solver.Solve(x, y);
            Normalise(y);
            Array.Copy(y, x, n);
        }
        return x;
    }

    private static void Normalise(Complex[] x)
    {
        double s = 0.0;
        foreach (Complex c in x) s += c.Real * c.Real + c.Imaginary * c.Imaginary;
        s = Math.Sqrt(s);
        if (s > 0) for (int i = 0; i < x.Length; i++) x[i] /= s;
    }

    /// <summary>Participation |psi_k||phi_k| normalised to sum 1; false when psi^H phi is not clearly non-zero.</summary>
    private static bool Participation(IArnoldiOperator op, ArnoldiPair p, int n, double[] part) => Participation(op, p.Value, p.Vector, n, part);

    private static bool Participation(IArnoldiOperator op, Complex lam, Complex[] vector, int n, double[] part)
    {
        IShiftedSolver adj;
        try { adj = op.FactorAdjoint(Perturb(lam), NearSingularRatio); }
        catch (SingularShiftException) { return false; }
        Complex[] left = Iterate(adj, n, ArnoldiOptions.DefaultSeed + 1UL, 4);
        Complex inner = Complex.Zero;
        for (int i = 0; i < n; i++) inner += Complex.Conjugate(left[i]) * vector[i];
        if (ComplexLinearAlgebra.Abs(inner) < ParticipationBiorthogonalityFloor) return false;
        double sum = 0.0;
        for (int i = 0; i < n; i++) { part[i] = ComplexLinearAlgebra.Abs(left[i]) * ComplexLinearAlgebra.Abs(vector[i]); sum += part[i]; }
        if (sum > 0) for (int i = 0; i < n; i++) part[i] /= sum;
        return true;
    }

    private static string R(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
