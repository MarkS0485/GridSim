using GridSim.Core.Api;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP15 review fix: the islands without a slack that the case editor accepts are those of the model as it stands,
/// recomputed after every accepted edit, never accumulated. Oracle: the validator's own island message
/// ("An island of ...") for the model after each edit.
/// </summary>
public sealed class Rc5Wp15ReviewFixTests
{
    private static KeyValuePair<string, double>[] V(params (string Name, double Value)[] v) =>
        v.Select(x => new KeyValuePair<string, double>(x.Name, x.Value)).ToArray();

    private static GridModel OneBus() => new()
    {
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack, BaseKv: 132)],
        Generators = [new Generator(1, 0)],
        Branches = [],
        Name = "one-bus",
    };

    [Fact]
    public void A_set_that_reopens_an_island_an_earlier_edit_closed_is_refused()
    {
        var ed = new CaseEditor(OneBus());
        Assert.Equal(1, ed.Add(ElementKind.Bus, "99", V(("base_kv", 132))));   // an island of bus 99: a structural edit accepts it
        Assert.Equal(2, ed.Add(ElementKind.Branch, "1-99:1", V(("x", 0.1)))); // connected: no island is left
        GridModel before = ed.Model;
        CaseEditException e = Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Branch, "1-99:1", V(("status", 0))));
        Assert.Contains("An island of 1 bus(es)", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, ed.Revision);
        Assert.Same(before, ed.Model);
    }

    [Fact]
    public void An_island_left_by_a_structural_edit_stays_accepted_until_an_edit_closes_it()
    {
        var ed = new CaseEditor(OneBus());
        ed.Add(ElementKind.Bus, "2", V(("base_kv", 132)));
        ed.Add(ElementKind.Load, "2", V(("pd", 10)));
        // Still an island of bus 2: a Set on it is checked against the case as it stands and accepted.
        Assert.Equal(3, ed.Set(ElementKind.Load, "2", V(("pd", 20))));
        ed.Add(ElementKind.Branch, "1-2:1", V(("x", 0.1)));
        // Closed by a Set as well as by an add: reconnecting, then opening again, is refused the second time.
        ed.Delete(ElementKind.Branch, "1-2:1");
        ed.Add(ElementKind.Branch, "1-2:2", V(("x", 0.1), ("status", 0)));   // added open: bus 2 still an island
        Assert.Equal(7, ed.Set(ElementKind.Branch, "1-2:2", V(("status", 1))));
        Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Branch, "1-2:2", V(("status", 0))));
        Assert.Equal(7, ed.Revision);
    }
}
