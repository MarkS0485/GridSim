using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// The GDA build data contract (October 2026 review, workstream WS7): what the LTDS
/// builder is allowed to put into a model, and where. Every test builds a tiny
/// synthetic <see cref="LtdsDataset"/> (no real grid data) that reproduces one review
/// finding's failure scenario, then pins the corrected behaviour with exact figures.
/// </summary>
public class GdaBuildDataContractTests
{
    // ---- fixture helpers -------------------------------------------------------------
    // The ohmic convention refers impedances to the GSP voltage parsed from the group
    // name, so every GSP group name here carries "132kV".

    private static LtdsNode Node(string code, string gsp, string licence = "NGED") =>
        new(licence, code, code, "BSP", gsp, "Energised");

    private static LtdsCircuit Series(string gsp, string n1, string n2, double kv, string licence = "NGED") =>
        new(licence, gsp, "BSP", n1, n2, "Energised", kv, "C",
            R1Ohm: 1.0, X1Ohm: 5.0, B1MicroS: 0, R0Ohm: 1, X0Ohm: 5,
            WinterRatingMva: 100, SummerRatingMva: 100, LengthKm: 1.0, Equipment: "Circuit");

    private static LtdsCircuit Tie(string gsp, string n1, string n2, double kv, string equipment = "Busbar", string licence = "NGED") =>
        new(licence, gsp, "BSP", n1, n2, "Energised", kv, "T",
            R1Ohm: 0, X1Ohm: 0, B1MicroS: 0, R0Ohm: 0, X0Ohm: 0,
            WinterRatingMva: 0, SummerRatingMva: 0, LengthKm: 0, Equipment: equipment);

    private static LtdsTransformer2 Tx(string gsp, string n1, double kv1, string n2, double kv2,
        double minTap = -10, double maxTap = 10, string licence = "NGED") =>
        new(licence, gsp, n1, kv1, n2, kv2, "Energised", "Dyn11",
            R1Ohm: 2, X1Ohm: 50, X0Ohm: 45, MinTap: minTap, MaxTap: maxTap,
            NominalRatingMva: 90, EmergencyRatingMva: 100);

    private static LtdsDemand Demand(string gsp, string primary, string node, double mw, double pf = 0.95) =>
        new(gsp, "BSP", primary, node, Kv: 33, MaxDemandMw: mw, PowerFactor: pf, FirmCapacityMva: 60);

    private static LtdsGeneration Gen(string gsp, string primary, double capMw, string status = "Connected", string fuel = "CCGT") =>
        new(gsp, "BSP", primary, Kv: 33, ExportCapacityMw: capMw, FuelType: fuel, Status: status);

    private static LtdsDataset Dataset(
        IReadOnlyList<LtdsNode> nodes,
        IReadOnlyList<LtdsCircuit>? circuits = null,
        IReadOnlyList<LtdsTransformer2>? tx2 = null,
        IReadOnlyList<LtdsDemand>? demand = null,
        IReadOnlyList<LtdsGeneration>? gen = null) =>
        new("synthetic", ImpedanceConvention.OhmicReferredToGsp,
            nodes,
            circuits ?? Array.Empty<LtdsCircuit>(),
            tx2 ?? Array.Empty<LtdsTransformer2>(),
            Array.Empty<LtdsTransformer3>(),
            demand ?? Array.Empty<LtdsDemand>(),
            gen ?? Array.Empty<LtdsGeneration>());

    private static Bus BusNamed(GridModel m, string name) => m.Buses.Single(b => b.Name == name);

    // =================================================================================
    // R014: the OLTC must control the To (untapped) end of its transformer branch.
    // SteadyStateSolver lowers the tap to RAISE the controlled bus, which is only true
    // for the To end (V_to ~ V_from / tap). The builder therefore orients every
    // transformer branch HV -> LV so the LV bus it regulates is always the To end.
    // =================================================================================

    private const string G = "Test 132kV";

    /// <summary>
    /// 132/33 kV transformer with its LV winding listed FIRST (Node1 = 33 kV), feeding
    /// 30 MW at 0.95 pf down a short 33 kV cable. Unregulated, the 33 kV side sags to
    /// about 0.97 p.u., below the 0.99 lower deadband edge, so the OLTC must act.
    /// </summary>
    private static LtdsDataset LvFirstTransformer(bool lvFirst) => Dataset(
        new[] { Node("GSP", G), Node("P", G), Node("L", G) },
        circuits: new[] { Series(G, "P", "L", 33) },
        tx2: new[] { lvFirst ? Tx(G, "P", 33, "GSP", 132) : Tx(G, "GSP", 132, "P", 33) },
        demand: new[] { Demand(G, "LoadSub", "L", 30) });

    [Fact]
    public void R014_lv_first_transformer_is_oriented_hv_to_lv_and_the_oltc_controls_the_to_end()
    {
        GdaBuildResult r = GdaModelBuilder.Build(LvFirstTransformer(lvFirst: true));
        Branch tx = r.Model.Branches.Single(b => b.IsTransformer);
        Oltc o = Assert.Single(r.Model.Oltcs);

        Assert.Equal(tx.Name, o.Transformer);
        Assert.Equal(BusNamed(r.Model, "GSP").Id, tx.From);   // HV end is From (tapped side)
        Assert.Equal(BusNamed(r.Model, "P").Id, tx.To);       // LV end is To (untapped side)
        Assert.Equal(tx.To, o.ControlsBus);                   // the OLTC regulates the To end
    }

    [Fact]
    public void R014_every_emitted_oltc_controls_the_to_end_of_its_transformer()
    {
        // Mixed orientations in one build: one LV-first and one HV-first transformer.
        var ds = Dataset(
            new[] { Node("GSP", G), Node("P1", G), Node("P2", G), Node("L1", G), Node("L2", G) },
            circuits: new[] { Series(G, "P1", "L1", 33), Series(G, "P2", "L2", 33) },
            tx2: new[] { Tx(G, "P1", 33, "GSP", 132), Tx(G, "GSP", 132, "P2", 33) },
            demand: new[] { Demand(G, "S1", "L1", 10), Demand(G, "S2", "L2", 10) });
        GdaBuildResult r = GdaModelBuilder.Build(ds);

        Assert.Equal(2, r.Model.Oltcs.Count);
        foreach (Oltc o in r.Model.Oltcs)
        {
            Branch tx = r.Model.Branches.Single(b => b.Name == o.Transformer);
            Assert.Equal(tx.To, o.ControlsBus);
            Bus from = r.Model.Buses.Single(b => b.Id == tx.From);
            Bus to = r.Model.Buses.Single(b => b.Id == tx.To);
            Assert.True(from.BaseKv > to.BaseKv, $"{tx.Name}: From {from.BaseKv} kV should be the HV end above To {to.BaseKv} kV");
        }
    }

    [Fact]
    public void R014_lv_first_oltc_raises_the_sagging_lv_bus_instead_of_running_to_its_limit()
    {
        // Unregulated reference: the same network with no OLTC.
        GdaBuildResult plain = GdaModelBuilder.Build(LvFirstTransformer(lvFirst: true), new GdaBuildOptions { CreateOltcs = false });
        PowerFlowResult pf0 = new SteadyStateSolver().Solve(plain.Model);
        Assert.True(pf0.Converged);
        double vUnregulated = pf0.Vm[IndexOf(pf0, BusNamed(plain.Model, "P").Id)];
        Assert.True(vUnregulated < 0.99, $"fixture should sag below the deadband, got {vUnregulated:R}");

        GdaBuildResult r = GdaModelBuilder.Build(LvFirstTransformer(lvFirst: true));
        var solver = new SteadyStateSolver();
        PowerFlowResult pf = solver.Solve(r.Model);
        Assert.True(pf.Converged);
        double vRegulated = pf.Vm[IndexOf(pf, BusNamed(r.Model, "P").Id)];
        double tap = solver.RegulatedModel.Branches.Single(b => b.IsTransformer).TapRatio;

        // The tap was lowered (raising the To-side LV bus) but did NOT run to its 0.9 floor,
        // and the LV bus ended inside the deadband, above its unregulated voltage.
        Assert.True(tap < 1.0, $"tap should have been lowered, got {tap:R}");
        Assert.True(tap > 0.9, $"tap ran to its floor, got {tap:R}");
        Assert.True(vRegulated > vUnregulated, $"regulated {vRegulated:R} should exceed unregulated {vUnregulated:R}");
        Assert.InRange(vRegulated, 0.99, 1.01);
        // Before the fix: tap 0.9 (the floor), LV bus 0.8657256944782079 p.u., which is
        // BELOW the unregulated 0.9621544654583003 p.u.
    }

    [Fact]
    public void R014_listing_order_of_the_windings_does_not_change_the_regulated_answer()
    {
        // The same physical transformer listed LV-first and HV-first must settle on the
        // same tap and the same LV voltage, bit for bit. Before the R014 fix the LV-first
        // build ran its tap to the 0.9 floor (LV bus 0.8657 p.u.) while the HV-first build
        // settled at 0.9625 (LV bus 1.0030 p.u.).
        double Solve(bool lvFirst, out double tap)
        {
            GdaBuildResult r = GdaModelBuilder.Build(LvFirstTransformer(lvFirst));
            var s = new SteadyStateSolver();
            PowerFlowResult pf = s.Solve(r.Model);
            Assert.True(pf.Converged);
            tap = s.RegulatedModel.Branches.Single(b => b.IsTransformer).TapRatio;
            return pf.Vm[IndexOf(pf, BusNamed(r.Model, "P").Id)];
        }
        double vLv = Solve(lvFirst: true, out double tapLv);
        double vHv = Solve(lvFirst: false, out double tapHv);
        Assert.Equal(tapHv, tapLv);
        Assert.Equal(vHv, vLv);
        // Pin the settled operating point: three 0.0125 steps down from 1.0, and the LV
        // bus inside the 0.99..1.01 deadband.
        Assert.Equal(1.0 - 0.0125 - 0.0125 - 0.0125, tapLv);
        Assert.Equal(1.0030203385602803, vLv);
    }

    private static int IndexOf(PowerFlowResult pf, int busId)
    {
        for (int i = 0; i < pf.BusIds.Count; i++) if (pf.BusIds[i] == busId) return i;
        throw new InvalidOperationException($"bus {busId} not in the result");
    }

    // =================================================================================
    // R015: only CONNECTED embedded generation enters the model by default.
    // =================================================================================

    private static LtdsDataset StatusFixture(params LtdsGeneration[] gen) => Dataset(
        new[] { Node("GSP", G), Node("A", G) },
        circuits: new[] { Series(G, "GSP", "A", 132) },
        demand: new[] { Demand(G, "Substation A", "A", 100) },
        gen: gen);

    private static readonly LtdsGeneration[] MixedStatusGen =
    {
        Gen(G, "Substation A", 10, "Connected"),
        Gen(G, "Substation A", 20, "Accepted"),                    // UKPN / NPg vocabulary
        Gen(G, "Substation A", 40, "Accepted not yet Connected"),  // contains 'connected' but is NOT
        Gen(G, "Substation A", 80, "Offered"),
        Gen(G, "Substation A", 160, "Connected"),
    };

    [Fact]
    public void R015_accepted_and_offered_generation_is_excluded_by_default()
    {
        GdaBuildResult r = GdaModelBuilder.Build(StatusFixture(MixedStatusGen));

        // Only the two Connected rows (10 + 160 MW) are built. Before the fix all five
        // were: 5 generators, 310 MW.
        Assert.Equal(2, r.Model.Generators.Count);
        Assert.Equal(170.0, r.Diagnostics.TotalGenCapacityMw);
        Assert.Equal(new[] { 10.0, 160.0 }, r.Model.Generators.Select(g => g.MBase).OrderBy(x => x));

        // The 20 + 40 + 80 = 140 MW left out is counted and reported, not silent.
        Assert.Equal(3, r.Diagnostics.GenNotConnected);
        Assert.Equal(140.0, r.Diagnostics.GenNotConnectedMw);
        Assert.Equal(0, r.Diagnostics.GenUnknownStatus);
        Assert.Equal(0, r.Diagnostics.UnmatchedGen);
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "Generation: 3 matched row(s), 140.0 MW export capacity, are not connected yet and were left out " +
            "(statuses: 'Accepted', 'Accepted not yet Connected', 'Offered').");
    }

    [Fact]
    public void R015_blank_or_unrecognised_status_is_left_out_with_a_warning()
    {
        GdaBuildResult r = GdaModelBuilder.Build(StatusFixture(
            Gen(G, "Substation A", 10, "Connected"),
            Gen(G, "Substation A", 25, ""),
            Gen(G, "Substation A", 35, "Mystery")));

        Generator g = Assert.Single(r.Model.Generators);
        Assert.Equal(10.0, g.MBase);
        Assert.Equal(2, r.Diagnostics.GenUnknownStatus);
        Assert.Equal(60.0, r.Diagnostics.GenUnknownStatusMw);
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "WARNING generation: 2 matched row(s), 60.0 MW export capacity, have a blank or unrecognised connection status " +
            "and were left out (statuses: (blank), 'Mystery').");
    }

    [Fact]
    public void R015_opting_out_restores_the_old_every_row_behaviour()
    {
        GdaBuildResult r = GdaModelBuilder.Build(StatusFixture(MixedStatusGen),
            new GdaBuildOptions { ConnectedGenerationOnly = false });
        Assert.Equal(5, r.Model.Generators.Count);
        Assert.Equal(310.0, r.Diagnostics.TotalGenCapacityMw);
        Assert.Equal(0, r.Diagnostics.GenNotConnected);
        Assert.DoesNotContain(r.Diagnostics.Notes, n => n.StartsWith("Generation:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Connected", true)]
    [InlineData("CONNECTED", true)]
    [InlineData(" connected ", true)]
    [InlineData("Energised", true)]
    [InlineData("In Service", true)]
    [InlineData("Commissioned", true)]
    [InlineData("Accepted", false)]
    [InlineData("Accepted not yet Connected", false)]
    [InlineData("Offered", false)]
    [InlineData("Contracted", false)]
    [InlineData("Not Connected", false)]
    [InlineData("Disconnected", false)]
    [InlineData("Decommissioned", false)]
    [InlineData("Under Construction", false)]
    public void R015_status_vocabulary_is_classified_connected_or_not(string status, bool built)
    {
        GdaBuildResult r = GdaModelBuilder.Build(StatusFixture(Gen(G, "Substation A", 10, status)));
        Assert.Equal(built ? 1 : 0, r.Model.Generators.Count);
        Assert.Equal(built ? 0 : 1, r.Diagnostics.GenNotConnected);
        Assert.Equal(0, r.Diagnostics.GenUnknownStatus);
    }

    [Fact]
    public void R015_unmatched_rows_stay_unmatched_and_are_not_counted_as_excluded()
    {
        // An accepted row that would not have joined anyway is still just unmatched: the
        // excluded MW must be exactly what left the model.
        GdaBuildResult r = GdaModelBuilder.Build(StatusFixture(
            Gen(G, "Substation A", 10, "Connected"),
            Gen(G, "Nowhere", 20, "Accepted")));
        Assert.Single(r.Model.Generators);
        Assert.Equal(1, r.Diagnostics.UnmatchedGen);
        Assert.Equal(0, r.Diagnostics.GenNotConnected);
    }

    [Fact]
    public void R015_accepted_generation_is_not_folded_into_negative_load_either()
    {
        GdaBuildResult r = GdaModelBuilder.Build(StatusFixture(MixedStatusGen),
            new GdaBuildOptions { EmbeddedGenAsNegativeLoad = true });
        // 100 MW demand minus the dispatched output of the two Connected CCGTs only.
        double cf = FuelMap.CapacityFactor(FuelMap.Classify("CCGT").Type);
        double expected = 100.0 - 10.0 * cf - 160.0 * cf;
        Assert.Equal(expected, BusNamed(r.Model, "A").Pd);
    }

    // =================================================================================
    // R047: --minkv aggregates the demand below the cut onto the boundary bus.
    // =================================================================================

    /// <summary>
    /// GdaBuildTests' shape as a dataset: 132 kV GSP -> 132/33 kV transformer -> 33 kV
    /// busbar pair (tie) -> 33 kV cable -> 50 MW load, with a 10 MW connected CCGT at the
    /// load's primary. With MinKv = 132 only the transformer survives the cut.
    /// </summary>
    private static LtdsDataset MinKvFixture(params LtdsDemand[] extraDemand) => Dataset(
        new[] { Node("GSP_1", G), Node("BB_A", G), Node("BB_B", G), Node("LOAD_33", G) },
        circuits: new[] { Tie(G, "BB_A", "BB_B", 33), Series(G, "BB_B", "LOAD_33", 33) },
        tx2: new[] { Tx(G, "GSP_1", 132, "BB_A", 33) },
        demand: new[] { Demand(G, "LoadSub", "LOAD_33", 50) }.Concat(extraDemand).ToArray(),
        gen: new[] { Gen(G, "LoadSub", 10) });

    [Fact]
    public void R047_minkv_folds_downstream_demand_onto_the_boundary_bus()
    {
        GdaBuildResult r = GdaModelBuilder.Build(MinKvFixture(), new GdaBuildOptions { MinKv = 132 });

        Assert.Equal(2, r.Model.Buses.Count);                       // GSP_1 and the BB_A boundary
        Assert.Equal(50.0, r.Diagnostics.TotalDemandMw);            // was 0: silently dropped
        Assert.Equal(50.0, BusNamed(r.Model, "BB_A").Pd);
        double q = 50.0 * Math.Tan(Math.Acos(0.95));
        Assert.Equal(q, BusNamed(r.Model, "BB_A").Qd);
        // The primary's embedded generator follows its demand to the boundary bus (was 0
        // generators: its primary never registered a bus).
        Generator g = Assert.Single(r.Model.Generators);
        Assert.Equal(BusNamed(r.Model, "BB_A").Id, g.Bus);

        // Counted and reported.
        Assert.Equal(1, r.Diagnostics.DemandPoints);
        Assert.Equal(0, r.Diagnostics.UnmatchedDemand);
        Assert.Equal(1, r.Diagnostics.AggregatedDemand);
        Assert.Equal(50.0, r.Diagnostics.AggregatedDemandMw);
        Assert.Equal(0, r.Diagnostics.OutOfScopeDemand);
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "Demand: 1 row(s), 50.0 MW below the 132 kV cut were folded onto their nearest boundary bus " +
            "(losses and voltage drop below the cut are not modelled).");
    }

    [Fact]
    public void R047_without_minkv_the_build_is_unchanged()
    {
        GdaBuildResult r = GdaModelBuilder.Build(MinKvFixture());
        Assert.Equal(3, r.Model.Buses.Count);
        Assert.Equal(50.0, r.Diagnostics.TotalDemandMw);
        Assert.Equal(50.0, BusNamed(r.Model, "LOAD_33").Pd);
        Assert.Equal(0, r.Diagnostics.AggregatedDemand);
        Assert.Equal(0, r.Diagnostics.OutOfScopeDemand);
    }

    [Fact]
    public void R047_the_aggregated_minkv_model_solves_with_the_slack_carrying_the_folded_load()
    {
        GdaBuildResult r = GdaModelBuilder.Build(MinKvFixture(), new GdaBuildOptions { MinKv = 132, CreateOltcs = false });
        PowerFlowResult pf = new SteadyStateSolver().Solve(r.Model);
        Assert.True(pf.Converged, $"mismatch {pf.MaxMismatch:E2}");
        Assert.Equal(50.0, r.Model.Buses.Sum(b => b.Pd));
    }

    [Fact]
    public void R047_below_cut_load_reachable_only_through_a_removed_circuit_is_reported_not_folded()
    {
        // The only path from LOAD_33 to the boundary is a 'Removed' cable, so it is not part
        // of the network below the cut either. It must be counted as out of scope, loudly.
        var ds = MinKvFixture() with
        {
            Circuits = new[] { Tie(G, "BB_A", "BB_B", 33), Series(G, "BB_B", "LOAD_33", 33) with { Status = "Removed" } },
        };
        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions { MinKv = 132 });

        Assert.Equal(0.0, r.Diagnostics.TotalDemandMw);
        Assert.Equal(0, r.Diagnostics.AggregatedDemand);
        Assert.Equal(1, r.Diagnostics.OutOfScopeDemand);
        Assert.Equal(50.0, r.Diagnostics.OutOfScopeDemandMw);
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "WARNING demand: 1 row(s), 50.0 MW are on nodes outside this build's network (nodes on no in-service element) and were not included.");
    }

    [Fact]
    public void R047_demand_on_a_node_with_no_element_is_reported_in_a_full_build()
    {
        // Before the fix a demand row whose node sat on no circuit or transformer was skipped
        // with no count at all.
        GdaBuildResult r = GdaModelBuilder.Build(MinKvFixture(Demand(G, "Ghost", "GHOST", 7)));
        Assert.Equal(50.0, r.Diagnostics.TotalDemandMw);
        Assert.Equal(1, r.Diagnostics.OutOfScopeDemand);
        Assert.Equal(7.0, r.Diagnostics.OutOfScopeDemandMw);
        Assert.Contains(r.Diagnostics.Notes, n => n.StartsWith("WARNING demand: 1 row(s), 7.0 MW", StringComparison.Ordinal));
    }

    [Fact]
    public void R047_other_areas_demand_under_a_carve_is_reported_without_a_warning()
    {
        const string other = "Other 132kV";
        var ds = MinKvFixture() with
        {
            Nodes = MinKvFixture().Nodes.Append(Node("OGSP", other)).Append(Node("OL", other)).ToArray(),
            Circuits = MinKvFixture().Circuits.Append(Series(other, "OGSP", "OL", 132)).ToArray(),
            Demand = MinKvFixture().Demand.Append(Demand(other, "Other Sub", "OL", 12)).ToArray(),
        };
        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions { GspGroup = G });
        Assert.Equal(50.0, r.Diagnostics.TotalDemandMw);
        Assert.Equal(1, r.Diagnostics.OutOfScopeDemand);
        Assert.Contains(r.Diagnostics.Notes, n => n.StartsWith("Demand: 1 row(s), 12.0 MW are on nodes outside", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Diagnostics.Notes, n => n.StartsWith("WARNING demand", StringComparison.Ordinal));
    }

    [Fact]
    public void R047_equidistant_boundary_buses_resolve_deterministically_to_the_lower_bus_id()
    {
        // Two 132/33 kV transformers, each feeding a 33 kV busbar one hop from the load. The
        // load is equidistant from both boundary buses and must go to the lower bus id
        // (BB_A, created first), every time.
        var ds = Dataset(
            new[] { Node("GSP", G), Node("BB_A", G), Node("BB_C", G), Node("L", G) },
            circuits: new[] { Series(G, "BB_A", "L", 33), Series(G, "BB_C", "L", 33) },
            tx2: new[] { Tx(G, "GSP", 132, "BB_A", 33), Tx(G, "GSP", 132, "BB_C", 33) },
            demand: new[] { Demand(G, "LoadSub", "L", 20) });
        for (int run = 0; run < 3; run++)
        {
            GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions { MinKv = 132 });
            Assert.True(BusNamed(r.Model, "BB_A").Id < BusNamed(r.Model, "BB_C").Id);
            Assert.Equal(20.0, BusNamed(r.Model, "BB_A").Pd);
            Assert.Equal(0.0, BusNamed(r.Model, "BB_C").Pd);
        }
    }

    // =================================================================================
    // R048: embedded generation joins on (GSP, primary), not on the primary name alone.
    // =================================================================================

    [Fact]
    public void R048_same_named_primaries_in_two_gsps_each_receive_their_own_generation()
    {
        const string ga = "Alpha 132kV", gb = "Beta 132kV";
        var ds = Dataset(
            new[] { Node("GA", ga), Node("A1", ga), Node("GB", gb), Node("B1", gb) },
            circuits: new[] { Series(ga, "GA", "A1", 132), Series(gb, "GB", "B1", 132) },
            demand: new[] { Demand(ga, "Substation A", "A1", 10), Demand(gb, "Substation A", "B1", 10) },
            // The generation table spells the groups its own way; GspKey reconciles them.
            gen: new[] { Gen("Alpha 132 kV S Stn", "Substation A", 50), Gen("Beta 132 kV S Stn", "Substation A", 70) });

        GdaBuildResult r = GdaModelBuilder.Build(ds);

        // Before the fix both landed on B1, the last demand row to register the name.
        Assert.Equal(2, r.Model.Generators.Count);
        Assert.Equal(BusNamed(r.Model, "A1").Id, r.Model.Generators.Single(g => g.MBase == 50).Bus);
        Assert.Equal(BusNamed(r.Model, "B1").Id, r.Model.Generators.Single(g => g.MBase == 70).Bus);
        Assert.Equal(0, r.Diagnostics.GenJoinedByUniqueName);
        Assert.Equal(0, r.Diagnostics.GenAmbiguousPrimary);
        Assert.Equal(0, r.Diagnostics.UnmatchedGen);
    }

    private static LtdsDataset TwoGspFixture(params LtdsGeneration[] gen)
    {
        const string ga = "Alpha 132kV", gb = "Beta 132kV";
        return Dataset(
            new[] { Node("GA", ga), Node("A1", ga), Node("A2", ga), Node("GB", gb), Node("B1", gb) },
            circuits: new[] { Series(ga, "GA", "A1", 132), Series(ga, "GA", "A2", 132), Series(gb, "GB", "B1", 132) },
            demand: new[]
            {
                Demand(ga, "Substation A", "A1", 10), Demand(gb, "Substation A", "B1", 10),   // name in two groups
                Demand(ga, "Only Alpha", "A2", 10),                                            // name in one group
            },
            gen: gen);
    }

    [Fact]
    public void R048_unique_primary_name_still_joins_when_the_gsp_spelling_does_not_match()
    {
        // The generation table's group name reconciles with no demand group, but the primary
        // name exists in only one group, so it is unambiguous: join there, and say so.
        GdaBuildResult r = GdaModelBuilder.Build(TwoGspFixture(Gen("Unheard Of Group", "Only Alpha", 30)));
        Generator g = Assert.Single(r.Model.Generators);
        Assert.Equal(BusNamed(r.Model, "A2").Id, g.Bus);
        Assert.Equal(1, r.Diagnostics.GenJoinedByUniqueName);
        Assert.Contains(r.Diagnostics.Notes, n => n.StartsWith("Generation join: 1 row(s) joined by primary name alone", StringComparison.Ordinal));
    }

    [Fact]
    public void R048_ambiguous_primary_name_is_left_unplaced_with_a_warning()
    {
        // "Substation A" exists in Alpha and Beta; the generator's group is neither. Before
        // the fix it went to whichever demand row registered the name last (B1).
        GdaBuildResult r = GdaModelBuilder.Build(TwoGspFixture(Gen("Unheard Of Group", "Substation A", 45)));
        Assert.Empty(r.Model.Generators);
        Assert.Equal(1, r.Diagnostics.UnmatchedGen);
        Assert.Equal(1, r.Diagnostics.GenAmbiguousPrimary);
        Assert.Equal(45.0, r.Diagnostics.GenAmbiguousPrimaryMw);
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "WARNING generation join: 1 row(s), 45.0 MW export capacity, name a primary that exists under several GSP groups, " +
            "none of them the generator's own; left unplaced: 'Substation A' (Unheard Of Group).");
    }

    [Fact]
    public void R048_same_name_on_two_buses_in_one_gsp_keeps_the_last_listed_bus_and_reports_it()
    {
        // Within one GSP group the old last-writer rule is kept (deterministic, data order),
        // but it is now counted rather than silent.
        var ds = Dataset(
            new[] { Node("GSP", G), Node("X1", G), Node("X2", G) },
            circuits: new[] { Series(G, "GSP", "X1", 132), Series(G, "GSP", "X2", 132) },
            demand: new[] { Demand(G, "Sub X", "X1", 5), Demand(G, "Sub X", "X2", 5) },
            gen: new[] { Gen(G, "Sub X", 12) });
        GdaBuildResult r = GdaModelBuilder.Build(ds);
        Assert.Equal(BusNamed(r.Model, "X2").Id, Assert.Single(r.Model.Generators).Bus);
        Assert.Equal(1, r.Diagnostics.PrimariesOnSeveralBuses);
        Assert.Contains(r.Diagnostics.Notes, n => n.StartsWith("Demand: 1 primary name(s) have demand rows on more than one bus", StringComparison.Ordinal));
    }

    [Fact]
    public void R048_licence_carve_does_not_import_another_licences_same_named_plant()
    {
        const string ge = "Eastgrid 132kV", gw = "Westgrid 132kV";
        var ds = Dataset(
            new[] { Node("GE", ge, "EMIDS"), Node("E1", ge, "EMIDS"), Node("GW", gw, "WMIDS"), Node("W1", gw, "WMIDS") },
            circuits: new[] { Series(ge, "GE", "E1", 132, "EMIDS"), Series(gw, "GW", "W1", 132, "WMIDS") },
            demand: new[] { Demand(ge, "Substation A", "E1", 10), Demand(gw, "Substation A", "W1", 10) },
            gen: new[] { Gen(ge, "Substation A", 50), Gen(gw, "Substation A", 70) });

        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions { Licence = "EMIDS" });

        Generator g = Assert.Single(r.Model.Generators);           // was 2: the WMIDS 70 MW joined by name
        Assert.Equal(50.0, g.MBase);
        Assert.Equal(1, r.Diagnostics.UnmatchedGen);                // was 0
        Assert.Equal(0, r.Diagnostics.GenAmbiguousPrimary);         // its own group is known: not ambiguous, just elsewhere
    }

    // =================================================================================
    // R097: published transformer tap ranges become the OLTC limits.
    // =================================================================================

    /// <summary>The R014 network (HV listed first) with a chosen published tap range.</summary>
    private static LtdsDataset TapRangeFixture(double minTap, double maxTap) => Dataset(
        new[] { Node("GSP", G), Node("P", G), Node("L", G) },
        circuits: new[] { Series(G, "P", "L", 33) },
        tx2: new[] { Tx(G, "GSP", 132, "P", 33, minTap, maxTap) },
        demand: new[] { Demand(G, "LoadSub", "L", 30) });

    [Fact]
    public void R097_published_tap_range_sets_the_oltc_limits()
    {
        // Before the fix every OLTC got 0.9..1.1 whatever was published.
        Oltc o = Assert.Single(GdaModelBuilder.Build(TapRangeFixture(-15, 5)).Model.Oltcs);
        Assert.Equal(1.0 + -15.0 / 100.0, o.MinTap);
        Assert.Equal(1.0 + 5.0 / 100.0, o.MaxTap);
        // Step and deadband are not published, so they keep their documented values.
        Assert.Equal(0.0125, o.StepPu);
        Assert.Equal(0.01, o.DeadbandPu);
    }

    [Fact]
    public void R097_symmetric_ten_percent_range_is_bit_identical_to_the_old_hard_coded_limits()
    {
        // Every existing fixture (and the common real range) is -10 %/+10 %. The new
        // arithmetic must reproduce the old literals exactly, not merely closely.
        Oltc o = Assert.Single(GdaModelBuilder.Build(TapRangeFixture(-10, 10)).Model.Oltcs);
        Assert.Equal(0.9, o.MinTap);
        Assert.Equal(1.1, o.MaxTap);
    }

    [Fact]
    public void R097_the_solver_honours_a_narrow_published_range()
    {
        // The fixture needs three 0.0125 steps (tap 0.9625) to lift the LV bus into band
        // (see R014). With a published floor of -2.5 % the tap must stop at 0.975, the
        // published limit, not continue towards the old hard-coded 0.9.
        GdaBuildResult r = GdaModelBuilder.Build(TapRangeFixture(-2.5, 5));
        Oltc o = Assert.Single(r.Model.Oltcs);
        Assert.Equal(1.0 + -2.5 / 100.0, o.MinTap);
        var s = new SteadyStateSolver();
        Assert.True(s.Solve(r.Model).Converged);
        double tap = s.RegulatedModel.Branches.Single(b => b.IsTransformer).TapRatio;
        // Two steps down give 1.0 - 0.0125 - 0.0125 = 0.97500000000000009, one ulp-scale
        // hair above the floor 0.975 (= 1.0 + -0.025). The third step clamps to the floor,
        // a move of 1.1e-16, which is below the solver's 1e-12 "tap moved" threshold, so
        // the loop settles on the two-step value. Pin that exactly.
        Assert.Equal(1.0 - 0.0125 - 0.0125, tap);
        Assert.True(Math.Abs(tap - o.MinTap) < 1e-12, $"tap {tap:R} should sit on the published floor {o.MinTap:R}");
    }

    [Fact]
    public void R097_zero_tap_range_creates_no_oltc_and_says_so()
    {
        // Before the fix a 0 %/0 % (off-load) unit still got a 0.9..1.1 OLTC.
        GdaBuildResult r = GdaModelBuilder.Build(TapRangeFixture(0, 0));
        Assert.Empty(r.Model.Oltcs);
        Assert.Equal(1, r.Diagnostics.OltcsWithoutTapRange);
        Assert.Equal(0, r.Diagnostics.OltcsWithInvalidTapRange);
        Assert.Contains(r.Diagnostics.Notes, n => n.StartsWith("OLTC: 1 transformer(s) publish a 0 %/0 % tap range", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5, -5, "minimum above maximum")]
    [InlineData(1, 17, "range excludes the nominal 0 % tap")]       // tap POSITIONS, not percent
    [InlineData(-20, -5, "range excludes the nominal 0 % tap")]
    [InlineData(-60, 10, "beyond +/-50 %, not a percent range")]
    [InlineData(double.NaN, 10, "not a finite number")]
    public void R097_invalid_tap_range_creates_no_oltc_and_warns_loudly(double minTap, double maxTap, string reason)
    {
        GdaBuildResult r = GdaModelBuilder.Build(TapRangeFixture(minTap, maxTap));
        Assert.Empty(r.Model.Oltcs);
        Assert.Equal(1, r.Diagnostics.OltcsWithInvalidTapRange);
        Assert.Equal(0, r.Diagnostics.OltcsWithoutTapRange);
        string note = Assert.Single(r.Diagnostics.Notes, n => n.StartsWith("WARNING OLTC:", StringComparison.Ordinal));
        Assert.Contains("TX GSP/P 132/33", note);
        Assert.Contains(reason, note);
    }

    [Fact]
    public void R097_zero_impedance_cable_is_merged_but_reported()
    {
        // A cable whose R/X cells were '-' parses as 0 and can only be merged as a tie. That
        // collapses two substations into one bus, so it must be reported, not silent.
        var ds = Dataset(
            new[] { Node("GSP", G), Node("A", G), Node("B", G) },
            circuits: new[] { Series(G, "GSP", "A", 132), Tie(G, "A", "B", 132, equipment: "Cable") },
            demand: new[] { Demand(G, "Sub B", "B", 10) });
        GdaBuildResult r = GdaModelBuilder.Build(ds);

        Assert.Equal(1, r.Diagnostics.BusbarTiesMerged);              // topology unchanged: still merged
        Assert.Equal(1, r.Diagnostics.ZeroImpedanceNonTieCircuits);
        string note = Assert.Single(r.Diagnostics.Notes, n => n.StartsWith("WARNING ties:", StringComparison.Ordinal));
        Assert.Contains("A-B 'Cable'", note);
    }

    [Theory]
    [InlineData("Busbar")]
    [InlineData("Disconnector")]
    [InlineData("Bus Section Switch")]
    [InlineData("Circuit Breaker")]
    public void R097_zero_impedance_busbar_or_switch_is_a_tie_without_warning(string equipment)
    {
        var ds = Dataset(
            new[] { Node("GSP", G), Node("A", G), Node("B", G) },
            circuits: new[] { Series(G, "GSP", "A", 132), Tie(G, "A", "B", 132, equipment) },
            demand: new[] { Demand(G, "Sub B", "B", 10) });
        GdaBuildResult r = GdaModelBuilder.Build(ds);

        Assert.Equal(1, r.Diagnostics.BusbarTiesMerged);
        Assert.Equal(0, r.Diagnostics.ZeroImpedanceNonTieCircuits);
        Assert.DoesNotContain(r.Diagnostics.Notes, n => n.StartsWith("WARNING ties:", StringComparison.Ordinal));
    }
}
