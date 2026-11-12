using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// New tests only (nothing existing is changed). GR-317: Modbus exception responses, transaction-id reuse and
/// pipelined responses, beyond what
/// <c>LimitationFixesStreamAndLinkTests</c> (GR-801 / GR-329) already pins.
/// </summary>
public class GA7E3Batch1Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-ga7e3-{Guid.NewGuid():N}");

    public GA7E3Batch1Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort, temp folder */ }
        GC.SuppressFinalize(this);
    }

    // ---- GR-317: Modbus --------------------------------------------------------------------------------------------

    private static IReadOnlyList<RtuPoint> Decode(ModbusProtocol p, byte[] frame) => p.Decode(frame, QualityFold.Default);

    // An exception response: MBAP (length 3) | fc | 0x80 | exception code.
    private static byte[] ExceptionResponse(int transactionId, byte unit, byte functionCode, byte code)
        => new byte[] { (byte)(transactionId >> 8), (byte)transactionId, 0x00, 0x00, 0x00, 0x03, unit, (byte)(functionCode | 0x80), code };

    [Fact]
    public void GR317_pipelined_requests_with_distinct_ids_pair_with_out_of_order_responses()
    {
        var p = new ModbusProtocol();
        Assert.Empty(Decode(p, ModbusTestFrames.ReadRequest(1, 1, 3, 100, 2)));
        Assert.Empty(Decode(p, ModbusTestFrames.ReadRequest(2, 1, 3, 200, 1)));

        // The second request is answered first: each response still takes the addresses of its own request.
        Assert.Equal(new[] { "MB:U1:3:@200" },
            Decode(p, ModbusTestFrames.ReadResponse(2, 1, 3, 9)).Select(x => x.Address).ToArray());
        Assert.Equal(new[] { "MB:U1:3:@100", "MB:U1:3:@101" },
            Decode(p, ModbusTestFrames.ReadResponse(1, 1, 3, 7, 8)).Select(x => x.Address).ToArray());
    }

    [Fact]
    public void GR317_an_id_reused_after_its_response_is_not_ambiguous()
    {
        var p = new ModbusProtocol();
        Decode(p, ModbusTestFrames.ReadRequest(7, 1, 3, 100, 1));
        Assert.Equal("MB:U1:3:@100", Decode(p, ModbusTestFrames.ReadResponse(7, 1, 3, 1)).Single().Address);

        // Same id, different block, but the first transaction is finished: this is ordinary id wrap-around.
        Decode(p, ModbusTestFrames.ReadRequest(7, 1, 3, 200, 1));
        Assert.Equal("MB:U1:3:@200", Decode(p, ModbusTestFrames.ReadResponse(7, 1, 3, 2)).Single().Address);
    }

    [Fact]
    public void GR317_an_exception_response_for_another_function_code_leaves_the_transaction_outstanding()
    {
        var p = new ModbusProtocol();
        Decode(p, ModbusTestFrames.ReadRequest(4, 1, 3, 50, 1));

        // Same id and unit but function 4 (not the outstanding function 3): it does not answer that request.
        Assert.Empty(Decode(p, ExceptionResponse(4, 1, 4, 0x02)));
        // A different unit under the same id also leaves it alone.
        Assert.Empty(Decode(p, ExceptionResponse(4, 2, 3, 0x02)));

        Assert.Equal("MB:U1:3:@50", Decode(p, ModbusTestFrames.ReadResponse(4, 1, 3, 5)).Single().Address);
    }

    [Fact]
    public void GR317_an_exception_response_clears_an_ambiguous_id()
    {
        var p = new ModbusProtocol();
        Decode(p, ModbusTestFrames.ReadRequest(9, 1, 3, 100, 2));
        Decode(p, ModbusTestFrames.ReadRequest(9, 1, 3, 200, 2));   // same id, other block: id 9 is now ambiguous

        // The device answers with an exception: the transaction is over, and the ambiguity goes with it.
        Assert.Empty(Decode(p, ExceptionResponse(9, 1, 3, 0x02)));

        // A fresh request and response under id 9 therefore pair normally instead of being refused.
        Decode(p, ModbusTestFrames.ReadRequest(9, 1, 3, 300, 1));
        Assert.Equal("MB:U1:3:@300", Decode(p, ModbusTestFrames.ReadResponse(9, 1, 3, 3)).Single().Address);
    }
}
