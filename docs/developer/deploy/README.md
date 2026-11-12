# Deploying the GridSim web app

The web app (`src/GridSim.Web`, Blazor Server plus a REST / WebSocket API) is deployed one of two ways. Both listen
on `127.0.0.1:5540` behind a reverse proxy, and both take every machine-specific setting from environment variables:
nothing in the repository names a real host, user or path.

**The released image.** Each release (F25, the 1.0 ship scope) pushes the image built from the `Dockerfile` to the
repository owner's GitHub Container Registry as `ghcr.io/<owner>/gridsim-web:<version>`, after probing that it starts,
shows that version in its footer and is replay-only. It is the only form in which the web app ships: there is no web
zip. Run it in place of a local build (`docker run --rm -p 127.0.0.1:5540:8080 ghcr.io/<owner>/gridsim-web:<version>`,
or set `image:` in `compose.yml`); everything below applies unchanged.

| File | Purpose |
|---|---|
| `Dockerfile` (repo root) | Two-stage container image: the .NET SDK builds with the `portable` publish profile, the ASP.NET Core runtime image runs it as the unprivileged `app` user. |
| `compose.yml` (repo root) | Runs the image on `127.0.0.1:${GRIDSIM_PORT:-5540}`; every setting is a variable with a safe default (listed at the top of the file). |
| `compose.override.example.yml` | Shape of a local `compose.override.yml` for site wiring such as an external Docker network. |
| `gridsim-web.example.service` | systemd unit for a host without Docker, on the same `127.0.0.1:5540`. |
| `nginx-gridsim.example.conf` | nginx reverse proxy for either deployment (WebSocket upgrade for `/_blazor`, caching of static assets but never of `/api/`, gzip). |

## Defaults

- **Replay-only in Production.** `appsettings.Production.json` sets `GridSim:ReplayOnly=true`: only the public-data
  replay is offered, and the solve API (`/api/*`) and the simulation WebSocket (`/ws/sim`) are not mapped. Every
  other dashboard address (`/dashboard/case9`, `/dashboard/live-bridge`, any bridge source but `@raw`) answers 404,
  and no live session can be opened by any route. The API and socket have no authorisation or rate limit yet
  (release register F14); set `GridSim__ReplayOnly=false` only on a trusted network.
- **Session cap.** At most `GridSim:MaxSessions` sessions (dashboard tabs plus `/ws/sim` connections) run at once,
  64 by default; one more is refused with a message until another closes.
- **Single sign-on off.** `appsettings.json` has `Sso:Enabled=false` and no authority, so every visitor is anonymous
  and every page still works. To join an SSO estate set `Sso__Enabled=true`, `Sso__Authority=<login origin>`,
  `Auth__ApplicationName`, `Auth__CookieDomain` and point `Auth__KeysDirectory` at the estate's shared key ring.
- **Neutral site identity.** With no `Site:*` settings the page loads no analytics or other third-party script,
  carries no share-card image or organisation block, shows no footer links, and `/.well-known/security.txt` answers
  404. The data-protection application name is `GridSim` unless `Auth__ApplicationName` is set (an estate that shares
  cookies across apps must set it to the estate's name). Every value is checked at start-up; a malformed one (a
  relative URL, a tag id that is not one, a security contact without a future expiry, a line break) stops the app
  with a message naming the setting. See [Site identity settings](#site-identity-settings).
- **No GDA bridge.** `GridSim__BridgeEndpoint` is empty, so the live bridge source is refused with a clear message.
- **Key ring.** Compose mounts the named volume `gridsim-keys` at `/keys`; a host folder can be used instead with
  `GRIDSIM_KEYS_DIR` (and `GRIDSIM_KEYS_MODE=ro` for a ring another app owns), with `GRIDSIM_UID` / `GRIDSIM_GID` set
  to its owner. New keys are encrypted at rest when the platform allows: see [Security settings](#security-settings).

## Security settings

As environment variables, `:` becomes `__`. Every value is checked at start-up; one that cannot be honoured stops the
app with a message naming the setting.

| Setting | Default | Effect |
|---|---|---|
| `Auth:SlidingHours`, `Auth:AbsoluteDays` | `8`, `14` | Sign-in cookie lifetime: idle hours before sign-in is needed again, and days from sign-in after which it is needed regardless. Whole numbers; days must not be shorter than the hours. |
| `Auth:KeyProtection` | `auto` | Encryption at rest of new data-protection keys. `auto`: Windows DPAPI (current account) on Windows; elsewhere a certificate when `Auth:KeyCertificatePath` is set, otherwise plain keys and a start-up warning. `dpapi`, `dpapi-machine` (Windows only), `certificate`, `none` (plain, on purpose). Existing keys are not rewritten. |
| `Auth:KeyCertificatePath` | empty | PKCS#12 (`.pfx`) file with the certificate and its private key. Every app sharing an estate key ring needs the same certificate. |
| `Auth:KeyCertificatePassword` | empty | That file's password. Supply it from an environment file or secret store only. |
| `Auth:KeyPreviousCertificatePath` | empty | Certificate rotation (N346): a second PKCS#12 file, the certificate being rotated out. It only decrypts keys written before the rotation; nothing new is encrypted to it. It must carry its private key and differ from `Auth:KeyCertificatePath`, or start-up stops. Honoured in every mode. |
| `Auth:KeyPreviousCertificatePassword` | empty | That file's password, from an environment file or secret store only. |
| `Sso:PublicOrigin` | empty | This app's public origin (for example `https://gridsim.example.com`) for the SSO landing URL and the ticket audience. Set it when the reverse proxy does not forward `X-Forwarded-Proto`; empty uses the request's own origin. A refused sign-in is logged with its reason. |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | The host names this instance answers (bug #170, the standard ASP.NET Core key; as an environment variable `AllowedHosts`, in Compose `GRIDSIM_ALLOWED_HOSTS`). Semicolon-separated names, `*.example.com` for any sub-domain, IPv6 in brackets; the port is ignored. A request under any other name gets a 400 whose text names this setting, before any page, API call or WebSocket runs: this is what stops a DNS-rebinding page reaching the instance. **Behind a reverse proxy that forwards `Host` (the nginx example does), set the public name, for example `AllowedHosts=gridsim.example.com`.** `*` alone answers any name (the earlier behaviour) and removes that protection; the start-up log then warns, and `/ws/sim` accepts a page's origin only when it is loopback or listed in `GridSim:AllowedOrigins`. An empty entry, a scheme, path or port in an entry, or `*` with other entries stops start-up. |
| `GridSim:AllowedOrigins` | empty | Extra page origins (comma-separated, for example `https://ops.example.com`) that may open `/ws/sim` from a browser. A page whose origin equals the request's host is accepted while `AllowedHosts` is a list of names. |
| `GridSim:AnonymousRole` | `Operator` when every `AllowedHosts` name is a loopback name, else `Viewer` | The role band (`Viewer`, `Analyst`, `Operator`, `Administrator`) of a caller who is not signed in (environment variable `GridSim__AnonymousRole`). `POST /api/solve` needs `Analyst` and every `/ws/sim` control frame `Operator`; a refusal is a 403 or an error frame, and is written to the audit log as an `AccessDenied` entry when `Audit:LogPath` is set. A signed-in caller also holds the highest band among its `GridSim.<band>` role claims (for example `GridSim.Operator`). **Behind a reverse proxy under a public name, anonymous callers can therefore read but not solve**; give operators the role claim through sign-on, or set this on purpose (start-up warns when it gives more than `Viewer` to an instance that answers other names). Any other value stops start-up. The dashboard's own sessions are not role-gated. |
| `GridSim:FrameAncestors` | `'none'` | Who may show the pages in a frame (bug #179): the Content-Security-Policy `frame-ancestors` source list sent on every answer, with the matching `X-Frame-Options` (`DENY` for `'none'`, `SAMEORIGIN` for `'self'`). For example `'self' https://portal.example.com` to let a named portal embed the dashboard; `*` lets any site frame it (not recommended). Every answer also carries `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`. A semicolon, comma or line break, or a quoted word other than `'none'` / `'self'`, stops start-up. |
| `GridSim:Hsts` | `true` | Outside Development the app sends `Strict-Transport-Security: max-age=31536000` on HTTPS requests (a request the trusted proxy forwarded as https counts; never for the loopback names). `false` leaves HSTS to the reverse proxy. |
| `GridSim:MaxSessions` | `64` | Most sessions held at once (a whole number, 1 or more). |
| `GridSim:MaxSessionsPerClient` | `16` | Most sessions one client (signed-in user, else remote address) holds at once, inside the cap above (a whole number, 1 or more). |
| `GridSim:ReplayCases` | `gb-full` | On a replay-only instance, the remote cases whose raw replay (`bridge:<case>@raw`) may be opened (N346): a comma-separated list of case names (letters, digits, `-`, `_`, `.`). Any other raw replay is refused by name (the page answers 404). The home page lands on the first. An empty entry, any other character or a name given twice stops start-up. |
| `GridSim:TrustedProxies` | `loopback` (Compose: `loopback,private`) | Which reverse proxies the app believes when they send `X-Forwarded-For` and `X-Forwarded-Proto` (N226). A comma-separated list of `loopback` (127.0.0.0/8, ::1/128), `private` (the RFC 1918 networks and fc00::/7, where a container's bridge gateway is), IP addresses and CIDR networks; or `any` alone, which believes every caller (the behaviour before N226; only where nothing but the proxy can reach the app). A header from any other caller is ignored, so behind an untrusted hop the request is seen as plain http. An empty entry, a malformed address or network, or `any` with other entries stops start-up. The start-up log names the networks in force. |

Rotating the key-ring certificate (N346): put the new certificate in `Auth:KeyCertificatePath` (and its password), the
old one in `Auth:KeyPreviousCertificatePath` (and its password), and restart every app that shares the ring. New keys are
encrypted to the new certificate and keys written before still decrypt, so signed-in users and SSO tickets carry on;
the start-up log names both thumbprints. Once every key encrypted to the old certificate has expired (90 days after the
rotation by default) or its key file has been removed, clear the two previous settings and retire the old certificate.

SSO tickets are single-use on each instance without any extra service. A deployment that runs several instances
behind one name must also register a shared distributed cache so a ticket spent on one is refused on the others.
GridSim cannot be the SSO authority (`Sso:IsAuthority=true` stops start-up): it has no login page.

## Docker Compose

```bash
docker compose up -d --build                 # http://127.0.0.1:5540/
GRIDSIM_SOURCE_REVISION=$(git rev-parse HEAD) docker compose build   # stamp the commit into the build
```

Put machine-specific values in a `.env` file beside `compose.yml` (not committed), for example:

```bash
GRIDSIM_BRIDGE_ENDPOINT=ws://bridge.example.internal:8770/ws/bridge
GRIDSIM_BRIDGE_ALLOW_UNENCRYPTED=true        # ws:// is refused without this; private network only, else use wss://
GRIDSIM_SSO_ENABLED=true
GRIDSIM_SSO_AUTHORITY=https://login.example.com
GRIDSIM_KEYS_DIR=/srv/estate/keys
GRIDSIM_KEYS_MODE=ro
GRIDSIM_UID=1000
GRIDSIM_GID=1000
```

## Site identity settings

Set these only for a deployment that should carry its own analytics, share card, security contact or links. As
environment variables, `:` becomes `__` (for example `Site__SecurityTxt__Contact`). Compose maps the first six from
`GRIDSIM_*` variables (listed at the top of `compose.yml`); the rest go in `compose.override.yml` or the systemd
`EnvironmentFile`.

| Setting | Effect when set |
|---|---|
| `Site:GoogleTagId` | Loads the Google tag (gtag.js) with this id, e.g. `G-XXXXXXXXXX`. |
| `Site:AnalyticsScriptUrl` | Loads this first-party analytics script (absolute URL) and sends it a page view on each navigation. |
| `Site:AnalyticsCollectorUrl` | The script's `data-collector` attribute (absolute URL); needs `AnalyticsScriptUrl`. |
| `Site:AnalyticsSiteName` | The script's `data-site` attribute (default `GridSim`). |
| `Site:ShareImageUrl` | Adds `og:image` and `twitter:image` with this absolute URL. |
| `Site:OrganisationName`, `Site:OrganisationUrl`, `Site:OrganisationLogoUrl` | Adds a schema.org Organization block (URL and logo optional, absolute; both need the name). |
| `Site:FooterLinks:N:Text`, `Site:FooterLinks:N:Url` | Footer links, in order of N (0, 1, 2, ...); each needs both. |
| `Site:SecurityTxt:Contact` | Serves `/.well-known/security.txt` (RFC 9116) with this `Contact` (`mailto:`, `https://` or `tel:`). |
| `Site:SecurityTxt:Expires` | Its `Expires` (ISO 8601, required with a contact, must be in the future at start-up). |
| `Site:SecurityTxt:PreferredLanguages` | Its `Preferred-Languages` (default `en`). |
| `Site:SecurityTxt:Comment` | A one-line `#` comment written first in the file. |
| `Auth:ApplicationName` | Data-protection application name (default `GridSim`). |

The web host builds `/.well-known/security.txt` from the `Site:SecurityTxt:*` settings only; it does not read the
repository's own `.well-known/security.txt`, so a deployment without the settings answers 404 there. The `Expires` date
is checked when the host starts, not while it runs: restart (or update the settings) before that date passes.

## Checking a deployment

The footer of every page shows the release number and build commit (`v1.95.NNN (abcdef12)`), the same release and
commit as `gridsim --version` (which prints the full 40-character commit), and `GET /api/openapi.json` (when the API is enabled) carries the same version in `info.version`.
