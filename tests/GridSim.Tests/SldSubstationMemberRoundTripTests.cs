using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using GridSim.Core.Model.Substations;
using GridSim.Core.Presentation.Workbench.Sld;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The node-breaker diagram's edits keep every member of the file. A switch edit rebuilt the model by naming each member
/// and left out the three-winding transformers, so saving after a switch edit deleted them from the file. These tests fill
/// every member of the model (found by reflection, so a member added later is covered without editing this file), run every
/// edit command, save, read the file back and compare every member the command did not touch, value by value (doubles
/// bit for bit).
/// </summary>
public sealed class SldSubstationMemberRoundTripTests
{
    /// <summary>Every member of <see cref="SubstationModel"/> and of the records it holds, set to a value other than its
    /// default somewhere (checked by <see cref="Every_member_of_the_fixture_holds_a_value_other_than_its_default"/>).</summary>
    private static SubstationModel Full() => new()
    {
        Name = "every-member",
        BaseMva = 50,
        FrequencyHz = 60,
        Substations = new[]
        {
            new Substation("S1", "North",
                new[] { new VoltageLevel("HV", 132), new VoltageLevel("MV", 33), new VoltageLevel("LV", 11) },
                new[] { new SubstationBay("B1", "HV", "Feeder bay") },
                new[]
                {
                    new ConnectivityNode("H1", "HV", "B1"), new ConnectivityNode("H2", "HV"), new ConnectivityNode("M1", "MV"),
                    new ConnectivityNode("M2", "MV"), new ConnectivityNode("L1", "LV"),
                },
                new[]
                {
                    new NbSwitch("CB1", SwitchKind.Breaker, "H1", "H2", false, "B1"),
                    new NbSwitch("DS1", SwitchKind.Disconnector, "M1", "M2", false),
                    new NbSwitch("LB1", SwitchKind.LoadBreak, "M1", "M2", true),
                    new NbSwitch("ES1", SwitchKind.EarthSwitch, "L1", null, true),
                }),
            new Substation("S2", "South",
                new[] { new VoltageLevel("HV", 132) },
                Array.Empty<SubstationBay>(),
                new[] { new ConnectivityNode("R1", "HV"), new ConnectivityNode("R2", "HV") },
                new[] { new NbSwitch("CB2", SwitchKind.Breaker, "R1", "R2", false) }),
        },
        Lines = new[] { new NbLine("LN1", "H2", "R1", 0.01, 0.1, 0.02, 150) },
        Transformers = new[] { new NbTransformer("T1", "H2", "M1", 0.002, 0.05, 0.001, 1.025, -2.5, 90) },
        Loads = new[] { new NbLoad("LD1", "L1", 12.5, 3.25) },
        Generators = new[] { new NbGenerator("G1", "R2", 40, 1.02, 5, -30, 30, 10, 80, true) },
        Shunts = new[] { new NbShunt("SH1", "M2", 0.5, 10) },
        Transformers3W = new[]
        {
            new NbTransformer3W("T3", "H1", "M2", "L1", 0.001, 0.08, 0.002, 0.12, 0.0015, 0.07, 0.0005,
                new[] { 1.01, 0.99, 1.02 }, new[] { 0.0, -30.0, -150.0 }, new[] { 100.0, 60.0, 40.0 }),
        },
        Notes = new[] { "switch DS1 was assumed closed" },
    };

    // ------------------------------------------------------------------------------------------------------------ the fixture

    [Fact]
    public void Reflection_sees_the_three_winding_transformers_and_the_notes()
    {
        string[] names = Members(typeof(SubstationModel)).Select(p => p.Name).ToArray();
        Assert.Contains(nameof(SubstationModel.Transformers3W), names);
        Assert.Contains(nameof(SubstationModel.Notes), names);
        Assert.Contains(nameof(SubstationModel.Substations), names);
    }

    [Fact]
    public void Every_member_of_the_fixture_holds_a_value_other_than_its_default()
    {
        SubstationModel full = Full();
        var blank = new SubstationModel();
        var problems = new List<string>();
        foreach (PropertyInfo p in Members(typeof(SubstationModel)))
        {
            var d = new List<string>();
            Compare(p.GetValue(blank), p.GetValue(full), p.Name, d);
            if (d.Count == 0) problems.Add($"SubstationModel.{p.Name} is at its default in Full(); fill it so the round trip covers it.");
        }
        // Every member of every record the model holds is set (not null, not empty, not zero, not false) somewhere.
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        Walk(full, seen);
        foreach (KeyValuePair<string, bool> kv in seen.Where(kv => !kv.Value))
            problems.Add($"{kv.Key} never holds a value other than its default in Full(); fill it so the round trip covers it.");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_fixture_survives_the_format_round_trip()
    {
        SubstationModel full = Full();
        var diffs = new List<string>();
        Compare(full, SubstationJson.Read(SubstationJson.Write(full)), "model", diffs);
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
    }

    // ------------------------------------------------------------------------------------------------------------ switch edits

    [Fact]
    public void Toggling_each_switch_and_saving_keeps_every_other_member()
    {
        foreach (string id in Full().AllSwitches.Select(s => s.Id))
        {
            using TrackedTempDir dir = TestTemp.NewDir("gridsim-sld-members-");
            string path = WriteFull(dir);
            SldSubstationDocument d = SldSubstationDocument.Open(path);
            bool open = !Full().AllSwitches.Single(s => s.Id == id).NormalOpen;

            SldCommandResult r = d.Execute(new SldSetSwitch(id, open));
            Assert.True(r.Ok && r.Changed, $"{id}: {r.Message}");
            SldSaveResult saved = d.Save();
            Assert.True(saved.Saved, $"{id}: {saved.Message}");
            AssertFile(path, Flipped(Full().Substations, id), $"after toggling {id}");
        }
    }

    [Fact]
    public void Every_step_of_an_edit_undo_redo_sequence_saves_every_member()
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-sld-members-");
        string path = WriteFull(dir);
        SldSubstationDocument d = SldSubstationDocument.Open(path);
        IReadOnlyList<Substation> s0 = Full().Substations;
        IReadOnlyList<Substation> s1 = Flipped(s0, "CB1");
        IReadOnlyList<Substation> s2 = Flipped(s1, "LB1");

        Assert.True(d.Execute(new SldSetSwitch("CB1", true)).Changed);
        Assert.True(d.Save().Saved);
        AssertFile(path, s1, "step 1 (open CB1)");

        Assert.True(d.Execute(new SldSetSwitch("LB1", false)).Changed);
        Assert.True(d.Save().Saved);
        AssertFile(path, s2, "step 2 (close LB1)");

        Assert.True(d.Undo().Ok);
        Assert.True(d.Save().Saved);
        AssertFile(path, s1, "step 3 (undo: LB1 open again)");

        Assert.True(d.Redo().Ok);
        Assert.True(d.Save().Saved);
        AssertFile(path, s2, "step 4 (redo: LB1 closed again)");

        Assert.True(d.Undo().Ok);
        Assert.True(d.Undo().Ok);
        Assert.True(d.Save().Saved);
        AssertFile(path, s0, "step 5 (undo both: the original states)");
    }

    [Fact]
    public void A_switch_already_in_the_asked_state_changes_nothing_and_saves_nothing()
    {
        using TrackedTempDir dir = TestTemp.NewDir("gridsim-sld-members-");
        string path = WriteFull(dir);
        byte[] before = File.ReadAllBytes(path);
        SldSubstationDocument d = SldSubstationDocument.Open(path);

        SldCommandResult r = d.Execute(new SldSetSwitch("CB1", false));
        Assert.True(r.Ok);
        Assert.False(r.Changed);
        Assert.False(d.IsDirty);
        Assert.False(d.Save().Saved);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ------------------------------------------------------------------------------------------------------------ every other command

    /// <summary>One instance of every command the diagram has, other than the switch state.</summary>
    private static SldCommand[] OtherCommands() =>
    [
        new SldMoveBus(1, 0.5, 0.5),
        new SldAdoptLayout(),
        new SldAddBus(null, 132, 0.5, 0.5),
        new SldAddBranch(SldBranchKind.Line, 1, 2, 0.01, 0.1),
        new SldAddGenerator(1, 100, 50),
        new SldAddLoad(1, 10),
        new SldDelete("node/H1"),
    ];

    [Fact]
    public void The_command_list_covers_every_command_type()
    {
        string[] all = typeof(SldCommand).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(SldCommand)) && !t.IsAbstract)
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] covered = OtherCommands().Select(c => c.GetType().Name).Append(nameof(SldSetSwitch))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(all, covered);
    }

    [Fact]
    public void Every_other_command_is_refused_and_leaves_the_file_and_every_member_untouched()
    {
        foreach (SldCommand c in OtherCommands())
        {
            using TrackedTempDir dir = TestTemp.NewDir("gridsim-sld-members-");
            string path = WriteFull(dir);
            byte[] before = File.ReadAllBytes(path);
            SldSubstationDocument d = SldSubstationDocument.Open(path);
            long revision = d.Revision;

            SldCommandResult r = d.Execute(c);
            Assert.False(r.Ok, $"{c.GetType().Name} was not refused: {r.Message}");
            Assert.False(r.Changed, c.GetType().Name);
            Assert.False(d.IsDirty, c.GetType().Name);
            Assert.Equal(revision, d.Revision);
            Assert.False(d.Save().Saved, c.GetType().Name);
            Assert.Equal(before, File.ReadAllBytes(path));
            AssertFile(path, Full().Substations, c.GetType().Name);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static string WriteFull(TrackedTempDir dir)
    {
        string path = dir.File("every-member.json");
        File.WriteAllText(path, SubstationJson.Write(Full()), new UTF8Encoding(false));
        return path;
    }

    private static IReadOnlyList<Substation> Flipped(IReadOnlyList<Substation> subs, string id) =>
        subs.Select(s => s with { Switches = s.Switches.Select(x => x.Id == id ? x with { NormalOpen = !x.NormalOpen } : x).ToArray() }).ToArray();

    /// <summary>The file at <paramref name="path"/> holds <paramref name="substations"/> and every other member of <see cref="Full"/>, unchanged.</summary>
    private static void AssertFile(string path, IReadOnlyList<Substation> substations, string step)
    {
        SubstationModel saved = SubstationJson.Read(File.ReadAllText(path, Encoding.UTF8));
        SubstationModel full = Full();
        var diffs = new List<string>();
        Compare(substations, saved.Substations, nameof(SubstationModel.Substations), diffs);
        foreach (PropertyInfo p in Members(typeof(SubstationModel)).Where(p => p.Name != nameof(SubstationModel.Substations)))
            Compare(p.GetValue(full), p.GetValue(saved), p.Name, diffs);
        Assert.True(diffs.Count == 0, step + ":\n" + string.Join("\n", diffs));
    }

    /// <summary>The members of a type: its public instance properties that hold data, that is every settable one (a
    /// record's positional members, the model's init members) and every get-only one with a compiler-generated backing
    /// field (a member set only in a constructor). Computed properties are left out; each must be named in
    /// <see cref="ComputedMembers"/> (checked by <see cref="Every_public_member_is_compared_or_a_named_computed_one"/>).</summary>
    private static PropertyInfo[] Members(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && (p.CanWrite || HasBackingField(p)))
            .OrderBy(p => p.MetadataToken)
            .ToArray();

    /// <summary>True for an auto-property (get-only included): the compiler stores it in <c>&lt;Name&gt;k__BackingField</c>.</summary>
    private static bool HasBackingField(PropertyInfo p) =>
        p.DeclaringType!.GetField($"<{p.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) is not null;

    /// <summary>The public properties computed from other members, which hold no data of their own.</summary>
    private static readonly string[] ComputedMembers =
        [nameof(SubstationModel) + "." + nameof(SubstationModel.AllNodes), nameof(SubstationModel) + "." + nameof(SubstationModel.AllSwitches)];

    /// <summary>The model type and every record type reachable from its members (element types of the lists included).</summary>
    private static List<Type> ModelTypes()
    {
        var types = new List<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(typeof(SubstationModel));
        while (queue.Count > 0)
        {
            Type t = queue.Dequeue();
            if (IsLeaf(t) || types.Contains(t)) continue;
            types.Add(t);
            foreach (PropertyInfo p in Members(t))
            {
                Type pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (pt.IsArray) pt = pt.GetElementType()!;
                else if (pt.IsGenericType && typeof(IEnumerable).IsAssignableFrom(pt)) pt = pt.GetGenericArguments()[0];
                if (!IsLeaf(pt) && pt.Namespace == typeof(SubstationModel).Namespace) queue.Enqueue(pt);
            }
        }
        return types;
    }

    [Fact]
    public void Every_public_member_is_compared_or_a_named_computed_one()
    {
        List<Type> types = ModelTypes();
        // The walk reaches the records the fixture fills.
        foreach (Type t in new[] { typeof(Substation), typeof(NbSwitch), typeof(NbTransformer3W), typeof(ConnectivityNode), typeof(SubstationBay) })
            Assert.Contains(t, types);
        var unseen = new List<string>();
        foreach (Type t in types)
        {
            PropertyInfo[] members = Members(t);
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                string name = t.Name + "." + p.Name;
                bool compared = members.Any(m => m.Name == p.Name);
                bool computed = ComputedMembers.Contains(name);
                if (compared == computed) unseen.Add(compared ? $"{name} is compared but listed as computed" : $"{name} is neither compared nor a named computed member");
            }
        }
        Assert.True(unseen.Count == 0, string.Join("\n", unseen));
        foreach (string c in ComputedMembers)
            Assert.Contains(c, types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => t.Name + "." + p.Name)));
    }

    [Fact]
    public void A_get_only_member_set_in_a_constructor_is_compared()
    {
        // The member rule on a type of its own: a constructor-set get-only member and an init member are both compared,
        // a computed one is not.
        string[] names = Members(typeof(ConstructorMemberProbe)).Select(p => p.Name).ToArray();
        Assert.Equal(new[] { nameof(ConstructorMemberProbe.Fixed), nameof(ConstructorMemberProbe.Init) }, names);
        var diffs = new List<string>();
        Compare(new ConstructorMemberProbe(1.5) { Init = "a" }, new ConstructorMemberProbe(2.5) { Init = "a" }, "probe", diffs);
        Assert.Equal(new[] { "probe.Fixed: expected 1.5 but the file holds 2.5" }, diffs);
    }

    private sealed class ConstructorMemberProbe(double fixedValue)
    {
        public double Fixed { get; } = fixedValue;
        public string Init { get; init; } = "";
        public double Twice => 2 * Fixed;
    }

    private static bool IsLeaf(Type t) => t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal);

    /// <summary>Structural comparison: leaves by value (doubles bit for bit), sequences entry by entry, records member by member.</summary>
    private static void Compare(object? expected, object? actual, string path, List<string> diffs)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null) diffs.Add($"{path}: expected {Show(expected)} but the file holds {Show(actual)}");
            return;
        }
        if (expected is double de && actual is double da)
        {
            if (BitConverter.DoubleToInt64Bits(de) != BitConverter.DoubleToInt64Bits(da)) diffs.Add($"{path}: expected {Show(de)} but the file holds {Show(da)}");
            return;
        }
        if (IsLeaf(expected.GetType()))
        {
            if (!expected.Equals(actual)) diffs.Add($"{path}: expected {Show(expected)} but the file holds {Show(actual)}");
            return;
        }
        if (expected is IEnumerable ee && actual is IEnumerable ae)
        {
            object?[] x = ee.Cast<object?>().ToArray();
            object?[] y = ae.Cast<object?>().ToArray();
            if (x.Length != y.Length) { diffs.Add($"{path}: expected {x.Length} entries but the file holds {y.Length}"); return; }
            for (int i = 0; i < x.Length; i++) Compare(x[i], y[i], $"{path}[{i}]", diffs);
            return;
        }
        if (expected.GetType() != actual.GetType()) { diffs.Add($"{path}: expected a {expected.GetType().Name} but the file holds a {actual.GetType().Name}"); return; }
        PropertyInfo[] members = Members(expected.GetType());
        if (members.Length == 0) { diffs.Add($"{path}: {expected.GetType().Name} has no members this comparison can see"); return; }
        foreach (PropertyInfo p in members)
            Compare(p.GetValue(expected), p.GetValue(actual), path + "." + p.Name, diffs);
    }

    /// <summary>Record, for every member of every record reachable from <paramref name="o"/>, whether any instance holds a
    /// value other than its default (null, empty, zero, false, the first enum value).</summary>
    private static void Walk(object? o, Dictionary<string, bool> seen)
    {
        if (o is null || IsLeaf(o.GetType())) return;
        if (o is IEnumerable items)
        {
            foreach (object? item in items) Walk(item, seen);
            return;
        }
        foreach (PropertyInfo p in Members(o.GetType()))
        {
            string key = o.GetType().Name + "." + p.Name;
            object? v = p.GetValue(o);
            bool set = v switch
            {
                null => false,
                string s => s.Length > 0,
                IEnumerable e => e.Cast<object?>().Any(),
                object value => !value.Equals(Activator.CreateInstance(value.GetType())),
            };
            seen[key] = (seen.TryGetValue(key, out bool before) && before) || set;
            Walk(v, seen);
        }
    }

    private static string Show(object? o) => o switch
    {
        null => "null",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        string s => "\"" + s + "\"",
        _ => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "?",
    };
}
