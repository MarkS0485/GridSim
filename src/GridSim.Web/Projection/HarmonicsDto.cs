using GridSim.Core.Harmonics;
using GridSim.Core.Model;

namespace GridSim.Web.Projection;

// Harmonics DTOs for the HARMONICS tab. Shaped directly against the Core frequency-domain engine
// (GridSim.Core/Harmonics/*):
//   * HarmonicPenetration.Solve      -> HarmonicPenetrationResult (per-order complex bus voltages)
//   * HarmonicIndices.FromPenetration -> HarmonicIndicesResult (per-bus voltage THD)
//   * HarmonicImpedanceScan.DrivingPoint -> ImpedanceScanResult (driving-point Z(h), resonance)
//   * HarmonicCompliance.AssessIeee519  -> IReadOnlyList<BusComplianceResult> (IEEE 519 verdicts)
//     + HarmonicCompliance.G55ThdPlanningLevelPct (G5/5 headline THD level)
// Complex phasors are reduced to magnitude-percent (|V_h|/|V_1|*100) and Z is split mag/real/imag so the
// wire stays double-only. The full study is a per-order linear solve (24 orders) - too heavy to run every
// frame - so the per-frame projection is a GUARDED STUB (HeavyModelsSkipped, mirroring ExperimentalDto). The
// FromCore mapper below is fully wired: a CLI/on-demand caller runs the engine and maps its result straight
// through.

/// <summary>One order's individual-harmonic distortion at a bus: order h and |V_h|/|V_1| as a percent.</summary>
public sealed record HarmonicOrderMagDto(int Order, double MagnitudePct);

/// <summary>One bus's voltage spectrum: its THD and the per-order IHD magnitudes.</summary>
public sealed record HarmonicBusSpectrumDto(int BusId, double BaseKv, double ThdPct, IReadOnlyList<HarmonicOrderMagDto> Orders);

/// <summary>One point on a driving-point impedance scan curve: the order and the complex Z (pu) there.</summary>
public sealed record ImpedanceScanPointDto(int Order, double ZMagnitude, double ZReal, double ZImag);

/// <summary>A driving-point impedance / resonance scan at one bus (from <see cref="ImpedanceScanResult"/>).</summary>
public sealed record ImpedanceScanDto(
    int BusId,
    int ResonanceOrder,
    double PeakMagnitude,
    double DampingSharpness,
    IReadOnlyList<ImpedanceScanPointDto> Points);

/// <summary>System-level harmonic indices: the worst bus and its THD (from <see cref="HarmonicIndicesResult"/>).</summary>
public sealed record HarmonicIndicesDto(int WorstBusId, double WorstThdPct);

/// <summary>One bus's compliance row against IEEE 519 and the G5/5 headline THD planning level
/// (from <see cref="BusComplianceResult"/> + <see cref="HarmonicCompliance.G55ThdPlanningLevelPct"/>).</summary>
public sealed record HarmonicComplianceRowDto(
    int BusId,
    double BaseKv,
    double ThdPct,
    double ThdLimitPct,
    double WorstIhdPct,
    int WorstIhdOrder,
    double IhdLimitPct,
    double G55ThdPlanningPct,
    bool CompliantIeee519,
    bool CompliantG55);

/// <summary>
/// Which harmonic sources the on-demand web study injects (N151). <see cref="Synthetic"/> is the default: one synthetic
/// six-pulse source at the largest-demand bus, with the G5 Issue 5 Table B.3 six-pulse profile since N131 (Ruling R1);
/// <see cref="SyntheticIdeal"/> is the same source with the idealised 1/h spectrum the default used before. The two converter
/// choices are the opt-in per-converter rule of Core (<see cref="ConverterHarmonicSources"/>, N036: G5 Issue 5 Table
/// B.3 profiles, one source per converter-interfaced unit), the same as the CLI's
/// <c>harmonics --converter-sources rated|dispatched</c>.
/// </summary>
public enum HarmonicSourceChoice
{
    /// <summary>One synthetic six-pulse source at the largest-demand bus (the default), with the G5 Issue 5 Table B.3
    /// six-pulse profile (<c>6-pulse-g5</c>) since N131; it was the idealised 1/h spectrum before.</summary>
    Synthetic,
    /// <summary>One G5 source per converter-interfaced unit, scaled by its RATED current (CLI <c>--converter-sources rated</c>).</summary>
    ConverterRated,
    /// <summary>One G5 source per converter-interfaced unit, scaled by its DISPATCHED current (CLI <c>--converter-sources dispatched</c>).</summary>
    ConverterDispatched,
    /// <summary>N131: the synthetic source with the idealised 1/h six-pulse spectrum (<c>ideal</c>), the default before
    /// N131 (CLI <c>harmonics --synth-spectrum ideal</c>). Appended last so the earlier values keep their numbers.</summary>
    SyntheticIdeal,
}

/// <summary>
/// What the per-converter source rule built and what it left out (N151, from Core's
/// <see cref="ConverterHarmonicSourceSet"/>), so the tab can say it as the CLI does: nothing is dropped without a line.
/// </summary>
/// <param name="Level">The fundamental-current level, "rated" or "dispatched" (the CLI's word).</param>
/// <param name="SelfCommutated">Units given the active-front-end profile (<see cref="ConverterHarmonicSources.SelfCommutatedProfile"/>).</param>
/// <param name="LineCommutated">Units given the twelve-pulse profile (<see cref="ConverterHarmonicSources.LineCommutatedProfile"/>).</param>
/// <param name="OmittedComponents">Each profile component left out (the triplens the balanced study cannot carry), named.</param>
/// <param name="UnitsNotModelled">Each converter unit that became no source, named with the reason.</param>
public sealed record HarmonicConverterSourcesDto(
    string Level,
    int SelfCommutated,
    int LineCommutated,
    IReadOnlyList<string> OmittedComponents,
    IReadOnlyList<string> UnitsNotModelled);

/// <summary>
/// The HARMONICS tab rollup. <see cref="HeavyModelsSkipped"/> is the guard: true means the per-frame path did
/// not run the study (the default per-frame state) and the collections are empty. <see cref="ProvenanceTag"/>
/// carries the source-spectra provenance (assumed-typical placeholder vs recorded) so the tab can badge it.
/// <see cref="Error"/> is non-null when the study was attempted and FAILED (for example
/// <c>SingularHarmonicNetworkException</c>: part of the network has no path to ground at some harmonic order).
/// A failed study is never a "skip": the tab must show it as a failure with the engine's message.
/// <see cref="SourceNote"/> says in plain English which harmonic sources the study actually injected (R099): the
/// web study uses ONE synthetic source, not one per converter, and the tab must say so rather than imply a fleet
/// study. Null on a skipped or failed rollup (no sources were injected).
/// <see cref="ComponentsOutsideOrders"/> (N151, from N075's <see cref="HarmonicPenetrationResult.ComponentsOutsideOrders"/>)
/// lists every source component at an order the study did not analyse, so it contributed nothing; empty for the
/// built-in source choices today (their orders are all analysed), shown as a warning when not.
/// <see cref="ConverterSources"/> is non-null only for a per-converter study (<see cref="HarmonicSourceChoice"/>).
/// </summary>
public sealed record HarmonicsDto(
    bool HeavyModelsSkipped,
    string ProvenanceTag,
    IReadOnlyList<int> Orders,
    IReadOnlyList<HarmonicBusSpectrumDto> BusSpectra,
    HarmonicIndicesDto Indices,
    ImpedanceScanDto? Scan,
    IReadOnlyList<HarmonicComplianceRowDto> Compliance,
    string? Error = null,
    string? SourceNote = null,
    IReadOnlyList<string>? ComponentsOutsideOrders = null,
    HarmonicConverterSourcesDto? ConverterSources = null)
{
    /// <summary>
    /// The standing limitation of the DEFAULT web study, shown on the tab and appended to <see cref="SourceNote"/>
    /// (R099). The default study places no source per converter. Since N036 Core has the per-converter rule
    /// (<see cref="ConverterHarmonicSources"/>), and since N151 the tab offers it as an opt-in source choice; the old
    /// text ("Core has no converter-to-spectrum rule yet") stopped being true then and was reworded.
    /// </summary>
    public const string PerConverterLimitation =
        "Per-converter sources are NOT modelled in this default study: it does not place a source at each " +
        "converter-interfaced generator, so the THD shown is the response to this one synthetic source, not a fleet " +
        "estimate. Choose a per-converter source option (G5 Issue 5 Table B.3, opt-in) for a fleet study.";

    /// <summary>The plain-English source note of a per-converter study (N151): how many sources of which profile, at
    /// which level, how they add, and the standing limitation that converter plant is an ideal current source in Y(h)
    /// (no converter impedance is stamped; G5 Issue 5 s.3.29 notes active-front-end converters may be resonant plant).</summary>
    public static string ConverterSourceNote(HarmonicConverterSourcesDto c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{c.SelfCommutated + c.LineCommutated} per-converter source(s): {c.SelfCommutated} self-commutated " +
            $"({ConverterHarmonicSources.SelfCommutatedProfile}), {c.LineCommutated} line-commutated " +
            $"({ConverterHarmonicSources.LineCommutatedProfile}), level {c.Level} (G5 Issue 5 Table B.3, linear addition). " +
            $"Each converter is an ideal current source: no converter impedance is modelled in Y(h).");
    }

    /// <summary>The plain-English source note for the on-demand study: one synthetic source of the given library
    /// type and fundamental current at the given bus, followed by <see cref="PerConverterLimitation"/>.</summary>
    public static string SyntheticSourceNote(string sourceType, double fundamentalCurrentPu, int busId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"One synthetic {sourceType} current source, {fundamentalCurrentPu:0.###} pu fundamental, at bus {busId} " +
            $"(the largest-demand bus). {PerConverterLimitation}");

    /// <summary>The empty/skipped rollup - what the per-frame projection returns (the study is a CLI/on-demand
    /// capability, not a live-frame quantity).</summary>
    public static HarmonicsDto Skipped() => new(
        true, "n/a", Array.Empty<int>(), Array.Empty<HarmonicBusSpectrumDto>(),
        new HarmonicIndicesDto(0, 0), null, Array.Empty<HarmonicComplianceRowDto>());

    /// <summary>True when the study was attempted and failed; <see cref="Error"/> says why.</summary>
    public bool Failed => Error is not null;

    /// <summary>
    /// A study that was attempted and failed. The collections are empty (there is no result to show) and
    /// <see cref="Error"/> carries the reason in plain English, which the tab displays as a failure.
    /// </summary>
    public static HarmonicsDto Failure(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return Skipped() with { ProvenanceTag = "study failed", Error = error };
    }
}

/// <summary>Maps the Core harmonic-engine results into <see cref="HarmonicsDto"/>. Fully wired - the only
/// missing input is the source spectra + which bus to scan, which the caller (CLI/tab) chooses.</summary>
public static class HarmonicsProjection
{
    /// <summary>
    /// Per-frame projection: always the guarded skipped rollup. The penetration study is a per-order complex solve
    /// over the whole network (default 24 orders), far too heavy to run on every tick, so the live study runs on
    /// demand instead (<c>SimSession.RunHarmonicsStudy</c>, cached per model and gated by bus count), which maps
    /// its result through <see cref="FromCore"/>.
    /// <para>Known limitation (R099), stated rather than hidden: neither this projection nor the on-demand study
    /// builds per-converter harmonic sources. The on-demand study injects ONE synthetic six-pulse source at the
    /// largest-demand bus and says so in <see cref="HarmonicsDto.SourceNote"/>. Building a source per
    /// converter-interfaced generator needs a Core rule (which library spectrum and which fundamental current
    /// for which converter class) that does not exist yet; inventing that rule here would be modelling in the
    /// presentation layer.</para>
    /// </summary>
    public static HarmonicsDto Project(GridModel model, GridSim.Core.PowerFlow.PowerFlowResult result)
    {
        _ = model; _ = result;   // intentionally unused: the per-frame rollup is always the skipped stub (see above)
        return HarmonicsDto.Skipped();
    }

    /// <summary>
    /// Map an already-computed penetration study (+ optional impedance scan) into the tab DTO. Reduces the
    /// complex phasors to magnitude-percent and splits Z into mag/real/imag. <paramref name="fundamentalVm"/>
    /// (per bus, aligned with the penetration result's <c>BusIds</c>) defaults to 1.0 pu when null.
    /// </summary>
    public static HarmonicsDto FromCore(
        GridModel model,
        HarmonicPenetrationResult pen,
        string provenanceTag,
        double[]? fundamentalVm = null,
        ImpedanceScanResult? scan = null)
    {
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen, fundamentalVm);
        var kvById = new Dictionary<int, double>(model.Buses.Count);
        foreach (Bus b in model.Buses) kvById[b.Id] = b.BaseKv;

        // Per-bus spectrum: IHD (%) per order, plus the bus THD from the indices result.
        var spectra = new List<HarmonicBusSpectrumDto>(pen.BusIds.Count);
        for (int bi = 0; bi < pen.BusIds.Count; bi++)
        {
            int busId = pen.BusIds[bi];
            double v1 = fundamentalVm is not null && bi < fundamentalVm.Length && fundamentalVm[bi] > 0 ? fundamentalVm[bi] : 1.0;
            var orders = new List<HarmonicOrderMagDto>(pen.Orders.Count);
            foreach (int h in pen.Orders)
            {
                if (h == 1) continue;   // fundamental carries no harmonic injection
                orders.Add(new HarmonicOrderMagDto(h, HarmonicIndices.IndividualDistortionPct(pen, bi, h, v1)));
            }
            spectra.Add(new HarmonicBusSpectrumDto(busId, kvById.GetValueOrDefault(busId), idx.ThdVoltagePct[bi], orders));
        }

        // IEEE 519 compliance rows, augmented with the G5/5 headline THD planning level.
        var compliance = new List<HarmonicComplianceRowDto>();
        foreach (BusComplianceResult c in HarmonicCompliance.AssessIeee519(model, pen, fundamentalVm))
        {
            // Core owns the verdict, including the unknown-voltage-level rule (R265).
            (double g55, bool g55Ok) = HarmonicCompliance.G55Verdict(c);
            compliance.Add(new HarmonicComplianceRowDto(
                c.BusId, c.BaseKv, c.ThdPct, c.ThdLimitPct, c.WorstIhdPct, c.WorstIhdOrder, c.IhdLimitPct,
                g55, c.Compliant, g55Ok));
        }

        ImpedanceScanDto? scanDto = null;
        if (scan is not null)
        {
            var pts = new List<ImpedanceScanPointDto>(scan.Orders.Count);
            for (int i = 0; i < scan.Orders.Count && i < scan.Z.Length; i++)
            {
                System.Numerics.Complex z = scan.Z[i];
                pts.Add(new ImpedanceScanPointDto(scan.Orders[i], z.Magnitude, z.Real, z.Imaginary));
            }
            scanDto = new ImpedanceScanDto(
                scan.BusId, scan.ResonanceOrder, scan.PeakMagnitude,
                HarmonicIndices.DampingSharpness(scan), pts);
        }

        return new HarmonicsDto(
            false, provenanceTag, pen.Orders, spectra,
            new HarmonicIndicesDto(idx.WorstBusId, idx.WorstThdPct), scanDto, compliance);
    }
}
