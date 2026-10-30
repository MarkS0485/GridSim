using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Simulation;

namespace GridSim.Core.Net;

/// <summary>
/// The GridSim side of the GDA<->GridSim bridge. Wraps a <see cref="ClientWebSocket"/> over one socket that carries
/// both the state stream and request/response (forked from the RoCoF app's <c>server.py</c> transport). On connect it
/// runs a receive loop that deserialises each <see cref="BridgeProtocol"/> message by its <c>type</c>:
/// <list type="bullet">
/// <item><c>topology</c> -> build and cache the <see cref="GridModel"/> (<see cref="Topology"/>), raise <see cref="TopologyReceived"/>.</item>
/// <item><c>state</c> -> <b>call <see cref="ForwardInferenceGuard.RejectFuture"/> on the tick timestamp BEFORE building
/// anything</b> (a frame newer than the 169 h horizon is a protocol violation and is refused), then assemble a
/// <see cref="SimFrame"/> against the cached topology and raise <see cref="FrameReceived"/>.</item>
/// <item><c>result</c> -> complete the pending <see cref="Query"/> the reply's <c>id</c> correlates to.</item>
/// <item><c>error</c> -> a stream-level failure (unknown case, rejected start, no settled data, ...): fire
/// <see cref="Faulted"/> with the server's message so the stream death is visible rather than silently dropped.</item>
/// </list>
/// Pure Core (no UI/OS deps beyond the BCL socket), so the message pipeline is unit-testable without a live server.
/// </summary>
public sealed class GridBridgeClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Pending queries correlated by request id (not per-op FIFO): the server runs queries as independent tasks,
    // so same-op replies can arrive out of order - the echoed id matches each reply to its own awaiter. (GS-H4)
    private readonly ConcurrentDictionary<string, PendingQuery> _pending = new();
    private int _queriesInFlight;   // GR-723: Query calls holding a slot (reserved atomically, released when they end)
    private long _requestId;

    private GridModel? _topology;
    private int _tick;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    /// <summary>Maximum time a <see cref="Query"/> waits for its <c>result</c> before failing with a
    /// <see cref="TimeoutException"/> - bounds a live-but-silent server so an awaiter never orphans. (GS-H4)</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>NCSC CAF B4 resource limit: the largest single message the receive loop will accumulate, in bytes
    /// (default 8 MiB). A message larger than this is refused loudly rather than buffered without bound, so a
    /// hostile or faulty server cannot exhaust memory with one enormous frame.</summary>
    public long MaxMessageBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>NCSC CAF B4 resource limit: the most in-flight queries allowed at once (default 256). A caller that
    /// tries to start more than this gets a loud refusal rather than letting the pending table grow without bound.</summary>
    public int MaxInFlightQueries { get; set; } = 256;

    /// <summary>NCSC CAF B3 transport security: when false (the default) <see cref="ConnectAsync"/> refuses an
    /// unencrypted <c>ws://</c> endpoint; the bridge must be <c>wss://</c>. Set true only for a trusted loopback or
    /// a test harness where the transport is protected by other means.</summary>
    public bool AllowUnencryptedEndpoint { get; set; }

    // There is deliberately NO clock-skew allowance on the horizon (R276). The client used to add 15 minutes to
    // "now" before the check, which quietly accepted frames only 168 h 45 min old - weaker than the exact
    // 169 h invariant every other ingester enforces. The horizon compares a week-old data stamp with this
    // client's own clock, so client/server skew is no reason to weaken it; a server whose clock runs ahead and
    // streams a frame inside the horizon is refused, loudly, exactly as the invariant says.

    // Count of single messages that could not be read and were skipped without ending the stream (R275).
    private long _rejectedMessages;

    private sealed record PendingQuery(string Op, TaskCompletionSource<QueryResult> Tcs);

    /// <summary>Raised for every accepted <c>state</c> tick, after the forward-inference guard has passed.</summary>
    public event Action<SimFrame>? FrameReceived;

    /// <summary>
    /// Raised for every accepted <c>state</c> tick alongside <see cref="FrameReceived"/>, additionally carrying the
    /// raw per-tick system scalars and per-asset overlays straight off the wire - the bits the fixed
    /// <see cref="SimFrame"/> record doesn't carry (inertia/condition/RoCoF-headroom for the HUD; condition-by-bus
    /// and stress-by-branch for the map colour overlays). Fired after the forward-inference guard has passed, in the
    /// same call as (and immediately after) <see cref="FrameReceived"/>, so the frame and its scalars/overlays stay
    /// in lock-step with no cross-tick race. Either block may be <c>null</c> if the tick omitted it.
    /// </summary>
    public event Action<SimFrame, BridgeProtocol.ScalarsBlock?, BridgeProtocol.OverlaysBlock?>? StateReceived;

    /// <summary>
    /// Raised for every accepted <c>state</c> tick alongside <see cref="StateReceived"/>, carrying the per-tick
    /// OPERATOR- and MARKET-domain series streamed from the GDA lake (balancing-mechanism dispatch, constraints,
    /// warnings; imbalance/day-ahead prices, interconnector flows, carbon). Either block may be <c>null</c>.
    /// </summary>
    public event Action<SimFrame, BridgeProtocol.OperatorBlock?, BridgeProtocol.MarketBlock?>? MarketDataReceived;

    /// <summary>
    /// Raised for every accepted <c>state</c> tick from the <c>source=raw</c> replay, carrying the raw per-tick
    /// generation MIX (per-fuel MW), WEATHER blocks, and the tick's UTC wall-clock timestamp - the channels a pure
    /// raw replay adds beyond the solved <see cref="SimFrame"/> (which, in raw mode, is a degenerate no-solve frame).
    /// The timestamp lets the transport scrubber + date-picker track the current replay instant. Fired immediately
    /// BEFORE <see cref="MarketDataReceived"/> in the same call, so a consumer that builds its frame envelope on the
    /// market event can read these from the same tick with no cross-tick race. Blocks are <c>null</c> from
    /// solve/presolved/estimated sources.
    /// </summary>
    public event Action<SimFrame, BridgeProtocol.GenerationMixBlock?, BridgeProtocol.WeatherBlock?,
        BridgeProtocol.RawInterconnectorsBlock?, DateTime>? RawDataReceived;

    /// <summary>
    /// Raised for every accepted <c>state</c> tick alongside <see cref="StateReceived"/>, carrying the frame's
    /// reconstruction source (<c>live|presolved|estimated</c>; null from a legacy server) and - for
    /// <c>source=estimated</c> frames - the per-frame WLS estimation-quality block (J, chi-square verdict, redundancy,
    /// removals). The honesty layer for measurement-fitted replay: a view showing an estimated state can show
    /// how well that state fit its telemetry.
    /// </summary>
    public event Action<SimFrame, string?, BridgeProtocol.EstimateBlock?>? EstimateReceived;

    /// <summary>Raised once when the <c>topology</c> arrives and the model is cached.</summary>
    public event Action<GridModel>? TopologyReceived;

    /// <summary>Raised if the receive loop faults (socket error, or a rejected forward-inference frame).</summary>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Raised for ONE bridge message that could not be read - malformed or untyped JSON, a bad <c>t</c>
    /// timestamp, a missing block, a <c>state</c> before any <c>topology</c> - and was therefore skipped. The
    /// stream keeps running: one bad frame must not end the session (R275). This is not a quiet drop: every
    /// rejection raises this event and is counted in <see cref="RejectedMessages"/>, so a host can show it. A
    /// forward-inference violation is NOT reported here: that is an invariant breach and still faults the loop.
    /// </summary>
    public event Action<BridgeMessageException>? MessageRejected;

    /// <summary>
    /// GR-228: one of GridSim's OWN subscribers (a view building its frame) threw while handling a good message. That
    /// is a GridSim defect, not a bad server message: it used to be counted as a rejected message with the server's JSON
    /// quoted (sending the operator to debug the server), or, for an exception type outside the bad-message list, end the
    /// whole stream. It is reported here and the stream carries on. With no handler attached the exception propagates
    /// (fail loud).
    /// </summary>
    public event Action<BridgeSubscriberException>? SubscriberFailed;

    /// <summary>How many single messages have been rejected and skipped so far (see <see cref="MessageRejected"/>).</summary>
    public long RejectedMessages => Interlocked.Read(ref _rejectedMessages);

    /// <summary>The cached network from the last <c>topology</c> message, or <c>null</c> before one arrives.</summary>
    public GridModel? Topology => _topology;

    /// <summary>The underlying socket state, for the UI to reflect connection status.</summary>
    public WebSocketState State => _ws.State;

    /// <summary>Connect to the bridge endpoint and start the background receive loop.</summary>
    /// <exception cref="InvalidOperationException">The endpoint is an unencrypted <c>ws://</c> URI and
    /// <see cref="AllowUnencryptedEndpoint"/> is false (B3 transport security).</exception>
    public async Task ConnectAsync(Uri endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!AllowUnencryptedEndpoint && string.Equals(endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to connect to an unencrypted bridge endpoint '{endpoint}'. Use a 'wss://' endpoint, or set " +
                $"{nameof(AllowUnencryptedEndpoint)}=true only for a trusted loopback or test transport.");
        await _ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => ReceiveLoopAsync(_loopCts.Token), _loopCts.Token);
    }

    // ---- Stream control (mirrors the RoCoF server's start/stop/set_speed/seek actions) ----

    /// <summary>
    /// Ask the server to begin streaming <paramref name="caseName"/> from <paramref name="start"/> at
    /// <paramref name="fps"/> ticks/s. <paramref name="source"/> selects the server path:
    /// <c>"presolved"</c> replays the demand-scaled pre-solved lake frame-by-frame (per-bus state varies each
    /// tick), <c>"estimated"</c> replays the measurement-conditioned WLS estimate lake (each frame fitted to
    /// that period's real telemetry, carrying an <c>estimate</c> quality block - J, chi-square verdict, removals),
    /// <c>"live"</c> streams the static-network solve with only system scalars moving.
    /// </summary>
    // Phase 0.1 (source-generated JSON, NativeAOT): each fixed request below used to be an anonymous object serialised
    // by reflection. It is now a named record (BridgeRequests, end of file) with the same members in the same order,
    // written through the source-generated bridge context with the same bridge options (camelCase, so Action ->
    // "action", Case -> "case"): the bytes on the wire are unchanged.
    public Task StartAsync(string caseName, DateTime start, double fps = 1.0, string source = "presolved", CancellationToken ct = default) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(
            new BridgeRequests.StartRequest("start", caseName, Iso(start), fps, source), BridgeProtocol.Json.StartRequest), ct);

    /// <summary>Start a STORED lake (presolved|estimated|solves) from a literal start token
    /// (e.g. "first"/"earliest") with optional server-side looping. The bridge resolves the token
    /// to the case's earliest available frame and, when <paramref name="loop"/> is set, wraps
    /// continuously - historical data only, so the no-forward-inference horizon still applies.</summary>
    public Task StartAsync(string caseName, string startLiteral, double fps, string source, bool loop, CancellationToken ct = default) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(
            new BridgeRequests.StartLoopingRequest("start", caseName, startLiteral, fps, source, loop), BridgeProtocol.Json.StartLoopingRequest), ct);

    public Task StopAsync(CancellationToken ct = default) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(new BridgeRequests.StopRequest("stop"), BridgeProtocol.Json.StopRequest), ct);

    public Task SetSpeedAsync(double fps, CancellationToken ct = default) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(new BridgeRequests.SetSpeedRequest("set_speed", fps), BridgeProtocol.Json.SetSpeedRequest), ct);

    /// <summary>Jump the stream to <paramref name="at"/> (the server reads the <c>start</c> field for a seek).</summary>
    public Task SeekAsync(DateTime at, string source = "presolved", CancellationToken ct = default) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(
            new BridgeRequests.SeekRequest("seek", Iso(at), source), BridgeProtocol.Json.SeekRequest), ct);

    /// <summary>
    /// Run an on-demand physics query (<c>inertia|condition|scenario|feasibility|post_event</c>) at a historical
    /// instant and await its <c>result</c>. The returned <see cref="QueryResult.Solve"/> is reconstructed by
    /// <see cref="SolveImport"/> from the payload's <c>gridsim-solve-export/1</c> document, exactly as a loaded run is.
    /// </summary>
    /// <remarks>Phase 0.1: <paramref name="params"/> is any object the caller likes (the desktop front ends pass
    /// anonymous objects), so writing it needs runtime reflection over a type known only at run time. That cannot be
    /// made trim- or AOT-safe, so this method says so (the two Requires attributes) and the requirement passes to the
    /// caller, rather than being hidden. The bridge is a desktop feature (the mobile heads have no bridge); Core's own
    /// query (<see cref="QueryPropagation"/>) uses the reflection-free path. The request bytes are exactly as before.</remarks>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Serialises a caller-supplied params object of any type with reflection.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Serialises a caller-supplied params object of any type with reflection.")]
    public Task<QueryResult> Query(string op, DateTime at, object @params, CancellationToken ct = default) =>
        QueryCore(op, id => JsonSerializer.SerializeToUtf8Bytes(
            new { action = "query", op, id, at = Iso(at), @params }, BridgeProtocol.JsonOptions), ct);

    /// <summary>
    /// The body of every query (Phase 0.1 split it out of <see cref="Query"/>, unchanged): reserve a slot, register
    /// the pending reply, send the request that <paramref name="request"/> writes for the query id, and await the
    /// reply or the timeout.
    /// </summary>
    private async Task<QueryResult> QueryCore(string op, Func<string, byte[]> request, CancellationToken ct)
    {
        // B4 resource limit: cap the number of outstanding queries so a caller cannot grow the pending table
        // without bound. The timeout still reaps slow replies; this refuses new ones once the ceiling is reached.
        // GR-723 / GR-864: the slot is reserved atomically (a count check followed by an insert let concurrent callers
        // pass the check together and exceed the cap); it is released in the finally below.
        int inFlight = Interlocked.Increment(ref _queriesInFlight);
        if (inFlight > MaxInFlightQueries)
        {
            Interlocked.Decrement(ref _queriesInFlight);
            throw new InvalidOperationException(
                $"The bridge already has {inFlight - 1} queries in flight (limit {MaxInFlightQueries}); refusing a new one.");
        }

        string id = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<QueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _pending[id] = new PendingQuery(op, tcs); }
        catch { Interlocked.Decrement(ref _queriesInFlight); throw; }

        // Per-query timeout so a live-but-silent server can't orphan the awaiter forever. (GS-H4)
        using var timeoutCts = new CancellationTokenSource(QueryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            try { await SendAsync(request(id), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Bug hunt 2026-10-29, finding 58: the timeout can fire while the send itself is still blocked (socket
                // back-pressure). The timeout-to-TimeoutException translation below is registered only after the send,
                // so that case used to surface as a bare OperationCanceledException although the caller cancelled
                // nothing. It is the documented TimeoutException here too.
                throw new TimeoutException(
                    $"Bridge query '{op}' timed out after {QueryTimeout.TotalSeconds:0.#}s while its request was still being sent.");
            }

            await using (linked.Token.Register(() =>
            {
                if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    tcs.TrySetException(new TimeoutException(
                        $"Bridge query '{op}' timed out after {QueryTimeout.TotalSeconds:0.#}s with no response."));
                else
                    tcs.TrySetCanceled(ct);
            }).ConfigureAwait(false))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            Interlocked.Decrement(ref _queriesInFlight);
        }
    }

    // ---- Receive loop ----

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        // A clean remote close still ends the stream: fail every pending query and raise Faulted so
                        // awaiters don't orphan and the UI leaves "LIVE" once frames stop. Local shutdown cancels the
                        // loop token first, so this path is remote-initiated only (Dispose's cancel -> OperationCanceled). (GS-H4)
                        var closed = new BridgeStreamException("The bridge closed the connection.");
                        FailPending(closed);
                        Faulted?.Invoke(closed);
                        return;
                    }
                    // B4 resource limit: stop accumulating once the message exceeds the cap, rather than buffering an
                    // unbounded frame from a hostile or faulty server. An oversized frame ends the stream loudly.
                    if (ms.Length + res.Count > MaxMessageBytes)
                        throw new BridgeStreamException(
                            $"A bridge message exceeded the {MaxMessageBytes}-byte limit and was refused; the stream is closed.");
                    ms.Write(buffer, 0, res.Count);
                }
                while (!res.EndOfMessage);

                // Per-message guard: a message that cannot be read is reported and skipped, and the loop goes
                // on to the next one. Only transport failures and forward-inference violations end the loop.
                DispatchGuarded(System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown: DisposeAsync, or the token the host passed to ConnectAsync. Bug hunt 2026-10-29,
            // finding 57: on the second path nothing failed the pending table, so every Query awaiter waited out its
            // full QueryTimeout for an answer the stopped loop could never deliver. The pending queries are failed
            // with a cancellation now (Faulted is not raised: the host cancelled the stream itself). Failing an
            // already-empty table, as after DisposeAsync, does nothing.
            FailPending(new OperationCanceledException("The bridge receive loop was cancelled; no response will arrive."));
        }
        catch (Exception ex)
        {
            FailPending(ex);
            Faulted?.Invoke(ex);
        }
    }

    /// <summary>Route one raw message by its <c>type</c> discriminator. Internal so tests can drive the pipeline
    /// without a socket. Throws for a message that cannot be read; the receive loop calls
    /// <see cref="DispatchGuarded"/>, which turns that into a reported, non-fatal rejection.</summary>
    internal void Dispatch(string json)
    {
        string? type = BridgeProtocol.PeekType(json);
        if (type is null)
        {
            // Malformed JSON, or an object with no string 'type'. This used to be ignored without a word, so a
            // truncated frame simply vanished. It is now an error for this one message (reported, not fatal).
            throw new InvalidDataException(
                "Bridge message is not valid JSON or carries no string 'type' discriminator.");
        }

        switch (type)
        {
            case "topology": HandleTopology(json); break;
            case "state": HandleState(json); break;
            case "result": HandleResult(json); break;
            case "error": HandleError(json); break;
            default: /* a well-formed message of a type this build does not know: forward-compatible no-op */ break;
        }
    }

    /// <summary>
    /// The receive loop's per-message guard (R275). Dispatch one message; if that single message cannot be read,
    /// count it, raise <see cref="MessageRejected"/> and return, so the stream carries on with the next frame.
    /// A <see cref="ForwardInferenceException"/> is re-thrown: a frame inside the historical horizon is an
    /// invariant breach, and that still ends the stream by design. Anything that is not a recognised bad-data
    /// error (a genuine bug) is re-thrown as well rather than being hidden. Internal for testability.
    /// </summary>
    internal void DispatchGuarded(string json)
    {
        try
        {
            Dispatch(json);
        }
        catch (ForwardInferenceException)
        {
            throw;   // invariant breach: loop-fatal by design
        }
        catch (BridgeSubscriberException ex) when (SubscriberFailed is not null)
        {
            SubscriberFailed?.Invoke(ex);   // GR-228: GridSim's own fault, reported as such; the stream carries on
        }
        catch (Exception ex) when (ex is not BridgeSubscriberException && IsBadMessage(ex))
        {
            Interlocked.Increment(ref _rejectedMessages);
            string start = json.Length <= 120 ? json : json[..120] + "...";
            MessageRejected?.Invoke(new BridgeMessageException(
                $"Bridge message rejected and skipped ({ex.GetType().Name}: {ex.Message}). Start of message: {start}", ex));
        }
    }

    // The exception types that mean "this one message's content is bad" rather than "the client is broken".
    private static bool IsBadMessage(Exception ex) =>
        ex is JsonException or InvalidDataException or FormatException or InvalidOperationException
            or ArgumentException or KeyNotFoundException or OverflowException;

    /// <summary>
    /// A stream-level failure reported by the server (unknown case, rejected start, no settled data, an internal
    /// stream exception, ...). Surfaces it as <see cref="Faulted"/> so a dead stream is visible rather than silently
    /// dropped (query errors keep coming back as <c>result</c>+<c>verdict:"ERROR"</c>). Internal for testability. (GS-H2)
    /// </summary>
    /// <remarks>Bug hunt 2026-10-29, finding 59 (decided, documented): an <c>error</c> message raises <see cref="Faulted"/> but
    /// does NOT stop the receive loop or fail the pending queries. An <c>error</c> can report a problem that need
    /// not end the stream (a bad request, a source that failed one tick), and frames and query replies can keep arriving
    /// after it; a transport failure or a remote close is what ends the stream, and those fail every pending query. A
    /// host that treats Faulted as "stream dead" disposes the client, which fails the pending queries.</remarks>
    internal void HandleError(string json)
    {
        string? message = BridgeProtocol.ReadMessage(json);
        Faulted?.Invoke(new BridgeStreamException(message ?? "The bridge reported an unspecified stream error."));
    }

    /// <summary>Build and cache the network. Internal for testability.</summary>
    internal GridModel HandleTopology(string json)
    {
        GridModel model = BridgeProtocol.ReadTopology(json).ToModel();
        _topology = model;
        _tick = 0;
        Publish("topology", () => TopologyReceived?.Invoke(model));
        return model;
    }

    /// <summary>
    /// Guard, build and publish one <c>state</c> frame. Throws <see cref="ForwardInferenceException"/> if the tick is
    /// newer than the historical horizon - enforced BEFORE the frame is built, so a future tick never reaches a view.
    /// Internal for testability.
    /// </summary>
    internal SimFrame HandleState(string json)
    {
        GridModel topo = _topology
            ?? throw new InvalidOperationException("Received a bridge 'state' before any 'topology'.");

        BridgeProtocol.StateMessage msg = BridgeProtocol.ReadState(json);
        // A missing/empty 't' parses to DateTime.MinValue, which passes the
        // horizon guard BY CONSTRUCTION - i.e. an undated frame slid past the
        // no-forward-inference defence entirely. Undated state is a protocol
        // violation, not a guardable timestamp.
        if (msg.Timestamp == DateTime.MinValue)
            throw new InvalidOperationException(
                "Bridge 'state' frame carries no 't' timestamp - refused (the horizon guard cannot vouch for undated state).");
        // The exact 169 h horizon against this client's clock, with no allowance (R276): a frame newer than the
        // horizon is a protocol violation and is refused before any frame is built.
        ForwardInferenceGuard.RejectFuture(msg.Timestamp, utcNow: DateTime.UtcNow, what: "bridge state");
        // N214 (October 2026): an estimate block whose chiTest is unknown or contradicts chiPassed is refused before the
        // frame is built, so neither shell shows a chi-square verdict the frame does not carry.
        msg.Estimate?.RequireConsistentChiTest();

        SimFrame frame = msg.ToSimFrame(topo, Interlocked.Increment(ref _tick));
        Publish("state", () =>
        {
            FrameReceived?.Invoke(frame);
            StateReceived?.Invoke(frame, msg.Scalars, msg.Overlays);
            // BEFORE MarketDataReceived: a raw-mode consumer builds its envelope on the market event and reads the raw
            // mix/weather/interconnectors + tick timestamp captured here for the same tick (see SimSession.WireBridge).
            RawDataReceived?.Invoke(frame, msg.GenerationMix, msg.Weather, msg.Interconnectors, msg.Timestamp);
            MarketDataReceived?.Invoke(frame, msg.Operator, msg.Market);
            EstimateReceived?.Invoke(frame, msg.Source, msg.Estimate);
        });
        return frame;
    }

    // GR-228: run the subscribers of a message that has been read and checked. An exception from them is wrapped as a
    // BridgeSubscriberException, which DispatchGuarded reports through SubscriberFailed instead of as a bad message.
    private static void Publish(string what, Action subscribers)
    {
        try { subscribers(); }
        catch (Exception ex) when (ex is not ForwardInferenceException and not OperationCanceledException)
        {
            throw new BridgeSubscriberException(
                $"A GridSim view failed while handling a good bridge '{what}' message ({ex.GetType().Name}: {ex.Message}); this is a GridSim fault, not a bad server message.", ex);
        }
    }

    /// <summary>
    /// Query GDA's <c>propagation_delays</c> product (<c>op="propagation"</c>) and return the corridor tau
    /// map ready to hand to <c>DistributedConfig.ProvidedTauSeconds</c> so the distributed inertia model
    /// uses GDA's derived delays instead of its own first-order estimate. Empty when the lake product is
    /// not built or carries no tau.
    /// </summary>
    public async Task<PropagationData> QueryPropagation(DateTime at, CancellationToken ct = default)
    {
        // Phase 0.1: the reflection-free query path. The request is the named BridgeRequests.QueryRequest record with an empty
        // params object, the same bytes the anonymous { action, op, id, at, params = new { } } produced.
        QueryResult r = await QueryCore("propagation", id => JsonSerializer.SerializeToUtf8Bytes(
            new BridgeRequests.QueryRequest<BridgeRequests.NoParams>("query", "propagation", id, Iso(at), new BridgeRequests.NoParams()),
            BridgeProtocol.Json.QueryNoParams), ct).ConfigureAwait(false);
        return r.Propagation ?? new PropagationData(
            new System.Collections.Generic.Dictionary<(int, int), double>(), null);
    }

    /// <summary>
    /// Complete the pending <see cref="Query"/> the reply's echoed <c>id</c> correlates to, strictly (GS-H4, D-DOC-078).
    /// Internal for testability.
    /// <para>
    /// D-DOC-078: a reply with no <c>id</c> used to be paired with the oldest pending query of the same <c>op</c>. With
    /// two same-op queries in flight (two feasibility probes at different buses) that hands one query the other's
    /// answer, silently. Pairing is now by <c>id</c> only, and every reply that cannot be paired exactly is reported
    /// through <see cref="MessageRejected"/> (and counted) instead of being guessed at:
    /// <list type="bullet">
    /// <item><description>no <c>id</c>, or an empty one: paired with nothing. The query it answers (if any) fails on its
    /// own <see cref="QueryTimeout"/>, which is the honest outcome for an unattributable reply.</description></item>
    /// <item><description>an <c>id</c> that matches no pending query (it timed out, was cancelled, or was never sent):
    /// reported, nothing completed.</description></item>
    /// <item><description>an <c>id</c> whose query asked a DIFFERENT <c>op</c>: that query fails at once with an
    /// <see cref="InvalidDataException"/> naming both ops (a server bug, never a usable answer).</description></item>
    /// <item><description>a correctly paired reply whose payload cannot be read: that query fails at once with the
    /// read error (it used to wait out the timeout), and the message is reported.</description></item>
    /// </list>
    /// The parsed result is still returned to the caller in every readable case, so a test can inspect it.
    /// </para>
    /// </summary>
    internal QueryResult HandleResult(string json)
    {
        BridgeProtocol.ResultMessage msg = BridgeProtocol.ReadResult(json);

        // Claim the pending query FIRST (by id only), so a payload that fails to read can fail its own awaiter.
        PendingQuery? pq = null;
        string? refusal = null;
        if (string.IsNullOrEmpty(msg.Id))
            refusal = $"Bridge 'result' for op '{msg.Op ?? "(none)"}' carries no 'id', so it cannot be paired with a query; ignored "
                      + "(replies are paired by id only, never by op).";
        else if (!_pending.TryRemove(msg.Id, out pq))
            refusal = $"Bridge 'result' id '{msg.Id}' (op '{msg.Op ?? "(none)"}') matches no pending query "
                      + "(it timed out, was cancelled, or was never sent); ignored.";
        else if (msg.Op is string op && !string.Equals(op, pq.Op, StringComparison.Ordinal))
        {
            refusal = $"Bridge 'result' id '{msg.Id}' answers op '{op}', but that query asked op '{pq.Op}'; the query is failed.";
            pq.Tcs.TrySetException(new InvalidDataException(refusal));
            pq = null;
        }

        QueryResult result;
        try { result = msg.ToQueryResult(); }
        catch (Exception ex) when (IsBadMessage(ex))
        {
            pq?.Tcs.TrySetException(ex);   // the paired query learns why now, not after QueryTimeout
            throw;                          // and the receive loop's guard reports the message (R275)
        }

        if (pq is not null) pq.Tcs.TrySetResult(result);
        else if (refusal is not null) ReportRejected(refusal);
        return result;
    }

    /// <summary>
    /// Test seam (D-DOC-078): register a pending query exactly as <see cref="Query"/> does, without a socket, so the
    /// reply pairing in <see cref="HandleResult"/> can be driven directly. Internal; not used by the client itself.
    /// </summary>
    internal Task<QueryResult> AddPendingForTest(string id, string op)
    {
        var tcs = new TaskCompletionSource<QueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, new PendingQuery(op, tcs)))
            throw new InvalidOperationException($"A query with id '{id}' is already pending.");
        return tcs.Task;
    }

    /// <summary>Count one unusable message and raise <see cref="MessageRejected"/> for it (the stream carries on).</summary>
    private void ReportRejected(string message)
    {
        Interlocked.Increment(ref _rejectedMessages);
        MessageRejected?.Invoke(new BridgeMessageException(message, new InvalidDataException(message)));
    }

    // ---- Send / helpers ----

    // Phase 0.1: takes the request already written (by the source-generated context, or by the one reflection path in
    // Query), so this method itself serialises nothing.
    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private void FailPending(Exception ex)
    {
        foreach (string key in _pending.Keys)
            if (_pending.TryRemove(key, out PendingQuery? pq))
                pq.Tcs.TrySetException(ex);
    }

    /// <summary>Format a request instant for the wire as ISO-8601 UTC. An Unspecified value is UTC by contract
    /// (<see cref="UtcTime.Normalise"/>); <c>ToUniversalTime</c> used to shift it by the machine's local offset, so
    /// on a BST machine a start of 12:00 asked the server for 11:00Z (R277). Internal for testability.</summary>
    internal static string Iso(DateTime dt) => UtcTime.Normalise(dt).ToString("o", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        try { _loopCts?.Cancel(); } catch { /* ignore */ }
        FailPending(new ObjectDisposedException(nameof(GridBridgeClient)));

        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* best-effort close */ }
        }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* already faulted/cancelled */ }
        }

        _ws.Dispose();
        _loopCts?.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>
/// A stream-level failure surfaced by <see cref="GridBridgeClient.Faulted"/>: either a <c>type:"error"</c> message
/// from the server (unknown case, rejected start, no settled data, an internal stream exception) or a clean remote
/// close while queries were still pending. Distinct from a query's own <c>verdict:"ERROR"</c>, which rides a normal
/// <c>result</c>. (GS-H2 / GS-H4)
/// </summary>
public sealed class BridgeStreamException : Exception
{
    public BridgeStreamException(string message) : base(message) { }
}

/// <summary>
/// One bridge message that could not be read and was skipped, surfaced by
/// <see cref="GridBridgeClient.MessageRejected"/>. The stream itself carries on. The original parse or
/// validation failure is the <see cref="Exception.InnerException"/>. (R275)
/// </summary>
public sealed class BridgeMessageException : Exception
{
    public BridgeMessageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>GR-228: a GridSim subscriber threw while handling a good bridge message (see
/// <see cref="GridBridgeClient.SubscriberFailed"/>).</summary>
public sealed class BridgeSubscriberException : Exception
{
    public BridgeSubscriberException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Phase 0.1 (source-generated JSON, NativeAOT): the fixed requests the client sends, as named records instead of
/// anonymous objects. Each record has exactly the members the anonymous object had, in the same order, so under the
/// bridge options (camelCase) it writes the same JSON: { "action": ..., "case": ..., ... }. Written through
/// <see cref="BridgeProtocol.Json"/>.
/// </summary>
internal static class BridgeRequests
{
    /// <summary>{action:"start", case, start, fps, source}: start streaming from a dated instant.</summary>
    internal sealed record StartRequest(string Action, string Case, string Start, double Fps, string Source);

    /// <summary>{action:"start", case, start, fps, source, loop}: start a stored lake from a literal start token.</summary>
    internal sealed record StartLoopingRequest(string Action, string Case, string Start, double Fps, string Source, bool Loop);

    /// <summary>{action:"stop"}.</summary>
    internal sealed record StopRequest(string Action);

    /// <summary>{action:"set_speed", fps}.</summary>
    internal sealed record SetSpeedRequest(string Action, double Fps);

    /// <summary>{action:"seek", start, source}.</summary>
    internal sealed record SeekRequest(string Action, string Start, string Source);

    /// <summary>{action:"query", op, id, at, params}: an on-demand query with a typed params object.</summary>
    internal sealed record QueryRequest<TParams>(string Action, string Op, string Id, string At, TParams Params);

    /// <summary>An empty params object; writes {} exactly as the anonymous new { } did.</summary>
    internal sealed class NoParams;
}
