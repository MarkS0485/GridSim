using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Dgs;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.Substations.DgsFixtures;

namespace GridSim.Tests.Substations;

/// <summary>
/// RC4 R4-11 part C: the DGS reader, class map and importer, on SYNTHETIC hand-written fixtures (see DgsFixtures). Nothing
/// here claims PowerFactory behaviour: the oracles are hand-built GridSim models and closed forms.
/// </summary>
public sealed class DgsImportTests
{
    private static DgsImportResult Import(string text, bool accept = true) =>
        DgsImporter.Import(DgsTableReader.Read(text), new DgsImportOptions(accept));

    private static LineFlow Flow(PowerFlowResult r, string name) => r.LineFlows.Single(f => f.Name == name);

    // ------------------------------------------------------------------------------------------------ reader

    [Fact]
    public void The_reader_gives_typed_tables_with_source_line_numbers()
    {
        DgsFile f = DgsTableReader.Read(TwoBus());
        Assert.Equal("5.0", f.Version);
        Assert.Contains(f.General, g => g.Descr == "Origin" && g.Val.StartsWith("SYNTHETIC", StringComparison.Ordinal));
        DgsTable t = f.Table("ElmTerm")!;
        Assert.Equal(new[] { "ID", "loc_name", "uknom" }, t.Columns.Select(c => c.Name));
        Assert.Equal(new[] { 'a', 'a', 'r' }, t.Columns.Select(c => c.Type));
        Assert.Equal(40, t.Columns[0].Length);
        Assert.Equal(new[] { "T1", "T2" }, t.Rows.Select(r => r.Id));
        Assert.Equal(110.0, t.Rows[1].Real("uknom"));
        Assert.Equal("Bus two", t.Rows[1].Text("loc_name"));
        // Header line 4 is "$$ElmTerm"; rows follow on 5 and 6 (the General block is lines 1-3).
        Assert.Equal(4, t.HeaderLine);
        Assert.Equal(new[] { 5, 6 }, t.Rows.Select(r => r.Line));
    }

    [Fact]
    public void The_reader_accepts_quoted_values_with_delimiters_and_crlf_and_a_bom()
    {
        string text = "﻿" + (Header + "$$ElmTerm;ID(a:40);loc_name(a:40);uknom(r:20)\n" + "\"T;1\";\"Bus \"\"one\"\"\";110\n").Replace("\n", "\r\n");
        DgsTable t = DgsTableReader.Read(text).Table("ElmTerm")!;
        Assert.Equal("T;1", t.Rows[0].Id);
        Assert.Equal("Bus \"one\"", t.Rows[0].Text("loc_name"));
    }

    [Theory]
    [InlineData("$$General;ID(a:40);Descr(a:40);Val(a:40)\ng1;Version;6.0\n", "version '6.0' is not supported")]
    [InlineData("$$General;ID(a:40);Descr(a:40);Val(a:40)\ng1;Other;x\n", "no row with Descr = Version")]
    [InlineData("$$ElmTerm;ID(a:40)\nT1\n", "no General table")]
    public void The_reader_refuses_a_missing_or_unsupported_version_by_name(string text, string expected)
    {
        DgsFormatException ex = Assert.Throws<DgsFormatException>(() => DgsTableReader.Read(text));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_refuses_what_it_cannot_confirm_naming_the_line()
    {
        // An unknown column type, a short row, a decimal comma in a real column, a repeated ID, data before a header,
        // a repeated table: each is named with its line number and nothing is guessed.
        string text = Header
            + "$$ElmTerm;ID(a:40);uknom(r:20);when(d:8)\n"                    // line 4: type d is UNCONFIRMED
            + "$$ElmLod;ID(a:40);plini(r:20)\n"                               // line 5
            + "LD1;4,5\n"                                                     // line 6: decimal comma -> 2 values? no: 1 delimiter -> "LD1","4,5"
            + "LD2\n"                                                         // line 7: one value for two columns
            + "LD3;1\nLD3;2\n"                                                // lines 8, 9: repeated ID
            + "$$ElmLod;ID(a:40)\n";                                          // line 10: repeated table
        DgsFormatException ex = Assert.Throws<DgsFormatException>(() => DgsTableReader.Read(text));
        string m = ex.Message;
        Assert.Contains("line 4: column 'when' has type 'd'", m, StringComparison.Ordinal);
        Assert.Contains("line 6: column 'plini' is real (r) but '4,5'", m, StringComparison.Ordinal);
        Assert.Contains("line 7: table 'ElmLod' declares 2 column(s) but the row has 1", m, StringComparison.Ordinal);
        Assert.Contains("line 9: ID 'LD3' repeats the ID at line 8", m, StringComparison.Ordinal);
        Assert.Contains("line 10: table 'ElmLod' appears a second time", m, StringComparison.Ordinal);

        DgsFormatException early = Assert.Throws<DgsFormatException>(() => DgsTableReader.Read("T1;110\n" + Header));
        Assert.Contains("line 1: a data line before any table header", early.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_repeated_in_a_different_table_is_refused_because_ids_are_unique_across_the_file()
    {
        string text = Header + "$$ElmTerm;ID(a:40)\nA\n$$ElmLod;ID(a:40)\nA\n";
        DgsFormatException ex = Assert.Throws<DgsFormatException>(() => DgsTableReader.Read(text));
        Assert.Contains("ID 'A' repeats the ID at line 5 (table 'ElmTerm')", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ class map

    [Fact]
    public void The_class_map_names_every_attribute_once_with_its_disposition_and_its_confidence()
    {
        foreach (DgsClassMapEntry e in DgsClassMap.Entries)
        {
            Assert.Equal(e.Attributes.Count, e.Attributes.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(e.Attributes.Where(a => a.Disposition != DgsDisposition.Refused), a => Assert.False(string.IsNullOrWhiteSpace(a.Target)));
        }
        Assert.Equal(DgsClassMap.Entries.Count, DgsClassMap.Entries.Select(e => e.Class).Distinct(StringComparer.Ordinal).Count());
        // The attributes the cited public sources confirm (PowSyBl docs, OPAL-RT docs) are marked Confirmed ...
        foreach ((string c, string a) in new[] { ("ElmTerm", "uknom"), ("ElmLne", "dline"), ("TypLne", "rline"), ("TypLne", "xline"), ("TypLne", "bline"),
                     ("ElmLod", "plini"), ("ElmLod", "qlini"), ("ElmLod", "scale0"), ("ElmCoup", "on_off"), ("ElmCoup", "aUsage"), ("ElmSym", "ip_ctrl") })
            Assert.Equal(DgsConfidence.Confirmed, DgsClassMap.Find(c)!.Attribute(a)!.Confidence);
        // ... and the rest is listed as UNCONFIRMED (never silently trusted).
        Assert.Contains("ElmTr2.typ_id", DgsClassMap.UnconfirmedAttributes());
        Assert.Contains("StaCubic.obj_id", DgsClassMap.UnconfirmedAttributes());
        Assert.Contains("ElmXnet.usetp", DgsClassMap.UnconfirmedAttributes());
        // Every disposition kind named in the plan is used somewhere.
        var kinds = DgsClassMap.Entries.SelectMany(e => e.Attributes).Select(a => a.Disposition).Distinct().ToHashSet();
        Assert.Contains(DgsDisposition.Mapped, kinds);
        Assert.Contains(DgsDisposition.Approximated, kinds);
        Assert.Contains(DgsDisposition.Carried, kinds);
        Assert.Contains(DgsDisposition.Refused, kinds);
    }

    [Fact]
    public void Without_acceptance_every_object_that_needs_an_unconfirmed_mapping_is_refused_by_name()
    {
        DgsImportResult r = Import(TwoBus(), accept: false);
        Assert.False(r.Complete);
        Assert.Empty(r.UnconfirmedUsed);
        Assert.Empty(r.Model.Substations);
        // Cubicles (fold_id / obj_id / obj_bus are UNCONFIRMED) are refused, so no element has an end.
        Assert.All(r.Objects.Where(o => o.Class == "StaCubic"), o => Assert.Equal(DgsObjectOutcome.Refused, o.Outcome));
        DgsObjectRecord l1 = r.Objects.Single(o => o.Class == "ElmLne");
        Assert.Equal(DgsObjectOutcome.Refused, l1.Outcome);
        Assert.Contains("UNCONFIRMED", l1.Note, StringComparison.Ordinal);
        Assert.Contains("typ_id", l1.Note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ two bus

    private static SubstationModel HandBuiltTwoBus(double r, double x, double b)
    {
        double zb = 110.0 * 110.0 / 100.0;
        return new SubstationModel
        {
            Name = "dgs-import",
            BaseMva = 100.0,
            Substations = new[]
            {
                new Substation(DgsImporter.UnassignedSubstation, null, new[] { new VoltageLevel("kV110", 110) }, Array.Empty<SubstationBay>(),
                    new[] { new ConnectivityNode("T1", "kV110"), new ConnectivityNode("T2", "kV110") }, Array.Empty<NbSwitch>()),
            },
            Lines = new[] { new NbLine("L1", "T1", "T2", r * 50 / zb, x * 50 / zb, b * 1e-6 * 50 * zb, Math.Sqrt(3.0) * 110 * 0.5) },
            Loads = new[] { new NbLoad("LD1", "T2", 40, 10) },
            Generators = new[] { new NbGenerator("X1", "T1", 0, 1.02, 0, Slack: true) },
        };
    }

    [Fact]
    public void The_two_bus_import_equals_the_hand_built_model_and_solves_bit_for_bit()
    {
        DgsImportResult r = Import(TwoBus());
        Assert.True(r.Complete);
        Assert.Equal(ImportProvenanceTag.FormatFromPublicDescription, r.Provenance);
        SubstationModel hand = HandBuiltTwoBus(0.1, 0.4, 2.5);
        Assert.Equal(SubstationJson.Write(hand), SubstationJson.Write(r.Model));

        SubstationProjectionResult p = r.Project();
        double zb = 110.0 * 110.0 / 100.0;
        var handGrid = new GridModel
        {
            BaseMva = 100.0,
            Name = "dgs-import",
            Buses = new[]
            {
                new Bus(1, BusType.Slack, 0, 0, 0, 0, 110, 1.02, Name: "T1"),
                new Bus(2, BusType.Pq, 40, 10, 0, 0, 110, 1.0, Name: "T2"),
            },
            Generators = new[] { new Generator(1, 0, 0, 1.02, Name: "X1") },
            Branches = new[] { new Branch(1, 2, 0.1 * 50 / zb, 0.4 * 50 / zb, 2.5 * 1e-6 * 50 * zb, RateAMva: Math.Sqrt(3.0) * 110 * 0.5, Name: "L1") },
        };
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(p.Model);
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(handGrid);
        Assert.True(a.Converged && b.Converged);
        Assert.Equal(b.Iterations, a.Iterations);
        Assert.Equal(b.BusIds, a.BusIds);
        Assert.Equal(b.Vm, a.Vm);
        Assert.Equal(b.VaDeg, a.VaDeg);
        Assert.Equal(b.PInjMw, a.PInjMw);
        Assert.Equal(b.QInjMvar, a.QInjMvar);
        Assert.Equal(b.TotalLossMw, a.TotalLossMw);
        Assert.Equal(b.LineFlows, a.LineFlows);
    }

    [Fact]
    public void The_lossless_two_bus_import_matches_the_closed_form_receiving_end_voltage()
    {
        // R = 0 and no charging: V1^2 V2^2 = (V2^2 + QX)^2 + (PX)^2, i.e. V2^4 + (2QX - V1^2) V2^2 + X^2 (P^2 + Q^2) = 0.
        DgsImportResult r = Import(TwoBus(rline: "0", xline: "0.4", bline: "0"));
        PowerFlowResult s = new NewtonRaphsonPowerFlow().Solve(r.Project().Model);
        Assert.True(s.Converged);
        double x = 0.4 * 50 / (110.0 * 110.0 / 100.0), v1 = 1.02, p = 0.40, q = 0.10;
        double c = v1 * v1 - 2 * q * x;
        double v2sq = (c + Math.Sqrt(c * c - 4 * x * x * (p * p + q * q))) / 2;
        Assert.Equal(Math.Sqrt(v2sq), s.Vm[s.BusIds.ToList().IndexOf(2)], 7);
        Assert.Equal(0.0, s.TotalLossMw, 6);
        Assert.Equal(40.0, Flow(s, "L1").PFromMw, 6);
    }

    // ------------------------------------------------------------------------------------------------ substation

    private static SubstationModel HandBuiltTwoFeed(bool couplerOpen = false, bool sw1Open = false, bool sw2Open = false) => new()
    {
        Name = "dgs-import",
        BaseMva = 100.0,
        Substations = new[]
        {
            new Substation("S1", "Substation one", new[] { new VoltageLevel("kV132", 132) }, Array.Empty<SubstationBay>(),
                new[] { new ConnectivityNode("C_G2.n1", "kV132"), new ConnectivityNode("C_X1.n1", "kV132"), new ConnectivityNode("T_B1", "kV132"), new ConnectivityNode("T_B2", "kV132") },
                new[]
                {
                    new NbSwitch("CP", SwitchKind.Breaker, "T_B1", "T_B2", couplerOpen),
                    new NbSwitch("SW_G2", SwitchKind.Breaker, "T_B2", "C_G2.n1", sw2Open),
                    new NbSwitch("SW_X1", SwitchKind.Breaker, "T_B1", "C_X1.n1", sw1Open),
                }),
            new Substation(DgsImporter.UnassignedSubstation, null, new[] { new VoltageLevel("kV132", 132) }, Array.Empty<SubstationBay>(),
                new[] { new ConnectivityNode("T_R", "kV132") }, Array.Empty<NbSwitch>()),
        },
        Lines = new[]
        {
            new NbLine("L1", "T_B1", "T_R", 0 * 50 / (132.0 * 132.0 / 100.0), 0.4 * 50 / (132.0 * 132.0 / 100.0), 0, 0),
            new NbLine("L2", "T_B2", "T_R", 0 * 50 / (132.0 * 132.0 / 100.0), 0.4 * 50 / (132.0 * 132.0 / 100.0), 0, 0),
        },
        Loads = new[] { new NbLoad("LD", "T_R", 80, 0) },
        Generators = new[] { new NbGenerator("G2", "C_G2.n1", 30, 1.0, 0, Slack: false), new NbGenerator("X1", "C_X1.n1", 0, 1.0, 0, Slack: true) },
    };

    [Fact]
    public void The_substation_import_equals_the_hand_built_node_breaker_model_with_dgs_switch_states()
    {
        Assert.Equal(SubstationJson.Write(HandBuiltTwoFeed()), SubstationJson.Write(Import(TwoFeed(coupler: 1)).Model));
        Assert.Equal(SubstationJson.Write(HandBuiltTwoFeed(couplerOpen: true)), SubstationJson.Write(Import(TwoFeed(coupler: 0)).Model));
    }

    [Fact]
    public void Coupler_open_splits_the_busbars_and_the_flows_are_the_two_closed_form_radial_feeds()
    {
        DgsImportResult r = Import(TwoFeed(coupler: 0));
        Assert.True(r.Complete);
        SubstationProjectionResult p = r.Project();
        // Nodes in ordinal order: C_G2.n1 (rank 0), C_X1.n1 (1), T_B1 (2), T_B2 (3), T_R (4). Open coupler: X1's side is
        // C_X1.n1+T_B1 (lowest rank 1, bus 2), G2's side C_G2.n1+T_B2 (bus 1), T_R alone is bus 5.
        Assert.Equal("C_G2.n1+T_B2|C_X1.n1+T_B1|T_R", p.GroupingText());
        Assert.Equal(new[] { 1, 2, 5 }, p.Buses.Select(b => b.BusId));
        PowerFlowResult s = new NewtonRaphsonPowerFlow().Solve(p.Model);
        Assert.True(s.Converged);
        // Lossless radial feeds: G2 sends 30 MW on L2, the slack sends the other 50 MW on L1.
        Assert.Equal(50.0, Flow(s, "L1").PFromMw, 5);
        Assert.Equal(30.0, Flow(s, "L2").PFromMw, 5);

        // Bit for bit against the same network written as a bus-branch case.
        double x = 0.4 * 50 / (132.0 * 132.0 / 100.0);
        var hand = new GridModel
        {
            BaseMva = 100.0,
            Name = "dgs-import",
            Buses = new[]
            {
                new Bus(1, BusType.Pv, 0, 0, 0, 0, 132, 1.0, Name: "C_G2.n1"),
                new Bus(2, BusType.Slack, 0, 0, 0, 0, 132, 1.0, Name: "C_X1.n1"),
                new Bus(5, BusType.Pq, 80, 0, 0, 0, 132, 1.0, Name: "T_R"),
            },
            Generators = new[] { new Generator(1, 30, 0, 1.0, Name: "G2"), new Generator(2, 0, 0, 1.0, Name: "X1") },
            Branches = new[] { new Branch(2, 5, 0 * 50 / (132.0 * 132.0 / 100.0), x, 0, Name: "L1"), new Branch(1, 5, 0 * 50 / (132.0 * 132.0 / 100.0), x, 0, Name: "L2") },
        };
        PowerFlowResult h = new NewtonRaphsonPowerFlow().Solve(hand);
        Assert.Equal(h.Vm, s.Vm);
        Assert.Equal(h.VaDeg, s.VaDeg);
        Assert.Equal(h.PInjMw, s.PInjMw);
        Assert.Equal(h.LineFlows, s.LineFlows);
    }

    [Fact]
    public void Coupler_closed_makes_one_busbar_and_the_two_equal_lines_share_the_load()
    {
        SubstationProjectionResult p = Import(TwoFeed(coupler: 1)).Project();
        Assert.Equal("C_G2.n1+C_X1.n1+T_B1+T_B2|T_R", p.GroupingText());
        PowerFlowResult s = new NewtonRaphsonPowerFlow().Solve(p.Model);
        Assert.True(s.Converged);
        Assert.Equal(40.0, Flow(s, "L1").PFromMw, 5);
        Assert.Equal(40.0, Flow(s, "L2").PFromMw, 5);
    }

    [Fact]
    public void An_open_cubicle_switch_isolates_its_machine_and_the_accounting_still_adds_up()
    {
        // SW_G2 open: G2 is alone on C_G2.n1 (one terminal), so it is isolated and not connected.
        SubstationProjectionResult p = Import(TwoFeed(coupler: 1, sw2: 0)).Project();
        EquipmentOutcome g2 = p.Equipment.Single(e => e.EquipmentId == "G2");
        Assert.Equal(EquipmentDisposition.Isolated, g2.Disposition);
        Assert.Equal(30.0, p.GenerationMwTotal - p.GenerationMwConnected);
        Assert.Equal(80.0, p.DemandMwServed);
        PowerFlowResult s = new NewtonRaphsonPowerFlow().Solve(p.Model);
        Assert.Equal(40.0, Flow(s, "L1").PFromMw, 5);
    }

    [Fact]
    public void Every_object_keeps_its_dgs_identity_and_the_report_maps_buses_back_to_terminals()
    {
        DgsImportResult r = Import(TwoFeed(coupler: 0));
        Assert.Equal(("ElmCoup", "CP"), (r.Identity["CP"].Class, r.Identity["CP"].Id));
        Assert.Equal("Coupler", r.Identity["CP"].LocName);
        Assert.Equal("StaSwitch", r.Identity["SW_X1"].Class);
        Assert.Equal("ElmTerm", r.Identity["T_B1"].Class);
        Assert.Equal("StaCubic", r.Identity["C_X1.n1"].Class);   // the node a cubicle switch adds
        Assert.Equal("ElmSym", r.Identity["G2"].Class);
        Assert.Equal("Machine two", r.Identity["G2"].LocName);
        Assert.True(r.Identity["L1"].Line > 0);
        string json = DgsImportReport.ToJson(r, r.Project());
        Assert.Contains("\"provenance\": \"FormatFromPublicDescription\"", json, StringComparison.Ordinal);
        using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
        {
            var bus1 = doc.RootElement.GetProperty("projection").GetProperty("busMap")[0];
            Assert.Equal(1, bus1.GetProperty("bus").GetInt32());
            Assert.Equal(new[] { "T_B2" }, bus1.GetProperty("dgsTerminals").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Contains("ElmXnet.bustp", r.UnconfirmedUsed);
    }

    // ------------------------------------------------------------------------------------------------ transformer and shunt

    [Fact]
    public void A_two_winding_transformer_and_a_shunt_map_to_hand_derived_per_unit_values()
    {
        string text = Header + string.Join("\n",
            "$$ElmTerm;ID(a:40);uknom(r:20)", "H;110", "L;33",
            "$$TypTr2;ID(a:40);strn(r:20);utrn_h(r:20);utrn_l(r:20);uktr(r:20);pcutr(r:20);i0tr(r:20)", "TT;40;115;33;10;200;0.3",
            "$$ElmTr2;ID(a:40);typ_id(p:40);nntap(i:4)", "TR1;TT;0",
            "$$ElmXnet;ID(a:40);bustp(a:2)", "X;SL",
            "$$ElmShnt;ID(a:40);qcapn(r:20);shtype(a:2)", "SH;12;2",
            "$$ElmLod;ID(a:40);plini(r:20);qlini(r:20)", "LD;20;5",
            "$$StaCubic;ID(a:40);fold_id(p:40);obj_id(p:40);obj_bus(i:2)", "C1;H;X;0", "C2;H;TR1;0", "C3;L;TR1;1", "C4;L;SH;0", "C5;L;LD;0") + "\n";
        DgsImportResult r = Import(text);
        NbTransformer t = r.Model.Transformers.Single();
        // zk = 0.10 on 40 MVA; rk = 200 kW / 40 MVA = 0.005; xk = sqrt(0.1^2 - 0.005^2); system base 100 MVA scales by 100/40.
        Assert.Equal(0.005 * 2.5, t.R, 12);
        Assert.Equal(Math.Sqrt(0.01 - 0.000025) * 2.5, t.X, 12);
        // Off-nominal ratio: rated 115 kV against a 110 kV terminal on the high side, 33 against 33 on the low side.
        Assert.Equal((115.0 / 110.0) / (33.0 / 33.0), t.TapRatio);
        Assert.Equal(40.0, t.RateAMva);
        Assert.Equal(-12.0, r.Model.Shunts.Single().Bs);
        Assert.Equal(DgsObjectOutcome.Approximated, r.Objects.Single(o => o.Id == "TR1").Outcome);   // i0tr is not modelled
        Assert.Contains("magnetising", r.Objects.Single(o => o.Id == "TR1").Note, StringComparison.Ordinal);
        PowerFlowResult s = new NewtonRaphsonPowerFlow().Solve(r.Project().Model);
        Assert.True(s.Converged);
    }

    // ------------------------------------------------------------------------------------------------ refusals and reporting

    [Fact]
    public void Unsupported_classes_are_listed_by_name_with_their_rows_and_never_dropped_silently()
    {
        string text = TwoBus() + "$$ElmTr3;ID(a:40);loc_name(a:40)\nTX3;Three winder\nTX4;Another\n$$ElmFoo;ID(a:40)\nF1\n";
        DgsImportResult r = Import(text);
        Assert.False(r.Complete);
        Assert.Equal(new[] { "ElmTr3", "ElmFoo" }, r.Unsupported.Select(u => u.Class));
        Assert.Equal(2, r.Unsupported[0].Rows);
        Assert.True(r.Unsupported[0].KnownAndRefused);
        Assert.False(r.Unsupported[1].KnownAndRefused);
        string json = DgsImportReport.ToJson(r, r.Project());
        Assert.Contains("\"class\": \"ElmTr3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"class\": \"ElmFoo\"", json, StringComparison.Ordinal);

        // No silent drop: every table of the file is either interpreted (its objects have records) or listed as unsupported.
        DgsFile f = DgsTableReader.Read(text);
        foreach (DgsTable t in f.Tables)
        {
            bool listed = r.Unsupported.Any(u => u.Class == t.Name) || r.Objects.Count(o => o.Class == t.Name) == t.Rows.Count;
            Assert.True(listed, t.Name);
        }
    }

    [Fact]
    public void Unread_columns_are_reported_per_class()
    {
        string text = TwoBus()
            .Replace("$$ElmLod;ID(a:40);loc_name(a:40);plini(r:20);qlini(r:20);scale0(r:20)\nLD1;Load;40;10;1",
                     "$$ElmLod;ID(a:40);loc_name(a:40);plini(r:20);qlini(r:20);scale0(r:20);cosn(r:20)\nLD1;Load;40;10;1;0.95");
        DgsImportResult r = Import(text);
        Assert.Equal(new[] { "cosn" }, r.UnreadColumns["ElmLod"]);
    }

    [Fact]
    public void Individual_refusals_name_the_object_and_the_reason()
    {
        string text = TwoFeed(coupler: 1)
            .Replace("CP;Coupler;1;cbk", "CP;Coupler;1;xyz")                       // unknown switch usage
            .Replace("L2;Line two;TL;50", "L2;Line two;TL;-5")                    // length not above zero
            .Replace("SW_G2;C_G2;1;cbk", "SW_G2;C_Q;1;cbk");                      // cubicle that does not exist
        DgsImportResult r = Import(text);
        Assert.Equal(DgsObjectOutcome.Refused, r.Objects.Single(o => o.Id == "CP").Outcome);
        Assert.Contains("aUsage 'xyz'", r.Objects.Single(o => o.Id == "CP").Note, StringComparison.Ordinal);
        Assert.Contains("dline must be above 0", r.Objects.Single(o => o.Id == "L2").Note, StringComparison.Ordinal);
        Assert.Contains("names no usable cubicle", r.Objects.Single(o => o.Id == "SW_G2").Note, StringComparison.Ordinal);
        Assert.False(r.Complete);
    }

    [Fact]
    public void A_switch_without_a_state_and_a_coupler_across_substations_are_refused_not_guessed()
    {
        DgsImportResult noState = Import(TwoFeed().Replace("CP;Coupler;1;cbk", "CP;Coupler;;cbk"));
        Assert.Contains("a switch state is never inferred", noState.Objects.Single(o => o.Id == "CP").Note, StringComparison.Ordinal);

        DgsImportResult across = Import(TwoFeed().Replace("T_B2;Busbar 2;132;S1", "T_B2;Busbar 2;132;"));
        Assert.Contains("different substations", across.Objects.Single(o => o.Id == "CP").Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Out_of_service_elements_are_left_out_and_listed_and_a_tap_position_is_refused()
    {
        string oos = TwoBus().Replace("L1;Line one;TL;50;0", "L1;Line one;TL;50;1");
        DgsImportResult r = Import(oos);
        Assert.Equal(DgsObjectOutcome.OutOfService, r.Objects.Single(o => o.Id == "L1").Outcome);
        Assert.Empty(r.Model.Lines);

        string tap = Header + string.Join("\n",
            "$$ElmTerm;ID(a:40);uknom(r:20)", "H;110", "L;33",
            "$$TypTr2;ID(a:40);strn(r:20);utrn_h(r:20);utrn_l(r:20);uktr(r:20);pcutr(r:20)", "TT;40;110;33;10;200",
            "$$ElmTr2;ID(a:40);typ_id(p:40);nntap(i:4)", "TR1;TT;2",
            "$$StaCubic;ID(a:40);fold_id(p:40);obj_id(p:40);obj_bus(i:2)", "C2;H;TR1;0", "C3;L;TR1;1") + "\n";
        Assert.Contains("nntap = 2", Import(tap).Objects.Single(o => o.Id == "TR1").Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_terminal_no_imported_object_uses_is_carried_and_left_out_of_the_model()
    {
        string text = TwoBus().Replace("T2;Bus two;110\n", "T2;Bus two;110\nT3;Spare;110\n");
        DgsImportResult r = Import(text);
        Assert.Equal(DgsObjectOutcome.Carried, r.Objects.Single(o => o.Id == "T3").Outcome);
        Assert.DoesNotContain(r.Model.AllNodes, n => n.Id == "T3");
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_of_tables_or_rows_and_repeats_exactly()
    {
        string a = TwoFeed(coupler: 0);
        DgsImportResult x = Import(a), y = Import(a), z = Import(Reversed(a));
        Assert.Equal(SubstationJson.Write(x.Model), SubstationJson.Write(y.Model));
        Assert.Equal(SubstationJson.Write(x.Model), SubstationJson.Write(z.Model));
        Assert.Equal(DgsImportReport.ToJson(x, x.Project()), DgsImportReport.ToJson(y, y.Project()));
    }

    [Fact]
    public void The_provenance_label_is_a_separate_output_only_type_and_leaves_the_pinned_provenance_enum_alone()
    {
        // ProvenanceTag stays exactly as the existing guards pin it (seven members, trust ranks, schema enums).
        Assert.Equal(7, Enum.GetValues<ProvenanceTag>().Length);
        Assert.DoesNotContain("FormatFromPublicDescription", Enum.GetNames<ProvenanceTag>());
        Assert.Equal(new[] { "FormatFromPublicDescription" }, Enum.GetNames<ImportProvenanceTag>());
    }
}
