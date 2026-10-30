using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tests for <see cref="GridModel.Validate"/> and its wiring into the loaders. Validate
/// itself never throws; the entry-point loaders (JSON, MATPOWER) hard-fail on error-severity
/// diagnostics, while the GDA data-merge pipeline collects them as notes and carries on.
/// </summary>
public class GridModelValidateTests
{
    private static Bus B(int id, BusType type = BusType.Pq, double kv = 132) => new(id, type, BaseKv: kv);

    private static GridModel Model(
        IReadOnlyList<Bus> buses,
        IReadOnlyList<Branch>? branches = null,
        IReadOnlyList<Generator>? gens = null,
        IReadOnlyList<Oltc>? oltcs = null) => new()
        {
            BaseMva = 100,
            Buses = buses,
            Branches = branches ?? Array.Empty<Branch>(),
            Generators = gens ?? Array.Empty<Generator>(),
            Oltcs = oltcs ?? Array.Empty<Oltc>(),
        };

    private static bool HasError(GridModel m, string fragment) =>
        m.Validate().Any(d => d.Severity == ValidationSeverity.Error && d.Message.Contains(fragment));

    private static bool HasWarning(GridModel m, string fragment) =>
        m.Validate().Any(d => d.Severity == ValidationSeverity.Warning && d.Message.Contains(fragment));

    // ---- individual diagnostics ---------------------------------------------------------------

    [Fact]
    public void Duplicate_bus_id_is_an_error()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack), B(1), B(2) });
        Assert.True(HasError(m, "Duplicate bus id 1"));
    }

    [Fact]
    public void Missing_slack_is_an_error()
    {
        GridModel m = Model(new[] { B(1), B(2) });
        Assert.True(HasError(m, "No slack"));
    }

    [Fact]
    public void Multiple_slacks_is_a_warning_not_an_error()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack), B(2, BusType.Slack) });
        Assert.DoesNotContain(m.Validate(), d => d.Severity == ValidationSeverity.Error);
        Assert.True(HasWarning(m, "multi-island"));
    }

    [Fact]
    public void Dangling_branch_endpoint_is_an_error()
    {
        GridModel m = Model(
            new[] { B(1, BusType.Slack), B(2) },
            new[] { new Branch(1, 99, 0.01, 0.1) });
        Assert.True(HasError(m, "nonexistent bus 99"));
    }

    [Fact]
    public void Generator_on_nonexistent_bus_is_an_error()
    {
        GridModel m = Model(
            new[] { B(1, BusType.Slack) },
            gens: new[] { new Generator(42, Pg: 10) });
        Assert.True(HasError(m, "nonexistent bus 42"));
    }

    [Fact]
    public void Oltc_naming_a_nonexistent_transformer_warns()
    {
        GridModel m = Model(
            new[] { B(1, BusType.Slack, 132), B(2, BusType.Pq, 33) },
            new[] { new Branch(1, 2, 0.01, 0.1, Name: "TX-A", IsTransformer: true) },
            oltcs: new[] { new Oltc("TX-MISSING", ControlsBus: 2) });
        Assert.True(HasWarning(m, "does not exist"));
        Assert.DoesNotContain(m.Validate(), d => d.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Oltc_on_the_hv_side_warns()
    {
        // bus 1 = 132 kV (HV), bus 2 = 33 kV (LV). Controlling the HV bus is the misconfiguration.
        GridModel m = Model(
            new[] { B(1, BusType.Slack, 132), B(2, BusType.Pq, 33) },
            new[] { new Branch(1, 2, 0.01, 0.1, Name: "TX-A", IsTransformer: true) },
            oltcs: new[] { new Oltc("TX-A", ControlsBus: 1) });
        Assert.True(HasWarning(m, "HV-side"));
    }

    [Fact]
    public void Oltc_on_the_lv_side_is_clean()
    {
        GridModel m = Model(
            new[] { B(1, BusType.Slack, 132), B(2, BusType.Pq, 33) },
            new[] { new Branch(1, 2, 0.01, 0.1, Name: "TX-A", IsTransformer: true) },
            oltcs: new[] { new Oltc("TX-A", ControlsBus: 2) });
        Assert.Empty(m.Validate());
    }

    // ---- non-finite inputs are errors (a NaN/Inf from a data merge would poison the solve) -----

    [Fact]
    public void Non_finite_bus_demand_is_an_error()
    {
        GridModel m = Model(new[] { new Bus(1, BusType.Slack, Pd: double.NaN, BaseKv: 132), B(2) });
        Assert.True(HasError(m, "Bus 1 Pd is not a finite number"));
    }

    [Fact]
    public void Non_finite_branch_reactance_is_an_error()
    {
        GridModel m = Model(
            new[] { B(1, BusType.Slack), B(2) },
            new[] { new Branch(1, 2, 0.01, double.PositiveInfinity, Name: "L12") });
        Assert.True(HasError(m, "Branch 'L12' X is not a finite number"));
    }

    [Fact]
    public void Non_finite_generator_output_is_an_error()
    {
        GridModel m = Model(
            new[] { B(1, BusType.Slack) },
            gens: new[] { new Generator(1, Pg: double.NaN, Name: "G1") });
        Assert.True(HasError(m, "Generator 'G1' Pg is not a finite number"));
    }

    [Fact]
    public void Infinite_generator_q_limits_are_not_flagged()
    {
        // +/-Infinity Qmin/Qmax is the "no reactive limit" default and must stay clean.
        GridModel m = Model(new[] { B(1, BusType.Slack) }, gens: new[] { new Generator(1, Pg: 10) });
        Assert.DoesNotContain(m.Validate(), d => d.Message.Contains("finite number"));
    }

    // ---- a clean, published model produces no diagnostics -------------------------------------

    [Fact]
    public void Clean_case9_produces_no_diagnostics()
    {
        Assert.Empty(IeeeCases.Case9().Validate());
    }

    // ---- loader wiring ------------------------------------------------------------------------

    [Fact]
    public void Matpower_loader_hard_fails_on_a_duplicate_bus_id()
    {
        // Two rows share bus id 1; there is a reference bus, so the parse reaches the validate net.
        const string s = @"function mpc = dup
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 138 1 1.1 0.9; 1 1 10 0 0 0 1 1 0 138 1 1.1 0.9; 2 1 0 0 0 0 1 1 0 138 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0 0.1 0 0 0 0 0 0 1 -360 360; ];";
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(s));
        Assert.Contains("Duplicate bus id", ex.Message);
    }

    // ---- GDA data-merge pipeline is warn-only: it must never hard-fail -------------------------

    [Fact]
    public void Gda_builder_warns_but_never_throws_on_a_benign_diagnostic()
    {
        // Two independent GSP groups => two islands => two slack buses => a benign multi-island
        // warning. The GDA builder must fold this into its notes and still return a model.
        var ds = new LtdsDataset(
            "test", ImpedanceConvention.OhmicReferredToGsp,
            Nodes: new[]
            {
                new LtdsNode("NGED", "GSP_A", "Alpha GSP", "BSP", "Alpha 132kV", "Energised"),
                new LtdsNode("NGED", "P_A", "Alpha Primary", "BSP", "Alpha 132kV", "Energised"),
                new LtdsNode("NGED", "GSP_B", "Beta GSP", "BSP", "Beta 132kV", "Energised"),
                new LtdsNode("NGED", "P_B", "Beta Primary", "BSP", "Beta 132kV", "Energised"),
            },
            Circuits: Array.Empty<LtdsCircuit>(),
            Transformers2: new[]
            {
                new LtdsTransformer2("NGED", "Alpha 132kV", "GSP_A", 132, "P_A", 33, "Energised", "Dyn11", 2, 50, 45, -10, 10, 90, 100),
                new LtdsTransformer2("NGED", "Beta 132kV", "GSP_B", 132, "P_B", 33, "Energised", "Dyn11", 2, 50, 45, -10, 10, 90, 100),
            },
            Transformers3: Array.Empty<LtdsTransformer3>(),
            Demand: Array.Empty<LtdsDemand>(),
            Generation: Array.Empty<LtdsGeneration>());

        GdaBuildResult result = GdaModelBuilder.Build(ds);   // must not throw

        Assert.True(result.Model.Buses.Count(b => b.Type == BusType.Slack) > 1);
        Assert.Contains(result.Diagnostics.Notes, note => note.StartsWith("validation:"));
    }
}
