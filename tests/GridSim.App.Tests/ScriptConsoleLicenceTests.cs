using GridSim.App.Scripting;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// 2.1.074: the desktop Script Console checks a loaded model against the licence's bus limit exactly as the command line does: a user
/// model over the limit is refused with the gate's own message, at the LOAD CASE line, for a typed line and for a script file.
/// </summary>
public sealed class ScriptConsoleLicenceTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A copy of the GB synthetic case outside the install: a user model, not a Bundled Network.</summary>
    private static string UserCopy(TempFolder temp)
    {
        string dir = temp.File("user-gb");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(Path.Combine(RepoRoot(), "data", "gb-synthetic"), "*.json"))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    private static string Q(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    [Fact]
    public async Task A_user_model_over_the_free_bus_limit_is_refused_with_the_gates_message()
    {
        using var temp = new TempFolder();
        string copy = UserCopy(temp);
        var gate = new LicenceGate(Entitlement.FreeDefault);
        var model = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(copy);
        GateResult expected = gate.Check(model.BusCount, bundled: false);
        Assert.False(expected.Allowed);

        using var vm = new ScriptConsoleViewModel(temp.Path, temp.File("sessions")) { Licence = gate };
        await vm.SubmitAsync($"LOAD CASE \"{Q(copy)}\"");
        Assert.NotNull(vm.LastError);
        Assert.Equal(expected.Message, vm.LastError!.Message);
        Assert.DoesNotContain("loaded \"", vm.OutputText, StringComparison.Ordinal);

        string script = temp.File("big.gss");
        File.WriteAllText(script, $"GRAMMAR 1\nLOAD CASE \"{Q(copy)}\"\n");
        await vm.RunFileAsync(script);
        Assert.NotNull(vm.LastError);
        Assert.Equal(2, vm.LastError!.Pos.Line);
        Assert.Equal(expected.Message, vm.LastError.Message);
        vm.EndSession();
    }
}
