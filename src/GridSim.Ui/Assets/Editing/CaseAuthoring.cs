using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Licensing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Models;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.1 (ASSETS plan section 3.3, Assets/Editing: "public CaseAuthoring wrapper over
// CaseSession/CaseTransaction/CaseOps"). The case editor's engine (GridSim.Core.IO.Json.CaseEditing) is internal to
// Core and reached from GridSim.Ui only (InternalsVisibleTo). This file is its public face for the ASSETS tab's Author
// mode: open a case file as an editing session, change it through the engine's operations (each one transaction, one
// undo step), undo and redo, Check, Save and Save As. It adds NO editing behaviour of its own: every change is a CaseOps
// operation, every check and save is the SaveGate, every write of a save in place is the CaseCommitter. What it adds is
// the three rules the Author mode needs on top, each refused loudly, by name:
//   1. a BUNDLED case (shipped under data/, pinned by data/bundled.sha256) is never written in place: Save refuses and
//      says to use Save As, which writes the case into a folder of the user's own (plan Phase 4: "Bundled cases: Save As
//      into user data, so the BundledNetworks hashes are not broken");
//   2. Save As never overwrites anything: the target folder must not exist or must be empty, and must not be a bundled
//      location;
//   3. (Phase 4.1 refused Check, Save and Save As for a case with an asset overlay, because the engine did not open the
//      overlay files. Phase 5b (2026-10-20) LIFTED that refusal: the engine opens the manifest's "assets" files as session
//      documents, the save gate checks them (schema, V2AssetChecker, the v2 loader on a staged copy that includes them),
//      the committer writes them with the rest and Save As copies them. What remains is a narrower rule: when the
//      manifest's "assets" list no longer matches the overlay files the session opened (it was edited in this session,
//      or it is not a list of file names), Check, Save and Save As refuse, naming both lists, rather than check or write
//      a case whose overlay the session does not hold.)

/// <summary>The kind of case an authoring session holds.</summary>
public enum AuthoringFormat
{
    /// <summary>A v1 case folder (the files JsonGridLoader reads).</summary>
    V1Folder,

    /// <summary>A v2 case manifest (gridsim-case/2) and the network files it lists.</summary>
    V2Case,

    /// <summary>A single v2 network file (gridsim-network/2) opened on its own (the run pipeline never runs one).</summary>
    V2Network,
}

/// <summary>
/// The outcome of one authoring operation (an edit, an undo, a record action). <see cref="Ok"/> false means REFUSED:
/// nothing changed and <see cref="Message"/> says why. <see cref="Changed"/> false with Ok true means there was nothing
/// to do. <see cref="Select"/> is the record the selection should follow afterwards (a new or moved record), if any.
/// </summary>
public sealed record AuthoringResult(bool Ok, bool Changed, string Message, CaseRef? Select = null)
{
    /// <summary>A refusal with its reason (nothing changed).</summary>
    public static AuthoringResult Refused(string message) => new(false, false, message);
}

/// <summary>How a Check or a save ended (the engine's SaveOutcome, in public form). Only Saved wrote anything.</summary>
public enum AuthoringOutcome
{
    /// <summary>No file differs from what is on disk: nothing was written.</summary>
    NothingToSave,

    /// <summary>The files were written (Save in place, or Save As into a new folder).</summary>
    Saved,

    /// <summary>Refused: errors found, or a rule of the Author mode (bundled case, an overlay list changed in the session, target folder). Nothing written.</summary>
    Refused,

    /// <summary>The edit adds model warnings the case did not have; the user must say yes. Nothing written.</summary>
    NeedsConfirmation,

    /// <summary>A file of the case changed on disk since it was opened (or last saved). Nothing written.</summary>
    ChangedOnDisk,

    /// <summary>Writing failed part-way; files already replaced were restored (see the headline). Nothing kept.</summary>
    CommitFailed,

    /// <summary>A Check found no error.</summary>
    CheckPassed,

    /// <summary>A Check found errors (or could not be run, the headline says why).</summary>
    CheckFailed,
}

/// <summary>One problem a Check or a save found: the file, the JSON pointer inside it ("" when not tied to a value) and
/// the message.</summary>
public sealed record AuthoringProblem(string File, string Pointer, string Message)
{
    /// <inheritdoc />
    public override string ToString() => File + (Pointer.Length > 0 ? " " + Pointer : "") + ": " + Message;
}

/// <summary>
/// What a Check or a save reports (the engine's SaveReport in public form). A refusal never reads as a success: the
/// <see cref="Headline"/> of anything but <see cref="AuthoringOutcome.Saved"/> and <see cref="AuthoringOutcome.CheckPassed"/>
/// starts with what did NOT happen.
/// </summary>
public sealed record AuthoringReport(
    AuthoringOutcome Outcome,
    string Headline,
    IReadOnlyList<AuthoringProblem> Errors,
    IReadOnlyList<string> NewWarnings,
    IReadOnlyList<string> FilesWritten,
    IReadOnlyList<string> Notes)
{
    /// <summary>True only when files were written.</summary>
    public bool Saved => Outcome == AuthoringOutcome.Saved;

    /// <summary>True only for a Check that found no error.</summary>
    public bool Passed => Outcome == AuthoringOutcome.CheckPassed;

    /// <summary>The whole report as plain text lines (headline first).</summary>
    public IEnumerable<string> Lines()
    {
        yield return Headline;
        foreach (AuthoringProblem e in Errors) yield return "  error: " + e;
        foreach (string w in NewWarnings) yield return "  new warning: " + w;
        foreach (string f in FilesWritten) yield return "  written: " + f;
        foreach (string n in Notes) yield return "  note: " + n;
    }

    /// <summary>A refusal by a rule of the Author mode itself (nothing written).</summary>
    internal static AuthoringReport Refusal(string headline, AuthoringOutcome outcome = AuthoringOutcome.Refused)
        => new(outcome, headline, Array.Empty<AuthoringProblem>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>One collection of records of the case (a v1 file's array, or one element array of one v2 network file).</summary>
/// <param name="Role">The engine's role ("buses", "generators", ...).</param>
/// <param name="Title">The section title ("Buses", "Generation", ...).</param>
/// <param name="Document">The document's index in the session.</param>
/// <param name="FileName">The document's file name.</param>
/// <param name="ArrayPointer">The JSON pointer of the array ("" for a v1 array file, "/buses" in a v2 network file).</param>
/// <param name="Exists">False when the file (v1) does not exist yet; an Add creates it.</param>
/// <param name="Count">How many records it holds now.</param>
public sealed record AuthoringRecordSet(string Role, string Title, int Document, string FileName, string ArrayPointer, bool Exists, int Count);

/// <summary>One record of a collection, for the tree: its reference, its label ("bus 12 'Drax'", "'Drax 1'", "#7") and a
/// muted detail (the file and position).</summary>
public sealed record AuthoringRecord(CaseRef Ref, string Label, string Detail);

/// <summary>One file of the case and its state.</summary>
/// <param name="FileName">The file name.</param>
/// <param name="Exists">True when the file exists in the session (on disk at open, or created by an edit).</param>
/// <param name="IsDirty">True when it differs from what is on disk.</param>
public sealed record AuthoringFile(string FileName, bool Exists, bool IsDirty);

/// <summary>Opening a case for authoring was refused (not a case file, a refused licence, an unreadable case); the
/// message says why.</summary>
public sealed class CaseAuthoringException : Exception
{
    /// <summary>A refusal with no message.</summary>
    public CaseAuthoringException() { }

    /// <summary>A refusal with its reason.</summary>
    public CaseAuthoringException(string message) : base(message) { }

    /// <summary>A refusal with its reason and the engine's exception.</summary>
    public CaseAuthoringException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A case file opened for editing (UI programme Phase 4.1): the public wrapper over the case editor's engine (CaseSession,
/// CaseTransaction, CaseOps, SaveGate, CaseCommitter). Every change is one engine operation and one undo step; nothing is
/// written until <see cref="Save"/> or <see cref="SaveAs"/> passes the save gate. Bundled cases are Save As only, a case
/// with an asset overlay is edited, checked and saved with its overlay files (Phase 5b), and every refusal says why.
/// </summary>
public sealed class CaseAuthoring
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private CaseSession _s;
    private readonly LicenceGate _gate;
    private readonly IReadOnlyList<string> _bundledRoots;
    private long _revision;

    private CaseAuthoring(CaseSession session, LicenceGate gate, IReadOnlyList<string> bundledRoots, string userCasesFolder)
    {
        _s = session;
        _gate = gate;
        _bundledRoots = bundledRoots;
        UserCasesFolder = userCasesFolder;
    }

    /// <summary>The engine's undo depth: how many undo levels are kept (the oldest is dropped beyond this).</summary>
    public const int UndoLimit = CaseSession.UndoLimit;

    /// <summary>
    /// The folder Save As offers by default: the user's own case folder in the per-user application data
    /// (<c>%LOCALAPPDATA%/GridSim/cases</c> on Windows, the XDG data folder elsewhere), beside the settings and the
    /// licence file the desktop app keeps there. Never under a bundled data root.
    /// </summary>
    public static string DefaultUserCasesFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridSim", "cases");

    /// <summary>The folder Save As suggests a copy in (<see cref="DefaultUserCasesFolder"/> unless the opener gave another).</summary>
    public string UserCasesFolder { get; }

    /// <summary>Raised after every change of the session (an edit, an undo or redo, a save, a Save As), so a view can
    /// rebuild what it shows.</summary>
    public event EventHandler? Changed;

    /// <summary>Increments on every change (as the engine's revision, kept across a Save As, which opens a new session).</summary>
    public long Revision => _revision;

    /// <summary>The engine session (for the field grid and the record actions in this assembly, and the tests).</summary>
    internal CaseSession Session => _s;

    // ===================================================================================================================
    // Opening
    // ===================================================================================================================

    /// <summary>
    /// Why Author mode is not available for <paramref name="source"/> (plan Q9: a MATPOWER .m file, a replay and the GDA
    /// bridge stream are not GridSim case files, so they have no records to edit), or null for a v1 folder or a v2
    /// manifest. An undeclared source (nothing loaded yet) is not available either.
    /// </summary>
    public static string? UnavailableReason(AssetCaseSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            AssetSourceKind.V1Folder or AssetSourceKind.V2Case => null,
            AssetSourceKind.Matpower => "AUTHOR is not available: this model was read from a MATPOWER .m file, not a GridSim case file, so it has no case records to edit.",
            AssetSourceKind.Replay => "AUTHOR is not available: this is a replay of an exported run folder, not a GridSim case file, so it has no case records to edit.",
            AssetSourceKind.Bridge => "AUTHOR is not available: this model is streamed by the GDA bridge, not read from a GridSim case file, so it has no case records to edit.",
            _ => "AUTHOR is not available: no case is loaded yet. Open a GridSim case (a v1 case folder or a v2 case manifest) first.",
        };
    }

    /// <summary>
    /// Open the case <paramref name="source"/> names for authoring, under <paramref name="licence"/> (the gate the run
    /// pipeline's load passed). <paramref name="bundledRoots"/> are the shipped data roots (default: the real ones,
    /// <see cref="BundledNetworks.DataRoots"/>); a case under one of their bundled folders is Save As only.
    /// <paramref name="userCasesFolder"/> is where Save As suggests a copy (default <see cref="DefaultUserCasesFolder"/>).
    /// Throws <see cref="CaseAuthoringException"/> with the reason when the source is not a case file (Q9) or the case
    /// cannot be opened (a refused licence, a missing or malformed file).
    /// </summary>
    public static CaseAuthoring Open(AssetCaseSource source, LicenceGate licence, IReadOnlyList<string>? bundledRoots = null, string? userCasesFolder = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(licence);
        if (UnavailableReason(source) is string why) throw new CaseAuthoringException(why);
        IReadOnlyList<string> roots = bundledRoots ?? BundledNetworks.DataRoots;
        string users = userCasesFolder ?? DefaultUserCasesFolder;
        return new CaseAuthoring(OpenSession(source.Kind == AssetSourceKind.V1Folder ? AuthoringFormat.V1Folder : AuthoringFormat.V2Case,
            source.Path!, licence), licence, roots, users);
    }

    // Open an engine session, turning the engine's refusals into one public exception naming the case.
    private static CaseSession OpenSession(AuthoringFormat format, string path, LicenceGate licence)
    {
        try
        {
            return format == AuthoringFormat.V1Folder ? CaseSession.OpenV1(path, licence) : CaseSession.OpenV2(path, licence);
        }
        catch (Exception ex) when (ex is CaseLicenceDeniedException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or JsonException)
        {
            // A refused licence, an unreadable case (not a case folder, a network file outside the folder), a missing or
            // locked file, malformed JSON. Anything else is a bug and is not swallowed.
            throw new CaseAuthoringException("AUTHOR could not open the case " + path + ": " + ex.Message, ex);
        }
    }

    // ===================================================================================================================
    // What the case is
    // ===================================================================================================================

    /// <summary>The case folder (v1) or the folder of the v2 manifest.</summary>
    public string RootPath => _s.RootPath;

    /// <summary>The v2 manifest's full path (v2 case), or the v1 folder.</summary>
    public string CasePath => Format == AuthoringFormat.V1Folder ? _s.RootPath : _s.Documents[0].Path;

    /// <summary>The case's name for people: the v1 folder's name, or the manifest's file name.</summary>
    public string CaseName => Format == AuthoringFormat.V1Folder ? Path.GetFileName(_s.RootPath) : _s.Documents[0].FileName;

    /// <summary>What kind of case this is.</summary>
    public AuthoringFormat Format => _s.Format switch
    {
        CaseFormat.V1Folder => AuthoringFormat.V1Folder,
        CaseFormat.V2Case => AuthoringFormat.V2Case,
        _ => AuthoringFormat.V2Network,
    };

    /// <summary>
    /// True when the case is a Bundled Network: in one of the shipped data folders (the location rule, so an EDITED
    /// shipped case, whose content no longer matches the manifest, still counts) or bundled by content as the licence
    /// gate decides it. Such a case is Save As only.
    /// </summary>
    public bool IsBundled => _s.IsBundled || BundledNetworks.IsBundledPath(_s.RootPath, _bundledRoots);

    /// <summary>True when any file differs from what is on disk.</summary>
    public bool IsDirty => _s.IsDirty;

    /// <summary>The files that differ from what is on disk.</summary>
    public IReadOnlyList<string> DirtyFiles => _s.DirtyDocuments.Select(d => d.FileName).ToList();

    /// <summary>Every file of the case (v1: the eleven loader files, absent ones included; v2: the manifest and its
    /// network files) and its state.</summary>
    public IReadOnlyList<AuthoringFile> Files => _s.Documents.Select(d => new AuthoringFile(d.FileName, d.Exists, d.IsDirty)).ToList();

    /// <summary>Files in a v1 folder the loader does not read (listed, never written; Save As copies them unchanged).</summary>
    public IReadOnlyList<string> OtherFiles => _s.OtherFiles;

    /// <summary>The number of buses (the licence counts these).</summary>
    public int BusCount => _s.BusCount;

    /// <summary>True when there is a change to undo.</summary>
    public bool CanUndo => _s.CanUndo;

    /// <summary>True when there is an undone change to redo.</summary>
    public bool CanRedo => _s.CanRedo;

    /// <summary>What Undo would reverse, or null.</summary>
    public string? UndoDescription => _s.UndoDescription;

    /// <summary>What Redo would repeat, or null.</summary>
    public string? RedoDescription => _s.RedoDescription;

    /// <summary>How many undo levels are held now (at most <see cref="UndoLimit"/>).</summary>
    public int UndoDepth => _s.UndoDepth;

    /// <summary>What was changed since the last save (empty when the history no longer reaches it).</summary>
    public IReadOnlyList<string> ChangesSinceSave => _s.ChangesSinceSave;

    /// <summary>True once a Save or Save As of this authoring wrote files (the run pipeline must reload the case).</summary>
    public bool SavedThisSession { get; private set; }

    /// <summary>The last Check's report, or null before the first Check (a save does not replace it).</summary>
    public AuthoringReport? LastCheck { get; private set; }

    /// <summary>The <see cref="Revision"/> the last Check was made at (-1 before the first).</summary>
    public long LastCheckRevision { get; private set; } = -1;

    /// <summary>What pre-flight found when the case was opened: the schema error count, the loader's (or v2 semantic
    /// checks') refusal, if any. Existing problems never block an unrelated edit; they show in Check.</summary>
    public string PreflightText
    {
        get
        {
            CasePreflight p = _s.Preflight;
            string loader = p.LoaderError is null ? "the case loads" : "the case does not load as it is: " + p.LoaderError;
            return string.Create(Inv, $"At open: {p.SchemaErrorCount} schema error(s); {loader}; {p.ModelWarnings.Count} model warning(s).");
        }
    }

    // ===================================================================================================================
    // The asset overlay (Phase 5b: edited, checked and saved with the case)
    // ===================================================================================================================

    /// <summary>The overlay files the v2 manifest lists in "assets" (empty for v1, a lone network file, or no overlay).</summary>
    public IReadOnlyList<string> OverlayFiles
    {
        get
        {
            if (Format != AuthoringFormat.V2Case || !_s.Documents[0].Exists) return Array.Empty<string>();
            JsonSourceNode? assets = RecordRead.Member(_s.Documents[0].Text.Root, "assets", StringComparer.Ordinal);
            if (assets is null) return Array.Empty<string>();
            if (assets.Kind != JsonSourceKind.Array) return new[] { "(an 'assets' member that is not a list)" };
            return assets.Elements.Select(e => e.Kind == JsonSourceKind.String ? e.StringValue! : _s.Documents[0].Text.RawText(e)).ToList();
        }
    }

    /// <summary>True when the manifest carries an asset overlay (an "assets" member with at least one file, or one that
    /// is not a list at all).</summary>
    public bool HasOverlay => OverlayFiles.Count > 0;

    /// <summary>The overlay files this session opened (the manifest's "assets" when it was opened, or last saved as),
    /// as paths relative to the case folder with '/' separators, in manifest order.</summary>
    public IReadOnlyList<string> OpenedOverlayFiles
        => _s.AssetDocuments.Select(d => Path.GetRelativePath(_s.RootPath, d.Path).Replace('\\', '/')).ToList();

    /// <summary>
    /// Phase 5c: the overlay records as the session holds them NOW (unsaved edits included), read leniently for the
    /// substation topology view (<see cref="global::GridSim.Ui.Assets.Overlay.SubstationTopologyView.ReadRecords"/>). A file that is absent, or
    /// whose text does not parse, gives no records. Empty for a case without an overlay.
    /// </summary>
    public IReadOnlyList<global::GridSim.Core.IO.Json.Overlay.AssetOverlayRecord> OverlayRecordsNow()
    {
        var docs = new List<(string, JsonElement)>();
        foreach (CaseDocument d in _s.AssetDocuments)
        {
            if (!d.Exists) continue;
            try { docs.Add((d.FileName, d.Text.ToElement())); }
            catch (JsonException) { /* a file that does not parse has no records to draw; Check says why */ }
        }
        return global::GridSim.Ui.Assets.Overlay.SubstationTopologyView.ReadRecords(docs);
    }

    /// <summary>The overlay files as session documents (their file names, as the error lists and the tree name them).</summary>
    public IReadOnlyList<string> OverlayFileNames => _s.AssetDocuments.Select(d => d.FileName).ToList();

    /// <summary>
    /// Why Check, Save and Save As refuse this case, or null (always null for a case without an overlay). Phase 5b
    /// (2026-10-20): a case WITH an overlay is checked and saved like any other (the overlay files are session documents);
    /// it is refused only when the manifest's "assets" list no longer matches the overlay files the session opened (the
    /// list was edited in this session, or is not a list of file names), because the session would then check and write
    /// a case whose overlay it does not hold.
    /// </summary>
    public string? OverlayRefusal
    {
        get
        {
            IReadOnlyList<string> listed = OverlayFiles;
            IReadOnlyList<string> opened = OpenedOverlayFiles;
            bool same = listed.Count == opened.Count && listed.Zip(opened).All(p => string.Equals(
                Path.GetFullPath(Path.Combine(_s.RootPath, p.First)), Path.GetFullPath(Path.Combine(_s.RootPath, p.Second)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (same) return null;
            return "the manifest's asset overlay list (assets: " + (listed.Count == 0 ? "none" : string.Join(", ", listed))
                + ") no longer matches the overlay files this session opened (" + (opened.Count == 0 ? "none" : string.Join(", ", opened))
                + "), so the case cannot be checked or saved as it stands: undo the change to \"assets\" (to change the overlay files, edit "
                + "the manifest outside AUTHOR and open the case again). "
                + "The files on disk are unchanged.";
        }
    }

    // ===================================================================================================================
    // Records
    // ===================================================================================================================

    /// <summary>Every collection of records, in the engine's order (v1: file order; v2: per network file, role order).</summary>
    public IReadOnlyList<AuthoringRecordSet> Collections()
        => _s.Collections.Select(c => new AuthoringRecordSet(c.Role, CaseRoles.Title(c.Role), c.Document, _s.Documents[c.Document].FileName,
            c.ArrayPointer, _s.Documents[c.Document].Exists, _s.ArrayOf(c)?.Elements.Count ?? 0)).ToList();

    /// <summary>The records of <paramref name="collection"/>, in file order.</summary>
    public IReadOnlyList<AuthoringRecord> Records(AuthoringRecordSet collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        RecordCollection c = _s.CollectionAt(collection.Document, collection.ArrayPointer)
            ?? throw new ArgumentException($"{collection.FileName} {collection.ArrayPointer}: not a collection of this case.", nameof(collection));
        CaseDocument d = _s.Documents[c.Document];
        JsonSourceNode? array = _s.ArrayOf(c);
        if (array is null) return Array.Empty<AuthoringRecord>();
        var list = new List<AuthoringRecord>(array.Elements.Count);
        for (int i = 0; i < array.Elements.Count; i++)
        {
            JsonSourceNode rec = array.Elements[i];
            var at = new CaseRef(d.Id, c.ArrayPointer, i, d.FileName);
            list.Add(new AuthoringRecord(at, LabelOf(c.Role, d, rec, i), at.Display));
        }
        return list;
    }

    // A record's label for the tree: a bus by its id and name, anything else by its name, else its position.
    private static string LabelOf(string role, CaseDocument d, JsonSourceNode rec, int index)
    {
        string? name = RecordRead.Str(rec, "name", d.KeyComparer);
        if (role == CaseRoles.Buses && RecordRead.Int(rec, "id", d.KeyComparer) is int id)
            return "bus " + id.ToString(Inv) + (name is null ? "" : " '" + name + "'");
        if (role == CaseRoles.AssetBindings && RecordRead.Str(rec, "matchType", d.KeyComparer) is string match)
            return "type '" + match + "'";
        if (role == CaseRoles.Demands && name is null && RecordRead.Int(rec, "bus", d.KeyComparer) is int bus)
            return "demand at bus " + bus.ToString(Inv);
        return name is null ? "#" + index.ToString(Inv) : "'" + name + "'";
    }

    /// <summary>
    /// The documents whose root is ONE record (a v1 system.json, a v2 case manifest), as records at index -1. A v1
    /// system.json that does not exist yet is listed too (setting one of its fields creates it).
    /// </summary>
    public IReadOnlyList<AuthoringRecord> RootRecords()
    {
        var list = new List<AuthoringRecord>();
        foreach (CaseDocument d in _s.Documents)
        {
            bool root = d.Spec is { IsArray: false } || (d.Spec is null && d.SchemaFile == CaseSession.V2CaseSchema);
            if (!root) continue;
            var at = new CaseRef(d.Id, "", -1, d.FileName);
            string label = d.Spec is not null ? "System" : "Case manifest";
            list.Add(new AuthoringRecord(at, label, d.FileName + (d.Exists ? "" : " (absent: setting a field creates it)")));
        }
        return list;
    }

    /// <summary>The role of the collection <paramref name="record"/> belongs to, or null (a root record, or not a top-level
    /// collection).</summary>
    public string? RoleOf(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Index < 0 ? null : _s.CollectionAt(record.Document, record.ArrayPointer)?.Role;
    }

    /// <summary>True when <paramref name="record"/> exists in the session now.</summary>
    public bool Exists(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Document < 0 || record.Document >= _s.Documents.Count) return false;
        CaseDocument d = _s.Documents[record.Document];
        if (record.Index < 0) return d.Spec is { IsArray: false } || d.Exists;
        return d.Exists && d.Text.Resolve(record.Pointer) is { Kind: JsonSourceKind.Object };
    }

    /// <summary>The record as messages name it ("generation.json #17 'Drax 1'").</summary>
    public string Describe(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        CaseDocument d = _s.Documents[record.Document];
        if (record.Index < 0 || !d.Exists || d.Text.Resolve(record.Pointer) is not { Kind: JsonSourceKind.Object } rec) return record.Display;
        return RecordRead.Describe(d, rec, record.Index, d.Spec is null ? record.ArrayPointer : "");
    }

    /// <summary>Phase 5b: an asset overlay record's id, kind and parent as written (null id or parent when absent; ""
    /// kind when absent), for the overlay tree.</summary>
    public (string? Id, string Kind, string? Parent) OverlayIdentity(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        CaseDocument d = _s.Documents[record.Document];
        if (!d.Exists || d.Text.Resolve(record.Pointer) is not { Kind: JsonSourceKind.Object } rec) return (null, "", null);
        return (RecordRead.Str(rec, "id", d.KeyComparer), RecordRead.Str(rec, "kind", d.KeyComparer) ?? "", RecordRead.Str(rec, "parent", d.KeyComparer));
    }

    /// <summary>The record's exact text as it is written in its file now (for the record summary).</summary>
    public string RawText(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        CaseDocument d = _s.Documents[record.Document];
        if (!d.Exists || d.Text.Resolve(record.Pointer) is not JsonSourceNode n) return "";
        return d.Text.RawText(n);
    }

    /// <summary>The field grid of <paramref name="record"/> (PROPERTIES in Author mode).</summary>
    public FieldGridModel FieldGrid(CaseRef record) => new(this, record);

    // ===================================================================================================================
    // Changing the case (every change goes through here, so the revision and the Changed event are never missed)
    // ===================================================================================================================

    /// <summary>Run one engine operation and report it in public form; a change bumps the revision and raises Changed.</summary>
    internal AuthoringResult Apply(Func<CaseSession, OpResult> op)
    {
        OpResult r = op(_s);
        CaseRef? select = r.Select is RecordRef rr ? CaseRef.Of(rr, _s.Documents[rr.Document].FileName) : null;
        if (r.Changed) Bump();
        return new AuthoringResult(r.Ok, r.Changed, r.Message, select);
    }

    /// <summary>
    /// UI programme Phase 4.2: run several engine operations as ONE undo step (a placed bus in a case with no layout is
    /// an Add record and then the automatic layout written into the other buses). Each operation is the engine's own, so
    /// every rule and refusal is the engine's; the engine records each as an undo step of its own, so once all have
    /// succeeded those steps are undone (the documents are then exactly as before, checked byte for byte) and the net
    /// change is applied again as one transaction: per document one patch from the bytes before to the bytes after (or
    /// the creation of a file the operations created), checked byte for byte against what the operations produced. One
    /// revision, one Changed event.
    /// <para>
    /// A refusal by the FIRST operation changes nothing. A refusal by a later one undoes the earlier ones and is
    /// reported with the reason; the documents are then back as before, byte for byte, and (2026-10-24) so is the
    /// whole undo and redo history and the save point (CaseSession.MarkHistory / RestoreHistory): the undone steps are
    /// not left behind as redo levels, and the redo history the user had before the group survives. The join of a
    /// successful group restores the history the same way before it applies the net change as one step, so the undo
    /// limit trims the oldest level once, as any single change does, not once per operation of the group. Callers put
    /// the operation that can be refused first (an Add record) and follow it with ones that cannot be refused in
    /// practice (coordinates).
    /// </para>
    /// </summary>
    internal AuthoringResult ApplyAsOne(string description, IReadOnlyList<Func<CaseSession, OpResult>> ops)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(ops);
        if (ops.Count == 0) throw new ArgumentException("no operation to apply", nameof(ops));

        // The documents as they are now (null: the file does not exist), and the history with them.
        byte[]?[] before = _s.Documents.Select(d => d.Exists ? d.Text.ToArray() : null).ToArray();
        HistoryMark history = _s.MarkHistory();
        int applied = 0;
        OpResult? last = null;
        RecordRef? select = null;
        var messages = new List<string>();
        foreach (Func<CaseSession, OpResult> op in ops)
        {
            OpResult r = op(_s);
            if (!r.Ok)
            {
                for (int i = 0; i < applied; i++) _s.Undo();
                RequireBytes(before, "after the refused operation's earlier steps were undone");
                if (applied == 0) return AuthoringResult.Refused(r.Message);
                _s.RestoreHistory(history);   // the bytes are back: the undo and redo history goes back with them
                Bump();
                return AuthoringResult.Refused(r.Message
                    + " The " + applied.ToString(Inv) + " step(s) already applied for '" + description + "' were undone, so the case and its undo and redo history are as before.");
            }
            if (r.Changed)
            {
                applied++;
                messages.Add(r.Message);
            }
            if (r.Select is RecordRef rr) select ??= rr;   // the first operation's selection (the record it made)
            last = r;
        }
        if (applied == 0) return new AuthoringResult(true, false, last!.Message);
        if (applied == 1)
        {
            // One engine step already is one undo step: nothing to join.
            Bump();
            return new AuthoringResult(true, true, string.Join(" ", messages), select is RecordRef one ? CaseRef.Of(one, _s.Documents[one.Document].FileName) : null);
        }

        // Join: the net change of every document, then the steps undone, then the net change applied as one transaction.
        byte[]?[] after = _s.Documents.Select(d => d.Exists ? d.Text.ToArray() : null).ToArray();
        for (int i = 0; i < applied; i++) _s.Undo();
        RequireBytes(before, "after the steps to join were undone");
        _s.RestoreHistory(history);   // as before the group, so the joined step trims the undo limit once and clears redo once
        using (CaseTransaction tx = _s.Begin(description))
        {
            for (int i = 0; i < _s.Documents.Count; i++)
            {
                byte[]? b = before[i], a = after[i];
                if (a is null && b is null) continue;
                if (a is null) throw new InvalidOperationException(_s.Documents[i].FileName + " was removed by an editing operation; no map edit removes a file.");
                if (b is null)
                {
                    tx.Create(_s.Documents[i], a);
                    continue;
                }
                if (b.AsSpan().SequenceEqual(a)) continue;
                tx.Patch(_s.Documents[i], new[] { NetEdit(b, a) });
            }
            tx.Commit();
        }
        RequireBytes(after, "after the joined step was applied");
        Bump();
        return new AuthoringResult(true, true, string.Join(" ", messages), select is RecordRef s1 ? CaseRef.Of(s1, _s.Documents[s1.Document].FileName) : null);
    }

    // The one edit that turns bytes b into bytes a: the differing middle between their common prefix and common suffix.
    private static JsonTextEdit NetEdit(byte[] b, byte[] a)
    {
        int prefix = 0;
        int max = Math.Min(a.Length, b.Length);
        while (prefix < max && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        return new JsonTextEdit(prefix, b.Length - prefix - suffix, a.AsSpan(prefix, a.Length - prefix - suffix).ToArray());
    }

    // Fail loud when a document is not exactly as expected (the join of several steps must never change a byte).
    private void RequireBytes(byte[]?[] expected, string when)
    {
        for (int i = 0; i < _s.Documents.Count; i++)
        {
            CaseDocument d = _s.Documents[i];
            byte[]? e = expected[i];
            bool same = e is null ? !d.Exists : d.Exists && d.Text.Bytes.SequenceEqual(e);
            if (!same) throw new InvalidOperationException(d.FileName + " is not as expected " + when + "; the editing session is inconsistent.");
        }
    }

    private void Bump()
    {
        _revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undo the last change (refused, with the reason, when there is nothing to undo).</summary>
    public AuthoringResult Undo()
    {
        if (!_s.CanUndo) return AuthoringResult.Refused("Nothing to undo.");
        string what = _s.Undo();
        Bump();
        return new AuthoringResult(true, true, "Undone: " + what);
    }

    /// <summary>Redo the last undone change (refused, with the reason, when there is nothing to redo).</summary>
    public AuthoringResult Redo()
    {
        if (!_s.CanRedo) return AuthoringResult.Refused("Nothing to redo.");
        string what = _s.Redo();
        Bump();
        return new AuthoringResult(true, true, "Redone: " + what);
    }

    // ===================================================================================================================
    // Check, Save, Save As
    // ===================================================================================================================

    /// <summary>
    /// Check the case as it is now, writing nothing: the save gate's check (<c>SaveGate.Run(CheckOnly: true)</c>: every
    /// file against its schema, the loader or the v2 checks, the licence; no solve). The report becomes
    /// <see cref="LastCheck"/>. Phase 5b: a case with an asset overlay is checked with it (the overlay checker and the v2 loader on the staged case), unless its "assets" list changed in the session (see <see cref="OverlayRefusal"/>).
    /// </summary>
    public AuthoringReport Check()
    {
        AuthoringReport report = OverlayRefusal is string overlay
            ? AuthoringReport.Refusal("CHECK NOT RUN: " + overlay, AuthoringOutcome.CheckFailed)
            : Public(SaveGate.Run(_s, new SaveOptions(CheckOnly: true)));
        LastCheck = report;
        LastCheckRevision = _revision;
        return report;
    }

    /// <summary>
    /// The problems of the last Check that belong to <paramref name="record"/> (a problem's pointer inside that record),
    /// or, for a root record, the problems of its file. Empty before the first Check. The Check is a snapshot: after a
    /// later change the positions are those of the case as it was checked.
    /// </summary>
    public IReadOnlyList<AuthoringProblem> CheckProblemsFor(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (LastCheck is null) return Array.Empty<AuthoringProblem>();
        string prefix = record.Pointer;
        return LastCheck.Errors.Where(e => string.Equals(e.File, record.FileName, StringComparison.Ordinal)
            && (record.Index < 0 || e.Pointer == prefix || e.Pointer.StartsWith(prefix + "/", StringComparison.Ordinal))).ToList();
    }

    /// <summary>The problems of the last Check that belong to no record of a collection (a file as a whole, the loader,
    /// the licence).</summary>
    public IReadOnlyList<AuthoringProblem> CaseLevelProblems()
    {
        if (LastCheck is null) return Array.Empty<AuthoringProblem>();
        return LastCheck.Errors.Where(e =>
        {
            CaseDocument? d = _s.Documents.FirstOrDefault(x => string.Equals(x.FileName, e.File, StringComparison.Ordinal));
            return d is null || !d.Exists || CaseOps.TopRecordPointer(_s, d, e.Pointer).Length == 0;
        }).ToList();
    }

    /// <summary>
    /// Save the case in place: the save gate (<c>SaveGate</c>: schema, loader or v2 checks, licence, new warnings) and
    /// then the committer (<c>CaseCommitter</c>: every new file written beside its target, verified, then renamed into
    /// place). Refused for a bundled case (use <see cref="SaveAs"/>) and for a case whose "assets" list changed in the session (<see cref="OverlayRefusal"/>). With
    /// <paramref name="confirmNewWarnings"/> the user has said yes to the new model warnings an earlier attempt listed.
    /// </summary>
    public AuthoringReport Save(bool confirmNewWarnings = false)
    {
        if (IsBundled)
            return AuthoringReport.Refusal("NOT SAVED: " + _s.RootPath + " is a bundled case (shipped with GridSim; its files are pinned "
                + "by the bundled-content manifest, " + BundledNetworks.ManifestFileName + "), so it is never written in place. Use Save As... "
                + "to save your edits as your own copy in your user folder. The bundled files are unchanged.");
        if (OverlayRefusal is string overlay) return AuthoringReport.Refusal("NOT SAVED: " + overlay);
        AuthoringReport report = Public(SaveGate.Run(_s, new SaveOptions(ConfirmNewWarnings: confirmNewWarnings)));
        if (report.Saved)
        {
            SavedThisSession = true;
            Bump();
        }
        return report;
    }

    /// <summary>
    /// A folder Save As can use for a copy of this case: <see cref="UserCasesFolder"/>/&lt;case name&gt;-copy, or -copy-2,
    /// -copy-3 ... when that exists. It does not create the folder.
    /// </summary>
    public string SuggestSaveAsFolder()
    {
        string stem = Path.GetFileNameWithoutExtension(Format == AuthoringFormat.V1Folder ? _s.RootPath : Path.GetFileNameWithoutExtension(_s.Documents[0].Path));
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '-');
        if (stem.Length == 0) stem = "case";
        string first = Path.Combine(UserCasesFolder, stem + "-copy");
        if (!Directory.Exists(first) && !File.Exists(first)) return first;
        for (int n = 2; ; n++)
        {
            string next = Path.Combine(UserCasesFolder, stem + "-copy-" + n.ToString(Inv));
            if (!Directory.Exists(next) && !File.Exists(next)) return next;
        }
    }

    /// <summary>
    /// Save the case as it is now into <paramref name="targetFolder"/>, as a new case of the user's own, and carry on
    /// editing that copy (the authoring now holds it; the undo history starts again there). The case must pass the save
    /// gate's check first (with <paramref name="confirmNewWarnings"/> for new model warnings), the target must not be a
    /// bundled location, must not lie inside this case's folder, and must not exist or be an empty folder (Save As never
    /// overwrites), and the licence must allow the copy (it is not bundled). Every file of the case is written (the v1
    /// folder's other files are copied unchanged) and read back to verify; on any failure the files written so far are
    /// removed and nothing else is touched. The source case, bundled or not, is never written.
    /// </summary>
    public AuthoringReport SaveAs(string targetFolder, bool confirmNewWarnings = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        if (OverlayRefusal is string overlay) return AuthoringReport.Refusal("NOT SAVED: " + overlay);
        if (Format == AuthoringFormat.V2Network)
            return AuthoringReport.Refusal("NOT SAVED: Save As copies a v1 case folder or a v2 case manifest; this is a lone network file.");

        string target;
        try { target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return AuthoringReport.Refusal("NOT SAVED: '" + targetFolder + "' is not a usable folder path: " + ex.Message);
        }
        if (BundledNetworks.IsBundledPath(target, _bundledRoots) || BundledNetworks.IsBundledPath(target))
            return AuthoringReport.Refusal("NOT SAVED: " + target + " is inside a bundled data folder; Save As never writes a bundled location. Choose a folder of your own.");
        StringComparison cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = Path.TrimEndingDirectorySeparator(_s.RootPath);
        if (string.Equals(target, root, cmp) || target.StartsWith(root + Path.DirectorySeparatorChar, cmp))
            return AuthoringReport.Refusal("NOT SAVED: " + target + " is this case's own folder (or inside it); Save As writes a new case into a new folder. Use Save to save in place.");
        if (File.Exists(target))
            return AuthoringReport.Refusal("NOT SAVED: " + target + " is a file, not a folder.");
        bool existed = Directory.Exists(target);
        if (existed && Directory.EnumerateFileSystemEntries(target).Any())
            return AuthoringReport.Refusal("NOT SAVED: " + target + " is not empty; Save As never overwrites anything. Choose a new or empty folder.");

        // The gate's check over the case as it is now (no write), and the licence for a copy that is not bundled.
        AuthoringReport check = Public(SaveGate.Run(_s, new SaveOptions(CheckOnly: true)));
        if (!check.Passed)
            return check with
            {
                Outcome = check.Outcome == AuthoringOutcome.CheckFailed ? AuthoringOutcome.Refused : check.Outcome,
                Headline = "NOT SAVED: " + check.Headline + " Nothing was written.",
            };
        if (check.NewWarnings.Count > 0 && !confirmNewWarnings)
            return check with
            {
                Outcome = AuthoringOutcome.NeedsConfirmation,
                Headline = "NOT SAVED YET: the case adds " + check.NewWarnings.Count.ToString(Inv) + " model warning(s) it did not have when opened. Save As anyway?",
            };
        GateResult licence = _gate.Check(_s.BusCount, bundled: false);
        if (!licence.Allowed)
            return AuthoringReport.Refusal("NOT SAVED: licence: a copy outside the bundled data folders counts toward the licence (" + _s.BusCount.ToString(Inv)
                + " buses): " + licence.Message);

        // Write every file of the case into the target, each read back to verify. Any failure removes what was written.
        var written = new List<string>();
        var notes = new List<string>();
        try
        {
            Directory.CreateDirectory(target);
            foreach (CaseDocument d in _s.Documents.Where(d => d.Exists))
            {
                string rel = Path.GetRelativePath(_s.RootPath, d.Path);
                WriteVerified(Path.Combine(target, rel), d.Text.ToArray(), written);
            }
            foreach (string other in _s.OtherFiles)
            {
                WriteVerified(Path.Combine(target, other), File.ReadAllBytes(Path.Combine(_s.RootPath, other)), written);
                notes.Add(other + " is not read by the loader; copied unchanged.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string cleaned = RemoveWritten(target, written, existed);
            return AuthoringReport.Refusal("NOT SAVED: writing the copy into " + target + " failed (" + ex.Message + "). " + cleaned + " The source case is unchanged.");
        }

        // Carry on editing the copy: open it as the session (pre-flight runs on it as for any open).
        string reopen = Format == AuthoringFormat.V1Folder ? target : Path.Combine(target, _s.Documents[0].FileName);
        CaseSession copy;
        try
        {
            copy = Format == AuthoringFormat.V1Folder ? CaseSession.OpenV1(reopen, _gate) : CaseSession.OpenV2(reopen, _gate);
        }
        catch (Exception ex) when (ex is CaseLicenceDeniedException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or JsonException)
        {
            // The copy is written and verified; it is the reopen that failed. Say so; the session stays on the source.
            return new AuthoringReport(AuthoringOutcome.CommitFailed, "SAVED AS, BUT NOT REOPENED: the copy in " + target
                + " was written and verified, but opening it for editing failed: " + ex.Message + " The editor still holds the source case.",
                Array.Empty<AuthoringProblem>(), Array.Empty<string>(), written, notes);
        }
        string from = _s.RootPath;
        _s = copy;
        SavedThisSession = true;
        notes.Add("the case is now your copy in " + target + "; the bundled or source case in " + from + " was not written. The undo history starts again here.");
        Bump();
        return new AuthoringReport(AuthoringOutcome.Saved, "Saved As " + written.Count.ToString(Inv) + " file(s) into " + target + ".",
            Array.Empty<AuthoringProblem>(), check.NewWarnings, written.Select(p => Path.GetRelativePath(target, p)).ToList(), notes);
    }

    // Write bytes to a new file, flushed, then read them back: a file that differs is a failed write.
    private static void WriteVerified(string path, byte[] bytes, List<string> written)
    {
        if (File.Exists(path)) throw new IOException(Path.GetFileName(path) + " already exists in the target; nothing is overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            written.Add(path);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        byte[] back = File.ReadAllBytes(path);
        if (!back.AsSpan().SequenceEqual(bytes))
            throw new IOException(Path.GetFileName(path) + ": the bytes read back differ from the bytes written.");
    }

    // Remove the files a failed Save As wrote (and the folder, if Save As made it and it is now empty).
    private static string RemoveWritten(string target, List<string> written, bool existed)
    {
        var left = new List<string>();
        foreach (string f in written)
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add(f); }
        }
        if (!existed)
        {
            try { if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Any(e => File.Exists(e))) Directory.Delete(target, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add(target); }
        }
        return left.Count == 0 ? "The files written so far were removed." : "Could not remove: " + string.Join(", ", left) + ".";
    }

    // The engine's report in public form.
    private static AuthoringReport Public(SaveReport r) => new(
        r.Outcome switch
        {
            SaveOutcome.NothingToSave => AuthoringOutcome.NothingToSave,
            SaveOutcome.Saved => AuthoringOutcome.Saved,
            SaveOutcome.Refused => AuthoringOutcome.Refused,
            SaveOutcome.NeedsConfirmation => AuthoringOutcome.NeedsConfirmation,
            SaveOutcome.ChangedOnDisk => AuthoringOutcome.ChangedOnDisk,
            SaveOutcome.CommitFailed => AuthoringOutcome.CommitFailed,
            SaveOutcome.CheckPassed => AuthoringOutcome.CheckPassed,
            _ => AuthoringOutcome.CheckFailed,
        },
        r.Headline,
        r.Errors.Select(e => new AuthoringProblem(e.File, e.Pointer, e.Message)).ToList(),
        r.NewWarnings, r.FilesWritten, r.Notes);

    // ===================================================================================================================
    // Back to Run mode
    // ===================================================================================================================

    /// <summary>
    /// The run pipeline's source for the case as saved: the v1 folder, or the v2 manifest (with the v2 loader). Run mode
    /// reloads the case from it. A lone network file has no run source (the run pipeline reads a case manifest).
    /// </summary>
    public ModelSource RunSource(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return Format switch
        {
            AuthoringFormat.V1Folder => new ModelSource(label, _s.RootPath),
            AuthoringFormat.V2Case => ModelCatalog.V2CaseSource(_s.Documents[0].Path) with { Label = label },
            _ => throw new InvalidOperationException("A lone v2 network file has no run source: the run pipeline reads a case manifest."),
        };
    }
}
