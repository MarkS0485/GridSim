# 24. The GDA bridge

The bridge is a small server in the GDA repository that streams reconstructed GB network state, and answers on-demand
physics queries, over one WebSocket. GDA computes and stores; GridSim renders. The desktop app and the web console are
its clients, and so is any program that speaks the JSON protocol. This chapter covers starting the server, checking
it, connecting from the apps, the ZONE, FROM and SRC pickers and the protocol fields behind them, the stream controls
and queries, and the faults you meet and what each one means. The protocol itself, message by message, is in
[ch. 9, GDA integration](<../technical/0009 GDA integration.md>).

## Before you start

- **The GDA repository** with its lake under `DataSources/` and the case directories under `NetworkModel/`. The server
  finds both beside `Applications/` in the `v1` directory of the repository, from the script's own location, so run it
  from a checkout, not a copy of the script. It reads no environment variable for the lake; a lake kept elsewhere must
  be reachable at `v1/DataSources`, for example through a link.
- **A Python environment** with `fastapi`, `uvicorn`, `pandas`, `numpy`, `scipy` and `pyarrow`. `uvicorn` serves
  WebSocket routes only when `websockets` or `wsproto` is installed; `uvicorn[standard]` installs `websockets`. The
  probe client below also needs `websockets`.
- **Built lakes for the sources you want.** `live` needs the reconstructed scalar tables, `presolved` the presolved
  lake and `estimated` the estimated lake ([ch. 22](<0022 Estimating GB history.md>)).

Timings and timestamps in the captures differ by machine and run, and the horizon cutoffs move with the clock.

## Worked example

Start the server from the `v1` directory of the GDA repository:

```bash
python Applications/GridSim-Bridge/bridge_server.py
```

```
$ python Applications/GridSim-Bridge/bridge_server.py
INFO:     Started server process [4296]
INFO:     Waiting for application startup.
INFO:     Application startup complete.
INFO:     Uvicorn running on http://127.0.0.1:8770 (Press CTRL+C to quit)
[server left running]
```

The server binds `127.0.0.1:8770`. `BRIDGE_HOST` and `BRIDGE_PORT` override the address and port. Check it with two
plain HTTP requests:

```bash
curl http://127.0.0.1:8770/
curl http://127.0.0.1:8770/meta
```

```
$ curl http://127.0.0.1:8770/
{"app":"GridSim-Bridge","ws":"/ws/bridge","cases":["gb-full","gb-spine","zone-enwl","zone-nged","zone-npg","zone-spen","zone-ssen","zone-ukpn"],"horizonCutoffUtc":"2026-09-26T11:38:34.302823+00:00"}
[exit 0]
```

```
$ curl http://127.0.0.1:8770/meta
{"cases":["gb-full","gb-spine","zone-enwl","zone-nged","zone-npg","zone-spen","zone-ssen","zone-ukpn"],"scalar_min_timestamp":"2019-02-01T00:30:00+00:00","scalar_max_timestamp":"2026-08-22T23:00:00+00:00","frequency_min_timestamp":"2014-01-01T00:00:00.500000+00:00","frequency_max_timestamp":"2026-08-31T23:59:58.500000+00:00","horizon_cutoff_utc":"2026-09-26T11:38:34.381192+00:00"}
[exit 0]
```

Now stream estimated frames. [The probe client](#watching-the-stream-yourself) sends one JSON action over
`ws://127.0.0.1:8770/ws/bridge` and prints each message:

```bash
python bridge_probe.py '{"action":"start","case":"gb-spine","start":"2026-09-01T10:00:00Z","fps":144,"source":"estimated"}' 3
```

```
$ python bridge_probe.py '{"action":"start","case":"gb-spine","start":"2026-09-01T10:00:00Z","fps":144,"source":"estimated"}' 3
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
topology  case=gb-spine  buses=316  branches=579
info      {"message": "Replaying gb-spine from estimated lake: 1 stored frames 2026-09-01T11:00:00+00:00 -> 2026-09-01T11:00:00+00:00 at 144 FPS"}
state     t=2026-09-01T11:00:00+00:00  source=estimated  buses=316
          estimate={"objectiveJ": 225.414916686831, "chiThreshold": 359.5207249007415, "dof": 317, "measurementsN": 948, "removedN": 0, "iterations": 8, "chiPassed": true}
done      {"framesSent": 1, "source": "estimated"}
[exit 0]
```

## Reading the output

**The server.** One `info` message greets every connection and lists the cases. The cases are `gb-full`, `gb-spine`
and every `NetworkModel/zones/zone-*` directory, found when the server starts.

**`GET /`** returns the app name, the WebSocket path, the cases and `horizonCutoffUtc`, the newest instant the server
will stream (now minus 169 hours). **`GET /meta`** adds the time range of the reconstructed scalar tables
(`scalar_min_timestamp`, `scalar_max_timestamp`, the range the `live` source and the `inertia` and `condition` queries
can serve) and of the 1-second frequency table the `raw` source uses (`frequency_min_timestamp`,
`frequency_max_timestamp`). On the example lake the scalars end on 2026-08-22, so a `live` stream cannot reach later.

**The stream.** One `start` produces, in order:

1. `topology`, once: the case's network (the `gridsim-solve-export/1` model block, with ETYS `boundaries` when the case
   has them). The client caches it for every tick; here 316 buses and 579 branches.
2. Zero or more `info` messages: advisories such as the replay window and any fallback.
3. One `state` per settlement period: `t` (ISO-8601 UTC), the slim `result` (each bus's voltage magnitude and angle and
   injections, each line's flows), `scalars` (frequency, rate of change of frequency, generation, demand, inertia,
   condition index, RoCoF headroom), `overlays` (branch stress, bus condition), the `operator` and `market` series for
   that period, `source`, and on an estimated frame the `estimate` block.
4. `done` with `framesSent`, or `error`.

**The `estimate` block** is the fit quality of that period: `objectiveJ` against `chiThreshold` for `dof` degrees of
freedom, `chiPassed` as the verdict, `measurementsN` consumed, `removedN` removed as bad data and `iterations`. It
mirrors the lake row columns ([ch. 22](<0022 Estimating GB history.md>)). The desktop pill shows it
([ch. 23](<0023 Replaying estimated states.md>)).

**The sources.** `source` on `start` and `seek` selects the reconstruction. An absent or unrecognised value selects
`live`.

| Source | What each tick is | What it needs |
|---|---|---|
| `live` | The case solved once; only the system scalars change per tick. Frames carry no `source` field. | The case directory, and the tables `Derived/inertia_reconstructed`, `Derived/system_condition_index` and `Derived/operator_market_frames` |
| `presolved` | A stored demand-scaled re-solve of the period: per-bus state varies tick to tick. | `Derived/presolved_state/<case>/topology.json` and the window's weekly partitions |
| `estimated` | A stored measurement-fitted estimate of the period, with its `estimate` block. | `Derived/estimated_state/<case>/...` |
| `solves` | The stored GDA-SOLVES library, `gb-spine` only, rebuilt per period from stored per-bus states, with an `estimate` block. | The `GDA-SOLVES` directory beside the repository's `v1` folder |
| `raw` | Public data only, no solve: generation mix, weather and interconnector blocks on a 1-second clock. The web console uses it. | The raw lake tables |

A missing scalar, operator or market table is read as empty, without a message, and the blocks it feeds are empty.
Every stored frame takes its `scalars`, `operator` and `market` blocks from those tables, and an estimated frame's
scalars come from them.

A `presolved` or `estimated` start with an instant falls back to the live stream, and says so in an `info` message,
when the lake has no `topology.json` for the case or holds no frame in the window; that view loses the per-bus replay.
The other sources do not fall back. `first` or `earliest` on a stored source with no frames, and `solves` for another
case or without a library, send an `info` and start no stream (see [Faults](#faults)). `raw` has no fallback.

## The pickers and the protocol

The desktop pickers set the fields of the `start` action. The web console sets them from its address
([ch. 23](<0023 Replaying estimated states.md>)).

| App control | Action field | Value |
|---|---|---|
| ZONE | `case` | `zone-nged`, `zone-ukpn`, `zone-ssen`, `zone-spen`, `zone-npg`, `zone-enwl`, `gb-full` or `gb-spine` |
| FROM | `start` | An ISO-8601 UTC instant: now minus 9, 12, 14, 21, 30 or 60 days |
| SRC | `source` | `presolved`, `estimated` or `live` |
| (fixed) | `fps` | 2 on the desktop |

Every change of a picker sends a new `start` on the open connection. The server stops the running stream and starts
the new one. The web console, for a stored source (`presolved`, `estimated`, `solves`), sends `start` with the literal
`first` (the case's earliest stored frame) and `loop` true, so the replay wraps; for `live` it sends an instant 169 hours
plus 2 days before now; for `raw` it sends 1 January of the current year at 00:00 UTC, clamped into the range that
`GET /meta` reports for the 1-second table.

## Variations

### Connect from the apps

**Desktop.** Set `BridgeEndpoint` in `%LOCALAPPDATA%\GridSim\settings.json` to `ws://127.0.0.1:8770/ws/bridge` (with
`"BridgeAllowUnencrypted": true`, without which the client refuses a `ws://` address) for a
local server, then pick **Live bridge** as the model source. The default is the public endpoint
`wss://simstream.twinscrollgridbalancer.co.uk/ws/bridge`; blank hides the entry.

**Web.** Set `GridSim:BridgeEndpoint` to the same address. `/dashboard/bridge:<case>[@<source>]` opens a session, and
without that key every bridge address fails with `No GDA bridge endpoint is configured (GridSim:BridgeEndpoint).`
[Ch. 3](<0003 Configuration.md>) lists both settings.

### Sessions and stream controls

A connection owns one stream at a time, a current case (initially `zone-nged`, or the first sorted case when it is
absent), a frame rate and a loop flag. The client sends JSON actions:

| Action | Fields | Effect |
|---|---|---|
| `start` | `case`, `start`, `fps`, `source`, optional `loop` | Stop any running stream and start `case` at `start`. `start` must be a string. It is an ISO-8601 instant or, for the stored sources, `first` or `earliest`. `fps` is clamped to 1 to 144; an omitted `fps` keeps the connection's current rate, 4 on a new connection, and an `fps` that is not an integer is ignored. `loop` defaults to false. |
| `stop` | none | End the running stream, which still finishes with `done`. The reply is `info` `Stop requested`, or `No active stream`. |
| `set_speed` | `fps` | Change the frame rate without restarting. |
| `seek` | `start`, optional `case`, `source` | Restart at a new instant on the same connection. An omitted `source` means `live`, not the running source, and `fps` and `loop` keep their current values. The reply is `info` `Seeking to <start>`. |
| `query` | `op`, `at`, `params`, `id` | Run physics at a historical instant, alongside the stream. |

A message that is not JSON, an unknown action and a `start` without a string `start` each get an `info` reply
(`Invalid JSON command`, `Unknown action: <name>`, `Missing start timestamp`) and change nothing. A `presolved` or
`estimated` stream covers at most 35 days; when the window is longer the server sends an `info` message naming the
capped window and ends the stream at the cap.

Each connection caches its topology, so the client fills asset names and ratings from it for every tick. All timestamps
are ISO-8601 UTC. One message that cannot be read is skipped and counted (`GridBridgeClient.MessageRejected`,
`RejectedMessages`); the stream carries on. The desktop writes it to the event log and the web console shows
`Bridge frame rejected (N so far)`. A clean close from the server fails every pending query and raises the fault
`The bridge closed the connection.`

### On-demand queries

```json
{"action":"query","op":"inertia","at":"2026-09-01T11:00:00Z","params":{},"id":"q1"}
```

The server replies with one `result` that echoes `op`, `id` and `at`. Replies are matched to requests by `id` only,
so queries that finish out of order are safe, and a query never stops the stream. GridSim ignores, and logs as a
rejected message, a reply with no `id` or with an `id` it is not waiting for; it never guesses the query from the `op`. The ops:

| Op | Returns |
|---|---|
| `inertia` | The reconstructed system-inertia scalars at `at`. |
| `condition` | The system-condition index at `at`. |
| `scenario` | A what-if solve (trip or scale) by the GDA sparse Newton and tap-changer engine. |
| `feasibility` | A limit check of a proposed state. |
| `post_event` | A post-disturbance replay around an event. |
| `asset` | A per-asset drill-down: envelopes, storage, tap changers, consumers. |
| `propagation` | Corridor propagation delays from the lake. |

`params.case` names the network a physics query runs on; without it the query uses the connection's current case. A
`params.case` that is not a listed case falls back to `gb-full` without a message.
The client's `GridBridgeClient.Query` times out after 60 seconds with a `TimeoutException`.

In bridge mode the desktop app turns a right-click into a query at the stream's start instant. A bus offers
`Feasibility: +500 MW here  (<bus name>)` and `Scenario: trip nearest generator to <bus name>`; a branch offers
`Scenario: trip this  (<branch name>)`. The pill reads `QUERY` while it runs and `WHAT-IF  *  <verdict>` once the result
is on the map; **Return to live stream** clears it.

Three replies show the verdicts you meet on this lake:

```
$ python bridge_probe.py '{"action":"query","op":"inertia","at":"2026-09-01T11:00:00Z","id":"q1"}' 1
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
result    {"op": "inertia", "id": "q1", "at": "2026-09-01T11:00:00Z", "payload": {"verdict": "NO_DATA", "scalars": {}, "violations": []}}
[exit 0]
```

`NO_DATA` means the lake holds no scalar for that instant (the scalar tables end on 2026-08-22).

```
$ python bridge_probe.py '{"action":"query","op":"inertia","at":"2026-10-02T11:00:00Z","id":"q2"}' 1
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
result    {"op": "inertia", "id": "q2", "at": "2026-10-02T11:00:00Z", "payload": {"verdict": "INFEASIBLE_HORIZON", "violations": []}}
[exit 0]
```

`INFEASIBLE_HORIZON` means `at` is newer than the horizon cutoff, and no engine ran.

```
$ python bridge_probe.py '{"action":"query","op":"nope","at":"2026-09-01T11:00:00Z","id":"q3"}' 1
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
result    {"op": "nope", "id": "q3", "at": "2026-09-01T11:00:00Z", "payload": {"verdict": "ERROR", "violations": [], "bindingConstraint": "unknown op 'nope'. Available: ['asset', 'condition', 'feasibility', 'inertia', 'post_event', 'propagation', 'scenario']"}}
[exit 0]
```

An `ERROR` verdict arrives on a normal `result`, so a failed query never faults the stream. An unparseable `at` also
returns `ERROR`.

### Watching the stream yourself

This client is all the examples in this chapter use. Save it as `bridge_probe.py`, install `websockets`, and pass one
JSON action and the number of `state` frames to wait for:

```python
import asyncio, json, sys, websockets

async def main(action, want):
    async with websockets.connect("ws://127.0.0.1:8770/ws/bridge", max_size=None) as ws:
        await ws.send(action)
        states = 0
        while True:
            m = json.loads(await asyncio.wait_for(ws.recv(), 120))
            kind = m.pop("type")
            if kind == "topology":
                print(f"topology  case={m['case']}  buses={len(m['model']['buses'])}  branches={len(m['model']['branches'])}")
            elif kind == "state":
                print(f"state     t={m['t']}  source={m.get('source')}  buses={len(m['result']['buses'])}")
                if "estimate" in m:
                    print("          estimate=" + json.dumps(m["estimate"]))
                states += 1
            else:
                print(f"{kind:<9} " + json.dumps(m))
            if kind in ("error", "done", "result") or states >= want:
                break

asyncio.run(main(sys.argv[1], int(sys.argv[2])))
```

Every stream ends with `done` after its last period, including a stream you stopped. The `done` of a stored source
carries `source`; the `done` of a `live` or `raw` stream does not. The probe also stops when it has printed the number
of `state` frames you asked for.

## The 169-hour horizon

The bridge replays history and never nowcasts. The rule is one number, 169 hours (7 days and 1 hour), enforced on
both ends independently. A stamp is too new when it is later than now minus 169 hours; a stamp exactly at that instant
or older is accepted.

- **Server.** A `start` or `seek` with an instant newer than the cutoff is rejected with an `error` for the `live`,
  `presolved`, `estimated` and `solves` sources. For `presolved` and `estimated` the check applies to the resolved
  instant, so `first` is rejected when the earliest stored frame is newer than the cutoff; `solves` with `first` skips
  the check and skips frames newer than the cutoff. `raw` never rejects: a start newer than its newest 1-second sample is
  moved to 2 hours before that sample, with an `info`. No tick newer than the cutoff is sent; a stream that reaches one
  ends, and its `done` follows. A query `at` newer than the cutoff returns `INFEASIBLE_HORIZON`.
- **The `live` window.** Ticks run every 30 minutes from the next half hour at or after `start` to the earlier of the
  cutoff and the newest scalar record (`scalar_max_timestamp`). When `start` is later than that record, the server moves
  the start to 7 days before the record, no earlier than the oldest scalar record, and sends the `info` `Requested start
  is past available scalar data; clamped to ...`. A `live` stream has no 35-day cap and ends with `done` after the last
  period.
- **Client.** `GridBridgeClient` applies the same rule to every received `state` tick before it builds a frame, against
  its own clock, with no clock-skew allowance. A tick newer than now minus 169 hours is a protocol violation: the
  receive loop ends, every pending query fails and the client raises `Faulted` (`HORIZON REJECTION` in the desktop
  pill). Replies to a later `start` on the same socket are not read, so recovery needs a new connection. A tick with no
  timestamp is rejected and counted like any other unreadable message, and the stream carries on. A tick at or older than
  the horizon is accepted.

Pick starts at least 8 days back. Every desktop preset already is. If you drive the socket yourself, read the cutoff
from `GET /meta`.

## Faults

A stream-level failure arrives as `{"type":"error","message":...}`, and `GridBridgeClient` raises it as a fault
carrying the server's message. Advisories arrive as `info` and `done` closes a stream; the client ignores both, so
they never fault anything.

```
$ python bridge_probe.py '{"action":"start","case":"gb-spine","start":"2026-10-02T00:00:00Z","fps":144,"source":"estimated"}' 3
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
error     {"message": "start 2026-10-02T00:00:00+00:00 is past the no-forward-inference horizon (cutoff 2026-09-26T11:35:06.513504+00:00, now - 7d 1h). Rejected."}
[exit 0]
```

| Message | Type | Cause and fix |
|---|---|---|
| `Unknown case 'gb-spin'. Available: [...]` | error | The case name is not one of the listed cases. A zone directory that did not exist when the server started is not listed. Use a listed name. |
| `start ... is past the no-forward-inference horizon (cutoff ..., now - 7d 1h). Rejected.` | error | The start is newer than the cutoff. Pick a start at least 8 days back. |
| `Invalid start date` | error | `start` did not parse as a timestamp. Send ISO-8601 UTC. |
| `No settlement periods in range.` | error | The `live` window held no settlement period: the start, rounded up to the next half hour, is later than the window's end. A `done` with `framesSent` 0 follows. Pick an earlier start. The server also sends it when a `stop` arrives before the first period is sent, which is a known server defect (the bridge server lives in the GDA repository), so a client that stops at once sees an error. |
| `Requested start is past available scalar data; clamped to ... -> ...` | info | The newest scalar record is earlier than the start you asked for, so the server streams the 7 days ending at that record (no earlier than the oldest record). The timestamps differ from your request. |
| `No estimated lake for 'zone-nged'; streaming the live on-demand solve instead (build with ...)` | info | The case has no stored lake (no `topology.json`) of the requested source. The stream continues on `live`. Build the lake, or choose `gb-spine`. |
| `No <source> frames for '<case>' in <start> -> <end>; streaming the live solve instead. ...` | info | The lake exists but holds nothing in the window. The stream continues on `live`. Build the window. |
| `No <source> frames stored for '<case>' yet; nothing to replay.` | info | The `first` or `earliest` token found no stored frames. No stream starts and no `done` follows. |
| `source='solves' only serves 'gb-spine' ...; nothing to replay.` | info | A `solves` request for another case. No stream starts and no `done` follows. |
| `Stream error: ...`, `Presolved replay error: ...`, `Solves replay error: ...`, `Raw stream error: ...` | error | The server raised while streaming; the text is the exception. |

```
$ python bridge_probe.py '{"action":"start","case":"gb-spine","start":"2026-09-01T10:00:00Z","fps":144,"source":"presolved"}' 2
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
topology  case=gb-spine  buses=316  branches=579
info      {"message": "Replaying gb-spine from presolved lake: 296 stored frames 2026-09-01T10:00:00+00:00 -> 2026-09-07T13:30:00+00:00 at 144 FPS"}
state     t=2026-09-01T10:00:00+00:00  source=presolved  buses=316
state     t=2026-09-01T10:30:00+00:00  source=presolved  buses=316
[exit 0]
```

```
$ python bridge_probe.py '{"action":"start","case":"zone-nged","start":"2026-09-01T10:00:00Z","fps":144,"source":"estimated"}' 2
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
info      {"message": "No estimated lake for 'zone-nged'; streaming the live on-demand solve instead (build with NetworkModel/build_estimated_state.py for per-bus frame-by-frame replay)."}
info      {"message": "Requested start is past available scalar data; clamped to 2026-08-15T23:00:00+00:00 -> 2026-08-22T23:00:00+00:00"}
topology  case=zone-nged  buses=1292  branches=1406
info      {"message": "Streaming zone-nged: 337 settlement periods 2026-08-15T23:00:00+00:00 -> 2026-08-22T23:00:00+00:00 at 144 FPS"}
state     t=2026-08-15T23:00:00+00:00  source=None  buses=1292
state     t=2026-08-15T23:30:00+00:00  source=None  buses=1292
[exit 0]
```

The last capture shows two fallbacks stacking. The estimated lake for `zone-nged` does not exist, so the server falls
back to the live path, and the scalar tables end before the requested start, so the live path moves the window to
2026-08-15 to 2026-08-22. The frames carry `source=None`, which marks them as live frames, not estimated ones.

**In the apps.** The desktop pill shows `BRIDGE FAULT  *  disconnected` for a stream `error` or a socket fault,
`HORIZON REJECTION  *  stream stopped` for a client-side horizon violation, and `CONNECT FAILED  *  <message>` when the
first connection fails, and logs the server's message in the event log each time. The web console shows the message as
its session error (`Bridge error: <message>`, `Bridge connect failed: ...`, `Bridge start failed: ...`). A fallback
`info` is shown by neither app.

## Pitfalls

- **A silent fallback.** The `info` messages that announce a fallback are not shown in either app. If you chose a
  stored source and the pill or view looks like a live stream, run the probe against the same start and read the
  `info` lines.
- **A start that the server accepts but moves.** On the `live` source the server clamps a start that lies past the
  scalar data to a 7-day window ending at the last scalar. The stream is valid and its timestamps are not the ones you
  asked for. Read `GET /meta` for the range.
- **Clock skew.** The client compares a tick's timestamp with its own clock. A server whose clock is ahead of the
  client's can stream a tick newer than the client's cutoff; the client refuses it and its receive loop ends. Keep the
  clocks aligned.
- **Network names.** `zone-*` names exist only for zone directories that are present when the server starts. A zone
  added later needs a restart.
- **No data for a query.** `NO_DATA` and `INFEASIBLE_HORIZON` are verdicts, not faults. Check `GET /meta` for the
  range before reading a verdict as a defect.
- **Wrong endpoint scheme.** The default endpoint is `wss://`, which needs TLS in front of the server. A local server
  speaks `ws://`, which the client refuses unless the unencrypted address is allowed (`BridgeAllowUnencrypted` on the
  desktop, `GridSim__BridgeAllowUnencrypted=true` for the web host).

## Limits

- The bridge serves only the lakes that exist: the estimated lake covers `gb-spine`, and `solves` serves only
  `gb-spine`.
- A `presolved` or `estimated` stream covers at most 35 days.
- Neither app displays an `info` message, so a fallback to the live stream is invisible there.
- The web console shows the `estimate` block only as a quality pill in its header (`ESTIMATED: chi-square PASS  *  J=...  n=...`, green on a pass and red on a fail, the desktop pill's text); `chiThreshold`, `dof` and `iterations` are not shown.
- An unrecognised `source` value is treated as `live` without a message.
- The live source's `state` frames repeat one solved network and move only the scalars.
- A `presolved` or `estimated` start of `first` on a case whose lake holds frames but no `topology.json` falls back to
  `live` with the literal `first`, which replies `error` `Invalid start date`.

## See also

- [ch. 9, GDA integration](<../technical/0009 GDA integration.md>) - the wire protocol, the client and the lake products
- [ch. 23, Replaying estimated states](<0023 Replaying estimated states.md>) - the pickers, the chi-square pill and the web addresses
- [ch. 22, Estimating GB history](<0022 Estimating GB history.md>) - building the presolved and estimated lakes
- [ch. 3, Configuration](<0003 Configuration.md>) - the bridge endpoint settings
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - bridge faults among the wider fault catalogue
