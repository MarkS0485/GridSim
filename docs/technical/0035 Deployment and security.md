# 35. Deployment and security

This chapter specifies how the GridSim web host is built, packaged, configured and defended, and how the repository
verifies its own releases. It covers the container image (`Dockerfile`), the Compose service (`compose.yml`), the
systemd unit and the nginx example under `docs/developer/deploy/`, the `GridSim.Web` host (listening addresses, the
data-protection key ring, estate single sign-on, sessions, licence resolution), the GitHub workflows under
`.github/workflows/`, and the threat-model statements in `docs/assurance/` that touch code, each checked against the
source. The web and API surface itself (the REST routes, the `/ws/sim` protocol, sessions) is the subject of
[ch. 34, Web host, REST/WebSocket, SSO and sessions](<0034 Web host, REST and WebSocket, SSO and sessions.md>); this chapter describes those routes only as far
as deployment and security need.

The user view of the same material, with the commands to run, is the user chapter
[26, Audit log, deployment and assurance](<../manual/0026 Audit log, deployment and assurance.md>). The audit log
that the assurance documents lean on is specified in [ch. 31, Audit log and diagnostics](<0031 Audit log and diagnostics.md>).

## Model

### The container image

`Dockerfile` (repository root) has two stages.

| Stage | Base image | What it does |
|---|---|---|
| `build` | `mcr.microsoft.com/dotnet/sdk:10.0` | Copies `Directory.Build.props`, `GridSim.slnx`, `.editorconfig` and the `GridSim.Core` and `GridSim.Web` project files, then runs `dotnet restore` on `GridSim.Web.csproj`, so the package layer is cached until a project file changes. Then copies `LICENSE.md`, `THIRD-PARTY-NOTICES.md`, `data/`, `src/GridSim.Core/` and `src/GridSim.Web/`, and runs `dotnet publish src/GridSim.Web/GridSim.Web.csproj -c Release -p:PublishProfile=portable -p:SourceRevisionId="${SOURCE_REVISION}" --no-restore`. |
| `runtime` | `mcr.microsoft.com/dotnet/aspnet:10.0` | Installs `curl` (only for the health check), copies `/src/artifacts/publish/gridsim-web/` to `/app`, creates `/keys` and `/app/App_Data/keys` owned by `$APP_UID`, switches to `USER $APP_UID`, sets the environment, exposes 8080 and runs `dotnet GridSim.Web.dll`. |

`.editorconfig` is copied because it carries the analyser severities: without it the warnings-as-errors build fails in
the image (`Directory.Build.props` sets `TreatWarningsAsErrors`). `SOURCE_REVISION` is a build argument, empty by
default; `.dockerignore` leaves `.git` out of the build context, so the commit cannot be read in the image and is
stamped into the assemblies as `SourceRevisionId`. Three surfaces print the version, each in its own form:
`gridsim --version` prints `gridsim <release>+<40-character commit>`, the page footer prints `v<release> (<8-character
commit>)` (its tooltip carries the full informational version, `<release>+<commit>`), and `info.version` of
`/api/openapi.json` is the release number alone, without the commit. `.dockerignore` also excludes `.github`, `bin/`, `obj/`,
`artifacts/`, `publish/`, `gda-out/`, `results/`, `data/uk-full/`, `**/App_Data/` and `.vs/`.

The `portable` publish profile (`src/GridSim.Web/Properties/PublishProfiles/portable.pubxml`) is framework-dependent,
not self-contained, not trimmed and not ReadyToRun, and publishes to `artifacts/publish/gridsim-web/`.

Image settings, in the image and not changeable by a build:

| Setting | Value |
|---|---|
| User | `$APP_UID`, the unprivileged `app` user of the .NET base image (1654) |
| Listening address | `ASPNETCORE_URLS=http://+:8080` (HTTP only; the image carries no certificate) |
| Environment | `ASPNETCORE_ENVIRONMENT=Production`, `Auth__KeysDirectory=/keys` |
| Exposed port | 8080 |
| Health check | `curl -fsS http://localhost:8080/ > /dev/null \|\| exit 1`, interval 30 s, timeout 5 s, start period 20 s, 3 retries |
| Entry point | `["dotnet", "GridSim.Web.dll"]` |

### The Compose service

`compose.yml` defines one service, `web`, and one named volume, `gridsim-keys`.

| Item | Value |
|---|---|
| Build | context `.`, build argument `SOURCE_REVISION` from `${GRIDSIM_SOURCE_REVISION:-}` |
| Image, container, restart | `gridsim-web:latest`, `gridsim-web`, `unless-stopped` |
| User | `${GRIDSIM_UID:-1654}:${GRIDSIM_GID:-1654}` |
| Ports | `127.0.0.1:${GRIDSIM_PORT:-5540}:8080` (loopback only on the host) |
| Volume | `${GRIDSIM_KEYS_DIR:-gridsim-keys}:/keys:${GRIDSIM_KEYS_MODE:-rw}` |
| Health check | `curl -fsS http://localhost:8080/`, interval 30 s, timeout 5 s, retries 3, start period 20 s |

The environment the service sets, by name (values are never printed in this chapter; the variable in the second column
is read from the shell or from a `.env` file beside `compose.yml`):

| Container setting | Compose variable (default) |
|---|---|
| `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS` | fixed: `Production`, `http://+:8080` |
| `GridSim__ReplayOnly` | `GRIDSIM_REPLAY_ONLY` (`true`) |
| `GridSim__BridgeEndpoint` | `GRIDSIM_BRIDGE_ENDPOINT` (empty) |
| `Sso__Enabled`, `Sso__Authority` | `GRIDSIM_SSO_ENABLED` (`false`), `GRIDSIM_SSO_AUTHORITY` (empty) |
| `Auth__ApplicationName` | `GRIDSIM_AUTH_APP_NAME` (empty, meaning `GridSim`) |
| `Auth__KeysDirectory` | fixed: `/keys` |
| `Auth__CookieDomain` | `GRIDSIM_COOKIE_DOMAIN` (empty, meaning a host-only cookie) |
| `Site__GoogleTagId`, `Site__AnalyticsScriptUrl`, `Site__AnalyticsCollectorUrl`, `Site__ShareImageUrl` | `GRIDSIM_GOOGLE_TAG_ID`, `GRIDSIM_ANALYTICS_SCRIPT_URL`, `GRIDSIM_ANALYTICS_COLLECTOR`, `GRIDSIM_SHARE_IMAGE_URL` (all empty) |
| `Site__SecurityTxt__Contact`, `Site__SecurityTxt__Expires` | `GRIDSIM_SECURITY_CONTACT`, `GRIDSIM_SECURITY_EXPIRES` (empty) |

`compose.yml` sets no `Sso__IsAuthority`, `Sso__AllowedClients` or `Sso__TicketSeconds`, so a Compose deployment is an
SSO client unless `compose.override.yml` sets them; a list setting in an environment variable takes the indexed form,
`Sso__AllowedClients__0`.

Site wiring that does not fit a variable (an external Docker network, extra mounts, the organisation block and footer
links) goes in `compose.override.yml`, which Compose merges and `.gitignore` excludes; `docs/developer/deploy/compose.override.example.yml`
shows the shape. Licence keys, bridge credentials and any other secret belong in the environment or in an environment
file readable only by the service account, never in the repository.

### The systemd unit

`docs/developer/deploy/gridsim-web.example.service` runs `dotnet /opt/gridsim-web/GridSim.Web.dll` as the user `gridsim` with
`Restart=on-failure`, `RestartSec=5`, `WorkingDirectory=/opt/gridsim-web` and the same address as Compose,
`ASPNETCORE_URLS=http://127.0.0.1:5540`, so one nginx file serves both. The key ring is
`Auth__KeysDirectory=/var/lib/gridsim-web/keys` under `StateDirectory=gridsim-web`. It hardens the process with
`NoNewPrivileges=true`, `ProtectSystem=strict`, `ProtectHome=true` and `PrivateTmp=true`, and offers an optional
`EnvironmentFile=-/etc/gridsim-web/env` for anything secret. The working directory matters: the host reads
`appsettings.json` and `appsettings.Production.json` from its content root, which is the folder it is started in.

### The nginx example

`docs/developer/deploy/nginx-gridsim.example.conf` is one `server` block listening on port 80 (IPv4 and IPv6) for
`gridsim.example.com`. Every `proxy_pass` goes to `http://127.0.0.1:5540` with `proxy_http_version 1.1`.

| Location | Matches | Behaviour |
|---|---|---|
| `^~ /_framework/` | Blazor's content-fingerprinted assets | Sets `Host` and `X-Forwarded-Proto`; `expires 365d`. |
| `^~ /api/` | The REST API | Sets `Host`, `X-Real-IP`, `X-Forwarded-For`, `X-Forwarded-Proto`; `Cache-Control: no-store`. The `^~` prefix stops nginx from trying the regular-expression location below. |
| `~* \.(?:css\|js\|mjs\|png\|jpe?g\|gif\|ico\|svg\|woff2?\|ttf\|eot\|map\|wasm\|json\|geojson)$` | Static files by extension | Sets `Host` and `X-Forwarded-Proto`; `expires 1d`. |
| `/` | Everything else, including `/_blazor`, `/ws/sim`, `/sso/*` and the pages | Sets `Host`, `X-Real-IP`, `X-Forwarded-For` (`$proxy_add_x_forwarded_for`), `X-Forwarded-Proto`, `Upgrade $http_upgrade` and `Connection "upgrade"`; `proxy_read_timeout 3600s`. No cache header. |

Blazor Server keeps one SignalR WebSocket open per browser tab at `/_blazor`. It falls under `location /`, which is why
that block carries the `Upgrade` and `Connection` headers and the one-hour read timeout; a deployment that drops them
serves a page that never becomes interactive. There is no `/_blazor` location of its own.

Compression: `gzip on`, `gzip_vary on`, `gzip_proxied any`, `gzip_comp_level 5`, `gzip_min_length 256`, for text, CSS,
XML, JavaScript, JSON, SVG, WebAssembly, web-manifest and GeoJSON types.

TLS: the example has no `ssl_certificate`. Run `certbot --nginx -d <host>` (the file's own instruction), which adds the
`listen 443`, the certificate lines and the redirect from port 80. TLS ends at nginx; the hop to Kestrel is plain HTTP on
loopback. The example sets no `Strict-Transport-Security`, `Content-Security-Policy` or other security header, and no
request or connection limit.

Because the static-file pattern matches by extension, any `/geo/*.geojson` path also matches it and receives
`expires 1d`. `/api/openapi.json` does not: the `^~ /api/` location takes it first and marks it `no-store`.

### The web host

`src/GridSim.Web/Program.cs` builds the host in this order. `ReplayOnly` is read first, and the order of the services
below it is the order of the statements.

- **Culture.** `InvariantCulture` is pinned for the process, so a comma-decimal host cannot misread `0.5`.
- **Replay-only mode.** `GridSim:ReplayOnly` (from `GridSim__ReplayOnly`). `appsettings.Production.json` sets it `true`; `appsettings.json` does not set it, so outside Production it is `false`. When `true`, the host does not map `/api/*` or `/ws/sim`, does not register the resident real-time estimator, and `/` opens the raw replay of the first case `GridSim:ReplayCases` names (default `gb-full`, so `/dashboard/bridge:gb-full@raw`). Every other `/dashboard/<id>` answers 404 before any page code runs: only `bridge:<case>@raw` for a configured case passes (`SessionManager.IsAllowedHere`). `SessionManager.Create` applies the same rule and throws `ReplayOnlyRefusedException` for anything else, which also covers a navigation inside an open circuit that makes no HTTP request; no live, presolved-replay or non-raw bridge session can be opened. A raw session shows the raw tabs (the AUDIT LOG tab is not among them).
- **Licence.** `LicenceGate.Resolve(builder.Configuration["Licence:Key"])` runs once at start-up and is registered as a singleton. The first non-empty source wins: the `Licence:Key` setting (the environment variable `Licence__Key` binds to it), then the environment variable `GRIDSIM_LICENCE`, then the file `licence.gridsim` under the per-user local application-data folder (`GridSim` subfolder); with none, the free Personal tier (1500 buses, never expires). A source that is present but rejected (bad signature, wrong build, malformed) falls back to the free tier and the reason is shown in the footer's tooltip; it is never silently ignored. Bundled networks are always allowed; a user model over the tier's bus cap throws `LicenceException`, which `/api/solve` and `/api/topology` return as HTTP 403 with `{"error": ...}`. Key format and trust model are in [ch. 22, Licensing: key format and trust model](<0022 Licensing - key format and trust model.md>).
- **Catalogue and sessions.** One `CaseCatalog` for the process and a `SessionManager` singleton, built at start-up so a malformed `GridSim:MaxSessions` stops the host. It holds no sessions, only the count of those alive: at most `GridSim:MaxSessions` (default 64, a whole number of 1 or more) at once; one more throws `SessionLimitException`, shown on the dashboard's load-error panel or as a `/ws/sim` error frame, and a disposed session gives its slot back.
- **Site identity.** `SiteSettings.FromConfiguration` reads and checks every `Site:*` value once; a malformed value stops start-up with a message naming the setting. With nothing configured the page loads no third-party script and `/.well-known/security.txt` answers 404.
- **Data protection.** `AddDataProtection().PersistKeysToFileSystem(<Auth:KeysDirectory>).SetApplicationName(<Auth:ApplicationName>)`, then the key protector `KeyRingProtection` (`src/GridSim.Web/Site/KeyRingProtection.cs`) resolves from `Auth:KeyProtection`. `auto` (the default) is Windows DPAPI for the current account on Windows; elsewhere it is the certificate in `Auth:KeyCertificatePath` when set, otherwise plain keys and a start-up warning naming the settings. `dpapi` and `dpapi-machine` (Windows only), `certificate` (needs `Auth:KeyCertificatePath`, a PKCS#12 file with its private key, and `Auth:KeyCertificatePassword` when it has one) and `none` are explicit. A value that cannot be honoured stops start-up naming the setting; the chosen protector is written to the console at start-up. Encryption applies to keys created from then on; existing plain keys stay readable. The directory is created if missing; unset, it is `App_Data/keys` under the content root. The application name is `Auth:ApplicationName`, or `GridSim` when blank (`SiteSettings.DefaultApplicationName`). Two applications can read each other's cookies and tickets only when they share the key ring, the application name and (when keys are encrypted) the means to decrypt them.
- **Cookie authentication.** The scheme is `IdentityConstants.ApplicationScheme` with the cookie named `.AspNetCore.Identity.Application` (override: `Auth:CookieName`), `SameSite=Lax`, `SecurePolicy=Always` when `Sso:Enabled` is true (GR-733: the sign-in cookie is never issued without `Secure`, whatever scheme the proxy forwards) and `SameAsRequest` otherwise, sliding expiration, and the cookie `Domain` set only when `Auth:CookieDomain` is non-blank. A challenge answers 401 and a forbid 403, with no redirect: the app serves no login or access-denied page, so the cookie handler's default `/Account/Login` and `/Account/AccessDenied` are not used. No page requires sign-in: an unauthenticated visitor is anonymous, and a visitor whose estate cookie decrypts sees `signed in as <name>` in the footer. There is no user store, no login form and no password.
- **Resident estimator.** Registered only when not replay-only and `Realtime:Enabled=true`, with `Realtime:Case` (default `case9`) and `Realtime:WindowsDir`; its latest output is `GET /api/realtime` (404 when it is not registered). The case is loaded once at start-up; a case the licence does not cover, or a name the catalogue does not know, stops start-up with a message naming `Realtime:Case` and the ways out.
- **Pipeline.** The exception handler comes first (outside Development, re-executing the `/Error` page, which shows the request id and no exception detail), so it covers every later middleware; then `/.well-known/security.txt` is answered, ahead of every other middleware, so none of them can claim it; then, on a replay-only instance, the `/dashboard` gate; then forwarded headers, static files, authentication, authorisation, an optional `/geo` static folder, antiforgery, and, when not replay-only, `UseWebSockets`, `MapGroup("/api")` and `/ws/sim`; then the Razor components and `MapEstateSso`.
- **Forwarded headers.** `XForwardedFor` and `XForwardedProto` are honoured only from the proxies `GridSim:TrustedProxies` names (`TrustedProxies.FromConfiguration`, `src/GridSim.Web/Site/TrustedProxies.cs`): default loopback; Docker Compose sets `loopback,private`, because the host proxy reaches the container from the bridge gateway. A malformed value stops start-up naming the setting, and the trusted set is written to stderr at start-up.

The REST and WebSocket routes the host maps when not replay-only are `GET /api/cases`, `GET /api/topology?case=`,
`GET /api/openapi.json`, `GET /api/realtime`, `POST /api/solve` and the WebSocket `/ws/sim`; none requires
authorisation. Their contracts are in [ch. 34](<0034 Web host, REST and WebSocket, SSO and sessions.md>).

### Sessions

A session is one simulation owned by one connection: a Blazor circuit (one browser tab) or one `/ws/sim` WebSocket. It
is created by `SessionManager.Create(caseId, solver)`, steps its own model under its own lock, and is disposed when the
connection ends. No session state is shared between visitors and nothing survives a disconnect; the LIVE tab's
presence and server-load panel (`PresenceService`, [ch. 34](<0034 Web host, REST and WebSocket, SSO and sessions.md>))
is shared with every visitor by design, and shows each viewer's anonymised handle, replay instant, play state and
connect time, and the host's CPU, RAM and thread count. `FeedSource` has three members:

| Kind | Created for | Behaviour |
|---|---|---|
| `Live` | any catalogue case that is not a replay run | A `LiveSim` solves the case in-process: held steady for `CaseKind.BuiltIn` and `CaseKind.Matpower` cases, driven along the GB day curve for the JSON GB cases. The engine is chosen by the `solver` argument (`auto`, `dense`, `sparse`, `sweep`; `?solver=` on the dashboard URL). The analysis cycle is on by default (`Cycle:Enabled`, with `Cycle:WindowMs`, `Cycle:Workers` and `Cycle:Shards` overrides; the default worker count is `min(4, processors - 1)`, at least 1). |
| `Replay` | a catalogue entry of kind `Replay` (a pre-solved run directory) | A `PresolvedReplay` played frame by frame, one settlement period (30 minutes of grid time) per frame. |
| `Bridge` | a case id starting `bridge:` or equal to `live-bridge` | A `GridBridgeClient` streams from the GDA bridge at `GridSim:BridgeEndpoint`. The id is `bridge:<case>[@<source>]` with source `presolved` (default), `estimated`, `live`, `solves` or `raw`; any other source (a typo, or nothing after the `@`) is refused naming the five, never replaced by `presolved`; an empty case means `gb-full`. With no endpoint configured, `Create` throws and the user sees the message. A `Replay` or `Bridge` session runs no solver of its own, so asking one for a specific engine is refused. |

Where a bridge session starts depends on its source. `raw` starts at `RawDefaultStart()`, 1 January of the current year at 00:00 UTC, clamped into the range the bridge reports when that range request answered (it is not clamped when the request failed); `presolved`, `estimated` and `solves` start at the first stored frame and loop; `live` starts at the guard's
horizon less `PresentationLimits.SettlementLagMarginDays` (2 days), 9 days and 1 hour before the moment of the request,
so the server's guard accepts it. Every state frame is also judged by `ForwardInferenceGuard` in `GridBridgeClient` (see
[ch. 31](<0031 Audit log and diagnostics.md>)).
The bridge is specified in [ch. 9, GDA integration](<0009 GDA integration.md>).

### Single sign-on across an estate

`WebSite.Data.Sso.EstateSso` (`src/GridSim.Web/Sso/EstateSso.cs`, options in `EstateSsoOptions.cs`) lets one estate of
applications share a login without an OIDC server or a client secret. Everything rests on the data-protection key ring
and application name that the applications share.

| Option (`Sso:*`) | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch. Off: `/sso/bridge` and `/sso/land` answer 404 and the cookie redirect does nothing; `/sso/logout` also answers 404. |
| `IsAuthority` | `false` | This instance is the login authority and mints tickets at `/sso/bridge`. Exactly one application in an estate sets it. GridSim.Web refuses `true` at start-up (with `Enabled`): it has no login page, so it cannot be the authority. |
| `Authority` | empty | The authority's public origin. A client sends unauthenticated visitors there. |
| `AllowedClients` | empty list | Origins the authority may mint tickets for. Consulted on the authority only. |
| `TicketSeconds` | 120 | Ticket lifetime in seconds. |
| `PublicOrigin` | empty | This client's public origin (`scheme://host[:port]`, no path), used for the landing URL and as the audience a ticket must carry. Empty: the request's own scheme and host. Checked at start-up. |

A client (`Enabled` true, `IsAuthority` false, `Authority` non-blank) post-configures the shared cookie scheme: the cookie
`Domain` is forced to null (host-only), and `OnRedirectToLogin` redirects a non-API request to
`<Authority>/sso/bridge?to=<this origin>/sso/land&r=<original path and query>`, where this origin is `PublicOrigin` when set; an XHR or JSON-only request gets 401
instead. GridSim.Web registers no protected page and no authorisation policy, so it never triggers that redirect; it
acts as a client only in receiving visitors at `/sso/land`.

The three endpoints:

- **`GET /sso/bridge`** (authority only; 404 otherwise). An unauthenticated visitor is challenged. For an authenticated one it reads `to` and `r`. `to` must be an absolute `http` or `https` URL whose path is exactly `/sso/land` (ignoring case) and whose origin (`scheme://authority`) is in `AllowedClients`, else the answer is 400 `invalid target`. It then issues a ticket for that origin and redirects to `<to>?t=<ticket>&r=<r>` (with `&` in place of `?` when `to` already has a query).
- **`GET /sso/land?t=<ticket>&r=<path>`** (404 when `Enabled` is false). The ticket is unprotected and checked; on success the claims in it are rebuilt into a principal, the visitor is signed in to the application's own cookie (persistent), and the browser is redirected to `r` when `r` is a local path (begins `/` and not `//`), else `/`. On any failure the answer is a local redirect to `/`, with no error text, and the reason is logged as a warning (event `SsoTicketRefused`): expired or tampered (or a different ring or application name), wrong audience (naming both origins and, when only the scheme differs, pointing at `X-Forwarded-Proto` and `PublicOrigin`), no nonce, or already used. The ticket is never logged.
- **`GET /sso/logout`** signs out of this application's cookie only and redirects to `/`.

The ticket is a data-protection payload from the time-limited protector with purpose `EstateSso.Ticket.v2`, valid for
`TicketSeconds`. Its JSON content is `a` (audience origin, trailing `/` removed), `j` (a random 32-character `jti`) and
`c` (claims as `[type, value]` pairs). Only these claim types travel: name identifier, name, email, role, the Identity
security stamp, `DisplayName`, `amr` and `preferred_username`. `CheckAsync` (and `ValidateAsync`, its claims only)
accepts a ticket only when it unprotects (genuine and unexpired), the audience equals this application's origin
(compared as origins: scheme, host ignoring case, and port, with the scheme's default port the same as no
port, so `https://host:443` matches `https://host`; the authority's `AllowedClients` entries are compared the same way), it carries a `jti`, and that `jti` has not been spent. Spending is atomic in the in-process
`SsoOneTimeStore`, which needs no configuration and keeps each `jti` until the ticket's own expiry plus 30 seconds, so a
ticket signs in once per instance; a ticket for another audience is refused without being spent. When the host also
registers an `IDistributedCache` (several instances behind one name), the `jti` is recorded there too and a ticket spent
on another instance is refused; a cache fault is logged (event `SsoTicketCacheFault`) and leaves the in-process check in
force.

Two mechanisms exist and share the same ring: a shared cookie for applications under one parent domain
(`Auth:CookieDomain`), and this ticket hand-off for applications on other domains. If the ring or the application name
differs, the cookie cannot be decrypted and the visitor is anonymous; the host still runs.

### Continuous integration and release

All workflows are under `.github/workflows/`; `.github/dependabot.yml` configures dependency updates.

| Workflow | Triggers | Jobs |
|---|---|---|
| `ci.yml` (CI) | called by `release.yml` and `publish.yml` (`workflow_call`); manual (`workflow_dispatch`) | `matrix`: on `ubuntu-latest` and `windows-latest`, each leg builds, reports the CPU vector tier with `gridsim simdcheck`, then runs the full test suite, each with SIMD `normal`, `avx2-off` (`DOTNET_EnableAVX2=0`) and `hwintrinsic-off` (`DOTNET_EnableHWIntrinsic=0`), plus `avx512` (`simdcheck --expect Avx512`) on both, and `normal` and `hwintrinsic-off` on `macos-latest`; `fail-fast: false`, no leg advisory. Each reduced leg fails unless `simdcheck` reports its switch in effect (`Avx2=False` and `Fma=False` for `avx2-off`, which holds the non-FMA axis because on .NET 10 `DOTNET_EnableAVX2=0` turns FMA off too; tier `Scalar`). There is no separate FMA-off leg: .NET 10 ignores `DOTNET_EnableFMA=0`. `vulnerable`: `dotnet list GridSim.slnx package --vulnerable --include-transitive`, failing on any advisory. `container`: build the image with `SOURCE_REVISION` set to the commit, run it on `127.0.0.1:5540`, poll `/` for 60 seconds and require the `build-version` footer. `test` (named `Build & test`): runs always, fails unless `matrix`, `vulnerable` and `container` all succeeded. |
| `release.yml` (Release) | push of a `v*` tag | `verify`: the tag must match `^v[0-9]+\.[0-9]+\.[0-9]+$` and equal the single `<Version>` in `Directory.Build.props`, `CHANGELOG.md` must have its `## [<version>]` heading, then build, test (normal SIMD mode) and the vulnerable-package gate. `cli`: for `win-x64`, `linux-x64` and `osx-arm64` on their own operating systems, publish a self-contained single file, require `--version` to print exactly `gridsim <version>+<commit>` and `case9 --once` to run, require `LICENSE.md`, `THIRD-PARTY-NOTICES.md` and `data/matpower/lib/case9.m` in the output, run `PublishedOutputTests` against the publish folder, archive. On `win-x64` the binary is Authenticode-signed (`signtool sign` then `signtool verify /pa`) when the repository secret `SIGNING_CERT_PFX_BASE64` is set; without it signing is skipped with a warning. `web-image`: build the replay-only container image with `SOURCE_REVISION`, run it on `127.0.0.1:5540` and require the version footer, save it as an artefact. `sbom`: `dotnet-CycloneDX` over `src/GridSim.Cli/GridSim.Cli.csproj` and `src/GridSim.Web/GridSim.Web.csproj` to `gridsim-<version>.sbom.json` and `gridsim-web-<version>.sbom.json`. `release`: write `SHA256SUMS.txt` over every file, cut the CHANGELOG section for the version as the notes, and run `gh release create <tag> ... --verify-tag`. `web-image-push`: push the image to `ghcr.io/<owner>/gridsim-web:<version>` once the Release exists. `nuget`: call `publish.yml` with the verified version. |
| `publish.yml` (Publish packages) | called by `release.yml`; a published GitHub Release; manual | For a published Release or a manual run, job `gate` runs `ci.yml` first and the package job needs it (called by `release.yml`, `gate` is skipped and the release's own gates apply). Determine the version and require it to equal `Directory.Build.props`, pack `GridSim.Core` (the only package that ships; any other package stops the run), sign it with `dotnet nuget sign` when the signing secret is set, and push it to the repository's GitHub Packages feed with `--skip-duplicate`. |
| `codeql.yml` (CodeQL) | manual only | CodeQL `security-and-quality` queries for C# over a manual build of the solution on `windows-latest`. |
| `dependabot-auto-merge.yml` | manual only | Disabled as an automatic trigger. Its job runs only for the Dependabot actor and merges `github.event.pull_request.html_url`, which a hand-started run does not have, so as written it does nothing when started by hand. |

Workflows take `contents: read` by default. The exceptions: the `release` job of `release.yml` takes `contents: write`, the `nuget` job takes `packages: write`, `publish.yml` takes `packages: write` at the top level, `codeql.yml` adds `security-events: write` to its job, and `dependabot-auto-merge.yml` takes `contents: write` and `pull-requests: write`.
The tag name reaches scripts only through environment variables and is validated against the strict pattern first.
`dependabot.yml` covers the NuGet projects (Core, Gda, Gpu, Cli, Web, Wpf and the tests), the Docker base images and the
GitHub Actions, all monthly, with version-update pull requests paused for the release (`open-pull-requests-limit: 0`). NuGet has one group, `nuget-minor-and-patch`, limited to minor and patch updates; GitHub Actions
has one group, `github-actions-all`, that takes every update type; Docker has no group.

The release signs the Windows command-line binary (Authenticode) and the NuGet package when the signing secret is set; the Linux and macOS binaries are not signed. It does not attest build provenance or build installers.

### Threat-model statements checked against the code

`docs/assurance/` holds three documents: `caf-mapping.md`, `nis-oes-gap-assessment.md` and
`rbac-sbom-secrets-notes.md`. They say what GridSim claims about its own security. Each statement below that touches
code was checked against the source. "Holds" means the code does what the statement says; "Behind the code" means the
code does more than the statement says; "Not on main" means the statement names a type, test or file that the
repository does not contain, which is how the documents themselves label a planned control.

| Statement | Verdict | Evidence |
|---|---|---|
| The guard rejects any stamp newer than `utcNow - 169 h`, with `Horizon`, `RejectFuture`, `AssertHistorical`, `RequireNotAfterRealClock` and `RequireDated` as written (caf 3.1; nis 5.1) | Holds | `ForwardInferenceGuard`; `ForwardInferenceGuardTests` |
| There is no flag, role or per-source override; widening the horizon is a source change that fails `RestrictionInvariantTests` (caf 3.2) | Holds | `Inv1_The_horizon_is_a_single_knob` asserts 169.0. `gridsim serve ... --utc-now <iso>` accepts only a clock at or before the real one (`ForwardInferenceGuard.RequireNotAfterRealClock`; a later one is `REJECTED`, exit 2), so it can only tighten the horizon ([ch. 31](<0031 Audit log and diagnostics.md>)). |
| The guard is called at every dated seam listed: files and the lake, replay, streaming, the real-time engines, the RTU interposer, synchrophasors, the CLI clock inputs and the review gate (caf 1.2, 3.1; nis 3.1) | Holds | The seam table in [ch. 31](<0031 Audit log and diagnostics.md>) |
| The bridge applies the exact horizon with no clock-skew allowance (caf 3.1) | Holds | `GridBridgeClient` state handler; `BridgeClientRobustnessTests.A_frame_inside_the_horizon_by_ten_minutes_is_refused` |
| The real-time engine refuses an undated window and skips a fresh one (nis 3.1) | Holds | `RealtimeEstimatorEngine.Run`; `RealtimeEstimatorEngineTests.An_undated_window_is_refused` |
| `RestrictionReview.Reviewed` is the code-resident, test-enforced register of dynamics-fused methods, with no separate document (caf 4.1) | Holds | Four entries (`Ekf`, `Ukf`, `EnKf`, `FrequencyRocofKf`); `RestrictionInvariantTests.Inv5_*` |
| Every emitted series carries a `ProvenanceTag` with the seven named members (caf 4.3) | Holds | `ProvenanceTag`; `AssetProvenanceTests` pins the `[VERIFY]` pairing |
| The web host has cookie sign-in and a single sign-on consumer, runs replay-only in production with `/api` and `/ws/sim` unmapped, and has no authorisation policy (caf 5.2; nis 7.2; rbac 1) | Holds | `Program.cs`, `EstateSso`, `SessionManager`; `WebSecurityDoc2Tests`, `WebHostDoc2Tests` |
| The key ring is encrypted with DPAPI on Windows, with a certificate elsewhere when one is configured, and unencrypted with a warning on Linux without one (caf 5.3; rbac 3) | Holds | `KeyRingProtection`; `WebSecurityDoc2Tests` |
| Sessions are capped at 64, a `/ws/sim` control frame over 16 KiB is refused, and the bridge client refuses a message over 8 MiB (`MaxMessageBytes`) and more than 256 outstanding queries (`MaxInFlightQueries`), the limits the hosts take from `ResilienceOptions`; there is no rate limit (caf 5.4) | Holds | `SessionManager`, `SimWebSocket`, `GridBridgeClient`, `ResilienceOptions`; `ResilienceOptionsTests` |
| `AuditLog` is a SHA-256 hash chain whose `Verify` re-derives every hash and link (caf 6.1; nis 5.2) | Holds | `AuditLog.ComputeHash`; `AuditLogTests`, `AuditLogDeletionTests`, `AuditLogFileTests` |
| The chain persists through `AuditLogWriter` with an exclusive lock and a flush per append; the anchor file is checked on every open and catches tail truncation and whole-log rewrite; it is unsigned, beside the log by default, and there is no write-once or syslog sink and no clock-skew note (caf 6.1, 6.2) | Holds | [ch. 31](<0031 Audit log and diagnostics.md>); `Doc3AuditAnchorTests`, `AuditLogFileTests.Rewriting_the_whole_log_with_fresh_hashes_is_caught_by_the_anchor` |
| The audit trail records one entry per audited run and, at the detection sites, one entry per security event (caf 6.1 table) | Holds | The CLI writes one entry per audited run (`estimate`, `crossval`, `interpose`, `serve`) and the web host one per resident-estimator run; `AuditRunRecorder.RecordSecurityEvent` / `SecurityAudit.Record` add a `SecurityEventKind` entry at the detection sites of `estimate`, `serve`, `interpose` and the resident estimator (a horizon refusal, an SLO breach). The `fdia` verdict and the estimate `status` appear in the outcome text of an audited estimate. `SecurityAuditTests`, `AuditWiringTests` |
| `FdiaScreen.Screen` reports `SuspectedFdia` for a chi-square-consistent estimate whose state breaks a hard law or significantly breaches the operational envelope; a perfectly cloaked attack is caught by the envelope (caf 6.3; nis 5.3) | Holds | `FdiaCloakedAttackTests`, `FdiaScreenTests` |
| `PhysicsCheckRegistry.HardGateLaws` is `{1,2,3,6,8}` (caf 4.2) | Holds | `PhysicsCheckRegistry` |
| `FeasibleWlsStep` demotes a physically impossible converged state to `EstimationStatus.Infeasible` (caf 5.4) | Holds | `WlsStateEstimator` sets `EstimationStatus.Infeasible` at its feasibility acceptance gate; `EstimationResult` documents it |
| `MeasurementSet.Validate` rejects non-finite values, non-positive sigmas, unknown buses and unresolved or ambiguous branches; constrained and feasible methods above the dense limit throw `NotSupportedException` (nis 5.1) | Holds | `MeasurementSet.Validate`, `WlsStateEstimator` |
| `Directory.Build.props` sets `TreatWarningsAsErrors`, `EnableNETAnalyzers`, `AnalysisLevel=latest`, `AnalysisMode=Recommended` and `Nullable=enable` (caf 5.4, 10.5) | Holds | `Directory.Build.props` |
| `GridSim.Core` has no third-party runtime dependency beyond the base class library (caf 4.4; rbac 2) | Holds | `GridSim.Core.csproj` has no `PackageReference`; `GridSim.Web.csproj` has none either |
| The release builds two CycloneDX SBOMs, signs the Windows binary and the NuGet package when the secret is set (and releases them unsigned with a warning when it is not), and attaches a signed build-provenance attestation to every Release file and the image; the release and CI fail on a vulnerable package; CodeQL and Dependabot exist; each project has a NuGet lock file, which no restore yet runs in locked mode (caf 4.3, 4.4, 8; nis 7.3; rbac 2) | Holds | `release.yml`, `publish.yml`, `ci.yml`, `codeql.yml`, `dependabot.yml`; `WorkflowContractTests`; `packages.lock.json` per project, `RestorePackagesWithLockFile` in `Directory.Build.props`. CI runs when the release calls it on a tag, or by hand, and CodeQL by hand only, not on every push. |
| `GridBridgeClient` has a 60 s `QueryTimeout` and a `Faulted` event; `ResilienceOptions` carries a bounded reconnect backoff schedule that no host drives, so there is no supervised reconnect or failover (caf 5.5) | Holds | `GridBridgeClient`, `ResilienceOptions.BackoffDelay`; `ResilienceOptionsTests` |
| The bridge wire is unauthenticated (caf 5.1, 5.2; nis 3.2, 7.2) | Holds | The client wraps a `ClientWebSocket` over the endpoint URI it is given and carries no credentials; a `wss://` URI gives transport encryption, a `ws://` endpoint is refused unless explicitly allowed (`GridSim:BridgeAllowUnencrypted`), and nothing authenticates the peer or the client |
| `RealtimeEstimatorEngine` continues past a failing window; `HealthProbe` folds a run's whole-run counters into a `HealthReport` against a `ServiceLevelObjective`, and a breach at the end of a `serve` run or of the resident estimator's run is recorded as an `AvailabilityDegraded` security event (nis 4, 7.4; rbac 4) | Holds, with a note | `RealtimeEstimatorOptions.ContinueOnError` defaults to true; `HealthProbe`, `ServiceLevelObjective`; `HealthProbeTests`. The counters are cumulative over the run, not a rolling window. The container health check (`GET /`) is an HTTP liveness probe of the web host |
| Estimates and replays are bit-identical run to run, and no estimation code reads the clock (caf 10.3) | Holds | `EstimatorDeterminismTests`, `GdaReplayDeterminismTests`, `EstimationClockIndependenceTests` |
| On the CPU a WLS or robust estimate takes the same sparse step at every size, so it does not depend on the bus count; the dense step a GPU linear solver uses agrees with it to solver tolerance, not to the bit (caf 10.4, gap 9) | Holds | `CanonicalWlsParityTests.The_estimate_is_bit_identical_across_the_dense_sparse_threshold`; `SparseEstimatorTests.Sparse_and_dense_backends_agree_on_a_noisy_set` (Vm 1e-8, Va 1e-7, J relative 1e-6) |
| `OperationRole`, `IdentityContext`, `RoleGuard`, `AccessDeniedException` (caf 5.2, 9) | Library only | The role gate exists in `GridSim.Core.Security`, with an audited overload that records an `AccessDenied` event; no host surface (endpoint, CLI verb or hosted service) calls it, so nothing is authorised by role. `RoleGuardTests` |
| `ISecretStore` (caf 5.3, 8) | Wired | The web host reads its secrets through it (`WebSecretStore` in `src/GridSim.Web/Program.cs`, `KeyRingProtection`), and `SignedAuditAnchor` takes its key through it. `SecretStoreTests`, `DirectorySecretStoreTests` |
| `ResilienceOptions` (caf 5.5) | Partly wired | The message, in-flight and transport limits are applied to every bridge client the web and desktop hosts create; the reconnect schedule is driven by no host. `ResilienceOptionsTests` |
| `IAuditSink`, `SecurityEventMonitor`, `IIncidentSink`, `IncidentRouter`, `IncidentPolicy`, `NullIncidentSink`, `BufferedIncidentSink` (caf 6.4; nis 7, 9) | Wired | The CLI builds its audit sinks (`--audit-forward`) and the incident route (`--incident-log`), the web host likewise (`Audit:ForwardPath`, `Audit:IncidentLogPath`); `IncidentPolicy` thresholds are cumulative counts. `IncidentRouterTests`, `SecurityEventMonitorTests`, `AuditWiringTests` |
| `HealthProbe`, `ServiceLevelObjective`, `BuildProvenance`, `ComplianceAttestation`, `SignedAuditAnchor` (nis 7, 9) | Wired | `HealthProbe` at the end of `serve` and of the resident estimator's run and behind the web host's health answer; `ComplianceAttestation` (with `BuildProvenance`) in the CLI and the web host; `SignedAuditAnchor` behind `--audit-sign-key`, `--audit-verify-key` and `audit verify --pubkey`. `HealthProbeTests`, `SignedAuditAnchorTests` |
| `RoleBasedAccessPolicy`, `ChannelCredentials` | Not on main | Neither type exists. |
| `docs/assurance/security-governance.md`, `risk-register.md`, `incident-response.md` (caf 4.1, 4.2, 7.1) | On main | `docs/assurance/` holds them with the other assurance documents |
| A test enforces that no secret reaches the audit log; a kill-mid-run test recovers a complete verifiable trail (caf 9, 10.2; rbac 3) | Partly | `NoSecretInAuditTests` enforces the first; no kill-mid-run recovery test exists |
| The forensic trail is the persisted, anchored audit trail, with `EstimationAuditTrail` supplying the outcome text (rbac 5) | Holds | `AuditRunRecorder` writing through `AuditLogWriter`; `EstimationAuditTrail.Outcome` |

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Container listening address | `http://+:8080` | URL | Kestrel inside the image | `Dockerfile` `ASPNETCORE_URLS` |
| Host port | 5540, bound to `127.0.0.1` | port | Where Compose publishes the container | `compose.yml` `${GRIDSIM_PORT:-5540}` |
| systemd listening address | `http://127.0.0.1:5540` | URL | Same address as Compose, so one nginx file serves both | `gridsim-web.example.service` |
| nginx upstream | `http://127.0.0.1:5540` | URL | Every `proxy_pass` | `nginx-gridsim.example.conf` |
| Container user | 1654:1654 | uid:gid | The .NET image's `app` user | `GRIDSIM_UID`, `GRIDSIM_GID` |
| Key ring mount | `/keys` | path | Data-protection key ring | `Auth__KeysDirectory` |
| Key ring fallback | `App_Data/keys` under the content root | path | Used when `Auth:KeysDirectory` is blank | `Program.cs` |
| Key protection | `auto`: DPAPI (current account) on Windows; certificate when `Auth:KeyCertificatePath` is set; else none, with a warning | | Encryption at rest of new keys | `Auth:KeyProtection`, `KeyRingProtection` |
| Health check | 30 s interval, 5 s timeout, 20 s start period, 3 retries | | `curl -fsS http://localhost:8080/` | `Dockerfile`, `compose.yml` |
| Replay-only | `true` in Production, unset otherwise | bool | Unmaps `/api/*` and `/ws/sim`; `/` redirects to the raw replay; every other `/dashboard/<id>` is 404 and no non-raw session can be created | `GridSim:ReplayOnly` |
| Session cap | 64 | sessions | Most dashboards plus `/ws/sim` connections at once | `GridSim:MaxSessions`, `SessionManager.DefaultMaxSessions` |
| Bridge endpoint | empty | URL | `wss://` (or, when allowed, `ws://`) address of the GDA bridge; checked at start-up (`SessionManager.ReadBridgeResilience`) | `GridSim:BridgeEndpoint` |
| Allow unencrypted bridge | `false` | `true` / `false` | allow a `ws://` bridge endpoint; without it a `ws://` endpoint stops start-up | `GridSim:BridgeAllowUnencrypted` |
| Analysis cycle | on | bool | Parallel analysis cycle for live sessions | `Cycle:Enabled` |
| Cycle workers | `min(4, processors - 1)`, at least 1 | threads | Background fan-out | `PresentationLimits.DefaultCycleWorkerCap`, `Cycle:Workers` |
| Resident estimator | off; case `case9` | | Registered only when not replay-only | `Realtime:Enabled`, `Realtime:Case`, `Realtime:WindowsDir` |
| Licence key | `Licence:Key`, then `GRIDSIM_LICENCE`, then `licence.gridsim` | | Precedence of `LicenceGate.Resolve` | `LicenceGate.EnvVar`, `LicenceGate.FileExtension` |
| Free tier | Personal, 1500 | buses | Applies when no key verifies | `Entitlement.FreeBusLimit` |
| SSO | off | bool | `Sso:Enabled`; authority blank | `EstateSsoOptions` |
| SSO ticket lifetime | 120 | s | `Sso:TicketSeconds` | `EstateSsoOptions.TicketSeconds` |
| SSO ticket purpose | `EstateSso.Ticket.v2` | text | Data-protection protector purpose | `EstateSsoTickets` |
| SSO spent-ticket margin | 30 | s | How long past its own expiry a spent ticket's `jti` is kept | `EstateSsoTickets.SpentMargin` |
| SSO public origin | empty (the request's origin) | URL | Landing URL and ticket audience | `Sso:PublicOrigin` |
| Cookie | `.AspNetCore.Identity.Application`, `SameSite=Lax`, `SecurePolicy=Always` with sign-on on (else `SameAsRequest`), sliding | | Shared estate cookie | `Program.cs` |
| Application name | `GridSim` | text | Data-protection discriminator when `Auth:ApplicationName` is blank | `SiteSettings.DefaultApplicationName` |
| `/ws/sim` control frame cap | 16 KiB | bytes | A larger client frame is refused | `SimWebSocket.maxControlBytes` |
| nginx read timeout | 3600 | s | Keeps the `/_blazor` circuit open | `proxy_read_timeout` |
| nginx cache | 365 days for `/_framework/`, 1 day for static extensions, `no-store` for `/api/` | | `expires`, `Cache-Control` | `nginx-gridsim.example.conf` |
| gzip | level 5, minimum 256 bytes | | Compression of text-like responses | `nginx-gridsim.example.conf` |
| Live bridge start margin | 2 | days | Added to the 169 h horizon for a `live` bridge session's start time | `PresentationLimits.SettlementLagMarginDays` |

## Invariants

Every test below is in `tests/GridSim.Tests`.

| Invariant | Pinned by |
|---|---|
| One port everywhere: Compose publishes 5540, the systemd unit binds 127.0.0.1:5540, every nginx `proxy_pass` is port 5540 | `DeploymentDefaultsTests.One_port_everywhere_compose_systemd_and_nginx` |
| The deployment files name no personal path, private address or specific estate; the web sources carry no site identity | `DeploymentDefaultsTests.Deployment_files_name_no_personal_path_private_address_or_specific_estate`, `Web_sources_carry_no_site_identity_of_their_own` |
| Single sign-on is off unless a deployment turns it on, and `/sso/bridge` answers 404 when off | `DeploymentDefaultsTests.Single_sign_on_is_off_unless_a_deployment_turns_it_on`, `WebHostTests.Sso_endpoints_are_off_by_default` |
| Production starts replay-only; replay-only maps neither `/api` nor `/ws/sim`; a full instance serves both | `DeploymentDefaultsTests.Production_defaults_to_replay_only`, `WebHostTests.Replay_only_maps_neither_the_api_nor_the_sim_websocket`, `Api_lists_cases_and_solves_case9_when_not_replay_only`, `Sim_websocket_accepts_a_connection_when_not_replay_only` |
| Replay-only refuses every dashboard route but `bridge:<case>@raw` (404) and `SessionManager.Create` refuses every other session | `WebSecurityDoc2Tests.Replay_only_refuses_every_dashboard_route_but_the_raw_replay`, `Replay_only_still_serves_the_raw_replay_page`, `Replay_only_session_manager_refuses_live_sessions_on_every_path`, `Only_bridge_raw_ids_are_allowed_on_a_replay_only_instance`, `A_full_instance_still_opens_a_live_dashboard` |
| An SSO ticket signs in once (also under a race and without a shared cache); wrong audience, expired, tampered and nonce-less tickets are refused with a reason; a shared cache refuses a ticket spent on another instance; `/sso/land` keeps its redirect local | `WebSecurityDoc2Tests.A_sso_ticket_signs_in_once_and_a_replay_is_refused`, `The_same_ticket_raced_from_many_requests_signs_in_exactly_once`, `A_ticket_for_another_app_is_refused_and_not_spent`, `Expired_tampered_and_nonce_less_tickets_are_refused_with_a_reason`, `With_a_shared_cache_a_ticket_spent_on_one_instance_is_refused_on_another`, `Land_redirect_is_kept_on_this_site`, `One_time_store_spends_a_nonce_once_and_forgets_it_only_after_its_keep_time` |
| `Sso:PublicOrigin` is the audience when set and is checked at start-up; an http-vs-https mismatch names the proxy header; `Sso:IsAuthority=true` stops start-up | `WebSecurityDoc2Tests.Sso_public_origin_is_the_audience_when_configured`, `A_malformed_sso_public_origin_stops_start_up`, `An_http_vs_https_audience_mismatch_names_the_proxy_header_and_the_setting`, `Sso_authority_mode_stops_start_up_with_the_reason` |
| A cookie challenge answers 401 and a forbid 403, with no redirect | `WebSecurityDoc2Tests.A_cookie_challenge_answers_401_and_a_forbid_403_without_a_redirect` |
| New keys are encrypted at rest (DPAPI on Windows by default; a certificate when configured, readable by a second host); an unusable protection setting stops start-up | `WebSecurityDoc2Tests.Default_host_encrypts_new_keys_at_rest_on_windows`, `Certificate_mode_encrypts_new_keys_and_a_second_host_reads_them_back`, `Auto_mode_picks_dpapi_on_windows_and_warns_elsewhere_without_a_certificate`, `Explicit_modes_are_honoured_or_refused_naming_the_setting`, `An_unusable_key_protection_setting_stops_start_up` |
| The session cap refuses one session too many, a disposed session frees its slot, and a malformed cap stops start-up | `WebHostDoc2Tests.Sessions_beyond_the_cap_are_refused_and_a_closed_one_frees_its_slot`, `The_session_cap_defaults_to_64_and_a_bad_value_is_refused` |
| An unloadable `Realtime:Case` stops start-up naming the setting; `/Error` is served; `dotnet run` starts in Development | `WebHostDoc2Tests.An_unloadable_realtime_case_stops_start_up_naming_the_setting`, `The_error_page_named_by_the_exception_handler_is_served`, `Dotnet_run_starts_the_web_app_in_development` |
| The nginx example keeps `/api/` out of the static-asset cache | `WebHostDoc2Tests.Nginx_example_keeps_the_api_out_of_the_static_asset_cache` |
| Compose builds from a Dockerfile that exists, copies `.editorconfig`, publishes with `portable` and runs as `$APP_UID`; `compose.override.yml` is ignored by source control | `DeploymentDefaultsTests.Compose_builds_from_a_dockerfile_that_exists_and_builds_strictly` |
| The desktop app has no default bridge server | `DeploymentDefaultsTests.Desktop_app_has_no_default_bridge_server` |
| The data-protection application name defaults to `GridSim` and follows `Auth:ApplicationName` | `WebHostTests.Data_protection_application_name_defaults_to_the_product_name` |
| `security.txt` is answered first, is exactly the configured file, and is 404 with no contact | `WebHostTests.Security_txt_is_served_before_anything_else`, `Security_txt_is_exactly_the_configured_file`, `Security_txt_answers_404_when_no_contact_is_configured` |
| A malformed `Site:*` value stops start-up naming the setting; the default page is neutral | `WebHostTests.A_malformed_site_setting_stops_start_up`, `Home_page_is_neutral_by_default`, `Home_page_carries_the_configured_site_values` |
| The home page serves anonymously with the build version; the OpenAPI document carries the version | `WebHostTests.Home_page_serves_anonymously_and_shows_the_build_version`, `Openapi_document_carries_the_central_version` |
| The licence resolves explicit key, then environment, then file, then free; a rejected key falls back to free with a visible note | `LicensingTests.Resolution_precedence_is_explicit_then_env_then_file`, `With_no_key_anywhere_resolution_falls_back_to_free`, `A_present_but_rejected_key_falls_back_to_free_with_a_note`, `LicensingReleaseTests.A_foreign_key_from_the_environment_falls_back_to_free_with_a_visible_note` |
| CI covers both operating systems and all three SIMD modes and keeps the `Build & test` check | `WorkflowContractTests.Ci_runs_both_operating_systems_and_all_three_simd_modes` |
| The release is tag-driven, refuses a tag that disagrees with `Directory.Build.props` or a missing CHANGELOG heading, smoke-tests each platform's binary, builds the CycloneDX SBOM and the checksums | `WorkflowContractTests.Release_is_tag_driven_and_gated_on_the_source_version` |
| Every publish profile the workflows use exists with ReadyToRun and trimming off | `WorkflowContractTests.Every_release_profile_the_workflow_uses_exists` |
| Dependabot watches every project and the Docker images | `WorkflowContractTests.Dependabot_watches_every_project` |
| The vulnerable-package gate and CodeQL exist | `WorkflowContractTests.Security_gates_exist` |
| The published output matches the build (single file, data beside it, version stamp, same `case9` report) | `PublishedOutputTests` (run by `release.yml` against each real publish folder; skipped in ordinary runs) |
| One version everywhere, with a CHANGELOG heading for it | `VersionContractTests` |

No test pins the authority's `AllowedClients` check (GridSim is never the authority), the forwarded-header handling,
or the systemd file beyond the port.

## Worked example

The built web host, started from the folder that holds its `appsettings*.json` (the settings are read from the content
root), probed with `curl`, then stopped. The captures show real status codes from the build; the server was started
for each capture and stopped afterwards. The container image, Compose and the health check are read from the files and
pinned by the tests; they are not run here.

Production defaults. `ASPNETCORE_ENVIRONMENT=Production` loads `appsettings.Production.json`, which sets
`GridSim:ReplayOnly=true`. The home page redirects to the raw replay, the API and `security.txt` and both SSO endpoints
answer 404:

```
$ cd src/GridSim.Web/bin/Release/net10.0 && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5540 dotnet GridSim.Web.dll
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/"
302 http://127.0.0.1:5540/dashboard/bridge:gb-full@raw
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/api/cases"
404 
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/api/openapi.json"
404 
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/.well-known/security.txt"
404 
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/sso/bridge"
404 
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/sso/land?t=x"
404 
[exit 0]
```

A full instance, `GridSim__ReplayOnly=false`: the home page is served and the API is mapped (it has no
authorisation, which is why this is for a trusted network only):

```
$ cd src/GridSim.Web/bin/Release/net10.0 && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5540 GridSim__ReplayOnly=false dotnet GridSim.Web.dll
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/"
200 
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/api/cases"
200 
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/api/openapi.json"
200 
$ curl -s http://127.0.0.1:5540/api/cases | head -c 120
[{"id":"case9","label":"IEEE 9-bus (WSCC) - ground truth","kind":"BuiltIn","replay":false},{"id":"case14","label":"IEEE 
[exit 0]
```

A single sign-on client, `Sso__Enabled=true` with an authority. A garbage ticket at `/sso/land` is not an error: it is
a local redirect to `/` (the visitor stays anonymous). `/sso/bridge` is 404 because this instance is not the authority,
`/sso/logout` redirects to `/` (with sign-on off it answers 404), and `/` still lands on the raw replay because
the instance is replay-only:

```
$ cd src/GridSim.Web/bin/Release/net10.0 && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5540 Sso__Enabled=true Sso__Authority=https://login.example.com dotnet GridSim.Web.dll
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/sso/land?t=garbage&r=/dashboard"
302 http://127.0.0.1:5540/
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/sso/bridge"
404 
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/sso/logout"
302 http://127.0.0.1:5540/
$ curl -s -o /dev/null -w "%{http_code} %{redirect_url}\n" "http://127.0.0.1:5540/"
302 http://127.0.0.1:5540/dashboard/bridge:gb-full@raw
[exit 0]
```

On a replay-only instance every dashboard address but the raw replay answers 404 before any session is made, and the
raw session has no AUDIT LOG tab. The footer prints the release and the 8-character commit (this capture was taken at
1.95.186; no bridge is configured, so the raw page shows its load-error panel):

```
$ cd src/GridSim.Web/bin/Release/net10.0 && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5540 dotnet GridSim.Web.dll
GridSim.Web: data-protection keys are encrypted at rest with Windows DPAPI (current account; Auth:KeyProtection=auto).
$ curl -s -o /dev/null -w "%{http_code}\n" "http://127.0.0.1:5540/dashboard/case9"
404
$ curl -s -o /dev/null -w "%{http_code}\n" "http://127.0.0.1:5540/dashboard/live-bridge"
404
$ curl -s "http://127.0.0.1:5540/dashboard/case9"
Not available: this is a replay-only instance (GridSim:ReplayOnly=true), which offers only the raw public-data replay.
$ curl -s -o /dev/null -w "%{http_code}\n" "http://127.0.0.1:5540/dashboard/bridge:gb-full@raw"
200
$ curl -s "http://127.0.0.1:5540/dashboard/bridge:gb-full@raw" | grep -o "AUDIT LOG" | wc -l
0
$ curl -s "http://127.0.0.1:5540/dashboard/bridge:gb-full@raw" | grep -o "v1\.95\.[^<]*"
v1.95.186 (e848f826)
[exit 0]
```

On a full instance `info.version` carries the release number only, and `gridsim --version` carries the commit in full:

```
$ cd src/GridSim.Web/bin/Release/net10.0 && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5540 GridSim__ReplayOnly=false dotnet GridSim.Web.dll
$ curl -s "http://127.0.0.1:5540/api/openapi.json" | python -c "import json,sys; print(json.load(sys.stdin)[\"info\"][\"version\"])"
1.95.149
$ curl -s "http://127.0.0.1:5540/dashboard/case9" | grep -o "v1\.95\.[^<]*"
v1.95.149 (5e6796e4)
[exit 0]
```

```
$ gridsim --version
gridsim 1.95.467+203c6b9cc0edff55f2bd73642c94dc0963a82b92
[exit 0]
```

Started from the repository root, giving the path to the DLL instead, the host does not find
`appsettings.Production.json` (it reads its settings from the working directory) and starts as a full instance, with
`/api/cases` answering 200: the working directory decides the mode, which is why the systemd unit sets
`WorkingDirectory`.

## Source map

| File | Type or section | Responsibility |
|---|---|---|
| `Dockerfile`, `.dockerignore` | Two-stage image | Build, publish, runtime image, user, port, health check. |
| `compose.yml`, `docs/developer/deploy/compose.override.example.yml` | Service `web`, volume `gridsim-keys` | Address, user, key-ring volume, environment, health check. |
| `docs/developer/deploy/gridsim-web.example.service` | systemd unit | Non-Docker run on the same address, with hardening. |
| `docs/developer/deploy/nginx-gridsim.example.conf` | `server` block | Proxy, WebSocket upgrade, caching, gzip. |
| `docs/developer/deploy/README.md` | Deployment guide | Defaults, Compose usage, the `Site:*` table. |
| `src/GridSim.Web/Program.cs` | Host builder | Replay-only (and its `/dashboard` gate), licence, site identity, data protection, cookie scheme, pipeline, forwarded headers. |
| `src/GridSim.Web/Site/KeyRingProtection.cs` | `KeyRingProtection` | `Auth:KeyProtection` modes and the key protector. |
| `src/GridSim.Web/Components/Pages/Error.razor` | `/Error` | The exception handler's page. |
| `src/GridSim.Web/appsettings.json`, `appsettings.Production.json` | Settings | SSO off; replay-only in Production. |
| `src/GridSim.Web/Sso/EstateSso.cs` | `EstateSso`, `EstateSsoTickets`, `SsoOneTimeStore`, `SsoTicketCheck` | The three endpoints, the redirect, ticket issue, validation and single use. |
| `src/GridSim.Web/Sso/EstateSsoOptions.cs` | `EstateSsoOptions` | `Sso:*` options and defaults. |
| `src/GridSim.Web/Sessions/SessionManager.cs`, `SimSession.cs` | `SessionManager`, `SimSession`, `FeedSource`, `ReplayOnlyRefusedException`, `SessionLimitException` | Session construction, the Live, Replay and Bridge kinds, the replay-only refusal and the session cap. |
| `src/GridSim.Web/Api/RestApi.cs`, `SimWebSocket.cs` | REST group, `/ws/sim` | The surfaces that replay-only unmaps. |
| `src/GridSim.Web/Site/SiteSettings.cs` | `SiteSettings` | Validated `Site:*` and the default application name. |
| `src/GridSim.Core/Licensing/LicenceGate.cs` | `LicenceGate` | Licence precedence, tier check, free fallback. |
| `.github/workflows/ci.yml`, `release.yml`, `publish.yml`, `codeql.yml`, `dependabot-auto-merge.yml` | Workflows | Verification, release, packages, scanning. |
| `.github/dependabot.yml` | Update policy | NuGet, Docker and Actions updates. |
| `docs/assurance/caf-mapping.md`, `nis-oes-gap-assessment.md`, `rbac-sbom-secrets-notes.md` | Assurance documents | Claims and gaps, checked in this chapter. |
| `tests/GridSim.Tests/DeploymentDefaultsTests.cs`, `WebHostTests.cs`, `WebSecurityDoc2Tests.cs`, `WebHostDoc2Tests.cs`, `WorkflowContractTests.cs`, `LicensingTests.cs`, `VersionContractTests.cs`, `PublishedOutputTests.cs` | Tests | The invariants above. |

## Limits

- The solve API and `/ws/sim` have no authorisation or rate limit; beyond the 16 KiB control-frame limit the only bound is the session cap (`GridSim:MaxSessions`), which is why Production starts replay-only.
- SSO single use is per instance unless the host registers a shared `IDistributedCache`; GridSim.Web registers none itself, so a multi-instance deployment must add one.
- On a non-Windows host with no `Auth:KeyCertificatePath`, new keys are still written without encryption at rest (a start-up warning says so); the folder's permissions are then the only protection. Keys written before key protection was configured stay plain until they expire or are removed.
- With `GridSim:TrustedProxies` including `private` (the Compose setting), any host on a private network can set `X-Forwarded-For` and `X-Forwarded-Proto`; the published host port is bound to `127.0.0.1` for that reason.
- Kestrel serves HTTP only and the nginx example listens on port 80; TLS, HSTS and every other security header are the deployer's to add, and neither the host nor the example sets any.
- The container health check requests `/`, so it shows that the page renders, not that the solver or the bridge works.
- The bridge wire carries no credentials: a bridge session connects with whatever the endpoint URI gives, and nothing authenticates the peer.
- The licence is resolved once at start-up; a changed key needs a restart.
- Role-based access control is built as a library gate that no host calls, so nothing is authorised by role; supervised reconnect and failover are not built (the backoff schedule exists, no host drives it). The secrets store, per-event security audit, incident routing, health probe and signed audit anchor are wired as the table above says.
- The release signs only the Windows binary and the NuGet package, and only when the signing secret is set; it does not attest build provenance or build installers. CI runs only on a release tag or by hand, CodeQL by hand only.
- The container image, Compose service and health check are read from the files and are not run here; the uid 1654 is a Compose default, not a value stored in the repository.
- Replay-only mode allows the raw replay `bridge:<case>@raw` of the cases `GridSim:ReplayCases` names (default `gb-full`) and nothing else.
- `dependabot-auto-merge.yml` does nothing when started by hand.

## See also

- [Ch. 26, Audit log, deployment and assurance](<../manual/0026 Audit log, deployment and assurance.md>) - the user view of the log, the service and the assurance documents.
- [Ch. 31, Audit log and diagnostics](<0031 Audit log and diagnostics.md>) - the audit log, the forward-inference guard and the review gate that the assurance documents rely on.
- [Ch. 3, Configuration](<../manual/0003 Configuration.md>) - the web app's settings and the CLI environment variables.
- [Ch. 4, Licensing and activation](<../manual/0004 Licensing and activation.md>) and [technical ch. 22, Licensing: key format and trust model](<0022 Licensing - key format and trust model.md>) - tiers, keys and the trust model.
- [Ch. 24, The GDA bridge](<../manual/0024 The GDA bridge.md>) and [technical ch. 9, GDA integration](<0009 GDA integration.md>) - the bridge that a bridge session streams from.
- [Technical ch. 1, Architecture](<0001 Architecture.md>) - the projects and how the web host sits on the core.
- `docs/developer/deploy/README.md`, `compose.yml`, `Dockerfile`, `.github/workflows/` and `docs/assurance/` - the files this chapter describes.
