using System.Globalization;
using System.Reflection;
using System.Text;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Ukpn;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N185 (October 2026): on the full GDA lake the UKPN RAWData CSVs are header-only stubs and the rows live in the
/// lake's Parquet conversion (UkPowerNetworks/Parquet/...), so `gda --dno ukpn` could not build. UkpnSource now reads
/// a header-only (or missing) table's Parquet copy, with the CSV reader's meaning for every cell; a CSV with rows is
/// still read exactly as before; neither with rows is the old loud refusal.
///
/// The fixture writes each table twice from ONE specification: as CSV text, and as the Parquet file the lake's
/// converter (pandas.read_csv(skipinitialspace=True) then pyarrow) would write from that text: leading blanks gone,
/// empty cells null, a numeric column typed double (or int64 when every cell is an integer), a column with any
/// non-number kept as text. Every number has at most 15 significant digits, so the converter's parse equals .NET's.
/// The two builds must then be identical field by field, doubles compared by their bits.
/// </summary>
public sealed class N185UkpnParquetPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-n185-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ---- the fixture specification -----------------------------------------------------------------------------

    private enum Kind { Text, Double, Long }

    private sealed record Column(string Name, Kind Kind, string[] Cells);

    private sealed record Table(string FolderName, string FileStem, Column[] Columns)
    {
        public int Rows => Columns[0].Cells.Length;
    }

    // Circuit table: real UKPN headers (with the stray space in "Susceptance_B_ Percent"). Row 2 copies the lake's
    // one row with empty impedances (Thorpe Grid, "Two Sites Sharing the Same Switch"); row 3 carries the text
    // values that keep the rating and length columns as text on the lake ("Not Available", "Series Reactor");
    // leading blanks (gone after the converter) and trailing blanks (kept by it, trimmed by the CSV reader) test
    // the text rule.
    private static Table Circuits() => new("Long_Term_Development_Statement_LTDS_Table_1_Circuit_data", "ukpn-ltds-table-1-circuit-data", new[]
    {
        new Column("GridSupplyPoint", Kind.Text, new[] { "Amersham", "Norwich", "  Amersham " }),
        new Column("FromSubstation", Kind.Text, new[] { "Sub A 132kV", "Sub B 33kV", "Sub B 33kV" }),
        new Column("FromNode", Kind.Text, new[] { "N1", "N2", " N2" }),
        new Column("ToNode", Kind.Text, new[] { "N2", "N2B", "N3 " }),
        new Column("ToSubstation", Kind.Text, new[] { "Sub B 33kV", "Sub B Local 33kV", "Sub C 33kV" }),
        new Column("LineName", Kind.Text, new[] { "L1", "Two Sites Sharing the Same Switch", "L3" }),
        new Column("OperatingVoltage (kV)", Kind.Double, new[] { "132", "33", "33" }),
        new Column("Positive_Sequence_Impedance_R_Percent", Kind.Double, new[] { "1.2", "", "0.3317" }),
        new Column("Positive_Sequence_Impedance_X_Percent", Kind.Double, new[] { "4.54", "", "2.12345678901234" }),
        new Column("Susceptance_B_ Percent", Kind.Double, new[] { "2.07", "", "0.000123" }),
        new Column("Rating_Amps_Summer", Kind.Text, new[] { "546", "", "Not Available" }),
        new Column("Rating_Amps_Winter", Kind.Text, new[] { "651", "", "1,200" }),
        new Column("Circuit_Length (km)", Kind.Text, new[] { "20.443", "", "Series Reactor" }),
        new Column("LicenceArea", Kind.Text, new[] { "Eastern Power Networks (EPN)", "Eastern Power Networks (EPN)", "Eastern Power Networks (EPN)" }),
        new Column("ID", Kind.Long, new[] { "1", "2", "3" }),
    });

    private static Table Transformers() => new("Long_Term_Development_Statement_LTDS_Table_2a_Transformer_Data_2W", "ukpn-ltds-table-2a-transformer-2w", new[]
    {
        new Column("GridSupplyPoint", Kind.Text, new[] { "Amersham" }),
        new Column("HV_Node", Kind.Text, new[] { "N3" }),
        new Column("HV_Substation", Kind.Text, new[] { "Sub C 33kV" }),
        new Column("Voltage_HV (kV)", Kind.Double, new[] { "33" }),
        new Column("LV_Node", Kind.Text, new[] { "N4" }),
        new Column("LV_Substation", Kind.Text, new[] { "Sub D 11kV" }),
        new Column("Voltage_LV (kV)", Kind.Double, new[] { "11.5" }),
        new Column("Vector_Group", Kind.Text, new[] { "Dyn11" }),
        new Column("Positive_Sequence_Impedance_R_Percent", Kind.Double, new[] { "7.7796" }),
        new Column("Positive_Sequence_Impedance_X_Percent", Kind.Double, new[] { "112.3978" }),
        new Column("Zero_Sequence_Impedance_X_Percent", Kind.Double, new[] { "101.158" }),
        new Column("Tap_Range_Max_Percent", Kind.Long, new[] { "110" }),
        new Column("Tap_Range_Min_Percent", Kind.Long, new[] { "90" }),
        new Column("Transformer_Rating_MVA_Winter", Kind.Double, new[] { "15" }),
        new Column("Transformer_Rating_MVA_Summer", Kind.Double, new[] { "12" }),
        new Column("LicenceArea", Kind.Text, new[] { "Eastern Power Networks (EPN)" }),
    });

    private static Table Demand() => new("Long_Term_Development_Statement_LTDS_Table_3a_Observed_Peak_Demand", "ukpn-ltds-table-3a-load-data-observed", new[]
    {
        new Column("GridSupplyPoint", Kind.Text, new[] { "Amersham", "Amersham", "Amersham" }),
        new Column("Substation", Kind.Text, new[] { "Sub D 11kV", "Sub D 11kV", "Sub C 33kV" }),
        new Column("Season", Kind.Text, new[] { "Winter", "Summer", "Winter" }),
        new Column("Maximum_Demand_24_25_MW", Kind.Double, new[] { "4.3", "3.8", "17.25" }),
        new Column("Maximum_Demand_24_25_PF", Kind.Double, new[] { "0.96", "0.96", "" }),
        new Column("Firm_Capacity_MW", Kind.Double, new[] { "14.4", "11", "40" }),
        new Column("LicenceArea", Kind.Text, new[] { "Eastern Power Networks (EPN)", "Eastern Power Networks (EPN)", "Eastern Power Networks (EPN)" }),
    });

    private static Table Generation(string capacity = "210.526315789473") => new("Long_Term_Development_Statement_LTDS_Table_5_Generation", "ukpn-ltds-table-5-generation", new[]
    {
        new Column("GridSupplyPoint", Kind.Text, new[] { "Amersham", "Amersham" }),
        new Column("Substation", Kind.Text, new[] { "Sub D 11kV", "-" }),
        new Column("Connection_Voltage_kV", Kind.Double, new[] { "11", "132" }),
        new Column("InstalledCapacity_MVA", Kind.Double, new[] { capacity, "480" }),
        new Column("FuelType", Kind.Text, new[] { "Solar", "Energy Storage (>=1MW)" }),
        new Column("Connected_Accepted", Kind.Text, new[] { "Connected", "Accepted" }),
    });

    private static Table[] AllTables() => new[] { Circuits(), Transformers(), Demand(), Generation() };

    // ---- writers -----------------------------------------------------------------------------------------------

    private string NewRoot(string name)
    {
        string root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CsvQuote(string cell) =>
        cell.Contains(',') || cell.Contains('"') ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;

    /// <summary>The CSV under RAWData/&lt;folder&gt;/; headerOnly writes the header line alone (the lake's stubs).</summary>
    private static void WriteCsv(string root, Table t, bool headerOnly)
    {
        string dir = Path.Combine(root, "UkPowerNetworks", "RAWData", t.FolderName);
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.Append('\uFEFF');   // the published files carry a UTF-8 BOM
        sb.Append(string.Join(",", t.Columns.Select(c => CsvQuote(c.Name)))).Append("\r\n");
        if (!headerOnly)
            for (int r = 0; r < t.Rows; r++)
                sb.Append(string.Join(",", t.Columns.Select(c => CsvQuote(c.Cells[r])))).Append("\r\n");
        File.WriteAllText(Path.Combine(dir, t.FileStem + ".csv"), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>The Parquet copy under Parquet/&lt;folder&gt;/, as the lake's converter writes it from the CSV text.</summary>
    private static async Task WriteParquet(string root, Table t, Func<Column, Kind>? overrideKind = null)
    {
        string dir = Path.Combine(root, "UkPowerNetworks", "Parquet", t.FolderName);
        Directory.CreateDirectory(dir);
        var fields = new List<DataField>();
        foreach (Column c in t.Columns)
        {
            Kind k = overrideKind?.Invoke(c) ?? c.Kind;
            fields.Add(k switch
            {
                Kind.Text => new DataField<string>(c.Name, true),
                Kind.Double => new DataField<double?>(c.Name),
                _ => new DataField<long>(c.Name),
            });
        }
        using Stream s = File.Create(Path.Combine(dir, t.FileStem + ".parquet"));
        await using ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(fields), s);
        using ParquetRowGroupWriter rg = w.CreateRowGroup();
        for (int i = 0; i < t.Columns.Length; i++)
        {
            Column c = t.Columns[i];
            Kind k = overrideKind?.Invoke(c) ?? c.Kind;
            switch (k)
            {
                case Kind.Text:
                    // pandas skipinitialspace: leading blanks go, trailing ones stay; an empty cell is null.
                    await rg.WriteAsync(fields[i], c.Cells.Select(x => x.Length == 0 ? null! : x.TrimStart(' ')).ToArray());
                    break;
                case Kind.Double:
                    await rg.WriteAsync(fields[i], new ReadOnlyMemory<double?>(c.Cells
                        .Select(x => x.Length == 0 ? (double?)null : double.Parse(x, CultureInfo.InvariantCulture)).ToArray()));
                    break;
                default:
                    await rg.WriteAsync(fields[i], new ReadOnlyMemory<long>(c.Cells
                        .Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray()));
                    break;
            }
        }
    }

    // ---- exact comparison --------------------------------------------------------------------------------------

    /// <summary>Every field of every record as text; doubles as their 64-bit patterns, so -0, NaN and last bits count.</summary>
    private static string Dump(LtdsDataset ds)
    {
        var sb = new StringBuilder();
        sb.Append(ds.Name).Append('|').Append(ds.Convention).Append('\n');
        void Rec(object r)
        {
            sb.Append(r.GetType().Name);
            foreach (PropertyInfo p in r.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.MetadataToken))
            {
                object? v = p.GetValue(r);
                sb.Append('|').Append(p.Name).Append('=');
                sb.Append(v is double d ? BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture)
                                        : Convert.ToString(v, CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
        foreach (var x in ds.Nodes) Rec(x);
        foreach (var x in ds.Circuits) Rec(x);
        foreach (var x in ds.Transformers2) Rec(x);
        foreach (var x in ds.Transformers3) Rec(x);
        foreach (var x in ds.Demand) Rec(x);
        foreach (var x in ds.Generation) Rec(x);
        return sb.ToString();
    }

    private string CsvRoot()
    {
        string root = NewRoot("csv");
        foreach (Table t in AllTables()) WriteCsv(root, t, headerOnly: false);
        return root;
    }

    // ---- tests -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Header_only_csvs_with_parquet_copies_build_exactly_what_the_full_csvs_build()
    {
        LtdsDataset fromCsv = UkpnSource.Load(CsvRoot());

        string root = NewRoot("stub-plus-parquet");
        foreach (Table t in AllTables())
        {
            WriteCsv(root, t, headerOnly: true);
            await WriteParquet(root, t);
        }
        var notes = new List<string>();
        LtdsDataset fromParquet = UkpnSource.Load(root, notes.Add);

        Assert.Equal(Dump(fromCsv), Dump(fromParquet));
        // And the fixture really exercised the rules (not an empty network compared with an empty network).
        Assert.Equal(3, fromParquet.Circuits.Count);
        Assert.Single(fromParquet.Transformers2);
        Assert.Equal(2, fromParquet.Demand.Count);
        Assert.Equal(2, fromParquet.Generation.Count);
        Assert.Equal(1200.0 * 33 * Math.Sqrt(3) / 1000.0, fromParquet.Circuits[2].WinterRatingMva);   // "1,200" A
        Assert.Equal(0.0, fromParquet.Circuits[2].SummerRatingMva);                                   // "Not Available"
        Assert.Equal("N3", fromParquet.Circuits[2].Node2);                                            // "N3 " trimmed
        Assert.Equal(1.0, fromParquet.Demand.Single(d => d.PrimarySubstation == "Sub C 33kV").PowerFactor); // empty PF -> 1.0
        // One note per table read from Parquet, none about long numbers (every number here has <= 15 digits).
        Assert.Equal(4, notes.Count);
        Assert.All(notes, n => Assert.Contains("has no data rows (header only); read ", n, StringComparison.Ordinal));
        Assert.DoesNotContain(notes, n => n.Contains("significant digits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_csv_with_rows_is_read_and_its_parquet_copy_ignored()
    {
        string csvOnly = CsvRoot();
        LtdsDataset expected = UkpnSource.Load(csvOnly);

        // The same full CSVs plus Parquet copies that disagree (different demand): the CSV must win, silently.
        string root = NewRoot("csv-plus-parquet");
        foreach (Table t in AllTables()) WriteCsv(root, t, headerOnly: false);
        Table changed = Demand() with
        {
            Columns = Demand().Columns.Select(c => c.Name == "Maximum_Demand_24_25_MW" ? c with { Cells = new[] { "99", "99", "99" } } : c).ToArray(),
        };
        foreach (Table t in new[] { Circuits(), Transformers(), changed, Generation() }) await WriteParquet(root, t);

        var notes = new List<string>();
        LtdsDataset got = UkpnSource.Load(root, notes.Add);
        Assert.Equal(Dump(expected), Dump(got));
        Assert.Empty(notes);
    }

    [Fact]
    public async Task Tables_choose_independently_csv_where_it_has_rows_parquet_where_it_is_a_stub()
    {
        LtdsDataset expected = UkpnSource.Load(CsvRoot());
        string root = NewRoot("mixed");
        WriteCsv(root, Circuits(), headerOnly: false);
        WriteCsv(root, Transformers(), headerOnly: true);
        await WriteParquet(root, Transformers());
        WriteCsv(root, Demand(), headerOnly: true);
        await WriteParquet(root, Demand());
        WriteCsv(root, Generation(), headerOnly: false);

        var notes = new List<string>();
        Assert.Equal(Dump(expected), Dump(UkpnSource.Load(root, notes.Add)));
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Contains("ukpn-ltds-table-2a-transformer-2w.csv", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("ukpn-ltds-table-3a-load-data-observed.csv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lake_with_only_the_parquet_folder_builds_the_same()
    {
        LtdsDataset expected = UkpnSource.Load(CsvRoot());
        string root = NewRoot("parquet-only");
        foreach (Table t in AllTables()) await WriteParquet(root, t);
        var notes = new List<string>();
        Assert.Equal(Dump(expected), Dump(UkpnSource.Load(root, notes.Add)));
        Assert.Equal(4, notes.Count);
        Assert.All(notes, n => Assert.Contains("read ", n, StringComparison.Ordinal));
    }

    [Fact]
    public void Neither_csv_rows_nor_a_parquet_copy_is_the_loud_no_demand_refusal_naming_both_places()
    {
        string root = NewRoot("neither");
        foreach (Table t in AllTables()) WriteCsv(root, t, headerOnly: true);
        Directory.CreateDirectory(Path.Combine(root, "UkPowerNetworks", "Parquet"));   // folder present, no copies

        var ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(root));
        Assert.StartsWith("UKPN ukpn-ltds-table-3a-load-data-observed.csv: column Maximum_Demand_24_25_MW gave no positive demand",
            ex.Message, StringComparison.Ordinal);
        Assert.Contains("(data rows read: circuits 0, transformers 0, demand 0)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no Parquet copy with rows was found for them under", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(root, "UkPowerNetworks", "Parquet"), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_parquet_copy_does_not_count_as_rows()
    {
        string root = NewRoot("empty-parquet");
        foreach (Table t in AllTables()) WriteCsv(root, t, headerOnly: true);
        // A Parquet demand copy with the columns and zero rows.
        Table empty = Demand() with { Columns = Demand().Columns.Select(c => c with { Cells = Array.Empty<string>() }).ToArray() };
        await WriteParquet(root, empty);
        var ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(root));
        Assert.Contains("(data rows read: circuits 0, transformers 0, demand 0)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_csv_and_no_parquet_copy_of_a_required_table_is_the_old_file_not_found_refusal()
    {
        string root = NewRoot("missing");
        WriteCsv(root, Circuits(), headerOnly: false);
        WriteCsv(root, Transformers(), headerOnly: false);
        Directory.CreateDirectory(Path.Combine(root, "UkPowerNetworks", "Parquet"));
        var ex = Assert.Throws<FileNotFoundException>(() => UkpnSource.Load(root));
        Assert.StartsWith("UKPN file not found under ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ltds-table-3a-load-data-observed.csv (or a 'ukpn-'-prefixed variant)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no Parquet copy with rows (ltds-table-3a-load-data-observed.parquet)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_folder_is_the_old_directory_refusal_word_for_word()
    {
        string root = NewRoot("nothing");
        var ex = Assert.Throws<DirectoryNotFoundException>(() => UkpnSource.Load(root));
        Assert.Equal($"UKPN data not found: {Path.Combine(root, "UkPowerNetworks", "RAWData")}", ex.Message);
    }

    [Fact]
    public async Task A_text_column_the_converter_made_numeric_is_refused_not_guessed()
    {
        // If every LineName were numeric pandas would have typed the column, and "007" could not be recovered.
        string root = NewRoot("numeric-text");
        Table circuits = Circuits() with
        {
            Columns = Circuits().Columns.Select(c => c.Name == "LineName" ? c with { Cells = new[] { "7", "8", "9" } } : c).ToArray(),
        };
        foreach (Table t in new[] { circuits, Transformers(), Demand(), Generation() })
        {
            WriteCsv(root, t, headerOnly: true);
            await WriteParquet(root, t, c => c.Name == "LineName" ? Kind.Double : c.Kind);
        }
        var ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(root));
        Assert.Contains("ukpn-ltds-table-1-circuit-data.parquet: row 1, column 'LineName' holds a Double", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read as the CSV", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Numbers_with_more_than_15_significant_digits_are_reported_in_a_note()
    {
        // 189.4526315789469 is one of the 8 generation capacities on the October 2026 lake with 16 or 17 digits.
        string root = NewRoot("long-digits");
        foreach (Table t in new[] { Circuits(), Transformers(), Demand(), Generation("189.4526315789469") })
        {
            WriteCsv(root, t, headerOnly: true);
            await WriteParquet(root, t);
        }
        var notes = new List<string>();
        LtdsDataset ds = UkpnSource.Load(root, notes.Add);
        Assert.Equal(189.4526315789469, ds.Generation[0].ExportCapacityMw);
        string note = Assert.Single(notes, n => n.Contains("significant digits", StringComparison.Ordinal));
        Assert.StartsWith("UKPN: 1 number(s) read from Parquet have more than 15 significant digits (InstalledCapacity_MVA 1).",
            note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(132.0, 3)]
    [InlineData(0.000123, 3)]
    [InlineData(-4.54, 3)]
    [InlineData(210.526315789473, 15)]
    [InlineData(189.4526315789469, 16)]
    [InlineData(52.510000000000005, 17)]
    [InlineData(1e-300, 1)]
    [InlineData(1.2345e21, 5)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void Significant_digits_of_the_shortest_exact_text(double d, int expected) =>
        Assert.Equal(expected, UkpnSource.SignificantDigits(d));
}
