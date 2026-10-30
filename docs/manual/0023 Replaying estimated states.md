# 23. Replaying estimated states

Once the estimated-state lake holds a day ([ch. 22](<0022 Estimating GB history.md>)), the desktop app and the web
console can replay it: the GB network on the map, one reconstructed state per 30-minute settlement period, each
state fitted to that period's real telemetry. This chapter covers how to start a replay, what the three bridge pickers
do, how to read the chi-square status pill that accompanies every estimated frame, and what the web console shows. The
apps get their frames from the GDA bridge server, which [ch. 24](<0024 The GDA bridge.md>) covers.

## Before you start

- **A reachable bridge.** The apps are clients. A bridge server with the estimated lake behind it must be running and
  reachable, whether the hosted one or your own ([ch. 24](<0024 The GDA bridge.md>)).
- **An endpoint in the app settings.** The desktop app lists a **Live bridge** model source when `BridgeEndpoint` in
  `%LOCALAPPDATA%\GridSim\settings.json` is non-empty. The default is the hosted endpoint
  `wss://simstream.twinscrollgridbalancer.co.uk/ws/bridge`, so a fresh install already lists **Live bridge** and
  connects to the hosted server. The web console shows its bridge cards when the `GridSim:BridgeEndpoint`
  configuration key is set. For a bridge on your own machine set the value to `ws://127.0.0.1:8770/ws/bridge` and allow the unencrypted address
  (`BridgeAllowUnencrypted`, below).
  [Ch. 3, Configuration](<0003 Configuration.md>) lists both settings.
- **An estimated lake for the network you pick.** The estimated lake is built on `gb-spine` (the 316-bus GSP-level
  network). Pick `gb-spine` for estimated replay. Any other network has no estimated lake, and the stream falls back
  to a live solve (see [Pitfalls](#pitfalls)).
- **Days that were estimated.** An estimated replay contains only the settlement periods the lake holds.

## Worked example

Start the desktop app on the estimated source of `gb-spine`, anchored 60 days back:

```bash
dotnet run --project src/GridSim.Wpf -c Release -- "Live bridge" --zone gb-spine --back 60 --bridge-source estimated
```

The non-flag argument `"Live bridge"` boots the app straight onto the bridge source. `--zone`, `--back` and
`--bridge-source` set the ZONE, FROM and SRC pickers before it connects. The app connects and sends one `start` action
for `gb-spine` with source `estimated`, a start of now minus 60 days and 2 frames per second, and the server answers.

The reference lake holds one stored period, 2026-09-01 11:00 UTC. A stored-lake stream plays the periods at or after
its start, up to the cutoff and at most 35 days from the start, so a preset shows that period only when it falls in the
window. On 2026-10-03, the day of the captures, `60 days ago` starts on 2026-08-04 and its window ends on 2026-09-08,
so it reaches the period. Every other FROM preset starts after the period, finds no stored frame and gets a live solve
instead (see [Pitfalls](#pitfalls)).

The conversation below is the same action driven with a short socket client that prints what the app receives (the
client is listed in [ch. 24](<0024 The GDA bridge.md#watching-the-stream-yourself>)). It uses an explicit start of
2026-09-01 10:00 UTC, the day the example lake was built for, and 144 frames per second so that the stream finishes at
once:

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

The reference lake holds one estimated period, so the stream is one frame. The app draws the 316-bus topology once,
paints the frame's bus voltages and line flows, and reads the `estimate` block into the pill:

```
ESTIMATED  *  gb-spine  *  chi-square PASS  *  J=225.4  n=948
```

`J` 225.4 sits below the chi-square threshold of 359.5 for 317 degrees of freedom, so the pill is green. Nothing was
removed, so no `removed=` item appears. The stored period was estimated on an earlier build, which counted the
`prior-solved` rows of a `--prior solve` run in `J` and in the degrees of freedom; a period estimated by the current
build has far fewer degrees of freedom and a different threshold
([ch. 22](<0022 Estimating GB history.md>)).

## Reading the output

### The three pickers

In bridge mode the transport bar shows three pickers. Changing any of them stops the stream and starts a new one on the
same connection; nothing reconnects. The pill shows `RESTARTING` while the new stream starts.

| Picker | Choices | Default | What it sets |
|---|---|---|---|
| **ZONE** | `NGED  -  Midlands/SW/S.Wales`, `UKPN  -  London/South East/East`, `SSEN  -  N.Scotland/Southern`, `SPEN  -  Scotland/Merseyside`, `NPg  -  North East/Yorkshire`, `ENWL  -  North West` (the six DNO zone cases), `GB full`, `GB spine  -  GSP-level` | `NGED  -  Midlands/SW/S.Wales` | The network the server streams: `zone-nged`, `zone-ukpn`, `zone-ssen`, `zone-spen`, `zone-npg`, `zone-enwl`, `gb-full` or `gb-spine` |
| **FROM** | 9 days ago, 12 days ago, 2 weeks ago, 3 weeks ago, 30 days ago, 60 days ago | 9 days ago | The instant the stream starts: now minus the preset, computed when the stream starts |
| **SRC** | `PRESOLVED`, `ESTIMATED`, `LIVE` | `PRESOLVED` | The reconstruction that plays |

The three sources:

- **`PRESOLVED`** replays the presolved lake: a demand-scaled re-solve of each period. Per-bus state varies from frame
  to frame.
- **`ESTIMATED`** replays the estimated lake. Every frame was fitted to that period's real telemetry and carries its fit
  quality.
- **`LIVE`** solves the network once and moves only the system scalars (frequency, inertia, demand) from tick to tick.

Every FROM preset is older than the 169-hour no-forward-inference horizon, so the server never rejects a desktop
start. A stored source (`PRESOLVED`, `ESTIMATED`) runs forward from the start to the horizon cutoff, one settlement
period per frame, at 2 frames per second, and one stream covers at most 35 days. `LIVE` has no 35-day cap: it runs from
the next half hour at or after the start to the earlier of the cutoff and the newest reconstructed scalar record, and
when the start is later than that record the server moves the start to 7 days before the record and sends an `info`
message ([ch. 24](<0024 The GDA bridge.md>)). For a stored source, the table gives how many frames a preset
produces, to within one period, if the lake holds every period in the window:

| FROM | Window to the cutoff | Periods | Playing time at 2 frames/s |
|---|---|---|---|
| 9 days ago | 47 h | 94 | 47 s |
| 12 days ago | 119 h | 238 | 119 s |
| 2 weeks ago | 167 h | 334 | 167 s |
| 3 weeks ago | 335 h | 670 | 335 s |
| 30 days ago | 551 h | 1,102 | 551 s |
| 60 days ago | 35 days (cap) | 1,680 | 840 s |

The reference lake holds one estimated period, so `ESTIMATED` produces 1 frame from `60 days ago` on the capture date and
a live solve from every other preset.

### The chi-square status pill

The pill sits on the transport bar. In estimated replay it is rebuilt from every frame's `estimate` block:

```
ESTIMATED  *  <case>  *  chi-square PASS|FAIL  *  J=<J>  n=<measurements>  [removed=<count>]
```

- **`chi-square PASS` or `FAIL`.** Whether that period's fit is consistent with the uncertainties its measurements
  declared. The pill is green on PASS and red on FAIL. A FAIL frame is still drawn; its numbers are uncorroborated
  until you know why the test failed ([ch. 21](<0021 State estimation.md>)).
- **`J`** is the weighted sum of squared residuals, to one decimal place, the statistic the test judges. The threshold
  it is judged against (`chiThreshold`) and the degrees of freedom (`dof`) travel in the frame but are not on the pill.
- **`n`** is how many measurements the fit consumed.
- **`removed`** is how many measurements the bad-data pass removed in that period. It is omitted when zero. A
  measurement removed at the same bus period after period is a measurement or model defect, which the closure report's
  outlier league ranks ([ch. 22](<0022 Estimating GB history.md>)).

The pill takes other forms around the stream:

| Pill text | Colour | Meaning |
|---|---|---|
| `CONNECTING  *  <case>` | amber | The socket is opening. |
| `LIVE  *  streaming <case>` | green | Frames are arriving. On `PRESOLVED` and `LIVE` this is the steady state. On `ESTIMATED` it shows only until a frame with a quality block arrives, or throughout if none does. |
| `ESTIMATED  *  ...` | green or red | An estimated frame, as above. |
| `RESTARTING  *  <case>` | amber | A picker changed. |
| `QUERY  *  <op> ...`, `WHAT-IF  *  <verdict>` | amber | An on-demand query is running or its result is on the map ([ch. 24](<0024 The GDA bridge.md>)). |
| `CONNECT FAILED  *  <message>`, `RESTART FAILED  *  <message>`, `QUERY FAILED  *  <op>: <message>` | red | The connection, the new stream or the query failed. |
| `BRIDGE FAULT  *  disconnected` | red | The server sent an `error` or the socket dropped. |
| `HORIZON REJECTION  *  stream stopped` | red | The client refused a tick newer than now minus 169 hours. The connection's receive loop has ended. |

The server's message for a fault is written to the app's event log.

### What a frame carries

Each `state` frame carries the slim result (bus voltage magnitude and angle, injections, line flows), the system scalars
(frequency, rate of change of frequency, generation, demand, inertia, condition index, RoCoF headroom), the map overlays
(branch stress, bus condition), the operator and market series for that period, the source tag and, on an estimated
frame, the `estimate` block. In the example, `buses=316` is the bus count of the result block.

## Variations

**Launch straight into a replay.** The first launch argument that does not start with `-` is the model text, and the
app opens on the first model source whose label contains it. Put the model text before any flag: a flag's value (`gb-spine`
after `--zone`) also starts without a dash, so without model text it is matched as model text. When no label contains
the model text, the app opens on the first model source, not the last-used one. `--zone` matches a case name as a
substring in the picker's order (`--zone nged` selects `zone-nged`; `--zone gb` selects `gb-full` because it comes
first, so use `gb-spine` in full). `--back N` takes 9, 12, 14, 21, 30 or 60; any other value leaves the default of 9.
`--bridge-source` takes `presolved`, `estimated` or `live`.

**Switch source mid-stream.** Change SRC from `PRESOLVED` to `ESTIMATED` on the same zone and start to compare the two
routes frame by frame. The divergence product ([ch. 22](<0022 Estimating GB history.md>)) quantifies the gap per
period.

**The web console.** Configure `GridSim:BridgeEndpoint` (for example the environment variable
`GridSim__BridgeEndpoint=ws://127.0.0.1:8770/ws/bridge` with `GridSim__BridgeAllowUnencrypted=true`), start the web app, and the home screen shows two bridge cards.
The **Estimated bridge** card opens:

```
/dashboard/bridge:gb-spine@estimated
```

The address is `bridge:<case>[@<source>]`. The source is `presolved` (the default when `@source` is omitted),
`estimated`, `live`, `solves` or `raw`; an unrecognised suffix falls back to `presolved`, and an empty case means
`gb-full`. `/dashboard/live-bridge` opens `gb-full` on the `presolved` source; its card reads `GDA lake: presolved states (gb-full)` (it read "live stream" until October 2026). On an estimated stream the header shows a quality pill with the desktop pill's text, for example `ESTIMATED: chi-square PASS  *  J=41.3  n=120`, green on a pass and red on a fail. The web console has no pickers. A stored
source (`presolved`, `estimated`, `solves`) plays from the case's earliest stored frame, wraps at the end of the
window and starts playing on connect. A case with no stored frame gets an `info` message and no stream; there is no
fallback to a live solve. The `live` source starts 169 hours plus 2 days before now and waits for Play.

**Chart the lake's quality series.** The GDA repository's DataExplorer (`python DataExplorer/server.py`, then
<http://localhost:8077>; `--port` changes it) has a **State estimation** group with two charts, each defaulting to the
last 7 days: **Estimation quality (gb-spine)** plots objective `J` against the chi-square gate on the left axis and
iterations and removals on the right, and **Solve vs estimate divergence (gb-spine)** plots the largest voltage,
angle and flow differences between the two routes. `J` above the gate is an inconsistent fit.

## Pitfalls

- **ESTIMATED is selected and the pill says LIVE.** The case has no estimated lake, or the lake holds no frame in the
  window. The server answers with an `info` message and streams a live solve instead, for example
  `No estimated lake for 'zone-nged'; streaming the live on-demand solve instead`. The desktop app does not show `info`
  messages, so the only sign is the missing chi-square pill. Choose `GB spine  -  GSP-level`, or a FROM preset that
  falls on an estimated day. On the reference lake only `60 days ago` does.
- **The stream is one frame long, or empty.** The estimated lake holds the periods that were built. A FROM anchor at or
  before a stored period replays from the anchor to the cutoff or the end of the 35-day window; an anchor after every
  stored period finds no frame and falls back to a live solve. The example lake holds one frame.
- **The pill turns red and the stream stops.** `HORIZON REJECTION` means the client refused a tick newer than now minus
  169 hours (against its own clock). The receive loop has ended, so a picker change does not revive it; select
  another model source and then **Live bridge** again to open a new connection. `BRIDGE FAULT  *  disconnected` means
  the server sent an `error` or the socket closed, and the event log carries the message, for example
  `Bridge fault: The bridge closed the connection.` After a server `error` the connection is still open, and a picker
  change starts a new stream; after a closed socket, reselect **Live bridge**.
- **A PASS is not a plausibility check.** The chi-square verdict says the fit agrees with the declared sigmas. Read the
  voltages on the map beside it: a state can pass with a bus voltage well outside the operating band
  ([ch. 22](<0022 Estimating GB history.md#pitfalls>)).
- **`--zone` picked the wrong network.** The match is a substring against the case names in the picker's order.
- **Right-click does not trip a generator.** In bridge mode a right-click on a bus or branch opens the on-demand
  physics menu, not the local trip ([ch. 24](<0024 The GDA bridge.md>)). A right-click that hits no bus or branch
  opens nothing unless a what-if result is showing.

## Limits

- The web console streams estimated states but shows no fit-quality block, so it has no chi-square pill.
- The desktop pill shows the verdict, `J`, `n` and the removal count. The chi-square threshold, the degrees of
  freedom and the list of removed measurements are in the frame and are not displayed.
- The estimated lake covers `gb-spine` only, and only days that were built.
- The desktop FROM picker has six presets and no date field, and a stored-lake stream is capped at 35 days.
- The desktop app has no scrubber in bridge mode; a different start needs a different FROM preset.
- After `HORIZON REJECTION` the desktop app does not reconnect; a new connection needs the model source reselected.
- On `ESTIMATED`, the next estimated frame replaces the `QUERY` or `WHAT-IF` pill while the what-if result stays on the map.

## See also

- [ch. 9, GDA integration](<../technical/0009 GDA integration.md>) - the lake products and the wire protocol
- [ch. 21, State estimation](<0021 State estimation.md>) - what `J`, chi-square and removals mean
- [ch. 22, Estimating GB history](<0022 Estimating GB history.md>) - building the lake these views read
- [ch. 24, The GDA bridge](<0024 The GDA bridge.md>) - the server, the sources, the faults
- [ch. 8, The desktop app](<0008 The desktop app.md>) - the transport bar and pickers in general
- [ch. 3, Configuration](<0003 Configuration.md>) - the bridge endpoint settings
