using System;
using GridSim.Core.Interpose;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R089 (October 2026): the DNP3 and IEC 61850 GOOSE codecs are link-framing scaffolding with no application layer and
/// no protocol stack; the registry listing says they are not selectable, and they are not registered.
/// </summary>
public class R089ScaffoldingLabelTests
{
    [Fact]
    public void The_registry_says_dnp3_and_goose_are_not_selectable_and_does_not_register_them()
    {
        string text = RtuProtocolRegistry.Describe();
        Assert.Contains("Not selectable: DNP3 and IEC 61850 GOOSE exist only as link-framing scaffolding", text, StringComparison.Ordinal);
        Assert.Contains("iec104", text, StringComparison.Ordinal);
        Assert.Contains("modbus", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((byte)5)]
    [InlineData((byte)32)]
    [InlineData((byte)7)]
    [InlineData((byte)33)]
    public void Step_position_and_bitstring_types_stay_counted_unsupported(byte typeId)
    {
        // Moved 2026-10 (END-PASS CF2 row 1, end pass; IEC 60870-5-101 / 104 type ids 5 M_ST_NA_1, 32 M_ST_TB_1, 7 M_BO_NA_1,
        // 33 M_BO_TB_1 decoded by N337, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): N337 reverses R089's
        // "stay unsupported" rule, so the pipeline decoder now turns these four types into points; the old behaviour no
        // longer exists for them (other unsupported types are still counted: Iec104DoublePointTimedTests). The name is kept
        // so existing references still resolve. Before -> after: Assert.False(IsSupportedType) -> Assert.True(IsSupportedType),
        // all four rows (observed: Expected False, Actual True, 1.95.273-275 runs).
        Assert.True(Iec104Asdu.IsSupportedType(typeId));
    }
}
