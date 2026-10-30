using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Synthetic;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N212 (Ruling R1 follow-up, 2026-10-04: make it lossless). GdaModelWriter, used by <c>gridsim synth</c> and
/// <c>gridsim gda --out</c>, dropped generator fields a JSON case can carry (Pmin, Pmax when not the capacity, the
/// scheduled Q, converter-interfaced, grid forming, in-service, ...). Now every such field round-trips through the
/// writer and the v1 loader bit for bit; what v1 cannot hold (X''d, X2, gencost) comes back as a note; and with every
/// field at its default the files are byte-identical to before (proved on the committed data/gb-synthetic folder, which
/// is the synth builder's output, and on the exact key order of a GDA-style generator row).
/// </summary>
public class N212GdaModelWriterLosslessTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"gridsim-n212-{Guid.NewGuid():N}");

    private static void SameBits(double expected, double actual, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>A three-bus model whose generators use every non-default field a v1 case can carry.</summary>
    private static GridModel RichModel() => new()
    {
        Name = "n212",
        BaseMva = 100.0,
        FrequencyHz = 50.0,
        Buses = new List<Bus>
        {
            new(1, BusType.Slack, BaseKv: 132.0, Name: "A"),
            new(2, BusType.Pv, Pd: 40.0, Qd: 10.0, BaseKv: 132.0, Name: "B"),
            new(3, BusType.Pq, Pd: 60.0, Qd: 20.0, BaseKv: 132.0, Name: "C"),
        },
        Generators = new List<Generator>
        {
            // The slack unit: limits at the defaults the writer always wrote.
            new(1, Pg: 50.0, Vg: 1.02, Qmin: -80.0, Qmax: 80.0, Pmin: 0, Pmax: 100.0, MBase: 100.0, H: 5.0, Name: "G1", Type: "ccgt"),
            // Every optional field away from its default; Pmax below the capacity; an unlimited Q limit.
            new(2, Pg: 30.0, Qg: 4.5, Vg: 1.01, Qmin: double.NegativeInfinity, Qmax: 25.0, Pmin: 12.5, Pmax: 70.0, MBase: 90.0,
                H: 0.0, Name: "G2 battery", Type: "battery", DynamicsClass: "reec-test",
                ConverterInterfaced: true, ConverterFaultCurrentPu: 1.1, PowerFlowOnly: true, UnitTransformerXPu: 0.12,
                GridForming: true),
            // Unlimited Pmax.
            new(2, Pg: 5.0, Vg: 1.01, Qmin: -5.0, Qmax: 5.0, Pmin: 1.0, Pmax: double.PositiveInfinity, MBase: 10.0, Name: "G3", Type: "ocgt"),
            // Out of service: kept in the fleet and written with inService: false.
            new(3, Pg: 7.0, Qmin: -3.0, Qmax: 3.0, Pmax: 20.0, MBase: 20.0, Name: "G4 off", Type: "wind", InService: false),
        },
        Branches = new List<Branch>
        {
            new(1, 2, 0.01, 0.05, 0.02, Name: "L12", RateAMva: 200.0),
            new(2, 3, 0.01, 0.05, 0.02, Name: "L23", RateAMva: 200.0),
        },
        Oltcs = new List<Oltc>(),
        Boundaries = new List<Boundary>(),
    };

    [Fact]
    public void Every_generator_field_a_json_case_carries_round_trips_bit_for_bit()
    {
        GridModel m = RichModel();
        string dir = TempDir();
        try
        {
            IReadOnlyList<string> notes = GdaModelWriter.WriteDirectory(m, dir, addGridInfeed: false);
            Assert.Empty(notes);   // nothing here is beyond JSON v1
            GridModel back = JsonGridLoader.LoadDirectory(dir, dispatch: false);

            Assert.Equal(m.AllGenerators.Count, back.AllGenerators.Count);
            Assert.Equal(3, back.Generators.Count);              // the out-of-service unit is not solved ...
            Assert.Single(back.OutOfServiceGenerators);          // ... but it is kept
            for (int i = 0; i < m.AllGenerators.Count; i++)
            {
                Generator a = m.AllGenerators[i], b = back.AllGenerators[i];
                string at = $"generator {i} ({a.Name})";
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.Bus, b.Bus);
                Assert.Equal(a.Type, b.Type);
                SameBits(a.Pg, b.Pg, at + " Pg");
                SameBits(a.Qg, b.Qg, at + " Qg");
                SameBits(a.Vg, b.Vg, at + " Vg");
                SameBits(a.Qmin, b.Qmin, at + " Qmin");
                SameBits(a.Qmax, b.Qmax, at + " Qmax");
                SameBits(a.Pmin, b.Pmin, at + " Pmin");
                SameBits(a.Pmax, b.Pmax, at + " Pmax");
                SameBits(a.MBase, b.MBase, at + " MBase");
                SameBits(a.H, b.H, at + " H");
                Assert.Equal(a.DynamicsClass, b.DynamicsClass);
                Assert.Equal(a.ConverterInterfaced, b.ConverterInterfaced);
                Assert.Equal(a.ConverterFaultCurrentPu, b.ConverterFaultCurrentPu);
                Assert.Equal(a.PowerFlowOnly, b.PowerFlowOnly);
                SameBits(a.UnitTransformerXPu, b.UnitTransformerXPu, at + " UnitTransformerXPu");
                Assert.Equal(a.GridForming, b.GridForming);
                Assert.Equal(a.InService, b.InService);
            }
            // The bus types come back too: the out-of-service unit on bus 3 did not make it PV, the slack is unit 1's.
            Assert.Equal(new[] { BusType.Slack, BusType.Pv, BusType.Pq }, back.Buses.Select(b => b.Type).ToArray());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void What_json_v1_cannot_hold_is_reported_not_dropped_silently()
    {
        GridModel rich = RichModel();
        var fleet = rich.AllGenerators.ToList();
        fleet[0] = fleet[0] with { SubtransientXdPu = 0.18, NegativeSeqXPu = 0.2 };
        fleet[1] = fleet[1] with { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { 0.0, 20.0, 0.0 }) };
        GridModel m = rich.CopyWith(generators: fleet);
        string dir = TempDir();
        try
        {
            IReadOnlyList<string> notes = GdaModelWriter.WriteDirectory(m, dir, addGridInfeed: false);
            Assert.Equal(2, notes.Count);
            Assert.Contains("1 generator(s) carry a subtransient (X''d) or negative-sequence reactance", notes[0], StringComparison.Ordinal);
            Assert.Contains("1 generator(s) carry a MATPOWER cost curve", notes[1], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Synth_output_is_byte_identical_to_the_committed_gb_synthetic_folder()
    {
        // data/gb-synthetic is `gridsim synth` at its defaults (regenerated through this writer in R1-E). Every generator
        // there has the default Pmin / Pmax / Q / flags, so the lossless writer must reproduce the committed files byte
        // for byte (line endings aside: git may check text out as CRLF).
        string dir = TempDir();
        try
        {
            Assert.Empty(GdaModelWriter.WriteDirectory(SyntheticGb.Build(), dir, addGridInfeed: false));
            string folder = Path.Combine(CliProcess.Root(), "data", "gb-synthetic");
            foreach (string f in new[] { "system.json", "buses.json", "generation.json", "demand.json", "transmission.json", "transformer.json", "onloadtapchanger.json" })
            {
                string expected = File.ReadAllText(Path.Combine(folder, f)).Replace("\r\n", "\n");
                string actual = File.ReadAllText(Path.Combine(dir, f)).Replace("\r\n", "\n");
                Assert.True(expected == actual, $"{f} differs from the committed data/gb-synthetic/{f}");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_gda_style_generator_row_keeps_exactly_its_old_keys_and_order()
    {
        // A GDA build sets only the basic fields (GdaModelBuilder: Qg 0, Pmin 0, Pmax = capacity, no flags), so its rows
        // must keep exactly the keys, in exactly the order, the writer always wrote; the grid infeed row likewise.
        GridModel rich = RichModel();
        var fleet = new List<Generator> { rich.AllGenerators[0] };
        GridModel m = rich.CopyWith(generators: fleet);
        string dir = TempDir();
        try
        {
            GdaModelWriter.WriteDirectory(m, dir);   // addGridInfeed: true, as `gda --out`
            JsonElement rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "generation.json"))).RootElement;
            string[] infeedKeys = rows[0].EnumerateObject().Select(p => p.Name).ToArray();
            string[] unitKeys = rows[1].EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "name", "bus", "type", "capacityMva", "outputMw", "voltageSetpoint", "qMinMvar", "qMaxMvar", "inertiaH", "role" }, infeedKeys);
            Assert.Equal(new[] { "name", "bus", "type", "capacityMva", "outputMw", "voltageSetpoint", "qMinMvar", "qMaxMvar", "inertiaH", "role" }, unitKeys);
            Assert.Equal(-80.0, rows[1].GetProperty("qMinMvar").GetDouble());   // a finite limit is still a number
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GR332_bus_zip_and_voltage_limits_and_line_ratings_and_zero_sequence_round_trip()
    {
        // GR-332: the writer used to drop these v1 fields silently. Each is written only when set, and read back bit for bit.
        GridModel rich = RichModel();
        var buses = rich.Buses.ToList();
        buses[2] = buses[2] with { Zip = new ZipLoad(0.2, 0.3, 0.5, 0.1, 0.1, 0.8), VminPu = 0.95, VmaxPu = 1.05 };
        var branches = rich.Branches.ToList();
        branches[0] = branches[0] with { RateBMva = 250.0, RateCMva = 300.0, R0 = 0.03, X0 = 0.15 };
        GridModel m = rich.CopyWith(buses: buses, branches: branches);
        string dir = TempDir();
        try
        {
            Assert.Empty(GdaModelWriter.WriteDirectory(m, dir, addGridInfeed: false));
            GridModel back = JsonGridLoader.LoadDirectory(dir, dispatch: false);
            Bus b3 = back.Buses.Single(b => b.Id == 3);
            Assert.Equal(new ZipLoad(0.2, 0.3, 0.5, 0.1, 0.1, 0.8), b3.Zip);
            SameBits(0.95, b3.VminPu!.Value, "bus 3 vminPu");
            SameBits(1.05, b3.VmaxPu!.Value, "bus 3 vmaxPu");
            Branch l12 = back.Branches.Single(b => b.Name == "L12");
            SameBits(250.0, l12.RateBMva, "L12 RateB");
            SameBits(300.0, l12.RateCMva, "L12 RateC");
            SameBits(0.03, l12.R0, "L12 R0");
            SameBits(0.15, l12.X0, "L12 X0");
            Assert.Null(back.Buses.Single(b => b.Id == 2).Zip);   // an unset field stays unset
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
