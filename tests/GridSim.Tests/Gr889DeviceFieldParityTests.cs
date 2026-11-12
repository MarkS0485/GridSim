using System.Text.Json;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-889: DenseSparseParityTests compares, bit for bit, the bus results, every branch-flow field and the solved bus
/// types, but not the coupled-device results a solve also returns: the LCC, VSC and MTDC link results, FACTS devices,
/// static var compensators and induction machines, their notes, the sweep step and the slack reactive note. This compares
/// those too, over the same built-in and JSON cases. Each list is compared through its JSON form, which writes every
/// double in round-trip form (so -0 and 0 differ, as bits do); a null list must be null on both sides.
/// </summary>
public class Gr889DeviceFieldParityTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    public static IEnumerable<object[]> Cases =>
        new[] { "9", "14", "30", "57", "118", "300", "24", "145",
                "uk", "gb-synthetic", "intl/ireland-all-island", "intl/iberia" }.Select(n => new object[] { n });

    private static GridModel Load(string name) => name switch
    {
        "9" => IeeeCases.Case9(),
        "14" => IeeeCases.Case14(),
        "30" => IeeeCases.Case30(),
        "57" => IeeeCases.Case57(),
        "118" => IeeeCases.Case118(),
        "300" => IeeeCases.Case300(),
        "24" => IeeeCases.Case24Rts(),
        "145" => IeeeCases.Case145(),
        _ => JsonGridLoader.LoadDirectory(Path.Combine(DataDir, name)),
    };

    // NaN and infinities are written as named literals instead of throwing, so a non-finite device value is compared too.
    private static readonly JsonSerializerOptions Opt = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Opt);

    [Theory]
    [MemberData(nameof(Cases))]
    [LargeCaseInlineData(CaseSizes.GbFull, Engine.Dense, "gb-full")]
    public void Gr889_coupled_device_results_and_notes_are_identical_dense_vs_sparse(string name)
    {
        GridModel m = Load(name);
        var opt = new PowerFlowOptions { EnforceQLimits = true };
        var dense = new NewtonRaphsonPowerFlow();
        var sparse = new SparseNewtonRaphsonPowerFlow();
        PowerFlowResult d = dense.Solve(m, opt);
        PowerFlowResult s = sparse.Solve(m, opt);

        Assert.Equal(d.Status, s.Status);
        Assert.Equal(Json(d.LccLinks), Json(s.LccLinks));
        Assert.Equal(d.LccNote, s.LccNote);
        Assert.Equal(Json(d.VscLinks), Json(s.VscLinks));
        Assert.Equal(d.VscNote, s.VscNote);
        Assert.Equal(Json(d.MtdcLinks), Json(s.MtdcLinks));
        Assert.Equal(d.MtdcNote, s.MtdcNote);
        Assert.Equal(Json(d.FactsDevices), Json(s.FactsDevices));
        Assert.Equal(Json(d.StaticVarCompensators), Json(s.StaticVarCompensators));
        Assert.Equal(d.FactsNote, s.FactsNote);
        Assert.Equal(Json(d.InductionMachines), Json(s.InductionMachines));
        Assert.Equal(d.InductionNote, s.InductionNote);
        Assert.Equal(d.SweepVoltageStepPu, s.SweepVoltageStepPu);
        Assert.Equal(dense.SlackQNote, sparse.SlackQNote);   // the slack-Q note lives on the solver, not the result
        Assert.Equal(d.ConvergedLabel(), s.ConvergedLabel());
    }
}
