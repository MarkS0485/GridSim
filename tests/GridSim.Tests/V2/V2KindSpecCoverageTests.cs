using System.Reflection;
using System.Text.Json;
using GridSim.Core.Assets;
using Xunit;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 1 slice 3 (dream-of-json-ification): the v2 kind specs cover what v1 can say today.
///
/// <list type="bullet">
/// <item>Every <see cref="AssetParams"/> constant (the canonical v1 asset-class parameter names) is declared by
///   exactly the kinds whose consumers read it.</item>
/// <item>Every key of every bundled v1 <c>asset-classes.json</c> is known to the v2 kind its class maps to.</item>
/// </list>
///
/// <para><b>Two findings for Mark are pinned here, not hidden.</b> A failure in this file is a DATA FINDING, never
/// a reason to loosen the test. Two things the plan asks for cannot both hold on today's code and data, and each
/// is pinned EXACTLY (an entry appearing, disappearing or changing fails the test) so the question stays visible:</para>
/// <list type="number">
/// <item><b>Shared parameters.</b> The plan says each AssetParams constant "belongs to exactly one kind", but v1
///   asset classes are flat, and the same key is read by the controllers of several kinds (for example
///   <c>rocofTripHzPerS</c> by the grid-following and grid-forming inverters AND the protection controller, and
///   the v1 data sets it on all three). Since every v1 key must also be known to its own kind, those keys are
///   declared on each kind that reads them. <see cref="SharedRegister"/> lists every such constant with its exact
///   set of kinds; every other constant must belong to exactly one kind.</item>
/// <item><b>Units missing from the closed vocabulary.</b> Seven constants had a unit the vocabulary could not express
///   (MW.s/Hz, MW/Hz, MW/s per MW, V, minutes), so they were NOT declared rather than declared with a false unit.
///   Q24 (2026-10) added those five units and the seven keys are now declared
///   (<see cref="FormerUnitGapsDeclaredByQ24"/> pins where and with which unit). <see cref="UnitGapRegister"/> and
///   <see cref="DataKeysUnknownForWantOfAUnit"/> are kept, now empty, so a new gap must be registered deliberately.</item>
/// </list>
/// </summary>
public sealed class V2KindSpecCoverageTests
{
    private const string Gfl = "gridFollowingInverter", Gfm = "gridFormingInverter", Storage = "storage",
        Protection = "protection", Oltc = "oltc", LineType = "lineType";

    /// <summary>AssetParams constants read under more than one kind, with the exact set of kinds that declare them
    /// and the consumers that justify it.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> SharedRegister = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // GridFollowingInverter and GridFormingInverter both read these from their own class.
        [AssetParams.DeadbandHz] = new[] { Gfl, Gfm },
        [AssetParams.ActuateTauS] = new[] { Gfl, Gfm },
        [AssetParams.HeadroomUpFraction] = new[] { Gfl, Gfm },
        [AssetParams.HeadroomDownFraction] = new[] { Gfl, Gfm },
        // Read by nobody (R073) but authored on both inverter kinds in gb-full.
        [AssetParams.DispatchRampMwPerMin] = new[] { Gfl, Gfm },
        // DcSources.FromClass (the storage consumer) is called from both inverter controllers on their own class.
        [AssetParams.CapacityMwh] = new[] { Gfl, Gfm, Storage },
        [AssetParams.SocInit] = new[] { Gfl, Gfm, Storage },
        [AssetParams.SocMin] = new[] { Gfl, Gfm, Storage },
        [AssetParams.SocMax] = new[] { Gfl, Gfm, Storage },
        [AssetParams.BatteryChemistry] = new[] { Gfl, Gfm, Storage },
        [AssetParams.ConverterEfficiency] = new[] { Gfl, Gfm, Storage },
        [AssetParams.AmbientC] = new[] { Gfl, Gfm, Storage },
        // Both inverter controllers and the ProtectionController read these trip settings.
        [AssetParams.RocofTripHzPerS] = new[] { Gfl, Gfm, Protection },
        [AssetParams.RocofTripConfirmS] = new[] { Gfl, Gfm, Protection },
        [AssetParams.LocalRocofUpliftFactor] = new[] { Gfl, Gfm, Protection },
        [AssetParams.LvrtVoltagePu] = new[] { Gfl, Gfm, Protection },
        // GridFollowingInverter and ProtectionController.
        [AssetParams.VectorShiftTripDeg] = new[] { Gfl, Protection },
        [AssetParams.DetectionLatencyS] = new[] { Gfl, Protection },
        // TransformerDynamics (v1 'transformer' = v2 oltc) and LineThermalDynamics (v1 'line' = v2 lineType).
        [AssetParams.AmbientLoadingFraction] = new[] { Oltc, LineType },
        [AssetParams.DerateAtFullThermal] = new[] { Oltc, LineType },
        // Moved 2026-10 (Q24, authorised): two former unit gaps are read under more than one kind, so declaring them
        // (Q24) puts them here. Before: absent (they were in UnitGapRegister). After: these two rows.
        // DcSources.FromClass (the storage consumer) is called from both inverter controllers on their own class.
        [AssetParams.DcVoltageV] = new[] { Gfl, Gfm, Storage },
        // TransformerDynamics (v1 'transformer' = v2 oltc) and LineThermalDynamics (v1 'line' = v2 lineType).
        [AssetParams.ThermalTauMin] = new[] { Oltc, LineType },
    };

    /// <summary>AssetParams constants NOT declared on any kind, because the closed unit vocabulary has no unit for
    /// them: constant -> (the kinds that would declare it, the unit it needs).</summary>
    /// <remarks>Moved 2026-10 (Q24, authorised). Before: seven entries (rocofGainMwsPerHz and the two fastSlew keys
    /// on gridFollowingInverter; dcVoltageV on both inverters and storage; synInertiaMwsPerHz and dampingMwPerHz on
    /// gridFormingInverter; thermalTauMin on oltc and lineType). After: empty, because Q24 added their units
    /// (MW.s/Hz, MW/Hz, MW/s/MW, V, min) and declared the keys. The old content lives on, as a positive check, in
    /// <see cref="FormerUnitGapsDeclaredByQ24"/>.</remarks>
    internal static readonly IReadOnlyDictionary<string, (string[] Kinds, string Unit)> UnitGapRegister =
        new Dictionary<string, (string[], string)>(StringComparer.Ordinal);

    /// <summary>The (v2 kind, key) pairs of bundled v1 data that no v2 kind declares - all of them unit gaps.</summary>
    /// <remarks>Moved 2026-10 (Q24, authorised). Before: seven pairs (gridFollowingInverter fastSlewDownMwPerSPerMw,
    /// fastSlewUpMwPerSPerMw and rocofGainMwsPerHz; gridFormingInverter dampingMwPerHz and synInertiaMwsPerHz;
    /// lineType thermalTauMin; oltc thermalTauMin). After: empty: every key of every bundled v1 asset class is now
    /// known to its v2 kind.</remarks>
    internal static readonly (string Kind, string Key)[] DataKeysUnknownForWantOfAUnit = Array.Empty<(string, string)>();

    /// <summary>The seven constants that were unit gaps until Q24, with the exact kinds that now declare each and
    /// the unit each is declared with. Checked by
    /// <see cref="The_former_unit_gaps_are_declared_on_exactly_their_kinds_with_their_unit"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, (string[] Kinds, string Unit)> FormerUnitGapsDeclaredByQ24 =
        new Dictionary<string, (string[], string)>(StringComparer.Ordinal)
        {
            [AssetParams.RocofGainMwsPerHz] = (new[] { Gfl }, "MW.s/Hz"),
            [AssetParams.FastSlewUpMwPerSPerMw] = (new[] { Gfl }, "MW/s/MW"),
            [AssetParams.FastSlewDownMwPerSPerMw] = (new[] { Gfl }, "MW/s/MW"),
            [AssetParams.DcVoltageV] = (new[] { Gfl, Gfm, Storage }, "V"),
            [AssetParams.SynInertiaMwsPerHz] = (new[] { Gfm }, "MW.s/Hz"),
            [AssetParams.DampingMwPerHz] = (new[] { Gfm }, "MW/Hz"),
            [AssetParams.ThermalTauMin] = (new[] { Oltc, LineType }, "min"),
        };

    /// <summary>v1 asset-class kind strings (lower-cased) to the v2 kind, mirroring the aliases of
    /// <c>JsonGridLoader.ParseAssetKind</c>. A v1 'transformer' class is OLTC plus thermal derating
    /// (TransformerDynamics), so it maps to v2 <c>oltc</c>; a v1 'line' class is line thermal rating, v2 <c>lineType</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, string> V1KindToV2 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gridfollowinginverter"] = Gfl, ["grid-following"] = Gfl, ["gfl"] = Gfl,
        ["gridforminginverter"] = Gfm, ["grid-forming"] = Gfm, ["gfm"] = Gfm,
        ["synchronousmachine"] = "synchronousMachine", ["machine"] = "synchronousMachine",
        ["transformer"] = Oltc,
        ["line"] = LineType, ["cable"] = LineType,
        ["protection"] = Protection, ["relay"] = Protection, ["protective"] = Protection,
    };

    /// <summary>Every <c>public const string</c> of AssetParams: name -> value.</summary>
    private static Dictionary<string, string> AssetParamConstants()
        => typeof(AssetParams).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

    /// <summary>For one parameter name, the kinds that declare it (sorted).</summary>
    private static List<string> DeclaringKinds(IReadOnlyDictionary<string, JsonElement> kinds, string param)
        => kinds.Where(kv => kv.Value.GetProperty("params").TryGetProperty(param, out _))
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------------------ AssetParams coverage

    [Fact]
    public void AssetParams_still_has_the_58_constants_this_coverage_was_built_on()
    {
        // A new constant must be placed in a kind (or registered) deliberately; this count makes that visible.
        // Moved 2026-10 (A7, final batch; B4 N297, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): B4 added
        // the constant settingsNominalHz, placed on the protection kind (every other coverage test passes, so it is placed
        // deliberately); this count is the trip-wire for exactly that. Before -> after: 58 -> 59. The test name keeps its
        // original wording so its history stays traceable.
        Assert.Equal(59, AssetParamConstants().Count);
    }

    [Fact]
    public void Every_AssetParams_constant_is_declared_by_exactly_its_kinds()
    {
        IReadOnlyDictionary<string, JsonElement> kinds = KindFileSet.LoadAll();
        var failures = new List<string>();
        foreach ((string field, string key) in AssetParamConstants())
        {
            List<string> actual = DeclaringKinds(kinds, key);
            string[] expected =
                SharedRegister.TryGetValue(key, out string[]? shared) ? shared.OrderBy(k => k, StringComparer.Ordinal).ToArray()
                : UnitGapRegister.ContainsKey(key) ? Array.Empty<string>()
                : actual.Count == 1 ? actual.ToArray()            // the ordinary case: exactly one kind, whichever it is
                : new[] { "<exactly one kind>" };
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                failures.Add($"AssetParams.{field} (\"{key}\"): declared by [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]");
        }
        Assert.True(failures.Count == 0, "AssetParams coverage findings:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void The_registers_name_real_constants_and_do_not_overlap()
    {
        var values = AssetParamConstants().Values.ToHashSet(StringComparer.Ordinal);
        foreach (string k in SharedRegister.Keys) Assert.Contains(k, values);
        foreach (string k in UnitGapRegister.Keys) Assert.Contains(k, values);
        Assert.Empty(SharedRegister.Keys.Intersect(UnitGapRegister.Keys, StringComparer.Ordinal));
        foreach ((string k, string[] ks) in SharedRegister)
        {
            Assert.True(ks.Length >= 2, $"{k}: a shared constant needs at least two kinds");
            foreach (string kind in ks) Assert.Contains(kind, KindFileSet.ParameterKinds);
        }
        // Moved 2026-10 (Q24, authorised): SharedRegister 20 -> 22 (dcVoltageV and thermalTauMin, now declared, are
        // read under several kinds) and UnitGapRegister 7 -> 0 (every gap closed by the five new units).
        Assert.Equal(22, SharedRegister.Count);
        Assert.Empty(UnitGapRegister);
    }

    [Fact]
    public void The_former_unit_gaps_are_declared_on_exactly_their_kinds_with_their_unit()
    {
        // Added 2026-10 (Q24). Each of the seven keys is declared on exactly the kinds that read it, with exactly the
        // unit the old register said it needs; that unit is in the closed vocabulary; and no kind description still
        // calls that unit missing.
        IReadOnlyDictionary<string, JsonElement> kinds = KindFileSet.LoadAll();
        Assert.Equal(7, FormerUnitGapsDeclaredByQ24.Count);
        foreach ((string key, (string[] ks, string unit)) in FormerUnitGapsDeclaredByQ24)
        {
            Assert.Contains(unit, KindFileSet.UnitVocabulary);
            Assert.Equal(ks.OrderBy(k => k, StringComparer.Ordinal), DeclaringKinds(kinds, key));
            foreach (string kind in ks)
            {
                JsonElement p = kinds[kind].GetProperty("params").GetProperty(key);
                Assert.Equal("number", p.GetProperty("type").GetString());
                Assert.Equal(unit, p.GetProperty("unit").GetString());
                Assert.DoesNotContain("NOT DECLARED (finding for Mark): the closed unit vocabulary has no unit for " + unit,
                    kinds[kind].GetProperty("description").GetString()!, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Each_unit_gap_is_recorded_in_the_description_of_every_kind_that_should_declare_it()
    {
        // The finding is written where a reader of the kind file will see it, naming the parameter.
        IReadOnlyDictionary<string, JsonElement> kinds = KindFileSet.LoadAll();
        foreach ((string key, (string[] ks, string unit)) in UnitGapRegister)
        {
            Assert.DoesNotContain(unit, KindFileSet.UnitVocabulary);   // still a real gap
            foreach (string kind in ks)
                Assert.Contains(key, kinds[kind].GetProperty("description").GetString()!, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------ bundled v1 data coverage

    private static List<string> BundledAssetClassFiles()
        => Directory.EnumerateFiles(Path.Combine(KindFileSet.RepoRoot(), "data"), "asset-classes.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();

    [Fact]
    public void The_bundled_v1_asset_class_files_are_found()
    {
        // Guards against a silently empty search: gb-full and gb-synthetic ship asset classes today.
        List<string> files = BundledAssetClassFiles();
        Assert.True(files.Count >= 2, "expected at least the gb-full and gb-synthetic asset-classes.json, found: " + string.Join(", ", files));
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("data/gb-full/asset-classes.json", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Replace('\\', '/').EndsWith("data/gb-synthetic/asset-classes.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_key_of_every_bundled_v1_asset_class_is_known_to_its_kind_apart_from_the_pinned_unit_gaps()
    {
        IReadOnlyDictionary<string, JsonElement> kinds = KindFileSet.LoadAll();
        var assetParamValues = AssetParamConstants().Values.ToHashSet(StringComparer.Ordinal);
        var unknown = new SortedSet<(string, string)>();
        var notAssetParams = new List<string>();
        int classes = 0, keys = 0;

        foreach (string file in BundledAssetClassFiles())
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), KindFileSet.ReadOptions);
            foreach (JsonElement cls in doc.RootElement.EnumerateArray())
            {
                classes++;
                string name = cls.GetProperty("name").GetString()!;
                string v1Kind = cls.GetProperty("kind").GetString()!;
                Assert.True(V1KindToV2.TryGetValue(v1Kind.ToLowerInvariant(), out string? v2Kind),
                    $"{file}: class '{name}' has v1 kind '{v1Kind}' with no v2 mapping");
                Assert.True(kinds.ContainsKey(v2Kind!), $"v2 kind '{v2Kind}' has no kind file");
                if (!cls.TryGetProperty("params", out JsonElement ps)) continue;
                foreach (JsonProperty p in ps.EnumerateObject())
                {
                    keys++;
                    if (!assetParamValues.Contains(p.Name)) notAssetParams.Add($"{name}.{p.Name}");
                    if (!kinds[v2Kind!].GetProperty("params").TryGetProperty(p.Name, out _))
                        unknown.Add((v2Kind!, p.Name));
                }
            }
        }

        Assert.True(classes >= 15 && keys >= 100, $"suspiciously little v1 data read: {classes} classes, {keys} keys");
        // Every v1 key is a canonical AssetParams name (no misspelling slipped into the data).
        Assert.True(notAssetParams.Count == 0, "v1 keys that are not AssetParams names: " + string.Join(", ", notAssetParams));
        // Every unknown key is a unit gap, and the set of unknown keys is EXACTLY the pinned finding.
        foreach ((string kind, string key) in unknown)
            Assert.True(UnitGapRegister.TryGetValue(key, out var gap) && gap.Kinds.Contains(kind),
                $"DATA FINDING: v1 key '{key}' on a {kind} class is unknown to the v2 kind and is not a registered unit gap");
        Assert.Equal(
            DataKeysUnknownForWantOfAUnit.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Kind}.{x.Key}"),
            unknown.Select(x => $"{x.Item1}.{x.Item2}"));
    }

    [Fact]
    public void The_v1_kind_mapping_mirrors_every_alias_the_loader_accepts()
    {
        // The loader's alias switch is private, so its source is the reference (as other CLI/loader contract tests do).
        string src = File.ReadAllText(Path.Combine(KindFileSet.RepoRoot(), "src", "GridSim.Core", "IO", "Json", "JsonGridLoader.cs"));
        int start = src.IndexOf("private static AssetKind ParseAssetKind", StringComparison.Ordinal);
        Assert.True(start >= 0, "ParseAssetKind not found in JsonGridLoader.cs");
        string body = src[start..src.IndexOf("};", start, StringComparison.Ordinal)];
        var aliases = System.Text.RegularExpressions.Regex.Matches(body, "\"([a-z-]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(aliases.OrderBy(a => a, StringComparer.Ordinal), V1KindToV2.Keys.OrderBy(a => a, StringComparer.Ordinal));
    }
}
