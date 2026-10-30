# 34. Web host, REST/WebSocket, SSO and sessions

This chapter specifies the external surface of `GridSim.Web` (`src/GridSim.Web/`): how a request reaches the host, the
REST routes and their status-code contract, the `/ws/sim` WebSocket protocol, how sessions are created, capped and
refused, the resident real-time estimator's endpoint, and the single sign-on routes. The JSON shapes of the documents
the routes return (`TopologyDto`, `FrameEnvelope` and their blocks) are in
[ch. 10, File and wire formats](<0010 File and wire formats.md>); the container, Compose, systemd and nginx
deployment, the key ring and the SSO configuration are in [ch. 35, Deployment and security](<0035 Deployment and security.md>).
The Blazor dashboard itself (its tabs and views) is described in the user manual. The web host is presentation only: every
number it serves comes from GridSim.Core.

## Model

### How the host starts

`src/GridSim.Web/Program.cs` builds the host. In order:

1. `InvariantCulture` is pinned for the process.
2. `GridSim:ReplayOnly` is read. A replay-only instance offers only the raw public-data replay (below).
3. The licence is resolved once (`LicenceGate.Resolve`) and registered; the case catalogue (`CaseCatalog`, one filesystem
   scan) and the `SessionManager` are registered as singletons. A model folder that cannot be read is skipped with a
   line on stderr naming it.
4. Non-finite doubles serialise as `null` on every HTTP JSON path (`NonFiniteDoubleConverter`).
5. `Site:*` settings are read and checked once (`SiteSettings.FromConfiguration`); a malformed value stops start-up
   naming the setting.
6. Data protection, cookie authentication and single sign-on are configured ([ch. 35](<0035 Deployment and security.md>)).
   `Sso:IsAuthority=true` with `Sso:Enabled` stops start-up: GridSim has no login page, so it cannot mint tickets.
   `Sso:PublicOrigin`, when set, must be a bare http(s) origin.
7. The audit settings are checked: `Audit:AnchorPath` without `Audit:LogPath`, or `Audit:LogPath` on a host that runs
   no resident estimator, stops start-up naming the setting.
8. When the host is not replay-only and `Realtime:Enabled=true`, the resident estimator's case (`Realtime:Case`, default
   `case9`) is loaded once, its audit log (when set) is opened and verified, and the estimator is registered as a
   hosted service over `Realtime:WindowsDir`. A case the licence does not cover, or a name the catalogue does not know,
   stops start-up naming `Realtime:Case`.
9. The `SessionManager` is built before the first request, so a malformed `GridSim:MaxSessions` or
   `GridSim:ReplayCases` stops start-up.

The request pipeline then runs: the exception handler outside Development first (the `/Error` page, which shows the
request id and no exception detail), so it covers every later middleware; `/.well-known/security.txt` (the configured
file, or 404 when no contact is configured); on a replay-only instance, the `/dashboard` gate; forwarded headers from the
proxies `GridSim:TrustedProxies` names (default loopback); static files; authentication and authorisation; the optional `/geo` folder (GeoJSON overlays, served
when the folder exists; only its `.geojson` and `.json` files, any other file is a 404, GR-708); antiforgery; when not replay-only, `UseWebSockets`, the `/api` group and `/ws/sim`; the Razor
components; and the SSO routes.

`dotnet run --project src/GridSim.Web` uses `Properties/launchSettings.json`: the `http` profile starts the host in the
Development environment on `http://localhost:5180`. `ASPNETCORE_URLS`, when set, wins over the profile's address.

### Routes

| Route | Method | Mapped when | Purpose |
|---|---|---|---|
| `/` and `/dashboard/<id>` | GET | always | The Blazor dashboard; on a replay-only instance `/` opens the first configured raw replay |
| `/api/cases` | GET | not replay-only | The loadable models |
| `/api/topology?case=<id>` | GET | not replay-only | One case's map topology |
| `/api/solve` | POST | not replay-only | One-shot solve of a non-replay case |
| `/api/realtime` | GET | not replay-only | The resident estimator's latest output |
| `/api/openapi.json` | GET | not replay-only | The OpenAPI 3.0.3 description of the surface |
| `/ws/sim` | WebSocket | not replay-only | Streamed simulation for external consumers |
| `/.well-known/security.txt` | GET | always | RFC 9116 file from `Site:SecurityTxt:*`, or 404 |
| `/Error` | GET | always | The error page the exception handler re-executes |
| `/compliance` | GET | always, anonymous | The build's compliance attestation as JSON (version, commit, build hash, SBOM hash, manifest hash, release build, controls) |
| `/health` | GET | always, anonymous | Liveness: `{status: "ok"}` when no resident estimator is configured; `{status: "starting"}` (200) while it is configured but has no summary yet; then `healthy` (200) or `degraded` (503) with the objective fractions and breaches |
| `/geo/*` | GET | when the `gda-out/geo` folder exists | Map overlay GeoJSON |
| `/sso/bridge`, `/sso/land`, `/sso/logout` | GET | always; each answers 404 when sign-on is off | Estate single sign-on ([ch. 35](<0035 Deployment and security.md>)) |

None of the REST or WebSocket routes requires authorisation. A cookie challenge answers 401 and a forbid 403, with no
redirect: the app serves no login or access-denied page.

### REST contract

`RestApi.MapGridSimApi` (`src/GridSim.Web/Api/RestApi.cs`):

| Route | 200 | 400 | 403 | 404 | 422 |
|---|---|---|---|---|---|
| `GET /api/cases` | `[{id, label, kind, replay}]` | - | - | - | - |
| `GET /api/topology?case=` | the topology (a replay run's is built once and rebuilt only when the run's files change, `ReplayTopologyCache`) | no case named (missing or blank) | the licence does not cover the model | unknown case | - |
| `POST /api/solve` with `{"case": ...}` | converged: `{converged: true, status, iterations, maxMismatch, topology, state}` | no case named, or a replay run (use `/ws/sim`) | the licence does not cover the model | unknown case | not converged: `{error, converged: false, status, iterations, maxMismatch, topology, state}` |
| `GET /api/realtime` | `{ok, summary, latest, skippedFiles}` | - | - | the resident estimator is not enabled | - |
| `GET /api/openapi.json` | the document | - | - | - | - |

An error body is `{"error": "<text>"}`. A missing case reads `The request must name a case, for example {"case":"case9"}
(POST /api/solve) or ?case=case9 (GET /api/topology).`; an unknown one `Unknown case '<id>'.`; a replay case on
`/api/solve` `Case '<id>' is a replay run - use /ws/sim.`.

`POST /api/solve` loads the case, solves it once with a dedicated `SteadyStateSolver` (`options: null`, at most
`RestApi.RestOltcPassCap` = 25 OLTC passes, the solver's own default), and projects the regulated model and result to one
`FrameEnvelope` at hour 0, tick 0 and the case's own nominal frequency, with every tab block filled (the stateful tabs
from fresh services, so their history-based fields read zero). A solve that did not converge is never answered 200: it
answers 422 with `status` `MaxIterations` or `NonFinite`, the error text `The power flow did not converge (<status>
after <n> iterations, largest mismatch <m>): the state below is not a solution and must not be used as one.` and the
unconverged topology and state for inspection.

`GET /api/realtime` always answers 200 when enabled; its `ok` is true only when there is a latest frame and that
estimate converged. Its `latest` is `{sequence, stateTimeUtc, status, converged, objectiveJ, chiSquare}` of the last frame,
or `null`; `skippedFiles` lists the windows the feed skipped (fresh or undated windows, refused by the horizon guard,
[ch. 31](<0031 Audit log and diagnostics.md>)).

`GET /api/openapi.json` is a hand-written OpenAPI 3.0.3 document whose `info.version` is the build's release number
(`BuildInfo.Version`) and whose every operation documents its responses.

### The `/ws/sim` protocol

`SimWebSocket.HandleAsync` (`src/GridSim.Web/Api/SimWebSocket.cs`). A request that is not a WebSocket upgrade answers 400.
The connection then holds at most one session at a time.

**Control frames (client to server)** are UTF-8 JSON objects with the properties `action`, `case`, `source`, `fps`,
`value`, `solver`, `assetGraph` and `waveField` (names case-insensitive). `action` is one of `start`, `play`, `pause`,
`step`, `seek`, `speed`, `trip`.

| Action | Effect |
|---|---|
| `start` | Disposes the connection's current session, opens a new generation on the socket (frames still queued from the replaced session are dropped), reads `assetGraph` and `waveField` (default false: the state frames' `assetGraph` and `experimental.waveField` are `null`), checks `source`, and creates a session with `SessionManager.Create(case, SolverChoice.Parse(solver))`. With `fps` given, the speed is set; an `fps` outside 0.1 to 60 (`PresentationLimits.MinFps` / `MaxFps`) is refused before the session is created (GR-734: it used to be clamped, and 0 or below ignored). The topology frame is queued ahead of the first state frame; a bridge session's topology is sent once, when the server delivers it. |
| `play`, `pause`, `step` | Drive the current session. |
| `seek` | `value`: a fraction of the run from 0 to 1. |
| `speed` | `value`: frames per second, from 0.1 to 60; outside that range it is refused with an error frame (GR-734). |
| `trip` | `value`: MW to trip; only a live in-process session can be tripped. |

`source`, when given, must be `live`, `replay` or `bridge` and must name the feed the case id opens (a `run-*` id is a
replay, `bridge:<case>[@<source>]` or `live-bridge` the bridge, any other case live); absent or blank, the case id
decides. `solver` is `auto` (default), `dense`, `sparse` or `sweep`, for a live session only.

**Frames (server to client)**: `{type: "topology", ...}`, `{type: "state", ...}` (shapes in ch. 10) and
`{type: "error", message}`. Every frame the server cannot act on is answered with an error frame, never dropped: a binary
frame, text that is not a JSON object (or JSON `null`), an unknown property (named, with the list of the eight), no or an
unknown `action`, `start` with no case, an unknown or mismatched `source`, an unknown solver or a solver for a replay or
bridge case, any action other than `start` before a successful `start`, `seek`, `speed` or `trip` without a finite
numeric `value`, a `speed` or `fps` outside 0.1 to 60, a `trip` on a replay or bridge session, and every refusal of
`SessionManager.Create`. A failed `start` leaves the connection with no session. The error text is the exception's
message with every absolute server path replaced by `<path>` (GR-721, `ClientErrorText`); the full message is logged on
the server (event 1721, `SimWebSocketDispatchFailed`).

**Limits.** A control frame larger than 16 KiB (16,384 bytes, assembled across fragments) closes the socket with
`MessageTooBig`. State frames go through a channel of 256 frames that drops the oldest under back-pressure; the topology
frame is held in a priority slot that is delivered first and never dropped. A client close frame is answered with the
server's own close frame echoing the client's status code (`NormalClosure` when it sent none); a server-side end sends
`NormalClosure` and waits for the reply.

### Sessions

`SessionManager` (`src/GridSim.Web/Sessions/SessionManager.cs`) creates every session, for a Blazor circuit (one browser
tab) and for a `/ws/sim` connection alike, and keeps only the count of those alive.

- **Kinds.** A replay run directory opens a `PresolvedReplay`; a `bridge:<case>[@<source>]` id or `live-bridge` streams
  from the GDA bridge at `GridSim:BridgeEndpoint` (source `presolved` by default, or `estimated`, `live`, `solves`,
  `raw`; any other `@source` is refused naming the five; an empty case means gb-full); every other catalogue case runs a
  live in-process simulation with the chosen engine. A replay or bridge session runs no solver of its own, so asking it
  for one is refused. With no bridge endpoint configured a bridge id is refused (`No GDA bridge endpoint is configured
  (GridSim:BridgeEndpoint).`). An unknown id throws `Unknown case '<id>'.`. On a bridge session a pause, speed change
  or seek the bridge does not take (a closed socket, a refused command) is shown as the session's warning (`Bridge
  <command> failed (...)`); the dashboard stays up.
- **Cap.** At most `GridSim:MaxSessions` (default `SessionManager.DefaultMaxSessions` = 64; a whole number of 1 or more)
  sessions are alive at once. A slot is taken before the session is built and given back exactly once when it is
  disposed, or at once when building it fails; one more session throws `SessionLimitException`.
- **Replay-only.** On a replay-only instance only `bridge:<case>@raw` for a case named in `GridSim:ReplayCases` (a comma
  list of remote case names; default `gb-full`) may be opened. The `/dashboard` gate answers 404 with `Not available: this
  is a replay-only instance (GridSim:ReplayOnly=true), which offers only the raw public-data replay.` before any page code
  runs, and `SessionManager.Create` throws `ReplayOnlyRefusedException` for the same ids, which also covers a navigation
  inside an open circuit. The home page lands on the raw replay of the first configured case.

A session belongs to its connection and is disposed when the connection ends; no session state (model, replay
position, results) is shared between visitors. One thing is shared on purpose: the process-wide `PresenceService`
singleton behind the LIVE tab, which every visitor can open. It shows each connected viewer by a random short handle
with the case, the replay instant they are looking at, play or pause and how long they have been connected; the
viewer count, the visits since the process started and the all-time visit count from the usage log; and the host's
CPU and RAM use and the process's RAM and thread count. It holds no names or IP addresses.

### Single sign-on

GridSim is an SSO consumer only. It recognises a visitor already signed in on the estate (a cookie that decrypts with
the shared key ring and application name) and, as an SSO client, redirects a challenge to the authority's `/sso/bridge`
and signs in once on `/sso/land` with a one-time ticket. Every page stays reachable anonymously. The settings, the
ticket rules and the threat statements are in [ch. 35](<0035 Deployment and security.md>).

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Session cap | 64 | sessions | Most sessions alive at once | `SessionManager.DefaultMaxSessions`, `GridSim:MaxSessions` |
| Replay cases | `gb-full` | - | Raw replays a replay-only host serves | `SessionManager.DefaultReplayCases`, `GridSim:ReplayCases` |
| Replay-only landing | `bridge:gb-full@raw` | - | Default landing session | `SessionManager.RawLandingCaseId` |
| Bridge sources | `presolved`, `estimated`, `live`, `solves`, `raw` | - | Accepted `@source` values | `SessionManager.BridgeSources` |
| REST OLTC pass cap | 25 | passes | One-shot solve | `RestApi.RestOltcPassCap` = `SteadyStateSolver.DefaultMaxOltcPasses` |
| Control frame limit | 16,384 | bytes | Larger closes with `MessageTooBig` | `SimWebSocket.HandleAsync` |
| State frame channel | 256 | frames | Drop-oldest buffer per connection | `SimOutbound` |
| Development address | `http://localhost:5180` | - | `dotnet run` profile | `Properties/launchSettings.json` |
| Resident estimator case | `case9` | - | `Realtime:Case` default | `Program.cs` |
| Solver names | `auto`, `dense`, `sparse`, `sweep` | - | `solver` / `?solver=` values | `SolverChoice.Options` |

## Invariants

| Invariant | Test |
|---|---|
| The home page serves anonymously with the build version; the OpenAPI document carries the central version and documents every operation's responses | `WebHostTests.Home_page_serves_anonymously_and_shows_the_build_version`, `Openapi_document_carries_the_central_version`; `WebHostDoc2Tests.Every_openapi_operation_documents_its_responses` |
| The API lists cases and solves case9 when not replay-only; replay-only maps neither the API nor the WebSocket | `WebHostTests.Api_lists_cases_and_solves_case9_when_not_replay_only`, `Replay_only_maps_neither_the_api_nor_the_sim_websocket` |
| A converged solve says so in the body; a non-converged one answers 422 with `converged: false`; a missing case is 400 and an unknown one 404 | `WebHostDoc2Tests.Rest_solve_of_a_converging_case_says_converged_in_the_body`, `Rest_solve_of_a_non_converged_result_answers_422_with_converged_false`, `A_missing_or_blank_case_is_400_and_an_unknown_one_still_404` |
| The WebSocket answers every frame it cannot act on with an error frame, refuses an unknown property by name, leaves no session after a failed start, completes the close handshake, and sends the asset graph only when asked | `WebHostDoc2Tests.Sim_websocket_answers_every_frame_it_cannot_act_on_with_an_error_frame`, `N227_sim_websocket_refuses_an_unknown_property_by_name`, `A_failed_start_leaves_no_session_behind`; `WebHostTests.Sim_websocket_completes_the_close_handshake_when_the_client_closes`, `Sim_websocket_sends_the_asset_graph_only_when_asked`, `Sim_websocket_start_with_an_unknown_solver_answers_an_error_frame` |
| Sessions beyond the cap are refused and a closed one frees its slot; the cap defaults to 64 and a bad value is refused; an unknown bridge source is refused | `WebHostDoc2Tests.Sessions_beyond_the_cap_are_refused_and_a_closed_one_frees_its_slot`, `The_session_cap_defaults_to_64_and_a_bad_value_is_refused`, `An_unknown_bridge_source_is_refused_not_replaced_by_presolved` |
| Replay-only refuses every dashboard route but the raw replay, on every path; a full instance still opens a live dashboard | `WebSecurityDoc2Tests.Replay_only_refuses_every_dashboard_route_but_the_raw_replay`, `Replay_only_still_serves_the_raw_replay_page`, `Replay_only_session_manager_refuses_live_sessions_on_every_path`, `Only_bridge_raw_ids_are_allowed_on_a_replay_only_instance`, `A_full_instance_still_opens_a_live_dashboard` |
| `security.txt` is served first, exactly as configured, or 404 | `WebHostTests.Security_txt_is_served_before_anything_else`, `Security_txt_answers_404_when_no_contact_is_configured`, `Security_txt_is_exactly_the_configured_file` |
| `dotnet run` starts the host in Development; the `/Error` page is served; an unloadable resident-estimator case stops start-up naming the setting | `WebHostDoc2Tests.Dotnet_run_starts_the_web_app_in_development`, `The_error_page_named_by_the_exception_handler_is_served`, `An_unloadable_realtime_case_stops_start_up_naming_the_setting` |
| A replay run's topology is built once and rebuilt when the run changes | `WebHostDoc2Tests.Replay_topology_is_built_once_and_rebuilt_when_the_run_changes` |
| A cookie challenge answers 401 and a forbid 403 without a redirect; SSO authority mode stops start-up | `WebSecurityDoc2Tests.A_cookie_challenge_answers_401_and_a_forbid_403_without_a_redirect`, `Sso_authority_mode_stops_start_up_with_the_reason` |

## Worked example

A client that sends `{"action":"start","case":"case9","solver":"warp"}` on `/ws/sim` receives one error frame,
`{"type":"error","message":"Unknown solver 'warp'. Use one of: auto, dense, sparse, sweep. (Parameter 'name')"}` (the
`ArgumentException` message of `SolverChoice.Parse`), and the connection holds no session, so a following
`{"action":"play"}` receives `'play' has no session to act on: send a 'start' frame first.`. A start with an unknown
property, `{"action":"start","case":"case9","fsp":5}`, receives `malformed control frame: unknown property 'fsp'; a
control frame has only action, case, source, fps, value, solver, assetGraph, waveField.`. `POST /api/solve` with the body
`{}` answers 400 with the missing-case text above; with `{"case":"case9"}` it answers 200 with `converged: true` and the
case's topology and projected state. The captured requests and responses of these runs are taken by
`tools/docs/capture.ps1`.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Web/Program.cs` | top-level program | Start-up checks, services, the request pipeline, route mapping |
| `src/GridSim.Web/Api/RestApi.cs` | `RestApi` | The REST routes, the solve status contract, the OpenAPI document |
| `src/GridSim.Web/Api/SimWebSocket.cs` | `SimWebSocket` | The `/ws/sim` protocol |
| `src/GridSim.Web/Api/SimOutbound.cs` | `SimOutbound` | Topology priority slot and the 256-frame state channel |
| `src/GridSim.Web/Api/ReplayTopologyCache.cs` | `ReplayTopologyCache` | Per-run topology cache for `/api/topology` |
| `src/GridSim.Web/Api/NonFiniteDoubleConverter.cs` | `NonFiniteDoubleConverter` | Non-finite doubles as `null` |
| `src/GridSim.Web/Sessions/SessionManager.cs` | `SessionManager` | Session kinds, cap, replay-only gate, bridge sources |
| `src/GridSim.Web/Sessions/SimSession.cs` | `SimSession` | One connection's simulation and its frames |
| `src/GridSim.Web/Sessions/SolverChoice.cs` | `SolverChoice` | Solver names to engines |
| `src/GridSim.Web/Realtime/RealtimeEstimatorHostedService.cs`, `ReplayDirectoryWindowFeed.cs` | resident estimator | The `/api/realtime` source and its window feed |
| `src/GridSim.Web/Site/` | `SiteSettings`, `TrustedProxies`, `KeyRingProtection` | Site identity, forwarded-header trust, key ring protection |
| `src/GridSim.Web/Sso/EstateSso.cs` | `EstateSso` | The SSO routes |
| `src/GridSim.Web/Properties/launchSettings.json` | launch profile | `dotnet run` environment and address |

## Limits

- The REST and WebSocket routes carry no authorisation of their own; a deployment that must restrict them does so at
  the proxy, or runs replay-only.
- The resident estimator runs the static engine once over a directory at start-up; it does not watch the directory.
- The state frame channel drops the oldest frames for a slow consumer, so a client that cannot keep up sees gaps in the
  tick sequence.

## See also

- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - the topology and state frame shapes.
- [ch. 35, Deployment and security](<0035 Deployment and security.md>) - deployment, key ring, SSO configuration.
- [ch. 31, Audit log and diagnostics](<0031 Audit log and diagnostics.md>) - the horizon guard and the resident estimator's audit entry.
- [ch. 9, GDA integration](<0009 GDA integration.md>) - the bridge a bridge session streams from.
- [ch. 18, Real-time, distribution and dynamic estimation](<0018 Real-time, distribution and dynamic estimation.md>) - the resident estimator.
