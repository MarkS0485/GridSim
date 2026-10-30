using System.Text.Json;
using Xunit;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 1 slice 3 (dream-of-json-ification): every <c>codeDefaults</c> entry of every kind file points at the C#
/// value GridSim uses TODAY, and states that value exactly. This is the anti-drift contract between the v2 kind
/// specs and the code, in the style of <c>Ws13CoreConstantsAntiDriftTests</c>: if a constant moves, or a literal is
/// edited, renamed or duplicated, this test names the entry that no longer matches.
///
/// <para><b>Reference forms</b> (full rules on <see cref="CodeDefaultRefResolver"/>):</para>
/// <list type="bullet">
/// <item><c>member:&lt;Assembly&gt;:&lt;Full.Type.Name&gt;.&lt;Member&gt;[.&lt;Member&gt;|[Prop=Value]...]</c> - const, static field
///   or static property by reflection, optionally walking into instance members and selecting one list element.</item>
/// <item><c>instance:&lt;Assembly&gt;:&lt;Full.Type.Name&gt;.&lt;Member&gt;</c> - the value on a freshly constructed object
///   (property initialisers, record init defaults).</item>
/// <item><c>param:&lt;Assembly&gt;:&lt;Full.Type.Name&gt;:&lt;Method or .ctor&gt;:&lt;parameter&gt;</c> - a parameter's compile-time
///   default (record positional defaults, optional arguments).</item>
/// <item><c>source:&lt;repo-relative path&gt;:&lt;exact snippet&gt;</c> - a literal inside a method body or in the CLI: the
///   snippet must occur exactly once in the file and its last literal must be exactly the value.</item>
/// </list>
///
/// <para><b>Where the code disagrees with itself</b> each consumer keeps its own entry; values are never merged.
/// The disagreements are pinned in <see cref="The_inconsistency_register_is_pinned_value_for_value"/> so a silent
/// "fix" in either direction is caught and must be ruled on by Mark.</para>
/// </summary>
public sealed class V2KindSpecCodeDefaultTests
{
    /// <summary>One row per kind file.</summary>
    [Theory]
    [MemberData(nameof(KindFileSet.KindNames), MemberType = typeof(KindFileSet))]
    public void Every_code_default_resolves_and_equals_the_code_value_exactly(string kind)
    {
        JsonElement root = KindFileSet.Load(kind);
        var failures = new List<string>();
        int checkedCount = 0;
        foreach (JsonProperty p in root.GetProperty("params").EnumerateObject())
            foreach (JsonElement c in p.Value.GetProperty("codeDefaults").EnumerateArray())
            {
                string reference = c.GetProperty("ref").GetString()!;
                string? why = CodeDefaultRefResolver.Mismatch(reference, c.GetProperty("value"));
                if (why is not null)
                    failures.Add($"{kind}.{p.Name} [{c.GetProperty("consumer").GetString()}] {reference}\n      -> {why}");
                checkedCount++;
            }
        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedCount} code defaults in {kind} do not match the code:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void Every_kind_with_code_in_GridSim_today_carries_code_defaults_and_the_total_is_pinned()
    {
        // A kind that silently lost all its entries (a bad regeneration, a merge) would still pass the per-entry test.
        // The counts are pinned exactly; adding entries is fine but must update this line deliberately.
        var counts = KindFileSet.LoadAll().ToDictionary(
            kv => kv.Key,
            kv => kv.Value.GetProperty("params").EnumerateObject().Sum(p => p.Value.GetProperty("codeDefaults").GetArrayLength()));
        foreach ((string kind, int n) in counts) Assert.True(n > 0, $"{kind} has no code defaults at all");
        // Moved 2026-10 (Q24, authorised): 576 -> 590. Declaring the seven former unit-gap keys added fourteen
        // entries: gridFollowingInverter 3 (rocofGainMwsPerHz, fastSlewUp/DownMwPerSPerMw), gridFormingInverter 8
        // (synInertiaMwsPerHz and dampingMwPerHz, four consumers each), storage 1 (dcVoltageV), oltc 1 and
        // lineType 1 (thermalTauMin). The inverter dcVoltageV entries carry none: their default lives on storage.
        // Moved 2026-10 (B4 R137, Ruling R1: Mark delegated EE calls 2026-10-04; source EREC G99 Issue 2 s.10.6.7.1 Table
        // 10.1): 590 -> 582. ProtectionController no longer reads an absent dwell as 0 s (it refuses the class), so the
        // eight "an absent dwell reads as 0 s (local P default)" entries of protection.kind.json are gone
        // (rocofTripConfirmS, dfDtTripDwellS, ufTripDwellS, ofTripDwellS, uvTripDwellS, ovTripDwellS, overloadTripDwellS,
        // lfddDwellS).
        // Moved 2026-10 (B4 N297, Ruling R1; source: B2's band convention, FrequencyBands): 582 -> 583. The new protection
        // key settingsNominalHz (the nominal a class's frequency settings are written for) carries one code default,
        // AssetParamDefaults.ProtectionSettingsNominalHz = 50 Hz.
        // Moved 2026-10 (B5 N309, Ruling R1: Mark delegated EE calls 2026-10-04; ruled by main: "add codeDefaults entries for
        // the new B3 kind parameters so the register is complete"; source: the constructor defaults in Tgov1, Ieeeg1,
        // HydroGovernor, SubtransientInit.MachineData and SubtransientMachine.Parameters): 583 -> 594. B3 (F23) added the
        // keys without entries although each has a code default; eleven entries, one per consumer: governor valveMaxPu x3
        // (Tgov1 valveMaxPu, Ieeeg1 valveMaxPu, HydroGovernor gateMaxPu, all +Infinity), ieeeg1K2 / K4 / K6 / K8 x1 each
        // (0), ieeeg1UoPuPerS x1 (+Infinity), ieeeg1UcPuPerS x1 (-Infinity); synchronousMachine xlPu x2 (MachineData.Xl and
        // SubtransientMachine.Parameters.LeakageReactance, 0). The time constants, HYGOV keys and saturation points carry
        // none (required by the dynamics loader, or a null curve, which is not a number). Pinned row by row in
        // B5ConverterModelsTests.N309.
        // Moved 2026-10 (B5 R138, Ruling R1: Mark delegated EE calls 2026-10-04; source: R138 in FULL-REVIEW-2026-10.md, one
        // PLL default for the three converter models): 594 -> 595. LinearInverterModel.ForGfl now reads the class's
        // pllDampingRatio with two fallbacks, the one PLL default (0.707) when its bandwidth is a PLL's and the critically
        // damped delay stand-in (1.0) otherwise, so gridFollowingInverter.pllDampingRatio gains one entry (the PLL branch).
        // Moved 2026-10 (D1, final batch; B5 R138 / D2, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): 595 ->
        // 591. The kind format allows one entry per reference; B5 / D2 left repeated references, which the structure test
        // refused: gridFollowingInverter.pllBandwidthHz 3 x ConverterDefaults.PllBandwidthHz, pllDampingRatio 2 x
        // ConverterDefaults.PllDampingRatio, gridCodeProfile.nominalFrequencyHz 2 x FrequencyLimits.NominalHz. Each set is
        // now one entry naming every consumer (4 -> 2, 4 -> 3, 6 -> 5): 595 - 4 = 591. No value changed.
        Assert.Equal(591, counts.Values.Sum());
    }

    // ------------------------------------------------------------------------------ the Q24 keys

    /// <summary>(kind, param, consumer fragment, value) for every code default of the seven keys Q24 declared. Each
    /// row is today's fallback in the named consumer when the v1 class does not set the key.</summary>
    public static TheoryData<string, string, string, double> Q24KeyRows() => new()
    {
        { "gridFollowingInverter", "rocofGainMwsPerHz", "GridFollowingInverter controller", 0.0 },
        { "gridFollowingInverter", "fastSlewUpMwPerSPerMw", "GridFollowingInverter controller", 0.0 },
        { "gridFollowingInverter", "fastSlewDownMwPerSPerMw", "GridFollowingInverter controller", 0.0 },
        { "gridFormingInverter", "synInertiaMwsPerHz", "GridFormingInverter controller", 0.0 },
        { "gridFormingInverter", "synInertiaMwsPerHz", "FormingAggregate.FromFleet", 0.0 },
        { "gridFormingInverter", "synInertiaMwsPerHz", "GridFormingImpedance", 0.0 },
        { "gridFormingInverter", "synInertiaMwsPerHz", "LinearInverterModel.ForGfm", 0.0 },
        { "gridFormingInverter", "dampingMwPerHz", "GridFormingInverter controller", 0.0 },
        { "gridFormingInverter", "dampingMwPerHz", "FormingAggregate.FromFleet", 0.0 },
        { "gridFormingInverter", "dampingMwPerHz", "GridFormingImpedance", 0.0 },
        { "gridFormingInverter", "dampingMwPerHz", "LinearInverterModel.ForGfm", 0.0 },
        { "storage", "dcVoltageV", "DcSources.FromClass", 1000.0 },
        { "oltc", "thermalTauMin", "TransformerDynamics", 20.0 },
        { "lineType", "thermalTauMin", "LineThermalDynamics", 15.0 },
    };

    [Theory]
    [MemberData(nameof(Q24KeyRows))]
    public void The_Q24_keys_carry_exactly_the_code_defaults_of_their_consumers(string kind, string param, string consumerFragment, double value)
    {
        // Added 2026-10 (Q24). Same check as the inconsistency register: exactly one entry names the consumer, it
        // carries this value, and the code agrees with it bit for bit.
        JsonElement p = KindFileSet.Load(kind).GetProperty("params").GetProperty(param);
        var hits = p.GetProperty("codeDefaults").EnumerateArray()
            .Where(c => c.GetProperty("consumer").GetString()!.Contains(consumerFragment, StringComparison.Ordinal))
            .ToList();
        Assert.True(hits.Count == 1, $"{kind}.{param}: {hits.Count} entries name consumer '{consumerFragment}', expected exactly one");
        Assert.Equal(value, hits[0].GetProperty("value").GetDouble());
        Assert.Null(CodeDefaultRefResolver.Mismatch(hits[0].GetProperty("ref").GetString()!, hits[0].GetProperty("value")));
    }

    [Fact]
    public void The_Q24_rows_cover_every_code_default_of_the_seven_keys()
    {
        // Added 2026-10 (Q24). No entry of the seven keys escapes the rows above: the per-key entry counts match.
        var rows = Q24KeyRows().Select(r => ((string)r[0], (string)r[1])).ToList();
        foreach (var g in rows.GroupBy(r => r))
            Assert.Equal(g.Count(), KindFileSet.Load(g.Key.Item1).GetProperty("params").GetProperty(g.Key.Item2)
                .GetProperty("codeDefaults").GetArrayLength());
        // The two inverter dcVoltageV entries are empty on purpose (the default is recorded on storage).
        Assert.Equal(0, KindFileSet.Load("gridFollowingInverter").GetProperty("params").GetProperty("dcVoltageV").GetProperty("codeDefaults").GetArrayLength());
        Assert.Equal(0, KindFileSet.Load("gridFormingInverter").GetProperty("params").GetProperty("dcVoltageV").GetProperty("codeDefaults").GetArrayLength());
    }

    // ------------------------------------------------------------------------------ the inconsistency register

    /// <summary>(kind, param, consumer fragment, value). Every row is a value the code holds TODAY for the same
    /// quantity, where another consumer holds a different value. Rows for one quantity are never merged.</summary>
    public static TheoryData<string, string, string, double> InconsistencyRows() => new()
    {
        // inertia H by fuel: GDA FuelMap vs SyntheticGb fleet (nuclear 5.0 vs 4.0)
        { "synchronousMachine", "inertiaH", "FuelMap.Classify: nuclear", 5.0 },
        { "synchronousMachine", "inertiaH", "SyntheticGb transmission fleet: Torness Nuclear", 4.0 },
        // GFM virtual reactance 0.20 (impedance placeholder) vs 0.15 (RMS record)
        { "gridFormingInverter", "virtualReactancePu", "GridFormingImpedance placeholder", 0.20 },
        { "gridFormingInverter", "virtualReactancePu", "RmsConverterParameters VirtualReactancePu", 0.15 },
        // governor droop 0.05 (frequency model) vs 0.04 (presentation torque story)
        { "governor", "droopPu", "DynamicsConfig.DroopPu", 0.05 },
        { "governor", "droopPu", "EnergyEconomicsConstants.GovernorDroopFraction", 0.04 },
        // OLTC tap step 0.0125 vs 0.00625 (SyntheticGb GSP SGTs)
        { "oltc", "tapStepPu", "Oltc.DefaultStepPu", 0.0125 },
        { "oltc", "tapStepPu", "SyntheticGb GSP supergrid transformer OLTC", 0.00625 },
        // OLTC deadband 0.01 vs 0.015 (SyntheticGb primaries)
        { "oltc", "tapDeadbandPu", "Model Oltc record default", 0.01 },
        { "oltc", "tapDeadbandPu", "SyntheticGb 132/33 kV primary OLTC", 0.015 },
        // load power factor 0.97 (JSON) vs 0.98 (GDA)
        { "load", "powerFactor", "JsonDefaults.LoadPowerFactor", 0.97 },
        { "load", "powerFactor", "GdaBuildDefaults.DemandPowerFactor", 0.98 },
        // PLL bandwidth 20 Hz (EMT) vs 5 Hz (impedance, small-signal).
        // Moved 2026-10 (B5 R138, Ruling R1: Mark delegated EE calls 2026-10-04; source: R138 in FULL-REVIEW-2026-10.md, "use
        // shared constants in all three models"; the 20 Hz choice and its literature are on ConverterDefaults): ruled on.
        // The three models share ConverterDefaults.PllBandwidthHz = 20 Hz, so the impedance (5 -> 20) and small-signal
        // (5 -> 20) rows are no longer a disagreement and leave the register (the guard below refuses rows of one value);
        // the EMT row stays to pin the agreed value. B5ConverterModelsTests.R138 pins all four entries at 20 Hz.
        { "gridFollowingInverter", "pllBandwidthHz", "EMT average-value converter", 20.0 },
        // PLL damping 0.707 vs 1.0 (small-signal).
        // Moved 2026-10 (B5 R138, Ruling R1; same source): ruled on. A PLL is damped at 0.707 in every model now; the 1.0
        // that remains belongs only to the small-signal stand-in for a detection delay (a delay has no overshoot), a
        // different quantity, so the two rows stay as a documented pair rather than a disagreement. Values unchanged.
        { "gridFollowingInverter", "pllDampingRatio", "ConverterDefaults.PllDampingRatio", 0.707 },
        { "gridFollowingInverter", "pllDampingRatio", "LinearInverterModel.ForGfl small-signal estimator", 1.0 },
        // capacity factor: FuelMap vs Dispatcher (a sample; the full list is in the kind files and NOTES)
        { "synchronousMachine", "capacityFactor", "FuelMap.CapacityFactor: ccgt", 0.45 },
        { "synchronousMachine", "capacityFactor", "Dispatcher.DefaultCapacityFactor: ccgt / gas", 0.50 },
        { "synchronousMachine", "capacityFactor", "FuelMap.CapacityFactor: any other type (fallback)", 0.30 },
        { "synchronousMachine", "capacityFactor", "Dispatcher.DefaultCapacityFactor: any other type (fallback)", 0.40 },
        { "gridFollowingInverter", "capacityFactor", "FuelMap.CapacityFactor: solar", 0.10 },
        { "gridFollowingInverter", "capacityFactor", "Dispatcher.DefaultCapacityFactor: solar / pv", 0.15 },
        { "gridFollowingInverter", "capacityFactor", "SyntheticGb embedded generation: solar", 0.25 },
        { "storage", "capacityFactor", "FuelMap.CapacityFactor: storage", 0.05 },
        { "storage", "capacityFactor", "Dispatcher.DefaultCapacityFactor: pumped", 0.20 },
        // RoCoF confirm delay: grid-following 0.5 s vs grid-forming 1.0 s fallback.
        // Moved 2026-10 (B4 R137, Ruling R1: Mark delegated EE calls 2026-10-04; source EREC G99 Issue 2 s.10.6.7.1 Table
        // 10.1, LoM (RoCoF) time delay 0.5 s): ruled on. Both inverter kinds now fall back to the one shared
        // AssetParamDefaults.RocofTripConfirmS = 0.5 s (grid-forming 1.0 -> 0.5); a protection class must author it
        // (its old 0 s fall-back entry left protection.kind.json). The two rows stay so the agreement is pinned.
        { "gridFollowingInverter", "rocofTripConfirmS", "GridFollowingInverter controller", 0.5 },
        { "gridFormingInverter", "rocofTripConfirmS", "GridFormingInverter controller", 0.5 },
        // frequency deadband: grid-following 0.015 Hz vs grid-forming 0 fallback
        { "gridFollowingInverter", "deadbandHz", "GridFollowingInverter controller", 0.015 },
        { "gridFormingInverter", "deadbandHz", "GridFormingInverter controller", 0.0 },
        // rating derate at full thermal state: line 0.1 vs transformer 0.0 fallback
        { "lineType", "derateAtFullThermal", "LineThermalDynamics", 0.1 },
        { "oltc", "derateAtFullThermal", "TransformerDynamics", 0.0 },
        // RoCoF trip setting: generic relay 1.0 Hz/s vs the legacy grid-code limit 0.125 Hz/s
        { "protection", "rocofTripHzPerS", "ProtectionSettings generic 81R relay", 1.0 },
        { "gridCodeProfile", "rocofLimitHzPerS", "FrequencyLimits.RocofLimitLegacyHzPerS", 0.125 },
    };

    [Theory]
    [MemberData(nameof(InconsistencyRows))]
    public void The_inconsistency_register_is_pinned_value_for_value(string kind, string param, string consumerFragment, double value)
    {
        // Exactly one entry of the parameter names this consumer, it carries this value, and the code agrees.
        JsonElement p = KindFileSet.Load(kind).GetProperty("params").GetProperty(param);
        var hits = p.GetProperty("codeDefaults").EnumerateArray()
            .Where(c => c.GetProperty("consumer").GetString()!.Contains(consumerFragment, StringComparison.Ordinal))
            .ToList();
        Assert.True(hits.Count == 1, $"{kind}.{param}: {hits.Count} entries name consumer '{consumerFragment}', expected exactly one");
        Assert.Equal(value, hits[0].GetProperty("value").GetDouble());
        Assert.Null(CodeDefaultRefResolver.Mismatch(hits[0].GetProperty("ref").GetString()!, hits[0].GetProperty("value")));
    }

    [Fact]
    public void Each_registered_disagreement_really_has_two_different_values_on_one_parameter()
    {
        // Guards the register itself: a "disagreement" whose rows all carry one value would be a false alarm.
        var byParam = InconsistencyRows()
            .Select(r => ((string)r[0], (string)r[1], (double)r[3]))
            .GroupBy(r => (r.Item1, r.Item2))
            .Where(g => g.Count() > 1);
        foreach (var g in byParam)
            Assert.True(g.Select(r => r.Item3).Distinct().Count() > 1, $"{g.Key}: registered rows do not disagree");
    }

    // ------------------------------------------------------------------------------ the resolver bites

    [Fact]
    public void Each_reference_form_resolves_a_known_value_exactly()
    {
        Assert.Null(Resolve("member:GridSim.Core:GridSim.Core.Model.Oltc.DefaultStepPu", "0.0125"));
        Assert.Null(Resolve("member:GridSim.Gda:GridSim.Gda.GdaBuildDefaults.DemandPowerFactor", "0.98"));
        Assert.Null(Resolve("member:GridSim.Core:GridSim.Core.Storage.BatteryChemistryLibrary.LeadAcid.Ageing.DodExponent", "1.6"));
        Assert.Null(Resolve("member:GridSim.Core:GridSim.Core.Harmonics.HarmonicSourceLibrary.EvCharger[Order=9].MagnitudePercent", "3.0"));
        Assert.Null(Resolve("instance:GridSim.Core:GridSim.Core.Protection.ProtectionSettings.VectorShiftTripDeg", "12.0"));
        Assert.Null(Resolve("instance:GridSim.Core:GridSim.Core.PowerFlow.PowerFlowOptions.MaxIterations", "30"));
        Assert.Null(Resolve("instance:GridSim.Core:GridSim.Core.PowerFlow.PowerFlowOptions.EnforceQLimits", "true"));
        Assert.Null(Resolve("param:GridSim.Core:GridSim.Core.Dynamics.Models.RmsConverterParameters:.ctor:VirtualReactancePu", "0.15"));
        Assert.Null(Resolve("member:GridSim.Core:GridSim.Core.IO.Json.JsonDefaults.GeneratorType", "\"ccgt\""));
        // Moved 2026-10 (END-PASS CF3 row 1, end pass; N345, every kind-spec literal named, Ruling R1 and Mark's go on the
        // end-pass moves 2026-10-11): N345 named both literals (DefaultTapStepPu, DefaultRocofTripHzPerS), so the two
        // cls.Param snippets no longer exist (observed: "the snippet occurs 0 times in src/GridSim.Core/Ass...",
        // 1.95.273-275 runs). The source: form itself still exists in the resolver, so the two rows keep testing it on the
        // same default in the same file, now quoted at its named declaration (each snippet occurs exactly once; the last
        // literal 0.0125 and the ending double.PositiveInfinity are unchanged). Before -> after:
        // "...TransformerDynamics.cs:cls.Param(AssetParams.TapStepPu, 0.0125)" -> "...TransformerDynamics.cs:internal const
        // double DefaultTapStepPu = 0.0125"; "...GridFollowingInverter.cs:cls.Param(AssetParams.RocofTripHzPerS,
        // double.PositiveInfinity" -> "...GridFollowingInverter.cs:internal const double DefaultRocofTripHzPerS =
        // double.PositiveInfinity". Expected values unchanged (null); derived from the resolver code, not observed.
        Assert.Null(Resolve("source:src/GridSim.Core/Assets/Passive/TransformerDynamics.cs:internal const double DefaultTapStepPu = 0.0125", "0.0125"));
        Assert.Null(Resolve("source:src/GridSim.Core/Assets/GridFollowingInverter.cs:internal const double DefaultRocofTripHzPerS = double.PositiveInfinity", "\"+Infinity\""));
    }

    [Fact]
    public void A_value_one_ulp_away_is_a_mismatch_because_comparison_is_exact()
    {
        string up = Math.BitIncrement(0.0125).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string down = Math.BitDecrement(0.0125).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(Resolve("member:GridSim.Core:GridSim.Core.Model.Oltc.DefaultStepPu", up));
        Assert.NotNull(Resolve("member:GridSim.Core:GridSim.Core.Model.Oltc.DefaultStepPu", down));
        // Moved 2026-10 (CF3-extra, end pass; N345 named the literal DefaultTapStepPu, main's decision under Ruling R1 and
        // Mark's go on the end-pass moves 2026-10-11): after N345 the old snippet occurred 0 times, so this assertion
        // passed vacuously (refused for "occurs 0 times", not for the one-ulp value). It now quotes the same default at its
        // named declaration, which occurs exactly once, so the one-ulp comparison runs again; no value or tolerance
        // loosened. Before -> after: snippet "cls.Param(AssetParams.TapStepPu, 0.0125)" -> "internal const double
        // DefaultTapStepPu = 0.0125"; value (0.0125 one ulp up) and Assert.NotNull unchanged (the refusal reason is now
        // "the snippet's last literal 0.0125 is 0.0125, not <up>": derived from the resolver code, not observed).
        Assert.NotNull(Resolve("source:src/GridSim.Core/Assets/Passive/TransformerDynamics.cs:internal const double DefaultTapStepPu = 0.0125", up));
    }

    [Theory]
    // the wrong value, and a value that is only a substring of the literal (0.3 inside 0.35)
    // Moved 2026-10 (END-PASS CF3 row 2, end pass; N345, every SyntheticGb network literal named, Ruling R1 and Mark's go
    // on the end-pass moves 2026-10-11): the branches.Add snippet no longer exists (observed: "the snippet occurs 0 times
    // in src/GridSim..." instead of "last literal", 1.95.273-275 runs). The row's purpose (0.3 must not match 0.35)
    // still exists, so it quotes the same literal at its new named declaration, which occurs exactly once. Before ->
    // after: snippet "branches.Add(new Branch(a, b, 0.0003, 0.003, 0.35" -> "private const double Ring400BPu = 0.35";
    // value "0.3" and fragment "last literal" unchanged (derived from the resolver code, not observed).
    [InlineData("source:src/GridSim.Core/Synthetic/SyntheticGb.cs:private const double Ring400BPu = 0.35", "0.3", "last literal")]
    // a snippet that occurs more than once must be refused, even though its value is right
    [InlineData("source:src/GridSim.Core/Assets/GridFormingInverter.cs:cls.Param(AssetParams.", "0.0", "not exactly once")]
    // a snippet that does not occur at all
    [InlineData("source:src/GridSim.Core/Assets/GridFormingInverter.cs:cls.Param(AssetParams.DeadbandHz, 0.015)", "0.015", "occurs 0 times")]
    [InlineData("source:src/GridSim.Core/NoSuchFile.cs:x = 1", "1", "not found")]
    [InlineData("source:../outside.cs:x = 1", "1", "repo-relative")]
    // the CLI is never reflected; unknown assemblies, types and members fail loud
    // Moved 2026-10 (END-PASS CF3 row 2, end pass; N345, main's ruling that the resolver reads the built CLI, Ruling R1
    // and Mark's go on the end-pass moves 2026-10-11): the CLI is now reflected, so the right value resolves (observed:
    // Assert.NotNull failed, value null, 1.95.273-275 runs) and "the CLI is never reflected" no longer holds. The row
    // keeps this test's purpose (a bad CLI reference is reported) with a wrong value. Before -> after: value "0.3",
    // fragment "source: form" -> value "0.31", fragment "expected 0.31 but the code holds 0.3" (observed: the same
    // reference and value pass with this fragment in Cf3OpenCodeFindingsTests.N345_the_resolver_reads_the_built_cli,
    // green in the 1.95.273-275 runs).
    [InlineData("member:GridSim.Cli:GridSim.Cli.CliDefaults.SyntheticXdpPu", "0.31", "expected 0.31 but the code holds 0.3")]
    [InlineData("member:GridSim.Web:GridSim.Web.Anything.X", "1", "not one the tests reference")]
    [InlineData("member:GridSim.Core:GridSim.Core.Model.Oltc.NoSuchMember", "1", "no static field or property")]
    [InlineData("member:GridSim.Core:No.Such.Type.X", "1", "names a type")]
    // a selector must pick exactly one element
    [InlineData("member:GridSim.Core:GridSim.Core.Harmonics.HarmonicSourceLibrary.EvCharger[Order=2].MagnitudePercent", "1", "matched 0 elements")]
    // a parameter without a default, and an unknown parameter
    [InlineData("param:GridSim.Core:GridSim.Core.Model.Oltc:.ctor:Transformer", "1", "no default value")]
    [InlineData("param:GridSim.Core:GridSim.Core.Model.Oltc:.ctor:Nope", "1", "0 overloads")]
    // types must agree: a number is not a string, an infinity needs a real infinity
    [InlineData("member:GridSim.Core:GridSim.Core.IO.Json.JsonDefaults.GeneratorType", "1", "expected the number")]
    [InlineData("member:GridSim.Core:GridSim.Core.Model.Oltc.DefaultStepPu", "\"+Infinity\"", "expected +Infinity")]
    [InlineData("nonsense:GridSim.Core:X", "1", "unknown reference form")]
    public void Bad_references_and_wrong_values_are_reported(string reference, string valueJson, string fragment)
    {
        string? why = Resolve(reference, valueJson);
        Assert.NotNull(why);
        Assert.Contains(fragment, why, StringComparison.Ordinal);
    }

    private static string? Resolve(string reference, string valueJson)
    {
        using JsonDocument v = JsonDocument.Parse(valueJson);
        return CodeDefaultRefResolver.Mismatch(reference, v.RootElement);
    }
}
