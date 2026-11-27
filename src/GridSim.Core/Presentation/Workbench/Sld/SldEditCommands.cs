using System.Globalization;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>Whether a new branch is a line (transmission.json) or a transformer (transformer.json).</summary>
public enum SldBranchKind { Line, Transformer }

/// <summary>One edit of the diagram (R4-18). A command is a value: the same command on the same document gives the same result on both
/// clients, and it is applied, validated, undone and redone only through <see cref="ISldDocument"/>.</summary>
public abstract record SldCommand;

/// <summary>Move a busbar: write the bus's layout coordinates x and y (case units, 0..1 on the map; the diagram scale is <see cref="SldOptions.LayoutScale"/>).</summary>
public sealed record SldMoveBus(int BusId, double X, double Y) : SldCommand;

/// <summary>Write the automatic layout into the case: every bus's x and y, in one step. After it the diagram can be placed from the case and edited by moving.</summary>
public sealed record SldAdoptLayout : SldCommand;

/// <summary>Add a bus (id one above the highest when <paramref name="BusId"/> is null) and, when <paramref name="ConnectTo"/> is given, a branch to that bus in the same step (a bus with no branch is refused by the case loader).</summary>
public sealed record SldAddBus(int? BusId, double BaseKv, double X, double Y, string? Name = null, int? ConnectTo = null,
    SldBranchKind Kind = SldBranchKind.Line, double RPu = 0.01, double XPu = 0.1, double RatingMva = 0) : SldCommand;

/// <summary>Connect two existing buses with a new line or transformer. <paramref name="BPu"/> (line charging, lines only) and <paramref name="TapRatio"/> (transformers only) are written only when they differ from 0 and 1, so a command without them writes the record it always did.</summary>
public sealed record SldAddBranch(SldBranchKind Kind, int From, int To, double RPu, double XPu, double RatingMva = 0, string? Name = null,
    double BPu = 0, double TapRatio = 1) : SldCommand;

/// <summary>Add a generator at a bus.</summary>
public sealed record SldAddGenerator(int BusId, double CapacityMva, double OutputMw, string? Name = null, double? VoltageSetpointPu = null) : SldCommand;

/// <summary>Add a demand at a bus.</summary>
public sealed record SldAddLoad(int BusId, double PMw, double QMvar = 0, string? Name = null) : SldCommand;

/// <summary>Delete a drawn element by its diagram id (<c>branch/3</c>, <c>gen/0</c>, <c>load/12</c>, <c>shunt/12</c>, <c>bus/12</c>). A bus that anything still refers to is refused.</summary>
public sealed record SldDelete(string ElementId) : SldCommand;

/// <summary>Change a switch's state in a node-breaker file (R4-11): <paramref name="Open"/> true opens it.</summary>
public sealed record SldSetSwitch(string SwitchId, bool Open) : SldCommand;

/// <summary>The outcome of a command: whether it was applied (<see cref="Changed"/>), why not when refused, and the problems the check found.</summary>
public sealed record SldCommandResult(bool Ok, bool Changed, string Message, IReadOnlyList<string> Problems)
{
    public static SldCommandResult Done(string m) => new(true, true, m, []);
    public static SldCommandResult NoChange(string m) => new(true, false, m, []);
    public static SldCommandResult Refused(string m, IReadOnlyList<string>? problems = null) => new(false, false, m, problems ?? []);
}

/// <summary>The outcome of a save: <see cref="Saved"/> only when files were written; <see cref="Lines"/> is the whole report.</summary>
public sealed record SldSaveResult(bool Saved, string Message, IReadOnlyList<string> Lines);

/// <summary>
/// An editable diagram source (R4-18): a model, commands that change it, undo and redo as inversions of those commands, and a save.
/// Every change goes through the case engine (<c>CaseSession</c>, the schema check, the case loader) or the node-breaker file's own
/// reader and writer; nothing here writes text of its own, and nothing writes outside the case it was opened on.
/// </summary>
public interface ISldDocument
{
    string Name { get; }
    /// <summary>True when the diagram can be edited (a JSON case folder, or a node-breaker file); false for a source with nothing to write back.</summary>
    bool CanEdit { get; }
    SldModel Model { get; }
    /// <summary>Increases with every applied change, undo, redo and save, so a view knows when to redraw.</summary>
    long Revision { get; }
    bool IsDirty { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    string? UndoDescription { get; }
    string? RedoDescription { get; }
    SldCommandResult Execute(SldCommand command);
    SldCommandResult Undo();
    SldCommandResult Redo();
    SldSaveResult Save();
}

/// <summary>
/// The diagram of a JSON case folder (v1) opened for editing. Every command becomes one or more <c>CaseOps</c> edits on the case's own
/// files (lossless text patches: the move of a symbol changes only the x and y members of its bus record); the result is checked by the
/// save gate's own stages (schema of every changed file, then the real case loader) before it is accepted, and refused with every problem
/// listed if the case would no longer be valid; a refused command leaves the documents and the undo history exactly as they were.
/// Undo and redo are the inverses of the applied steps. <see cref="Save"/> is the save gate: staged, checked, minimal text change.
/// </summary>
public sealed class SldCaseDocument : ISldDocument, IDisposable
{
    private string? _workingCopy;
    private string? _displayName;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly CaseSession _s;
    private readonly List<(string Description, int Steps)> _undo = [];
    private readonly List<(string Description, int Steps)> _redo = [];
    private GridModel _grid;
    private SldModel _model;
    private long _revision;

    private SldCaseDocument(CaseSession s, GridModel grid)
    {
        _s = s;
        _grid = grid;
        _model = SldModel.From(grid);
    }

    public string Name => _displayName ?? Path.GetFileName(_s.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string Folder => _s.RootPath;
    public bool CanEdit => true;
    public GridModel Grid => _grid;
    public SldModel Model => _model;
    public long Revision => _revision;
    public bool IsDirty => _s.IsDirty;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>The files that differ from what was opened (name, current bytes), for a client that cannot write the case in place (the web client offers them as downloads).</summary>
    public IReadOnlyList<(string File, byte[] Bytes)> ChangedFiles() => _s.DirtyDocuments.Select(d => (d.FileName, d.Text.ToArray())).ToList();

    /// <summary>Open a v1 case folder. Throws <see cref="InvalidDataException"/> (or the case session's own exceptions) when the folder is not a valid case.</summary>
    public static SldCaseDocument Open(string folder, LicenceGate gate)
    {
        CaseSession s;
        try { s = CaseSession.OpenV1(folder, gate); }
        catch (CaseLicenceDeniedException ex) { throw new InvalidDataException("The licence gate refused the case: " + ex.Message, ex); }
        if (s.Preflight.LoaderError is { } err) throw new InvalidDataException("The case does not load: " + err);
        return new SldCaseDocument(s, StageLoad(s) ?? throw new InvalidDataException("The case does not load."));
    }

    /// <summary>
    /// Open a private working copy of a case folder (its top-level files, in a fresh temporary folder) for editing. For a host that must
    /// not write a shared case in place, such as the web client: every edit and <see cref="Save"/> go to the copy, the original is never
    /// opened for writing, and <see cref="ChangedFiles"/> gives the edited files to hand to the user. <see cref="Dispose"/> deletes the copy.
    /// </summary>
    public static SldCaseDocument OpenWorkingCopy(string folder, LicenceGate gate)
    {
        string source = Path.GetFullPath(folder);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Case folder not found: " + folder);
        // SEC-04: a temporary folder only this user can read (0700 outside Windows); the case's documents go into it.
        string copy = Directory.CreateTempSubdirectory("gridsim-sld-copy-").FullName;
        try
        {
            foreach (string f in Directory.EnumerateFiles(source, "*.json", SearchOption.TopDirectoryOnly))
                File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
            SldCaseDocument d = Open(copy, gate);
            d._workingCopy = copy;
            d._displayName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return d;
        }
        catch
        {
            try { Directory.Delete(copy, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* harmless */ }
            throw;
        }
    }

    /// <summary>Delete the working copy, if this document has one.</summary>
    public void Dispose()
    {
        if (_workingCopy is null) return;
        try { if (Directory.Exists(_workingCopy)) Directory.Delete(_workingCopy, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temporary folder is harmless */ }
        _workingCopy = null;
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------------------------------------------------------------

    public SldCommandResult Execute(SldCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            return command switch
            {
                SldMoveBus m => Apply($"move bus {m.BusId}", false, () => MoveOps(m)),
                SldAdoptLayout => Apply("adopt the automatic layout", false, AdoptOps),
                SldAddBus a => Apply($"add bus", true, () => AddBusOps(a)),
                SldAddBranch a => Apply($"add {a.Kind.ToString().ToLowerInvariant()} {a.From}-{a.To}", true, () => AddBranchOps(a)),
                SldAddGenerator a => Apply($"add generator at bus {a.BusId}", true, () => AddGeneratorOps(a)),
                SldAddLoad a => Apply($"add load at bus {a.BusId}", true, () => AddLoadOps(a)),
                SldDelete d => Apply($"delete {d.ElementId}", true, () => DeleteOps(d.ElementId)),
                SldSetSwitch => SldCommandResult.Refused("A bus-branch case has no switches to set; open a node-breaker substation file."),
                _ => SldCommandResult.Refused("Unknown command."),
            };
        }
        catch (ArgumentException ex) { return SldCommandResult.Refused(ex.Message); }
        catch (InvalidOperationException ex) { return SldCommandResult.Refused(ex.Message); }
    }

    /// <summary>Run the operations (each an engine edit, each its own undo step in the session); undo them all if one is refused or, for a structural change, if the case would no longer check.</summary>
    private SldCommandResult Apply(string description, bool fullCheck, Func<IReadOnlyList<Func<OpResult>>> plan)
    {
        IReadOnlyList<Func<OpResult>> ops;
        try { ops = plan(); }
        catch (SldRefusal r) { return SldCommandResult.Refused(r.Message); }
        if (ops.Count == 0) return SldCommandResult.NoChange("Nothing to do.");
        HistoryMark mark = _s.MarkHistory();
        int applied = 0;
        var messages = new List<string>();
        foreach (Func<OpResult> op in ops)
        {
            OpResult r = op();
            if (!r.Ok)
            {
                Rollback(applied, mark);
                return SldCommandResult.Refused(r.Message);
            }
            if (r.Changed) { applied++; messages.Add(r.Message); }
        }
        if (applied == 0) return SldCommandResult.NoChange(messages.Count > 0 ? messages[0] : "Nothing changed.");

        GridModel? staged = null;
        if (fullCheck)
        {
            SaveReport check = SaveGate.Run(_s, new SaveOptions(CheckOnly: true));
            if (check.Outcome != SaveOutcome.CheckPassed)
            {
                Rollback(applied, mark);
                return SldCommandResult.Refused("The case would no longer be valid, so the change was not made. " + check.Headline,
                    check.Errors.Select(e => e.ToString()).ToList());
            }
        }
        staged = StageLoad(_s);
        if (staged is null)
        {
            Rollback(applied, mark);
            return SldCommandResult.Refused("The case would no longer load, so the change was not made.");
        }
        _grid = staged;
        _model = SldModel.From(staged);
        _undo.Add((description, applied));
        _redo.Clear();
        _revision++;
        return SldCommandResult.Done(char.ToUpperInvariant(description[0]) + description[1..] + ".");
    }

    private void Rollback(int applied, HistoryMark mark)
    {
        for (int i = 0; i < applied; i++) _s.Undo();
        _s.RestoreHistory(mark);
    }

    public SldCommandResult Undo()
    {
        if (_undo.Count == 0) return SldCommandResult.Refused("Nothing to undo.");
        (string d, int steps) = _undo[^1];
        for (int i = 0; i < steps; i++) _s.Undo();
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((d, steps));
        Reproject();
        return SldCommandResult.Done("Undid: " + d + ".");
    }

    public SldCommandResult Redo()
    {
        if (_redo.Count == 0) return SldCommandResult.Refused("Nothing to redo.");
        (string d, int steps) = _redo[^1];
        for (int i = 0; i < steps; i++) _s.Redo();
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((d, steps));
        Reproject();
        return SldCommandResult.Done("Redid: " + d + ".");
    }

    private void Reproject()
    {
        _grid = StageLoad(_s) ?? throw new InvalidOperationException("The case no longer loads after the history step.");
        _model = SldModel.From(_grid);
        _revision++;
    }

    /// <summary>Save through the save gate (staged, schema and loader checked, minimal text change). A refusal changes nothing on disk.</summary>
    public SldSaveResult Save()
    {
        SaveReport r = SaveGate.Run(_s);
        _revision++;
        return new SldSaveResult(r.Saved, r.Headline, r.Lines().ToList());
    }

    /// <summary>Check the case as it stands without writing it.</summary>
    public SldSaveResult Check()
    {
        SaveReport r = SaveGate.Run(_s, new SaveOptions(CheckOnly: true));
        return new SldSaveResult(r.Outcome == SaveOutcome.CheckPassed, r.Headline, r.Lines().ToList());
    }

    /// <summary>
    /// The model of the case as its documents stand now, loaded exactly as <see cref="JsonGridLoader.LoadDirectory(string, bool)"/> loads a
    /// case folder from disk (generation dispatched from the capacities unless the case says otherwise), for a client that studies the
    /// case rather than draws it. Null when the documents no longer load. <see cref="Grid"/> is the undispatched model the diagram draws.
    /// </summary>
    public GridModel? LoadStudyModel() => StageLoad(_s, dispatch: true);

    /// <summary>The model of the case as its documents stand now: the files staged in a fresh temporary folder and read by the real case loader (no dispatch, so no number of the case changes).</summary>
    private static GridModel? StageLoad(CaseSession s, bool dispatch = false)
    {
        string? stage = null;
        try
        {
            // SEC-04: a user-only temporary folder (0700 outside Windows), not one every local account can read.
            stage = Directory.CreateTempSubdirectory("gridsim-sld-").FullName;
            foreach (CaseDocument d in s.Documents.Where(d => d.Exists)) File.WriteAllBytes(Path.Combine(stage, d.FileName), d.Text.ToArray());
            return JsonGridLoader.LoadDirectory(stage, dispatch);
        }
        catch (Exception ex) when (CaseSession.IsLoaderFailure(ex)) { return null; }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temporary folder is harmless */ }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Plans: command -> engine operations
    // ---------------------------------------------------------------------------------------------------------------------------

    private sealed class SldRefusal(string message) : Exception(message);

    private static string Fmt(double v)
    {
        if (!double.IsFinite(v)) throw new SldRefusal("A coordinate or value is not a finite number.");
        double r = Math.Round(v, 6);
        return WorkbenchText.Canon(r == 0 ? 0 : r);
    }

    private RecordRef BusRecord(int busId)
    {
        CaseDocument d = _s.Doc("buses.json");
        int at = _model.NodeOfBus(busId);
        if (at < 0) throw new SldRefusal($"There is no bus {busId.ToString(Inv)}.");
        JsonSourceNode? rec = d.Text.Root.Elements.ElementAtOrDefault(at);
        if (rec is null || RecordRead.Int(rec, "id", d.KeyComparer) != busId)
            throw new SldRefusal($"Bus {busId.ToString(Inv)} is not where the diagram expects it in buses.json; reopen the case.");
        return new RecordRef(d.Id, "", at);
    }

    private IReadOnlyList<Func<OpResult>> MoveOps(SldMoveBus m)
    {
        string x = Fmt(m.X), y = Fmt(m.Y);
        RecordRef r = BusRecord(m.BusId);
        return [() => CaseOps.SetBusCoordinates(_s, [new CaseOps.CoordinateEdit(r, x, y)], $"move bus {m.BusId.ToString(Inv)}", r)];
    }

    private IReadOnlyList<Func<OpResult>> AdoptOps()
    {
        var o = new SldOptions();
        SldPlacement p = SldAutoLayout.Place(_model, o);
        const double pad = 100;
        double minX = p.X.Length == 0 ? 0 : p.X.Min(), minY = p.Y.Length == 0 ? 0 : p.Y.Min();
        var edits = new List<CaseOps.CoordinateEdit>();
        for (int i = 0; i < _model.Nodes.Count; i++)
            edits.Add(new CaseOps.CoordinateEdit(BusRecord(_model.Nodes[i].Key), Fmt((p.X[i] - minX + pad) / o.LayoutScale), Fmt((p.Y[i] - minY + pad) / o.LayoutScale)));
        return [() => CaseOps.SetBusCoordinates(_s, edits, "adopt the automatic layout", null)];
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Pairs(params (string Key, string? Value)[] items)
        => items.Where(i => i.Value is not null).Select(i => new KeyValuePair<string, string>(i.Key, i.Value!)).ToList();

    private Func<OpResult> AddBranchOp(SldBranchKind kind, int from, int to, double r, double x, double rating, string? name, double bPu = 0, double tapRatio = 1)
    {
        if (from == to) throw new SldRefusal("A branch needs two different buses.");
        string file = kind == SldBranchKind.Line ? "transmission.json" : "transformer.json";
        CaseDocument d = _s.Doc(file);
        IReadOnlyList<KeyValuePair<string, string>> values = Pairs(("name", name), ("from", from.ToString(Inv)), ("to", to.ToString(Inv)),
            ("rPu", Fmt(r)), ("xPu", Fmt(x)), ("ratingMva", rating > 0 ? Fmt(rating) : null),
            ("bPu", kind == SldBranchKind.Line && bPu != 0 ? Fmt(bPu) : null), ("tapRatio", kind == SldBranchKind.Transformer && tapRatio != 1 ? Fmt(tapRatio) : null));
        return () => CaseOps.AddRecord(_s, d.Id, "", -1, values);
    }

    private IReadOnlyList<Func<OpResult>> AddBusOps(SldAddBus a)
    {
        int id = a.BusId ?? (_model.Nodes.Count == 0 ? 1 : _model.Nodes.Max(n => n.Key) + 1);
        if (!(a.BaseKv > 0)) throw new SldRefusal("A bus needs a base voltage above zero.");
        CaseDocument d = _s.Doc("buses.json");
        var ops = new List<Func<OpResult>>
        {
            () => CaseOps.AddRecord(_s, d.Id, "", -1, Pairs(("id", id.ToString(Inv)), ("name", a.Name), ("baseKv", Fmt(a.BaseKv)), ("x", Fmt(a.X)), ("y", Fmt(a.Y)))),
        };
        if (a.ConnectTo is int to)
        {
            if (_model.NodeOfBus(to) < 0) throw new SldRefusal($"There is no bus {to.ToString(Inv)} to connect to.");
            ops.Add(AddBranchOp(a.Kind, to, id, a.RPu, a.XPu, a.RatingMva, null));
        }
        return ops;
    }

    private IReadOnlyList<Func<OpResult>> AddBranchOps(SldAddBranch a)
    {
        if (_model.NodeOfBus(a.From) < 0 || _model.NodeOfBus(a.To) < 0) throw new SldRefusal("Both buses of a branch must exist.");
        return [AddBranchOp(a.Kind, a.From, a.To, a.RPu, a.XPu, a.RatingMva, a.Name, a.BPu, a.TapRatio)];
    }

    private IReadOnlyList<Func<OpResult>> AddGeneratorOps(SldAddGenerator a)
    {
        if (_model.NodeOfBus(a.BusId) < 0) throw new SldRefusal($"There is no bus {a.BusId.ToString(Inv)}.");
        CaseDocument d = _s.Doc("generation.json");
        var v = Pairs(("name", a.Name), ("bus", a.BusId.ToString(Inv)), ("capacityMva", Fmt(a.CapacityMva)), ("outputMw", Fmt(a.OutputMw)),
            ("voltageSetpoint", a.VoltageSetpointPu is double vs ? Fmt(vs) : null));
        return [() => CaseOps.AddRecord(_s, d.Id, "", -1, v)];
    }

    private IReadOnlyList<Func<OpResult>> AddLoadOps(SldAddLoad a)
    {
        if (_model.NodeOfBus(a.BusId) < 0) throw new SldRefusal($"There is no bus {a.BusId.ToString(Inv)}.");
        CaseDocument d = _s.Doc("demand.json");
        var v = Pairs(("name", a.Name), ("bus", a.BusId.ToString(Inv)), ("pMw", Fmt(a.PMw)), ("qMvar", Fmt(a.QMvar)));
        return [() => CaseOps.AddRecord(_s, d.Id, "", -1, v)];
    }

    private static int RecordCount(CaseDocument d) => d.Exists ? d.Text.Root.Elements.Count : 0;

    private IReadOnlyList<Func<OpResult>> DeleteOps(string id)
    {
        int slash = id.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !int.TryParse(id[(slash + 1)..], NumberStyles.Integer, Inv, out int number)) throw new SldRefusal($"'{id}' is not a diagram element id.");
        switch (id[..slash])
        {
            case "bus":
                return [() => CaseOps.DeleteRecord(_s, BusRecord(number))];
            case "branch":
            {
                CaseDocument lines = _s.Doc("transmission.json"), xfmrs = _s.Doc("transformer.json");
                int nl = RecordCount(lines);
                if (nl + RecordCount(xfmrs) != _grid.Branches.Count) throw new SldRefusal("The branch list does not line up with the case files; reopen the case.");
                if (number < 0 || number >= _grid.Branches.Count) throw new SldRefusal($"There is no branch {number.ToString(Inv)}.");
                RecordRef r = number < nl ? new RecordRef(lines.Id, "", number) : new RecordRef(xfmrs.Id, "", number - nl);
                return [() => CaseOps.DeleteRecord(_s, r)];
            }
            case "gen":
            {
                CaseDocument gens = _s.Doc("generation.json");
                if (RecordCount(gens) != _grid.AllGenerators.Count) throw new SldRefusal("The generator list does not line up with the case files; reopen the case.");
                if (number < 0 || number >= RecordCount(gens)) throw new SldRefusal($"There is no generator {number.ToString(Inv)}.");
                return [() => CaseOps.DeleteRecord(_s, new RecordRef(gens.Id, "", number))];
            }
            case "load":
            {
                CaseDocument dem = _s.Doc("demand.json");
                var at = new List<int>();
                for (int i = 0; i < RecordCount(dem); i++)
                    if (RecordRead.Int(dem.Text.Root.Elements[i], "bus", dem.KeyComparer) == number) at.Add(i);
                if (at.Count == 0) throw new SldRefusal($"Bus {number.ToString(Inv)} has no demand record.");
                at.Reverse();
                return at.Select<int, Func<OpResult>>(i => () => CaseOps.DeleteRecord(_s, new RecordRef(dem.Id, "", i))).ToList();
            }
            case "shunt":
            {
                RecordRef bus = BusRecord(number);
                return [() => CaseOps.ResetField(_s, bus.Document, bus.Pointer, "gs"), () => CaseOps.ResetField(_s, bus.Document, bus.Pointer, "bs")];
            }
            default:
                throw new SldRefusal($"'{id}' is not an element a bus-branch diagram can delete.");
        }
    }
}

/// <summary>
/// The diagram of a node-breaker file (<c>gridsim-substations/1</c>, R4-11) opened for editing. The only command is a switch state
/// (<see cref="SldSetSwitch"/>): it is made on the parsed model and written with the format's own canonical writer, so on a file in
/// canonical form exactly one value changes; undo and redo are the commands with the state put back. The file carries no layout, so the
/// diagram is always laid out automatically and a busbar cannot be moved.
/// </summary>
public sealed class SldSubstationDocument : ISldDocument
{
    private readonly string _path;
    private byte[] _baseline;
    private SubstationModel _model;
    private SldModel _sld;
    private string _text;
    private readonly List<(string Description, SldSetSwitch Do, SldSetSwitch Inverse)> _undo = [];
    private readonly List<(string Description, SldSetSwitch Do, SldSetSwitch Inverse)> _redo = [];
    private long _revision;

    private SldSubstationDocument(string path, byte[] bytes, SubstationModel model)
    {
        _path = path;
        _baseline = bytes;
        _text = new System.Text.UTF8Encoding(false).GetString(bytes);
        _model = model;
        _sld = SldModel.From(model);
    }

    public string Name => Path.GetFileName(_path);
    public bool CanEdit => true;
    public SubstationModel Substations => _model;
    public SldModel Model => _sld;
    public long Revision => _revision;
    public string Text => _text;
    public bool IsDirty => !new System.Text.UTF8Encoding(false).GetBytes(_text).AsSpan().SequenceEqual(_baseline);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>The state of every switch, by id: true when open.</summary>
    public IReadOnlyDictionary<string, bool> SwitchStates() => _model.AllSwitches.ToDictionary(s => s.Id, s => s.NormalOpen, StringComparer.Ordinal);

    public static SldSubstationDocument Open(string path)
    {
        string full = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(full);
        SubstationModel m = SubstationJson.Read(new System.Text.UTF8Encoding(false, true).GetString(bytes));
        return new SldSubstationDocument(full, bytes, m);
    }

    /// <summary>Open from text (nothing on disk: <see cref="Save"/> refuses). For the web client and for tests.</summary>
    public static SldSubstationDocument FromText(string name, string json)
    {
        SubstationModel m = SubstationJson.Read(json);
        return new SldSubstationDocument(name, new System.Text.UTF8Encoding(false).GetBytes(json), m);
    }

    public SldCommandResult Execute(SldCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not SldSetSwitch set)
            return SldCommandResult.Refused("A node-breaker file carries no layout and has one editable thing, the state of a switch; its diagram is laid out automatically.");
        NbSwitch? sw = _model.AllSwitches.FirstOrDefault(s => s.Id == set.SwitchId);
        if (sw is null) return SldCommandResult.Refused($"There is no switch '{set.SwitchId}'.");
        if (sw.NormalOpen == set.Open) return SldCommandResult.NoChange($"Switch {sw.Id} is already {(set.Open ? "open" : "closed")}.");
        string d = $"{(set.Open ? "open" : "close")} switch {sw.Id}";
        Set(set);
        _undo.Add((d, set, new SldSetSwitch(sw.Id, sw.NormalOpen)));
        _redo.Clear();
        return SldCommandResult.Done(char.ToUpperInvariant(d[0]) + d[1..] + ".");
    }

    private void Set(SldSetSwitch c)
    {
        var subs = _model.Substations.Select(s => s with { Switches = s.Switches.Select(x => x.Id == c.SwitchId ? x with { NormalOpen = c.Open } : x).ToList() }).ToList();
        // Every member but the substations is carried over member-wise, so nothing the file holds is lost on a switch edit.
        SubstationModel next = _model.WithSubstations(subs);
        _text = SubstationJson.Write(next);
        _model = SubstationJson.Read(_text);   // the written form read back: what is shown is what is saved
        _sld = SldModel.From(_model);
        _revision++;
    }

    public SldCommandResult Undo()
    {
        if (_undo.Count == 0) return SldCommandResult.Refused("Nothing to undo.");
        var e = _undo[^1];
        Set(e.Inverse);
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(e);
        return SldCommandResult.Done("Undid: " + e.Description + ".");
    }

    public SldCommandResult Redo()
    {
        if (_redo.Count == 0) return SldCommandResult.Refused("Nothing to redo.");
        var e = _redo[^1];
        Set(e.Do);
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(e);
        return SldCommandResult.Done("Redid: " + e.Description + ".");
    }

    /// <summary>Write the file (a temporary sibling, then a move over the original). Nothing is written when nothing changed; a document opened from text cannot be saved.</summary>
    public SldSaveResult Save()
    {
        if (!File.Exists(_path)) return new SldSaveResult(false, "NOT SAVED: this document was not opened from a file.", ["NOT SAVED: this document was not opened from a file."]);
        if (!IsDirty) return new SldSaveResult(false, "No changes to save.", ["No changes to save."]);
        if (!File.ReadAllBytes(_path).AsSpan().SequenceEqual(_baseline))
            return new SldSaveResult(false, "NOT SAVED: the file changed on disk since it was opened.", ["NOT SAVED: the file changed on disk since it was opened."]);
        string tmp = _path + ".gridsim-new";
        File.WriteAllBytes(tmp, new System.Text.UTF8Encoding(false).GetBytes(_text));
        File.Move(tmp, _path, overwrite: true);
        _baseline = new System.Text.UTF8Encoding(false).GetBytes(_text);
        return new SldSaveResult(true, "Saved " + Name + ".", ["Saved " + Name + "."]);
    }
}
