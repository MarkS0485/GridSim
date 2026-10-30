namespace GridSim.Core.Harmonics;

/// <summary>
/// Knobs for the frequency-domain harmonic model: which orders to analyse and how the network elements behave
/// with frequency. Defaults keep the model purely network-grounded (branch reactance and line charging scale
/// with order; no skin effect; loads are NOT added as shunts) so that <c>Y(1)</c> reduces exactly to the
/// fundamental <see cref="PowerFlow.YBus"/>. The frequency-dependent load model (a resonance damper) and
/// skin effect are opt-in, honestly-assumed refinements.
/// </summary>
public sealed record HarmonicModelOptions
{
    /// <summary>Default order set: odd harmonics 3..49 (the classic power-electronic spectrum; triplens are
    /// carried and only vanish in a balanced three-phase treatment, Phase 5).</summary>
    public static readonly IReadOnlyList<int> DefaultOrders =
        Enumerable.Range(1, 24).Select(k => 2 * k + 1).ToArray(); // 3,5,7,...,49

    /// <summary>The harmonic orders the penetration / scan drivers sweep. (The <c>Y(h)</c> builder takes one h.)
    /// Validated when set (<see cref="ValidateOrders"/>): at least one order, every order a positive integer, no order
    /// twice. A duplicate used to be counted twice in THD (up to sqrt(2) too high), and h &lt;= 0 built an infinite or
    /// sign-flipped Y(h).</summary>
    public IReadOnlyList<int> Orders
    {
        get => _orders;
        init => _orders = ValidateOrders(value, nameof(Orders));
    }

    private readonly IReadOnlyList<int> _orders = DefaultOrders;

    /// <summary>Refuse an empty, non-positive or duplicated harmonic order list (fail loud, naming the culprit), and
    /// return it unchanged otherwise. Public so every driver that takes its own order list checks it the same way.</summary>
    public static IReadOnlyList<int> ValidateOrders(IReadOnlyList<int>? orders, string what)
    {
        if (orders is null) throw new ArgumentNullException(what);
        if (orders.Count == 0) throw new ArgumentException($"{what}: at least one harmonic order is needed.", what);
        var seen = new HashSet<int>();
        foreach (int h in orders)
        {
            if (h <= 0)
                throw new ArgumentOutOfRangeException(what, h, $"{what}: harmonic order {h} is not a positive integer.");
            if (!seen.Add(h))
                throw new ArgumentException($"{what}: harmonic order {h} is listed twice (it would be counted twice in THD).", what);
        }
        return orders;
    }

    /// <summary>Skin-effect coefficient k in <c>R(h) = R * (1 + k*sqrt(max(h-1,0)))</c> for overhead lines
    /// (and the default for unknown equipment). 0 (default) = no skin (frequency-independent series
    /// resistance).</summary>
    public double SkinCoefficient { get; init; } = 0.0;

    /// <summary>Skin-effect coefficient for CABLES (branch <c>Equipment == "cable"</c>) - cables show stronger
    /// skin/proximity growth than OHL. When 0 (default) cables fall back to <see cref="SkinCoefficient"/>.</summary>
    public double CableSkinCoefficient { get; init; } = 0.0;

    /// <summary>When true, apply the distributed-parameter (hyperbolic) long-line correction per order to each
    /// branch's series impedance and shunt charging (Z' = Z*sinh(gl)/gl, Y'/2 = (Y/2)*tanh(gl/2)/(gl/2), with
    /// gl = sqrt(Z(h)*Y(h))). Grounded and frequency-dependent; identity for short lines. Off by default so
    /// <c>Y(1)</c> stays the lumped-pi fundamental.</summary>
    public bool LongLineCorrection { get; init; } = false;

    /// <summary>When true, each load bus (Pd/Qd) is added as a frequency-dependent shunt admittance -
    /// a parallel R-L model <c>Y_load(h) = (Pd - j*Qd/h) / BaseMva</c> at 1 pu voltage - which damps
    /// resonances (loads are the main harmonic damping the retiring synchronous fleet leaves behind).
    /// Off by default so <c>Y(1)</c> equals the fundamental <see cref="PowerFlow.YBus"/>.</summary>
    public bool IncludeLoadShunts { get; init; } = false;

    /// <summary>
    /// When true, every physical synchronous machine is stamped as a shunt to ground at its harmonic source
    /// reactance: <c>y_g(h) = 1 / (j * h * X)</c>, with <c>X</c> the machine's negative-sequence reactance
    /// (X2, defaulting to X''d - the round-rotor convention) plus its unit transformer, converted from MBase to
    /// the system base. Exactly the reactance the fault studies use
    /// (<see cref="Generator.FaultSourceNegativeSeqReactancePu"/>), so the two models cannot drift apart.
    ///
    /// <para>Why: synchronous machines are the main harmonic path to ground and the main thing that sets
    /// where the network resonates. Without them a radial feeder's Y(h) is singular and a meshed network's
    /// resonances are set by line charging alone (R001, October 2026). Converter-interfaced plant (a
    /// controlled current source with no EMF behind a reactance) and power-flow-only devices are not stamped,
    /// matching the fault studies. No machine resistance is stamped (none is carried in the data), which is
    /// conservative: it makes resonance peaks sharper, never flatter.</para>
    ///
    /// <para>Off by default so that <c>Y(1)</c> with default options stays bit-for-bit the fundamental
    /// <see cref="PowerFlow.YBus"/> (a pinned contract). Production studies use <see cref="Study"/>.</para>
    /// </summary>
    public bool IncludeGeneratorSources { get; init; } = false;

    /// <summary>
    /// When true, <c>Y(h)</c> is the network seen by a BALANCED harmonic of integer order h, which rotates as a
    /// symmetrical-component sequence (<see cref="HarmonicSequence.Of"/>): positive for h mod 3 == 1, negative
    /// for h mod 3 == 2, zero for the triplens (R029, October 2026).
    /// <list type="bullet">
    /// <item>A transformer phase shift theta acts as +theta on a positive-sequence order and -theta on a
    /// negative-sequence order (a Dy winding or quad booster shifts the two sequence sets in opposite
    /// directions, at every frequency). Without this mode every order saw +theta.</item>
    /// <item>A triplen order is zero sequence: its network is the zero-sequence network (line Z0, transformer
    /// winding and earthing), which this positive-sequence model does not have. Building <c>Y(h)</c> for a
    /// triplen therefore throws; penetration and the coupled flow refuse a source that injects one (so a
    /// triplen order carries no current and its voltage is exactly zero); the impedance scan skips triplen
    /// orders. For triplens use the three-phase study (<see cref="ThreePhaseHarmonicPenetration"/>). Without
    /// this mode triplens were carried through every transformer as if positive sequence.</item>
    /// <item>h must be a whole number (a sequence is defined only for integer orders).</item>
    /// </list>
    /// Off by default: the bare builder stays a plain positive-sequence frequency operator (also used, with
    /// signed fractional frequencies, by the impedance-stability assembler, where every signal is positive
    /// sequence by construction). <see cref="Study"/> turns it on.
    /// </summary>
    public bool BalancedHarmonicSequences { get; init; } = false;

    /// <summary>
    /// N149 (CF3, October 2026; Ruling R1): the internal impedance of an ACTIVE-FRONT-END converter, as reactance
    /// (pu on the unit's own rating, <see cref="Model.Generator.MBase"/>, at the fundamental). Null (the default) keeps every
    /// converter out of <c>Y(h)</c>, an ideal current source, exactly as before.
    ///
    /// <para><b>Why.</b> ENA EREC G5 Issue 5 (2020), docs/reference S139, s.3.29 p.17 defines resonant plant as plant
    /// that "may materially modify the background harmonic level as a result of interaction with the public supply system
    /// without emitting any harmonic current or voltage", naming "active-front-end converters/inverters", and its NOTE:
    /// "Active-front-end convertors/ inverters may be classified as resonant plant due to their internal impedance". An
    /// ideal current source has infinite internal impedance, so it can neither shift nor damp a resonance; a converter
    /// with a phase reactor or filter can. With this set, every in-service, non-power-flow-only converter whose harmonic
    /// class is active front end (<see cref="ConverterHarmonicSources.ProfileFor"/> gives
    /// <see cref="ConverterHarmonicSources.SelfCommutatedProfile"/>) is stamped as a shunt to ground of
    /// <c>R + j h X</c> (converted from its rating to the system base): its Norton equivalent, the source current (when
    /// the study makes it a source) in parallel with that impedance. Line-commutated units
    /// (<see cref="ConverterHarmonicSources.LineCommutatedProfile"/>) are not touched, so a study of them gives exactly
    /// the numbers it gave before. The same Norton form is the converter representation of IEC TR 61000-3-6 and of the
    /// harmonic load flows of PowerFactory and PSS/E, where the impedance is the manufacturer's (user-entered) value.</para>
    ///
    /// <para><b>Which value.</b> No document on disk gives a typical active-front-end internal impedance (G5 names the
    /// effect, not a value) [VERIFY]: it is the converter's coupling reactor or filter, from the manufacturer. So there
    /// is no default; a study that wants the effect states the value (CLI <c>harmonics --afe-reactance</c>). Must be
    /// finite and above 0.</para>
    /// </summary>
    public double? ActiveFrontEndReactancePu { get; init; }

    /// <summary>N149: the resistance (pu on the unit's rating, frequency-independent) in series with
    /// <see cref="ActiveFrontEndReactancePu"/>; 0 (the default) is the conservative choice (no damping from the
    /// converter, sharper peaks). Read only when the reactance is set; must be finite and not negative.</summary>
    public double ActiveFrontEndResistancePu { get; init; }

    /// <summary>The X''d (pu on MBase) assumed for a machine that carries none - the same fallback the fault
    /// studies use (<see cref="Protection.FaultAnalysis.DefaultSubtransientXdPu"/>).</summary>
    public double GeneratorFallbackXdPu { get; init; } = Protection.FaultAnalysis.DefaultSubtransientXdPu;

    /// <summary>
    /// The physical harmonic study model that the CLI <c>harmonics</c> verb, the Web HARMONICS tab and the
    /// scheduled analysis job run: the network PLUS machine source impedance
    /// (<see cref="IncludeGeneratorSources"/>) PLUS loads as damping shunts (<see cref="IncludeLoadShunts"/>),
    /// solved as balanced harmonics (<see cref="BalancedHarmonicSequences"/>: sequence-correct transformer
    /// phase shifts, triplen sources refused). Machines ground the network and set the resonances; loads are
    /// the main resistive damping. Default orders, no skin effect, no long-line correction.
    /// </summary>
    public static HarmonicModelOptions Study { get; } = new()
    {
        IncludeGeneratorSources = true,
        IncludeLoadShunts = true,
        BalancedHarmonicSequences = true,
    };

    /// <summary>The series-resistance multiplier at order <paramref name="h"/> from the skin coefficient.</summary>
    public double SkinFactor(double h) => SkinFactor(h, null);

    /// <summary>The series-resistance multiplier at order <paramref name="h"/> for a branch of the given
    /// equipment type ("cable" uses <see cref="CableSkinCoefficient"/> when set, else the OHL coefficient).</summary>
    public double SkinFactor(double h, string? equipment)
    {
        double k = string.Equals(equipment, "cable", StringComparison.OrdinalIgnoreCase) && CableSkinCoefficient > 0
            ? CableSkinCoefficient : SkinCoefficient;
        // Skin effect depends on the magnitude of the frequency, so a negative order (the dq impedance path's mirror
        // frequency) takes the factor at |h| and Y(-h) = conj(Y(h)) holds with skin effect on. For h > 0, Math.Abs is the
        // identity, so every harmonic study is bit-identical to before.
        return 1.0 + k * Math.Sqrt(Math.Max(Math.Abs(h) - 1.0, 0.0));
    }
}
