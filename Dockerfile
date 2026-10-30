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
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project files alone, so the package download layer is cached until a project file
# changes (source edits then rebuild without re-downloading packages).
# .editorconfig carries the analyzer rule severities: without it the strict build (warnings are errors) fails.
COPY Directory.Build.props GridSim.slnx .editorconfig ./
COPY src/GridSim.Core/GridSim.Core.csproj src/GridSim.Core/
COPY src/GridSim.Web/GridSim.Web.csproj   src/GridSim.Web/
RUN dotnet restore src/GridSim.Web/GridSim.Web.csproj

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
RUN dotnet publish src/GridSim.Web/GridSim.Web.csproj -c Release -p:PublishProfile=portable \
        -p:SourceRevisionId="${SOURCE_REVISION}" -p:GridSimSbomSha256="${SBOM_SHA256}" --no-restore

# -------------------------------------------------------------------------------------------- runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is used only by the container health check (the runtime image does not include it).
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

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

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/ > /dev/null || exit 1

ENTRYPOINT ["dotnet", "GridSim.Web.dll"]
