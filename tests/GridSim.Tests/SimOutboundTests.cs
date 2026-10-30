using GridSim.Web.Api;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R050: the <c>/ws/sim</c> outbound queue must deliver each start's topology exactly once, ahead of that start's
/// first frame, for EVERY start on the socket, and must never let a frame from a replaced session reach the client
/// after the new topology. Before the fix the topology slot was one-shot per connection, so a second start sent the
/// new case's state frames without its topology.
/// </summary>
public class SimOutboundTests
{
    /// <summary>Drain everything currently queued, in the order the send loop would put it on the wire.</summary>
    private static List<object> Drain(SimOutbound q)
    {
        var wire = new List<object>();
        while (q.Reader.TryRead(out SimOutbound.Item item))
            wire.AddRange(q.PrepareSend(item));
        return wire;
    }

    [Fact]
    public void First_start_sends_topology_once_before_its_first_frame()
    {
        var q = new SimOutbound();
        long g = q.BeginSession();
        q.SetTopology("topo-case9", g);
        q.TryWrite("state-9a");
        q.TryWrite("state-9b");

        Assert.Equal(new object[] { "topo-case9", "state-9a", "state-9b" }, Drain(q));
    }

    [Fact]
    public void Second_start_delivers_the_new_topology_before_its_frames()
    {
        var q = new SimOutbound();
        long g1 = q.BeginSession();
        q.SetTopology("topo-case9", g1);
        q.TryWrite("state-9a");
        Assert.Equal(new object[] { "topo-case9", "state-9a" }, Drain(q));

        long g2 = q.BeginSession();
        Assert.Equal(g1 + 1, g2);
        q.SetTopology("topo-case118", g2);
        q.TryWrite("state-118a");

        // Before the fix this drained as just "state-118a": the client kept the 9-bus topology.
        Assert.Equal(new object[] { "topo-case118", "state-118a" }, Drain(q));
    }

    [Fact]
    public void Frames_left_from_a_replaced_session_are_dropped_not_sent_after_the_new_topology()
    {
        var q = new SimOutbound();
        long g1 = q.BeginSession();
        q.SetTopology("topo-case9", g1);
        q.TryWrite("state-9a");
        q.TryWrite("state-9b");            // still queued when the client sends the second start

        long g2 = q.BeginSession();
        q.SetTopology("topo-case118", g2);
        q.TryWrite("state-118a");

        Assert.Equal(new object[] { "topo-case118", "state-118a" }, Drain(q));
    }

    [Fact]
    public void A_late_topology_or_error_from_a_replaced_session_is_ignored()
    {
        var q = new SimOutbound();
        long g1 = q.BeginSession();
        long g2 = q.BeginSession();

        // A bridge session from generation 1 reports its topology and an error after it was replaced.
        q.SetTopology("topo-stale", g1);
        q.TryWrite("error-stale", g1);
        q.SetTopology("topo-new", g2);
        q.TryWrite("state-new");

        Assert.Equal(new object[] { "topo-new", "state-new" }, Drain(q));
    }

    [Fact]
    public void A_failed_start_still_delivers_its_error_frame()
    {
        var q = new SimOutbound();
        long g1 = q.BeginSession();
        q.SetTopology("topo-case9", g1);
        q.TryWrite("state-9a");
        Drain(q);

        // The second start fails (unknown case): no topology, just the error frame, which must arrive.
        q.BeginSession();
        q.TryWrite("error-unknown-case");
        Assert.Equal(new object[] { "error-unknown-case" }, Drain(q));
    }
}
