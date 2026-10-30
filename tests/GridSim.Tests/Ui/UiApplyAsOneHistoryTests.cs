using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// CaseAuthoring.ApplyAsOne keeps the undo and redo history exactly as it was before the group (2026-10-24): a refused
/// group leaves no redo levels behind and loses none the user had, a successful group joins into one undo step that
/// trims the undo limit once, and the save point is kept. (Before, a refused group left its undone steps as redo levels,
/// replacing the user's own redo history, and a join trimmed the undo limit once per operation.)
/// </summary>
public sealed class UiApplyAsOneHistoryTests
{
    private static CaseAuthoring Open(string dir) => CaseAuthoring.Open(AssetCaseSource.V1(dir), CaseSessionTests.Unlimited(), Array.Empty<string>());

    private static byte[] Buses(CaseAuthoring a) => a.Session.Doc("buses.json").Text.ToArray();

    // An Add of bus <id> (two of the same id in one group: the second is refused, the id is taken).
    private static Func<CaseSession, OpResult> AddBus(CaseAuthoring a, string id)
    {
        AuthoringRecordSet buses = a.Collections().First(c => c.Role == CaseRoles.Buses);
        return s => CaseOps.AddRecord(s, buses.Document, buses.ArrayPointer, int.MaxValue,
            new[] { new KeyValuePair<string, string>("id", id), new KeyValuePair<string, string>("baseKv", "132") });
    }

    [Fact]
    public void A_refused_group_keeps_the_redo_history_the_user_had_and_Redo_still_replays_it()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = Open(temp.Dir);
        Assert.True(MapEditing.MoveBus(a, 3, 0.5, 0.75).Ok);
        byte[] moved = Buses(a);
        string moveText = a.UndoDescription!;
        Assert.True(a.Undo().Ok);
        byte[] original = Buses(a);
        Assert.Equal((false, true, moveText), (a.CanUndo, a.CanRedo, a.RedoDescription));

        AuthoringResult r = a.ApplyAsOne("two adds", new[] { AddBus(a, "9001"), AddBus(a, "9001") });
        Assert.False(r.Ok);
        Assert.EndsWith("so the case and its undo and redo history are as before.", r.Message, StringComparison.Ordinal);

        // The bytes and the whole history as before the group: the user's redo of the move is still there.
        Assert.Equal(original, Buses(a));
        Assert.Equal((false, true, moveText), (a.CanUndo, a.CanRedo, a.RedoDescription));
        Assert.True(a.Redo().Ok);
        Assert.Equal(moved, Buses(a));
    }

    [Fact]
    public void A_refused_group_keeps_the_undo_history_and_the_save_point()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = Open(temp.Dir);
        Assert.True(MapEditing.MoveBus(a, 3, 0.5, 0.75).Ok);
        Assert.True(a.Save().Saved);
        Assert.False(a.IsDirty);
        (int depth, string? text) = (a.UndoDepth, a.UndoDescription);

        Assert.False(a.ApplyAsOne("two adds", new[] { AddBus(a, "9001"), AddBus(a, "9001") }).Ok);

        Assert.Equal((depth, text, false, false), (a.UndoDepth, a.UndoDescription, a.CanRedo, a.IsDirty));
        Assert.True(a.Undo().Ok);   // the kept undo level still reverses the move
        Assert.True(a.IsDirty);     // and leaves the saved point, as any undo past it does
    }

    [Fact]
    public void A_successful_group_at_the_undo_limit_trims_the_oldest_level_once()
    {
        using var temp = new EditorTestSupport.TempCopy(EditorTestSupport.CaseDir("uk"));
        CaseAuthoring a = Open(temp.Dir);
        // Fill the history to its limit with distinct, describable steps.
        var texts = new List<string>();
        for (int i = 0; i < CaseAuthoring.UndoLimit; i++)
        {
            Assert.True(MapEditing.MoveBus(a, 3, 0.1 + i * 1e-4, 0.5).Ok);
            texts.Add(a.UndoDescription!);
        }
        Assert.Equal(CaseAuthoring.UndoLimit, a.UndoDepth);

        // Two operations joined into one step: the limit drops exactly the oldest level (texts[0]), no more.
        AuthoringResult both = a.ApplyAsOne("two good adds", new[] { AddBus(a, "9001"), AddBus(a, "9002") });
        Assert.True(both.Ok, both.Message);
        Assert.Equal(CaseAuthoring.UndoLimit, a.UndoDepth);
        Assert.Equal("two good adds", a.UndoDescription);
        for (int i = 0; i < CaseAuthoring.UndoLimit - 1; i++) Assert.True(a.Undo().Ok);
        Assert.Equal(1, a.UndoDepth);
        Assert.Equal(texts[1], a.UndoDescription);
    }
}
