using System.Threading.Channels;

namespace GridSim.Web.Api;

/// <summary>
/// The outbound frame queue behind one <c>/ws/sim</c> socket: a bounded DropOldest channel for state/error frames,
/// plus a priority slot for the topology frame. The topology bypasses the droppable channel and is emitted just
/// before the first channel frame of its session, so a slow client can never evict it (GS-H6 topology-first).
/// <para>
/// One socket can carry several sessions in turn: every <c>start</c> control frame replaces the session. Each
/// start therefore opens a new <b>generation</b> (<see cref="BeginSession"/>):
/// <list type="bullet">
///   <item>the topology slot is cleared and re-armed, so the NEW case's topology is delivered once, ahead of its
///   first state frame (R050: the slot used to be one-shot per connection, so a second start never delivered the
///   new topology and the client paired 118-bus state frames with a 9-bus topology);</item>
///   <item>frames still queued from the replaced session carry the old generation and are dropped at send time,
///   so they can never reach the client after the new topology;</item>
///   <item>late callbacks from the replaced session (a bridge topology arriving after the swap) pass their own
///   generation and are ignored.</item>
/// </list>
/// All state is guarded by one lock, so the receive loop, session threads and the send loop can call it freely.
/// </para>
/// </summary>
public sealed class SimOutbound
{
    /// <summary>One queued message and the session generation it belongs to.</summary>
    public readonly record struct Item(long Generation, object Message);

    private readonly Channel<Item> _channel = Channel.CreateBounded<Item>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly object _lock = new();
    // GR-227: error frames wait here, unbounded, and are sent ahead of the next channel item of their generation, so a
    // slow client under back-pressure can never have an error evicted by the drop-oldest channel (technical 0034: "answered
    // with an error frame, never dropped"). A marker item in the channel wakes the send loop.
    private readonly Queue<Item> _errors = new();
    private static readonly object ErrorWake = new();
    private long _generation;          // bumped by every BeginSession; 0 before the first start
    private object? _topology;         // the current generation's topology, once known
    private bool _topologyDelivered;   // true once that topology has been handed to the send loop

    /// <summary>The channel the send loop drains.</summary>
    public ChannelReader<Item> Reader => _channel.Reader;

    /// <summary>The current session generation.</summary>
    public long Generation { get { lock (_lock) return _generation; } }

    /// <summary>
    /// Start a new session on this socket: drop the old topology, re-arm the slot, and return the new generation.
    /// Call it after the old session has been unsubscribed, before the new one can produce frames.
    /// </summary>
    public long BeginSession()
    {
        lock (_lock)
        {
            _generation++;
            _topology = null;
            _topologyDelivered = false;
            return _generation;
        }
    }

    /// <summary>Queue a frame for the CURRENT generation (the receive loop's own error frames, the live session's
    /// frames). Returns false only when the channel is completed.</summary>
    public bool TryWrite(object msg)
    {
        long g;
        lock (_lock) g = _generation;
        return _channel.Writer.TryWrite(new Item(g, msg));
    }

    /// <summary>Queue a frame on behalf of a specific generation. A frame from a replaced session is still
    /// queued but is dropped at send time (see <see cref="PrepareSend"/>).</summary>
    public bool TryWrite(object msg, long generation) => _channel.Writer.TryWrite(new Item(generation, msg));

    /// <summary>Queue an ERROR frame for the current generation (or <paramref name="generation"/>): it is held outside the
    /// drop-oldest channel and sent before the next frame, so it cannot be discarded under back-pressure (GR-227).
    /// Returns false only when the channel is completed.</summary>
    public bool TryWriteError(object msg, long? generation = null)
    {
        long g;
        lock (_lock)
        {
            g = generation ?? _generation;
            _errors.Enqueue(new Item(g, msg));
        }
        // Wake the send loop. If the channel is full this marker may itself be dropped, but then other items are queued
        // and the next one delivers the held error first.
        return _channel.Writer.TryWrite(new Item(g, ErrorWake));
    }

    /// <summary>Close the channel; the send loop finishes once it has drained.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// Record the topology for <paramref name="generation"/>. Ignored when that generation has already been
    /// replaced (a late bridge callback from a disposed session). Setting it re-arms delivery, so it is sent once
    /// before the next frame of this generation.
    /// </summary>
    public void SetTopology(object topology, long generation)
    {
        ArgumentNullException.ThrowIfNull(topology);
        lock (_lock)
        {
            if (generation != _generation) return;
            _topology = topology;
            _topologyDelivered = false;
        }
    }

    /// <summary>
    /// Decide what the send loop must put on the wire for one dequeued item, in order: nothing for a stale frame
    /// from a replaced session; otherwise the pending topology (exactly once per generation) followed by the
    /// frame itself.
    /// </summary>
    public IReadOnlyList<object> PrepareSend(Item item)
    {
        lock (_lock)
        {
            // Held error frames of the current generation go first (GR-227); those of a replaced session are dropped.
            var held = new List<object>();
            while (_errors.Count > 0)
            {
                Item e = _errors.Dequeue();
                if (e.Generation == _generation) held.Add(e.Message);
            }
            bool wake = ReferenceEquals(item.Message, ErrorWake);
            if (item.Generation != _generation) return held;
            var send = new List<object>(held.Count + 2);
            if (_topology is { } topo && !_topologyDelivered && !wake)
            {
                _topologyDelivered = true;
                send.Add(topo);
            }
            send.AddRange(held);
            if (!wake) send.Add(item.Message);
            return send;
        }
    }
}
