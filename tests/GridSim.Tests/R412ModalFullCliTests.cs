using System.Text.Json;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.Dynamics.SmallSignal.Full;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-12: <c>gridsim modal-full</c> as the built CLI. The CLI is a reader and printer over the Core assembler and analysis,
/// so the oracle for the numbers is the Core called directly on the same bindings (the CLI must add nothing and lose nothing);
/// the refusals, exit codes and help are checked as the process.
/// </summary>
public sealed class R412ModalFullCliTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-r412-" + Guid.NewGuid().ToString("N"));

    public R412ModalFullCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Machines(bool coded)
    {
        const string template = """
        {"format":"gridsim-modal-machines/1","machines":[
          {"bus":1,"model":"flux-decay","xdp":0.0608,"xd":0.5,"xq":0.4,"tdop":8.0,"damping":2.0,"exciter":{"model":"SEXS","parameters":{"K":20,"Ta":1,"Tb":1}}@CODED@},
          {"bus":2,"model":"flux-decay","xdp":0.1198,"xd":0.9,"xq":0.8,"tdop":6.0,"damping":2.0,"exciter":{"model":"SEXS","parameters":{"K":20,"Ta":1,"Tb":1}}},
          {"bus":3,"model":"classical","xdp":0.1813,"damping":2.0,"governor":{"model":"TGOV1"}}
        ]}
        """;
        return template.Replace("@CODED@", coded ? ",\"coded\":[\"Esst4b\"]" : "", StringComparison.Ordinal);
    }

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void The_cli_numbers_equal_the_core_called_on_the_same_bindings()
    {
        string models = Path.Combine(_dir, "ok"); Directory.CreateDirectory(models);
        Write("ok/machines.json", Machines(coded: false));
        string o = Path.Combine(_dir, "ok.json");
        CliProcess.Result r = CliProcess.RunBuilt("modal-full", "case9", "--models", models, "--json", o);
        Assert.True(r.ExitCode is 0 or 3, r.ToString());
        Assert.Contains("FULL-MODEL SMALL-SIGNAL", r.StdOut, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(o));
        JsonElement root = doc.RootElement;
        Assert.Equal("modal-full", root.GetProperty("verb").GetString());

        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        FullMachineBindings b = FullMachineBindings.Parse(File.ReadAllBytes(Path.Combine(models, "machines.json")), model, id =>
            GridSim.Core.Dynamics.UserModels.UserModelLibrary.Ids.Contains(id) ? GridSim.Core.Dynamics.UserModels.UserModelLibrary.Bytes(id) : null);
        FullModalResult core = FullModalAnalysis.Analyze(FullModelAssembler.Assemble(model, pf, b.Specs), new FullModalOptions());

        Assert.Equal(core.EigenvalueDigest, root.GetProperty("summary").GetProperty("eigenvalue_digest").GetString());
        JsonElement modes = root.GetProperty("tables").GetProperty("modes");
        Assert.Equal(core.Modes.Count, modes.GetArrayLength());
        for (int i = 0; i < core.Modes.Count; i++)
        {
            Assert.Equal(core.Modes[i].Mode.Eigenvalue.Real, modes[i].GetProperty("sigma").GetDouble());
            Assert.Equal(core.Modes[i].Mode.Eigenvalue.Imaginary, modes[i].GetProperty("omega_rad_s").GetDouble());
        }
        Assert.Equal("COMPLETE", root.GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(core.Order, root.GetProperty("summary").GetProperty("states").GetInt32());
    }

    [Fact]
    public void A_coded_controller_is_listed_as_not_linearised_and_the_run_is_INCOMPLETE_with_exit_3()
    {
        string models = Path.Combine(_dir, "coded"); Directory.CreateDirectory(models);
        Write("coded/machines.json", Machines(coded: true));
        CliProcess.Result r = CliProcess.RunBuilt("modal-full", "case9", "--models", models);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("NOT LINEARISED", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("Esst4b", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_and_input_errors_have_the_documented_exit_codes()
    {
        string models = Path.Combine(_dir, "m"); Directory.CreateDirectory(models);
        Write("m/machines.json", Machines(coded: false));
        Assert.Equal(2, CliProcess.RunBuilt("modal-full", "case9").ExitCode);                                           // no --models
        Assert.Equal(2, CliProcess.RunBuilt("modal-full", "case9", "--models", models, "--region", "1,2,3").ExitCode);  // bad region
        Assert.Equal(2, CliProcess.RunBuilt("modal-full", "case9", "--models", models, "--method", "magic").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("modal-full", "case9", "--models", models, "--top", "3").ExitCode);        // flag of another verb
        Assert.Equal(1, CliProcess.RunBuilt("modal-full", "case9", "--models", Path.Combine(_dir, "nope")).ExitCode);   // no directory
        Directory.CreateDirectory(Path.Combine(_dir, "bad"));
        Write("bad/machines.json", "{\"format\":\"gridsim-modal-machines/1\",\"machines\":[{\"bus\":99}]}");
        CliProcess.Result r = CliProcess.RunBuilt("modal-full", "case9", "--models", Path.Combine(_dir, "bad"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("bus 99", r.StdErr, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(_dir, "unk"));
        Write("unk/machines.json", "{\"format\":\"gridsim-modal-machines/1\",\"machines\":[{\"bus\":1,\"exciter\":{\"model\":\"NOPE\"}}]}");
        Assert.Equal(1, CliProcess.RunBuilt("modal-full", "case9", "--models", Path.Combine(_dir, "unk")).ExitCode);
    }

    [Fact]
    public void Help_names_the_flags_and_the_verb_is_listed()
    {
        CliProcess.Result h = CliProcess.RunBuilt("modal-full", "--help");
        Assert.Equal(0, h.ExitCode);
        Assert.Contains("--models", h.StdOut, StringComparison.Ordinal);
        Assert.Contains("--residual-gate", h.StdOut, StringComparison.Ordinal);
        Assert.Contains("COMPARATOR-UNVERIFIED", h.StdOut, StringComparison.Ordinal);
        Assert.Contains("modal-full", CliProcess.RunBuilt("--help").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void The_binding_reader_is_strict_and_names_the_json_path()
    {
        GridModel model = IeeeCases.Case9();
        byte[]? None(string id) => null;
        var ex = Assert.Throws<FullBindingException>(() => FullMachineBindings.Parse("{\"format\":\"gridsim-modal-machines/1\",\"machines\":[{\"bus\":1,\"colour\":3}]}"u8, model, None));
        Assert.Contains("$.machines[0]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("colour", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FullBindingException>(() => FullMachineBindings.Parse("{\"format\":\"other/1\",\"machines\":[]}"u8, model, None));
        Assert.Throws<FullBindingException>(() => FullMachineBindings.Parse("{\"format\":\"gridsim-modal-machines/1\",\"machines\":[{\"bus\":1},{\"bus\":1}]}"u8, model, None));
    }
}
