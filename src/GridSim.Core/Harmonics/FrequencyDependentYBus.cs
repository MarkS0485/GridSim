using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Harmonics;

/// <summary>
/// The network admittance matrix at a harmonic order h, <c>Y(h)</c> - the physics core of the harmonic engine.
/// It is built from the SAME <see cref="AdmittanceStamp"/> as the fundamental <see cref="YBus"/>, with the
/// frequency dependence applied per element: series reactance and line charging scale with h
/// (<c>X -&gt; hX</c>, <c>B -&gt; hB</c>), series resistance by an optional skin-effect factor, and bus shunts
/// by their nature (a capacitor's susceptance scales with h, a reactor's inversely). Loads are optionally
/// added as frequency-dependent damping shunts.
///
/// <para><b>Grounded, and reduces exactly:</b> at h=1 with the default options (no skin, no load shunts) this
/// is bit-for-bit the fundamental <see cref="YBus.BuildSparse"/>. It emits a <see cref="ComplexCsr"/> for the
/// <see cref="Abstractions.IComplexLinearSolver"/> seam. It degrades gracefully on today's data (lumped
/// pi-model per order); once GDA hands over line length + OHL/cable + zero-sequence fields (Phase 6) the skin
/// and long-line refinements attach here.</para>
/// </summary>
public static class FrequencyDependentYBus
{
    /// <summary>Build <c>Y(h)</c> for the given model and harmonic order (h=1 = fundamental).</summary>
    public static ComplexCsr Build(GridModel model, double h, HarmonicModelOptions? options = null)
    {
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        // Balanced-harmonic mode (R029): the order's symmetrical-component sequence decides the sign of every
        // transformer phase shift, and a zero-sequence (triplen) order cannot be represented at all.
        return BuildCore(model, h, opt, BalancedShiftSign(h, opt));
    }

    /// <summary>
    /// Build the POSITIVE- or NEGATIVE-sequence network at any (possibly fractional) frequency multiple
    /// <paramref name="h"/>: the frequency sweep's operator (R063). A positive-sequence set sees every
    /// transformer phase shift as +theta, a negative-sequence set as -theta, at every frequency. The options'
    /// <see cref="HarmonicModelOptions.BalancedHarmonicSequences"/> flag is not consulted (the sequence is given
    /// explicitly); a zero-sequence request is refused (this model has no zero-sequence network).
    /// </summary>
    public static ComplexCsr BuildSequence(GridModel model, double h, HarmonicSequenceKind sequence, HarmonicModelOptions? options = null)
    {
        if (!(h > 0.0) || !double.IsFinite(h))
            throw new ArgumentException($"Sequence-network Y(h) needs a positive finite frequency multiple; got h = {h:R}.", nameof(h));
        double shiftSign = sequence switch
        {
            HarmonicSequenceKind.Positive => 1.0,
            HarmonicSequenceKind.Negative => -1.0,
            _ => throw new ArgumentException(
                "This positive-sequence model has no zero-sequence network; use the three-phase study.", nameof(sequence)),
        };
        return BuildCore(model, h, options ?? new HarmonicModelOptions(), shiftSign);
    }

    /// <summary>
    /// Build <c>Y</c> at a SIGNED frequency ratio <paramref name="hSigned"/> = f/f0 for the dq impedance path
    /// (<c>GridImpedanceAssembler.DrivingPointAtRatio</c>): the negative arm f_p - f0 of a sub-synchronous perturbation
    /// is a negative frequency, where every reactance and susceptance changes sign so that, for a passive network,
    /// <c>Y(-h) = conj(Y(h))</c>. That sign flip is the physics wanted there, not an error. This entry accepts any finite
    /// non-zero ratio; zero (the DC arm: every machine stamp -1/(0 X) = -Infinity) and non-finite ratios are refused.
    /// Every harmonic study (an order, not a signed frequency) keeps using <see cref="Build"/>, which refuses h &lt;= 0.
    /// (Restored 2026-10-19: 1.95.300 put the h &gt; 0 check inside the shared core, which also refused this path.)
    /// </summary>
    internal static ComplexCsr BuildSignedFrequency(GridModel model, double hSigned, HarmonicModelOptions? options = null)
    {
        if (hSigned == 0.0 || !double.IsFinite(hSigned))
            throw new ArgumentOutOfRangeException(nameof(hSigned), hSigned, "Y(f/f0): the signed frequency ratio must be a non-zero finite number.");
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        return BuildCore(model, hSigned, opt, BalancedShiftSign(hSigned, opt), signedFrequency: true);
    }

    private static ComplexCsr BuildCore(GridModel model, double h, HarmonicModelOptions opt, double shiftSign, bool signedFrequency = false)
    {
        // Every harmonic build, balanced or not: h must be a positive finite multiple of the fundamental. h = 0 made
        // every machine stamp -1/(0 X) = -Infinity, and h < 0 flipped the sign of every reactance. The one exception is
        // the signed-frequency dq path (BuildSignedFrequency, which has already refused 0 and non-finite values), where
        // a negative ratio is a negative frequency and the sign flip is the intended physics.
        if (!signedFrequency && (!(h > 0.0) || !double.IsFinite(h)))
            throw new ArgumentOutOfRangeException(nameof(h), h, "Y(h): the harmonic order must be a positive finite number.");
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        var builder = new ComplexCsr.Builder(n);

        // Branches: the shared pi-model stamp at order h (X->hX, B->hB, R->R*skin), with a per-branch skin
        // factor (cables differ from OHL) and an optional distributed-parameter long-line correction.
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            int f = index[br.From];
            int t = index[br.To];
            double rFactor = opt.SkinFactor(h, br.Equipment);
            // A zero series impedance has no admittance: the lumped stamp divided 1 / 0 and the long-line one stamped
            // an open circuit (keeping the shunt halves), so the two paths disagreed and neither said so. Refused on
            // both, naming the branch (GridModel.Validate refuses such a branch too; this guards a model that was not
            // validated). A zero-impedance tie is modelled by merging its buses or by a small reactance.
            if (br.R * rFactor == 0.0 && br.X == 0.0)
                throw new ArgumentException(
                    $"Harmonic Y(h): branch {br.From}-{br.To}{(br.Name is null ? "" : $" ('{br.Name}')")} has zero series impedance; " +
                    "merge its buses or give it a small reactance.");

            BranchStamp s = opt.LongLineCorrection && br.B != 0.0
                ? LongLineStamp(br, h, rFactor, shiftSign)
                : AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, shiftSign * br.PhaseShiftDeg, h, rFactor);

            builder.Add(f, f, s.Yff.Real, s.Yff.Imaginary);
            builder.Add(t, t, s.Ytt.Real, s.Ytt.Imaginary);
            builder.Add(f, t, s.Yft.Real, s.Yft.Imaginary);
            builder.Add(t, f, s.Ytf.Real, s.Ytf.Imaginary);
        }

        // Bus shunts, frequency-dependent by nature: capacitive B scales with h, inductive B inversely.
        // Gs (a resistive shunt/conductance) is frequency-independent. At h=1 this equals the YBus shunt.
        foreach (Bus b in model.Buses)
        {
            if (b.Gs == 0 && b.Bs == 0) continue;
            int i = index[b.Id];
            double bsH = b.Bs >= 0 ? b.Bs * h : b.Bs / h;
            builder.Add(i, i, b.Gs / model.BaseMva, bsH / model.BaseMva);
        }

        // Optional load-as-shunt damping model at 1 pu voltage. A load's real power is a (frequency-independent)
        // conductance; a lagging (Qd > 0) reactive part is a reactor whose susceptance -Qd falls as 1/h; a
        // leading (Qd < 0) part is a capacitor whose susceptance -Qd grows as h. This is the dominant harmonic
        // damping in a grid shedding synchronous machines, so it is available - but off by default (keeps
        // Y(1) == YBus).
        // R029: a NET-EXPORTING bus (Pd < 0: embedded generation netted into demand) contributes NO conductance
        // - its load is unknown, and Pd/BaseMva would be a negative resistance that can manufacture a
        // resonance. A leading load used to be scaled as 1/h like a reactor.
        if (opt.IncludeLoadShunts)
        {
            foreach (Bus b in model.Buses)
            {
                if (b.Pd == 0 && b.Qd == 0) continue;
                int i = index[b.Id];
                double g = Math.Max(b.Pd, 0.0) / model.BaseMva;
                double bLoad = b.Qd >= 0 ? -(b.Qd / h) / model.BaseMva    // reactor: |B| falls as 1/h
                                         : -(b.Qd * h) / model.BaseMva;   // capacitor: B grows as h
                builder.Add(i, i, g, bLoad);
            }
        }

        // Optional machine source impedance (see HarmonicModelOptions.IncludeGeneratorSources): each physical
        // synchronous machine is a reactance h*X to ground, X = X2 (default X''d) + unit transformer, on the
        // system base. y = 1/(j*h*X) = -j/(h*X): no conductance, susceptance -1/(h*X).
        if (opt.IncludeGeneratorSources)
        {
            foreach (Generator g in model.Generators)
            {
                if (g.PowerFlowOnly) continue;          // a power-flow device (slack balancer etc.): no physical source
                if (g.IsConverterInterfaced) continue;  // a controlled current source: no EMF behind a reactance
                double xSys = GeneratorSourceReactanceSystemPu(g, model.BaseMva, opt.GeneratorFallbackXdPu);
                int i = index[g.Bus];
                builder.Add(i, i, 0.0, -1.0 / (h * xSys));
            }
        }

        // N149 (opt-in, HarmonicModelOptions.ActiveFrontEndReactancePu): every active-front-end converter is resonant plant
        // through its internal impedance (G5 Issue 5 s.3.29 p.17), a shunt of R + j*h*X to ground on the system base.
        // Line-commutated units and every converter when the option is not set are not stamped (unchanged).
        if (opt.ActiveFrontEndReactancePu is double xAfe)
        {
            double rAfe = opt.ActiveFrontEndResistancePu;
            ValidateActiveFrontEndImpedance(rAfe, xAfe);
            foreach (Generator g in model.Generators)
            {
                if (!IsActiveFrontEndPlant(g)) continue;
                (double r, double x) = ActiveFrontEndImpedanceSystemPu(g, model.BaseMva, rAfe, xAfe);
                // y = 1 / (r + j*h*x) = (r - j*h*x) / (r^2 + (h*x)^2)
                double hx = h * x;
                double den = r * r + hx * hx;
                builder.Add(index[g.Bus], index[g.Bus], r / den, -hx / den);
            }
        }

        return builder.Build();
    }

    /// <summary>N149: true for a unit whose harmonic class is active front end and which is physical plant: in service
    /// (the caller passes <see cref="GridModel.Generators"/>, in-service units), converter-interfaced, not
    /// power-flow-only, not line-commutated. Exactly the units <see cref="ConverterHarmonicSources"/> gives the
    /// <see cref="ConverterHarmonicSources.SelfCommutatedProfile"/> profile.</summary>
    public static bool IsActiveFrontEndPlant(Generator g)
        => ConverterHarmonicSources.ProfileFor(g) == ConverterHarmonicSources.SelfCommutatedProfile;

    /// <summary>N149: fail loud on an impedance that is not a physical one (the message names the option).</summary>
    public static void ValidateActiveFrontEndImpedance(double resistancePu, double reactancePu)
    {
        if (!double.IsFinite(reactancePu) || !(reactancePu > 0.0))
            throw new ArgumentException(
                $"Active-front-end converter reactance must be finite and above 0 pu (on the unit's rating); got {reactancePu:R}.", nameof(reactancePu));
        if (!double.IsFinite(resistancePu) || resistancePu < 0.0)
            throw new ArgumentException(
                $"Active-front-end converter resistance must be finite and not negative (pu on the unit's rating); got {resistancePu:R}.", nameof(resistancePu));
    }

    /// <summary>N149: one active-front-end unit's internal impedance converted from its rating (MBase) to the system base,
    /// as the machine source reactance is converted (<see cref="GeneratorSourceReactanceSystemPu"/>). A unit with no
    /// usable rating fails loud naming it (the impedance would be meaningless).</summary>
    public static (double R, double X) ActiveFrontEndImpedanceSystemPu(Generator g, double baseMva, double resistancePu, double reactancePu)
    {
        if (!double.IsFinite(g.MBase) || !(g.MBase > 0.0))
            throw new InvalidOperationException(
                $"Converter '{g.Name ?? "(unnamed)"}' at bus {g.Bus} has rating MBase = {g.MBase:R} MVA; its active-front-end " +
                "impedance is given on its rating, so the rating must be finite and positive.");
        double scale = baseMva / g.MBase;
        return (resistancePu * scale, reactancePu * scale);
    }

    /// <summary>
    /// The sign applied to every transformer phase shift at order <paramref name="h"/>: +1 unless the options
    /// ask for balanced harmonics (<see cref="HarmonicModelOptions.BalancedHarmonicSequences"/>), in which case
    /// a negative-sequence order gets -1 and a zero-sequence (triplen) or non-integer order is refused loudly.
    /// </summary>
    public static double BalancedShiftSign(double h, HarmonicModelOptions opt)
    {
        if (!opt.BalancedHarmonicSequences) return 1.0;
        if (!(h >= 1.0) || h != Math.Floor(h) || h > int.MaxValue)
            throw new ArgumentException(
                $"Balanced-harmonic Y(h) needs a whole harmonic order >= 1; got h = {h:R}. A sequence is defined " +
                "only for integer orders (use the plain positive-sequence builder for a frequency sweep).", nameof(h));
        return HarmonicSequence.Of((int)h) switch
        {
            HarmonicSequenceKind.Positive => 1.0,
            HarmonicSequenceKind.Negative => -1.0, // a negative-sequence set is shifted the opposite way
            _ => throw new ArgumentException(
                $"Order {(int)h} is a triplen (zero sequence). Its network is the zero-sequence network (line Z0, " +
                "transformer winding and earthing), which this positive-sequence model does not have; use the " +
                "three-phase harmonic study.", nameof(h)),
        };
    }

    /// <summary>
    /// A machine's harmonic source reactance on the SYSTEM base (pu): X2 (default X''d) plus its unit
    /// transformer, both on MBase, scaled by BaseMva/MBase - the same conversion the fault studies make. A
    /// machine with no MBase is taken as rated at the system base. Fails loudly on a non-positive or
    /// non-finite reactance (it would be a short circuit to ground, or garbage), naming the unit.
    /// </summary>
    public static double GeneratorSourceReactanceSystemPu(Generator g, double baseMva, double fallbackXdPu)
    {
        double mBase = g.MBase > 0 ? g.MBase : baseMva;
        double xSys = g.FaultSourceNegativeSeqReactancePu(fallbackXdPu) * baseMva / mBase;
        if (!(xSys > 0.0) || !double.IsFinite(xSys))
            throw new InvalidOperationException(
                $"Generator '{g.Name ?? "(unnamed)"}' at bus {g.Bus} has harmonic source reactance {xSys:R} pu " +
                "(system base); it must be positive and finite. Check its X''d/X2, unit transformer and MBase.");
        return xSys;
    }

    /// <summary>
    /// The long-line-corrected pi-model stamp at order h. From the lumped series impedance Z(h) = R*skin + jhX
    /// and the total shunt Y(h) = jhB, the exact distributed-parameter (hyperbolic) equivalent-pi corrects
    /// them: Z' = Z*sinh(gl)/gl and Y'/2 = (Y/2)*tanh(gl/2)/(gl/2), with the propagation product
    /// gl = sqrt(Z(h)*Y(h)) (the line length cancels, so no length data is needed). Reduces to the lumped pi
    /// for a short line (gl -> 0), matters for long cables at high order.
    /// </summary>
    private static BranchStamp LongLineStamp(Branch br, double h, double rFactor, double shiftSign)
    {
        Complex z = new Complex(br.R * rFactor, h * br.X);   // series impedance at order h
        Complex y = new Complex(0, h * br.B);                // total shunt admittance at order h
        Complex gl = Complex.Sqrt(z * y);

        Complex zc, ycHalf;
        if (gl.Magnitude < 1e-9)
        {
            zc = z;
            ycHalf = y / 2.0;
        }
        else
        {
            zc = z * DetMath.Sinh(gl) / gl;
            ycHalf = (y / 2.0) * (DetMath.Tanh(gl / 2.0) / (gl / 2.0));
        }

        // zc is not zero here: a zero series impedance is refused before the stamp (see the caller).
        Complex ys = Complex.One / zc;
        return AdmittanceStamp.FromAdmittance(ys, ycHalf, br.TapRatio, shiftSign * br.PhaseShiftDeg);
    }
}
