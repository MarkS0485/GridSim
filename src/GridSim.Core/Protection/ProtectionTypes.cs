namespace GridSim.Core.Protection;

/// <summary>Where a relay verdict sits in the network.</summary>
public enum ProtectionLocation { Bus, Branch, Generator, System }

/// <summary>
/// How much weight a verdict's SETTING carries. The operating quantities are always computed from the
/// solved state; this grades the pickup it is compared against. Real relay settings are unobtainable, so
/// the honest default is <see cref="Generic"/> - a standard-practice value - with the grounded frequency/
/// RoCoF limits marked <see cref="Grounded"/> and the elements a positive-sequence model cannot evaluate
/// marked <see cref="NotCheckable"/> rather than given a fabricated verdict.
/// </summary>
public enum ProtectionConfidence
{
    /// <summary>The pickup is a measured/statutory GB value (frequency band, modern RoCoF limit).</summary>
    Grounded,
    /// <summary>The pickup is a standard-practice generic value (a real scheme would be graded to the site).</summary>
    Generic,
    /// <summary>The element needs data the positive-sequence phasor model does not carry (see the basis).</summary>
    NotCheckable,
}

/// <summary>
/// One relay element's verdict on one location, over the solved state. <see cref="Operate"/> is the
/// headline "would this relay pick up"; <see cref="Basis"/> is the plain-language reason ("Vm 0.74 pu below
/// the 0.80 pu undervoltage pickup"). <see cref="Margin"/> is the signed distance past pickup in the
/// quantity's own units (positive once operated). <see cref="TimeToTripS"/> is the inverse-time operating
/// time where the element has a curve (null for definite-time / not-applicable).
/// </summary>
public sealed record RelayVerdict(
    string Device,                      // ANSI device number(s), e.g. "27", "51", "21-Z1"
    string Function,                    // human name, e.g. "Undervoltage"
    ProtectionLocation LocationKind,
    int LocationId,                     // bus id, or branch index, or generator index (see LocationKind)
    string LocationName,
    bool Operate,
    double Quantity,                    // the operating quantity
    string Unit,
    double Pickup,                      // the setting the quantity is compared against
    double Margin,                      // signed distance past pickup (>0 = operated)
    double? TimeToTripS,                // inverse-time operating time, else null
    string Basis,                       // the "would fire because ..." sentence
    ProtectionConfidence Confidence);

/// <summary>
/// A summary of a dynamic run, supplied when one is available so the frequency-derivative elements
/// (RoCoF, loss-of-mains vector shift, over/under-frequency excursion) have something to evaluate. When
/// null, those elements report <see cref="ProtectionConfidence.NotCheckable"/> against a steady state
/// rather than inventing a transient.
/// </summary>
public sealed record ProtectionTrajectory(
    double MaxAbsRocofHzPerS,
    double MinFrequencyHz,
    double MaxFrequencyHz,
    double MaxAbsVectorShiftDeg = 0.0)
{
    /// <summary>
    /// D-DOC-220 (October 2026): the trajectory of a centre-of-inertia frequency run (<see cref="Dynamics.FrequencyResponse"/>:
    /// the `trip` study and the asset-aware frequency study). Nothing in GridSim built a trajectory before, so 81R
    /// (RoCoF) and 78 (vector shift) were never evaluable on any run. The peak |df/dt| is the response's
    /// <see cref="Dynamics.FrequencyResponse.InitialRocofHzPerS"/> (the largest magnitude over the run, as `trip` prints
    /// it), the minimum frequency its nadir, the maximum the largest sample (at least the nadir, so a run with no points
    /// still forms a valid trajectory). A COI run has no bus voltage angle, so <see cref="MaxAbsVectorShiftDeg"/> is NaN
    /// and the 78 element reports itself not checkable rather than reading 0 degrees.
    /// </summary>
    public static ProtectionTrajectory FromFrequencyResponse(Dynamics.FrequencyResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        double max = response.NadirHz;
        foreach (Frequency.FrequencyPoint p in response.Points) if (p.Hz > max) max = p.Hz;
        return new ProtectionTrajectory(Math.Abs(response.InitialRocofHzPerS), response.NadirHz, max, double.NaN);
    }
}

/// <summary>
/// Generic, settable relay pickups. Frequency and RoCoF limits are the grounded GB statutory / modern-
/// ALoMCP values; the 27 / 59 voltage pickups are the G99 Issue 2 Table 10.1 interface-protection settings (S3,
/// October 2026), still graded Generic because a real transmission bus has no such relay; everything else is
/// standard-practice generic (a real scheme is graded per site and is unobtainable). Overrides let a caller sweep a stricter setting to ask "what would a tighter relay do".
/// </summary>
public sealed record ProtectionSettings
{
    // ---- G01 grounding (S3, October 2026) of the voltage, frequency and loss-of-mains elements ----
    // Source: ENA Engineering Recommendation G99, Issue 2 (10 March 2025), docs/reference id S124, section 10.6.7.1,
    // Table 10.1 "Settings for Long-Term Parallel Operation", page 125 (the interface protection every distribution-
    // connected Power Generating Module is set to). A steady-state solved state is a SUSTAINED condition, so each element
    // here takes the stage of Table 10.1 that a sustained excursion trips first (the lowest pickup, whatever its delay).
    // The settings are applied at every bus in per unit of that bus's nominal voltage, so the HV column is used.

    // Voltage (27/59), per-unit of nominal.
    /// <summary>27: G99 Issue 2 Table 10.1 U/V, HV (and LV) protection, V - 20% = 0.80 pu with a 2.5 s delay
    /// (S124 p.125). Value unchanged by the grounding.</summary>
    public double UnderVoltagePu { get; init; } = 0.80;
    /// <summary>59: G99 Issue 2 Table 10.1 O/V stage 1, HV protection, V + 10% = 1.10 pu with a 1.0 s delay (S124
    /// p.125; stage 2 is V + 13% at 0.5 s, so a sustained 1.10-1.13 pu trips on stage 1). Moved 1.20 -> 1.10 in
    /// October 2026 (S3 G01): 1.20 pu was unsourced and is in no row of Table 10.1 (the nearest, LV stage 2, is
    /// V + 19% = 1.19 pu, and the bus elements here are HV).</summary>
    public double OverVoltagePu { get; init; } = 1.10;

    // Frequency (81U/81O), Hz - GB statutory protection band.
    /// <summary>81U: G99 Issue 2 Table 10.1 U/F stage 1, 47.5 Hz with a 20 s delay (S124 p.125; stage 2 is 47.0 Hz
    /// at 0.5 s). Value unchanged by the grounding.</summary>
    public double UnderFrequencyHz { get; init; } = 47.5;
    /// <summary>81O: G99 Issue 2 Table 10.1 O/F, 52.0 Hz with a 0.5 s delay (S124 p.125). Value unchanged.</summary>
    public double OverFrequencyHz { get; init; } = 52.0;

    /// <summary>
    /// The nominal frequency (Hz) the absolute 81U / 81O pickups above are written for. D2 nominal frequency (October 2026,
    /// with N017; Ruling R1: Mark delegated EE calls 2026-10-04): G99 is the GB code, so its 47.5 / 52.0 Hz are settings for
    /// a 50 Hz system (<see cref="Dynamics.FrequencyLimits.NominalHz"/>, the default). Applied to a 60 Hz system (the WSCC
    /// 9-bus is one) they are meaningless: its healthy 60 Hz steady state would read as "above the 52.0 Hz overfrequency
    /// pickup" and operate 81O. A frequency element whose system nominal differs from this value is therefore reported
    /// not-checkable, naming both frequencies, instead of a fabricated trip; settings written for that system (the CLI's
    /// <c>protection-check --uf</c> and <c>--of</c> together) declare its nominal here. No GridSim value is invented for a
    /// 60 Hz grid code (NERC PRC-024 and IEEE 1547 differ by region and plant type).
    /// </summary>
    public double SettingsNominalHz { get; init; } = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz;

    // RoCoF / loss-of-mains (81R / 78).
    /// <summary>81R: G99 Issue 2 Table 10.1 LoM (RoCoF), 1 Hz/s with a 0.5 s time delay, Types A to C (S124 p.125;
    /// Type D is "intertripping expected"); the same 1.0 Hz/s, 500 ms setting G59/3-7 onwards applies retrospectively
    /// (EREC G59 Issue 3 Amendment 8, S135, clause 2.10, p.8). Value unchanged by the grounding.</summary>
    public double RocofTripHzPerS { get; init; } = 1.0;
    /// <summary>78: vector shift is NOT an acceptable loss-of-mains technique under G99 Issue 2 (S124 clause
    /// 10.4.11, p.120) and was removed from every G59 plant from 01/09/22 (S135, "Historic Vector Shift Settings",
    /// p.63). The legacy setting there is K1 x 6 degrees with K1 = 1.0 for low-impedance networks and 1.66 - 2.0
    /// for high-impedance networks (S135 p.63, note (2)), so 12 degrees is the legacy high-impedance upper end
    /// (K1 = 2.0). Kept at 12.0 (value unchanged): the element is a legacy screen, graded Generic.</summary>
    public double VectorShiftTripDeg { get; init; } = 12.0;

    // Overcurrent (50 instantaneous, 51 inverse-time), as multiples of rated current.
    public double InstantaneousOcMultiple { get; init; } = 2.50;   // 50: |I| above this * rated
    public double TimeOcMultiple { get; init; } = 1.20;            // 51: pickup, IEC standard-inverse curve
    public double TimeOcTms { get; init; } = 0.10;                 // 51 time multiplier setting

    // Undercurrent (37): an ABSOLUTE current floor, not a fraction of rating - it detects a de-energized /
    // open-ended circuit still marked in service, NOT a normally lightly-loaded line (which carries a small
    // but real current). Default ~0.001 pu (~0.1 MVA on a 100 MVA base) = effectively dead.
    public double UnderCurrentFloorPu { get; init; } = 0.001;

    // Thermal overload (49), as a loading fraction of RateA, with a first-order thermal replica.
    public double ThermalPickupFraction { get; init; } = 1.00;
    public double ThermalTimeConstantS { get; init; } = 900.0;

    // Distance / impedance (21), zone reaches as a fraction of the line's own series impedance.
    public double DistanceZone1Reach { get; init; } = 0.80;
    public double DistanceZone2Reach { get; init; } = 1.25;
    public double DistanceZone3Reach { get; init; } = 2.00;
    // Distance zone operating times (s), settable like the reaches (R193; were literals in the relay logic,
    // values unchanged): zone 1 instantaneous, zone 2 and zone 3 time-graded.
    public double DistanceZone1TimeS { get; init; } = 0.0;
    public double DistanceZone2TimeS { get; init; } = 0.3;
    public double DistanceZone3TimeS { get; init; } = 0.8;
    public double DistanceAngleWindowDeg { get; init; } = 75.0;    // forward-fault sector half-width

    // Directional / reverse power (32), MW into the monitored element below -this = reverse.
    public double ReversePowerMw { get; init; } = 5.0;

    // Differential (87) busbar bias: operate current pickup (pu) over the KCL residual.
    public double DifferentialPickupPu { get; init; } = 0.10;

    // F21 (October 2026): earth-fault (50N / 51N), earth-fault distance (21N) and negative-sequence (46) elements, read
    // only when an unbalanced fault study supplies sequence currents. Generic standard-practice values, as the other
    // current elements: [VERIFY] each against a cited setting policy (docs/reference protection-dno / protection-tx;
    // the S3 grounding stream, G01). Multiples are of the branch's rated current (RateA at 1 pu voltage).
    // S3 G01 (October 2026) searched every protection document on disk and found no setting POLICY that states these
    // as multiples of a circuit's rated current, so the values and their [VERIFY] markers stay:
    //  - National Grid PS(T)010 (RES) Issue 1, July 2017 (S158, Appendix A.11, pp.15-17) sets earth-fault back-up in
    //    absolute primary amps for named positions (SGT LV earth fault 480 A; 132 kV bus section 600 A, TM 0.5;
    //    66 kV bus section 400 A, TM 0.6), not as a multiple of a line rating.
    //  - Siemens SIPROTEC 4 7SJ80 manual V4.7 (S318) gives the relay's FACTORY defaults in CT-secondary amps for a 1 A
    //    CT: 50N-1 0.20 A / 0.50 s, 50N-2 0.50 A / 0.10 s, 51N 0.20 A (IEC time dial 0.20 s) (address 1302-1311,
    //    PDF p.78), 46-TOC 0.90 A (address 4008, PDF p.137). A factory default is not a setting policy and a CT
    //    nominal is not a circuit rating, so they are recorded, not adopted (51N 0.20 x rated agrees by coincidence).
    //  - The documents that would settle them are blocked downloads in docs/reference/README.md: NGED ST:TP21AE earth
    //    fault clearance times (S181) and the SPEN 33 kV protection specifications PROT-03-019 / PROT-03-020
    //    (S173, S174), or any DNO setting policy that states the residual and negative-sequence pickups.
    /// <summary>50N: residual |3 I0| above this multiple of rated current trips instantly. [VERIFY]</summary>
    public double EarthFaultInstantaneousMultiple { get; init; } = 1.00;
    /// <summary>51N: residual pickup, multiple of rated current, on the IEC standard-inverse curve. [VERIFY]</summary>
    public double EarthFaultTimeMultiple { get; init; } = 0.20;
    /// <summary>51N time multiplier setting. [VERIFY]</summary>
    public double EarthFaultTms { get; init; } = 0.10;
    /// <summary>46: negative-sequence |I2| above this multiple of rated current. [VERIFY]</summary>
    public double NegativeSequenceMultiple { get; init; } = 0.20;
    /// <summary>46 definite operating time, s. [VERIFY]</summary>
    public double NegativeSequenceTimeS { get; init; } = 1.0;

    /// <summary>
    /// 21N residual-current release (D-DOC-088, October 2026): an earth-fault distance loop at a branch end is measured
    /// only when the residual current there, |3 I0|, exceeds this fraction of the largest phase current at that end.
    /// This is the earth-fault detection ("3I0 release" or "residual supervision") that every numerical distance relay
    /// puts in front of its phase-to-earth loops: without it the loop Z = V_p / (I_p + k0 3 I0) of a healthy-to-earth
    /// fault reduces to V_p / I_p and a line-to-line fault (I0 = 0) reads as an earth fault on the faulted phases. Before
    /// this setting existed the 21N element operated on a line-to-line fault ("phase-b earth-loop" with I0 = 0).
    /// 0.10 (10 % of the largest phase current) is a generic stabilised-release value, graded Generic with the other
    /// earth-fault pickups. Source (confirmed 2026-10-11, N236): Siemens SIPROTEC 7SA522 V4.3 manual
    /// (C53000-G1176-C155-3), section 2.2.1.3, address 1207A "3I0>/ Iphmax", range 0.05 to 0.30, "The preset value
    /// 3I0>/Iphmax = 0.10 (Address 1207A) usually is recommended for the slope of the 3I0 characteristic". That relay
    /// also has an absolute floor (address 1203 "3I0> Threshold", default 0.10 A on a 1 A relay); GridSim models the
    /// slope only, with no absolute floor beyond its numerical guard. The protection-dno / protection-tx documents in
    /// docs/reference set no 21N release.
    /// </summary>
    public double EarthDistanceResidualReleaseFraction { get; init; } = 0.10;

    /// <summary>
    /// OPT-IN site-specific overrides, keyed by asset (branch) name. When a branch's name is present, the
    /// cascade-driving branch relays (49 thermal, 50/51 overcurrent, 21 distance) read THAT record's settings
    /// instead of these generics - the "generic where we have nothing better, site-specific where we have the
    /// data" layer. Null (the default) means every asset uses the generic settings, so existing behaviour is
    /// exactly unchanged. Each override is a full <see cref="ProtectionSettings"/>; author it from the base
    /// with <c>base with { TimeOcTms = 0.05 }</c> and leave its own <see cref="PerAsset"/> null.
    /// </summary>
    public IReadOnlyDictionary<string, ProtectionSettings>? PerAsset { get; init; } = null;
}

/// <summary>The full protection assessment over one solved state: every relay verdict, and the subset that
/// would operate. Never mutates the model or result - it is a read-only overlay.</summary>
public sealed record ProtectionReport(
    IReadOnlyList<RelayVerdict> Verdicts,
    ProtectionSettings Settings,
    bool HadTrajectory)
{
    /// <summary>
    /// The elements that would pick up, FASTEST first (D-DOC-158, October 2026):
    /// <list type="number">
    /// <item>elements with an operating time (<see cref="RelayVerdict.TimeToTripS"/>; instantaneous = 0) in ascending
    /// time, equal times by their ROW INDEX (their position in <see cref="Verdicts"/>: the relay registry order, then
    /// each relay's own bus / branch / generator order) (N270, October 2026),</item>
    /// <item>then elements without one (voltage, frequency, reverse power, undercurrent), each by its margin RELATIVE
    /// to its own pickup (<see cref="RelativeMargin"/>), largest first;</item>
    /// <item>remaining ties keep the order the relays produced them in (a stable sort, so again the row index).</item>
    /// </list>
    /// N270 (October 2026): equal operating times used to be ordered by the raw margin, largest first. Elements with
    /// the same definite time often have the same margin in exact arithmetic (a far-end distance relay of a bolted
    /// fault sees apparent Z = line Z exactly, so (zone-3 reach x Zline - Zapp) / Zline is the same number for every
    /// such branch), so their order was decided by the last bit of each margin and moved when the fault engine's
    /// rounding moved (N233: the fault-case14 21-Z2 rows). Two elements with the same operating time operate at the
    /// same instant, so the margin carried no physical order between them anyway; the row index makes the order part
    /// of the contract, independent of rounding. Elements whose times differ are ordered exactly as before.
    /// It used to sort every element by its raw <see cref="RelayVerdict.Margin"/>, whose unit is the element's own (MW
    /// for 32, pu for 27, multiples of rated current for 50 / 51, ...), so a reverse-power margin of hundreds of MW
    /// headed any fault report (the `fault uk --bus 1` "primary clearance" named Moyle reverse power). Within one device
    /// with one setting the new order is the old one (a larger margin over the same pickup is a faster time for every
    /// inverse or definite curve here), so a caller picking "the first 51" sees the same branch.
    /// </summary>
    public IReadOnlyList<RelayVerdict> Operating =>
        Verdicts.Select((v, row) => (Verdict: v, Row: row))
            .Where(x => x.Verdict.Operate)
            // Timed elements first, then the untimed ones.
            .OrderBy(x => x.Verdict.TimeToTripS is null ? 1 : 0)
            // Timed: fastest first (untimed all compare equal here).
            .ThenBy(x => x.Verdict.TimeToTripS ?? 0.0)
            // Untimed: largest margin relative to the pickup first. Timed elements get a constant key here, so an
            // equal operating time falls straight through to the row index below (N270).
            .ThenByDescending(x => x.Verdict.TimeToTripS is null ? RelativeMargin(x.Verdict) : 0.0)
            // N270: the explicit, rounding-free tie-break. (OrderBy is stable, so this only states what the stable
            // sort already gives; it is written out so the contract does not rest on a library property.)
            .ThenBy(x => x.Row)
            .Select(x => x.Verdict)
            .ToList();

    /// <summary>D-DOC-158: a verdict's margin as a fraction of its own pickup (unitless, so elements in different units
    /// compare), or the raw margin when the pickup is zero or not finite.</summary>
    public static double RelativeMargin(RelayVerdict v)
    {
        ArgumentNullException.ThrowIfNull(v);
        double p = Math.Abs(v.Pickup);
        return double.IsFinite(p) && p > 0.0 ? v.Margin / p : v.Margin;
    }

    /// <summary>
    /// D-DOC-158 (October 2026): the element that clears a fault first. An operating busbar differential (87) is the
    /// unit protection of the faulted busbar, instantaneous, so it is the primary clearance whenever it operates (the
    /// fault studies put the fault current inside the faulted bus's zone, so it operates for every bolted bus fault);
    /// otherwise it is the fastest operating BRANCH element that has an operating time (49, 50, 51, 21 zones, 50N,
    /// 51N, 21N zones, 46); null when neither operates. The `fault` summary used to call the first row of the
    /// margin-sorted list "primary clearance", which on a GB case was a generator reverse-power element (no circuit is
    /// opened by it).
    /// </summary>
    public RelayVerdict? PrimaryClearance =>
        Operating.FirstOrDefault(v => v.Device == "87")
        ?? Operating.FirstOrDefault(v => v.LocationKind == ProtectionLocation.Branch && v.TimeToTripS is not null);

    /// <summary>Any relay would operate on this state.</summary>
    public bool AnyOperate => Verdicts.Any(v => v.Operate);

    /// <summary>Count of operating elements per device function.</summary>
    public IReadOnlyDictionary<string, int> OperatingByFunction =>
        Verdicts.Where(v => v.Operate).GroupBy(v => v.Function).ToDictionary(g => g.Key, g => g.Count());
}
