using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 parity programme, WP5: PSS/E SEQ sequence data in the layouts of revisions 33 to 36.
/// <para>The layout is the one PSS/E writes from revision 33: one GENERATOR section (I, ID, ZRPOS, ZXPPDV, ZXPDV, ZXSDV, ZRNEG,
/// ZXNEGDV, ZR0, ZX0DV, CZG, ZRG, ZXG, REFDEG), a LOAD section, the zero-sequence non-transformer branch, mutual, transformer
/// (I, J, K, ICKT, CZ0, CZG, CC, RG1, XG1, R01, X01, RG2, XG2, R02, X02, RNUTRL, XNUTRL), switched shunt and fixed shunt
/// sections and the induction machine section, with the system-wide section first and the two non-conventional source
/// sections last from revision 35; the transformer connection codes are 1 to 4 in revisions 33 and 34 and 11 to 14 from
/// revision 35. The fixtures are written here from that description, not copied from a vendor file. Every expected value is
/// the number written into the fixture; the equality tests check that the same data in the revision 33 and the revision 35
/// layout gives the same model, bit for bit.</para>
/// </summary>
public class Rc5Wp5PsseSeqLayoutTests
{
    private static PsseParseResult Raw() => PsseRawParser.ParseWithDiagnostics(Rc5Wp5PsseRev36Tests.Raw35(), "case");

    private const string Gen35 =
        "@!I,'ID',ZRPOS,ZXPPDV,ZXPDV,ZXSDV,ZRNEG,ZXNEGDV,ZR0,ZX0DV,CZG,ZRG,ZXG,REFDEG\n" +
        "1,'1',0.0,0.18,0.25,1.1,0.0,0.2,0.0,0.08,1,0,0,0\n" +
        "2,'2',0.0,0.3,0.4,1.2,0.0,0.32,0.001,0.1,1,0,0,0\n";

    private const string Line =
        "@!I,J,'ICKT',RLINZ,XLINZ,BCHZ,GI,BI,GJ,BJ,IPR,SCTYP\n" +
        "1,2,'1',0.03,0.15,0,0,0,0,0,0,0\n" +
        "1,2,'2',0.036,0.18\n";

    private static string Transformer(int cc, string tail = "0,0") =>
        "@!I,J,K,'ICKT',CZ0,CZG,CC,RG1,XG1,R01,X01,RG2,XG2,R02,X02,RNUTRL,XNUTRL\n" +
        $"2,3,0,'1',1,1,{cc},0,0,0.002,0.12,0,0,0,0,{tail}\n";

    private static string Seq35(string gen = Gen35, string xfmr = "", string ncs = "") =>
        "@!IC, REV\n0, 35 / TEST\nRPTFORMAT, AMPOUT=0\nMOV, ITERATIONS=20\n" +
        "0 / END OF SYSTEM-WIDE DATA, BEGIN GENERATOR DATA\n" + gen +
        "0 / END OF GENERATOR DATA, BEGIN LOAD DATA\n" +
        "0 / END OF LOAD DATA, BEGIN ZERO SEQ. NON-TRANSFORMER BRANCH DATA\n" + Line +
        "0 / END OF ZERO SEQ. NON-TRANSFORMER BRANCH DATA, BEGIN ZERO SEQ. MUTUAL DATA\n" +
        "0 / END OF ZERO SEQ. MUTUAL DATA, BEGIN ZERO SEQ. TRANSFORMER DATA\n" + (xfmr.Length > 0 ? xfmr : Transformer(12)) +
        "0 / END OF ZERO SEQ. TRANSFORMER DATA, BEGIN ZERO SEQ. SWITCHED SHUNT DATA\n" +
        "0 / END OF ZERO SEQ. SWITCHED SHUNT DATA, BEGIN ZERO SEQ. FIXED SHUNT DATA\n" +
        "0 / END OF ZERO SEQ. FIXED SHUNT DATA, BEGIN INDUCTION MACHINE DATA\n" +
        "0 / END OF INDUCTION MACHINE DATA, BEGIN NON-CONVENTIONAL SOURCE TABLE DATA\n" +
        "0 / END OF NON-CONVENTIONAL SOURCE TABLE DATA, BEGIN NON-CONVENTIONAL SOURCE MACHINE DATA\n" + ncs +
        "0 / END OF NON-CONVENTIONAL SOURCE MACHINE DATA\nQ\n";

    private static string Seq33(string xfmr = "") =>
        "0, 33 / TEST\n" +
        Gen35 +
        "0 / END OF GENERATOR DATA, BEGIN LOAD DATA\n" +
        "0 / END OF LOAD DATA, BEGIN ZERO SEQ. NON-TRANSFORMER BRANCH DATA\n" + Line +
        "0 / END OF ZERO SEQ. NON-TRANSFORMER BRANCH DATA, BEGIN ZERO SEQ. MUTUAL DATA\n" +
        "0 / END OF ZERO SEQ. MUTUAL DATA, BEGIN ZERO SEQ. TRANSFORMER DATA\n" + (xfmr.Length > 0 ? xfmr : Transformer(2)) +
        "0 / END OF ZERO SEQ. TRANSFORMER DATA, BEGIN ZERO SEQ. SWITCHED SHUNT DATA\n" +
        "0 / END OF ZERO SEQ. SWITCHED SHUNT DATA, BEGIN ZERO SEQ. FIXED SHUNT DATA\n" +
        "0 / END OF ZERO SEQ. FIXED SHUNT DATA, BEGIN INDUCTION MACHINE DATA\n" +
        "0 / END OF INDUCTION MACHINE DATA\nQ\n";

    [Fact]
    public void A_revision_35_sequence_file_sets_machine_line_and_transformer_zero_sequence_data()
    {
        PsseSeqResult r = PsseSeqReader.Apply(Seq35(), Raw());
        Generator[] g = r.Model.AllGenerators.ToArray();
        Assert.Equal((0.18, 0.2), (g[0].SubtransientXdPu, g[0].NegativeSeqXPu));
        Assert.Equal((0.3, 0.32), (g[1].SubtransientXdPu, g[1].NegativeSeqXPu));
        Assert.Equal(new MachineZeroSequence(0.0, 0.08), g[0].ZeroSequenceZPu);
        Assert.Equal(new MachineZeroSequence(0.001, 0.1), g[1].ZeroSequenceZPu);
        Branch c1 = r.Model.Branches.Single(b => b.From == 1 && b.To == 2 && b.R0 == 0.03);
        Assert.Equal(0.15, c1.X0);
        Branch c2 = r.Model.Branches.Single(b => b.From == 1 && b.To == 2 && b.R0 == 0.036);
        Assert.Equal(0.18, c2.X0);
        // Connection code 12: grounded wye on winding 1 (the from end), delta on winding 2; the path is R01 + jX01.
        Branch t = r.Model.Branches.Single(b => b.From == 2 && b.To == 3);
        Assert.Equal((0.002, 0.12, WindingConnection.WyeGrounded, WindingConnection.Delta), (t.R0, t.X0, t.FromWinding, t.ToWinding));
        Assert.Equal((2, 2, 2, 1), (r.PositiveSequenceMachines, r.NegativeSequenceMachines, r.ZeroSequenceLines, r.ZeroSequenceTransformers));
    }

    [Fact]
    public void The_revision_33_and_revision_35_layouts_of_the_same_data_give_the_same_model_bit_for_bit()
    {
        PsseSeqResult a = PsseSeqReader.Apply(Seq35(), Raw());
        PsseSeqResult b = PsseSeqReader.Apply(Seq33(), Raw());
        Assert.Equal(a.Model.AllGenerators, b.Model.AllGenerators);
        Assert.Equal(a.Model.Branches, b.Model.Branches);
    }

    [Fact]
    public void Revision_36_takes_the_revision_35_layout()
    {
        PsseSeqResult a = PsseSeqReader.Apply(Seq35(), Raw());
        PsseSeqResult b = PsseSeqReader.Apply(Seq35().Replace("0, 35 / TEST", "0, 36 / TEST", StringComparison.Ordinal), Raw());
        Assert.Equal(a.Model.AllGenerators, b.Model.AllGenerators);
        Assert.Equal(a.Model.Branches, b.Model.Branches);
    }

    [Fact]
    public void A_revision_36_raw_case_takes_a_revision_35_sequence_file()
    {
        PsseParseResult raw36 = PsseRawParser.ParseWithDiagnostics(Rc5Wp5PsseRev36Tests.Raw36(), "case");
        Assert.Equal(36, raw36.Revision);
        PsseSeqResult r = PsseSeqReader.Apply(Seq35(), raw36);
        Assert.Equal((2, 2), (r.PositiveSequenceMachines, r.NegativeSequenceMachines));
    }

    [Fact]
    public void Grounding_on_winding_1_with_connection_code_12_is_folded_as_three_times_rg_plus_jxg()
    {
        string x = "@!I,J,K,'ICKT',CZ0,CZG,CC,RG1,XG1,R01,X01,RG2,XG2,R02,X02,RNUTRL,XNUTRL\n2,3,0,'1',1,1,12,0.5,1.5,0.002,0.12,0,0,0,0,0,0\n";
        PsseSeqResult r = PsseSeqReader.Apply(Seq35(xfmr: x), Raw());
        Branch t = r.Model.Branches.Single(b => b.From == 2 && b.To == 3);
        Assert.Equal(0.002 + 3.0 * 0.5, t.R0, 12);
        Assert.Equal(0.12 + 3.0 * 1.5, t.X0, 12);
    }

    [Fact]
    public void Connection_code_13_grounds_winding_2_and_uses_its_grounding_impedance()
    {
        string x = "@!I,J,K,'ICKT',CZ0,CZG,CC,RG1,XG1,R01,X01,RG2,XG2,R02,X02,RNUTRL,XNUTRL\n2,3,0,'1',1,1,13,9,9,0.002,0.12,0.5,1.5,0,0,0,0\n";
        PsseSeqResult r = PsseSeqReader.Apply(Seq35(xfmr: x), Raw());
        Branch t = r.Model.Branches.Single(b => b.From == 2 && b.To == 3);
        Assert.Equal((WindingConnection.Delta, WindingConnection.WyeGrounded), (t.FromWinding!.Value, t.ToWinding!.Value));
        Assert.Equal(0.002 + 3.0 * 0.5, t.R0, 12);
    }

    [Theory]
    [InlineData("2,3,0,'1',2,1,12,0,0,0.002,0.12,0,0,0,0,0,0", "CZ0")]
    [InlineData("2,3,0,'1',1,3,12,0,0,0.002,0.12,0,0,0,0,0,0", "CZG")]
    [InlineData("2,3,0,'1',1,1,7,0,0,0.002,0.12,0,0,0,0,0,0", "connection code")]
    public void A_code_the_reader_cannot_place_is_refused_not_guessed_at(string record, string mentions)
    {
        string x = "@!I,J,K,'ICKT',CZ0,CZG,CC,RG1,XG1,R01,X01,RG2,XG2,R02,X02,RNUTRL,XNUTRL\n" + record + "\n";
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(Seq35(xfmr: x), Raw()));
        Assert.Contains(mentions, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_data_with_no_element_in_the_model_is_refused_unless_allowed()
    {
        string gen = Gen35.Replace("2,'2',0.0,0.3,0.4,1.2,0.0,0.32,0.001,0.1,1,0,0,0", "2,'2',0.0,0.3,0.4,1.2,0.0,0.32,0.001,0.1,1,0.05,0.2,0", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(Seq35(gen), Raw()));
        Assert.Contains("generator grounding impedance", ex.Message, StringComparison.Ordinal);
        PsseSeqResult ok = PsseSeqReader.Apply(Seq35(gen), Raw(), new PsseSeqOptions { AllowUnmodelled = true });
        Assert.Contains(ok.Notes, n => n.Contains("generator grounding impedance", StringComparison.Ordinal) && n.Contains("NOT modelled", StringComparison.Ordinal));
        var ex2 = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(Seq35(ncs: "'T1',1,2,3\n"), Raw()));
        Assert.Contains("non-conventional source", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_revision_the_reader_does_not_know_and_a_mismatch_with_the_raw_case_are_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(Seq35().Replace("0, 35 / TEST", "0, 37 / TEST", StringComparison.Ordinal), Raw()));
        Assert.Contains("revision 37", ex.Message, StringComparison.Ordinal);
        var ex2 = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(Seq35().Replace("0, 35 / TEST", "0 / TEST", StringComparison.Ordinal), Raw()));
        Assert.Contains("does not say its revision", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_stops_after_a_section_is_read_with_a_note_and_a_record_after_the_last_is_refused()
    {
        string cut = "0, 35 / TEST\n" + "0 / END OF SYSTEM-WIDE DATA\n" + Gen35 + "Q\n";
        PsseSeqResult r = PsseSeqReader.Apply(cut, Raw());
        Assert.Equal(2, r.PositiveSequenceMachines);
        var ex = Assert.Throws<InvalidDataException>(() => PsseSeqReader.Apply(Seq35().Replace("Q\n", "", StringComparison.Ordinal) + "7,7\n", Raw()));
        Assert.Contains("a record follows the last section", ex.Message, StringComparison.Ordinal);
    }
}
