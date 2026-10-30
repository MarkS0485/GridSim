using System.Globalization;
using System.Text.RegularExpressions;

namespace GridSim.Gda;

/// <summary>
/// Resolves logical GDA dataset names to files under a <c>--root</c> (the read-only
/// <c>/gda</c> data lake, or a local mirror). Filenames carry publication dates
/// (e.g. <c>tec-register-05-may-2026.csv</c>), so most lookups glob a directory and
/// take the newest match rather than hard-coding a dated name. Nothing here embeds
/// data  -  only where to find it on the authorised machine.
///
/// <para>When an <see cref="AsOfUtc"/> is supplied, the "newest" register lookups honour the
/// no-forward-inference invariant: they pick the newest register published <b>on or before</b>
/// the as-of instant, never a register published inside the replayed window. This is what keeps
/// a reconstructed "state at T" from being materialised on a register that did not yet exist at T
///. With no as-of (the default) the lookups take the unconditional newest, as before.</para>
/// </summary>
public sealed class GdaPaths
{
    public string Root { get; }

    /// <summary>
    /// The historical as-of instant (UTC): "newest" register lookups will not select a register
    /// published after this. <c>null</c> means unconstrained (take the absolute newest).
    /// </summary>
    public DateTime? AsOfUtc { get; }

    public GdaPaths(string root, DateTime? asOfUtc = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"GDA root not found: {root}");
        AsOfUtc = asOfUtc;
    }

    // ---- NGED (the "NationalGrid" folder is NGED the DNO) LTDS tabular model ----
    private string LtdsDir => Path.Combine(Root, "NationalGrid", "RAWData", "LTDS Tabular Model");
    public string LtdsNodes => Need(LtdsDir, "ltds-attachment-8-node.csv");
    public string LtdsCircuits => Need(LtdsDir, "ltds-table-1-circuit.csv");
    public string LtdsTwoWinding => Need(LtdsDir, "ltds-table-2a-two-winding-transformer.csv");
    public string LtdsThreeWinding => Need(LtdsDir, "ltds-table-2b-three-winding-transformer.csv");
    // The demand table's filename varies across LTDS publications (the current archive
    // ships "table-3-2025.csv"; older mirrors used "ltds-table-3-demand.csv"). Glob any
    // "*table-3*.csv" and take the newest rather than hard-coding one dated name.
    public string LtdsDemand => NeedGlob(LtdsDir, "*table-3*.csv");
    public string LtdsGeneration => Need(LtdsDir, "table-5-generation.csv");
    public string LtdsFaultLevel => Need(LtdsDir, "ltds-table-4-fault-level.csv");

    public string BusMapping => Need(Path.Combine(Root, "NationalGrid", "RAWData", "Curtailment Bus Index"), "bus_mapping.csv");
    public string Substations => Need(Path.Combine(Root, "NationalGrid", "RAWData", "Network Capacity"), "substations.csv");

    // ---- NESO transmission overlay ----
    // N081 (October 2026): these four registers are read as the CSVs NESO publishes (NESO/RAWData/<register>). The full
    // GDA lake (W:\GDA\DataSources at the time of writing) holds them only as Parquet conversions
    // (NESO/Parquet/<dataset>), so `gda --fleet` used to stop with a bare "GDA dataset folder not found" naming the CSV
    // folder. GridSim does not read the Parquet copies (their columns are the converter's, not NESO's, and reading
    // them would be a new data path to validate). The lookup is unchanged; when the CSV folder is missing but the
    // Parquet dataset is there, the error now says exactly that and what to do. It is a ParquetOnlyDatasetException, a
    // DirectoryNotFoundException subtype, so a caller catching the base type is unchanged; since GR-795 Has lets it
    // propagate (the register is present but unreadable, which is a fault, not an absent dataset).
    public string TecRegister => NesoRegister("Transmission Entry Capacity (TEC) register", "tec-register-*.csv", "transmission_entry_capacity_tec_register");
    public string InterconnectorRegister => NesoRegister("Interconnector Register", "interconnector-register-*.csv", "interconnector_register");
    public string EmbeddedRegister => NesoRegister("Embedded Register", "embedded-register-*.csv", "embedded_register");
    public string ConstraintLimits24Mo => NesoRegister("24 Months Ahead Constraint Limits", "24-months-ahead-constraint-limit*.csv", "24_months_ahead_constraint_limits");

    /// <summary>
    /// N081: the newest NESO register CSV under <c>NESO/RAWData/&lt;csvFolder&gt;</c>, exactly as before. When that folder
    /// does not exist but the lake's Parquet conversion <c>NESO/Parquet/&lt;parquetDataset&gt;</c> does, the
    /// <see cref="DirectoryNotFoundException"/> says so (the register is in the lake, but only as Parquet, which this
    /// reader does not read) instead of only naming the missing CSV folder.
    /// </summary>
    private string NesoRegister(string csvFolder, string pattern, string parquetDataset)
    {
        string dir = Path.Combine(Root, "NESO", "RAWData", csvFolder);
        // Bug #62 (2.0.178): the GDA lake's NESO acquire step writes the published CSVs under
        // NESO/Processors/RAWData/<register> (NESO/RAWData is the converter's old default and does not exist in the
        // live lake), so the registers were never found there. NESO/RAWData still wins when it exists, so a lake copy
        // laid out the old way reads exactly as before; only when it is missing and the acquire tree holds the
        // register is that tree read. When neither exists the error below still names NESO/RAWData, as before.
        string acquired = Path.Combine(Root, "NESO", "Processors", "RAWData", csvFolder);
        if (!Directory.Exists(dir) && Directory.Exists(acquired))
            dir = acquired;
        if (!Directory.Exists(dir))
        {
            string parquet = Path.Combine(Root, "NESO", "Parquet", parquetDataset);
            if (Directory.Exists(parquet))
                throw new ParquetOnlyDatasetException(
                    $"GDA dataset folder not found: {dir}. This lake holds the NESO {csvFolder} only as Parquet ({parquet}); " +
                    $"GridSim reads the published CSVs ('{pattern}') and does not read the Parquet conversion. Put the CSVs " +
                    $"under {dir} (or point --root at a lake copy that has NESO/RAWData).");
        }
        return Newest(dir, pattern);
    }

    /// <summary>True if a logical dataset is present (so the builder can degrade gracefully).</summary>
    public bool Has(Func<GdaPaths, string> selector)
    {
        // GR-795: only "not there" is "absent". A permission error, an ambiguous file choice (InvalidDataException) or
        // the N081 Parquet-only lake (ParquetOnlyDatasetException) is a fault the user must see, so it propagates; it
        // used to be swallowed and the builder degraded without a word (for example 0 GW of interconnectors).
        try { return File.Exists(selector(this)); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException ex) when (ex is not ParquetOnlyDatasetException) { return false; }
    }

    private static string Need(string dir, string file)
    {
        string p = Path.Combine(dir, file);
        if (!File.Exists(p)) throw new FileNotFoundException($"GDA dataset not found: {p}");
        return p;
    }

    /// <summary>Like <see cref="Need"/> but the file is named by a glob (newest match wins).</summary>
    private string NeedGlob(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"GDA dataset folder not found: {dir}");
        string? best = PickNewest(Directory.EnumerateFiles(dir, pattern), AsOfUtc);
        if (best is null) throw new FileNotFoundException($"No file matching '{pattern}' under {dir}");
        return best;
    }

    private string Newest(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"GDA dataset folder not found: {dir}");
        string? best = PickNewest(Directory.EnumerateFiles(dir, pattern), AsOfUtc);
        if (best is null)
            throw new FileNotFoundException(AsOfUtc is { } asOf
                ? $"No file matching '{pattern}' published on or before {asOf:o} under {dir}"
                : $"No file matching '{pattern}' under {dir}");
        return best;
    }

    // dd-monthname-yyyy embedded anywhere in the name, e.g. "tec-register-05-may-2026.csv".
    private static readonly Regex DateInName =
        new(@"(\d{1,2})-([A-Za-z]+)-(\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] DateFormats =
    {
        "d-MMMM-yyyy", "dd-MMMM-yyyy",   // full month name (january, may)
        "d-MMM-yyyy", "dd-MMM-yyyy",     // abbreviated month name (jan, may)
    };

    /// <summary>
    /// Extracts the publication date embedded in a GDA filename (e.g. the
    /// <c>05-may-2026</c> in <c>tec-register-05-may-2026.csv</c>). Case-insensitive;
    /// returns <c>null</c> when no <c>dd-monthname-yyyy</c> token is present.
    /// </summary>
    public static DateTime? TryParseDateFromName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string name = Path.GetFileName(path);
        Match m = DateInName.Match(name);
        if (!m.Success) return null;
        // GR-827: "sept" is a common British abbreviation of September that the invariant culture does not know (its
        // short name is "Sep"), so "tec-register-05-sept-2025.csv" used to read as undated. It is read as "sep". Other
        // spellings (numeric and ISO dates) are still not parsed: in GDA names a numeric date is as often a data
        // period as a publication date.
        string token = string.Equals(m.Groups[2].Value, "sept", StringComparison.OrdinalIgnoreCase)
            ? $"{m.Groups[1].Value}-sep-{m.Groups[3].Value}"
            : m.Value;
        if (DateTime.TryParseExact(token, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime dt))
            return dt;
        return null;
    }

    /// <summary>
    /// Picks the chronologically newest file from <paramref name="files"/>. Ordering key is
    /// the date embedded in the filename (see <see cref="TryParseDateFromName"/>). When
    /// <paramref name="asOfUtc"/> is supplied, files published <b>after</b> it are excluded first,
    /// so the result is the newest file that already existed at the as-of instant (the
    /// no-forward-inference guard). Returns <c>null</c> for an empty sequence - or when
    /// nothing satisfies the as-of.
    /// <para><b>File modification times are never used</b> (the rule <c>Tabular.PickCsv</c> already follows): an mtime
    /// is when the file was downloaded or synced, it differs from machine to machine, and ranking on it made the same
    /// command build different models on different machines (and, under an as-of, prefer a freshly synced undated copy
    /// that may post-date the as-of). A single candidate is returned whether or not its name is dated. With more than
    /// one candidate, every one must carry a parseable date in its name; otherwise there is no reproducible way to say
    /// which is newest, and <see cref="InvalidDataException"/> names the candidates (keep one, or rename each with its
    /// publication date).</para>
    /// </summary>
    public static string? PickNewest(IEnumerable<string> files, DateTime? asOfUtc = null)
    {
        IEnumerable<string> candidates = files;
        if (asOfUtc is { } asOf)
            // Only a date embedded in the FILENAME is a trustworthy publication date for the
            // no-forward-inference guard. A file with no dated name (e.g. the "24 Months Ahead
            // Constraint Limits" export) has only its local download mtime - which is reset on
            // every mirror sync and is NOT a publication date. Excluding on it would make such
            // undated datasets silently vanish whenever the mirror is refreshed, so undated
            // files are never excluded by the as-of; only files whose name proves they were
            // published after the as-of are dropped.
            candidates = candidates.Where(f => TryParseDateFromName(f) is not { } d || d <= asOf);

        List<string> list = candidates.ToList();
        if (list.Count <= 1) return list.FirstOrDefault();

        string[] undated = list.Where(f => TryParseDateFromName(f) is null)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal).ToArray();
        if (undated.Length > 0)
            throw new InvalidDataException(
                $"Cannot tell which of {list.Count} candidate files is the newest: {undated.Length} have no publication date " +
                $"in their name ({string.Join(", ", undated.Select(Path.GetFileName))}), and file modification times are not " +
                "used (they differ between machines). Keep one file, or rename each with its publication date (dd-month-yyyy).");

        return list
            .OrderByDescending(f => TryParseDateFromName(f)!.Value)
            // Stable tiebreak so equal-dated files resolve deterministically (newest name wins).
            .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}

/// <summary>N081 / GR-795: the CSV folder of a dataset is absent but the lake holds it as Parquet, which GridSim does not
/// read. A <see cref="DirectoryNotFoundException"/> (so existing handlers still catch it) that <see cref="GdaPaths.Has"/>
/// does not treat as "absent".</summary>
public sealed class ParquetOnlyDatasetException : DirectoryNotFoundException
{
    public ParquetOnlyDatasetException() { }

    public ParquetOnlyDatasetException(string message) : base(message) { }

    public ParquetOnlyDatasetException(string message, Exception innerException) : base(message, innerException) { }
}
