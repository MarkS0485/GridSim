using System.Globalization;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Licensing;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>The licence gate refused a case (at open or at save). The message is the gate's own, naming the limit.</summary>
internal sealed class CaseLicenceDeniedException : Exception
{
    public CaseLicenceDeniedException() { }
    public CaseLicenceDeniedException(string message) : base(message) { }
    public CaseLicenceDeniedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>One step of a transaction, in a form that can be applied and inverted.</summary>
internal abstract record TxStep(int Document);

/// <summary>Patch a document's bytes (edits in that document's current coordinates).</summary>
internal sealed record PatchStep(int Document, IReadOnlyList<JsonTextEdit> Edits) : TxStep(Document);

/// <summary>Create a document that does not exist yet, with these bytes.</summary>
internal sealed record CreateStep(int Document, byte[] Bytes) : TxStep(Document);

/// <summary>Remove a document whose current bytes are exactly these (the inverse of a create).</summary>
internal sealed record DestroyStep(int Document, byte[] Bytes) : TxStep(Document);

/// <summary>One undo (or redo) level: what the user did, and the steps that reverse it, in the order to apply them.</summary>
internal sealed record UndoEntry(string Description, IReadOnlyList<TxStep> Steps);

/// <summary>
/// The undo and redo history and the save point at one moment (<see cref="CaseSession.MarkHistory"/>), to be put back
/// exactly (<see cref="CaseSession.RestoreHistory"/>) once the documents are back, byte for byte, as they were then.
/// </summary>
internal sealed record HistoryMark(UndoEntry[] Undo, UndoEntry[] Redo, int SavedUndoDepth);

/// <summary>
/// What was found when the case was opened (SCHEMA-EDITOR-PLAN.md section 7: existing problems appear in Check but
/// do not block saving; integrity is enforced at each operation). The save gate compares the model's warnings after
/// an edit with <see cref="ModelWarnings"/> here: only NEW warnings need the user's explicit yes.
/// </summary>
internal sealed record CasePreflight(
    IReadOnlyDictionary<string, IReadOnlyList<Schema.SchemaError>> SchemaErrors,
    string? LoaderError,
    IReadOnlyList<string> ModelWarnings)
{
    /// <summary>Total schema errors across the case's files.</summary>
    public int SchemaErrorCount => SchemaErrors.Values.Sum(e => e.Count);
}

/// <summary>
/// An open case: its documents (the bytes are the model, SCHEMA-EDITOR-PLAN.md section 4), the files it does not read,
/// what pre-flight found, and the undo / redo history. Every change goes through a <see cref="CaseTransaction"/>, which
/// is atomic (a failure part-way puts every document back) and becomes ONE undo step even when it spans files (a
/// cascading rename). Nothing here touches the disk after open; saving is <see cref="SaveGate"/> and
/// <see cref="CaseCommitter"/>.
/// </summary>
internal sealed class CaseSession
{
    /// <summary>How many undo levels are kept (the oldest is dropped beyond this).</summary>
    public const int UndoLimit = 1000;

    /// <summary>Suffix of the temporary file a save writes beside each target before renaming it into place.</summary>
    public const string NewFileSuffix = ".gridsim-new";

    private readonly List<UndoEntry> _undo = new();
    private readonly List<UndoEntry> _redo = new();
    // The open transaction, held as a plain object: the session does not own it (the caller disposes it with "using"),
    // it only checks that the one being ended is the one it handed out.
    private object? _open;
    private string _openDescription = "";
    private int _savedUndoDepth;

    private CaseSession(string rootPath, CaseFormat format, LicenceGate gate, IReadOnlyList<CaseDocument> documents,
                        IReadOnlyList<string> otherFiles, IReadOnlyList<string> leftovers, FieldCatalogue catalogue)
    {
        RootPath = rootPath;
        Format = format;
        Gate = gate;
        Documents = documents;
        OtherFiles = otherFiles;
        LeftoverFiles = leftovers;
        Catalogue = catalogue;
        IsBundled = BundledNetworks.IsBundledPath(rootPath);
        Preflight = new CasePreflight(new Dictionary<string, IReadOnlyList<Schema.SchemaError>>(), null, Array.Empty<string>());
    }

    /// <summary>The case folder (v1) or the folder of the opened v2 document.</summary>
    public string RootPath { get; }

    /// <summary>What kind of case this is.</summary>
    public CaseFormat Format { get; }

    /// <summary>The licence gate checked at open and at save.</summary>
    public LicenceGate Gate { get; }

    /// <summary>True when the case is a Bundled Network (under data/ in the repository layout): it never counts toward a bus limit.</summary>
    public bool IsBundled { get; }

    /// <summary>The case's documents. For v1, one per loader file in <see cref="CaseFileSpec.V1Files"/> order (absent files included, not existing).</summary>
    public IReadOnlyList<CaseDocument> Documents { get; }

    /// <summary>Files in the folder the loader does not read: listed, never written.</summary>
    public IReadOnlyList<string> OtherFiles { get; }

    /// <summary>Leftover <c>*.gridsim-new</c> files found at open: an earlier save was interrupted.</summary>
    public IReadOnlyList<string> LeftoverFiles { get; }

    /// <summary>The field catalogue for this case's format.</summary>
    public FieldCatalogue Catalogue { get; }

    /// <summary>What pre-flight found at open.</summary>
    public CasePreflight Preflight { get; private set; }

    /// <summary>Increments on every change (edit, undo, redo, save), so a UI can tell when to rebuild its indexes.</summary>
    public long Revision { get; private set; }

    /// <summary>True when any document differs from its baseline.</summary>
    public bool IsDirty => Documents.Any(d => d.IsDirty);

    /// <summary>The documents that differ from their baselines.</summary>
    public IEnumerable<CaseDocument> DirtyDocuments => Documents.Where(d => d.IsDirty);

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when there is something to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>What Undo would reverse, or null.</summary>
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    /// <summary>What Redo would repeat, or null.</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>How many undo levels are held.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>The descriptions of the changes made since the last save (empty when the history no longer reaches it).</summary>
    public IReadOnlyList<string> ChangesSinceSave
        => _savedUndoDepth >= 0 && _savedUndoDepth <= _undo.Count ? _undo.Skip(_savedUndoDepth).Select(u => u.Description).ToList() : Array.Empty<string>();

    /// <summary>The document of a file by name (throws for an unknown name).</summary>
    public CaseDocument Doc(string fileName)
        => Documents.FirstOrDefault(d => string.Equals(d.FileName, fileName, StringComparison.Ordinal))
           ?? throw new ArgumentException($"'{fileName}' is not a file of this case.", nameof(fileName));

    /// <summary>
    /// Every collection of records in the case: for v1 one per array file (absent files included), for v2 one per
    /// element array per network file (absent arrays included), in document then role order.
    /// </summary>
    public IReadOnlyList<RecordCollection> Collections { get; private set; } = Array.Empty<RecordCollection>();

    /// <summary>The collections of one role, in document order (v2: across every network file).</summary>
    public IEnumerable<RecordCollection> CollectionsOf(string role) => Collections.Where(c => c.Role == role);

    /// <summary>The collection whose array is at <paramref name="arrayPointer"/> in a document, or null (a nested array, say).</summary>
    public RecordCollection? CollectionAt(int doc, string arrayPointer)
        => Collections.FirstOrDefault(c => c.Document == doc && string.Equals(c.ArrayPointer, arrayPointer, StringComparison.Ordinal));

    /// <summary>The array node of a collection, or null when its document or array does not exist.</summary>
    public JsonSourceNode? ArrayOf(RecordCollection c)
    {
        ArgumentNullException.ThrowIfNull(c);
        CaseDocument d = Documents[c.Document];
        return d.Exists && d.Text.Resolve(c.ArrayPointer) is { Kind: JsonSourceKind.Array } a ? a : null;
    }

    /// <summary>
    /// The number of buses: the length of buses.json's array (v1), or the sum over every network file's buses (v2).
    /// Pinned equal to the loader's count on every bundled case by a test.
    /// </summary>
    public int BusCount => CollectionsOf(CaseRoles.Buses).Sum(c => ArrayOf(c)?.Elements.Count ?? 0);

    private IReadOnlyList<RecordCollection> BuildCollections()
    {
        var list = new List<RecordCollection>();
        foreach (CaseDocument d in Documents)
        {
            if (d.Spec is { IsArray: true, Role: string role })
                list.Add(new RecordCollection(d.Id, "", role, FieldCatalogue.V1RecordShape(d.Spec)));
            else if (d.Spec is null && d.SchemaFile == V2NetworkSchema)
            {
                RecordShape root = RecordShapeOf(d);
                foreach (string r in CaseRoles.V2Network)
                    if (root.Field(r) is { Kind: FieldKind.Array, Nested: SchemaLocation loc })
                        list.Add(new RecordCollection(d.Id, "/" + r, r, Catalogue.Shape(loc)));
            }
            else if (d.Spec is null && d.SchemaFile == V2AssetsSchema
                     && RecordShapeOf(d).Field("records") is { Kind: FieldKind.Array, Nested: SchemaLocation records })
            {
                // Phase 5b: one collection per asset overlay file, its "records" array (absent files included, as the
                // network files are). Only cases that list "assets" have these, after every network collection.
                list.Add(new RecordCollection(d.Id, OverlayRecordsPointer, CaseRoles.Overlay, Catalogue.Shape(records)));
            }
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Opening
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Open a v1 case folder. Reads every loader file that exists (a malformed file is refused, naming it), lists the
    /// files the loader does not read and any leftover <c>*.gridsim-new</c>, checks the licence (bus count, bundled
    /// path) and runs pre-flight (schema validation of every file, then the real loader). Throws
    /// <see cref="CaseLicenceDeniedException"/> when the gate refuses, <see cref="DirectoryNotFoundException"/> or
    /// <see cref="InvalidDataException"/> for an unreadable case.
    /// </summary>
    public static CaseSession OpenV1(string folder, LicenceGate gate)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(gate);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Case folder not found: {folder}");

        var docs = new List<CaseDocument>();
        foreach (CaseFileSpec spec in CaseFileSpec.V1Files)
        {
            string path = Path.Combine(full, spec.FileName);
            byte[]? bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            docs.Add(new CaseDocument(docs.Count, path, spec, spec.SchemaFile, "", StringComparer.OrdinalIgnoreCase, bytes));
        }
        if (!docs[1].Exists)
            throw new InvalidDataException($"{full}: there is no buses.json, so this is not a v1 case folder (a v1 case needs at least buses.json and generation.json).");

        var known = new HashSet<string>(CaseFileSpec.V1Files.Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
        var others = new List<string>();
        var leftovers = new List<string>();
        foreach (string f in Directory.EnumerateFiles(full).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (f.EndsWith(NewFileSuffix, StringComparison.OrdinalIgnoreCase)) leftovers.Add(f);
            else if (!known.Contains(f)) others.Add(f);
        }

        var session = new CaseSession(full, CaseFormat.V1Folder, gate, docs, others, leftovers, FieldCatalogue.V1);
        session.Collections = session.BuildCollections();
        session.CheckLicence("open");
        session.Preflight = RunPreflightV1(session);
        return session;
    }

    /// <summary>The v2 network schema file name.</summary>
    public const string V2NetworkSchema = "network.schema.json";

    /// <summary>The v2 case manifest schema file name.</summary>
    public const string V2CaseSchema = "case.schema.json";

    /// <summary>Phase 5b: the asset overlay schema file name (gridsim-assets/2), the schema of an "assets" document.</summary>
    public const string V2AssetsSchema = Overlay.V2AssetChecker.SchemaFile;

    /// <summary>Phase 5b: the asset overlay documents of the case (the manifest's "assets" files), in manifest order;
    /// empty for a v1 case, a lone network file and a case without an overlay.</summary>
    public IEnumerable<CaseDocument> AssetDocuments => Documents.Where(d => d.SchemaFile == V2AssetsSchema);

    /// <summary>Phase 5b: true when the case lists at least one asset overlay file.</summary>
    public bool HasOverlay => AssetDocuments.Any();

    /// <summary>The JSON pointer of the records array of an asset overlay document.</summary>
    public const string OverlayRecordsPointer = "/records";

    /// <summary>
    /// Open a v2 document (Mark's decision D4): a case manifest (gridsim-case/2, normally *.case.json) with every
    /// network file it lists, or a single network file (gridsim-network/2). The kind is taken from the file name
    /// (*.case.json, *.network.json) or, failing that, from the document's "schema" member. Every listed network file
    /// must lie inside the manifest's folder (fact 9: the schema's path pattern accepts "..", the editor refuses it)
    /// and be listed once; a listed file that does not exist is opened as absent and reported by pre-flight. Member
    /// names compare Ordinal (v2 is case-sensitive). Licence and pre-flight as for v1, with the v2 semantic checks in
    /// place of the loader (the editor does not run the v2 loader, <see cref="V2CaseLoader"/>; D-DOC-162, DOC-17).
    /// Phase 5b (2026-10-20): a manifest's asset overlay files ("assets", gridsim-assets/2) are opened as documents too,
    /// after the network files and under the same rules, so the overlay is edited, checked and saved with the case.
    /// </summary>
    public static CaseSession OpenV2(string file, LicenceGate gate)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(gate);
        string full = Path.GetFullPath(file);
        if (!File.Exists(full)) throw new FileNotFoundException($"v2 document not found: {file}", file);
        string folder = Path.GetDirectoryName(full)!;
        byte[] bytes = File.ReadAllBytes(full);
        JsonSourceText first = JsonSourceText.Parse(bytes, Path.GetFileName(full), StringComparer.Ordinal);
        string? schema = first.Root.Kind == JsonSourceKind.Object ? RecordRead.Str(first.Root, "schema", StringComparer.Ordinal) : null;

        bool isCase = full.EndsWith(".case.json", StringComparison.OrdinalIgnoreCase) || (!full.EndsWith(".network.json", StringComparison.OrdinalIgnoreCase) && schema == "gridsim-case/2");
        bool isNetwork = !isCase && (full.EndsWith(".network.json", StringComparison.OrdinalIgnoreCase) || schema == "gridsim-network/2");
        if (!isCase && !isNetwork)
            throw new InvalidDataException($"{Path.GetFileName(full)}: not a v2 case manifest (*.case.json, schema gridsim-case/2) or network file (*.network.json, schema gridsim-network/2).");

        var docs = new List<CaseDocument>();
        if (isNetwork)
        {
            docs.Add(new CaseDocument(0, full, null, V2NetworkSchema, "", StringComparer.Ordinal, bytes));
        }
        else
        {
            docs.Add(new CaseDocument(0, full, null, V2CaseSchema, "", StringComparer.Ordinal, bytes));
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (RecordRead.Member(first.Root, "network", StringComparer.Ordinal) is { Kind: JsonSourceKind.Array } list)
                foreach (JsonSourceNode entry in list.Elements)
                {
                    if (entry.Kind != JsonSourceKind.String) continue;   // a schema error, reported by pre-flight
                    string rel = entry.StringValue!;
                    string path = Path.GetFullPath(Path.Combine(folder, rel));
                    if (!IsInside(folder, path))
                        throw new InvalidDataException($"{Path.GetFileName(full)}: network file '{rel}' lies outside the case folder; the editor refuses to open or write files outside it.");
                    if (!seen.Add(path))
                        throw new InvalidDataException($"{Path.GetFileName(full)}: network file '{rel}' is listed twice.");
                    // Bug hunt 2026-10-29, finding 36: the editor names a document by its file name (Doc(fileName), the
                    // error lists, RecordRead.Describe), so two network files with one file name in different subfolders
                    // would be confused in every message and lookup. Refused, as the asset-overlay branch below refuses it.
                    string netName = Path.GetFileName(path);
                    if (docs.Any(d => string.Equals(d.FileName, netName, StringComparison.Ordinal)))
                        throw new InvalidDataException($"{Path.GetFileName(full)}: network file '{rel}' has the same file name as another file of the case ({netName}); the editor names files by their name, so each must be distinct.");
                    byte[]? nb = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    docs.Add(new CaseDocument(docs.Count, path, null, V2NetworkSchema, "", StringComparer.Ordinal, nb));
                }

            // Phase 5b (2026-10-20): the asset overlay files the manifest lists in "assets" (gridsim-assets/2) are
            // session documents too, opened losslessly exactly as the network files are, AFTER them (so every document
            // index a case without an overlay has is unchanged, and such a case opens exactly as before). The same rules
            // hold: inside the case folder, listed once (and not also as a network file), a missing one opened as absent
            // and reported by pre-flight. Messages and the error lists name a document by its file name, so two
            // documents of one case with the same file name (in different subfolders) are refused rather than confused.
            if (RecordRead.Member(first.Root, "assets", StringComparer.Ordinal) is { Kind: JsonSourceKind.Array } assets)
                foreach (JsonSourceNode entry in assets.Elements)
                {
                    if (entry.Kind != JsonSourceKind.String) continue;   // a schema error, reported by pre-flight
                    string rel = entry.StringValue!;
                    string path = Path.GetFullPath(Path.Combine(folder, rel));
                    if (!IsInside(folder, path))
                        throw new InvalidDataException($"{Path.GetFileName(full)}: asset overlay file '{rel}' lies outside the case folder; the editor refuses to open or write files outside it.");
                    if (!seen.Add(path))
                        throw new InvalidDataException($"{Path.GetFileName(full)}: asset overlay file '{rel}' is listed twice (or is also listed as a network file).");
                    string name = Path.GetFileName(path);
                    if (docs.Any(d => string.Equals(d.FileName, name, StringComparison.Ordinal)))
                        throw new InvalidDataException($"{Path.GetFileName(full)}: asset overlay file '{rel}' has the same file name as another file of the case ({name}); the editor names files by their name, so each must be distinct.");
                    byte[]? ab = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    docs.Add(new CaseDocument(docs.Count, path, null, V2AssetsSchema, "", StringComparer.Ordinal, ab));
                }
        }

        var leftovers = Directory.EnumerateFiles(folder, "*" + NewFileSuffix).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var session = new CaseSession(folder, isCase ? CaseFormat.V2Case : CaseFormat.V2Network, gate, docs, Array.Empty<string>(), leftovers, FieldCatalogue.V2);
        session.Collections = session.BuildCollections();
        session.CheckLicence("open");
        session.Preflight = RunPreflightV2(session);
        return session;
    }

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="folder"/> (both full paths).</summary>
    internal static bool IsInside(string folder, string path)
    {
        string root = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Pre-flight for a v2 case: every existing document against its schema, then the v2 semantic checks.</summary>
    private static CasePreflight RunPreflightV2(CaseSession s)
    {
        var schemaErrors = new Dictionary<string, IReadOnlyList<Schema.SchemaError>>(StringComparer.Ordinal);
        foreach (CaseDocument d in s.Documents.Where(d => d.Exists))
            schemaErrors[d.FileName] = BundledSchemas.V2Validate.Validate(d.Text.ToElement(), d.SchemaFile);
        (List<SaveProblem> errors, List<string> warnings) = V2CaseChecks.Run(s);
        string? semantic = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
        return new CasePreflight(schemaErrors, semantic, warnings);
    }

    /// <summary>Check the licence for the current bus count; throws <see cref="CaseLicenceDeniedException"/> when refused.</summary>
    internal void CheckLicence(string when)
    {
        GateResult r = Gate.Check(BusCount, IsBundled);
        if (!r.Allowed)
            throw new CaseLicenceDeniedException($"Licence refused the case at {when} ({BusCount.ToString(CultureInfo.InvariantCulture)} buses): {r.Message}");
    }

    /// <summary>
    /// Pre-flight for a v1 case: every existing file against its schema, then the loader as every verb runs it
    /// (dispatch on). A loader failure is recorded, not thrown: the editor opens a broken case so it can be fixed.
    /// </summary>
    private static CasePreflight RunPreflightV1(CaseSession s)
    {
        var schemaErrors = new Dictionary<string, IReadOnlyList<Schema.SchemaError>>(StringComparer.Ordinal);
        foreach (CaseDocument d in s.Documents.Where(d => d.Exists))
            schemaErrors[d.FileName] = BundledSchemas.V1Validate.Validate(d.Text.ToElement(), d.SchemaFile);

        string? loaderError = null;
        IReadOnlyList<string> warnings = Array.Empty<string>();
        try
        {
            GridModel model = JsonGridLoader.LoadDirectory(s.RootPath, dispatch: true);
            warnings = WarningsOf(model);
        }
        catch (Exception ex) when (IsLoaderFailure(ex))
        {
            loaderError = ex.Message;
        }
        return new CasePreflight(schemaErrors, loaderError, warnings);
    }

    /// <summary>The warning-severity diagnostics of a model's structural validation, as messages.</summary>
    internal static IReadOnlyList<string> WarningsOf(GridModel model)
        => model.Validate().Where(d => d.Severity == ValidationSeverity.Warning).Select(d => d.Message).ToList();

    /// <summary>The exception types a loader run turns bad input into (anything else is a bug and is not swallowed).</summary>
    internal static bool IsLoaderFailure(Exception ex)
        => ex is InvalidDataException or System.Text.Json.JsonException or IOException or ArgumentException
              or InvalidOperationException or KeyNotFoundException or FormatException or NotSupportedException;

    // ---------------------------------------------------------------------------------------------------------------
    // Transactions, undo and redo
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Start a transaction. Only one may be open at a time. Steps apply immediately (so later steps see earlier ones);
    /// <see cref="CaseTransaction.Commit"/> records them as one undo step, and disposing without committing puts every
    /// document back exactly.
    /// </summary>
    public CaseTransaction Begin(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (_open is not null) throw new InvalidOperationException($"a transaction ('{_openDescription}') is already open.");
        var tx = new CaseTransaction(this, description);
        _open = tx;
        _openDescription = description;
        return tx;
    }

    internal void EndTransaction(CaseTransaction tx, UndoEntry? entry)
    {
        if (!ReferenceEquals(_open, tx)) throw new InvalidOperationException("ending a transaction that is not the open one.");
        _open = null;
        if (entry is null) return;
        _undo.Add(entry);
        if (_undo.Count > UndoLimit)
        {
            _undo.RemoveAt(0);
            _savedUndoDepth--;   // the save point slides down with the history (negative = no longer reachable)
        }
        // A new change invalidates the redo history; if the save point was in it, it is no longer reachable.
        if (_savedUndoDepth > _undo.Count - 1) _savedUndoDepth = -1;
        _redo.Clear();
        Revision++;
    }

    /// <summary>
    /// The undo and redo history and the save point as they are now (2026-10-24). A caller that runs several
    /// operations as one group (CaseAuthoring.ApplyAsOne) marks the history first, so that when it has undone its own
    /// steps it can put the history back exactly as it was: without this, its undone steps were left behind as redo
    /// levels and the redo history the user had before the group was lost. Throws while a transaction is open.
    /// </summary>
    internal HistoryMark MarkHistory()
    {
        if (_open is not null) throw new InvalidOperationException("cannot mark the history while a transaction is open.");
        return new HistoryMark(_undo.ToArray(), _redo.ToArray(), _savedUndoDepth);
    }

    /// <summary>
    /// Put back the history marked by <see cref="MarkHistory"/>. The caller must first have put every document back,
    /// byte for byte, as it was at the mark (each entry's steps are only valid against those exact bytes); the
    /// documents' saved baselines are untouched, so their unsaved-changes state follows from their bytes as before.
    /// Throws while a transaction is open.
    /// </summary>
    internal void RestoreHistory(HistoryMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (_open is not null) throw new InvalidOperationException("cannot restore the history while a transaction is open.");
        _undo.Clear();
        _undo.AddRange(mark.Undo);
        _redo.Clear();
        _redo.AddRange(mark.Redo);
        _savedUndoDepth = mark.SavedUndoDepth;
        Revision++;
    }

    /// <summary>Undo the last change. Returns its description; throws when there is nothing to undo.</summary>
    public string Undo()
    {
        if (_open is not null) throw new InvalidOperationException("cannot undo while a transaction is open.");
        if (_undo.Count == 0) throw new InvalidOperationException("Nothing to undo.");
        UndoEntry entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new UndoEntry(entry.Description, ApplyAll(entry.Steps)));
        Revision++;
        return entry.Description;
    }

    /// <summary>Redo the last undone change. Returns its description; throws when there is nothing to redo.</summary>
    public string Redo()
    {
        if (_open is not null) throw new InvalidOperationException("cannot redo while a transaction is open.");
        if (_redo.Count == 0) throw new InvalidOperationException("Nothing to redo.");
        UndoEntry entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new UndoEntry(entry.Description, ApplyAll(entry.Steps)));
        Revision++;
        return entry.Description;
    }

    /// <summary>Apply steps in order and return the steps that reverse them, in the order to apply those.</summary>
    private List<TxStep> ApplyAll(IReadOnlyList<TxStep> steps)
    {
        var inverse = new List<TxStep>(steps.Count);
        foreach (TxStep s in steps) inverse.Add(ApplyStep(s));
        inverse.Reverse();
        return inverse;
    }

    /// <summary>Apply one step to its document and return the step that reverses it.</summary>
    internal TxStep ApplyStep(TxStep step)
    {
        CaseDocument d = Documents[step.Document];
        switch (step)
        {
            case PatchStep p:
            {
                (JsonSourceText next, IReadOnlyList<JsonTextEdit> inverse) = d.Text.Apply(p.Edits);
                d.Current = next;
                return new PatchStep(d.Id, inverse);
            }
            case CreateStep c:
                if (d.Exists) throw new InvalidOperationException($"{d.FileName} already exists; it cannot be created.");
                d.Current = JsonSourceText.Parse((byte[])c.Bytes.Clone(), d.FileName, d.KeyComparer);
                return new DestroyStep(d.Id, c.Bytes);
            case DestroyStep x:
                if (!d.Exists || !d.Text.Bytes.SequenceEqual(x.Bytes))
                    throw new InvalidOperationException($"{d.FileName} is not in the state its removal expects; the history is inconsistent.");
                d.Current = null;
                return new CreateStep(d.Id, x.Bytes);
            default:
                throw new InvalidOperationException($"unknown transaction step {step.GetType().Name}.");
        }
    }

    /// <summary>After a save the user confirmed: the model's warnings at that point are the accepted set for the next save.</summary>
    internal void AcceptWarnings(IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        Preflight = Preflight with { ModelWarnings = warnings.ToList() };
    }

    /// <summary>After a successful save: every document's current state becomes its baseline; the undo history is kept.</summary>
    internal void MarkSaved()
    {
        foreach (CaseDocument d in Documents) d.AdvanceBaseline();
        _savedUndoDepth = _undo.Count;
        Revision++;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Shapes
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The shape a document's root is walked from: for a v1 file, its records' shape (the array items, or the system
    /// object); for a v2 document, the root object of its schema (network or case manifest).
    /// </summary>
    public RecordShape RecordShapeOf(CaseDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Spec is not null) return FieldCatalogue.V1RecordShape(doc.Spec);
        return Catalogue.Shape(new SchemaLocation(doc.SchemaFile, doc.RootPointer));
    }

    /// <summary>
    /// The shape of the object at <paramref name="objectPointer"/> in a document (a top-level record, or a nested
    /// object such as a bus's zip block or one boundary branch), or null when the pointer does not lead to a record.
    /// </summary>
    public RecordShape? ShapeAt(CaseDocument doc, string objectPointer)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(objectPointer);
        RecordShape shape = RecordShapeOf(doc);
        string[] tokens = objectPointer.Length == 0 ? Array.Empty<string>() : objectPointer[1..].Split('/');
        int i = 0;
        // Array-root files: the first token is the record index.
        if (doc.Spec is { IsArray: true })
        {
            if (tokens.Length == 0) return null;
            i = 1;
        }
        while (i < tokens.Length)
        {
            string name = tokens[i].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            FieldSpec? f = ReferenceEquals(doc.KeyComparer, StringComparer.OrdinalIgnoreCase) ? shape.FieldIgnoringCase(name) : shape.Field(name);
            if (f?.Nested is not SchemaLocation loc) return null;
            switch (f.Kind)
            {
                case FieldKind.Object:
                    shape = Catalogue.Shape(loc);
                    i++;
                    break;
                case FieldKind.Array:
                case FieldKind.Map:
                    if (i + 1 >= tokens.Length || !Catalogue.IsRecord(loc)) return null;
                    shape = Catalogue.Shape(loc);
                    i += 2;
                    break;
                default:
                    return null;
            }
        }
        return shape;
    }
}

/// <summary>
/// An open, atomic change to a <see cref="CaseSession"/>. Each step applies at once (later steps see earlier ones);
/// <see cref="Commit"/> turns them into one undo step; disposing an uncommitted transaction (an operation that
/// failed part-way, or was refused after a first step) reverses every step, so the documents are exactly as before.
/// </summary>
internal sealed class CaseTransaction : IDisposable
{
    private readonly CaseSession _session;
    private readonly List<TxStep> _inverse = new();
    private bool _finished;

    internal CaseTransaction(CaseSession session, string description)
    {
        _session = session;
        Description = description;
    }

    /// <summary>What the user did, as the undo history and messages show it.</summary>
    public string Description { get; set; }

    /// <summary>True once any step has been applied.</summary>
    public bool HasChanges => _inverse.Count > 0;

    /// <summary>Patch a document (edits in its current coordinates; an empty list does nothing).</summary>
    public void Patch(CaseDocument doc, IReadOnlyList<JsonTextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(edits);
        ThrowIfFinished();
        if (edits.Count == 0) return;
        _inverse.Add(_session.ApplyStep(new PatchStep(doc.Id, edits)));
    }

    /// <summary>
    /// Patch a document with edits the caller has already applied to its current text (N099): <paramref name="applied"/>
    /// is <c>before.Apply(edits)</c>, so the new text is not parsed a second time (one parse of gb-full generation.json
    /// is tens of milliseconds). Same effect and same undo step as <see cref="Patch(CaseDocument, IReadOnlyList{JsonTextEdit})"/>.
    /// Throws when <paramref name="before"/> is no longer the document's current text.
    /// </summary>
    public void Patch(CaseDocument doc, JsonSourceText before, (JsonSourceText Text, IReadOnlyList<JsonTextEdit> Inverse) applied)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(applied.Text);
        ArgumentNullException.ThrowIfNull(applied.Inverse);
        ThrowIfFinished();
        if (!ReferenceEquals(doc.Current, before))
            throw new InvalidOperationException($"{doc.FileName} changed since the edits were applied to it; the patch is stale.");
        doc.Current = applied.Text;
        _inverse.Add(new PatchStep(doc.Id, applied.Inverse));
    }

    /// <summary>Create a document that does not exist, with initial bytes.</summary>
    public void Create(CaseDocument doc, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(bytes);
        ThrowIfFinished();
        _inverse.Add(_session.ApplyStep(new CreateStep(doc.Id, bytes)));
    }

    /// <summary>Record the steps as one undo level (nothing is recorded when no step changed anything).</summary>
    public void Commit()
    {
        ThrowIfFinished();
        _finished = true;
        UndoEntry? entry = null;
        if (_inverse.Count > 0)
        {
            var steps = new List<TxStep>(_inverse);
            steps.Reverse();
            entry = new UndoEntry(Description, steps);
        }
        _session.EndTransaction(this, entry);
    }

    /// <summary>Roll back an uncommitted transaction: every applied step is reversed, last first.</summary>
    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        for (int i = _inverse.Count - 1; i >= 0; i--) _session.ApplyStep(_inverse[i]);
        _session.EndTransaction(this, null);
    }

    private void ThrowIfFinished()
    {
        if (_finished) throw new InvalidOperationException("the transaction has already been committed or rolled back.");
    }
}
