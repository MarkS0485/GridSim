namespace GridSim.Core.Physics;

/// <summary>
/// The single source of truth for which physical laws the audit covers and which of them GATE
/// acceptance of a state. Mirrors the <c>SolverSelector</c> / <c>InertiaModelSelector</c> idiom: a
/// static registry the CLI, the estimator's feasibility gate, and the conformance sweep all route
/// through, so "what makes a state physically impossible" is defined in exactly one place.
///
/// The audit itself is <see cref="PhysicsLaws.Audit"/>, which emits the ten laws inline; this registry names
/// the law set (<see cref="Names"/>, which the audit reads, so a law's label is written once) and the
/// hard-gate subset. R092 (October 2026 review): the doc used to promise an <c>IPhysicsCheck</c> extraction
/// with an <c>All</c> enumeration that was never built, and <see cref="Names"/> was a hand copy that had drifted
/// from the audit ("Ampere's law" vs "Ampere's law (passivity)", "Complex power S=VI*" vs "S = V I*").
/// </summary>
public static class PhysicsCheckRegistry
{
    /// <summary>
    /// The laws whose violation is physically impossible - a state that breaks one is not a real
    /// network state, so the constrained estimator must reject it. Energy conservation (1), Ohm (2),
    /// Joule (3), Kirchhoff's current law (6), complex power S=VI* (8). The remaining laws are
    /// constitutive (4,5), structural (7), a dynamics property (9), or out of model (10) - reported
    /// for completeness but never blocking a steady-state estimate.
    /// </summary>
    public static IReadOnlyCollection<int> HardGateLaws { get; } = new[] { 1, 2, 3, 6, 8 };

    // ---- Experimental laws (numbers 11 and up) -------------------------------------------------------------
    // One number per law, allocated HERE so two audits can never claim the same number again. Before October
    // 2026 (R091) the harmonic KCL check and the small-signal COI null mode both reported law 12, so any
    // report keyed by number collided. None of these is ever a hard-gate law.

    /// <summary>Reciprocity of the passive network admittance, Y = Y^T (<c>ReciprocityCheck</c>).</summary>
    public const int ReciprocityLaw = 11;

    /// <summary>Harmonic KCL, Y(h) V(h) = I(h) at every order (<c>HarmonicKclCheck</c>).</summary>
    public const int HarmonicKclLaw = 12;

    /// <summary>Synchronising-Laplacian row sums vanish (<c>SmallSignalConsistency</c>).</summary>
    public const int LaplacianRowSumLaw = 13;

    /// <summary>Passive grid driving-point impedance is positive-real (<c>ImpedancePassivityCheck</c>).</summary>
    public const int GridPassivityLaw = 14;

    /// <summary>A grid-following converter's non-passive band (<c>ImpedancePassivityCheck</c>).</summary>
    public const int ConverterNonPassiveBandLaw = 15;

    /// <summary>The swing-only A matrix has a centre-of-inertia eigenvalue at s = 0 (<c>SmallSignalConsistency</c>).
    /// Was 12 before 1.95.006 (clashed with harmonic KCL).</summary>
    public const int CoiNullModeLaw = 16;

    /// <summary>Every experimental law number and its label - unique keys by construction.</summary>
    public static IReadOnlyDictionary<int, string> ExperimentalLaws { get; } = new Dictionary<int, string>
    {
        [ReciprocityLaw] = "Reciprocity (experimental)",
        [HarmonicKclLaw] = "Harmonic KCL (experimental)",
        [LaplacianRowSumLaw] = "Synchronising-Laplacian row sum (experimental)",
        [GridPassivityLaw] = "Grid passivity (experimental)",
        [ConverterNonPassiveBandLaw] = "Converter non-passivity band (experimental)",
        [CoiNullModeLaw] = "COI null mode (experimental)",
    };

    /// <summary>Human labels for the ten laws, in number order (index 0 == law 1). <see cref="PhysicsLaws.Audit"/>
    /// labels its results from this table (R092).</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "Conservation of energy", "Ohm's law", "Joule heating", "Faraday induction", "Ampere's law (passivity)",
        "Kirchhoff's current law", "Kirchhoff's voltage law", "Complex power S = V I*",
        "AC phase & frequency synchronism", "Gauss's law / Coulomb",
    };

    /// <summary>The label of headline law <paramref name="number"/> (1..10).</summary>
    public static string Name(int number) => number is >= 1 and <= 10
        ? Names[number - 1]
        : throw new ArgumentOutOfRangeException(nameof(number), number, "headline laws are numbered 1..10");
}
