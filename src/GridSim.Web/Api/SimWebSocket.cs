using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;

namespace GridSim.Web.Api;

/// <summary>
/// The raw-WebSocket streaming endpoint (<c>/ws/sim</c>) - the external-consumer path (the Blazor UI uses
/// the same <see cref="SimSession"/> in-process over its own circuit). Protocol: the client sends a
/// <c>start</c> control frame, the server replies with a <c>topology</c> frame then a <c>state</c> frame per
/// tick; further control frames (play/pause/step/seek/speed/trip) drive the session. A later <c>start</c> on the
/// same socket replaces the session and again sends the NEW case's <c>topology</c> before its first <c>state</c>
/// frame; frames still queued from the replaced session are dropped. Message types reuse the
/// bridge protocol's <c>type</c> discriminator so a GDA server could feed the same shape later.
/// </summary>
public static class SimWebSocket
{
    // NonFiniteDoubleConverter keeps a +/-inf/NaN double (GDA Q-limits, +inf purchased time) from throwing
    // mid-send and silently killing the send loop / Blazor circuit (GS-H7).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new NonFiniteDoubleConverter() },
    };

    /// <summary>
    /// Cross-site WebSocket hijacking guard. A WebSocket is not subject to CORS, so without this any web page a user on
    /// the trusted network visited could open <c>/ws/sim</c> from the user's browser, drive sessions and read the
    /// frames. A browser always sends <c>Origin</c> on a WebSocket upgrade; it must name this host (scheme-agnostic,
    /// host and port as the request's <c>Host</c>) or one of <c>GridSim:AllowedOrigins</c> (comma-separated origins,
    /// for example <c>https://ops.example.com</c>). A request with no <c>Origin</c> is not from a browser page (a script
    /// or a native client, which the hijacking attack cannot use) and is allowed. Public for the tests.
    /// </summary>
    public static bool OriginAllowed(string? origin, string? requestHost, string? allowedOriginsSetting)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? o)) return false;
        string originHostPort = o.IsDefaultPort ? o.Host : $"{o.Host}:{o.Port}";
        if (!string.IsNullOrEmpty(requestHost)
            && (string.Equals(originHostPort, requestHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Authority, requestHost, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (string.IsNullOrWhiteSpace(allowedOriginsSetting)) return false;
        foreach (string allowed in allowedOriginsSetting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Uri.TryCreate(allowed, UriKind.Absolute, out Uri? a)
                && string.Equals(a.Scheme, o.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Authority, o.Authority, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // GR-721: a dispatch failure is logged in full on the server (the client gets the path-scrubbed text).
    private static readonly Action<ILogger, string, Exception?> LogDispatchFailure = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1721, "SimWebSocketDispatchFailed"), "/ws/sim control frame '{Action}' failed");

    public static async Task HandleAsync(HttpContext ctx, SessionManager manager)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

        string? allowedOrigins = ctx.RequestServices.GetService<IConfiguration>()?["GridSim:AllowedOrigins"];
        if (!OriginAllowed(ctx.Request.Headers.Origin.ToString(), ctx.Request.Host.Value, allowedOrigins))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("WebSocket upgrade refused: the page's Origin is not this host or one of GridSim:AllowedOrigins.");
            return;
        }

        using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
        ILogger log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GridSim.Web.Api.SimWebSocket");
        CancellationToken ct = ctx.RequestAborted;

        // Frames flow session -> channel -> socket, so the session's loop never blocks on a slow client.
        // The bounded DropOldest channel may drop state frames under backpressure, but the one-time
        // topology frame is held in a priority slot (SimOutbound) that the send loop delivers first and
        // never drops - preserving the topology-before-state contract even for a stalled client, and again
        // for every later 'start' on the same socket.
        var outbound = new SimOutbound();

        // The connection's current session, held in a slot Dispatch can clear: when a 'start' disposes the old session
        // and then fails (unknown case, refused source), the slot is emptied before the error is raised, so the next
        // control frame finds "no session" instead of driving the disposed one.
        var slot = new SessionSlot();
        // N022: the per-frame ASSETS graph (every bus, branch and generator with their electrical fields) is the
        // largest block of a state frame and only the dashboard's ASSETS explorer reads it, so an external consumer
        // gets it only when its start frame asks ("assetGraph": true). The flag is per start. N154: the EXPERIMENTAL
        // wave field likewise ("waveField": true; see FrameShape.IncludeWaveField).
        var shape = new FrameShape();
        void OnFrame(FrameEnvelope f) => outbound.TryWrite(shape.Apply(f));

        Task sendLoop = SendLoopAsync(ws, outbound, ct);
        try
        {
            var buffer = new byte[16 * 1024];
            const int maxControlBytes = 16 * 1024;   // control frames are ~100 B; anything bigger is abuse
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                using var ms = new MemoryStream();
                do
                {
                    res = await ws.ReceiveAsync(buffer, ct);
                    if (res.MessageType == WebSocketMessageType.Close) goto done;
                    ms.Write(buffer, 0, res.Count);
                    if (ms.Length > maxControlBytes)
                    {
                        // Kestrel's MaxRequestBodySize does not apply to upgraded sockets, so an
                        // unbounded fragmented message could otherwise grow the stream to OOM.
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                            "control frame too large", CancellationToken.None);
                        goto done;
                    }
                } while (!res.EndOfMessage);

                // D-DOC-178: a frame the server cannot act on is answered with an error frame, never dropped in silence.
                // Before, a binary frame, text that is not JSON, JSON that is not an object (or is null) and an unknown
                // action were all ignored, so a client with a typo waited for a reply that never came.
                if (res.MessageType != WebSocketMessageType.Text)
                {
                    outbound.TryWriteError(new ErrorFrame("error", "control frames must be text (UTF-8 JSON); a binary frame was ignored."));
                    continue;
                }
                Control? cmd = Deserialize(ms.ToArray(), out string? parseError);
                if (cmd is null)
                {
                    outbound.TryWriteError(new ErrorFrame("error", parseError ?? "malformed control frame."));
                    continue;
                }

                // A dispatch failure (unknown case, bridge unconfigured, corrupt run dir, unknown action, a control
                // before 'start') must surface as a structured error frame, not abort the socket (GS-H6).
                // GR-721: the client gets the message with any server path replaced (ClientErrorText); the full message
                // and exception go to the server log.
                try { await Dispatch(cmd, slot, manager, OnFrame, outbound, shape); }
                catch (Exception ex)
                {
                    LogDispatchFailure(log, cmd.Action ?? "", ex);
                    outbound.TryWriteError(new ErrorFrame("error", ClientErrorText.For(ex)));
                }
            }
            done: ;
        }
        catch (OperationCanceledException) { /* client went away */ }
        catch (WebSocketException) { /* abrupt close */ }
        finally
        {
            outbound.Complete();
            if (slot.Current is { } session) { session.FrameProduced -= OnFrame; await session.DisposeAsync(); }
            try { await sendLoop; } catch { /* already tearing down */ }
            await CompleteCloseAsync(ws);
        }
    }

    /// <summary>How long the handler waits to put its own close frame on the wire before giving up on a client that
    /// has stopped reading. A close frame is a few bytes; this only bounds a stalled peer.</summary>
    private static readonly TimeSpan CloseSendTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// N088: finish the WebSocket close handshake (RFC 6455 section 5.5.1) whichever side started it.
    /// <list type="bullet">
    /// <item><description>Client closed first (state <c>CloseReceived</c>): before N088 the handler simply returned,
    /// so the client never got the answering close frame and saw the connection drop mid-handshake. Now the handler
    /// echoes the client's own status code (RFC 6455 5.5.1: "the endpoint typically echos the status code it
    /// received"), or NormalClosure when the client sent none.</description></item>
    /// <item><description>Socket still open (the server is ending the session, e.g. on shutdown): send NormalClosure
    /// and wait for the client's reply, as before.</description></item>
    /// <item><description>Already closed or aborted (MessageTooBig close sent, abrupt disconnect): nothing to do.</description></item>
    /// </list>
    /// A peer that vanished mid-close raises a WebSocketException or a timeout; that is the end of the connection
    /// either way, so it is swallowed here (the handler is already tearing down).
    /// </summary>
    private static async Task CompleteCloseAsync(WebSocket ws)
    {
        using var timeout = new CancellationTokenSource(CloseSendTimeout);
        try
        {
            if (ws.State == WebSocketState.CloseReceived)
            {
                WebSocketCloseStatus status = ws.CloseStatus ?? WebSocketCloseStatus.Empty;
                string? description = ws.CloseStatusDescription;
                // 1005 (Empty: "no status received") is a local marker that must never be sent on the wire
                // (RFC 6455 7.4.1), and a reason text needs a status code, so a status-less close is answered
                // with NormalClosure and no text.
                if (status == WebSocketCloseStatus.Empty) { status = WebSocketCloseStatus.NormalClosure; description = null; }
                await ws.CloseOutputAsync(status, description, timeout.Token);
            }
            else if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
        }
        catch (WebSocketException) { /* peer gone mid-close */ }
        catch (OperationCanceledException) { /* peer stopped reading; give up after CloseSendTimeout */ }
    }

    /// <summary>
    /// N022 / N154: what a connection wants in its state frames. <see cref="IncludeAssetGraph"/> and
    /// <see cref="IncludeWaveField"/> are set by each <c>start</c> (default false: the frame's <c>assetGraph</c> is null,
    /// and so is <c>experimental.waveField</c>).
    /// </summary>
    internal sealed class FrameShape
    {
        public bool IncludeAssetGraph { get; set; }

        /// <summary>
        /// N154 (Ruling R1, Mark delegated 2026-10-04): the EXPERIMENTAL per-bus wave field (the map's WAVE animation,
        /// 229,908 bytes of JSON per frame on gb-full, the same tick after tick) is sent only when the start frame asks
        /// (<c>"waveField": true</c>), exactly as <c>assetGraph</c> already is. A documented wire change: before, every
        /// /ws/sim state frame carried it. The in-app dashboard does not read /ws/sim (the Blazor map gets the field
        /// from the session directly), so nothing on screen changes.
        /// </summary>
        public bool IncludeWaveField { get; set; }

        /// <summary>The frame as this connection receives it (the same instance when nothing is removed). Every other
        /// field is passed through untouched; the rest of the EXPERIMENTAL block (reciprocity, the dominant wave
        /// frequency and split, traced flows, carbon) is kept.</summary>
        public FrameEnvelope Apply(FrameEnvelope f)
        {
            bool dropGraph = !IncludeAssetGraph && f.AssetGraph is not null;
            bool dropWave = !IncludeWaveField && f.Experimental?.WaveField is not null;
            if (!dropGraph && !dropWave) return f;
            return f with
            {
                AssetGraph = dropGraph ? null : f.AssetGraph,
                Experimental = dropWave ? f.Experimental! with { WaveField = null } : f.Experimental,
            };
        }
    }

    /// <summary>The connection's current session (null before the first successful 'start' and after a failed one).</summary>
    private sealed class SessionSlot
    {
        public SimSession? Current { get; set; }
    }

    /// <summary>The control actions /ws/sim understands, in the order the unknown-action error lists them.</summary>
    internal static readonly IReadOnlyList<string> Actions = new[] { "start", "play", "pause", "step", "seek", "speed", "trip" };

    /// <summary>The feed names a 'start' frame's optional <c>source</c> may carry (D-DOC-172).</summary>
    internal static readonly IReadOnlyList<string> Sources = new[] { "live", "replay", "bridge" };

    private static async Task Dispatch(Control cmd, SessionSlot slot, SessionManager manager,
        Action<FrameEnvelope> onFrame, SimOutbound outbound, FrameShape shape)
    {
        string action = (cmd.Action ?? "").Trim().ToLowerInvariant();
        // D-DOC-178: no action, or one the server does not know, is refused by name (it used to fall to a silent default).
        if (action.Length == 0)
            throw new ArgumentException($"control frame has no action; use one of: {string.Join(", ", Actions)}.");
        if (!Actions.Contains(action))
            throw new ArgumentException($"unknown action '{cmd.Action}'; use one of: {string.Join(", ", Actions)}.");

        if (action == "start")
        {
            // Dispose the old session and EMPTY the slot before anything that can throw (blank case, refused source,
            // or Create throwing on an unknown case / unconfigured bridge), so the connection never keeps driving, or
            // disposes a second time, a session that is already gone.
            if (slot.Current is { } old) { slot.Current = null; old.FrameProduced -= onFrame; await old.DisposeAsync(); }
            // Open a new generation on the socket (R050): the topology slot is re-armed for this start, and
            // any frame the replaced session left in the queue is dropped instead of reaching the client
            // after the new topology. Done before any early return so a failed start's error frame belongs
            // to the new generation and is still delivered.
            long generation = outbound.BeginSession();
            shape.IncludeAssetGraph = cmd.AssetGraph == true;   // N022: opt-in, per start
            shape.IncludeWaveField = cmd.WaveField == true;     // N154: opt-in, per start, like assetGraph
            // D-DOC-178: a start with no case used to return quietly with no session and no reply.
            if (string.IsNullOrWhiteSpace(cmd.Case))
                throw new ArgumentException("'start' needs a case, for example {\"action\":\"start\",\"case\":\"case9\"}.");
            // D-DOC-172: the documented 'source' field used to be read and ignored. The case id decides the feed; a
            // source that is given must now name that same feed, so a client asking for a replay of a live case (or a
            // typo) is told, instead of silently getting something else.
            CheckSource(cmd.Source, cmd.Case, manager);
            // GR-734: a start's fps, when given, must be in range (it used to be clamped, and 0 or a negative was ignored).
            double? startFps = cmd.Fps is { } askedFps ? CheckFps(askedFps, "start") : null;
            // N063: an optional "solver" (auto | dense | sparse | sweep) picks a live session's engine; an
            // unknown name or a solver for a replay / bridge feed fails loud as an error frame.
            SimSession session = manager.Create(cmd.Case, SolverChoice.Parse(cmd.Solver));
            slot.Current = session;   // held at once, so even a failure below leaves it to be disposed with the socket
            session.FrameProduced += onFrame;
            if (startFps is { } startFpsValue) session.SetSpeed(startFpsValue);
            if (session.TopologyReady)
                outbound.SetTopology(session.Topology, generation);    // live/replay: topology known now
            else
                WireTopologyOnArrival(session, outbound, generation);  // bridge: server sends it after connect
            if (session.Current is not null) outbound.TryWrite(shape.Apply(session.Current)); // initial state
            return;
        }

        // D-DOC-178: every other action drives an existing session; before 'start' (or after a failed one) it used to
        // be a silent no-op.
        SimSession current = slot.Current
            ?? throw new InvalidOperationException($"'{action}' has no session to act on: send a 'start' frame first.");
        switch (action)
        {
            case "play": current.Play(); return;
            case "pause": current.Pause(); return;
            case "step":
                // GR-220: a bridge session is server-driven, so a step used to do nothing and send nothing. Refused.
                if (current.IsBridge)
                    throw new InvalidOperationException("step is not available on a bridge session: its frames are driven by the bridge server.");
                current.StepOnce();
                return;
            case "seek":
            {
                // GR-220: only a replay has a run to seek through; on a live or bridge session a seek used to be a silent
                // no-op. The value is checked first, so a seek with no value is still told that.
                double fraction = RequireValue(cmd, action, "a fraction of the run from 0 to 1");
                if (!current.IsReplay)
                    throw new InvalidOperationException("seek is only available on a replay session: a live or bridge feed has no recorded run to seek through.");
                current.SeekFraction(fraction);
                return;
            }
            case "speed": current.SetSpeed(CheckFps(RequireValue(cmd, action, "frames per second"), "speed")); return;
            case "trip":
                // A trip only reaches a live in-process sim. On a replay/bridge session it used to be a silent
                // no-op; now it fails loud, and the caller's catch turns this into a structured error frame (R303).
                if (!current.CanTrip)
                    throw new InvalidOperationException(
                        "trip is not available on this session: only a live in-process feed can be tripped (replay and bridge frames are not simulated here).");
                current.Trip(RequireValue(cmd, action, "the MW to trip"));
                return;
        }
    }

    /// <summary>
    /// GR-734 (October 2026): a frame rate outside <see cref="PresentationLimits.MinFps"/>..<see cref="PresentationLimits.MaxFps"/>
    /// (0.1 to 60 fps) is refused with an error frame naming the range. It used to be clamped in silence, so a client that
    /// asked for 120 fps ran at 60 and a client that asked for 0.01 ran at 0.1 with no word. The session still clamps (the
    /// Blazor transport bar can only offer in-range values); this check is the wire's.
    /// </summary>
    internal static double CheckFps(double fps, string action)
    {
        if (double.IsFinite(fps) && fps >= PresentationLimits.MinFps && fps <= PresentationLimits.MaxFps) return fps;
        throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"'{action}' fps {fps} is out of range: give a frame rate from {PresentationLimits.MinFps} to {PresentationLimits.MaxFps}."));
    }

    /// <summary>D-DOC-178: seek, speed and trip need a finite numeric <c>value</c>; without one they used to do nothing.</summary>
    private static double RequireValue(Control cmd, string action, string meaning)
    {
        if (cmd.Value is { } v && double.IsFinite(v)) return v;
        throw new ArgumentException($"'{action}' needs a numeric value ({meaning}).");
    }

    /// <summary>
    /// D-DOC-172: an optional <c>source</c> on 'start' must be live, replay or bridge and must match the feed the case
    /// id opens (<see cref="SessionManager.FeedOf"/>). Absent or blank: nothing to check. An unknown case is left for
    /// <see cref="SessionManager.Create"/> to refuse with its own message.
    /// </summary>
    private static void CheckSource(string? source, string caseId, SessionManager manager)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        string s = source.Trim().ToLowerInvariant();
        if (!Sources.Contains(s))
            throw new ArgumentException($"unknown source '{source}'; use one of: {string.Join(", ", Sources)} (or leave it out: the case id decides the feed).");
        string? feed = manager.FeedOf(caseId);
        if (feed is not null && feed != s)
            throw new ArgumentException($"'start' asks for source '{s}' but '{caseId}' is a {feed} feed; leave source out or set it to '{feed}'.");
    }

    /// <summary>
    /// Bridge sessions start with an empty topology - the server sends it after connect. Push the real
    /// topology to the client exactly once, when it arrives, so consumers never get <c>state</c> frames for
    /// a network they have no topology for (GS-H6). The subscription lives with the session and is collected
    /// when it is disposed. Everything it writes is tagged with the start's <paramref name="generation"/>, so a
    /// late callback from a session that has since been replaced is ignored rather than mixed into the new one.
    /// </summary>
    private static void WireTopologyOnArrival(SimSession session, SimOutbound outbound, long generation)
    {
        bool sent = false;
        bool errored = false;
        void OnState()
        {
            // Bridge connect failures / later faults only set session.Error and raise
            // StateChanged - without forwarding them here an external /ws/sim consumer
            // hung silently forever despite the GS-H6 error-frame design.
            if (!errored && session.Error is { } err)
            {
                errored = true;
                outbound.TryWriteError(new ErrorFrame("error", err), generation);
            }
            if (sent || !session.TopologyReady) return;
            sent = true;
            outbound.SetTopology(session.Topology, generation);
        }
        session.StateChanged += OnState;
    }

    private static async Task SendLoopAsync(WebSocket ws, SimOutbound outbound, CancellationToken ct)
    {
        async Task Send(object msg)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        try
        {
            await foreach (SimOutbound.Item item in outbound.Reader.ReadAllAsync(ct))
            {
                // The queue decides the wire order: the current start's topology (once) ahead of its first
                // frame, so a state frame never reaches the client before the topology it describes, and
                // nothing at all for a frame left over from a replaced session.
                foreach (object msg in outbound.PrepareSend(item)) await Send(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// <summary>
    /// Read one control frame. Null with <paramref name="error"/> set when the bytes are not a JSON object (D-DOC-178:
    /// the caller answers with an error frame; before, the frame was dropped without a word). JSON <c>null</c> is
    /// refused too: it deserialises to no object at all.
    /// </summary>
    private static Control? Deserialize(byte[] utf8, out string? error)
    {
        try
        {
            // N227 (October 2026): a property the control frame does not define is refused by name. It used to be
            // dropped without a word, so a misspelt field ("assetgrph": true, "fsp": 5) silently fell back to its default
            // and the client never learned why. Names match as the deserialiser matches them (the Web defaults: camelCase,
            // case-insensitive), so every frame that worked before is accepted unchanged.
            using (JsonDocument doc = JsonDocument.Parse(utf8))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var unknown = doc.RootElement.EnumerateObject()
                        .Select(p => p.Name)
                        .Where(n => !ControlFields.Contains(n))
                        .ToList();
                    if (unknown.Count > 0)
                    {
                        error = $"malformed control frame: unknown propert{(unknown.Count == 1 ? "y" : "ies")} " +
                            $"{string.Join(", ", unknown.Select(n => "'" + n + "'"))}; a control frame has only " +
                            $"{string.Join(", ", ControlFieldsInOrder)}.";
                        return null;
                    }
                }
            }
            Control? c = JsonSerializer.Deserialize<Control>(utf8, Json);
            error = c is null ? "malformed control frame: expected a JSON object such as {\"action\":\"start\",\"case\":\"case9\"}, got null." : null;
            return c;
        }
        catch (JsonException ex)
        {
            // The parser's own text says where it stopped (line and byte position); the frame itself is not echoed.
            error = $"malformed control frame: expected a JSON object such as {{\"action\":\"start\",\"case\":\"case9\"}} ({ex.Message})";
            return null;
        }
    }

    /// <summary>N227: the control frame's wire property names (camelCase, as the Web serializer defaults write them),
    /// taken from <see cref="Control"/>'s own constructor so the list cannot drift from the record.</summary>
    internal static readonly IReadOnlyList<string> ControlFieldsInOrder = typeof(Control).GetConstructors()
        .OrderByDescending(ci => ci.GetParameters().Length).First().GetParameters()
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name!)).ToList();

    /// <summary>N227: <see cref="ControlFieldsInOrder"/> as a case-insensitive set (the Web defaults read property names
    /// case-insensitively, so "Action" has always meant "action").</summary>
    private static readonly HashSet<string> ControlFields = new(ControlFieldsInOrder, StringComparer.OrdinalIgnoreCase);

    /// <summary>A client control frame.</summary>
    private sealed record Control(string? Action, string? Case, string? Source, double? Fps, double? Value,
        string? Solver = null, bool? AssetGraph = null, bool? WaveField = null);

    /// <summary>A structured error frame (<c>type:"error"</c>) sent when a control dispatch fails (GS-H6).</summary>
    private sealed record ErrorFrame(string Type, string Message);

}
