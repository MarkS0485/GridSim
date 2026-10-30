using System;
using System.IO;
using System.Linq;
using GridSim.Core.IO;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Telemetry;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression tests for the IO / protocol defects from the adversarial review (Wave 6), plus the two
/// test-gap findings (JsonGridLoader's asset branch and MatpowerUnitNormalizer had zero coverage).
/// </summary>
public class Wave6IoTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    [Fact] // test-gap: the asset-classes/asset-bindings/protections branch of LoadDirectory was never exercised
    public void JsonGridLoader_loads_the_asset_dynamics_branch_from_gb_synthetic()
    {
        string dir = Path.Combine(DataDir, "gb-synthetic");
        Assert.True(File.Exists(Path.Combine(dir, "asset-classes.json")), $"bundled file missing: {Path.Combine(dir, "asset-classes.json")}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently

        GridModel m = JsonGridLoader.LoadDirectory(dir);

        Assert.NotNull(m.AssetDynamics);
        Assert.True(m.AssetDynamics!.Library.Count > 0, "asset-classes.json should populate the class library");
        Assert.True(m.AssetDynamics.Bindings.RuleCount > 0, "asset-bindings.json should populate the binding rules");
    }

    [Fact] // test-gap: the silent unit-normalization heuristic guarded every CLI .m load untested
    public void MatpowerUnitNormalizer_flags_and_fixes_a_raw_distribution_feeder()
    {
        string case33 = Path.Combine(DataDir, "matpower", "dist", "case33bw.m");
        Assert.True(File.Exists(case33), $"bundled file missing: {case33}");   // GR-404: the file is committed (BundledDataPresenceTests checks it), so a missing one fails instead of passing silently

        // Moved 2026-10 (N165, authorised by Mark's 2026-10-02 best-judgement ruling): ParseFile now normalises a raw
        // feeder by default, so the raw model this test inspects is asked for explicitly (AsWritten); the expectation
        // is unchanged. Before: MatpowerParser.ParseFile(case33). After: ParseFile(case33, MatpowerUnitHandling.AsWritten).
        GridModel raw = MatpowerParser.ParseFile(case33, MatpowerUnitHandling.AsWritten);
        Assert.True(MatpowerUnitNormalizer.LooksRawUnits(raw), "case33bw ships in raw ohms/kW and must be detected");

        GridModel norm = MatpowerUnitNormalizer.Normalize(raw);
        // kW -> MW: the whole feeder's demand is a few MW, not thousands.
        Assert.True(norm.Buses.Sum(b => b.Pd) < 10.0, "normalised demand should be a few MW, not kW-scale");
        // And it now solves in a sane voltage band (raw units voltage-collapse).
        PowerFlowResult r = new BackwardForwardSweep().Solve(norm);
        Assert.True(r.Converged && r.Vm.Min() > 0.8, $"normalised feeder should solve in-band (vmin {r.Vm.Min():F3})");
    }

    [Fact]
    public void MatpowerUnitNormalizer_leaves_a_per_unit_transmission_case_untouched()
    {
        GridModel m = IeeeCases.Case14();   // per-unit, 400 kV-class buses
        Assert.False(MatpowerUnitNormalizer.LooksRawUnits(m));
        Assert.Same(m, MatpowerUnitNormalizer.Normalize(m));
    }

    [Fact] // MATPOWER matrices with newline-separated rows must not collapse into one row
    public void MatpowerParser_reads_newline_separated_matrix_rows()
    {
        const string newlineCase = @"
function mpc = nl
mpc.baseMVA = 100;
mpc.bus = [
    1   3   0    0    0   0   1   1.0   0   345   1   1.1   0.9
    2   1   50   20   0   0   1   1.0   0   345   1   1.1   0.9
    3   1   30   10   0   0   1   1.0   0   345   1   1.1   0.9
];
mpc.gen = [
    1   0   0   Inf   -Inf   1.02   100   1   Inf   0
];
mpc.branch = [
    1   2   0.01   0.1   0.02   250   0   0   0   0   1   -360   360
    2   3   0.02   0.2   0.02   250   0   0   0   0   1   -360   360
];
";
        GridModel m = MatpowerParser.Parse(newlineCase);
        Assert.Equal(3, m.BusCount);            // was 1 before the fix (all rows merged)
        Assert.Equal(2, m.Branches.Count);
    }

    [Fact] // CIM export must round-trip loads, generators, base voltages and transformers
    public void CimExporter_round_trips_loads_generators_base_voltages_and_transformers()
    {
        var net = new CimNetwork(
            new[] { new CimBus("n1", "Slack", 132.0), new CimBus("n2", "Load", 33.0) },
            new[] { new CimBranch("l1", "line", "n1", "n2", 0.01, 0.1, 0.0) },
            new[] { new CimLoad("ld1", "load", "n2", 42.0, 12.0) },
            new[] { new CimGenerator("g1", "gen", "n1", 100.0) },
            new[]
            {
                new CimTransformer("tx1", "tx",
                    new[] { new CimTransformerEnd(1, "n1", 0.1, 5.0, 132.0), new CimTransformerEnd(2, "n2", 0.0, 0.0, 33.0) },
                    TapRatio: 1.05, PhaseShiftDeg: 0.0),
            });

        CimNetwork rt = CimImporter.Parse(CimExporter.Export(net));

        Assert.Single(rt.Loads);
        Assert.Equal(42.0, rt.Loads[0].P, 6);
        Assert.Equal(12.0, rt.Loads[0].Q, 6);
        Assert.Single(rt.Generators);
        Assert.Equal(100.0, rt.Generators[0].P, 6);
        Assert.Equal(132.0, rt.Buses.Single(b => b.MRID == "n1").BaseKv, 6);
        Assert.Single(rt.Transformers);
        Assert.Equal(1.05, rt.Transformers[0].TapRatio, 4);
    }

    [Fact] // multiple CIM generators on one node must not wipe out the slack bus
    public void CimGridModelBridge_keeps_the_slack_when_two_units_share_a_node()
    {
        var net = new CimNetwork(
            new[] { new CimBus("n1", "Station", 400.0), new CimBus("n2", "Load", 400.0) },
            new[] { new CimBranch("l1", "line", "n1", "n2", 0.01, 0.1, 0.0) },
            Array.Empty<CimLoad>(),
            new[] { new CimGenerator("g1", "unit1", "n1", 100.0), new CimGenerator("g2", "unit2", "n1", 120.0) },
            Array.Empty<CimTransformer>());

        GridModel m = CimGridModelBridge.ToGridModel(net);
        Assert.Equal(1, m.Buses.Count(b => b.Type == BusType.Slack));
    }

    [Fact] // IEC 104 U-format function bits must survive a parse/encode round trip; oversize ASDU must throw
    public void Iec104_u_format_round_trips_and_oversize_is_rejected()
    {
        var startDt = new Iec104Apdu(Iec104Format.U, 0, 0, Array.Empty<byte>(), Iec104UFunction.StartDtAct);
        byte[] bytes = Iec104Frame.ToBytes(startDt);
        Iec104Apdu back = Iec104Frame.Parse(bytes);
        Assert.Equal(Iec104Format.U, back.Format);
        Assert.Equal(Iec104UFunction.StartDtAct, back.UControl);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Iec104Frame.ToBytes(new Iec104Apdu(Iec104Format.I, 0, 0, new byte[300])));
    }

    [Fact] // the discriminator peek must be case-insensitive like the deserializers
    public void BridgeProtocol_peek_type_is_case_insensitive()
    {
        Assert.Equal("state", BridgeProtocol.PeekType("{\"type\":\"state\"}"));
        Assert.Equal("state", BridgeProtocol.PeekType("{\"Type\":\"state\"}"));
    }
}
