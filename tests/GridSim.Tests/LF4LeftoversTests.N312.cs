using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N312 (LF4; B3 / B5 leftovers). <c>markGated</c> marks a kind parameter whose wiring into a consumer needs the project
/// owner's sign-off. The synchronousMachine / exciter / pss / governor keys that a <c>gridsim-dynamics/2</c> file feeds
/// into the RMS study (F23, wired and merged) still said <c>true</c> while the keys B3 added for the same route said
/// <c>false</c>. They now agree: false on every key the dynamics file reads, except the three that also set the
/// Mark-gated frequency / inertia model (synchronousMachine inertiaH, governor model and droopPu). The governor kind's
/// model description now says that HYGOV is GridSim's simplified hydro governor, not the full PSS/E HYGOV (the literal is
/// kept, N284).
/// </summary>
public sealed partial class LF4LeftoversTests
{
    private static KindSpecRegistry N312Kinds() => KindSpecRegistry.LoadDirectory(V2TestSupport.KindsDir, V2TestSupport.RealSchemas());

    /// <summary>The keys a gridsim-dynamics/2 file reads, per kind, from the loader's own lists.</summary>
    private static IEnumerable<(string Kind, string Key)> N312DynamicsFileKeys()
    {
        foreach (string k in MachineDynamicsLoader.MachineRequired.Concat(MachineDynamicsLoader.MachineOptional))
            yield return ("synchronousMachine", k);
        foreach ((string kind, string[] models) in new[]
                 {
                     ("exciter", new[] { "SEXS", "IEEET1" }),
                     ("pss", new[] { "PSS1A" }),
                     ("governor", new[] { "TGOV1", "IEEEG1", "HYGOV" }),
                 })
        {
            yield return (kind, "model");
            foreach (string m in models)
                foreach (string k in MachineDynamicsLoader.RequiredFor(m).Concat(MachineDynamicsLoader.OptionalFor(m)))
                    yield return (kind, k);
        }
    }

    [Fact]
    public void N312_every_key_a_dynamics_file_reads_is_not_mark_gated_except_the_frequency_and_inertia_ones()
    {
        KindSpecRegistry kinds = N312Kinds();
        var stillGated = new HashSet<(string, string)> { ("synchronousMachine", "inertiaH"), ("governor", "model"), ("governor", "droopPu") };
        var wrong = new List<string>();
        foreach ((string kind, string key) in N312DynamicsFileKeys().Distinct())
        {
            bool gated = kinds.Get(kind).Params[key].MarkGated;
            if (gated != stillGated.Contains((kind, key))) wrong.Add($"{kind}.{key} markGated={gated}");
        }
        Assert.True(wrong.Count == 0, "markGated out of step with the dynamics-file route: " + string.Join(", ", wrong));
        // The frequency / inertia keys the file does not read stay gated.
        foreach (string key in new[] { "governorTimeConstantS", "responsiveFraction" })
            Assert.True(kinds.Get("governor").Params[key].MarkGated, key);
        foreach (string key in new[] { "xdOverXdpRatio", "xdppOverXdpRatio", "xqOverXdpRatio", "xqpOverXdpRatio", "xqppOverXdpRatio" })
            Assert.True(kinds.Get("synchronousMachine").Params[key].MarkGated, key);
    }

    [Fact]
    public void N312_the_hygov_literal_is_kept_and_documented_as_the_simplified_hydro_governor()
    {
        KindParamSpec model = N312Kinds().Get("governor").Params["model"];
        Assert.Contains("HYGOV", model.Values);   // N284: the literal stays (files and the loader use it)
        foreach (string s in new[] { "simplified hydro governor", "WITHOUT the turbine gain At", "no-load flow qNL", "turbine damping Dturb", "gate velocity limit" })
            Assert.Contains(s, model.Description, StringComparison.Ordinal);
        // None of the four missing PSS/E HYGOV terms has a key, so a full data set cannot be given and silently dropped.
        IReadOnlyDictionary<string, KindParamSpec> p = N312Kinds().Get("governor").Params;
        Assert.DoesNotContain(p.Keys, k => k.Contains("At", StringComparison.Ordinal) && k.StartsWith("hygov", StringComparison.Ordinal));
        Assert.DoesNotContain(p.Keys, k => k.StartsWith("hygov", StringComparison.Ordinal)
                                           && (k.Contains("NoLoad", StringComparison.OrdinalIgnoreCase) || k.Contains("Dturb", StringComparison.OrdinalIgnoreCase)
                                               || k.Contains("Velocity", StringComparison.OrdinalIgnoreCase)));
    }
}
