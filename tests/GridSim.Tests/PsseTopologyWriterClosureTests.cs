using GridSim.Core.IO.Psse.Topology;
using Xunit;
using static GridSim.Tests.PsseTopologyFixture;

namespace GridSim.Tests;

/// <summary>IO-23: the writer refuses a closure its own reader would refuse.</summary>
public sealed class PsseTopologyWriterClosureTests
{
    [Fact]
    public void Writing_a_closure_between_two_buses_is_refused()
    {
        // A fifth node on bus 100 behind an open tie from node 4 (bus 200): consistent while open.
        string text = InsertAfter(CouplerText, "   4,'CCT 2 BAY', 200, 1", "   5,'FAR NODE', 100, 1");
        text = InsertAfter(text, "   2, 4, 'B4'", "   4, 5, 'T5', 'TIE', 1, 0, 0, 0.0001");
        Assert.NotEqual(CouplerText, text);
        PsseSubstationCase c = Read(text);
        var tie = new PsseSwitchKey(1, 4, 5, "T5");
        var states = PsseTopologyModelBuilder.AllClosed(c.Topology).ToDictionary(kv => kv.Key, kv => kv.Value);
        states[tie] = false;
        PsseTopologyRawWriter.Write(c, states);   // open: fine

        states[tie] = true;
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => PsseTopologyRawWriter.Write(c, states));
        Assert.Contains("same bus-branch bus", e.Message);
        Assert.Contains("'T5'", e.Message);
    }
}
