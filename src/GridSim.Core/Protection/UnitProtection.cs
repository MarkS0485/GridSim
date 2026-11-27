using System.Globalization;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Protection;

/// <summary>The function of a relay a generating unit carries itself (its own protection, not the network's).</summary>
public enum UnitRelayFunction
{
    /// <summary>27: undervoltage at the unit's terminal bus.</summary>
    UnderVoltage,
    /// <summary>59: overvoltage at the unit's terminal bus.</summary>
    OverVoltage,
    /// <summary>32: reverse power (the unit motoring).</summary>
    ReversePower,
    /// <summary>40: loss of excitation (field failure), an offset mho characteristic in the impedance plane.</summary>
    LossOfExcitation,
    /// <summary>81U: underfrequency.</summary>
    UnderFrequency,
    /// <summary>81O: overfrequency.</summary>
    OverFrequency,
    /// <summary>81R: rate of change of frequency (loss of mains).</summary>
    Rocof,
    /// <summary>78: vector shift (loss of mains).</summary>
    VectorShift,
}

/// <summary>Where a resolved unit setting came from: the unit's own override or its protection class default.</summary>
public enum UnitSettingOrigin
{
    /// <summary>The unit protection template (class default) the unit takes.</summary>
    ClassDefault,
    /// <summary>The unit's own value (its protection schedule, what was agreed at connection).</summary>
    UnitOverride,
}

/// <summary>
/// One resolved relay setting of a unit: the value, its provenance (where the number came from) and which layer supplied
/// it. <see cref="Layer"/> names the layer in words (for example <c>unit</c>, <c>template 'ccgt-gt'</c>,
/// <c>unit, preset rocof 'modern'</c>).
/// </summary>
public sealed record UnitSetting(string Key, double Value, AssetClassProvenance Provenance, UnitSettingOrigin Origin, string Layer)
{
    /// <summary>True when the value is a placeholder (<see cref="ProvenanceTag.CuratedPlaceholder"/>), not a sourced value.</summary>
    public bool IsPlaceholder => Provenance.Tag == ProvenanceTag.CuratedPlaceholder;

    /// <summary>"placeholder" or "sourced", the word every unit verdict prints.</summary>
    public string Grade => IsPlaceholder ? "placeholder" : "sourced";
}

/// <summary>
/// One stage of a unit's own relay after resolution. <see cref="Pickup"/> is the operating threshold (Hz, Hz/s, deg, pu of
/// the bus nominal voltage, or pu of the unit rating for 32), <see cref="Delay"/> the definite time delay (s). For 40,
/// <see cref="Pickup"/> is the mho circle's offset below the origin and <see cref="Diameter"/> its diameter, both pu on the
/// unit rating.
/// </summary>
public sealed record UnitRelayStage(string Device, UnitRelayFunction Function, string FunctionName, UnitSetting Pickup,
    UnitSetting Delay, UnitSetting? Diameter = null);

/// <summary>
/// One authored layer of unit protection settings, exactly as written: numeric settings by key, the per-key provenance,
/// and the optional presets. A preset expands into numeric settings with the preset's own cited provenance; within one
/// layer an explicit key wins over <see cref="RocofPreset"/>, which wins over <see cref="InterfacePreset"/>.
/// </summary>
public sealed record UnitProtectionLayer(
    IReadOnlyDictionary<string, double> Settings,
    IReadOnlyDictionary<string, AssetClassProvenance> Provenance,
    string? RocofPreset = null,
    string? InterfacePreset = null);

/// <summary>A unit protection class (a v2 template of kind unitProtection): the default settings a unit taking it inherits.
/// Its provenance needs a <c>_default</c> entry (every v2 template has one).</summary>
public sealed record UnitProtectionClass(string Name, string? Description, UnitProtectionLayer Layer);

/// <summary>A unit's own protection: the class it takes (its unitProtection template, or none) and its own values (the
/// per-unit overrides), each with its source.</summary>
public sealed record UnitProtectionAssignment(string Unit, string? ClassName, UnitProtectionLayer Layer)
{
    /// <summary>Why the unit's own values differ from its class (the per-unit overrides' reasons), or null when it has none.</summary>
    public string? Reason { get; init; }
}

/// <summary>A unit's relays after resolution (unit override, then class default, then absent).</summary>
public sealed record ResolvedUnitProtection(string Unit, string? ClassName, double SettingsNominalHz,
    UnitSetting? SettingsNominal, IReadOnlyList<UnitRelayStage> Stages);

/// <summary>
/// Per-unit (per-asset) protection: the unit protection classes of a case and each unit's own protection block, resolved
/// once at construction. Carried by <see cref="Model.GridModel.UnitProtection"/>; null there (the default) means no unit
/// carries relays of its own, and every study is exactly what it was before per-unit protection existed.
///
/// <para><b>Resolution.</b> Per setting key: the unit's own layer, else its class's layer, else absent. An absent relay is
/// absent: nothing is invented. A stage exists when its pickup resolves; its delay must then resolve too (from either
/// layer), and a delay with no pickup is refused, so a half-set stage never loads. Within a layer an explicit key wins over
/// the layer's RoCoF preset, which wins over its interface preset.</para>
///
/// <para><b>Provenance.</b> A class layer needs a <c>_default</c> provenance; a key without its own entry takes it. A unit
/// layer needs an entry for every key it sets (the unit's value must say where it came from). A preset's keys carry the
/// preset's own provenance and may not be given another. A <see cref="ProvenanceTag.CuratedPlaceholder"/> note must carry
/// the literal <c>[VERIFY]</c> (the asset-class honesty rule).</para>
///
/// <para>Every problem is listed in one <see cref="InvalidDataException"/> (fail loud, never only the first).</para>
/// </summary>
public sealed class UnitProtectionSet
{
    /// <summary>The settings nominal frequency (Hz) when no layer gives one: the GB 50 Hz the ENA settings are written for.</summary>
    public const double DefaultSettingsNominalHz = Dynamics.FrequencyLimits.NominalHz;

    private readonly Dictionary<string, ResolvedUnitProtection> _byUnit = new(StringComparer.Ordinal);

    /// <summary>Build and resolve; throws <see cref="InvalidDataException"/> listing every problem.</summary>
    public UnitProtectionSet(IEnumerable<UnitProtectionClass> classes, IEnumerable<UnitProtectionAssignment> units)
    {
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(units);
        Classes = classes.ToList();
        Units = units.ToList();
        var problems = new List<string>();
        var classByName = new Dictionary<string, UnitProtectionClass>(StringComparer.Ordinal);
        foreach (UnitProtectionClass c in Classes)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) { problems.Add("a unit protection template has a blank name"); continue; }
            if (!classByName.TryAdd(c.Name, c)) { problems.Add($"unit protection template '{c.Name}' is defined more than once"); continue; }
            CheckLayer(c.Layer, $"unit protection template '{c.Name}'", isClass: true, problems);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnitProtectionAssignment u in Units)
        {
            if (string.IsNullOrWhiteSpace(u.Unit)) { problems.Add("a unit protection block names a blank unit"); continue; }
            if (!seen.Add(u.Unit)) { problems.Add($"unit '{u.Unit}' has more than one unit protection block"); continue; }
            string at = $"unit '{u.Unit}'";
            CheckLayer(u.Layer, at, isClass: false, problems);
            UnitProtectionClass? cls = null;
            if (u.ClassName is not null && !classByName.TryGetValue(u.ClassName, out cls))
            {
                problems.Add($"{at} takes unit protection template '{u.ClassName}', which is not defined");
                continue;
            }
            ResolvedUnitProtection? r = Resolve(u, cls, problems);
            if (r is not null) _byUnit[u.Unit] = r;
        }
        if (problems.Count > 0)
            throw new InvalidDataException("Unit protection is invalid:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", problems));
        Resolved = Units.Select(u => _byUnit[u.Unit]).ToList();
    }

    /// <summary>The classes, in the order written.</summary>
    public IReadOnlyList<UnitProtectionClass> Classes { get; }

    /// <summary>The unit blocks, in the order written.</summary>
    public IReadOnlyList<UnitProtectionAssignment> Units { get; }

    /// <summary>The resolved relays of every unit with a block, in <see cref="Units"/> order.</summary>
    public IReadOnlyList<ResolvedUnitProtection> Resolved { get; }

    /// <summary>The resolved relays of <paramref name="unit"/>, or null when the unit has no block.</summary>
    public ResolvedUnitProtection? For(string unit) => _byUnit.GetValueOrDefault(unit);

    // ------------------------------------------------------------------------------------------------ keys and stages

    /// <summary>The definition of one stage: device label, function, pickup key, delay key, diameter key (40 only).</summary>
    private sealed record StageKeys(string Device, UnitRelayFunction Function, string Name, string Pickup, string Delay, string? Diameter = null);

    /// <summary>Every stage in the order verdicts are produced: voltage (27, 59), power (32), field (40), frequency (81U,
    /// 81O, 81R, 78), the grouping of <see cref="ProtectionRegistry"/>.</summary>
    private static readonly StageKeys[] StageTable =
    {
        new("27-1", UnitRelayFunction.UnderVoltage, "Undervoltage stage 1", "uv1Pu", "uv1DelayS"),
        new("27-2", UnitRelayFunction.UnderVoltage, "Undervoltage stage 2", "uv2Pu", "uv2DelayS"),
        new("59-1", UnitRelayFunction.OverVoltage, "Overvoltage stage 1", "ov1Pu", "ov1DelayS"),
        new("59-2", UnitRelayFunction.OverVoltage, "Overvoltage stage 2", "ov2Pu", "ov2DelayS"),
        new("32", UnitRelayFunction.ReversePower, "Reverse power", "reversePowerPu", "reversePowerDelayS"),
        new("40-1", UnitRelayFunction.LossOfExcitation, "Loss of excitation zone 1", "loe1OffsetPu", "loe1DelayS", "loe1DiameterPu"),
        new("40-2", UnitRelayFunction.LossOfExcitation, "Loss of excitation zone 2", "loe2OffsetPu", "loe2DelayS", "loe2DiameterPu"),
        new("81U-1", UnitRelayFunction.UnderFrequency, "Underfrequency stage 1", "uf1Hz", "uf1DelayS"),
        new("81U-2", UnitRelayFunction.UnderFrequency, "Underfrequency stage 2", "uf2Hz", "uf2DelayS"),
        new("81O-1", UnitRelayFunction.OverFrequency, "Overfrequency stage 1", "of1Hz", "of1DelayS"),
        new("81O-2", UnitRelayFunction.OverFrequency, "Overfrequency stage 2", "of2Hz", "of2DelayS"),
        new("81R", UnitRelayFunction.Rocof, "RoCoF", "rocofHzPerS", "rocofDelayS"),
        new("78", UnitRelayFunction.VectorShift, "Vector shift", "vectorShiftDeg", "vectorShiftDelayS"),
    };

    /// <summary>The settings nominal frequency key.</summary>
    public const string SettingsNominalKey = "relayNominalHz";

    /// <summary>Every numeric setting key a layer may carry, in the fixed order the writer emits them.</summary>
    public static IReadOnlyList<string> SettingKeys { get; } = StageTable
        .SelectMany(s => s.Diameter is null ? new[] { s.Pickup, s.Delay } : new[] { s.Pickup, s.Diameter, s.Delay })
        .Append(SettingsNominalKey).ToArray();

    /// <summary>The RoCoF preset names.</summary>
    public static IReadOnlyList<string> RocofPresetNames { get; } = new[] { "legacy", "legacy-high-impedance", "modern" };

    /// <summary>The interface (G99 Table 10.1) preset names.</summary>
    public static IReadOnlyList<string> InterfacePresetNames { get; } = new[] { "g99-hv", "g99-lv" };

    // The plain-language range rule of each key kind, checked per layer (and, for frequency, against the nominal later).
    private static string? RangeProblem(string key, double v)
    {
        if (!double.IsFinite(v)) return "must be a finite number";
        if (key.EndsWith("DelayS", StringComparison.Ordinal)) return v >= 0.0 ? null : "is a time delay and must be 0 s or more";
        return key switch
        {
            "uv1Pu" or "uv2Pu" => v > 0.0 && v < 1.0 ? null : "is an undervoltage pickup and must lie between 0 and 1 pu",
            "ov1Pu" or "ov2Pu" => v > 1.0 ? null : "is an overvoltage pickup and must be above 1 pu",
            "reversePowerPu" => v > 0.0 && v <= 1.0 ? null : "is a reverse-power pickup (pu of the unit rating) and must lie above 0 and at most 1",
            "loe1OffsetPu" or "loe2OffsetPu" => v >= 0.0 ? null : "is a mho offset (pu of the unit rating) and must be 0 or more",
            "loe1DiameterPu" or "loe2DiameterPu" => v > 0.0 ? null : "is a mho diameter (pu of the unit rating) and must be above 0",
            "rocofHzPerS" => v > 0.0 ? null : "is a RoCoF pickup and must be above 0 Hz/s",
            "vectorShiftDeg" => v > 0.0 && v < 180.0 ? null : "is a vector-shift pickup and must lie between 0 and 180 degrees",
            _ => v > 0.0 ? null : "is a frequency and must be above 0 Hz",   // uf*, of*, relayNominalHz
        };
    }

    // ------------------------------------------------------------------------------------------------ presets

    private static AssetClassProvenance Rec(string note) => new(ProvenanceTag.Recorded, note);

    private const string ModernRocofNote =
        "Modern GB loss-of-mains RoCoF setting, 1 Hz/s with a 0.5 s definite time delay that resets when RoCoF falls below the "
        + "threshold: EREC G99 Issue 2 (docs/reference S124) Table 10.1 and its note, pp.125-126; EREC G59 Issue 3 Amendment 8 "
        + "(S135), RoCoF settings for all plant from 01/09/22, p.62";

    private static string LegacyRocofNote(string k2, string network) =>
        $"Legacy G59 loss-of-mains RoCoF setting K2 x 0.125 Hz/s with K2 = {k2} ({network} network): EREC G59 Issue 3 Amendment 8 "
        + "(docs/reference S135), RoCoF settings for plant commissioned before 01/02/18, permitted until 01/09/22, p.62, and LoM "
        + "constants note (2), p.63; EREC G59 Issue 3 Amendment 2 (S132) Table 10.5.7.1, p.63";

    private const string LegacyDelayNote =
        "Legacy G59 loss-of-mains RoCoF time delay 0.5 s: EREC G59 Issue 3 Amendment 8 (docs/reference S135) Table 10.5.7.1, "
        + "p.62, RoCoF settings for Power Stations < 5MW Registered Capacity, Generating Plant commissioned before 01/02/18, "
        + "settings permitted until 01/09/22: 'Not to be less than K2 x 0.125 Hz/s and not to be greater than 1.0Hz/s, time "
        + "delay 0.5s'; EREC G59 Issue 3 Amendment 2 (S132) Table 10.5.7.1, p.63, the same 0.5 s for the legacy band (Power "
        + "Stations >= 5MW commissioned before 01/08/14: not less than K2 x 0.125 Hz/s, time delay 0.5s; S132's bare '<5MW "
        + "K2 x 0.125 Hz/s' row carries no time entry, and the delay follows the amended band). The delay begins when the "
        + "measured RoCoF exceeds the threshold and is reset if it falls below it (S135 note \u00b6, p.63)";

    /// <summary>The settings a RoCoF preset expands to, with their provenance.</summary>
    internal static IReadOnlyList<(string Key, double Value, AssetClassProvenance Prov)> RocofPreset(string name) => name switch
    {
        "modern" => new[] { ("rocofHzPerS", 1.0, Rec(ModernRocofNote)), ("rocofDelayS", 0.5, Rec(ModernRocofNote)) },
        "legacy" => new[]
        {
            ("rocofHzPerS", 0.125, Rec(LegacyRocofNote("1.0", "low-impedance"))),
            ("rocofDelayS", 0.5, Rec(LegacyDelayNote)),
        },
        "legacy-high-impedance" => new[]
        {
            ("rocofHzPerS", 0.2, Rec(LegacyRocofNote("1.6", "high-impedance"))),
            ("rocofDelayS", 0.5, Rec(LegacyDelayNote)),
        },
        _ => throw new InvalidDataException($"unknown RoCoF preset '{name}'"),
    };

    private const string G99Table = "EREC G99 Issue 2 (docs/reference S124) Table 10.1 Settings for Long-Term Parallel Operation, p.125";

    /// <summary>The settings an interface preset expands to (EREC G99 Issue 2 Table 10.1, Types A to C), with provenance.</summary>
    internal static IReadOnlyList<(string Key, double Value, AssetClassProvenance Prov)> InterfacePreset(string name)
    {
        bool hv = name switch
        {
            "g99-hv" => true,
            "g99-lv" => false,
            _ => throw new InvalidDataException($"unknown interface preset '{name}'"),
        };
        string col = hv ? "HV protection" : "LV protection (230 V base)";
        AssetClassProvenance P(string row) => Rec($"{G99Table}, {col}, {row}");
        var list = new List<(string, double, AssetClassProvenance)>
        {
            ("uv1Pu", 0.8, P("U/V: V - 20% (LV 184.0 V), 2.5 s (may need reducing where auto-reclose is under 3 s)")),
            ("uv1DelayS", 2.5, P("U/V time delay 2.5 s")),
        };
        if (hv)
        {
            list.Add(("ov1Pu", 1.1, P("O/V stage 1: V + 10%, 1.0 s")));
            list.Add(("ov1DelayS", 1.0, P("O/V stage 1 time delay 1.0 s")));
            list.Add(("ov2Pu", 1.13, P("O/V stage 2: V + 13%, 0.5 s")));
            list.Add(("ov2DelayS", 0.5, P("O/V stage 2 time delay 0.5 s")));
        }
        else
        {
            list.Add(("ov1Pu", 1.14, P("O/V stage 1: V + 14% (262.2 V), 1.0 s")));
            list.Add(("ov1DelayS", 1.0, P("O/V stage 1 time delay 1.0 s")));
            list.Add(("ov2Pu", 1.19, P("O/V stage 2: V + 19% (273.7 V), 0.5 s")));
            list.Add(("ov2DelayS", 0.5, P("O/V stage 2 time delay 0.5 s")));
        }
        list.Add(("uf1Hz", 47.5, P("U/F stage 1: 47.5 Hz, 20 s")));
        list.Add(("uf1DelayS", 20.0, P("U/F stage 1 time delay 20 s")));
        list.Add(("uf2Hz", 47.0, P("U/F stage 2: 47.0 Hz, 0.5 s")));
        list.Add(("uf2DelayS", 0.5, P("U/F stage 2 time delay 0.5 s")));
        list.Add(("of1Hz", 52.0, P("O/F: 52.0 Hz, 0.5 s")));
        list.Add(("of1DelayS", 0.5, P("O/F time delay 0.5 s")));
        list.Add(("rocofHzPerS", 1.0, P("LoM (RoCoF): 1 Hz/s, time delay 0.5 s")));
        list.Add(("rocofDelayS", 0.5, P("LoM (RoCoF) time delay 0.5 s, reset when RoCoF falls below the threshold (note, p.126)")));
        list.Add((SettingsNominalKey, 50.0, P("a GB 50 Hz setting")));
        return list;
    }

    // ------------------------------------------------------------------------------------------------ checks

    private static void CheckLayer(UnitProtectionLayer layer, string at, bool isClass, List<string> problems)
    {
        var known = new HashSet<string>(SettingKeys, StringComparer.Ordinal);
        foreach ((string key, double v) in layer.Settings)
        {
            if (!known.Contains(key)) { problems.Add($"{at}: setting '{key}' is not a unit protection setting"); continue; }
            if (RangeProblem(key, v) is { } why) problems.Add($"{at}: setting '{key}' = {v.ToString("R", CultureInfo.InvariantCulture)} {why}");
        }
        if (layer.RocofPreset is { } rp && !RocofPresetNames.Contains(rp))
            problems.Add($"{at}: rocofPreset '{rp}' is not one of {string.Join(", ", RocofPresetNames)}");
        if (layer.InterfacePreset is { } ip && !InterfacePresetNames.Contains(ip))
            problems.Add($"{at}: interfacePreset '{ip}' is not one of {string.Join(", ", InterfacePresetNames)}");

        HashSet<string> presetKeys = PresetKeys(layer);
        foreach ((string key, AssetClassProvenance prov) in layer.Provenance)
        {
            if (key is "rocofPreset" or "interfacePreset") { }   // the preset choice itself; its values carry their own source
            else if (key == AssetClass.DefaultKey)
            {
                if (!isClass) problems.Add($"{at}: a unit's provenance has no '{AssetClass.DefaultKey}' entry; give each setting the unit sets its own source");
            }
            else if (!known.Contains(key)) problems.Add($"{at}: provenance names '{key}', which is not a unit protection setting");
            else if (!layer.Settings.ContainsKey(key) && presetKeys.Contains(key))
                problems.Add($"{at}: provenance names '{key}', which this layer takes from a preset; a preset carries its own source");
            else if (!layer.Settings.ContainsKey(key) && !isClass)
                problems.Add($"{at}: provenance names '{key}', which this layer does not set");
            if (prov.Tag == ProvenanceTag.CuratedPlaceholder && !(prov.Note?.Contains("[VERIFY]", StringComparison.Ordinal) ?? false))
                problems.Add($"{at}: the provenance of '{key}' is CuratedPlaceholder but its note lacks the literal [VERIFY] token");
        }
        if (isClass && !layer.Provenance.ContainsKey(AssetClass.DefaultKey))
            problems.Add($"{at}: provenance needs a '{AssetClass.DefaultKey}' entry (the class default's source, CuratedPlaceholder with [VERIFY] when it is a placeholder)");
        if (!isClass)
            foreach (string key in layer.Settings.Keys)
                if (!layer.Provenance.ContainsKey(key))
                    problems.Add($"{at}: setting '{key}' has no provenance; a unit's own value must say where it came from (its protection schedule, the connection agreement, or CuratedPlaceholder with [VERIFY])");
    }

    private static HashSet<string> PresetKeys(UnitProtectionLayer layer)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (layer.RocofPreset is { } rp && RocofPresetNames.Contains(rp)) foreach (var x in RocofPreset(rp)) keys.Add(x.Key);
        if (layer.InterfacePreset is { } ip && InterfacePresetNames.Contains(ip)) foreach (var x in InterfacePreset(ip)) keys.Add(x.Key);
        return keys;
    }

    // ------------------------------------------------------------------------------------------------ resolution

    /// <summary>The effective settings of one layer: explicit keys, then the RoCoF preset, then the interface preset.</summary>
    private static Dictionary<string, UnitSetting> Effective(UnitProtectionLayer layer, UnitSettingOrigin origin, string name)
    {
        var map = new Dictionary<string, UnitSetting>(StringComparer.Ordinal);
        if (layer.InterfacePreset is { } ip && InterfacePresetNames.Contains(ip))
            foreach ((string k, double v, AssetClassProvenance p) in InterfacePreset(ip))
                map[k] = new UnitSetting(k, v, p, origin, $"{name}, preset interface '{ip}'");
        if (layer.RocofPreset is { } rp && RocofPresetNames.Contains(rp))
            foreach ((string k, double v, AssetClassProvenance p) in RocofPreset(rp))
                map[k] = new UnitSetting(k, v, p, origin, $"{name}, preset rocof '{rp}'");
        foreach ((string k, double v) in layer.Settings)
        {
            AssetClassProvenance prov = layer.Provenance.TryGetValue(k, out AssetClassProvenance? p) ? p
                : layer.Provenance.TryGetValue(AssetClass.DefaultKey, out AssetClassProvenance? d) ? d
                : new AssetClassProvenance(ProvenanceTag.CuratedPlaceholder, "[VERIFY] unspecified provenance");
            map[k] = new UnitSetting(k, v, prov, origin, name);
        }
        return map;
    }

    private static ResolvedUnitProtection? Resolve(UnitProtectionAssignment u, UnitProtectionClass? cls, List<string> problems)
    {
        string at = $"unit '{u.Unit}'";
        Dictionary<string, UnitSetting> unit = Effective(u.Layer, UnitSettingOrigin.UnitOverride, "unit");
        Dictionary<string, UnitSetting> klass = cls is null
            ? new Dictionary<string, UnitSetting>(StringComparer.Ordinal)
            : Effective(cls.Layer, UnitSettingOrigin.ClassDefault, $"template '{cls.Name}'");
        UnitSetting? Get(string key) => unit.TryGetValue(key, out UnitSetting? s) ? s : klass.GetValueOrDefault(key);

        UnitSetting? nominal = Get(SettingsNominalKey);
        double f0 = nominal?.Value ?? DefaultSettingsNominalHz;
        int before = problems.Count;
        var stages = new List<UnitRelayStage>();
        foreach (StageKeys sk in StageTable)
        {
            UnitSetting? pickup = Get(sk.Pickup), delay = Get(sk.Delay);
            UnitSetting? diameter = sk.Diameter is null ? null : Get(sk.Diameter);
            if (pickup is null)
            {
                if (delay is not null) problems.Add($"{at}: relay {sk.Device} has a delay ({sk.Delay}, from {delay.Layer}) but no pickup ({sk.Pickup}); set both or neither");
                if (diameter is not null) problems.Add($"{at}: relay {sk.Device} has a diameter ({sk.Diameter}, from {diameter.Layer}) but no offset ({sk.Pickup}); set all three or none");
                continue;
            }
            if (delay is null) { problems.Add($"{at}: relay {sk.Device} has a pickup ({sk.Pickup}, from {pickup.Layer}) but no delay ({sk.Delay}); set both or neither"); continue; }
            if (sk.Diameter is not null && diameter is null) { problems.Add($"{at}: relay {sk.Device} has an offset ({sk.Pickup}) but no diameter ({sk.Diameter}); set all three or none"); continue; }
            if (sk.Function == UnitRelayFunction.UnderFrequency && !(pickup.Value < f0))
                problems.Add($"{at}: relay {sk.Device} pickup {pickup.Value.ToString("R", CultureInfo.InvariantCulture)} Hz is not below the {f0.ToString("R", CultureInfo.InvariantCulture)} Hz settings nominal");
            if (sk.Function == UnitRelayFunction.OverFrequency && !(pickup.Value > f0))
                problems.Add($"{at}: relay {sk.Device} pickup {pickup.Value.ToString("R", CultureInfo.InvariantCulture)} Hz is not above the {f0.ToString("R", CultureInfo.InvariantCulture)} Hz settings nominal");
            stages.Add(new UnitRelayStage(sk.Device, sk.Function, sk.Name, pickup, delay, diameter));
        }
        if (problems.Count > before) return null;
        return new ResolvedUnitProtection(u.Unit, u.ClassName, f0, nominal, stages);
    }
}
