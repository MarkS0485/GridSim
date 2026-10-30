using GridSim.Core.Dispatch;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F16 (October 2026 release gap): generator in-service status in the model, JSON and MATPOWER. A status-0 (out of
/// service) generator used to be DROPPED by the MATPOWER loader and JSON had no status at all. It is now kept in the
/// model with <see cref="Generator.InService"/> false: <see cref="GridModel.Generators"/> (what every engine solves)
/// leaves it out exactly as before, while <see cref="GridModel.AllGenerators"/> keeps it for the writers.
///
/// The binding rule is that every bundled case solves bit-identically to before. Of the 25 bundled MATPOWER files
/// only nesta/pglib_opf_case588_sdet has status-0 units (72 of its 167); for every other file the in-service list IS
/// the full list (the same reference), so nothing an engine reads has changed. For case588 the in-service list must
/// equal, unit for unit, what the old loader kept, and solving it must give the same bits as a model built from that
/// list alone, on the dense and the sparse engine.
/// </summary>
public class GeneratorInServiceTests
{
    // Bus 2 carries an in-service unit and an out-of-service one; bus 3 only an out-of-service one (so bus 3, typed
    // PV, must be solved as PQ, as MATPOWER's bustypes() does).
    private const string Mixed = @"
function mpc = mixed
mpc.baseMVA = 100;
mpc.bus = [
    1   3   0    0    0   0   1   1.00   0   345   1   1.1   0.9;
    2   2   20   5    0   0   1   1.02   0   345   1   1.1   0.9;
    3   2   80   30   0   0   1   1.01   0   345   1   1.1   0.9;
];
mpc.gen = [
    1   0    0   300   -300   1.00   100   1   300   0;
    2   40   0   100   -100   1.02   100   1   100   0;
    2   25   3   50    -50    1.03   80    0   60    5;
    3   30   0   100   -100   1.01   100   0   100   0;
];
mpc.branch = [
    1   2   0.01   0.1   0.02   250   0   0   0   0   1   -360   360;
    2   3   0.01   0.1   0.02   250   0   0   0   0   1   -360   360;
    1   3   0.02   0.2   0.02   250   0   0   0   0   1   -360   360;
];
";

    [Fact]
    public void A_status_0_matpower_generator_is_kept_and_marked_out_of_service()
    {
        MatpowerParseResult res = MatpowerParser.ParseWithDiagnostics(Mixed, frequencyHz: 50);
        GridModel m = res.Model;

        Assert.Equal(4, m.AllGenerators.Count);
        Assert.Equal(2, m.Generators.Count);
        Assert.Equal(2, m.OutOfServiceGenerators.Count);
        Assert.All(m.Generators, g => Assert.True(g.InService));
        Assert.All(m.OutOfServiceGenerators, g => Assert.False(g.InService));

        // File order is kept in AllGenerators, and the out-of-service unit keeps all its data.
        Assert.Equal(new[] { 1, 2, 2, 3 }, m.AllGenerators.Select(g => g.Bus).ToArray());
        Generator off = m.AllGenerators[2];
        Assert.False(off.InService);
        Assert.Equal(25.0, off.Pg);
        Assert.Equal(3.0, off.Qg);
        Assert.Equal(1.03, off.Vg);
        Assert.Equal(-50.0, off.Qmin);
        Assert.Equal(50.0, off.Qmax);
        Assert.Equal(5.0, off.Pmin);
        Assert.Equal(60.0, off.Pmax);
        Assert.Equal(80.0, off.MBase);

        Assert.Contains(res.Notes, n => n.Contains("kept 2 out-of-service generator(s) in the model, marked out of service"));
        // Bus 3's only unit is out of service, so it is demoted to PQ (an out-of-service unit holds no voltage).
        Assert.Equal(BusType.Pq, m.Buses.Single(b => b.Id == 3).Type);
        Assert.Equal(BusType.Pv, m.Buses.Single(b => b.Id == 2).Type);
    }

    [Fact]
    public void Solving_a_model_with_out_of_service_units_is_bit_identical_to_solving_without_them()
    {
        GridModel withOff = MatpowerParser.Parse(Mixed, frequencyHz: 50);
        // The model the old loader built: the same buses and branches, only the in-service units.
        GridModel old = new()
        {
            Name = withOff.Name, BaseMva = withOff.BaseMva, FrequencyHz = withOff.FrequencyHz,
            Buses = withOff.Buses, Branches = withOff.Branches,
            Generators = withOff.AllGenerators.Where(g => g.InService).ToList(),
        };
        Assert.Empty(old.OutOfServiceGenerators);
        S10TestSupport.AssertSolvesIdentical("mixed", withOff, old, new PowerFlowOptions { EnforceQLimits = true });
        S10TestSupport.AssertSolvesIdentical("mixed, Q limits off", withOff, old, new PowerFlowOptions { EnforceQLimits = false });
    }

    [Fact]
    public void An_all_in_service_fleet_is_returned_by_reference()
    {
        var gens = new List<Generator> { new(1, 10), new(2, 20) };
        var m = new GridModel { BaseMva = 100, Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv) }, Generators = gens, Branches = Array.Empty<Branch>() };
        Assert.Same(gens, m.Generators);
        Assert.Same(gens, m.AllGenerators);
        Assert.Empty(m.OutOfServiceGenerators);
    }

    [Theory]
    [MemberData(nameof(S10TestSupport.BundledMatpowerData), MemberType = typeof(S10TestSupport))]
    public void Every_bundled_matpower_case_solves_exactly_as_before(string rel)
    {
        GridModel m = S10TestSupport.LoadMatpower(rel).Model;
        if (m.OutOfServiceGenerators.Count == 0)
        {
            // Nothing out of service: the engines read the very list the loader built, exactly as before F16.
            Assert.Same(m.AllGenerators, m.Generators);
            return;
        }

        // Only case588 reaches here. The in-service list must be exactly the units the old loader kept (status > 0
        // on a live bus), in file order, and the solve must match a model built from that list alone.
        Assert.Equal("nesta/pglib_opf_case588_sdet.m", rel);
        Assert.Equal(72, m.OutOfServiceGenerators.Count);
        Assert.Equal(167, m.AllGenerators.Count);
        Assert.Equal(95, m.Generators.Count);
        GridModel old = new()
        {
            Name = m.Name, BaseMva = m.BaseMva, FrequencyHz = m.FrequencyHz,
            Buses = m.Buses, Branches = m.Branches,
            Generators = m.AllGenerators.Where(g => g.InService).ToList(),
        };
        S10TestSupport.AssertSolvesIdentical(rel, m, old, new PowerFlowOptions { EnforceQLimits = true });
    }

    [Fact]
    public void CopyWith_carries_out_of_service_units_and_keeps_the_in_service_list()
    {
        GridModel m = MatpowerParser.Parse(Mixed, frequencyHz: 50);

        GridModel same = m.CopyWith();
        Assert.Same(m.Generators, same.Generators);
        Assert.Equal(m.AllGenerators, same.AllGenerators);

        // A redispatch built from the in-service list keeps the out-of-service units (appended after it).
        var redispatched = m.Generators.Select(g => g with { Pg = g.Pg + 1 }).ToList();
        GridModel copy = m.CopyWith(generators: redispatched);
        Assert.Same(redispatched, copy.Generators);
        Assert.Equal(4, copy.AllGenerators.Count);
        Assert.Equal(m.OutOfServiceGenerators, copy.OutOfServiceGenerators);

        // A list that itself marks a unit out of service is the complete fleet.
        var full = new List<Generator> { m.AllGenerators[0], m.AllGenerators[1] with { InService = false } };
        GridModel switched = m.CopyWith(generators: full);
        Assert.Single(switched.Generators);
        Assert.Equal(2, switched.AllGenerators.Count);
    }

    [Fact]
    public void Validate_refuses_an_out_of_service_unit_on_a_missing_bus()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack) },
            Generators = new[] { new Generator(1, 0), new Generator(9, 5, Name: "ghost", InService: false) },
            Branches = Array.Empty<Branch>(),
        };
        Assert.Contains(m.Validate(), d => d.Severity == ValidationSeverity.Error && d.Message.Contains("Out-of-service generator 'ghost' references nonexistent bus 9"));
    }

    // ---- JSON ------------------------------------------------------------------------------------------------------

    private static string TempCase(string generationJson)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-f16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "f16", "baseMva": 100, "frequencyHz": 50 }""");
        File.WriteAllText(Path.Combine(dir, "buses.json"), """
            [ { "id": 1, "baseKv": 400, "type": "pq" }, { "id": 2, "baseKv": 400, "type": "pq" } ]
            """);
        File.WriteAllText(Path.Combine(dir, "demand.json"), """[ { "name": "D", "bus": 2, "pMw": 50, "qMvar": 10 } ]""");
        File.WriteAllText(Path.Combine(dir, "transmission.json"), """[ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1, "ratingMva": 500 } ]""");
        File.WriteAllText(Path.Combine(dir, "generation.json"), generationJson);
        return dir;
    }

    [Fact]
    public void A_json_unit_marked_out_of_service_is_kept_but_not_solved_and_does_not_type_its_bus()
    {
        string dir = TempCase("""
            [ { "name": "G1", "bus": 1, "role": "slack", "capacityMva": 200, "outputMw": 0 },
              { "name": "G2", "bus": 2, "role": "pv", "capacityMva": 100, "outputMw": 30, "inService": false } ]
            """);
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Assert.Single(m.Generators);
            Assert.Equal("G1", m.Generators[0].Name);
            Generator g2 = Assert.Single(m.OutOfServiceGenerators);
            Assert.Equal("G2", g2.Name);
            Assert.Equal(30.0, g2.Pg);
            // An out-of-service unit does not raise its bus to PV.
            Assert.Equal(BusType.Pq, m.Buses.Single(b => b.Id == 2).Type);
            // The dispatch balance (Dispatcher.Balance, through GridModel.CopyWith) kept the unit.
            Assert.Equal(2, m.AllGenerators.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_json_case_whose_only_slack_unit_is_out_of_service_is_refused()
    {
        string dir = TempCase("""
            [ { "name": "G1", "bus": 1, "role": "slack", "capacityMva": 200, "inService": false } ]
            """);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("No slack bus", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Json_in_service_absent_is_in_service_and_the_bundled_cases_have_no_out_of_service_unit()
    {
        foreach (string d in new[] { "uk", "gb-synthetic", "gb-full" })
        {
            GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, d));
            Assert.Same(m.AllGenerators, m.Generators);
        }
    }

    [Fact]
    public void Dispatcher_balance_works_on_the_in_service_fleet_only()
    {
        // The out-of-service unit's 25 MW must not count as fixed generation in the balance.
        GridModel m = MatpowerParser.Parse(Mixed, frequencyHz: 50);
        Dispatcher.BalanceResult b = Dispatcher.BalanceDetailed(m, lossAllowance: 0.0);
        double demand = m.Buses.Sum(x => x.Pd);   // 100 MW
        Assert.Equal(demand - 40.0, b.SlackTargetMw);   // only the in-service 40 MW unit is fixed
        Assert.Equal(2, b.Model.OutOfServiceGenerators.Count);
    }
}
