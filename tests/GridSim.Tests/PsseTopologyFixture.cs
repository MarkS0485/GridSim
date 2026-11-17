using System.Security.Cryptography;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Psse.Topology;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Shared helpers for the P2 node-breaker topology tests: hash-bound reads of the two-section coupler fixture
/// (and text variants of it) and bit-level comparison of two AC solves.</summary>
internal static class PsseTopologyFixture
{
    public static string Dir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "psse"));
    public static string CouplerPath => Path.Combine(Dir, "substation-coupler-v35.raw");
    public static string CouplerText => File.ReadAllText(CouplerPath);

    public static readonly PsseTopologyModelOptions Options = new() { ZeroImpedanceThresholdPu = 0.0001 };

    public static readonly PsseSwitchKey Coupler = new(1, 1, 2, "C1");
    public static readonly PsseSwitchKey LoadBayDisconnector = new(1, 2, 3, "D3");
    public static readonly PsseSwitchKey Circuit2Breaker = new(1, 2, 4, "B4");

    /// <summary>Reads RAW text through the hash-bound case reader (the text is written to a temporary file first).</summary>
    public static PsseSubstationCase Read(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        string path = Path.Combine(Path.GetTempPath(), "gridsim-p2-" + Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            File.WriteAllBytes(path, bytes);
            return PsseSubstationCaseReader.Read(path, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally { File.Delete(path); }
    }

    public static PsseSubstationCase ReadCoupler() => Read(CouplerText);

    /// <summary>The 1-based line of the first line of <paramref name="text"/> that contains <paramref name="marker"/>.</summary>
    public static int LineOf(string text, string marker)
    {
        string[] lines = PsseText.Lines(text);
        for (int i = 0; i < lines.Length; i++) if (lines[i].Contains(marker, StringComparison.Ordinal)) return i + 1;
        throw new InvalidOperationException($"Marker '{marker}' is not in the text.");
    }

    public static string InsertAfter(string text, string marker, string newLine)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"marker '{marker}' not found");
        int eol = text.IndexOf('\n', at);
        return text[..(eol + 1)] + newLine + "\r\n" + text[(eol + 1)..];
    }

    public static string Remove(string text, string wholeLineMarker)
    {
        string[] lines = text.Split('\n');
        int matches = lines.Count(l => l.Contains(wholeLineMarker, StringComparison.Ordinal));
        Assert.Equal(1, matches);
        return string.Join('\n', lines.Where(l => !l.Contains(wholeLineMarker, StringComparison.Ordinal)));
    }

    public static PowerFlowResult Solve(GridSim.Core.Model.GridModel model) => new NewtonRaphsonPowerFlow().Solve(model);

    /// <summary>Asserts two AC solves are identical to the last bit, quantity by quantity, naming the first difference.</summary>
    public static void AssertBitIdentical(PowerFlowResult expected, PowerFlowResult actual)
    {
        Assert.Equal(expected.Converged, actual.Converged);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Iterations, actual.Iterations);
        Bits("MaxMismatch", expected.MaxMismatch, actual.MaxMismatch);
        Assert.Equal(expected.BusIds, actual.BusIds);
        Series("Vm", expected.Vm, actual.Vm);
        Series("VaDeg", expected.VaDeg, actual.VaDeg);
        Series("PInjMw", expected.PInjMw, actual.PInjMw);
        Series("QInjMvar", expected.QInjMvar, actual.QInjMvar);
        Bits("TotalLossMw", expected.TotalLossMw, actual.TotalLossMw);
        Bits("TotalLossMvar", expected.TotalLossMvar, actual.TotalLossMvar);
        Assert.Equal(expected.LineFlows.Count, actual.LineFlows.Count);
        for (int k = 0; k < expected.LineFlows.Count; k++)
        {
            LineFlow e = expected.LineFlows[k], a = actual.LineFlows[k];
            Assert.Equal((e.From, e.To, e.Name), (a.From, a.To, a.Name));
            Bits($"LineFlows[{k}].PFromMw", e.PFromMw, a.PFromMw);
            Bits($"LineFlows[{k}].QFromMvar", e.QFromMvar, a.QFromMvar);
            Bits($"LineFlows[{k}].PToMw", e.PToMw, a.PToMw);
            Bits($"LineFlows[{k}].QToMvar", e.QToMvar, a.QToMvar);
            Bits($"LineFlows[{k}].PLossMw", e.PLossMw, a.PLossMw);
            Bits($"LineFlows[{k}].QLossMvar", e.QLossMvar, a.QLossMvar);
            Bits($"LineFlows[{k}].LoadingPercent", e.LoadingPercent, a.LoadingPercent);
        }
        Assert.Equal(expected.SolvedBusTypes, actual.SolvedBusTypes);
    }

    private static void Series(string what, double[] e, double[] a)
    {
        Assert.Equal(e.Length, a.Length);
        for (int i = 0; i < e.Length; i++) Bits($"{what}[{i}]", e[i], a[i]);
    }

    private static void Bits(string what, double e, double a)
    {
        long eb = BitConverter.DoubleToInt64Bits(e), ab = BitConverter.DoubleToInt64Bits(a);
        Assert.True(eb == ab, $"{what}: expected {e:R} (0x{eb:X16}), actual {a:R} (0x{ab:X16})");
    }
}
