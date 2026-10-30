using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R073 (October 2026): asset class inputs that load but are never acted on now fail loud. A class of kind
/// synchronousMachine (no controller: a bound machine silently stayed passive), dispatchRampMwPerMin and
/// minOutputFraction (read by nothing), any key no consumer of the class's kind reads (for example a misspelling or
/// an 'armDelayS' nobody populates from), and a ProtectionAction.RestoreBranch stage (a silent no-op) are refused.
/// The bundled gb-full and gb-synthetic classes, with the unread dispatchRampMwPerMin removed, still load.
/// </summary>
public class AssetClassSupportTests
{
    private static readonly Dictionary<string, AssetClassProvenance> Prov = new()
    {
        [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test constants [VERIFY]"),
    };

    private static AssetClass Class(string name, AssetKind kind, params (string Key, double Value)[] p)
        => new(name, kind, "", p.ToDictionary(x => x.Key, x => x.Value), Prov);

    [Fact]
    public void A_synchronous_machine_class_is_refused_because_nothing_simulates_it()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[] { Class("sm", AssetKind.SynchronousMachine) }));
        Assert.Contains("'sm'", ex.Message);
        Assert.Contains("no controller", ex.Message);
        // The factory still has no controller for it, which is exactly why it must not load.
        Assert.Null(AssetControllerFactory.CreateActive(Class("sm", AssetKind.SynchronousMachine), 1, 10, 10, 50));
    }

    [Theory]
    [InlineData(AssetKind.GridFollowingInverter, "dispatchRampMwPerMin")]
    [InlineData(AssetKind.GridFormingInverter, "dispatchRampMwPerMin")]
    [InlineData(AssetKind.GridFollowingInverter, "minOutputFraction")]
    public void The_two_unread_inverter_keys_are_refused_with_their_reason(AssetKind kind, string key)
    {
        var ex = Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[] { Class("inv", kind, (key, 1.0)) }));
        Assert.Contains($"'{key}'", ex.Message);
        Assert.Contains("no controller reads it", ex.Message);
    }

    [Theory]
    [InlineData(AssetKind.GridFollowingInverter, "droopGain")]                 // a misspelling of droopGainMwPerHzPerMw
    [InlineData(AssetKind.GridFollowingInverter, AssetParams.TapStepPu)]       // a transformer key on an inverter
    [InlineData(AssetKind.GridFormingInverter, AssetParams.DroopGainMwPerHzPerMw)]   // GFM has no droop leg
    [InlineData(AssetKind.Protection, "armDelayS")]                            // no class parameter populates ArmDelayS
    [InlineData(AssetKind.Line, AssetParams.TapStepPu)]                        // a line has no tap
    [InlineData(AssetKind.Transformer, AssetParams.UfTripHz)]
    public void A_key_no_consumer_of_the_kind_reads_is_refused(AssetKind kind, string key)
    {
        var ex = Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[] { Class("c", kind, (key, 1.0)) }));
        Assert.Contains($"'{key}'", ex.Message);
        Assert.Contains("silently ignored", ex.Message);
    }

    [Fact]
    public void Every_problem_in_a_library_is_listed_in_one_exception()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[]
        {
            Class("a", AssetKind.SynchronousMachine),
            Class("b", AssetKind.GridFollowingInverter, (AssetParams.DispatchRampMwPerMin, 30.0), ("typo", 1.0)),
        }));
        Assert.Contains("'a'", ex.Message);
        Assert.Contains("'dispatchRampMwPerMin'", ex.Message);
        Assert.Contains("'typo'", ex.Message);
    }

    [Fact]
    public void A_class_using_only_keys_its_kind_reads_loads()
    {
        var lib = new AssetClassLibrary(new[]
        {
            Class("gfl", AssetKind.GridFollowingInverter, (AssetParams.DroopGainMwPerHzPerMw, 2.0), (AssetParams.PllBandwidthHz, 20.0)),
            Class("gfm", AssetKind.GridFormingInverter, (AssetParams.SynInertiaMwsPerHz, 8.0), (AssetParams.CapacityMwh, 2.0)),
            Class("tx", AssetKind.Transformer, (AssetParams.TapStepPu, 0.0125)),
            Class("ln", AssetKind.Line, (AssetParams.ThermalTauMin, 15.0)),
            Class("prot", AssetKind.Protection, (AssetParams.UfTripHz, 48.8), (AssetParams.DetectionLatencyS, 0.1)),
        });
        Assert.Equal(5, lib.Count);
    }

    [Theory]
    [InlineData("gb-full")]
    [InlineData("gb-synthetic")]
    public void The_bundled_classes_load_and_no_longer_carry_the_unread_ramp_key(string dataset)
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", dataset));
        Assert.NotNull(m.AssetDynamics);
        Assert.All(m.AssetDynamics!.Library.Classes, c => Assert.Empty(AssetClassSupport.Problems(c)));
        Assert.DoesNotContain(m.AssetDynamics.Library.Classes, c => c.Params.ContainsKey(AssetParams.DispatchRampMwPerMin));
    }

    [Fact]
    public void Loading_a_case_whose_classes_use_an_unsupported_kind_fails_loud_at_load()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-r073-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "data", "uk")))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllText(Path.Combine(dir, "asset-classes.json"), """
                [
                  { "name": "machine-2024", "kind": "synchronousMachine", "mode": "", "params": {},
                    "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "test [VERIFY]" } } },
                  { "name": "wind-2024", "kind": "gridFollowingInverter", "mode": "", "params": { "minOutputFraction": 0.1 },
                    "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "test [VERIFY]" } } }
                ]
                """);
            var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("'machine-2024'", ex.Message);
            Assert.Contains("'minOutputFraction'", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The accepted-key table must be exactly what the JSON v2 kind specs declare for the matching kind, less the
    /// refused keys, so the code and the specs cannot drift apart. v1 'transformer' maps to v2 oltc, v1 'line' to
    /// lineType (the same mapping as V2KindSpecCoverageTests). Only AssetParams constants are compared: a v2 kind
    /// also declares v2-only keys that no v1 class can carry.
    /// </summary>
    [Fact]
    public void The_accepted_keys_match_the_v2_kind_specs_less_the_refused_keys()
    {
        IReadOnlyDictionary<string, JsonElement> kinds = KindFileSet.LoadAll();
        var constants = new HashSet<string>(typeof(AssetParams).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!), StringComparer.Ordinal);

        var map = new (AssetKind Kind, string V2)[]
        {
            (AssetKind.GridFollowingInverter, "gridFollowingInverter"),
            (AssetKind.GridFormingInverter, "gridFormingInverter"),
            (AssetKind.Protection, "protection"),
            (AssetKind.Transformer, "oltc"),
            (AssetKind.Line, "lineType"),
        };
        foreach ((AssetKind kind, string v2) in map)
        {
            var declared = kinds[v2].GetProperty("params").EnumerateObject().Select(p => p.Name)
                .Where(constants.Contains)
                .Where(k => !AssetClassSupport.RefusedKeys.ContainsKey(k))
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var accepted = AssetClassSupport.AcceptedKeys(kind).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.True(declared.SequenceEqual(accepted, StringComparer.Ordinal),
                $"{kind} vs v2 {v2}: spec-only [{string.Join(", ", declared.Except(accepted))}], " +
                $"code-only [{string.Join(", ", accepted.Except(declared))}]");
        }
        Assert.Empty(AssetClassSupport.AcceptedKeys(AssetKind.SynchronousMachine));
        Assert.False(AssetClassSupport.IsSupportedKind(AssetKind.SynchronousMachine));
    }

    [Fact]
    public void The_refused_keys_are_still_declared_in_the_v2_kind_specs_with_a_refused_description()
    {
        // The kind files keep the keys (structure unchanged) and say they are refused, so a v2 author is told why.
        IReadOnlyDictionary<string, JsonElement> kinds = KindFileSet.LoadAll();
        foreach ((string v2, string key) in new[]
        {
            ("gridFollowingInverter", AssetParams.DispatchRampMwPerMin),
            ("gridFormingInverter", AssetParams.DispatchRampMwPerMin),
            ("gridFollowingInverter", AssetParams.MinOutputFraction),
        })
        {
            string description = kinds[v2].GetProperty("params").GetProperty(key).GetProperty("description").GetString()!;
            Assert.Contains("Refused at load since R073", description);
        }
    }

    [Fact]
    public void A_restore_branch_protection_stage_is_refused_rather_than_silently_doing_nothing()
    {
        var stage = new ProtectionStage(MonitoredQuantity.Frequency, ThresholdSense.Below, 49.0, 0.0,
            ProtectionAction.RestoreBranch, TargetBranch: "L1");
        Assert.Throws<NotSupportedException>(() => new ProtectionEvaluator(stage));
    }
}
