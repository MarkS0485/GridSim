using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R092 (October 2026 review): the registry's law names were a hand copy that had drifted from the audit. The audit
/// now labels every headline law from <see cref="PhysicsCheckRegistry.Names"/>.
/// </summary>
public class PhysicsRegistryNamesTests
{
    [Fact]
    public void Every_audited_law_is_labelled_from_the_registry()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        PhysicsLawReport report = PhysicsLaws.Audit(model, pf);
        var headline = report.Laws.Where(l => l.Number is >= 1 and <= 10).ToList();
        Assert.Equal(10, headline.Select(l => l.Number).Distinct().Count());
        foreach (LawResult law in headline)
            Assert.Equal(PhysicsCheckRegistry.Name(law.Number), law.Name);
    }

    [Fact]
    public void Registry_names_carry_the_audit_wording()
    {
        Assert.Equal("Ampere's law (passivity)", PhysicsCheckRegistry.Name(5));
        Assert.Equal("Complex power S = V I*", PhysicsCheckRegistry.Name(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhysicsCheckRegistry.Name(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhysicsCheckRegistry.Name(0));
    }

    [Fact]
    public void Physics_laws_source_carries_no_hand_written_headline_label()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Physics", "PhysicsLaws.cs"));
        foreach (string name in PhysicsCheckRegistry.Names)
            Assert.DoesNotContain($"\"{name}\"", src);
    }
}
