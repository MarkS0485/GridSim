using System.Text.Json.Nodes;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Licensing;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using Xunit;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// CaseOps.SetBusCoordinates (2026-10-24): the bus move of map editing, moved from GridSim.Ui into the engine. These pin
/// the engine operation on its own (one transaction, lossless, the refusals and their words), and pin that the map's
/// MoveBus, which now hands its rounded numbers to it, writes exactly the bytes the engine writes for the same numbers.
/// </summary>
public sealed class CaseOpsBusCoordinatesTests
{
    private static LicenceGate Gate() => CaseSessionTests.Unlimited();

    // The record reference of the bus at <index> of a v1 buses.json (a root array: the array pointer is "").
    private static RecordRef Bus(CaseSession s, int index) => new(s.Doc("buses.json").Id, "", index);

    [Fact]
    public void Two_buses_move_in_one_undo_step_writing_only_their_x_and_y()
    {
        using var temp = new TempCopy(CaseDir("uk"));
        CaseSession s = CaseSession.OpenV1(temp.Dir, Gate());
        byte[] before = s.Doc("buses.json").Text.ToArray();
        int depth = s.UndoDepth;

        OpResult r = CaseOps.SetBusCoordinates(s, new[]
        {
            new CaseOps.CoordinateEdit(Bus(s, 0), "0.125", "0.875"),
            new CaseOps.CoordinateEdit(Bus(s, 1), "0.25", "0.75"),
        }, "move two buses", Bus(s, 1));

        Assert.True(r.Ok, r.Message);
        Assert.True(r.Changed);
        Assert.Equal("Move two buses (4 member(s) written).", r.Message);
        Assert.Equal(Bus(s, 1), r.Select);
        Assert.Equal(depth + 1, s.UndoDepth);
        Assert.Equal("move two buses", s.UndoDescription);

        // Only x and y of the two buses differ; every other member of every bus is as it was.
        JsonArray now = JsonNode.Parse(s.Doc("buses.json").Text.ToArray())!.AsArray();
        JsonArray old = JsonNode.Parse(before)!.AsArray();
        Assert.Equal(old.Count, now.Count);
        Assert.Equal(("0.125", "0.875"), (now[0]!["x"]!.ToJsonString(), now[0]!["y"]!.ToJsonString()));
        Assert.Equal(("0.25", "0.75"), (now[1]!["x"]!.ToJsonString(), now[1]!["y"]!.ToJsonString()));
        for (int i = 0; i < old.Count; i++)
        {
            JsonObject a = (JsonObject)old[i]!.DeepClone(), b = (JsonObject)now[i]!.DeepClone();
            foreach (JsonObject o in new[] { a, b }) { o.Remove("x"); o.Remove("y"); }
            Assert.True(JsonNode.DeepEquals(a, b), "bus " + i + " changed beyond x and y");
        }

        // One undo takes both back, byte for byte.
        s.Undo();
        Assert.Equal(depth, s.UndoDepth);
        Assert.True(s.Doc("buses.json").Text.ToArray().AsSpan().SequenceEqual(before));
    }

    [Fact]
    public void Nothing_to_do_and_every_refusal_change_nothing()
    {
        using var temp = new TempCopy(CaseDir("uk"));
        CaseSession s = CaseSession.OpenV1(temp.Dir, Gate());
        byte[] buses = s.Doc("buses.json").Text.ToArray();
        int depth = s.UndoDepth;
        void Unchanged()
        {
            Assert.Equal(depth, s.UndoDepth);
            Assert.True(s.Doc("buses.json").Text.ToArray().AsSpan().SequenceEqual(buses));
        }

        OpResult none = CaseOps.SetBusCoordinates(s, Array.Empty<CaseOps.CoordinateEdit>(), "move nothing", null);
        Assert.Equal((true, false, "No bus to move."), (none.Ok, none.Changed, none.Message));
        Unchanged();

        // Already there: the values as written now (read back from the file) are no change at all.
        JsonNode first = JsonNode.Parse(buses)!.AsArray()[0]!;
        OpResult same = CaseOps.SetBusCoordinates(s, new[] { new CaseOps.CoordinateEdit(Bus(s, 0), first["x"]!.ToJsonString(), first["y"]!.ToJsonString()) }, "move in place", null);
        Assert.Equal((true, false, "x and y unchanged (the bus is already there)."), (same.Ok, same.Changed, same.Message));
        Unchanged();

        // A value the engine does not parse as a number.
        OpResult bad = CaseOps.SetBusCoordinates(s, new[] { new CaseOps.CoordinateEdit(Bus(s, 0), "north", "0.5") }, "move badly", null);
        Assert.False(bad.Ok);
        Assert.False(bad.Changed);
        Unchanged();

        // A record that is not a bus: refused by name, file, array and position.
        CaseDocument gen = s.Doc("generation.json");
        OpResult notBus = CaseOps.SetBusCoordinates(s, new[] { new CaseOps.CoordinateEdit(new RecordRef(gen.Id, "", 0), "0.5", "0.5") }, "move a generator", null);
        Assert.Equal((false, "generation.json #0 is not a bus record; only bus records have layout coordinates."), (notBus.Ok, notBus.Message));
        Unchanged();

        // A position past the end of the array.
        int count = JsonNode.Parse(buses)!.AsArray().Count;
        OpResult missing = CaseOps.SetBusCoordinates(s, new[] { new CaseOps.CoordinateEdit(Bus(s, count), "0.5", "0.5") }, "move a ghost", null);
        Assert.Equal((false, "buses.json: there is no record at /" + count + "."), (missing.Ok, missing.Message));
        Unchanged();

        // The same bus twice is a caller error, not a refusal.
        Assert.Throws<ArgumentException>(() => CaseOps.SetBusCoordinates(s, new[]
        {
            new CaseOps.CoordinateEdit(Bus(s, 0), "0.1", "0.1"),
            new CaseOps.CoordinateEdit(Bus(s, 0), "0.2", "0.2"),
        }, "move twice", null));
        Unchanged();
    }

    [Fact]
    public void The_maps_MoveBus_writes_exactly_the_bytes_the_engine_writes_for_the_same_numbers()
    {
        using var viaMap = new TempCopy(CaseDir("uk"));
        using var viaEngine = new TempCopy(CaseDir("uk"));
        CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V1(viaMap.Dir), Gate(), Array.Empty<string>());
        CaseRef bus3 = MapEditing.Build(a).Bus(3)!.Record;
        Assert.True(MapEditing.MoveBus(a, 3, 0.5, 0.75).Ok);

        CaseSession s = CaseSession.OpenV1(viaEngine.Dir, Gate());
        // World (0.5, 0.75) is case x 0.5, y 1 - 0.75 = 0.25 (MapEditing's convention), written in its number form.
        OpResult r = CaseOps.SetBusCoordinates(s, new[] { new CaseOps.CoordinateEdit(Bus(s, bus3.Index), MapEditing.Format(0.5), MapEditing.Format(0.25)) },
            "move bus 3", null);
        Assert.True(r.Ok, r.Message);
        Assert.True(a.Session.Doc("buses.json").Text.ToArray().AsSpan().SequenceEqual(s.Doc("buses.json").Text.ToArray()));
    }
}
