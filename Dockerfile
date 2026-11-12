# GridSim web app container image (F05).
#
# Build:  docker build -t gridsim-web --build-arg SOURCE_REVISION=$(git rev-parse HEAD) .
#   (or:  docker compose build    - compose.yml points here)
# Run:    docker run --rm -p 127.0.0.1:5540:8080 gridsim-web
#
# Two stages: the .NET SDK image builds the web app with the same publish profile the release uses
# (src/GridSim.Web/Properties/PublishProfiles/portable.pubxml); the much smaller ASP.NET Core runtime image
# runs it. Nothing machine-specific is baked in: single sign-on, the data-protection key folder, the GDA bridge
# address and the cookie domain all come from environment variables at run time (see compose.yml and
# docs/developer/deploy/README.md). The image runs as the unprivileged "app" user the .NET images provide.
#
# SOURCE_REVISION: the build context deliberately leaves out .git (see .dockerignore), so the .NET SDK cannot
# read the commit itself. Pass it in and it is stamped into the assemblies exactly as a normal build does
# (InformationalVersion 1.95.NNN+<sha>, shown in the web footer and the OpenAPI document). Left empty, the
# version shows without a commit.

# ---------------------------------------------------------------------------------------------- build ---
# Bug #161: the SDK image is pinned to the exact SDK in global.json (10.0.401), not the floating 10.0 tag, so the
# image restores the same implicit package versions the committed packages.lock.json files record. Change both together.
# GR-423: also pinned by digest (the multi-arch index digest of that tag on mcr.microsoft.com, resolved 2026-11-01), so a
# re-pushed tag cannot change the build image. Update tag and digest together.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

# Restore first, from the project files alone, so the package download layer is cached until a project file
# changes (source edits then rebuild without re-downloading packages).
# .editorconfig carries the analyzer rule severities: without it the strict build (warnings are errors) fails.
# global.json and the lock files come too, and the restore is LOCKED (bug #161): a restore that would change a lock
# file fails the image build loud instead of quietly building from a drifted dependency graph.
COPY Directory.Build.props GridSim.slnx .editorconfig ./
COPY global.json ./
COPY src/GridSim.Core/GridSim.Core.csproj src/GridSim.Core/packages.lock.json src/GridSim.Core/
COPY src/GridSim.Web/GridSim.Web.csproj   src/GridSim.Web/packages.lock.json  src/GridSim.Web/
# The SDK detects Razor components during restore and adds the ASP.NET Core assets package.
# Copy them before locked restore so it uses the same dependencies as a full checkout.
COPY src/GridSim.Web/Components/ src/GridSim.Web/Components/
RUN dotnet restore src/GridSim.Web/GridSim.Web.csproj --locked-mode

# The sources the web app needs: Core, Web, the bundled case data it ships, the JSON schemas Core embeds (the v1 / v2
# schema files and kind specs, GridSim.Core.csproj; without them the image's Core had no embedded schemas), and the
# licence / notices files.
COPY LICENSE.md THIRD-PARTY-NOTICES.md ./
COPY data/ data/
COPY schemas/ schemas/
COPY src/GridSim.Core/ src/GridSim.Core/
COPY src/GridSim.Web/  src/GridSim.Web/

ARG SOURCE_REVISION=
# SBOM_SHA256: the release workflow passes the SHA-256 of the web SBOM, so the image's assemblies attest the bill of
# materials they were built against (`/compliance`); left empty (any local build) the attestation says "unset".
ARG SBOM_SHA256=
# SOURCE_TREE (bug #176): "clean" or "dirty", the state of the checkout the image is built from. The build context has no
# .git, so the build cannot see it for itself; the release workflow computes it with git status and passes it in. Left
# empty, the image records the source tree as unknown and does not attest a release build, even with an SBOM hash.
ARG SOURCE_TREE=
RUN dotnet publish src/GridSim.Web/GridSim.Web.csproj -c Release -p:PublishProfile=portable \
        -p:SourceRevisionId="${SOURCE_REVISION}" -p:GridSimSbomSha256="${SBOM_SHA256}" \
        -p:GridSimSourceTree="${SOURCE_TREE}" --no-restore

# -------------------------------------------------------------------------------------------- runtime ---
# The runtime the pinned SDK bundles (10.0.401 carries runtime 10.0.12), pinned for the same reason, and by digest
# (GR-423; the image is Ubuntu 24.04).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime

# GR-423: no packages are installed. The health check used to install curl from the distribution at build time, an
# unpinned package; it now uses bash's built-in /dev/tcp, so the runtime image is the pinned base plus the app.

WORKDIR /app
COPY --from=build /src/artifacts/publish/gridsim-web/ ./

# The data-protection key ring. /keys is the default mount point in compose.yml; it is created here owned by the
# unprivileged user so a fresh named volume starts writable. App_Data/keys is the app's own fallback when
# Auth__KeysDirectory is not set at all.
RUN mkdir -p /keys /app/App_Data/keys && chown -R "$APP_UID" /keys /app/App_Data
USER $APP_UID

# Listen on 8080 inside the container (the .NET image default); publish it on the host as you choose.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Auth__KeysDirectory=/keys
EXPOSE 8080

# The probe does what `curl -fsS http://localhost:8080/` did: GET / with Host: localhost, healthy on a 2xx or 3xx status
# line, unhealthy on a refused connection, a 4xx/5xx status or no answer within 5 s.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET / HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && IFS= read -r -t 5 status <&3 && case "$status" in "HTTP/1."[01]" "[23]*) exit 0 ;; *) exit 1 ;; esac' || exit 1

ENTRYPOINT ["dotnet", "GridSim.Web.dll"]
