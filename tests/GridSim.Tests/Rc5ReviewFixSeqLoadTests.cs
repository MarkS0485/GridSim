using GridSim.Core.IO.Psse;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 parity programme, review fix: the LOAD section of a revision 33 or later SEQ file. Its field layout (PNEG and QNEG in
/// MW and Mvar, then a grounding flag before the zero-sequence fields) is not confirmed, so a non-empty section is refused
/// with an error that names it; an empty section is read as before.
/// </summary>
public class Rc5ReviewFixSeqLoadTests
{
    private static string Seq(int revision, string loadRecords) =>
        $"0, {revision} / TEST\n" +
        "@!I,'ID',ZRPOS,ZXPPDV,ZXPDV,ZXSDV,ZRNEG,ZXNEGDV,ZR0,ZX0DV,CZG,ZRG,ZXG,REFDEG\n" +
        "1,'1',0.0,0.18,0.25,1.1,0.0,0.2,0.0,0.08,1,0,0,0\n" +
        "0 / END OF GENERATOR DATA, BEGIN LOAD DATA\n" + loadRecords +
        "0 / END OF LOAD DATA, BEGIN ZERO SEQ. NON-TRANSFORMER BRANCH DATA\n" +
        "0 / END OF ZERO SEQ. NON-TRANSFORMER BRANCH DATA, BEGIN ZERO SEQ. MUTUAL DATA\n" +
        "0 / END OF ZERO SEQ. MUTUAL DATA, BEGIN ZERO SEQ. TRANSFORMER DATA\n" +
        "0 / END OF ZERO SEQ. TRANSFORMER DATA, BEGIN ZERO SEQ. SWITCHED SHUNT DATA\n" +
        "0 / END OF ZERO SEQ. SWITCHED SHUNT DATA, BEGIN ZERO SEQ. FIXED SHUNT DATA\n" +
        "0 / END OF ZERO SEQ. FIXED SHUNT DATA, BEGIN INDUCTION MACHINE DATA\n" +
        "0 / END OF INDUCTION MACHINE DATA\nQ\n";

    private static PsseParseResult Raw() => PsseRawParser.ParseWithDiagnostics(Rc5Wp5PsseRev36Tests.Raw35(), "case");

    [Fact]
    public void A_non_empty_load_section_of_revision_33_is_refused_and_named()
    {
        Exception e = Assert.ThrowsAny<Exception>(() => PsseSeqReader.Apply(Seq(33, "2,'1',0.1,0.05,0.0,0.0,0.0\n"), Raw()));
        Assert.Contains("LOAD", e.Message, StringComparison.Ordinal);
        Assert.Contains("not read", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_load_section_is_read_as_before()
    {
        PsseSeqResult r = PsseSeqReader.Apply(Seq(33, ""), Raw());
        Assert.Equal(0.18, r.Model.AllGenerators.First().SubtransientXdPu);
    }
}
