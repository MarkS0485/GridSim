using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Pages;

namespace GridSim.Ui.Assets.Overviews;

// UI programme ASSETS B2 (plan S8): the OVERVIEW page of the class nodes the A2 explorer never had (DEMANDS,
// BOUNDARIES, PROTECTIONS, ASSET CLASSES with their bindings, OUT OF SERVICE) and of their members. These pages are new
// (no WPF counterpart, so no parity oracle): one page type shows them all, its content made by OverviewBuilder from the
// model, the authored bundles and the case records, and exact tests pin that content. The page is an AssetPageModel so it
// lives on the A2 explorer's stack like every other page: opened through the navigator, refreshed only while visible,
// kept in the back / forward history.

/// <summary>A headline figure of a class page ("RECORDS 214").</summary>
/// <param name="Label">The tile's caption.</param>
/// <param name="Value">The figure.</param>
/// <param name="Note">A muted line under it, or "".</param>
public sealed record OverviewTile(string Label, string Value, string Note);

/// <summary>A labelled value of a unit page ("Monitored bus  12 'Drax 400'").</summary>
/// <param name="Label">What the value is.</param>
/// <param name="Value">The value.</param>
public sealed record OverviewFact(string Label, string Value);

/// <summary>A row of a page's list: a member of a class, or a link from a unit page.</summary>
/// <param name="Name">The row's name.</param>
/// <param name="Detail">A muted second text.</param>
/// <param name="Value">A figure on the right, or "".</param>
/// <param name="Target">The node a click selects, or null when the row opens nothing.</param>
public sealed record OverviewRow(string Name, string Detail, string Value, AssetKey? Target)
{
    /// <summary>True when a click selects something.</summary>
    public bool CanOpen => Target is not null;
}

/// <summary>
/// Phase 5e (2026-10-20): one row of an <see cref="OverviewTable"/>: one text per column, in column order. Display only:
/// a table row opens nothing (the page's link rows, <see cref="OverviewRow"/>, are the links).
/// </summary>
/// <param name="Cells">The cell texts, one per column of the table.</param>
public sealed record OverviewTableRow(IReadOnlyList<string> Cells);

/// <summary>
/// Phase 5e (2026-10-20): a small table on an OVERVIEW page (an overlay record's kind-specific values with their units,
/// a substation's earthing summary, a three-winding transformer's legs). Display only; every column title carries its
/// unit in brackets where the column has one, so a value is never shown without what it is measured in.
/// </summary>
/// <param name="Title">The caption over the table (it says what the table is, and for overlay tables that it is carried
/// and not used by the solver).</param>
/// <param name="Columns">The column titles, with units in brackets ("Resistance (ohm)").</param>
/// <param name="Rows">The rows; each has exactly one cell per column (the constructor refuses anything else).</param>
public sealed record OverviewTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<OverviewTableRow> Rows)
{
    /// <summary>A muted line under the table ("" for none): where the values come from, or why one is absent.</summary>
    public string Note { get; init; } = "";

    /// <summary>True when the table has a note.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>
    /// Fail loud on a malformed table: a row with a different number of cells from the columns would show values under
    /// the wrong heading, so it is refused here rather than drawn.
    /// </summary>
    public static OverviewTable Of(string title, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows, string note = "")
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(note);
        var list = new List<OverviewTableRow>();
        foreach (IReadOnlyList<string> cells in rows)
        {
            if (cells.Count != columns.Count)
                throw new ArgumentException($"table '{title}': a row has {cells.Count} cells for {columns.Count} columns.", nameof(rows));
            list.Add(new OverviewTableRow(cells));
        }
        return new OverviewTable(title, columns, list) { Note = note };
    }
}

/// <summary>Everything an <see cref="AssetOverviewPage"/> shows, for one frame.</summary>
/// <param name="Eyebrow">The small caption over the title ("ASSET CLASS", "BOUNDARY").</param>
/// <param name="Title">The page title.</param>
/// <param name="Sub">The line under the title.</param>
/// <param name="Tiles">The headline figures (class pages).</param>
/// <param name="Facts">The labelled values (unit pages).</param>
/// <param name="RowsHeader">The caption over the list.</param>
/// <param name="Rows">The list (members of a class, or links).</param>
/// <param name="RowsNote">"showing 200 of 1,234" when the list is capped, else "".</param>
/// <param name="Note">A plain-English note on what the page shows (where the data comes from), or "".</param>
/// <param name="LinksToInverterFleet">True when the page offers OPEN INVERTER FLEET (asset classes and bindings).</param>
public sealed record OverviewContent(string Eyebrow, string Title, string Sub, IReadOnlyList<OverviewTile> Tiles,
    IReadOnlyList<OverviewFact> Facts, string RowsHeader, IReadOnlyList<OverviewRow> Rows, string RowsNote, string Note,
    bool LinksToInverterFleet)
{
    /// <summary>Phase 5e: the page's tables (shown under the facts), empty on every page that has none (every model
    /// page: only overlay record pages carry tables).</summary>
    public IReadOnlyList<OverviewTable> Tables { get; init; } = Array.Empty<OverviewTable>();
}

/// <summary>
/// The OVERVIEW page of a B2 class node or of one of its members (UI programme ASSETS B2). Made for one page key and one
/// asset index; every frame it is shown, its content is rebuilt by <see cref="OverviewBuilder.Build"/> from that frame.
/// </summary>
public sealed class AssetOverviewPage : AssetPageModel
{
    private OverviewContent _content;

    /// <summary>A page for <paramref name="key"/> over <paramref name="index"/>, showing its content before any frame.</summary>
    public AssetOverviewPage(AssetPageKey key, AssetIndex index)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Index = index ?? throw new ArgumentNullException(nameof(index));
        _content = OverviewBuilder.Build(key, index, null);
    }

    /// <summary>The page this is.</summary>
    public AssetPageKey Key { get; }

    /// <summary>The index the page reads.</summary>
    public AssetIndex Index { get; }

    /// <summary>The content of the last refresh.</summary>
    public OverviewContent Content => _content;

    /// <inheritdoc />
    public override string Eyebrow => _content.Eyebrow;

    /// <inheritdoc />
    public override string Title => _content.Title;

    /// <inheritdoc />
    public override string Sub => _content.Sub;

    /// <summary>The headline figures.</summary>
    public IReadOnlyList<OverviewTile> Tiles => _content.Tiles;

    /// <summary>The labelled values.</summary>
    public IReadOnlyList<OverviewFact> Facts => _content.Facts;

    /// <summary>The caption over the list.</summary>
    public string RowsHeader => _content.RowsHeader;

    /// <summary>The list.</summary>
    public IReadOnlyList<OverviewRow> Rows => _content.Rows;

    /// <summary>The list's cap note, or "".</summary>
    public string RowsNote => _content.RowsNote;

    /// <summary>True when the list is capped.</summary>
    public bool HasRowsNote => _content.RowsNote.Length > 0;

    /// <summary>The page's note, or "".</summary>
    public string Note => _content.Note;

    /// <summary>True when the page has a note.</summary>
    public bool HasNote => _content.Note.Length > 0;

    /// <summary>True when the page offers OPEN INVERTER FLEET.</summary>
    public bool LinksToInverterFleet => _content.LinksToInverterFleet;

    /// <summary>True when the page has headline figures.</summary>
    public bool HasTiles => _content.Tiles.Count > 0;

    /// <summary>True when the page has labelled values.</summary>
    public bool HasFacts => _content.Facts.Count > 0;

    /// <summary>Phase 5e: the page's tables (display only).</summary>
    public IReadOnlyList<OverviewTable> Tables => _content.Tables;

    /// <summary>Phase 5e: true when the page has at least one table.</summary>
    public bool HasTables => _content.Tables.Count > 0;

    /// <summary>
    /// UI programme ASSETS slice 5-view: the overlay record this page shows, or null (not an overlay record page, or the
    /// record is gone from the index's overlay).
    /// </summary>
    public OverlayEntry? OverlayRecord => Key.Kind == AssetPageKind.OverlayRecord && Key.Name is string id ? Index.Overlay?.Find(id) : null;

    /// <summary>True when the page shows a toolkit single-line diagram (a substation overlay record with busbar
    /// sections, <see cref="OverlaySubstationDiagram"/>).</summary>
    public bool HasDiagram => OverlaySubstationDiagram.CanDraw(OverlayRecord);

    /// <summary>
    /// Phase 5c: the solved voltage (pu) of every bus in the last frame, by bus id, kept only on a page with a diagram
    /// (the substation diagram colours each section by the voltage of the bus it collapses to); null before any frame
    /// and on every other page. Read-only: it is the frame's values, nothing is fed back.
    /// </summary>
    public IReadOnlyDictionary<long, double>? SolvedVoltages { get; private set; }

    /// <inheritdoc />
    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (HasDiagram)
        {
            var vm = new Dictionary<long, double>();
            IReadOnlyList<int> ids = frame.Result.BusIds;
            for (int i = 0; i < ids.Count && i < frame.Result.Vm.Length; i++) vm.TryAdd(ids[i], frame.Result.Vm[i]);
            SolvedVoltages = vm;
        }
        _content = OverviewBuilder.Build(Key, Index, frame);
        // Every bound value is the content's, so one change notification per property.
        foreach (string p in new[] { nameof(Eyebrow), nameof(Title), nameof(Sub), nameof(Tiles), nameof(Facts), nameof(RowsHeader),
                     nameof(Rows), nameof(RowsNote), nameof(HasRowsNote), nameof(Note), nameof(HasNote), nameof(LinksToInverterFleet),
                     nameof(HasTiles), nameof(HasFacts), nameof(Tables), nameof(HasTables), nameof(Content) })
            Raise(p);
    }
}
