using GridSim.Core.Diagnostics;

namespace GridSim.Core.Assets;

/// <summary>The provenance of one authored parameter: where the number came from and a human note.</summary>
public sealed record AssetClassProvenance(ProvenanceTag Tag, string Note);

/// <summary>
/// One authored asset class - a portable, self-contained device description (one element of a v1 case's
/// <c>asset-classes.json</c>: a bare JSON array of classes, with no <c>schema</c> member and no schema id; D-DOC-230:
/// this comment used to name a schema id, gridsim-asset-classes/1, that no file carries and no reader checks) you bind
/// to a bus. Parameters are a flat name-&gt;value bag so the
/// format stays hand-editable and version-tolerant; <see cref="Provenance"/> records, per parameter,
/// whether the value is grounded or a literature-approximated placeholder. Any
/// <see cref="ProvenanceTag.CuratedPlaceholder"/> note MUST carry the literal token <c>[VERIFY]</c>
/// (validated at load) - the honesty discipline that keeps assumed control constants labelled as such.
/// </summary>
public sealed record AssetClass(
    string Name,
    AssetKind Kind,
    string Mode,
    IReadOnlyDictionary<string, double> Params,
    IReadOnlyDictionary<string, AssetClassProvenance> Provenance)
{
    /// <summary>Read a parameter, or <paramref name="fallback"/> if the class does not set it.</summary>
    public double Param(string key, double fallback)
        => Params.TryGetValue(key, out double v) ? v : fallback;

    /// <summary>The provenance for a parameter: its explicit entry, else the class <c>_default</c>, else a
    /// <see cref="ProvenanceTag.CuratedPlaceholder"/> entry with the note "[VERIFY] unspecified provenance" (never a
    /// grounded tag). D-DOC-213: this comment used to call the last fallback "a synthetic default"; the code returns
    /// CuratedPlaceholder, not a Synthetic tag.</summary>
    public AssetClassProvenance ProvenanceFor(string key)
        => Provenance.TryGetValue(key, out AssetClassProvenance? p) ? p
         : Provenance.TryGetValue(DefaultKey, out AssetClassProvenance? d) ? d
         : new AssetClassProvenance(ProvenanceTag.CuratedPlaceholder, "[VERIFY] unspecified provenance");

    public const string DefaultKey = "_default";

    /// <summary>
    /// Validate the honesty rule: every <see cref="ProvenanceTag.CuratedPlaceholder"/> provenance note
    /// (explicit or the <c>_default</c>) must contain the literal <c>[VERIFY]</c> token. Returns the
    /// human-readable violations (empty = clean).
    /// </summary>
    public IReadOnlyList<string> ValidateProvenance()
    {
        var errs = new List<string>();
        foreach ((string key, AssetClassProvenance prov) in Provenance)
            if (prov.Tag == ProvenanceTag.CuratedPlaceholder && !(prov.Note?.Contains("[VERIFY]") ?? false))
                errs.Add($"asset class '{Name}' parameter '{key}' is CuratedPlaceholder but its note lacks the literal [VERIFY] token.");
        return errs;
    }
}

/// <summary>
/// A named collection of authored asset classes (from <c>asset-classes.json</c>). Lookup is by class
/// name; an unknown name is a hard, fail-loud error at bind time - a silent miss would leave an asset
/// mysteriously passive.
/// </summary>
public sealed class AssetClassLibrary
{
    private readonly IReadOnlyDictionary<string, AssetClass> _classes;

    public AssetClassLibrary(IEnumerable<AssetClass> classes)
    {
        var map = new Dictionary<string, AssetClass>(StringComparer.OrdinalIgnoreCase);
        var unsupported = new List<string>();
        foreach (AssetClass c in classes)
        {
            if (!map.TryAdd(c.Name, c))
                throw new InvalidDataException($"Duplicate asset class name '{c.Name}' in asset-classes.json.");
            unsupported.AddRange(AssetClassSupport.Problems(c));
        }
        // Every class entering a library (from asset-classes.json, built in code, or re-built by an asset sweep) is
        // checked against AssetClassSupport here: a kind with no controller, or a parameter nothing reads, is refused,
        // and every problem is listed in one exception (rule R073).
        if (unsupported.Count > 0)
            throw new InvalidDataException("Unsupported asset class input (R073):" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", unsupported));
        _classes = map;
    }

    public int Count => _classes.Count;
    public IEnumerable<AssetClass> Classes => _classes.Values;
    public bool Contains(string name) => _classes.ContainsKey(name);

    public AssetClass Get(string name)
        => _classes.TryGetValue(name, out AssetClass? c) ? c
         : throw new InvalidDataException($"Unknown asset class '{name}' referenced by a binding but not defined in asset-classes.json.");

    /// <summary>Validate every class's provenance discipline; throws on any violation.</summary>
    public void ValidateOrThrow()
    {
        var errs = _classes.Values.SelectMany(c => c.ValidateProvenance()).ToList();
        if (errs.Count > 0)
            throw new InvalidDataException("Invalid asset-classes.json:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", errs));
    }
}

/// <summary>
/// Which asset-class kinds GridSim can simulate and which parameters each kind's consumers actually read (rule R073).
/// <see cref="AssetClassLibrary"/> refuses, listing every problem: a class of a kind with no controller
/// (<see cref="AssetKind.SynchronousMachine"/>: a generator bound to it would stay passive), a refused key
/// (<see cref="RefusedKeys"/>), and any key no consumer of the class's kind reads (a misspelt key included).
/// D-DOC-215 (DOC-17): this summary used to narrate the history of R073 ("before this ... now refuses"); it now states
/// the rule as it stands.
/// </summary>
/// <remarks>
/// The accepted keys per kind are exactly the <see cref="AssetParams"/> constants the JSON v2 kind spec for that
/// kind declares (gridFollowingInverter, gridFormingInverter, protection, oltc for a v1 'transformer' class, lineType
/// for a v1 'line' class), less the refused keys below; <c>AssetClassSupportTests</c> pins that equality, so the
/// table and the kind specs cannot drift apart. A key accepted here is read by at least one consumer of that kind
/// (the time-domain controller, the storage DC source, the small-signal / impedance screens, the protection stages).
/// </remarks>
public static class AssetClassSupport
{
    /// <summary>
    /// Keys refused on every kind, with the reason. <c>dispatchRampMwPerMin</c>: the inverter controllers model only the
    /// frequency response about a FIXED dispatch, so there is no dispatch setpoint for a ramp limit to act on, and
    /// clamping the fast response with a MW/min figure would contradict the fastSlew parameters that already limit it;
    /// the bundled values came from an observed fleet ramp band (GDA ramp_correlation), a system-level observation and
    /// not a unit control limit (technical manual chapter 13, asset-dynamics data grounding, records them; D-DOC-173: this
    /// used to cite the bare chapter number as a path, which is no file). <c>minOutputFraction</c>: declared, never read.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RefusedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AssetParams.DispatchRampMwPerMin] =
            "no controller reads it: the inverter controllers model the frequency response about a fixed dispatch, so " +
            "there is no dispatch setpoint to ramp (the fast response is already rate-limited by fastSlewUp/DownMwPerSPerMw). " +
            "The bundled values were an observed fleet ramp band, recorded in the technical manual, chapter 13 (asset-dynamics " +
            "data grounding, docs/technical/0013 Asset-dynamics data grounding.md). Remove the key.",
        [AssetParams.MinOutputFraction] =
            "no controller reads it (no controller models a minimum stable output). Remove the key.",
    };

    // Keys read by the grid-following controller, its DC source, and the small-signal / impedance screens.
    private static readonly string[] GridFollowingKeys =
    {
        AssetParams.DeadbandHz, AssetParams.DroopGainMwPerHzPerMw, AssetParams.RocofGainMwsPerHz,
        AssetParams.DetectionLatencyS, AssetParams.ComputeLatencyS, AssetParams.ActuateTauS,
        AssetParams.FastSlewUpMwPerSPerMw, AssetParams.FastSlewDownMwPerSPerMw,
        AssetParams.HeadroomUpFraction, AssetParams.HeadroomDownFraction,
        AssetParams.RocofTripHzPerS, AssetParams.RocofTripConfirmS, AssetParams.LocalRocofUpliftFactor,
        AssetParams.VectorShiftTripDeg, AssetParams.LvrtVoltagePu,
        AssetParams.PllBandwidthHz, AssetParams.PllDampingRatio, AssetParams.CurrentLoopBandwidthHz,
        AssetParams.CapacityMwh, AssetParams.SocInit, AssetParams.SocMin, AssetParams.SocMax,
        AssetParams.BatteryChemistry, AssetParams.DcVoltageV, AssetParams.ConverterEfficiency, AssetParams.AmbientC,
    };

    // Keys read by the grid-forming controller, its DC source, and the impedance screen.
    private static readonly string[] GridFormingKeys =
    {
        AssetParams.DeadbandHz, AssetParams.SynInertiaMwsPerHz, AssetParams.DampingMwPerHz, AssetParams.ActuateTauS,
        AssetParams.HeadroomUpFraction, AssetParams.HeadroomDownFraction,
        AssetParams.RocofTripHzPerS, AssetParams.RocofTripConfirmS, AssetParams.LocalRocofUpliftFactor,
        AssetParams.LvrtVoltagePu, AssetParams.VirtualReactancePu, AssetParams.PowerLoopBandwidthHz,
        AssetParams.CapacityMwh, AssetParams.SocInit, AssetParams.SocMin, AssetParams.SocMax,
        AssetParams.BatteryChemistry, AssetParams.DcVoltageV, AssetParams.ConverterEfficiency, AssetParams.AmbientC,
    };

    // Keys ProtectionController turns into stages (plus its measurement latency).
    private static readonly string[] ProtectionKeys =
    {
        AssetParams.RocofTripHzPerS, AssetParams.RocofTripConfirmS, AssetParams.DfDtTripHzPerS, AssetParams.DfDtTripDwellS,
        AssetParams.LocalRocofUpliftFactor, AssetParams.UfTripHz, AssetParams.UfTripDwellS, AssetParams.OfTripHz,
        AssetParams.OfTripDwellS, AssetParams.UvTripPu, AssetParams.UvTripDwellS, AssetParams.OvTripPu,
        AssetParams.OvTripDwellS, AssetParams.LvrtVoltagePu, AssetParams.VectorShiftTripDeg, AssetParams.DetectionLatencyS,
        AssetParams.OverloadTripFraction, AssetParams.OverloadTripDwellS, AssetParams.LfddStageHz, AssetParams.LfddBlockMw,
        AssetParams.LfddDwellS, AssetParams.AutoRecloseDelayS,
        AssetParams.SettingsNominalHz,   // N297 (B4): the nominal the frequency settings are written for
    };

    // Keys TransformerDynamics reads.
    private static readonly string[] TransformerKeys =
    {
        AssetParams.TargetVpu, AssetParams.TapStepPu, AssetParams.TapDeadbandPu, AssetParams.MinTap, AssetParams.MaxTap,
        AssetParams.InterTapDelayS, AssetParams.InitialTapDelayS, AssetParams.AmbientLoadingFraction,
        AssetParams.ThermalTauMin, AssetParams.DerateAtFullThermal,
    };

    // Keys LineThermalDynamics reads.
    private static readonly string[] LineKeys =
    {
        AssetParams.AmbientLoadingFraction, AssetParams.ThermalTauMin, AssetParams.DerateAtFullThermal,
    };

    /// <summary>The parameter keys a class of <paramref name="kind"/> may set; empty for a kind GridSim cannot simulate.</summary>
    public static IReadOnlySet<string> AcceptedKeys(AssetKind kind) => kind switch
    {
        AssetKind.GridFollowingInverter => Gfl,
        AssetKind.GridFormingInverter => Gfm,
        AssetKind.Protection => Prot,
        AssetKind.Transformer => Tx,
        AssetKind.Line => Ln,
        _ => None,
    };

    /// <summary>True when GridSim has a controller for <paramref name="kind"/>.</summary>
    public static bool IsSupportedKind(AssetKind kind) => kind != AssetKind.SynchronousMachine;

    private static readonly IReadOnlySet<string> Gfl = new HashSet<string>(GridFollowingKeys, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Gfm = new HashSet<string>(GridFormingKeys, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Prot = new HashSet<string>(ProtectionKeys, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Tx = new HashSet<string>(TransformerKeys, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Ln = new HashSet<string>(LineKeys, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Every reason <paramref name="cls"/> cannot be simulated as written: an unsupported kind, a refused key, or a
    /// key no consumer of its kind reads (keys are matched exactly, as the controllers look them up). Empty = fine.
    /// </summary>
    public static IReadOnlyList<string> Problems(AssetClass cls)
    {
        var problems = new List<string>();
        if (!IsSupportedKind(cls.Kind))
        {
            problems.Add($"asset class '{cls.Name}' has kind {cls.Kind}, which GridSim cannot simulate as an authored class: " +
                "there is no controller for it, so every asset bound to it would silently stay passive. Remove the class " +
                "(a synchronous machine's inertia comes from its generator's H and MBase).");
            return problems;   // its keys cannot be judged against a kind that has no consumer
        }
        IReadOnlySet<string> accepted = AcceptedKeys(cls.Kind);
        foreach (string key in cls.Params.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (RefusedKeys.TryGetValue(key, out string? why))
                problems.Add($"asset class '{cls.Name}' sets '{key}': {why}");
            else if (!accepted.Contains(key))
                problems.Add($"asset class '{cls.Name}' (kind {cls.Kind}) sets '{key}', which nothing that simulates a " +
                    $"{cls.Kind} reads, so it would be silently ignored. Check the spelling; accepted keys: " +
                    string.Join(", ", accepted.OrderBy(k => k, StringComparer.Ordinal)) + ".");
        }
        // N304 / N318: a protection frequency setting on the wrong side of, or implausibly far from, the nominal it is
        // written for.
        if (cls.Kind == AssetKind.Protection) problems.AddRange(ProtectionFrequencyProblems(cls));
        return problems;
    }

    /// <summary>
    /// N304 (October 2026, stream LF3) introduced this as the largest distance (Hz) a protection frequency setting could
    /// lie from its settings nominal, either side. N318 (LF6, October 2026; ruled by main under Ruling R1, Mark delegated
    /// the EE calls 2026-10-04) retired it from the protection rule, which is now direction-aware: see
    /// <see cref="MaxFrequencySettingDistanceHz"/> and <see cref="ProtectionFrequencyProblems"/>. The constant is KEPT,
    /// value unchanged, because the <c>fit-frequency</c> trace reader in the CLI (FrequencyTraceFile.MaxOffsetFromNominalHz)
    /// still uses 5 Hz as its "this is not a 50 Hz trace" margin. That check is not a protection setting: a trace sample
    /// may lie on either side of nominal, so it stays two-sided, and changing it is outside N318.
    /// </summary>
    public const double MaxFrequencySettingOffsetHz = 5.0;

    /// <summary>
    /// N318 (LF6, October 2026; ruled by main under Ruling R1, Mark delegated the EE calls 2026-10-04; replaces N304's
    /// symmetric 5 Hz bound): the largest distance (Hz) a protection frequency setting may lie from the nominal it is
    /// written for (<see cref="AssetParams.SettingsNominalHz"/>, absent: the GB 50 Hz), ON ITS OWN SIDE of that nominal.
    /// The rule (<see cref="ProtectionFrequencyProblems"/>) is direction-aware:
    /// <list type="bullet">
    /// <item>an under-frequency stage (<see cref="AssetParams.UfTripHz"/>) and an LFDD stage
    /// (<see cref="AssetParams.LfddStageHz"/>) operate when frequency falls BELOW the setting, so the setting must lie
    /// strictly below the nominal, and no more than 10 Hz below it;</item>
    /// <item>an over-frequency stage (<see cref="AssetParams.OfTripHz"/>) operates when frequency rises ABOVE the setting,
    /// so the setting must lie strictly above the nominal, and no more than 10 Hz above it.</item>
    /// </list>
    /// Real settings (documents in docs/reference):
    /// <list type="bullet">
    /// <item>GB: EREC G99 Issue 2 (S124) Table 10.1 spans 47.0 Hz (U/F stage 2) to 52.0 Hz (O/F), -3 / +2 Hz about
    /// 50 Hz; the GB LFDD stages run from 48.8 Hz down to 47.8 Hz (Grid Code CC.A.5.5.1, S076).</item>
    /// <item>North America at 60 Hz: NERC PRC-024-3 (S294) Attachment 1 lets a generator trip instantaneously only
    /// outside 57.8 / 61.8 Hz (Eastern Interconnection, Table 1), 57.0 / 61.7 Hz (Western, Table 2) and 57.5 / 61.8 Hz
    /// (ERCOT, Table 4); PRC-006-3 (S295) Attachment 1 models generator trips at 57.8 / 62.2 Hz and holds the UFLS curve
    /// at 58.0 Hz or above.</item>
    /// <item>The widest is the Quebec Interconnection (PRC-024-3 Table 3): instantaneous trips below 55.5 Hz and above
    /// 66.0 Hz, -4.5 / +6.0 Hz about 60 Hz. N304's symmetric 5 Hz refused Quebec's +6.0 Hz over-frequency stage (LF4,
    /// N310 notes); 10 Hz admits it with margin.</item>
    /// </list>
    /// What the rule exists to catch is the 50 / 60 Hz unit mistake, and it is the SIDE test that catches it, not the
    /// distance. Native 60 Hz settings written without <c>settingsNominalHz</c> = 60 are read as GB 50 Hz settings: every
    /// real 60 Hz UF / LFDD stage (55.5 Hz and up) then lies ABOVE 50 Hz, the wrong side, and is refused; every real 60 Hz
    /// OF stage (61.7 Hz and up) lies more than 10 Hz above 50 Hz and is refused. The reverse mistake (GB settings declared
    /// at 60 Hz) puts the GB OF stage (52.0 Hz) below 60 Hz, the wrong side, and every GB UF / LFDD stage (47.0 to 49.x Hz)
    /// more than 10 Hz below 60 Hz. Without the check such settings are moved by the nominal difference (N297): a 60 Hz UF
    /// stage at 59.3 Hz read as a GB setting becomes 69.3 Hz on a 60 Hz case and operates at once, with no word.
    /// A setting exactly AT the nominal is refused for every stage: a UF / LFDD stage there would operate on any dip at all
    /// and an OF stage on any rise, which is no protection setting. The 10 Hz is inclusive (exactly 10 Hz away on the
    /// right side is accepted). Every bundled class is within 1.2 Hz of 50 Hz on its own side.
    /// </summary>
    public const double MaxFrequencySettingDistanceHz = 10.0;

    /// <summary>
    /// N304 / N318: every reason a protection class's frequency settings are not plausible for the nominal they are
    /// written for: a <see cref="AssetParams.SettingsNominalHz"/> that is not a finite frequency above 0, a frequency
    /// setting that is not finite, a setting on the wrong side of that nominal (UF / LFDD at or above it, OF at or below
    /// it), or one more than <see cref="MaxFrequencySettingDistanceHz"/> from it on its own side. Each problem names the
    /// class, the key, the value and the nominal. Empty = fine (and always empty for a class that is not a protection).
    /// Called at load (every class entering an <see cref="AssetClassLibrary"/>) and again by
    /// <see cref="Protection.ProtectionController"/> for a class built directly in code.
    /// </summary>
    public static IReadOnlyList<string> ProtectionFrequencyProblems(AssetClass cls)
    {
        ArgumentNullException.ThrowIfNull(cls);
        var problems = new List<string>();
        if (cls.Kind != AssetKind.Protection) return problems;
        double nominal = cls.Param(AssetParams.SettingsNominalHz, AssetParamDefaults.ProtectionSettingsNominalHz);
        bool declared = cls.Params.ContainsKey(AssetParams.SettingsNominalHz);
        if (!double.IsFinite(nominal) || !(nominal > 0.0))
        {
            problems.Add($"protection class '{cls.Name}' sets '{AssetParams.SettingsNominalHz}' = {nominal:R}, which is not a " +
                "finite number of Hz above 0.");
            return problems;   // the settings cannot be judged against a nominal that is not a frequency
        }
        // How the nominal was arrived at, for the messages: declared by the class, or the GB default.
        string nominalText = declared
            ? $"'{AssetParams.SettingsNominalHz}' = {nominal:R}"
            : $"'{AssetParams.SettingsNominalHz}' absent, so the GB {nominal:R} Hz";
        string advice = $"Settings written for another system must declare its nominal in '{AssetParams.SettingsNominalHz}' " +
            "(for example 60 for a 60 Hz system).";
        // Fixed key order (UF, OF, LFDD), so the message lists problems in the same order on every run. The flag says which
        // way the stage operates: true = when frequency falls BELOW its setting (UF, LFDD), false = rises ABOVE it (OF).
        foreach ((string key, bool below) in new[]
                 { (AssetParams.UfTripHz, true), (AssetParams.OfTripHz, false), (AssetParams.LfddStageHz, true) })
        {
            if (!cls.Params.TryGetValue(key, out double hz)) continue;
            if (!double.IsFinite(hz))
            {
                problems.Add($"protection class '{cls.Name}' sets '{key}' = {hz:R}, which is not a finite frequency.");
                continue;
            }
            // Distance from the nominal measured towards the stage's own side: positive = the right side. For a UF / LFDD
            // stage that is (nominal - hz), for an OF stage (hz - nominal). One IEEE subtraction and two comparisons, so
            // the edges (exactly at nominal, exactly 10 Hz away) are judged identically on every CPU and OS.
            double towardsOwnSide = below ? nominal - hz : hz - nominal;
            string side = below ? "below" : "above";
            string stage = below ? "an under-frequency or LFDD stage" : "an over-frequency stage";
            if (!(towardsOwnSide > 0.0))
                problems.Add($"protection class '{cls.Name}' sets '{key}' = {hz:R} Hz, which is not {side} the {nominal:R} Hz " +
                    $"nominal its settings are written for ({nominalText}). It is {stage}, which operates when frequency goes " +
                    $"{side} its setting, so the setting must lie {side} the nominal; a setting on the wrong side is the mark of " +
                    $"settings written for another nominal. {advice}");
            else if (towardsOwnSide > MaxFrequencySettingDistanceHz)
                problems.Add($"protection class '{cls.Name}' sets '{key}' = {hz:R} Hz, more than {MaxFrequencySettingDistanceHz:R} Hz " +
                    $"{side} the {nominal:R} Hz nominal its settings are written for ({nominalText}). A real frequency setting " +
                    $"lies within a few hertz of its nominal. {advice}");
        }
        return problems;
    }
}

/// <summary>
/// R137 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the ONE table of fall-back values for an
/// asset-class parameter that more than one controller reads. Before, the same authored key fell back to different
/// values in different controllers: an absent <see cref="AssetParams.RocofTripConfirmS"/> was 0.5 s in
/// <see cref="GridFollowingInverter"/>, 1.0 s in <see cref="GridFormingInverter"/> and 0.0 s (an instantaneous trip) in
/// <see cref="Protection.ProtectionController"/>. Now the inverters read the value below, and a protection class must
/// author every dwell its stages need (<see cref="Protection.ProtectionController"/> refuses the class otherwise; see
/// <see cref="ProtectionDwellKeys"/>).
/// </summary>
public static class AssetParamDefaults
{
    /// <summary>
    /// RoCoF (loss-of-mains) trip confirmation time, seconds, for an inverter class that sets a RoCoF threshold but no
    /// confirmation time. Source: ENA EREC G99 Issue 2 (2025, docs/reference S124), s.10.6.7.1 Table 10.1: LoM (RoCoF)
    /// 1 Hz/s, "time delay 0.5 s", for Type A to C Power Generating Modules (LV and HV), with the note (p.126) that the
    /// relay "shall not trip unless the measured rate remains above the threshold ... continuously for 500 ms". The
    /// grid-following fallback was already 0.5 s (unchanged); the grid-forming fallback moves from 1.0 s to 0.5 s. No
    /// bundled grid-forming class is affected: every one either authors the time (gfm-bess-2024: 1.0 s) or never reaches
    /// its threshold (gfm-vsm-study-2024: 1e9 Hz/s).
    /// </summary>
    public const double RocofTripConfirmS = 0.5;

    /// <summary>
    /// N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the system nominal frequency (Hz) a
    /// protection class's frequency settings (<see cref="AssetParams.UfTripHz"/>, <see cref="AssetParams.OfTripHz"/>,
    /// <see cref="AssetParams.LfddStageHz"/>) are written for, when the class does not say
    /// (<see cref="AssetParams.SettingsNominalHz"/>): the GB 50 Hz, the convention of every bundled class (GB data:
    /// G99 47.5 / 52 Hz, the 48.8 Hz LFDD stage). On a system at another nominal the settings move with it as hertz
    /// offsets (the convention of <see cref="Dynamics.FrequencyBands"/>), so a GB 48.8 Hz LFDD stage operates at 58.8 Hz on
    /// a 60 Hz case instead of never.
    /// </summary>
    public const double ProtectionSettingsNominalHz = Dynamics.FrequencyLimits.NominalHz;

    /// <summary>
    /// R137: each protection stage threshold key and the dwell (definite-time delay) key it needs. A protection class
    /// that sets the threshold must set the dwell (0 is a legitimate instantaneous setting, but it must be written).
    /// The LVRT and vector-shift stages have no dwell key: they are instantaneous by definition.
    /// </summary>
    public static readonly IReadOnlyList<(string Threshold, string Dwell)> ProtectionDwellKeys = new[]
    {
        (AssetParams.RocofTripHzPerS, AssetParams.RocofTripConfirmS),
        (AssetParams.DfDtTripHzPerS, AssetParams.DfDtTripDwellS),
        (AssetParams.UfTripHz, AssetParams.UfTripDwellS),
        (AssetParams.OfTripHz, AssetParams.OfTripDwellS),
        (AssetParams.UvTripPu, AssetParams.UvTripDwellS),
        (AssetParams.OvTripPu, AssetParams.OvTripDwellS),
        (AssetParams.OverloadTripFraction, AssetParams.OverloadTripDwellS),
        (AssetParams.LfddStageHz, AssetParams.LfddDwellS),
    };
}

/// <summary>
/// Canonical parameter names for the built-in inverter controllers. Centralised so the JSON, the
/// controllers and the tests never drift on a spelling.
/// </summary>
public static class AssetParams
{
    // --- shared / grid-following ---
    public const string DeadbandHz = "deadbandHz";
    public const string DroopGainMwPerHzPerMw = "droopGainMwPerHzPerMw";
    public const string RocofGainMwsPerHz = "rocofGainMwsPerHz";
    public const string DetectionLatencyS = "detectionLatencyS";
    public const string ComputeLatencyS = "computeLatencyS";
    public const string ActuateTauS = "actuateTauS";
    public const string FastSlewUpMwPerSPerMw = "fastSlewUpMwPerSPerMw";
    public const string FastSlewDownMwPerSPerMw = "fastSlewDownMwPerSPerMw";
    public const string DispatchRampMwPerMin = "dispatchRampMwPerMin";
    public const string HeadroomUpFraction = "headroomUpFraction";
    public const string HeadroomDownFraction = "headroomDownFraction";
    public const string CapacityMwh = "capacityMwh";
    public const string SocInit = "socInit";
    public const string SocMin = "socMin";
    public const string SocMax = "socMax";
    // --- storage chemistry (Storage/): batteryChemistry code 1 LFP, 2 NMC, 3 LTO, 4 Na-ion, 5 VRFB, 6 lead-acid;
    // with capacityMwh set it replaces the SoC bucket with a chemistry pack behind the converter. ---
    public const string BatteryChemistry = "batteryChemistry";
    public const string DcVoltageV = "dcVoltageV";
    public const string ConverterEfficiency = "converterEfficiency";
    public const string AmbientC = "ambientC";
    public const string RocofTripHzPerS = "rocofTripHzPerS";
    public const string RocofTripConfirmS = "rocofTripConfirmS";
    public const string LocalRocofUpliftFactor = "localRocofUpliftFactor";
    public const string VectorShiftTripDeg = "vectorShiftTripDeg";
    public const string LvrtVoltagePu = "lvrtVoltagePu";
    public const string MinOutputFraction = "minOutputFraction";

    // --- grid-forming ---
    public const string SynInertiaMwsPerHz = "synInertiaMwsPerHz";
    public const string DampingMwPerHz = "dampingMwPerHz";

    // --- small-signal (modal) archetype: optional PLL bandwidth for the linearised SRF-PLL of a
    // grid-following unit. The time-domain controller carries a transport DELAY, not PLL gains, so the
    // small-signal model substitutes a 2nd-order SRF-PLL; when unset the bandwidth is derived from
    // detectionLatencyS. This parameter only affects the opt-in `modal --enable-inverters` path. ---
    public const string PllBandwidthHz = "pllBandwidthHz";

    // --- small-signal impedance (Workstream D); opt-in, absent = literature-typical CuratedPlaceholder
    // [VERIFY]. These are NEVER read by the behavioural time-domain controllers, so every existing/validated
    // path stays byte-identical - they only feed the impedance-stability screen's converter admittance. ---
    public const string PllDampingRatio = "pllDampingRatio";
    public const string CurrentLoopBandwidthHz = "currentLoopBandwidthHz";
    public const string VirtualReactancePu = "virtualReactancePu";      // GFM
    public const string PowerLoopBandwidthHz = "powerLoopBandwidthHz";   // GFM

    // --- protections (frequency/voltage/thermal/recloser) ---
    public const string UfTripHz = "ufTripHz";
    public const string UfTripDwellS = "ufTripDwellS";
    public const string OfTripHz = "ofTripHz";
    public const string OfTripDwellS = "ofTripDwellS";
    public const string DfDtTripHzPerS = "dfDtTripHzPerS";
    public const string DfDtTripDwellS = "dfDtTripDwellS";
    public const string UvTripPu = "uvTripPu";
    public const string UvTripDwellS = "uvTripDwellS";
    public const string OvTripPu = "ovTripPu";
    public const string OvTripDwellS = "ovTripDwellS";
    public const string OverloadTripFraction = "overloadTripFraction";
    public const string OverloadTripDwellS = "overloadTripDwellS";
    public const string LfddStageHz = "lfddStageHz";
    public const string LfddBlockMw = "lfddBlockMw";
    public const string LfddDwellS = "lfddDwellS";
    public const string AutoRecloseDelayS = "autoRecloseDelayS";
    /// <summary>N297 (B4, October 2026): the system nominal frequency (Hz) a protection class's frequency settings are
    /// written for. Absent: <see cref="AssetParamDefaults.ProtectionSettingsNominalHz"/> (50 Hz, GB).</summary>
    public const string SettingsNominalHz = "settingsNominalHz";

    // --- passive plant: transformer OLTC + thermal ---
    public const string TargetVpu = "targetVpu";
    public const string TapStepPu = "tapStepPu";
    public const string TapDeadbandPu = "tapDeadbandPu";
    public const string MinTap = "minTap";
    public const string MaxTap = "maxTap";
    public const string InterTapDelayS = "interTapDelayS";
    public const string InitialTapDelayS = "initialTapDelayS";
    public const string ThermalTauMin = "thermalTauMin";
    public const string AmbientLoadingFraction = "ambientLoadingFraction";
    public const string DerateAtFullThermal = "derateAtFullThermal";
}
