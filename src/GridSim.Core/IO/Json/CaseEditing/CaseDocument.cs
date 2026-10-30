using GridSim.Core.IO.Json.Lossless;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// One file of an open case: where it lives, the bytes it had when opened (or last saved), and its current text. A
/// file that did not exist has a null baseline; a file the editor has not created has no current text. The document
/// is dirty exactly while its current bytes differ from the baseline (or one exists and the other does not), so an
/// edit undone back to the original makes it clean again.
/// </summary>
internal sealed class CaseDocument
{
    private byte[]? _baseline;

    public CaseDocument(int id, string path, CaseFileSpec? spec, string schemaFile, string rootPointer, StringComparer keyComparer, byte[]? baseline)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(schemaFile);
        ArgumentNullException.ThrowIfNull(keyComparer);
        Id = id;
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Spec = spec;
        SchemaFile = schemaFile;
        RootPointer = rootPointer;
        KeyComparer = keyComparer;
        _baseline = baseline;
        Current = baseline is null ? null : JsonSourceText.Parse(baseline, FileName, keyComparer);
    }

    /// <summary>The document's index in <see cref="CaseSession.Documents"/>.</summary>
    public int Id { get; }

    /// <summary>The absolute path of the file.</summary>
    public string Path { get; }

    /// <summary>The file name (for messages, the title bar and error lists).</summary>
    public string FileName { get; }

    /// <summary>The v1 loader file this is, or null for a v2 document.</summary>
    public CaseFileSpec? Spec { get; }

    /// <summary>The schema file the whole document validates against.</summary>
    public string SchemaFile { get; }

    /// <summary>The schema pointer of the document root inside <see cref="SchemaFile"/> ("" = the file's root schema).</summary>
    public string RootPointer { get; }

    /// <summary>How member names compare: OrdinalIgnoreCase for v1, Ordinal for v2.</summary>
    public StringComparer KeyComparer { get; }

    /// <summary>The bytes at open or last save; null when the file did not exist then.</summary>
    public ReadOnlySpan<byte> Baseline => _baseline;

    /// <summary>True when the file existed at open or last save.</summary>
    public bool BaselineExists => _baseline is not null;

    /// <summary>The current text; null while the file does not exist.</summary>
    public JsonSourceText? Current { get; internal set; }

    /// <summary>True while the file exists in the session (on disk at open, or created by an edit).</summary>
    public bool Exists => Current is not null;

    /// <summary>The current text, or an exception naming the file when it does not exist.</summary>
    public JsonSourceText Text => Current ?? throw new InvalidOperationException($"{FileName} does not exist in this case.");

    /// <summary>True when the current state differs from the baseline (bytes, or existence).</summary>
    public bool IsDirty
    {
        get
        {
            if (Current is null) return _baseline is not null;
            if (_baseline is null) return true;
            return !Current.Bytes.SequenceEqual(_baseline);
        }
    }

    /// <summary>Make the current state the new baseline (after a successful save).</summary>
    internal void AdvanceBaseline() => _baseline = Current?.ToArray();

    /// <summary>A copy of the baseline bytes (null when the file did not exist), for rollback and external-change checks.</summary>
    internal byte[]? BaselineCopy() => _baseline is null ? null : (byte[])_baseline.Clone();
}

/// <summary>
/// A record inside a case document: the document, the JSON pointer of the array holding it ("" for a file whose root
/// is the array) and its index. A file whose root is a single object (system.json) has index -1 and pointer "".
/// </summary>
internal readonly record struct RecordRef(int Document, string ArrayPointer, int Index)
{
    /// <summary>The JSON pointer of the record itself.</summary>
    public string Pointer => Index < 0 ? ArrayPointer : ArrayPointer + "/" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The same array, another index.</summary>
    public RecordRef WithIndex(int index) => this with { Index = index };
}
