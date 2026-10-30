using GridSim.Cli;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F stream: the machine dynamic data file (<c>--dyn-data</c>, gridsim-dynamics/2, F23) beyond <c>stability --rms</c>:
/// <c>trip</c> and <c>transient</c> take each bound machine's inertia H (the only per-machine number of the system swing),
/// <c>modal</c> takes H, X'd and the damping D (the three numbers of the classical small-signal model). Pinned: the model
/// edits exactly; that a file whose H values equal the case's leaves trip and transient bit for bit as without it (the
/// only change is the line saying the file was read); that a different H reaches the swing; modal's header; and the
/// refusals (a bad file exits 1, a transient batch exits 2).
/// </summary>
public sealed partial class FFrontDoorsTests
{
    private static string DynFixture => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    private static string DynSchemas => Path.Combine(CliProcess.Root(), "schemas", "v2");

    /// <summary>The fixture with one exact text replacement, written into <paramref name="dir"/>.</summary>
    private static string DynVariant(string dir, string find, string replace)
    {
        string text = File.ReadAllText(DynFixture);
        Assert.Contains(find, text, StringComparison.Ordinal);
        string path = Path.Combine(dir, "variant.dynamics.json");
        File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
        return path;
    }

    // ---- The model edits ----------------------------------------------------------------------------------------------

    [Fact]
    public void DynData_inertia_copy_replaces_only_the_bound_generators_H()
    {
        GridModel model = IeeeCases.Case9();
        string dir = TempDir();
        try
        {
            // Bus 2's template says H = 8.5 instead of the case's 6.4.
            MachineDynamicsSet set = MachineDynamicsLoader.Load(DynVariant(dir, "\"inertiaH\": 6.4,", "\"inertiaH\": 8.5,"), DynSchemas, model);
            GridModel copy = DynDataCli.WithFileInertia(model, set);
            Assert.Equal(new[] { 23.64, 8.5, 3.01 }, copy.Generators.Select(g => g.H).ToArray());
            for (int i = 0; i < model.Generators.Count; i++)
                Assert.Equal(model.Generators[i] with { H = copy.Generators[i].H }, copy.Generators[i]);   // nothing else moves
            Assert.Equal(1, DynDataCli.InertiaChangedCount(model, set));
            Assert.Equal(model.FrequencyHz, copy.FrequencyHz);
            Assert.Same(model.Buses, copy.Buses);
            Assert.Same(model.Branches, copy.Branches);
        }
        finally { Clean(dir); }

        // The fixture's own H values are the case's: the copy carries the very same numbers.
        MachineDynamicsSet same = MachineDynamicsLoader.Load(DynFixture, DynSchemas, model);
        Assert.Equal(0, DynDataCli.InertiaChangedCount(model, same));
        Assert.Equal(model.Generators.ToArray(), DynDataCli.WithFileInertia(model, same).Generators.ToArray());
    }

    [Fact]
    public void DynData_modal_inputs_take_the_files_xdp_and_damping_and_fill_the_rest()
    {
        GridModel model = IeeeCases.Case9();
        MachineDynamicsSet set = MachineDynamicsLoader.Load(DynFixture, DynSchemas, model);
        // Every case9 machine is bound: the fixture's X'd (0.30, 0.30, 0.18) and damping (0, 0.5, 0) whatever the base.
        Assert.Equal(new[] { 0.30, 0.30, 0.18 }, DynDataCli.TransientReactance(model, new[] { 9.0, 9.0, 9.0 }, set));
        Assert.Equal(new[] { 0.30, 0.30, 0.18 }, DynDataCli.TransientReactance(model, null, set));
        Assert.Equal(new[] { 0.0, 0.5, 0.0 }, DynDataCli.Damping(model, new[] { 2.0, 2.0, 2.0 }, set));
        Assert.Equal(new[] { 0.0, 0.5, 0.0 }, DynDataCli.Damping(model, null, set));

        // Only bus 2 bound: the others keep the base map (or the classical default / zero damping when there is none).
        string dir = TempDir();
        try
        {
            string text = File.ReadAllText(DynFixture);
            int m = text.IndexOf("\"machines\"", StringComparison.Ordinal);
            string onlyBus2 = text[..m] + "\"machines\": [ { \"bus\": 2, \"template\": \"gridsim.test.wscc9.steam2@1\" } ]\n}\n";
            string path = Path.Combine(dir, "bus2.dynamics.json");
            File.WriteAllText(path, onlyBus2);
            MachineDynamicsSet one = MachineDynamicsLoader.Load(path, DynSchemas, model);
            Assert.Equal(new[] { 9.0, 0.30, 7.0 }, DynDataCli.TransientReactance(model, new[] { 9.0, 8.0, 7.0 }, one));
            double dflt = GridSim.Core.Dynamics.Distributed.ClassicalInit.DefaultTransientReactancePu;
            Assert.Equal(new[] { dflt, 0.30, dflt }, DynDataCli.TransientReactance(model, null, one));
            Assert.Equal(new[] { 1.5, 0.5, 1.5 }, DynDataCli.Damping(model, new[] { 1.5, 1.5, 1.5 }, one));
            Assert.Equal(new[] { 0.0, 0.5, 0.0 }, DynDataCli.Damping(model, null, one));
        }
        finally { Clean(dir); }
    }

    // ---- trip -----------------------------------------------------------------------------------------------------------

    /// <summary>A file whose H values are the case's: the trip is the same trip, bit for bit, plus one line.</summary>
    [Fact]
    public void DynData_trip_with_the_cases_own_inertia_is_the_same_trip_plus_one_line()
    {
        CliProcess.Result plain = CliProcess.RunBuilt("trip", "case9");
        CliProcess.Result dyn = CliProcess.RunBuilt("trip", "case9", "--dyn-data", DynFixture);
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.True(dyn.ExitCode == 0, dyn.ToString());
        string[] dynLines = dyn.StdOut.Split('\n');
        string machineLine = Assert.Single(dynLines, l => l.StartsWith("Machine data:", StringComparison.Ordinal));
        Assert.Contains("'WSCC 9-bus GENROU test machines' (wscc9-genrou.dynamics.json): inertia H of 3 generator(s) from the file (0 differ from the case)", machineLine, StringComparison.Ordinal);
        Assert.Contains("not used by this frequency swing", machineLine, StringComparison.Ordinal);
        Assert.Equal(plain.StdOut, string.Join('\n', dynLines.Where(l => !l.StartsWith("Machine data:", StringComparison.Ordinal))));
    }

    [Fact]
    public void DynData_trip_takes_a_different_inertia_from_the_file()
    {
        string dir = TempDir();
        try
        {
            string path = DynVariant(dir, "\"inertiaH\": 6.4,", "\"inertiaH\": 8.5,");
            CliProcess.Result r = CliProcess.RunBuilt("trip", "case9", "--dyn-data", path);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("(1 differ from the case)", r.StdOut, StringComparison.Ordinal);
            // The system inertia line is the file's: 23.64 x 247.5 + 8.5 x 192 + 3.01 x 128 MW.s.
            GridModel model = IeeeCases.Case9();
            GridModel copy = DynDataCli.WithFileInertia(model, MachineDynamicsLoader.Load(path, DynSchemas, model));
            double total = copy.Generators.Sum(g => g.H * g.MBase);
            Assert.Contains($"System inertia: {total / 1000:F1} ->", r.StdOut, StringComparison.Ordinal);
            CliProcess.Result plain = CliProcess.RunBuilt("trip", "case9");
            Assert.DoesNotContain($"System inertia: {total / 1000:F1} ->", plain.StdOut, StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void DynData_a_bad_file_exits_1_on_trip_modal_and_transient()
    {
        string dir = TempDir();
        try
        {
            string bad = DynVariant(dir, "\"tdopS\": 6.0,", "");   // bus 2's GENROU loses a required time constant
            foreach (string[] args in new[]
            {
                new[] { "trip", "case9", "--dyn-data", bad },
                new[] { "modal", "case9", "--dyn-data", bad },
                new[] { "transient", "case9", "--minutes", "0.001", "--out", Path.Combine(dir, "t"), "--dyn-data", bad },
                new[] { "trip", "case9", "--dyn-data", Path.Combine(dir, "missing.dynamics.json") },
            })
            {
                CliProcess.Result r = CliProcess.RunBuilt(args);
                Assert.True(r.ExitCode == 1, string.Join(' ', args) + "\n" + r);
                Assert.Contains(": --dyn-data:", r.StdErr, StringComparison.Ordinal);
            }
            CliProcess.Result noValue = CliProcess.RunBuilt("trip", "case9", "--dyn-data");
            Assert.True(noValue.ExitCode == 2, noValue.ToString());
        }
        finally { Clean(dir); }
    }

    // ---- transient ------------------------------------------------------------------------------------------------------

    [Fact]
    public void DynData_transient_with_the_cases_own_inertia_gives_the_same_trajectory()
    {
        string dir = TempDir();
        try
        {
            string plainOut = Path.Combine(dir, "plain"), dynOut = Path.Combine(dir, "dyn");
            string[] common = { "transient", "case9", "--minutes", "0.01", "--trip", "50", "--trip-at", "0.2", "--trip-window", "0.01" };
            CliProcess.Result plain = CliProcess.RunBuilt(common.Concat(new[] { "--out", plainOut }).ToArray());
            CliProcess.Result dyn = CliProcess.RunBuilt(common.Concat(new[] { "--out", dynOut, "--dyn-data", DynFixture }).ToArray());
            Assert.True(plain.ExitCode == 0, plain.ToString());
            Assert.True(dyn.ExitCode == 0, dyn.ToString());
            Assert.Contains("Machine data:   'WSCC 9-bus GENROU test machines'", dyn.StdErr, StringComparison.Ordinal);
            Assert.Contains("dyn-data=wscc9-genrou.dynamics.json (inertia H only)", File.ReadAllText(Path.Combine(dynOut, "run-metadata.json")), StringComparison.Ordinal);

            // Every data row equal, column for column, except the wall-clock solve time (column 10).
            static string[][] Rows(string folder) => File.ReadAllLines(Path.Combine(folder, "timeseries.csv"))
                .Where(l => l.Length > 0 && char.IsDigit(l[0])).Select(l => l.Split(',')).ToArray();
            string[][] a = Rows(plainOut), b = Rows(dynOut);
            Assert.Equal(a.Length, b.Length);
            Assert.True(a.Length > 100);
            for (int i = 0; i < a.Length; i++)
                Assert.Equal(a[i].Where((_, c) => c != 10), b[i].Where((_, c) => c != 10));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void DynData_transient_refuses_a_batch()
    {
        CliProcess.Result r = CliProcess.RunBuilt("transient", "--cases", "case9,case14", "--minutes", "0.001", "--dyn-data", DynFixture);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("cannot be used with a batch of cases", r.StdErr, StringComparison.Ordinal);
    }

    // ---- modal ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void DynData_modal_says_where_its_machine_numbers_came_from()
    {
        CliProcess.Result plain = CliProcess.RunBuilt("modal", "case9");
        CliProcess.Result dyn = CliProcess.RunBuilt("modal", "case9", "--dyn-data", DynFixture);
        Assert.True(plain.ExitCode is 0 or 1, plain.ToString());
        Assert.True(dyn.ExitCode is 0 or 1, dyn.ToString());
        Assert.Contains("X'd = Anderson-Fouad", plain.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("machine data:", plain.StdOut, StringComparison.Ordinal);

        Assert.Contains("X'd = 3 from wscc9-genrou.dynamics.json  *  ", dyn.StdOut, StringComparison.Ordinal);
        Assert.Contains("damping D = 0 (undamped) (the file's own on its 3 machine(s))", dyn.StdOut, StringComparison.Ordinal);
        Assert.Contains("machine data: 'WSCC 9-bus GENROU test machines'  *  H, X'd and damping of 3 machine(s) from the file", dyn.StdOut, StringComparison.Ordinal);
        Assert.Contains("note: generator", dyn.StdOut, StringComparison.Ordinal);   // bus 1's H taken from the case, said
        Assert.Contains("the modes are INDICATIVE", dyn.StdOut, StringComparison.Ordinal);   // the fixture is Synthetic
        // The file's X'd and damping are not case9's Anderson-Fouad set, so the modes move.
        Assert.NotEqual(EigenRows(plain.StdOut), EigenRows(dyn.StdOut));

        static string[] EigenRows(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd())
            .Where(l => l.EndsWith("oscillatory", StringComparison.Ordinal) || l.EndsWith("   real", StringComparison.Ordinal)).ToArray();
    }
}
