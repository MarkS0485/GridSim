using System.Globalization;
using GridSim.Core.Api;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP11: the Core object model (<see cref="CaseEditor"/>). Oracles: the model as loaded (an edit and its undo give the
/// same records, so the same solve bits), a MATPOWER file edited by text (the same edit made at the source), and the RAW
/// parser's own machine and branch keys. Every number compared is compared bit for bit.
/// </summary>
public sealed class Rc5Wp11ObjectModelTests
{
    private static string Lib(string name) => Path.Combine(CliProcess.Root(), "data", "matpower", "lib", name);

    private static GridModel Case9(string? text = null) => MatpowerParser.Parse(text ?? File.ReadAllText(Lib("case9.m")), "case9");

    private static PowerFlowResult Solve(GridModel m) => new SteadyStateSolver().Solve(m, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });

    private static void SameBits(PowerFlowResult a, PowerFlowResult b)
    {
        Assert.True(a.Converged && b.Converged);
        Assert.Equal(a.BusIds, b.BusIds);
        for (int i = 0; i < a.Vm.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Vm[i]), BitConverter.DoubleToInt64Bits(b.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.VaDeg[i]), BitConverter.DoubleToInt64Bits(b.VaDeg[i]));
        }
    }

    [Fact]
    public void Keys_follow_the_rules_in_model_order()
    {
        var ed = new CaseEditor(Case9());
        Assert.Equal(Enumerable.Range(1, 9).Select(i => i.ToString(CultureInfo.InvariantCulture)), ed.Elements(ElementKind.Bus).Select(k => k.Key));
        Assert.Equal(["G1", "G2", "G3"], ed.Elements(ElementKind.Generator).Select(k => k.Key));
        Assert.Equal(["5", "7", "9"], ed.Elements(ElementKind.Load).Select(k => k.Key));
        Assert.Equal(["1-4", "4-5", "5-6", "3-6", "6-7", "7-8", "8-2", "8-9", "9-4"], ed.Elements(ElementKind.Branch).Select(k => k.Key));
        Assert.Empty(ed.Elements(ElementKind.Shunt));
        Assert.Equal(0, ed.Revision);
        Assert.Equal(["bus", "generator", "load", "branch", "shunt"], ElementKinds.All.Select(ElementKinds.Name));
    }

    [Fact]
    public void Raw_cases_are_keyed_by_their_native_identities()
    {
        string raw = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw");
        PsseParseResult p = PsseRawParser.ParseFile(raw, new PsseReadOptions());
        var ed = new CaseEditor(p.Model, p.MachineKeys.Select(k => new ElementKeys.MachineId(k.Bus, k.Id)).ToList(),
            p.BranchKeys.Select(k => new ElementKeys.BranchId(k.FromBus, k.ToBus, k.ThirdBus, k.Circuit, k.Winding)).ToList());
        IReadOnlyList<ElementKey> gens = ed.Elements(ElementKind.Generator);
        Assert.Equal(p.Model.AllGenerators.Count, gens.Count);
        for (int i = 0; i < gens.Count; i++)
            Assert.Equal($"{p.MachineKeys[i].Bus}:{p.MachineKeys[i].Id.Trim()}", gens[i].Key);
        IReadOnlyList<ElementKey> brs = ed.Elements(ElementKind.Branch);
        Assert.Equal(p.Model.Branches.Count, brs.Count);
        Assert.Equal($"{p.BranchKeys[0].FromBus}-{p.BranchKeys[0].ToBus}:{p.BranchKeys[0].Circuit.Trim()}", brs[0].Key);
        Assert.Equal(p.Model.AllGenerators[0].Pg, ed.Get(ElementKind.Generator, gens[0].Key, "pg"));
    }

    [Fact]
    public void An_edit_matches_the_same_edit_made_in_the_file_and_its_undo_gives_the_original_bits()
    {
        GridModel original = Case9();
        var ed = new CaseEditor(original);
        Assert.Equal(125.0, ed.Get(ElementKind.Load, "9", "pd"));
        Assert.Equal(1, ed.Set(ElementKind.Load, "9", [new("pd", 0.0)]));
        Assert.Equal(2, ed.Set(ElementKind.Load, "9", [new("qd", 0.0)]));
        Assert.Same(original, ed.Original);
        Assert.Equal(125.0, original.Buses[8].Pd);   // nothing mutated in place

        string text = File.ReadAllText(Lib("case9.m")).Replace("\t9\t1\t125\t50\t", "\t9\t1\t0\t0\t", StringComparison.Ordinal);
        GridModel fileEdited = Case9(text);
        Assert.Equal(0.0, fileEdited.Buses[8].Pd);
        SameBits(Solve(fileEdited), Solve(ed.Model));

        ed.Set(ElementKind.Load, "9", [new("pd", 125.0), new("qd", 50.0)]);
        Assert.Equal(3, ed.Revision);
        SameBits(Solve(original), Solve(ed.Model));
        Assert.Equal(original.Buses[8], ed.Model.Buses[8]);

        Assert.Equal(4, ed.Edits.Count);
        Assert.Equal(new CaseEdit(1, ElementKind.Load, "9", "pd", 125.0, 0.0), ed.Edits[0]);
        Assert.Equal(new CaseEdit(3, ElementKind.Load, "9", "qd", 0.0, 50.0), ed.Edits[3]);
    }

    [Fact]
    public void Refused_writes_change_nothing()
    {
        var ed = new CaseEditor(Case9());
        GridModel before = ed.Model;
        Assert.False(ElementKinds.TryParse("transformer", out _));
        Assert.Contains("its attributes are: pd, qd, status", Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Load, "9", [new("p", 1.0)])).Message, StringComparison.Ordinal);
        Assert.Contains("status is 1 (in service) or 0", Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Generator, "G2", [new("status", 2.0)])).Message, StringComparison.Ordinal);
        Assert.Contains("read only", Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Bus, "5", [new("base_kv", 1.0)])).Message, StringComparison.Ordinal);
        Assert.Contains("no load '4'", Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Load, "4", [new("pd", 1.0)])).Message, StringComparison.Ordinal);
        // One bad value in a write refuses the whole write, the good one included.
        Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Generator, "G2", [new("pg", 100.0), new("status", 7.0)]));
        Assert.Throws<CaseEditException>(() => ed.Set(ElementKind.Generator, "G2", [new("pg", double.NaN)]));
        Assert.Same(before, ed.Model);
        Assert.Equal(0, ed.Revision);
        Assert.Empty(ed.Edits);
        Assert.Equal(163.0, ed.Get(ElementKind.Generator, "G2", "pg"));
    }

    [Fact]
    public void A_load_taken_out_keeps_its_set_point_for_re_energising()
    {
        var ed = new CaseEditor(Case9());
        ed.Set(ElementKind.Load, "7", [new("status", 0.0)]);
        Assert.Equal(0.0, ed.Model.Buses[6].Pd);
        Assert.Equal(0.0, ed.Get(ElementKind.Load, "7", "status"));
        Assert.Equal(100.0, ed.Get(ElementKind.Load, "7", "pd"));
        ed.Set(ElementKind.Load, "7", [new("pd", 80.0)]);
        Assert.Equal(0.0, ed.Model.Buses[6].Pd);
        ed.Set(ElementKind.Load, "7", [new("status", 1.0)]);
        Assert.Equal(80.0, ed.Model.Buses[6].Pd);
        Assert.Equal(35.0, ed.Model.Buses[6].Qd);
    }

    [Fact]
    public void Every_attribute_reads_the_model_value_and_writes_through_with()
    {
        var ed = new CaseEditor(Case9());
        ed.Set(ElementKind.Branch, "8-9", [new("status", 0.0), new("rate_a", 123.0)]);
        Assert.False(ed.Model.Branches[7].InService);
        Assert.Equal(123.0, ed.Model.Branches[7].RateAMva);
        Assert.Equal(ed.Model.Branches[7] with { InService = true, RateAMva = 250.0 }, ed.Original.Branches[7]);
        ed.Set(ElementKind.Generator, "G3", [new("status", 0.0)]);
        Assert.Equal(2, ed.Model.Generators.Count);
        Assert.Equal(3, ed.Model.AllGenerators.Count);
        ed.Set(ElementKind.Bus, "5", [new("vmin", 0.95), new("type", 1.0)]);
        Assert.Equal(0.95, ed.Model.Buses[4].VminPu);
        Assert.Equal(345.0, ed.Get(ElementKind.Bus, "5", "base_kv"));
        Assert.Equal(1.0, ed.Get(ElementKind.Bus, "5", "type"));
        Assert.Equal(3.0, ed.Get(ElementKind.Bus, "1", "type"));
        Assert.Equal(ElementAttributes.Of(ElementKind.Bus).Select(a => a.Name), ed.GetAll(ElementKind.Bus, "1").Select(kv => kv.Key));
    }

    [Fact]
    public void The_load_flow_view_reads_the_result_itself()
    {
        GridModel m = Case9();
        PowerFlowResult r = Solve(m);
        var v = new LoadFlowView(m, r);
        Assert.True(v.Converged);
        Assert.Equal(r.Vm[8], v.Bus(9)!.Value.Vm);
        Assert.Null(v.Bus(99));
        Assert.Same(r.LineFlows[3], v.Branch(3));
    }
}
