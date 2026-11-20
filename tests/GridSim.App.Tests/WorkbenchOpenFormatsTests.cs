using GridSim.App.Workbench;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>A4: the workbench opens every case format the main app and the CLI read, through their readers and the same licence gate.</summary>
public sealed class WorkbenchOpenFormatsTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static string Fixture(params string[] parts) => Path.Combine([RepoRoot(), "tests", "GridSim.Tests", "fixtures", .. parts]);

    private static readonly LicenceGate Unlimited = new(Entitlement.FreeDefault with { BusLimit = 0 });

    [Fact]
    public void A_v2_case_opens_and_matches_the_main_app_loader()
    {
        string path = Path.Combine(RepoRoot(), "docs", "_captures", "networks", "inputs", "v2-two-bus", "two-bus.case.json");
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(path, Unlimited), vm.StatusText);
        var expected = GridSim.Ui.Hosting.SimHost.ReadCase(GridSim.Ui.Models.ModelCatalog.V2CaseSource(path)).Model;
        Assert.Equal(expected.Buses.Count, vm.Project!.Model.Buses.Count);
        Assert.Equal(expected.Branches.Count, vm.Project.Model.Branches.Count);
        Assert.Equal(2, vm.Project.Model.Buses.Count);
        Assert.False(vm.Diagram.CanEdit);   // read-only: a v2 case has no v1 case files to write back to
    }

    [Fact]
    public void A_psse_raw_file_opens()
    {
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(Fixture("dynamics-study", "case14-parallel.raw"), Unlimited), vm.StatusText);
        Assert.Equal(14, vm.Project!.Model.Buses.Count);
    }

    [Fact]
    public void A_ucte_file_opens()
    {
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(Fixture("ucte", "small.uct"), Unlimited), vm.StatusText);
        Assert.True(vm.Project!.Model.Buses.Count > 0);
    }

    [Fact]
    public void A_cgmes_folder_and_a_cgmes_zip_open_to_the_same_network()
    {
        string dir = Fixture("cgmes", "p3-coupler");
        var vm = new WorkbenchViewModel();
        Assert.True(vm.TryLoad(dir, Unlimited), vm.StatusText);
        int buses = vm.Project!.Model.Buses.Count;
        Assert.True(buses > 0);

        string zip = Path.Combine(Path.GetTempPath(), "wb-cgmes-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(dir, zip);
            var vm2 = new WorkbenchViewModel();
            Assert.True(vm2.TryLoad(zip, Unlimited), vm2.StatusText);
            Assert.Equal(buses, vm2.Project!.Model.Buses.Count);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void An_unreadable_or_unknown_file_says_why_and_the_gate_still_applies_to_the_new_formats()
    {
        var vm = new WorkbenchViewModel();
        string txt = Path.Combine(Path.GetTempPath(), "wb-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(txt, "x");
        try
        {
            Assert.False(vm.TryLoad(txt));
            Assert.Contains("Cannot open", vm.StatusText, StringComparison.Ordinal);
        }
        finally { File.Delete(txt); }

        // A bus cap below the file's size refuses a PSS/E file with the gate's message, as it does a MATPOWER one.
        var tiny = new LicenceGate(Entitlement.FreeDefault with { BusLimit = 3 });
        var vm2 = new WorkbenchViewModel();
        Assert.False(vm2.TryLoad(Fixture("dynamics-study", "case14-parallel.raw"), tiny));
        Assert.Null(vm2.Project);
        Assert.Contains(tiny.Check(14, false).Message, vm2.StatusText, StringComparison.Ordinal);
    }
}
