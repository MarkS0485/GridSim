# Install

Two routes: a ready-built release (no .NET install needed), or a build from source. The full chapter is
[Getting started]({{SITE}}manual/0001%20Getting%20started/) in the user manual.

## Release binaries (Windows, Linux and macOS)

The latest release is [2.1.073 RC-4]({{REPO}}/releases/tag/v2.1.073-RC-4). All builds are
self-contained: the .NET runtime is carried inside, so nothing else needs installing. Pick the file for your machine
(`<version>` is the release number, for example `2.1.073`):

| What | Platform | File |
|---|---|---|
| Command line tool (`gridsim`) | Windows x64 | `gridsim-<version>-win-x64.zip` |
| Command line tool (`gridsim`) | Linux x64 | `gridsim-<version>-linux-x64.tar.gz` |
| Command line tool (`gridsim`) | macOS arm64 (Apple silicon) | `gridsim-<version>-osx-arm64.tar.gz` |
| Desktop app (Avalonia) | Windows x64 | `gridsim-app-<version>-win-x64.zip` |
| Desktop app (Avalonia) | Linux x64 | `gridsim-app-<version>-linux-x64.tar.gz` |
| Desktop app (WPF) | Windows x64 | `gridsim-wpf-<version>-win-x64.zip` |
| CycloneDX SBOMs | Any | `*.sbom.json` |
| SHA-256 checksums of every file | Any | `SHA256SUMS.txt` |

All of them are on the [release page]({{REPO}}/releases/tag/v2.1.073-RC-4). Check every download against
`SHA256SUMS.txt`. The `GridSim.Core` NuGet package and the web container image are published separately; the
[manual chapter]({{SITE}}manual/0001%20Getting%20started/) says where.

Unpack the command line archive anywhere and run `gridsim` (`gridsim.exe` on Windows) from that folder. Keep the
`data/` folder next to the executable, because the executable finds its built-in cases there. Then check it:

```
gridsim --version
gridsim case9 --once
```

The `gda` verbs are in the tool, but they need a GDA data lake, which no download includes.

## macOS

The command line tool has a macOS build for Apple silicon. The desktop apps have none: Mac users build the Avalonia
app from source; the exact commands are in [RC1.md]({{REPO}}/blob/main/RC1.md).

## Build from source

You need the .NET 10 SDK (the SDK, not only the runtime). Check with `dotnet --list-sdks`; a 10.0.x line must be
present. From the repository root:

```
dotnet build GridSim.slnx -c Release
dotnet test  GridSim.slnx -c Release --no-build
```

The build is warning-free by contract, so a healthy build ends with 0 warnings and 0 errors. From source, `gridsim <verb>`
means `dotnet run --project src/GridSim.Cli -c Release -- <verb>`. The details, including publishing a self-contained
single-file build, are in the
[manual chapter]({{SITE}}manual/0001%20Getting%20started/).

Next: [Getting started](Getting-started).
