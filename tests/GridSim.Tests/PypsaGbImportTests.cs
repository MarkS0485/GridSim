using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Geo;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.PyPsa;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S2 / P02 (October 2026): the PyPSA-GB importers (andrewlyden/PyPSA-GB @ 8e084af, MIT): the reduced network
/// (<c>pypsa-gb-reduced</c>: 29 substations with OSGB coordinates, PyPSA-GB's 99 lines, 6 DC interconnector links) and the
/// zonal transport model (<c>pypsa-gb-zonal</c>: 20 zones, 37 links, not AC solvable), the OSGB grid conversion they use,
/// and the boundary cuts derived from NESO's ETYS boundary GIS lines by tools/gb-reference/derive_boundaries.py.
/// </summary>
public class PypsaGbImportTests
{
    private static string Data => S10TestSupport.DataDir;
    private static string ReducedDir => Path.Combine(Data, GbReferenceModels.PypsaReducedRelativeDir);
    private static string ZonalDir => Path.Combine(Data, GbReferenceModels.PypsaZonalRelativeDir);
    private static GridModel Gb29() => MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(Path.Combine(Data, GbReferenceModels.Gb29RelativePath)));
    private static readonly PowerFlowOptions PfOptions = new() { MaxIterations = 100, EnforceQLimits = true };

    // ---- OSGB grid conversion ------------------------------------------------------------------------------------------

    [Fact]
    public void Osgb_inverse_projection_reproduces_the_ordnance_survey_worked_example()
    {
        // Ordnance Survey, "A guide to coordinate systems in Great Britain", Annex C worked example: E 651409.903 m,
        // N 313177.270 m is OSGB36 latitude 52 deg 39' 27.2531" N, longitude 1 deg 43' 4.5177" E. The guide prints the
        // answer to 0.0001 arc-second (2.8e-8 degrees), so that is the tolerance: a geodesy check against a printed
        // value, not a solver number.
        (double lat, double lon) = OsgbGrid.ToOsgb36LatLon(651409.903, 313177.270);
        Assert.Equal(52 + 39 / 60.0 + 27.2531 / 3600.0, lat, 3e-8);
        Assert.Equal(1 + 43 / 60.0 + 4.5177 / 3600.0, lon, 3e-8);
    }

    [Theory]
    // PyPSA-GB's own WGS84 copy of its bus positions (reduced_network/buses_wgs84_backup.csv), converted by PyPSA-GB
    // independently of GridSim. GridSim's Helmert conversion matches all 29 to 4.3e-8 degrees (measured); 1e-7 here.
    [InlineData(248167.62589478298, 845011.7305259121, 57.4698758, -4.533299377)]    // Beauly
    [InlineData(411831.01133776025, 843821.4508902557, 57.48448144, -1.804331289)]   // Peterhead
    [InlineData(381478.8925611077, 121917.13582157023, 50.99634637, -2.265293518)]   // S.W.Penisula
    public void Osgb_to_wgs84_matches_pypsa_gbs_own_conversion(double e, double n, double lat, double lon)
    {
        (double la, double lo) = OsgbGrid.ToWgs84(e, n);
        Assert.Equal(lat, la, 1e-7);
        Assert.Equal(lon, lo, 1e-7);
    }

    [Fact]
    public void The_gb_layout_box_is_the_bundled_cases_box_and_clamps()
    {
        Assert.Equal((0.0, 0.0), OsgbGrid.ToGbLayout(49.8, -8.2));   // the south-west corner of the box
        Assert.Equal((1.0, 1.0), OsgbGrid.ToGbLayout(60.0, 3.0));    // beyond the north-east corner: clamped
        (double cx, double cy) = OsgbGrid.ToGbLayout(49.8 + 4.6, -8.2 + 5.05);
        Assert.Equal(0.5, cx, 1e-12);                                 // the centre (floating-point rounding aside)
        Assert.Equal(0.5, cy, 1e-12);
        Assert.Equal((0.0, 1.0), OsgbGrid.ToGbLayout(70.0, -20.0));   // outside the box: clamped, never off the map
        Assert.Throws<ArgumentOutOfRangeException>(() => OsgbGrid.ToGbLayout(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OsgbGrid.ToWgs84(double.PositiveInfinity, 0));
    }

    // ---- the reduced network ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_reduced_network_imports_with_names_positions_and_exact_notes()
    {
        GridModel op = Gb29();
        PypsaGbReducedNetwork net = PypsaGbReduced.Load(ReducedDir, op);
        GridModel m = net.Model;
        Assert.Equal("pypsa-gb-reduced", m.Name);
        Assert.Equal(29, m.BusCount);
        Assert.Equal(99, m.Branches.Count);
        Assert.Equal(Enumerable.Range(1, 29), m.Buses.Select(b => b.Id));
        Assert.Equal("Beauly", m.Buses[0].Name);
        Assert.Equal("Sellindge", m.Buses[26].Name);
        Assert.Equal(BusType.Slack, m.Buses[26].Type);
        Assert.Equal(275.0, m.Buses[2].BaseKv);                    // Errochty: buses.csv wins over the .m file's 132 kV
        Assert.Same(op.AllGenerators, m.AllGenerators);            // the operating point's fleet, the very list
        Assert.All(m.Branches, b => Assert.False(b.IsTransformer));
        Assert.Equal("Beauly-Peterhead #0", m.Branches[0].Name);
        Assert.Equal("Neilston-Deeside #16", m.Branches[16].Name);
        Assert.Equal(0.0, m.Branches[16].R);
        Assert.Equal(0.0001, m.Branches[16].X);                    // PyPSA-GB's stand-in for the Western HVDC link
        Assert.Equal(2200.0, m.Branches[16].RateAMva);
        // Every bus is drawn on the GB map box (0..1), Beauly north-west of Peterhead.
        Assert.All(m.Buses, b => Assert.InRange(b.X, 0.0, 1.0));
        Assert.True(m.Buses[0].X < m.Buses[1].X);
        Assert.Equal(57.46987579453433, net.Buses[0].LatDeg, 1e-7);

        Assert.Equal(6, net.Links.Count);
        Assert.All(net.Links, l => Assert.True(l.External));
        Assert.Equal(new[] { "BritNed", "EastWest", "Moyle", "Nemo", "IFA", "IFA2" }, net.Links.Select(l => l.Name));
        Assert.Equal(2100.0, net.Links.Single(l => l.Name == "IFA").PNomMw);

        Assert.Equal(new[]
        {
            "demand, generation, bus types, starting voltages and voltage limits are taken from 'GBreducednetwork' (bus i = buses.csv row i); PyPSA-GB's network files carry none of them.",
            "bus 3 'Errochty': buses.csv gives 275 kV where the operating point 'GBreducednetwork' gives 132 kV; buses.csv is used (base kV does not enter the per-unit power flow).",
            "96 of 99 line(s) equal a branch of 'GBreducednetwork' (same ends, r, x, b and rating); 3 do not, and 3 of its 99 branch(es) have no counterpart here.",
            "lines with no counterpart in 'GBreducednetwork': 'Neilston-Deeside #16' (5-12), 'Keadby-Feckenham #53' (16-18), 'Keadby-Feckenham #54' (16-18).",
            "'GBreducednetwork' branches with no counterpart here: branch 51 (16-19), branch 52 (16-19), branch 99 (3-2).",
            "4 matched line(s) are a tap / phase-shifting transformer in 'GBreducednetwork', which lines.csv cannot express (no tap or shift column); they are plain lines here: 'Beauly-Errochty #1' = branch 2 (1-3, ratio 1, shift 2 deg); 'Beauly-Errochty #3' = branch 4 (1-3, ratio 1, shift 2 deg); 'Deeside-Feckenham #38' = branch 36 (12-18, ratio 1, shift 2 deg); 'Deeside-Daines #40' = branch 38 (12-13, ratio 1, shift 2 deg).",
            "6 link(s) in links.csv (6 to a foreign node) are carried in the import result but NOT modelled: GridSim has no HVDC element and the files give no transfer setpoint.",
            "4 boundary cut(s) read from boundaries.json (derived from the ETYS boundary GIS lines; no transfer limit is set).",
        }, net.Notes);
    }

    [Fact]
    public void The_reduced_networks_boundary_cuts_are_the_derived_ones()
    {
        GridModel m = PypsaGbReduced.Load(ReducedDir, Gb29()).Model;
        Assert.Equal(new[] { "B4 SHETL-SPT (SSE-SP)", "B6 Scotland-England (SCOTEX)", "B7 Upper North England (HARSPNBLY)", "B8 North to Midlands (FLOWSTH)" },
            m.Boundaries.Select(b => b.Name));
        Assert.All(m.Boundaries, b => Assert.Equal(0.0, b.LimitMw));   // no limit invented
        Assert.All(m.Boundaries, b => Assert.All(b.Branches, x => Assert.Equal(1, x.Direction)));   // every cut circuit is listed north end first
        Assert.Equal(new[] { "Peterhead-Denny/Bonnybridge #4", "Peterhead-Denny/Bonnybridge #5", "Errochty-Denny/Bonnybridge #6", "Errochty-Denny/Bonnybridge #7" },
            m.Boundaries[0].Branches.Select(x => x.Branch));
        // B6: the four 400 kV circuits (two Strathaven-Harker, two Eccles-Stella West) and PyPSA-GB's Western link line,
        // the same set PyPSA-GB's own neso_boundary_mapping.yaml gives SCOTEX (four AC circuits plus the Western HVDC).
        Assert.Equal(new[] { "Neilston-Deeside #16", "Strathaven-Harker #17", "Strathaven-Harker #18", "Eccles-Stella West #23", "Eccles-Stella West #24" },
            m.Boundaries[1].Branches.Select(x => x.Branch));
        Assert.Equal(5, m.Boundaries[2].Branches.Count);
        Assert.Equal(8, m.Boundaries[3].Branches.Count);
    }

    [Fact]
    public void Each_boundary_file_is_the_cut_its_audit_sides_define()
    {
        // boundary-sides.json (written by the same script) lists the north side of each cut. A circuit is in a cut exactly
        // when its ends are on different sides, with direction +1 when bus0 is north: check both derived files against it.
        using JsonDocument sides = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "matpower", "gb", "pypsa-gb", "boundary-sides.json")));
        GridModel red = PypsaGbReduced.Load(ReducedDir, Gb29()).Model;
        PypsaGbZonalNetwork zon = PypsaGbZonal.Load(ZonalDir);
        string[] busName = red.Buses.Select(b => b.Name!).ToArray();
        int k = 0;
        foreach (JsonProperty p in sides.RootElement.EnumerateObject())
        {
            string full = p.Value.GetProperty("gbFullName").GetString()!;
            var rn = p.Value.GetProperty("reducedNorth").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
            var zn = p.Value.GetProperty("zonalNorth").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
            var expectRed = red.Branches.Where(b => rn.Contains(busName[b.From - 1]) != rn.Contains(busName[b.To - 1]))
                .Select(b => (b.Name!, rn.Contains(busName[b.From - 1]) ? 1 : -1)).ToList();
            Boundary rb = red.Boundaries.Single(b => b.Name == full);
            Assert.Equal(expectRed, rb.Branches.Select(x => (x.Branch, x.Direction)).ToList());
            var expectZon = zon.Links.Where(l => !l.External && zn.Contains(l.Bus0) != zn.Contains(l.Bus1))
                .Select(l => (l.Name, zn.Contains(l.Bus0) ? 1 : -1)).ToList();
            PypsaGbZonalBoundary zb = zon.Boundaries.Single(b => b.Name == full);
            Assert.Equal(expectZon, zb.Links.Select(x => (x.Branch, x.Direction)).ToList());
            k++;
        }
        Assert.Equal(4, k);
    }

    [Fact]
    public void Names_positions_and_kv_change_no_number_of_the_solve()
    {
        // The import's bus records differ from the .m file's only in name, map position and base kV; with the .m file's
        // own branches put back, the solve is the .m file's, bit for bit (dense and sparse).
        GridModel op = Gb29();
        GridModel imported = PypsaGbReduced.Load(ReducedDir, op).Model;
        var hybrid = new GridModel { Name = "hybrid", BaseMva = op.BaseMva, FrequencyHz = op.FrequencyHz, Buses = imported.Buses, Generators = op.AllGenerators, Branches = op.Branches };
        S10TestSupport.AssertBitIdentical("names/positions [sparse]", new SparseNewtonRaphsonPowerFlow().Solve(op, PfOptions), new SparseNewtonRaphsonPowerFlow().Solve(hybrid, PfOptions));
        S10TestSupport.AssertBitIdentical("names/positions [dense]", new NewtonRaphsonPowerFlow().Solve(op, PfOptions), new NewtonRaphsonPowerFlow().Solve(hybrid, PfOptions));
    }

    [Fact]
    public void The_reduced_network_solve_and_boundary_flows_are_pinned()
    {
        GridModel m = PypsaGbReduced.Load(ReducedDir, Gb29()).Model;
        S10TestSupport.AssertSolvesIdentical("pypsa-gb-reduced pf", m, m, PfOptions);
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, PfOptions);
        Assert.True(s.Converged);
        Assert.Equal(21, s.Iterations);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: mismatch 3.240963053485757E-12 -> 1.4708234630234074E-12; loss MW
        // 2066.1979255895526 -> 2066.1979255895403; loss MVAr 19056.015442012864 -> 19056.015442012897; Vmin 0.8731466354519171 ->
        // 0.8731466354519183; boundary flows 2058.9468105179208 -> 2058.9468105179226, 5786.771814384412 -> 5786.771814384755,
        // 6584.428789242157 -> 6584.428789242498 (iterations, slack and the fourth boundary unchanged).
        Assert.Equal(1.4708234630234074E-12, s.MaxMismatch);
        Assert.Equal(2066.1979255895403, s.TotalLossMw);
        Assert.Equal(19056.015442012897, s.TotalLossMvar);
        Assert.Equal(-15779.082074410593, s.PInjMw[26]);           // slack (Sellindge) net injection
        int lo = Array.IndexOf(s.Vm, s.Vm.Min());
        Assert.Equal(25, s.BusIds[lo]);
        Assert.Equal(0.8731466354519183, s.Vm[lo]);
        IReadOnlyList<BoundaryFlow> f = BoundaryAnalysis.Evaluate(m, s);
        Assert.Equal(2058.9468105179226, f[0].FlowMw);
        Assert.Equal(5786.771814384755, f[1].FlowMw);
        Assert.Equal(6584.428789242498, f[2].FlowMw);
        Assert.Equal(12640.347217652652, f[3].FlowMw);
        Assert.All(f, x => Assert.False(x.Overloaded));             // limit 0 = no limit authored, never an overload
        // The contingency study solver starts from the same state (no OLTCs).
        S10TestSupport.AssertBitIdentical("pypsa-gb-reduced study vs pf", s, ContingencyAnalysis.CreateStudySolver(m).Solve(m, ContingencyAnalysis.StudyOptions));
    }

    [Fact]
    public void The_reduced_importer_refuses_what_does_not_line_up()
    {
        string dir = CopyReduced();
        try
        {
            // An operating point with a different bus count.
            GridModel op = Gb29();
            var short28 = new GridModel { BaseMva = 100, Buses = op.Buses.Take(28).ToList(), Generators = op.AllGenerators.Where(g => g.Bus <= 28).ToList(), Branches = op.Branches.Where(b => b.From <= 28 && b.To <= 28).ToList() };
            var ex = Assert.Throws<InvalidDataException>(() => PypsaGbReduced.Load(dir, short28));
            Assert.Contains("has 28 buses but buses.csv has 29", ex.Message, StringComparison.Ordinal);

            // A line naming a bus that is not in buses.csv.
            File.WriteAllText(Path.Combine(dir, "lines.csv"), "name,bus0,bus1,r,x,b,s_nom\n0,Beauly,Atlantis,0.01,0.1,0,100\n");
            ex = Assert.Throws<InvalidDataException>(() => PypsaGbReduced.Load(dir, op));
            Assert.Contains("lines.csv line 2: bus 'Atlantis' is not in buses.csv", ex.Message, StringComparison.Ordinal);

            // A header with a renamed column.
            File.WriteAllText(Path.Combine(dir, "lines.csv"), "name,bus0,bus1,r,x,b,rating\n0,Beauly,Peterhead,0.01,0.1,0,100\n");
            ex = Assert.Throws<InvalidDataException>(() => PypsaGbReduced.Load(dir, op));
            Assert.Contains("header is 'name,bus0,bus1,r,x,b,rating' but this importer reads exactly 'name,bus0,bus1,r,x,b,s_nom'", ex.Message, StringComparison.Ordinal);

            // A non-finite number.
            File.WriteAllText(Path.Combine(dir, "lines.csv"), "name,bus0,bus1,r,x,b,s_nom\n0,Beauly,Peterhead,NaN,0.1,0,100\n");
            ex = Assert.Throws<InvalidDataException>(() => PypsaGbReduced.Load(dir, op));
            Assert.Contains("column 'r' is 'NaN', which is not a finite number", ex.Message, StringComparison.Ordinal);

            // A quoted name holding a comma is read as one field (and then refused only because it is not a bus).
            File.WriteAllText(Path.Combine(dir, "lines.csv"), "name,bus0,bus1,r,x,b,s_nom\n0,\"Beauly, north\",Peterhead,0.01,0.1,0,100\n");
            ex = Assert.Throws<InvalidDataException>(() => PypsaGbReduced.Load(dir, op));
            Assert.Contains("bus 'Beauly, north' is not in buses.csv", ex.Message, StringComparison.Ordinal);

            // A boundary naming a branch the network does not have.
            File.Copy(Path.Combine(ReducedDir, "lines.csv"), Path.Combine(dir, "lines.csv"), overwrite: true);
            File.WriteAllText(Path.Combine(dir, "boundaries.json"), "[{\"name\":\"B6\",\"limitMw\":0,\"branches\":[{\"branch\":\"Nowhere #1\",\"direction\":1}]}]");
            ex = Assert.Throws<InvalidDataException>(() => PypsaGbReduced.Load(dir, op));
            Assert.Contains("boundary 'B6' names branch 'Nowhere #1', which this network does not have", ex.Message, StringComparison.Ordinal);

            // An unknown key in the boundary file.
            File.WriteAllText(Path.Combine(dir, "boundaries.json"), "[{\"name\":\"B6\",\"limit\":0,\"branches\":[]}]");
            Assert.Throws<InvalidDataException>(() => PypsaGbReduced.Load(dir, op));

            // A missing file.
            File.Delete(Path.Combine(dir, "links.csv"));
            File.Delete(Path.Combine(dir, "boundaries.json"));
            Assert.Throws<FileNotFoundException>(() => PypsaGbReduced.Load(dir, op));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string CopyReduced()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-pypsa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in new[] { "buses.csv", "lines.csv", "links.csv" }) File.Copy(Path.Combine(ReducedDir, f), Path.Combine(dir, f));
        return dir;
    }

    // ---- the zonal model -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_zonal_model_imports_with_its_capacities_and_is_not_ac_solvable()
    {
        PypsaGbZonalNetwork z = PypsaGbZonal.Load(ZonalDir);
        Assert.False(PypsaGbZonalNetwork.IsAcSolvable);
        Assert.Equal(20, z.Zones.Count);
        Assert.Equal(37, z.Links.Count);
        Assert.Equal(6, z.Links.Count(l => l.External));
        Assert.Equal(new[] { "BritNed", "EastWest", "Moyle", "Nemo", "IFA", "IFA2" }, z.Links.Where(l => l.External).Select(l => l.Name));
        Assert.All(z.Links, l => Assert.Equal(-1.0, l.PMinPu));
        Assert.Equal(8000.0, z.Links.Single(l => l.Name == "Z9-Z3").PNomMw);
        Assert.Equal(57.182, z.Zones.Single(x => x.Name == "Z2").LatDeg);
        // Boundary capacities (MW), the same both ways because every link is bidirectional (p_min_pu = -1).
        Assert.Equal(new[] { 22000.0, 22000.0, 20000.0, 16000.0 }, z.Boundaries.Select(b => b.NorthToSouthMw));
        Assert.Equal(new[] { 22000.0, 22000.0, 20000.0, 16000.0 }, z.Boundaries.Select(b => b.SouthToNorthMw));
        Assert.Equal(new[] { "Z7-Z2", "Z9-Z3", "Z7-Z6" }, z.Boundaries[1].Links.Select(x => x.Branch));   // B6
        Assert.All(z.Boundaries, b => Assert.All(b.Links, x => Assert.Equal(-1, x.Direction)));         // links run south to north
        Assert.Equal(new[]
        {
            "20 zone(s), 37 link(s): 31 between zones, 6 interconnector(s) to a foreign node.",
            "not AC solvable: it is a zonal transport model (zones joined by links that have a MW capacity and no impedance), so it has no AC or DC power flow.",
            "4 boundary cut(s) read from boundaries.json (derived from the ETYS boundary GIS lines): capacities only, no flows.",
        }, z.Notes);
    }

    [Fact]
    public void The_zonal_importer_refuses_bad_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-zonal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "buses.csv"), "name,y,x,v_nom,carrier\nZ1,57,-4,400,AC\nZ2,56,-3,400,AC\n");
            File.WriteAllText(Path.Combine(dir, "links.csv"), "name,bus0,bus1,carrier,p_nom,p_min_pu\nZ1-Z2,Z1,Z2,AC,1000,2\n");
            var ex = Assert.Throws<InvalidDataException>(() => PypsaGbZonal.Load(dir));
            Assert.Contains("p_min_pu 2; PyPSA's p_min_pu lies in [-1, 1]", ex.Message, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "links.csv"), "name,bus0,bus1,carrier,p_nom,p_min_pu\nX,Mars,Venus,DC,1000,-1\n");
            ex = Assert.Throws<InvalidDataException>(() => PypsaGbZonal.Load(dir));
            Assert.Contains("neither of which is a zone", ex.Message, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "links.csv"), "name,bus0,bus1,carrier,p_nom,p_min_pu\nZ1-Z2,Z1,Z2,AC,1000,0\n");
            PypsaGbZonalNetwork ok = PypsaGbZonal.Load(dir);
            Assert.Equal("Z1-Z2", ok.Links.Single().Name);
            Assert.Empty(ok.Boundaries);
            File.WriteAllText(Path.Combine(dir, "buses.csv"), "name,y,x,v_nom,carrier\nZ1,97,-4,400,AC\n");
            ex = Assert.Throws<InvalidDataException>(() => PypsaGbZonal.Load(dir));
            Assert.Contains("which is not a place on Earth", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- CLI ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_cli_resolves_the_two_pypsa_names_and_refuses_the_zonal_one_as_an_input_error()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        Assert.Contains("\"pypsa-gb-reduced\" => Gated.PypsaGbReduced(root),", src, StringComparison.Ordinal);
        Assert.Contains("\"pypsa-gb-zonal\" => throw Gated.NotAcSolvable(name),", src, StringComparison.Ordinal);

        CliProcess.Result pf = CliProcess.RunBuilt("pf", "pypsa-gb-zonal");
        Assert.True(pf.ExitCode == 1, pf.ToString());
        Assert.Contains("case 'pypsa-gb-zonal' cannot be solved: it is a zonal transport model", pf.StdErr, StringComparison.Ordinal);
        Assert.Equal("", pf.StdOut);
        // A verb that resolves its case through SelectCase / LoadSelected: the refusal reaches the process-wide handler
        // as an input error (exit 1), never a crash and never a solve.
        CliProcess.Result n1 = CliProcess.RunBuilt("nminus1", "pypsa-gb-zonal");
        Assert.True(n1.ExitCode == 1, n1.ToString());
        Assert.Contains("Show it with 'gridsim zonal pypsa-gb-zonal'", n1.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pf_verb_on_pypsa_gb_reduced_prints_the_pinned_solve_and_the_import_notes()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "pypsa-gb-reduced", "--top", "1");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Power flow - pypsa-gb-reduced (29 buses)", r.StdOut, StringComparison.Ordinal);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): the printed
        // mismatch follows the pinned solve above. Before -> after: 3.24E-012 -> 1.47E-012 (losses print unchanged).
        Assert.Contains("converged in 21 iteration(s), mismatch 1.47E-012 pu   losses 2066.198 MW / 19056.015 MVAr", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("note: pypsa-gb-reduced: 96 of 99 line(s) equal a branch of 'GBreducednetwork'", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("note:", r.StdOut, StringComparison.Ordinal);   // notes go to stderr; stdout is the report
    }

    [Fact]
    public void The_zonal_verb_shows_the_model_and_writes_its_result_file()
    {
        string json = Path.Combine(Path.GetTempPath(), "gridsim-zonal-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("zonal", "pypsa-gb-zonal", "--json", json);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("Zonal transport model - pypsa-gb-zonal (20 zones, 37 links: 31 between zones, 6 interconnectors)", r.StdOut, StringComparison.Ordinal);
            Assert.Matches(@"B6 Scotland-England \(SCOTEX\) +22,000 / +22,000 +\(Z7-Z2, Z9-Z3, Z7-Z6\)", r.StdOut);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal("zonal", doc.RootElement.GetProperty("verb").GetString());
            Assert.False(doc.RootElement.GetProperty("summary").GetProperty("ac_solvable").GetBoolean());
            Assert.Equal(37, doc.RootElement.GetProperty("tables").GetProperty("links").GetArrayLength());
            Assert.Equal(4, doc.RootElement.GetProperty("tables").GetProperty("boundaries").GetArrayLength());

            CliProcess.Result usage = CliProcess.RunBuilt("zonal");
            Assert.True(usage.ExitCode == 2, usage.ToString());
            CliProcess.Result missing = CliProcess.RunBuilt("zonal", Path.Combine(Path.GetTempPath(), "no-such-zonal-" + Guid.NewGuid().ToString("N")));
            Assert.True(missing.ExitCode == 1, missing.ToString());
        }
        finally { if (File.Exists(json)) File.Delete(json); }
    }
}
