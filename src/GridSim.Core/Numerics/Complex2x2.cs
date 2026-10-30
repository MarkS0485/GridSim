using System.Numerics;

namespace GridSim.Core.Numerics;

/// <summary>
/// A minimal 2x2 complex matrix - the dq return-ratio / admittance object of the impedance-based stability
/// screen (Workstream D). There is no existing complex-matrix type in the estate (<see cref="SymmetricEigen"/>
/// is real-symmetric only), so this carries exactly the operations the generalised-Nyquist path needs:
/// product, adjugate inverse, and the closed-form 2x2 eigenvalues (the characteristic loci).
/// </summary>
/// <param name="M11">Row 0, col 0.</param>
/// <param name="M12">Row 0, col 1.</param>
/// <param name="M21">Row 1, col 0.</param>
/// <param name="M22">Row 1, col 1.</param>
public readonly record struct Complex2x2(Complex M11, Complex M12, Complex M21, Complex M22)
{
    /// <summary>The 2x2 identity.</summary>
    public static Complex2x2 Identity => new(Complex.One, Complex.Zero, Complex.Zero, Complex.One);

    /// <summary>A scalar multiple of the identity, <c>d*I</c>.</summary>
    public static Complex2x2 Diagonal(Complex d) => new(d, Complex.Zero, Complex.Zero, d);

    /// <summary>A diagonal matrix with the two given entries.</summary>
    public static Complex2x2 Diagonal(Complex d1, Complex d2) => new(d1, Complex.Zero, Complex.Zero, d2);

    /// <summary>Trace <c>M11 + M22</c>.</summary>
    public Complex Trace => M11 + M22;

    /// <summary>Determinant <c>M11*M22 - M12*M21</c>.</summary>
    public Complex Det => (M11 * M22) - (M12 * M21);

    /// <summary>The largest off-diagonal magnitude - a coupling measure (0 = the matrix is diagonal).</summary>
    public double OffDiagonalMagnitude => Math.Max(M12.Magnitude, M21.Magnitude);

    /// <summary>Matrix sum.</summary>
    public Complex2x2 Add(in Complex2x2 b) => new(M11 + b.M11, M12 + b.M12, M21 + b.M21, M22 + b.M22);

    /// <summary>Matrix product <c>this * b</c>.</summary>
    public Complex2x2 Multiply(in Complex2x2 b) => new(
        (M11 * b.M11) + (M12 * b.M21),
        (M11 * b.M12) + (M12 * b.M22),
        (M21 * b.M11) + (M22 * b.M21),
        (M21 * b.M12) + (M22 * b.M22));

    /// <summary>Scale every entry by a complex scalar.</summary>
    public Complex2x2 Scale(Complex s) => new(M11 * s, M12 * s, M21 * s, M22 * s);

    /// <summary>
    /// The inverse by the adjugate over the determinant, or <c>null</c> when the matrix is numerically
    /// singular (<c>|Det|</c> at or below <paramref name="eps"/> relative to the entry scale). Returning a
    /// nullable keeps the caller honest under the 0-warning build: a singular return-ratio is an explicit
    /// error, never a fabricated zero.
    /// </summary>
    public Complex2x2? Inverse(double eps = 1e-15)
    {
        Complex det = Det;
        // Singularity is judged RELATIVE to the entry scale (|Det| ~ scale^2 when well-conditioned).
        // A Math.Max(1.0, ...) floor turned this into an absolute test for sub-unity matrices, so a
        // perfectly-conditioned matrix with tiny entries (e.g. 1e-8*I, |Det|=1e-16) was wrongly
        // declared singular. Use the true max entry magnitude; only a genuine zero matrix has scale 0.
        double scale = Math.Max(Math.Max(M11.Magnitude, M12.Magnitude), Math.Max(M21.Magnitude, M22.Magnitude));
        // GR-450: a NaN entry has no inverse; a NaN scale or determinant fails BOTH comparisons below, so it used to pass and the
        // "inverse" came back full of NaN. Singular (null) as for every matrix that cannot be inverted.
        if (double.IsNaN(scale) || double.IsNaN(det.Real) || double.IsNaN(det.Imaginary) || double.IsInfinity(scale)) return null;
        if (scale <= 0.0 || det.Magnitude <= eps * scale * scale) return null;
        Complex inv = Complex.One / det;
        return new Complex2x2(M22 * inv, -M12 * inv, -M21 * inv, M11 * inv);
    }

    /// <summary>
    /// The two eigenvalues in closed form: <c>lambda = (tr +/- sqrt(tr^2 - 4 det)) / 2</c>. Exact for a 2x2,
    /// so no iterative eigensolver is needed to form the generalised-Nyquist characteristic loci. The pair is
    /// ordered (L1, L2) with the <c>+</c> branch first; frame-to-frame continuity pairing is the caller's job.
    /// GR-449: only the LARGER eigenvalue is taken from the +/- formula (the branch where tr and disc add, not
    /// cancel); the smaller one is det / larger (Vieta: L1 * L2 = det), so it keeps full relative accuracy when
    /// the two eigenvalues differ by many orders of magnitude. The (+, -) branch order is unchanged.
    /// </summary>
    public (Complex L1, Complex L2) Eigenvalues()
    {
        Complex tr = Trace;
        Complex det = Det;
        Complex disc = Complex.Sqrt((tr * tr) - (4.0 * det));
        Complex plus = 0.5 * (tr + disc);
        Complex minus = 0.5 * (tr - disc);
        // Re(conj(tr) * disc) >= 0 means tr and disc point the same way, so the + branch is the one free of
        // cancellation; otherwise the - branch is. A zero larger root means tr = 0 and disc = 0, so both are zero
        // and the direct pair is already exact. A non-finite larger root (NaN or infinite input) also keeps the
        // direct pair, so a NaN still comes back as NaN and is never hidden by the division.
        bool plusIsLarger = ((tr.Real * disc.Real) + (tr.Imaginary * disc.Imaginary)) >= 0.0;
        Complex larger = plusIsLarger ? plus : minus;
        if (larger == Complex.Zero || !double.IsFinite(larger.Real) || !double.IsFinite(larger.Imaginary))
            return (plus, minus);
        Complex smaller = det / larger;
        return plusIsLarger ? (plus, smaller) : (smaller, minus);
    }
}
