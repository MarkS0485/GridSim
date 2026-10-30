using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.PowerFlow;
using GridSim.Gda;
using GridSim.Web.Visuals;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS6 import and fuel group: R030 (MATPOWER PV buses with no in-service generator are PQ,
/// dropped generators are counted), R269 (the assumed nominal frequency is explicit and noted), R298 (hydrogen
/// is not hydro) and R305 (one shared fuel taxonomy; reciprocating engines are synchronous fossil).
/// </summary>
public class ImportAndFuelWs6Tests
{
    // Bus 2 is type 2 (PV) but its only generator has status 0; a third generator sits on bus 9, which the file
    // never defines; bus 4 is isolated (type 4) and carries an in-service generator.
    private const string OfflinePv = @"
function mpc = offline_pv
mpc.baseMVA = 100;
mpc.bus = [
    1   3   0    0    0   0   1   1.00   0   345   1   1.1   0.9;
    2   2   0    0    0   0   1   1.04   0   345   1   1.1   0.9;
    3   1   80   30   0   0   1   1.00   0   345   1   1.1   0.9;
    4   4   0    0    0   0   1   1.00   0   345   1   1.1   0.9;
];
mpc.gen = [
    1   0    0   300   -300   1.02   100   1   300   0;
    2   50   0   300   -300   1.04   100   0   300   0;
    9   10   0   300   -300   1.00   100   1   300   0;
    4   10   0   300   -300   1.00   100   1   300   0;
];
mpc.branch = [
    1   2   0.01   0.1   0.02   250   0   0   0   0   1   -360   360;
    2   3   0.01   0.1   0.02   250   0   0   0   0   1   -360   360;
];
";

    [Fact]
    public void A_pv_bus_whose_generators_are_all_offline_is_parsed_as_pq_with_a_note()
    {
        MatpowerParseResult res = MatpowerParser.ParseWithDiagnostics(OfflinePv, frequencyHz: 60);
        Bus b2 = res.Model.Buses.Single(b => b.Id == 2);
        Assert.Equal(BusType.Pq, b2.Type);
        Assert.Contains(res.Notes, n => n.Contains("PV (type 2) bus(es) with no in-service generator as PQ"));
    }

    [Fact]
    public void Generators_on_dropped_or_undefined_buses_are_counted_in_the_notes()
    {
        MatpowerParseResult res = MatpowerParser.ParseWithDiagnostics(OfflinePv, frequencyHz: 60);
        Assert.Single(res.Model.Generators);
        Assert.Contains(res.Notes, n => n.Contains("1 in-service generator(s) whose bus id is not defined"));
        Assert.Contains(res.Notes, n => n.Contains("1 in-service generator(s) on dropped isolated"));
        Assert.Contains(res.Notes, n => n.Contains("1 out-of-service generator(s)"));
    }

    [Fact]
    public void The_demoted_bus_solves_as_matpower_would_with_q_limits_off()
    {
        // With EnforceQLimits off (the crossval/replay setting) the old PV bus was held at its file Vm 1.04 with
        // unlimited reactive power. As PQ it carries no injection and floats.
        GridModel m = MatpowerParser.Parse(OfflinePv, frequencyHz: 60);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = false });
        Assert.True(r.Converged);
        int i2 = r.BusIds.ToList().IndexOf(2);
        Assert.NotEqual(1.04, r.Vm[i2]);
        Assert.True(Math.Abs(r.QInjMvar[i2]) < 1e-6, $"a PQ bus with no load or unit injects nothing (got {r.QInjMvar[i2]} MVAr)");
    }

    [Fact]
    public void An_omitted_frequency_is_an_assumption_recorded_in_the_notes()
    {
        MatpowerParseResult assumed = MatpowerParser.ParseWithDiagnostics(OfflinePv);
        Assert.Equal(MatpowerParser.AssumedFrequencyHz, assumed.Model.FrequencyHz);
        Assert.Contains(assumed.Notes, n => n.Contains("nominal frequency not given"));

        MatpowerParseResult given = MatpowerParser.ParseWithDiagnostics(OfflinePv, frequencyHz: 60);
        Assert.Equal(60.0, given.Model.FrequencyHz);
        Assert.DoesNotContain(given.Notes, n => n.Contains("nominal frequency"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_invalid_frequency_fails_loud(double f)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MatpowerParser.ParseWithDiagnostics(OfflinePv, frequencyHz: f));

    [Fact]
    public void A_reference_bus_without_a_generator_is_reported()
    {
        const string noSlackGen = @"
function mpc = noslackgen
mpc.baseMVA = 100;
mpc.bus = [
    1   3   0    0    0   0   1   1.00   0   345   1   1.1   0.9;
    2   2   10   0    0   0   1   1.02   0   345   1   1.1   0.9;
];
mpc.gen = [
    2   10   0   300   -300   1.02   100   1   300   0;
];
mpc.branch = [
    1   2   0.01   0.1   0.02   250   0   0   0   0   1   -360   360;
];
";
        MatpowerParseResult res = MatpowerParser.ParseWithDiagnostics(noSlackGen, frequencyHz: 50);
        Assert.Contains(res.Notes, n => n.Contains("reference (type 3) bus 1 has no in-service generator"));
        Assert.Equal(BusType.Slack, res.Model.Buses[0].Type);
    }

    // ---- R298 ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Hydrogen")]
    [InlineData("hydrogen fuel cell")]
    [InlineData("Fuel Cell")]
    [InlineData("Electrolyser")]
    public void Hydrogen_plant_is_converter_storage_with_no_inertia(string raw)
    {
        FuelMap.Kind k = FuelMap.Classify(raw);
        Assert.Equal("storage", k.Type);
        Assert.Equal(0.0, k.H);
        Assert.True(ConverterPlant.IsConverterType(k.Type));
    }

    [Theory]
    [InlineData("Hydro", "hydro", 3.0)]
    [InlineData("Large Hydro", "hydro", 3.0)]
    [InlineData("Hydrogen CCGT", "ccgt", 5.0)]
    [InlineData("Hydrogen Gas Turbine", "ocgt", 4.0)]
    [InlineData("Hydrogen CHP", "chp", 2.5)]
    public void Real_hydro_and_hydrogen_burning_turbines_keep_their_classes(string raw, string type, double h)
    {
        FuelMap.Kind k = FuelMap.Classify(raw);
        Assert.Equal(type, k.Type);
        Assert.Equal(h, k.H);
    }

    // ---- R305 ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("recip")]
    [InlineData("Reciprocating")]
    [InlineData("diesel")]
    [InlineData("engine")]
    public void Reciprocating_engines_are_synchronous_fossil(string type)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        Assert.Equal(FuelCategory.Fossil, d.Category);
        Assert.True(d.Synchronous);
        Assert.Equal("Reciprocating engine", d.Name);
        // Consistent with Core's machine-speed map, which treats recip as a synchronous swing mass.
        Assert.Equal(DomainKind.Synchronous, PoleMap.ForType("recip").Kind);
    }

    [Fact]
    public void A_gas_engine_keeps_its_gas_label()
        => Assert.Equal("Gas", FuelTaxonomy.Classify("gas engine").Name);

    [Fact]
    public void Hydrogen_is_not_shown_as_hydro()
    {
        FuelDescriptor d = FuelTaxonomy.Classify("hydrogen");
        Assert.Equal("Hydrogen", d.Name);
        Assert.False(d.Synchronous);
        Assert.Equal("Hydro", FuelTaxonomy.Classify("hydro").Name);
    }

    [Theory]
    [InlineData("nuclear")] [InlineData("coal")] [InlineData("ccgt")] [InlineData("ocgt")] [InlineData("chp")]
    [InlineData("biomass")] [InlineData("pumped-storage")] [InlineData("hydro")] [InlineData("wind-offshore")]
    [InlineData("wind-onshore")] [InlineData("solar")] [InlineData("storage")] [InlineData("interconnector")]
    [InlineData("grid")] [InlineData("gas")] [InlineData("tidal")] [InlineData("recip")] [InlineData("hydrogen")]
    [InlineData("")] [InlineData(null)] [InlineData("something else")]
    public void The_web_fuels_are_the_core_taxonomy(string? type)
    {
        FuelDescriptor core = FuelTaxonomy.Classify(type);
        FuelInfo web = Fuels.Classify(type);
        Assert.Equal(core.Name, web.Name);
        Assert.Equal(core.Color, web.Color);
        Assert.Equal(core.Synchronous, web.Synchronous);
        Assert.Equal(core.Category.ToString(), web.Class.ToString());
    }

    [Fact]
    public void Every_core_category_maps_to_the_web_class_of_the_same_name()
    {
        foreach (FuelCategory c in Enum.GetValues<FuelCategory>())
            Assert.Equal(c.ToString(), Fuels.ToFuelClass(c).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => Fuels.ToFuelClass((FuelCategory)99));
    }

    [Theory]
    [InlineData("nuclear", "Nuclear", "#7E57C2", FuelClass.Nuclear, true)]
    [InlineData("ccgt", "CCGT", "#FF8A3D", FuelClass.Fossil, true)]
    [InlineData("pumped-storage", "Pumped storage", "#26A69A", FuelClass.Renewable, true)]
    [InlineData("wind-offshore", "Wind (offshore)", "#00B8D4", FuelClass.Renewable, false)]
    [InlineData("storage", "Storage", "#EC407A", FuelClass.Storage, false)]
    [InlineData("grid", "Grid infeed", "#607D8B", FuelClass.Interconnector, false)]
    [InlineData("other", "Other", "#9E9E9E", FuelClass.Other, false)]
    public void Existing_web_classifications_are_unchanged(string type, string name, string color, FuelClass cls, bool sync)
    {
        FuelInfo f = Fuels.Classify(type);
        Assert.Equal(name, f.Name);
        Assert.Equal(color, f.Color);
        Assert.Equal(cls, f.Class);
        Assert.Equal(sync, f.Synchronous);
    }
}
