using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The PSS/E RAW writer's devices (export pack): a model read from a RAW file carrying a two-terminal, VSC or
/// multi-terminal DC link, a FACTS device, a static var compensator, an induction machine or a transformer flow control is
/// written and read back (tap and shunt control applied, as <c>gridsim convert</c> reads it) to the same device records,
/// and its power flow gives exactly the bits of the input put in the file's order (<see cref="PsseRawWriter.InFileOrder"/>),
/// in every writable revision.
/// </summary>
public class PsseRawDeviceWriterTests
{
    private static readonly PsseReadOptions Controls = new() { ApplyTapControl = true, ApplyShuntControl = true };

    private const string PhaseShifter = "1, 0, 0, 200, 200, 200, 3, 0, 30, -30, 150, 140, 61, 0, 0, 0, 0";

    public static TheoryData<string, int> Cases()
    {
        var d = new TheoryData<string, int>();
        foreach (string c in new[] { "lcc", "vsc", "mtdc", "facts", "svc", "induction", "flow" })
            foreach (int rev in new[] { 33, 34, 35 }) d.Add(c, rev);
        return d;
    }

    private static string Raw(string name) => name switch
    {
        "lcc" => PsseLccTests.Case(),
        "vsc" => PsseVscTests.Case(),
        "mtdc" => PsseMtdcTests.Case(),
        "facts" => PsseFactsTests.Case(),
        "svc" => PsseShuntControlTests.TwoBus("2, 0, 1, 1.03, 0.97, 0, 100, '', 0, 4, 10, 2, 20"),
        "induction" => PsseInductionTests.Case(),
        "flow" => PsseFlowControlTests.Parallel(PhaseShifter),
        _ => throw new ArgumentException(name),
    };

    private static void SameBits(double[] a, double[] b, string what)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]), $"{what} [{i}]: {a[i]:R} vs {b[i]:R}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_device_case_round_trips_through_the_writer_to_the_same_records_and_bits(string name, int revision)
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(Raw(name), name, Controls with { FrequencyHz = 50 }).Model;
        PsseWriteResult w = PsseRawWriter.Write(m, revision);
        GridModel back = PsseRawParser.ParseWithDiagnostics(w.Text, name, Controls).Model;
        GridModel expected = PsseRawWriter.InFileOrder(m, w);

        Assert.Equal(expected.LccLinks, back.LccLinks);
        Assert.Equal(expected.VscLinks, back.VscLinks);
        Assert.Equal(expected.MtdcLinks.Count, back.MtdcLinks.Count);
        for (int k = 0; k < expected.MtdcLinks.Count; k++)
        {
            Assert.Equal(expected.MtdcLinks[k].Converters, back.MtdcLinks[k].Converters);
            Assert.Equal(expected.MtdcLinks[k].DcBuses, back.MtdcLinks[k].DcBuses);
            Assert.Equal(expected.MtdcLinks[k].Lines, back.MtdcLinks[k].Lines);
        }
        Assert.Equal(expected.FactsDevices, back.FactsDevices);
        // Names are not part of the power flow; revision 35 reads a switched shunt back as "SVC <bus> <id>".
        Assert.Equal(expected.StaticVarCompensators.Select(c => c with { Name = "" }), back.StaticVarCompensators.Select(c => c with { Name = "" }));
        Assert.Equal(expected.InductionMachines, back.InductionMachines);
        Assert.Equal(expected.TransformerFlowControls, back.TransformerFlowControls);
        Assert.Equal(expected.Branches, back.Branches);
        Assert.Equal(expected.Buses, back.Buses);

        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(expected), b = new NewtonRaphsonPowerFlow().Solve(back);
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Iterations, b.Iterations);
        SameBits(a.Vm, b.Vm, "Vm");
        SameBits(a.VaDeg, b.VaDeg, "Va");
    }

    [Fact]
    public void A_tap_changer_whose_band_raw_cannot_give_back_exactly_is_listed_and_left_at_its_tap()
    {
        // A two-winding case with an OLTC of band 1.0 +/- 0.01 and step 0.0125 on 0.9..1.1 (gb-full's tap changers):
        // (VMA - VMI) / 2 cannot be 0.01 to the bit for any doubles VMA, VMI, so it is not written and the notes say why.
        GridModel m = PsseRawParser.ParseWithDiagnostics(PsseFlowControlTests.Parallel(PhaseShifter), "flow", Controls with { FrequencyHz = 50 }).Model;
        string t = m.TransformerFlowControls[0].Transformer;
        GridModel withOltc = m.CopyWith(transformerFlowControls: Array.Empty<TransformerFlowControl>(),
            oltcs: new[] { new Oltc(t, m.Buses[^1].Id, 1.0, 0.9, 1.1, 0.0125, 0.01) });
        PsseWriteResult w = PsseRawWriter.Write(withOltc);
        Assert.Contains(w.Notes, n => n.StartsWith("1 tap changer(s) or flow control(s) NOT written", StringComparison.Ordinal) && n.Contains("VMA and VMI"));
        Assert.Empty(PsseRawParser.ParseWithDiagnostics(w.Text, "flow", Controls).Model.Oltcs);
        // A band that does round-trip (1.0 +/- 0.0625, step 0.03125 on 0.875..1.125, 9 positions) is written and read back.
        GridModel exact = withOltc.CopyWith(oltcs: new[] { new Oltc(t, m.Buses[^1].Id, 1.0, 0.875, 1.125, 0.03125, 0.0625) });
        PsseWriteResult we = PsseRawWriter.Write(exact);
        Oltc o = Assert.Single(PsseRawParser.ParseWithDiagnostics(we.Text, "flow", Controls).Model.Oltcs);
        Assert.Equal(exact.Oltcs[0], o);
    }

    [Fact]
    public void A_name_holding_a_single_quote_is_written_in_double_quotes()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(PsseLccTests.Case(), "lcc", Controls with { FrequencyHz = 50 }).Model;
        GridModel named = m.CopyWith(buses: m.Buses.Select((b, i) => i == 0 ? b with { Name = "Clyde's Mill" } : b).ToArray());
        PsseWriteResult w = PsseRawWriter.Write(named);
        Assert.Contains("\"Clyde's Mill\"", w.Text, StringComparison.Ordinal);
        Assert.Equal("Clyde's Mill", PsseRawParser.ParseWithDiagnostics(w.Text, "lcc", Controls).Model.Buses[0].Name);
        GridModel both = m.CopyWith(buses: m.Buses.Select((b, i) => i == 0 ? b with { Name = "a'b\"c" } : b).ToArray());
        Assert.Contains("both kinds of quote", Assert.Throws<InvalidDataException>(() => PsseRawWriter.Write(both)).Message);
    }

    [Fact]
    public void Convert_writes_raw_and_proves_the_round_trip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-convert-raw-" + Guid.NewGuid().ToString("N"));
        try
        {
            string output = Path.Combine(dir, "case14.raw");
            CliProcess.Result r = CliProcess.RunBuilt("convert", "case14", output);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("round trip: the written case solves bit-identically", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("compared in the file's branch order", r.StdOut, StringComparison.Ordinal);
            Assert.True(File.Exists(output));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
