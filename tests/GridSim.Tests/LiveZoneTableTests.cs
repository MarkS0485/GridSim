using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N056: the interactive live dashboard's zone table works on any case. On the GB 10-zone model it must read exactly
/// as the old hard-wired table (rows at or above 200 kV, partner at id + 100); elsewhere it must never print another
/// bus's value as a zone's lower-voltage side.
/// </summary>
public sealed class LiveZoneTableTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    /// <summary>The pre-N056 table, reproduced verbatim from the CLI's RenderDashboard, as the reference.</summary>
    private static List<(string Zone, double V400, double V132, double Dem)> OldTable(GridModel m, PowerFlowResult r)
    {
        var vmById = new Dictionary<int, double>();
        for (int i = 0; i < r.BusIds.Count; i++) vmById[r.BusIds[i]] = r.Vm[i];
        var pdById = m.Buses.ToDictionary(b => b.Id, b => b.Pd);
        var rows = new List<(string, double, double, double)>();
        foreach (Bus b in m.Buses.Where(b => b.BaseKv >= 200).OrderBy(b => b.Id))
        {
            string zone = (b.Name ?? $"Bus {b.Id}").Replace(" 400kV", "");
            double v132 = vmById.GetValueOrDefault(b.Id + 100, double.NaN);
            double dem = pdById.GetValueOrDefault(b.Id + 100);
            rows.Add((zone, vmById[b.Id], v132, dem));
        }
        return rows;
    }

    [Fact]
    public void Gb_ten_zone_model_reads_exactly_as_before()
    {
        GridModel m = Uk();
        PowerFlowResult r = new SteadyStateSolver().Solve(m);
        Assert.True(r.Converged);
        var old = OldTable(m, r);
        LiveZoneTable t = LiveZoneTable.Build(m, r);

        Assert.Equal(10, old.Count);
        Assert.Equal(old.Count, t.Rows.Count);
        for (int i = 0; i < old.Count; i++)
        {
            Assert.Equal(old[i].Zone, t.Rows[i].Zone);
            Assert.Equal(old[i].V400, t.Rows[i].UpperVmPu);     // exact: the same double read off the same result
            Assert.Equal(old[i].V132, t.Rows[i].LowerVmPu);
            Assert.Equal(old[i].Dem, t.Rows[i].DemandMw);
            Assert.Equal(t.Rows[i].BusId + 100, t.Rows[i].PartnerBusId);
        }
        Assert.Equal("400kV", t.UpperHeader);
        Assert.Equal("132kV", t.LowerHeader);
        Assert.Null(t.Note);
        Assert.Equal(0, t.Omitted);
    }

    [Fact]
    public void Ieee118_never_pairs_a_bus_with_an_unrelated_id_plus_100()
    {
        GridModel m = IeeeCases.Case118();
        PowerFlowResult r = new SteadyStateSolver().Solve(m);
        LiveZoneTable t = LiveZoneTable.Build(m, r);
        var busById = m.Buses.ToDictionary(b => b.Id);
        Assert.NotEmpty(t.Rows);
        foreach (LiveZoneTable.Row row in t.Rows)
        {
            Assert.True(busById[row.BusId].BaseKv >= LiveZoneTable.TransmissionKvFloor);
            if (row.PartnerBusId is int p)
            {
                // A partner is always a real branch neighbour at a lower voltage.
                Assert.Contains(m.Branches, br => br.InService && ((br.From == row.BusId && br.To == p) || (br.To == row.BusId && br.From == p)));
                Assert.True(busById[p].BaseKv < busById[row.BusId].BaseKv);
            }
            else
            {
                Assert.True(double.IsNaN(row.LowerVmPu));
                Assert.Equal(busById[row.BusId].Pd, row.DemandMw);
            }
        }
    }

    // ---- small hand-built networks (no solve needed: the table only reads the result's Vm) ----

    private static GridModel Net(Bus[] buses, Branch[] branches) => new()
    {
        BaseMva = 100, Buses = buses, Generators = Array.Empty<Generator>(), Branches = branches, Name = "t",
    };

    private static PowerFlowResult Flat(GridModel m, Func<int, double> vm) => new()
    {
        Converged = true, Iterations = 1, MaxMismatch = 0,
        BusIds = m.Buses.Select(b => b.Id).ToArray(),
        Vm = m.Buses.Select(b => vm(b.Id)).ToArray(),
        VaDeg = new double[m.Buses.Count], PInjMw = new double[m.Buses.Count], QInjMvar = new double[m.Buses.Count],
        TotalLossMw = 0, TotalLossMvar = 0, LineFlows = Array.Empty<LineFlow>(),
    };

    [Fact]
    public void An_id_plus_100_bus_that_is_not_a_neighbour_is_not_a_partner()
    {
        // Bus 1 (400 kV) feeds bus 2 (132 kV) through a transformer; bus 101 (132 kV) exists but hangs off bus 2.
        GridModel m = Net(
            new[] { new Bus(1, BusType.Slack, BaseKv: 400, Name: "A 400kV"), new Bus(2, BusType.Pq, Pd: 50, BaseKv: 132),
                    new Bus(101, BusType.Pq, Pd: 999, BaseKv: 132) },
            new[] { new Branch(1, 2, 0.01, 0.1, IsTransformer: true), new Branch(2, 101, 0.01, 0.1) });
        LiveZoneTable t = LiveZoneTable.Build(m, Flat(m, id => 1.0 + id / 1000.0));
        LiveZoneTable.Row row = Assert.Single(t.Rows);
        Assert.Equal("A", row.Zone);
        Assert.Equal(2, row.PartnerBusId);           // the real neighbour, not 101
        Assert.Equal(1.002, row.LowerVmPu);
        Assert.Equal(50.0, row.DemandMw);
    }

    [Fact]
    public void Several_lower_neighbours_without_the_convention_leave_the_row_unpaired()
    {
        GridModel m = Net(
            new[] { new Bus(1, BusType.Slack, BaseKv: 275), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 132), new Bus(3, BusType.Pq, Pd: 20, BaseKv: 33) },
            new[] { new Branch(1, 2, 0.01, 0.1, IsTransformer: true), new Branch(1, 3, 0.01, 0.1, IsTransformer: true) });
        LiveZoneTable t = LiveZoneTable.Build(m, Flat(m, _ => 1.0));
        LiveZoneTable.Row row = Assert.Single(t.Rows);
        Assert.Null(row.PartnerBusId);
        Assert.True(double.IsNaN(row.LowerVmPu));
        Assert.Equal("Bus 1", row.Zone);
        Assert.Equal("275kV", t.UpperHeader);
        Assert.Equal("LV", t.LowerHeader);
        Assert.Contains("not applicable", t.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_service_branch_does_not_make_a_partner()
    {
        GridModel m = Net(
            new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(101, BusType.Pq, BaseKv: 132) },
            new[] { new Branch(1, 101, 0.01, 0.1, IsTransformer: true, InService: false) });
        Assert.Null(Assert.Single(LiveZoneTable.Build(m, Flat(m, _ => 1.0)).Rows).PartnerBusId);
    }

    [Fact]
    public void A_case_without_transmission_uses_its_highest_voltage()
    {
        GridModel m = Net(
            new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 5, BaseKv: 33), new Bus(3, BusType.Pq, BaseKv: 132) },
            new[] { new Branch(1, 2, 0.01, 0.1, IsTransformer: true), new Branch(1, 3, 0.01, 0.1) });
        LiveZoneTable t = LiveZoneTable.Build(m, Flat(m, _ => 1.0));
        Assert.Equal(new[] { 1, 3 }, t.Rows.Select(r => r.BusId));
        Assert.Equal(2, t.Rows[0].PartnerBusId);
        Assert.Equal(5.0, t.Rows[0].DemandMw);
        Assert.Null(t.Rows[1].PartnerBusId);
        Assert.Equal("132kV", t.UpperHeader);
        Assert.Equal("33kV", t.LowerHeader);
        Assert.Equal("Zone table: 1 of 2 buses have no single lower-voltage partner (33kV not applicable there).", t.Note);
    }

    [Fact]
    public void A_case_without_bus_voltages_has_no_rows_and_says_why()
    {
        GridModel m = Net(new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq) }, new[] { new Branch(1, 2, 0.01, 0.1) });
        LiveZoneTable t = LiveZoneTable.Build(m, Flat(m, _ => 1.0));
        Assert.Empty(t.Rows);
        Assert.Equal("Zone table: bus voltages (kV) are not in the data for this case.", t.Note);
    }

    [Fact]
    public void Rows_are_capped_and_the_rest_counted()
    {
        Bus[] buses = Enumerable.Range(1, 30).Select(i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq, BaseKv: 400)).ToArray();
        Branch[] branches = Enumerable.Range(1, 29).Select(i => new Branch(i, i + 1, 0.01, 0.1)).ToArray();
        GridModel m = Net(buses, branches);
        LiveZoneTable t = LiveZoneTable.Build(m, Flat(m, _ => 1.0));
        Assert.Equal(LiveZoneTable.MaxRows, t.Rows.Count);
        Assert.Equal(30 - LiveZoneTable.MaxRows, t.Omitted);
        Assert.Equal(Enumerable.Range(1, LiveZoneTable.MaxRows), t.Rows.Select(r => r.BusId));
    }
}
