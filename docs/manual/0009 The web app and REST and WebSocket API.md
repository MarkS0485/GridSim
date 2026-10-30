# 9. The web app and REST/WebSocket API

`GridSim.Web` is the browser version of the control room and, when enabled, an HTTP and WebSocket API over the same
`GridSim.Core` solve. The browser dashboard shows the map and the analysis tabs of [ch. 8](<0008 The desktop app.md>)
and [ch. 10](<0010 Analysis views.md>) for a live case, a replayed run directory or a GDA bridge stream. The API
lets another program list the cases, fetch a network's topology, run a one-shot solve and stream a simulation frame by
frame. You need this chapter to run the web host on your own machine, to drive it from a script, and to know what a
public deployment does and does not expose. Deployment behind nginx, sign-on, the container image and the key ring are
in [ch. 26](<0026 Audit log, deployment and assurance.md>).

## Before you start

- A build of the repository ([ch. 1](<0001 Getting started.md>)). The web host is the project `src/GridSim.Web`.
- `gridsim serve` is not the web host. It is the CLI's real-time estimator over a folder of measurement windows and binds
  no port ([ch. 25](<0025 Interposing live telemetry.md>)).
- Settings come from `appsettings.json`, the environment-specific `appsettings.<Environment>.json` and environment
  variables, where a double underscore stands for the `:` separator (`GridSim__ReplayOnly` sets `GridSim:ReplayOnly`).
  Every setting is listed in [ch. 3, Configuration](<0003 Configuration.md>).
- The two environments differ in one decisive way. `Development` (the `http` launch profile) maps the whole API.
  `Production` (`appsettings.Production.json`, which the container image and the systemd unit select) sets
  `GridSim:ReplayOnly=true`: only the raw public-data replay is offered, and `/api/*` and `/ws/sim` are not mapped.
  The API has no authorisation or rate limit, so it is meant for a trusted network.
- A licence, if the host needs one, is read as for the CLI: `Licence:Key` (`Licence__Key`), then `GRIDSIM_LICENCE`, then
  the installed licence file, then the free Personal tier ([ch. 4](<0004 Licensing and activation.md>)). A case above
  the tier's bus limit is refused (HTTP 403 on the API).

## Worked example

Start the host in the `Development` environment, which listens on `http://localhost:5180`:

```bash
dotnet run --project src/GridSim.Web
```

Open `http://localhost:5180/` for the model picker, or call the API from a second terminal:

```bash
curl -s http://localhost:5180/api/cases
curl -s "http://localhost:5180/api/topology?case=case9"
curl -s -X POST http://localhost:5180/api/solve -H "Content-Type: application/json" -d "{\"case\":\"case9\"}"
curl -s http://localhost:5180/api/openapi.json
```

`/api/cases` answers a JSON array with one object per loadable model: `id` (the handle the other calls take), `label`,
`kind` (`BuiltIn`, `JsonDir`, `Matpower` or `Replay`) and `replay` (true for a run directory). `/api/solve` on `case9`
loads the case, solves it once with the on-load tap changer loop (at most 25 passes), and answers HTTP 200 with
`converged: true`, `status: "Converged"`, the iteration count, the final mismatch, the case's `topology` and the solved
`state`, the same frame the dashboard draws. Each part is described under [Reading the output](#reading-the-output).

## Reading the output

### The browser pages

- **`/`** is the model picker: one card per catalogue entry (built-in cases, the bundled GB JSON cases, MATPOWER files
  and run directories found in nearby `results/` folders), plus `Live bridge` and `Estimated bridge` cards when a bridge
  endpoint is configured. On a replay-only instance it opens the raw replay at once.
- **`/dashboard/<case id>`** is the dashboard for one case. Its header names the case, the bus and branch counts and,
  for a replay, the frame count. A live session shows a `SOLVER` picker (Auto, the dense and sparse Newton engines,
  Radial sweep); picking one reloads the page with `?solver=auto|dense|sparse|sweep`, which restarts the session on
  that engine. A raw replay reads `REPLAY | raw public data`.
- **Tabs.** A solved session shows NETWORK, GENERATION & INERTIA, THE MACHINE, SCADA, OPERATOR, MARKET, NESO RP1,
  MECHANICAL, HUMAN SCALE, SPEED DOMAINS, TRIP / DEVICE, INVERTER FLEET, CONFORMANCE, EXPERIMENTAL, HARMONICS, CYCLE,
  ASSETS and AUDIT LOG. The raw replay runs no solve, so it shows NETWORK, CHARTS, GENERATION & INERTIA, THE MACHINE,
  NESO RP1, HUMAN SCALE, OPERATOR, MARKET, LIVE and ABOUT. The tabs are read as in [ch. 10](<0010 Analysis views.md>);
  the differences from the desktop app are listed in [ch. 8](<0008 The desktop app.md>).

### `GET /api/cases`

An array of `{ "id", "label", "kind", "replay" }`, in a stable order: built-ins, GB JSON cases, MATPOWER files, replay
runs.

### `GET /api/topology?case=<id>`

The one-time map of a case: `type` (`topology`), `caseId`, `caseLabel`, `baseMva`, `busCount`, `replay`, `frameCount`,
the `nodes`, `edges` and `fleet` lists, the transmission `boundaries`, and `isGeolocated` (true when the case ships real GB
coordinates, so the geography overlays line up). A replay's topology is built once per run and rebuilt only when the
run's files change. Status codes: 200, 400 for a missing or blank `?case=`, 403 when the licence does not cover the
model, 404 for an unknown case.

### `POST /api/solve`

The body is `{ "case": "<id>" }`. The answer always says whether the solve converged:

| Status | Body |
|---|---|
| 200 | `converged: true`, `status: "Converged"`, `iterations`, `maxMismatch`, `topology`, `state`. |
| 422 | `converged: false`, `status` (`MaxIterations` or `NonFinite`), an `error` text saying the state is not a solution, `iterations`, `maxMismatch`, and the same `topology` and `state` for inspection only. |
| 400 | No case in the body, or the case is a replay run (use `/ws/sim` for a replay). |
| 403 | The licence does not cover the model. |
| 404 | Unknown case. |

`state` is one frame envelope: `type` (`state`), `tick`, `hour`, `clock`, `label`, the `headline` (`frequencyHz`,
`rocofHzPerS`, `generationMw`, `demandMw`, `lossMw`, `inertiaGvaS`, `avgVoltagePu`, `converged`, `minVoltagePu`,
`maxVoltagePu`), the per-bus `nodes` (`id`, `vm`, `vaDeg`, `pInjMw`, `qInjMvar`), the per-branch `edges` (`index`,
`pFromMw`, `qFromMvar`, `pLossMw`, `loadingFrac`), the `boundaries` (`name`, `flowMw`, `limitMw`, `loadingPercent`,
`overloaded`), and one block per tab (`genInertia`, `operator`, `market`, `rp1`, `mechanical`, `scada`, `machine`, and
the rest, null where a tab is not computed). The one-shot solve fills the tab blocks from fresh services, so the
history-based fields (ramps, time error) read zero. A steady snapshot sits at the case's own nominal frequency. A value
that is not finite (an unlimited reactive limit, an infinite purchased time) is written as `null`.

### `GET /api/realtime`

The resident real-time estimator's output, when `Realtime:Enabled=true`: `summary` (its run counts), `latest` (`sequence`,
`stateTimeUtc`, `status`, `converged`, `objectiveJ`, `chiSquare`) and `skippedFiles`. 404 when the estimator is not
enabled. It runs once, at start-up, on `Realtime:Case` (default `case9`) over the windows in `Realtime:WindowsDir`; it
does not watch the folder. Every window passes the 169-hour horizon guard inside the engine. With `Audit:LogPath` set,
the run is recorded in the audit log, opened and verified against its anchor before the host serves anything
([ch. 26](<0026 Audit log, deployment and assurance.md>)).

### `GET /api/openapi.json`

An OpenAPI 3.0.3 document of the five paths above and `/ws/sim`. Its `info.version` is the release number
(`1.95.NNN`), the same number `gridsim --version` prints before its commit.

### The `/ws/sim` WebSocket

A raw WebSocket at `ws://<host>/ws/sim` streams one session per connection. The client sends text frames holding one
JSON object; the server answers with `topology`, `state` and `error` messages.

**Control frames.** The fields are `action`, `case`, `source`, `fps`, `value`, `solver`, `assetGraph` and `waveField`;
any other property is refused by name.

| `action` | Fields | Effect |
|---|---|---|
| `start` | `case` (required), `source`, `fps`, `solver`, `assetGraph`, `waveField` | Open a session on the case, replacing any earlier one on the connection. |
| `play`, `pause`, `step` | - | Run, stop, or advance exactly one frame. |
| `seek` | `value`: a fraction of the run from 0 to 1 | Replay only: jump to that point and send its frame. |
| `speed` | `value`: frames per second | Set the frame rate, from 0.1 to 60; a value outside that range is refused with an error frame (it used to be clamped). A `start` frame's `fps` follows the same rule. |
| `trip` | `value`: MW | Live only: trip that much infeed into the session's frequency integrator. |

- `case` decides the feed: a run directory id is a replay, `bridge:<case>@<source>` or `live-bridge` is a bridge stream,
  and any other catalogue case is live. `source`, when given, must be `live`, `replay` or `bridge` and must match that
  feed, or the start is refused.
- `solver` (`auto`, `dense`, `sparse`, `sweep`) picks a live session's engine; on a replay or bridge feed it is refused.
- `assetGraph: true` adds the per-frame asset graph (every bus, branch and generator with its electrical fields) to the
  state frames, and `waveField: true` the experimental per-bus wave field. Both are off by default, and both blocks are
  then `null`; they are the largest parts of a frame (the wave field alone is about 230 kB per frame on `gb-full`).
- A bridge source is `presolved` (the default with no `@`), `estimated`, `live`, `solves` or `raw`; anything else is
  refused with the list. A bridge stream needs `GridSim:BridgeEndpoint`.

**Server messages.** After a `start` the server sends one `topology` message, then a `state` message per frame (the
envelope described under `POST /api/solve`). A later `start` sends the new case's topology before its first state, and
frames still queued from the replaced session are dropped. A bridge session sends its topology when the bridge server
delivers it. Under backpressure the server drops the oldest queued state frames, never the topology. An `error` message
is `{ "type": "error", "message": "..." }`: it answers a binary frame, text that is not a JSON object, an unknown
property, a missing or unknown action, a control before `start`, a missing or non-numeric `value`, a refused source or
solver, an unknown case, a trip on a replay or bridge feed, the session cap, and a bridge fault. The connection stays
open after an error frame. A control frame larger than 16 KiB closes the socket with `MessageTooBig`. When the client
closes, the server answers with its own close frame, echoing the client's status code.

## Variations

### A replay-only public instance

`GridSim__ReplayOnly=true` (the `Production` default) offers only the raw public-data replay of the cases named in
`GridSim__ReplayCases` (a comma list; default `gb-full`): `/` opens `bridge:<first case>@raw`, every other dashboard
address answers 404 before any session code runs, and `/api/*`, `/ws/sim` and the resident estimator are not mapped.
Set `GridSim__ReplayOnly=false` only on a trusted network.

### The session cap

Each browser tab and each `/ws/sim` connection holds one session, private to it and disposed when it closes. At most
`GridSim__MaxSessions` (default 64) run at once; one more is refused with `The server is already running its maximum of
64 sessions (GridSim:MaxSessions); close another dashboard or connection, or try again shortly.` A value that is not a
whole number of 1 or more stops start-up.

### The analysis cycle

A live session runs the resident analysis cycle (the CYCLE tab of [ch. 10](<0010 Analysis views.md>)) unless
`Cycle__Enabled=false`. `Cycle__Workers`, `Cycle__WindowMs` and `Cycle__Shards` tune it; the default worker count is a
modest share of the processors so the web server is not starved.

### A bridge stream

`GridSim__BridgeEndpoint=wss://<bridge host>:<port>` connects bridge sessions to a GDA bridge server (a `ws://`
address also needs `GridSim__BridgeAllowUnencrypted=true`, or the host stops at start-up naming that setting)
([ch. 24](<0024 The GDA bridge.md>)). Without it a bridge case is refused with `No GDA bridge endpoint is configured
(GridSim:BridgeEndpoint).`

## Pitfalls

- **`/api/...` returns 404.** The instance is replay-only: the `Production` environment sets it.
- **A 422 from `/api/solve` is not a server fault.** The power flow did not converge; the body carries the
  untrustworthy state for inspection only.
- **A `/ws/sim` client hears nothing back.** Every refused frame is answered with an `error` message, so silence means the
  frame never arrived (check that it is a text frame) or that no `start` has opened a session yet.
- **A misspelt control field is refused, not defaulted.** `{"action":"start","case":"case9","fsp":5}` is answered with
  an error naming `'fsp'`.
- **`trip` on a replay or bridge feed is refused.** Only a live in-process session can be tripped.
- **A bridge id with a typo in its source is refused.** `bridge:gb-spine@estimate` names no source; the error lists the
  five that exist.

## Limits

- The solve API and `/ws/sim` have no authorisation and no rate limit; only the session cap bounds them. Production
  starts replay-only for that reason.
- `/api/solve` solves one case once, from its stored state; it takes no options (engine, tolerance, outages).
- A replay case cannot be solved through `/api/solve`; it is streamed through `/ws/sim`.
- The `state` frames of the one-shot solve carry no history, so their ramp and time-error fields read zero.

## See also

- [ch. 8, The desktop app](<0008 The desktop app.md>) - the control room and how the web app differs from it
- [ch. 10, Analysis views](<0010 Analysis views.md>) - every tab, field by field
- [ch. 26, Audit log, deployment and assurance](<0026 Audit log, deployment and assurance.md>) - the container, nginx, sign-on, the key ring and the trusted proxies
- [ch. 3, Configuration](<0003 Configuration.md>) - every web setting and its default
- [ch. 24, The GDA bridge](<0024 The GDA bridge.md>) - the bridge server and its sources
- [Technical ch. 10, File and wire formats](<../technical/0010 File and wire formats.md>) - the `/ws/sim` messages and frame DTOs in full
- [ch. 19, CLI reference](<0019 CLI reference.md#serve>) - `serve`, the CLI estimator that is not the web host
