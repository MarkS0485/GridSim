using System.Text;
using GridSim.Core.IO.Ucte;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The UCTE-DEF writer keeps every branch status code on a round trip. UCTE-DEF version 02 (2007-05-01) gives a line
/// one of six status codes and a transformer one of four: 0 real element in operation, 8 real element out of operation,
/// 1 equivalent element in operation, 9 equivalent element out of operation, and, for lines only, 2 busbar coupler in
/// operation and 7 busbar coupler out of operation (the reader's documented code sets, <see cref="UcteParser"/>). The
/// reader skips 7, 8 and 9, and reads 1 as 0 except that it accepts a negative R or X for an equivalent element.
/// <para>Before, the writer wrote 0 for a status-1 element, so a status-1 element with a negative R or X was refused
/// when the written file was read back; it wrote 8 for a busbar coupler out of service; and the out-of-operation records
/// of the source were lost.</para>
/// <para>Oracles. The expected status of every record follows from the code table above and the file order (each
/// skipped record written back just before the branch it preceded in the source). The expected per-unit values of the
/// negative-impedance elements are derived outside GridSim (Python, IEEE doubles): Zbase = 380^2 / 100 = 1444 ohm, so
/// -0.5 ohm is -0.00034626038781163435 pu, -2 ohm -0.0013850415512465374, -0.1 ohm -6.925207756232688e-05 and -1 ohm
/// -0.0006925207756232687; written back in six columns they are "-0.500", "-2.000", "-0.100" and "-1.000", which read
/// back to the same doubles. Read-backs are compared with the first read field by field, to the bit.</para>
/// </summary>
public class UcteStatusRoundTripTests
{
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    // The records, in file order. Columns: nodes 1-8 and 10-17, order 19, status 21, R 23-28, X 30-35, B 37-44,
    // current limit 46-51, name from 53 (lines); U1 23-27, U2 29-33, S 35-39, R 41-46, X 48-53, B 55-62, G 64-69,
    // current limit 71-76, name from 78 (transformers). Checked by slicing in Python before use.
    private const string L1 = "FAAAAA11 FBBBBB11 1 0 1.4440 14.440   120.00   2000 LINE-AB";
    private const string L2 = "FAAAAA11 FBBBBB11 2 8 1.5000 15.000   125.00   2000 LINE-AB-2";
    private const string L3 = "FAAAAA11 FBBBBB11 3 1 -0.500 -2.000   0.0000        EQUIV-AB";
    private const string L4 = "FAAAAA11 FBBBBB11 4 9 -0.600 -3.000   0.0000        EQUIV-AB-2";
    private const string L5 = "FCCCCC21 FCCCCC22 1 2 0.0000 0.0000   0.0000";
    private const string L6 = "FCCCCC21 FCCCCC22 2 7 0.0000 0.0000   0.0000";
    // A real line beside the coupler, so either can be switched out without leaving CHARLIE B an island with no slack
    // (the reader refuses one).
    private const string L7 = "FCCCCC21 FCCCCC22 3 0 0.4840 4.8400   10.000";
    private const string T1 = "FBBBBB11 FCCCCC21 1 0 380.0 220.0 600.0 0.7220 36.100   0.0000 0.0000    900 TR-BC";
    private const string T2 = "FBBBBB11 FCCCCC21 2 8 380.0 220.0 600.0 0.7220 36.100   0.0000 0.0000    900 TR-BC-2";
    private const string T3 = "FBBBBB11 FCCCCC21 3 1 380.0 220.0 600.0 -0.100 -1.000   0.0000 0.0000    900 TR-EQ";
    private const string T4 = "FBBBBB11 FCCCCC21 4 9 380.0 220.0 600.0 -0.200 -2.000   0.0000 0.0000    900 TR-EQ-2";
    private const string R2 = "FBBBBB11 FCCCCC21 2 1.250 16   2";   // the regulation of T2, out of operation

    /// <summary>small.uct's four ordinary nodes, with every line and transformer status code.</summary>
    private static string Case()
    {
        var sb = new StringBuilder();
        sb.Append("##C 2007.05.01\n");
        sb.Append("GridSim status round-trip test network, written by hand to the UCTE-DEF version 02 columns.\n");
        sb.Append("##N\n##ZFR\n");
        sb.Append("FAAAAA11 ALPHA        0 3 400.00 0.00000 0.00000 -500.00 0.00000 0.00000 -1000.0 500.000 -500.00\n");
        sb.Append("FBBBBB11 BRAVO        0 0        300.000 100.000\n");
        sb.Append("FCCCCC21 CHARLIE      0 2 225.00 150.000 50.0000 -100.00 0.00000 -10.000 -400.00 200.000 -200.00\n");
        sb.Append("FCCCCC22 CHARLIE B    0 0        20.0000 5.00000\n");
        sb.Append("##L\n");
        foreach (string l in new[] { L1, L2, L3, L4, L5, L6, L7 }) sb.Append(l).Append('\n');
        sb.Append("##T\n");
        foreach (string t in new[] { T1, T2, T3, T4 }) sb.Append(t).Append('\n');
        sb.Append("##R\n").Append(R2).Append('\n');
        return sb.ToString();
    }

    /// <summary>The records of one block of written text, in order.</summary>
    private static List<string> Block(string text, string block)
    {
        string[] lines = text.Split('\n');
        int at = Array.IndexOf(lines, block);
        Assert.True(at >= 0, $"no {block} block");
        return lines.Skip(at + 1).TakeWhile(l => !l.StartsWith("##", StringComparison.Ordinal) && l.Length > 0).ToList();
    }

    private static string StatusOf(string record) => record.Substring(20, 1);   // column 21

    private static void SameBranches(IReadOnlyList<Branch> expected, IReadOnlyList<Branch> actual)
    {
        Assert.Equal(expected, actual);
        for (int k = 0; k < expected.Count; k++)
            Assert.True(Same(expected[k].R, actual[k].R) && Same(expected[k].X, actual[k].X) && Same(expected[k].B, actual[k].B)
                && Same(expected[k].TapRatio, actual[k].TapRatio) && Same(expected[k].PhaseShiftDeg, actual[k].PhaseShiftDeg),
                $"branch {k + 1}");
    }

    // ---- The read -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_reader_records_each_branch_status_and_each_skipped_record()
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        // In operation, in file order: L1 (0), L3 (1), the coupler L5 (2), L7 (0), T1 (0), T3 (1).
        Assert.Equal(new[] { 0, 1, 2, 0, 0, 1 }, r.BranchStatus);
        Assert.Equal(new[] { "line", "line", "coupler", "line", "transformer", "transformer" }, r.BranchKeys.Select(k => k.Kind));
        // Out of operation, in file order, each with the number of model branches read before it.
        Assert.Equal(new[]
        {
            new UcteSkippedRecord("##L", L2, 1, null),
            new UcteSkippedRecord("##L", L4, 2, null),
            new UcteSkippedRecord("##L", L6, 3, null),
            new UcteSkippedRecord("##T", T2, 5, R2),
            new UcteSkippedRecord("##T", T4, 6, null),
        }, r.Skipped);
        // The equivalent elements' negative impedances (derived values, see the class summary).
        Assert.True(Same(-0.00034626038781163435, r.Model.Branches[1].R) && Same(-0.0013850415512465374, r.Model.Branches[1].X), "L3");
        Assert.True(Same(-6.925207756232688e-05, r.Model.Branches[5].R) && Same(-0.0006925207756232687, r.Model.Branches[5].X), "T3");
    }

    // ---- Round trip: every status code kept -----------------------------------------------------------------------------

    [Fact]
    public void Every_line_status_code_is_written_back_in_place()
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        UcteWriteResult w = UcteWriter.Write(r.Model, new UcteWriteOptions { Source = r });
        List<string> lines = Block(w.Text, "##L");
        Assert.Equal(new[] { "0", "8", "1", "9", "2", "7", "0" }, lines.Select(StatusOf));
        // The skipped records are the file's own text.
        Assert.Equal(L2, lines[1]);
        Assert.Equal(L4, lines[3]);
        Assert.Equal(L6, lines[5]);
        // The kept records name the same element.
        Assert.StartsWith("FAAAAA11 FBBBBB11 1 ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("FAAAAA11 FBBBBB11 3 ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("FCCCCC21 FCCCCC22 1 ", lines[4], StringComparison.Ordinal);
        Assert.StartsWith("FCCCCC21 FCCCCC22 3 ", lines[6], StringComparison.Ordinal);
        // The status-1 line's negative impedance in its columns (R 23-28, X 30-35).
        Assert.Equal("-0.500", lines[2].Substring(22, 6));
        Assert.Equal("-2.000", lines[2].Substring(29, 6));
    }

    [Fact]
    public void Every_transformer_status_code_is_written_back_in_place_with_its_regulation()
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        UcteWriteResult w = UcteWriter.Write(r.Model, new UcteWriteOptions { Source = r });
        List<string> xfmrs = Block(w.Text, "##T");
        Assert.Equal(new[] { "0", "8", "1", "9" }, xfmrs.Select(StatusOf));
        Assert.Equal(T2, xfmrs[1]);
        Assert.Equal(T4, xfmrs[3]);
        Assert.StartsWith("FBBBBB11 FCCCCC21 1 ", xfmrs[0], StringComparison.Ordinal);
        Assert.StartsWith("FBBBBB11 FCCCCC21 3 ", xfmrs[2], StringComparison.Ordinal);
        Assert.Equal("-0.100", xfmrs[2].Substring(40, 6));   // R, columns 41-46
        Assert.Equal("-1.000", xfmrs[2].Substring(47, 6));   // X, columns 48-53
        // The out-of-operation transformer keeps its regulation record; the others had none and get none.
        Assert.Equal(new[] { R2 }, Block(w.Text, "##R"));
    }

    [Fact]
    public void The_written_case_reads_back_to_the_same_model_statuses_and_skipped_records()
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        UcteWriteResult w = UcteWriter.Write(r.Model, new UcteWriteOptions { Source = r });
        // Before the fix the status-1 elements were written as 0 and this read refused them ("R and X of a real element
        // must not be negative").
        UcteParseResult back = UcteParser.ParseWithDiagnostics(w.Text, "t");
        Assert.Equal(r.Model.Buses, back.Model.Buses);
        Assert.Equal(r.Model.AllGenerators, back.Model.AllGenerators);
        SameBranches(r.Model.Branches, back.Model.Branches);
        Assert.Equal(r.BranchKeys, back.BranchKeys);
        Assert.Equal(r.BranchStatus, back.BranchStatus);
        Assert.Equal(r.Skipped, back.Skipped);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, w.BranchOrder);
    }

    [Fact]
    public void A_second_generation_write_is_the_same_text()
    {
        // Write, read, write again: the statuses, the skipped records and their places are stable.
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        UcteWriteResult w1 = UcteWriter.Write(r.Model, new UcteWriteOptions { Source = r });
        UcteParseResult r1 = UcteParser.ParseWithDiagnostics(w1.Text, "t");
        UcteWriteResult w2 = UcteWriter.Write(r1.Model, new UcteWriteOptions { Source = r1 });
        Assert.Equal(w1.Text, w2.Text);
    }

    // ---- A branch switched out after the read ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "##L", 0, "8")]   // L1: real line -> real element out of operation
    [InlineData(1, "##L", 2, "9")]   // L3: equivalent line -> equivalent element out of operation
    [InlineData(2, "##L", 4, "7")]   // L5: busbar coupler -> busbar coupler out of operation
    [InlineData(3, "##L", 6, "8")]   // L7: real line -> 8
    [InlineData(4, "##T", 0, "8")]   // T1: real transformer -> 8
    [InlineData(5, "##T", 2, "9")]   // T3: equivalent transformer -> 9
    public void A_branch_taken_out_of_service_is_written_with_its_own_out_of_operation_code(int k, string block, int row, string expected)
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        GridModel open = r.Model.CopyWith(branches: r.Model.Branches.Select((b, i) => i == k ? b with { InService = false } : b).ToArray());
        UcteWriteResult w = UcteWriter.Write(open, new UcteWriteOptions { Source = r });
        List<string> records = Block(w.Text, block);
        Assert.Equal(expected, StatusOf(records[row]));
        // Every other record keeps its code (row order as in the full round trip above).
        string[] all = block == "##L" ? new[] { "0", "8", "1", "9", "2", "7", "0" } : new[] { "0", "8", "1", "9" };
        all[row] = expected;
        Assert.Equal(all, records.Select(StatusOf));
        Assert.DoesNotContain(k, w.BranchOrder);

        // Read back: the branch is skipped, every other branch and status is as read.
        UcteParseResult back = UcteParser.ParseWithDiagnostics(w.Text, "t");
        SameBranches(r.Model.Branches.Where((_, i) => i != k).ToList(), back.Model.Branches);
        Assert.Equal(r.BranchStatus.Where((_, i) => i != k), back.BranchStatus);
        Assert.Equal(r.Skipped.Count + 1, back.Skipped.Count);
        Assert.Contains(back.Skipped, s => s.Block == block && s.Text.Length > 20 && StatusOf(s.Text) == expected && s.Text[..20] == records[row][..20]);
    }

    // ---- Without a source read ----------------------------------------------------------------------------------------

    [Fact]
    public void Without_a_source_read_a_branch_is_written_real_0_in_service_and_8_out()
    {
        // No source: the writer knows no status, so every element is real (unchanged behaviour): L1 in service, a copy
        // of it out of service.
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        Branch l1 = r.Model.Branches[0];
        GridModel m = r.Model.CopyWith(branches: new[] { l1, l1 with { InService = false } });
        UcteWriteResult w = UcteWriter.Write(m, new UcteWriteOptions { Country = "FR" });
        Assert.Equal(new[] { "0", "8" }, Block(w.Text, "##L").Select(StatusOf));
    }

    [Fact]
    public void Without_a_source_read_a_negative_impedance_branch_is_written_as_an_equivalent_element_and_reads_back()
    {
        // Coordinator ruling: with no source, a branch with a negative R or X is written with status 1 (9 out of
        // service), since UCTE-DEF lets only an equivalent element carry a negative impedance; the notes name it. Written
        // as 0 it would be refused on read-back ("R and X of a real element must not be negative").
        // Branches as read: L1, L3 (negative), the coupler L5 (a plain line without the source), L7, T1, T3 (negative).
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        UcteWriteResult w = UcteWriter.Write(r.Model, new UcteWriteOptions { Country = "FR" });
        Assert.Equal(new[] { "0", "1", "0", "0" }, Block(w.Text, "##L").Select(StatusOf));
        Assert.Equal(new[] { "0", "1" }, Block(w.Text, "##T").Select(StatusOf));
        // One note per such branch, naming it (model branch number, its from and to buses).
        Assert.Single(w.Notes, n => n.StartsWith("branch 2 (1-2, ", StringComparison.Ordinal) && n.Contains("equivalent element", StringComparison.Ordinal));
        Assert.Single(w.Notes, n => n.StartsWith("branch 6 (3-2, ", StringComparison.Ordinal) && n.Contains("equivalent element", StringComparison.Ordinal));
        Assert.Equal(2, w.Notes.Count(n => n.Contains("has a negative R or X", StringComparison.Ordinal)));

        // The file reads back, and the negative impedances come back to the bit (derived values, see the class summary).
        UcteParseResult back = UcteParser.ParseWithDiagnostics(w.Text, "t");
        Assert.Equal(new[] { 0, 1, 0, 0, 0, 1 }, back.BranchStatus);
        Assert.True(Same(-0.00034626038781163435, back.Model.Branches[1].R) && Same(-0.0013850415512465374, back.Model.Branches[1].X), "L3");
        Assert.True(Same(-6.925207756232688e-05, back.Model.Branches[5].R) && Same(-0.0006925207756232687, back.Model.Branches[5].X), "T3");
    }

    [Fact]
    public void Without_a_source_read_a_negative_impedance_branch_out_of_service_is_written_9()
    {
        UcteParseResult r = UcteParser.ParseWithDiagnostics(Case(), "t");
        GridModel open = r.Model.CopyWith(branches: r.Model.Branches.Select((b, i) => i == 1 ? b with { InService = false } : b).ToArray());
        UcteWriteResult w = UcteWriter.Write(open, new UcteWriteOptions { Country = "FR" });
        Assert.Equal(new[] { "0", "9", "0", "0" }, Block(w.Text, "##L").Select(StatusOf));
        Assert.Single(w.Notes, n => n.StartsWith("branch 2 (1-2, ", StringComparison.Ordinal));
        UcteParseResult back = UcteParser.ParseWithDiagnostics(w.Text, "t");
        Assert.Equal(new[] { 0, 0, 0, 0, 1 }, back.BranchStatus);   // L3 skipped; T3 still equivalent
        Assert.Single(back.Skipped);
    }
}
