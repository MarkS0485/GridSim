# GridSim release candidate 1.0

| | |
|---|---|
| Version | 2.0.195 (release candidate 1.0) |
| Build date | 2026-10-30 |
| Source | the commit that holds this file; the binaries report `2.0.195+1724f89b...` from `--version`, the development commit they were built from, whose source is this tree without this file |
| Platforms | Windows x64 and Linux x64 |

RC 1.0 ships builds for **Windows and Linux only**. Every build is Release, self-contained (it carries the .NET 10
runtime, so nothing else from Microsoft needs installing) and single-file where the project allows it.

## No macOS build

There is no macOS build in RC 1.0 because the author has no Mac to compile and test a compatible build on. A build
nobody has run is not shipped. Mac and other Apple users can build GridSim themselves from source, on a Mac with
**Xcode** installed (the Xcode command line tools are enough for the .NET SDK to sign the binaries ad hoc, which
Apple silicon requires) and the **.NET 10 SDK**:

```bash
git clone <repository>/GridSim.git
cd GridSim
dotnet --list-sdks          # a 10.0.x line must be present

# Command line tool (self-contained, single file, with data/ and schemas/ beside it)
dotnet publish src/GridSim.Cli -c Release -p:PublishProfile=osx-arm64                  # Apple silicon
dotnet publish src/GridSim.Cli -c Release -r osx-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o artifacts/publish/gridsim-osx-x64                                                 # Intel Macs

# GridSim.Core library: the NuGet package is platform neutral (the same package as the Windows and Linux one)
dotnet pack src/GridSim.Core -c Release -o artifacts/nuget
# or the library and its dependencies as files, for one Mac architecture
dotnet publish src/GridSim.Core -c Release -r osx-arm64 -o artifacts/publish/gridsim-core-osx-arm64
dotnet publish src/GridSim.Core -c Release -r osx-x64   -o artifacts/publish/gridsim-core-osx-x64

# Desktop app (Avalonia), self-contained single file
dotnet publish src/GridSim.App.Desktop -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o artifacts/publish/gridsim-app-osx-arm64
dotnet publish src/GridSim.App.Desktop -c Release -r osx-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o artifacts/publish/gridsim-app-osx-x64
```

The Windows desktop app (`GridSim.Wpf`) is Windows only and has no macOS build. A binary you build yourself is not
signed with a Developer ID, so macOS may ask you to allow it in System Settings, Privacy and Security, the first time it
runs. Run the test suite on your Mac before you rely on the build: `dotnet test GridSim.slnx -c Release`.

## Artefacts

| File | What it is | SHA-256 |
|---|---|---|
| `gridsim-cli-2.0.195-win-x64.zip` | Command line tool `gridsim.exe` for Windows x64, with the bundled `data/`, `schemas/`, the validation sidecars under `tools/`, `LICENSE.md` and the third-party notices | `33bf23d496cc406f066bb9fdd62086c1c99d3ba3c00fb87d47628170cbb96001` |
| `gridsim-cli-2.0.195-linux-x64.tar.gz` | Command line tool `gridsim` for Linux x64, with the same folders | `89d1f1b04f0657a28be24c2a98500d9db7c5820ce07cc86232d7607337e197c9` |
| `gridsim-wpf-2.0.195-win-x64.zip` | Windows desktop app `GridSim.Wpf.exe` (WPF), with the GB and MATPOWER cases it lists | `bd6bc50a095935c84129dbf6be4f18fd9a7075eed302c82011d2b0bd2642101c` |
| `gridsim-app-2.0.195-win-x64.zip` | Cross-platform desktop app `GridSim.App.Desktop.exe` (Avalonia) for Windows x64 | `1f241f03a709d400e1954fd5e43d173ffcc78df60d331e3c0b1b051ca5b352d3` |
| `gridsim-app-2.0.195-linux-x64.tar.gz` | Cross-platform desktop app `GridSim.App.Desktop` (Avalonia) for Linux x64 | `04c6caafb5843710f69334d1b291e5760555907d150d6f4a92b1aabe947fd29f` |
| `GridSim.Core.2.0.195.nupkg` | The GridSim.Core library as a NuGet package (platform neutral: Windows, Linux and macOS) | `07bf2e049b3b7b012a93b97f2043074241949e247806c1f73a06d51c847d0bf2` |
| `gridsim-2.0.195.sbom.json` | CycloneDX software bill of materials of the command line tool; its SHA-256 is stamped into `gridsim` | `97a18732d623a8a4d4645dd720b48aec5330c7d8f79e0bdee293fc5e57665a43` |

The executables inside the archives, for checking an unpacked copy:

| File | SHA-256 |
|---|---|
| `gridsim.exe` (Windows CLI) | `b4dd1d12acd01a8697c6bc9e7aee9321ca4d515e5cd611fdff767b572622ceb3` |
| `gridsim` (Linux CLI) | `6b1fd4353caa8aa0eb5caa884a7d3a14a6b742586eb7858954a5ea335cc9db8d` |
| `GridSim.Wpf.exe` | `4076f561451d6bf4db20784887bfff5fb2cfcf12e098b589f5cb70db6a43a805` |
| `GridSim.App.Desktop.exe` (Windows) | `a15869b28893f89dcdc37eb70e109124345172ec4aae20eee14f8ea9ec247deb` |
| `GridSim.App.Desktop` (Linux) | `5017672c115e55ee809e867fe5cd5fedbd8bb05479ee85350d322df5071bce4f` |

Check a download with `sha256sum <file>` (Linux) or `Get-FileHash <file> -Algorithm SHA256` (Windows PowerShell).
The Windows binaries in RC 1.0 are not Authenticode signed, so Windows SmartScreen may warn on first run.

The Avalonia app's archive carries no `data/` folder: its case list holds the cases built into GridSim.Core and any
case file you open. To list the bundled GB and MATPOWER cases as well, copy the `data/` folder from the command line
tool's archive next to `GridSim.App.Desktop`.

## System requirements

### Running the builds

| | Minimum | Recommended |
|---|---|---|
| Operating system, Windows build | Windows 10 or Windows 11, 64-bit (x64), a release still supported by .NET 10 | Windows 11 x64 (the release candidate was tested on Windows 11, build 26300) |
| Operating system, Linux build | x64 Linux with glibc 2.27 or later (the newest glibc symbol the binaries need is 2.27), for example Ubuntu 18.04, Debian 10 or RHEL 8 and later; glibc based only, so not Alpine (musl) | A current LTS distribution (tested on Ubuntu 22.04, 24.04 and 26.04) |
| .NET | Nothing to install: every build is self-contained and carries the .NET 10 runtime | |
| CPU | A 64-bit x86 (x64) processor supported by the .NET 10 runtime; no ARM build in RC 1.0 | 8 cores or more, with AVX2 or AVX-512 |
| RAM | 8 GB (enough for every bundled case up to the 3,539-bus gb-full model, with about 1.5 GB free to study it) | 16 GB or more (12 GB adds the 5,824-bus continental Europe case; 16 GB holds every case GridSim ships) |
| Disk | About 110 MB per unpacked command line tool or Avalonia app, 155 MB for the WPF app, plus space for results | 1 GB free for run results, exports and recordings |
| GPU | None: every solve runs on the CPU | Optional, for the `--gpu` batch solves: an AMD Instinct with ROCm, or an OpenCL device with FP64, or an NVIDIA GPU with CUDA |

The design point is 8 cores, 16 GB of RAM or more and a GPU. A laptop with 8 GB is supported and runs everything
GridSim ships up to gb-full, more slowly.

**SIMD.** The dense kernels pick AVX-512, AVX2, SSE or plain scalar code at run time, and every tier gives the same
bits; `gridsim simdcheck` names the tier in use. No numerical path uses fused multiply-add, so whether the CPU has FMA
changes nothing.

**GPU.** GPU use is optional and only for the command line batch solves (`--gpu`). A missing or unusable device prints
a warning and the solve falls back to the CPU; `gridsim gpucheck` reports what is available. GPU arithmetic is outside
the bit-identity guarantee. The desktop apps need no compute GPU; they draw through the normal graphics driver.

**Extra runtime needs.**

- The WPF app (`GridSim.Wpf`) runs on Windows only.
- Linux, every build: the ICU library (the distribution's `libicu` or `icu-libs` package). Without it the .NET runtime
  stops at start-up with "Couldn't find a valid ICU package"; setting `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` runs the
  command line tool without it (GridSim formats every number in the invariant culture anyway). OpenSSL (`libssl`) is
  needed only for network connections.
- Linux, Avalonia app: an X11 display (on a Wayland desktop it runs through XWayland), with `libX11`, `libXcursor`,
  `libXi`, `libXrandr`, `libICE`, `libSM`, `fontconfig` and at least one installed font. GTK 3 (`libgtk-3`) is used for
  the system file dialogs when present.
- Optional: Python 3 with `pandapower` (which brings NumPy and SciPy) for the `crossval` sidecar only. Without it that
  verb still writes its export and exits 77 ("skipped").
- Optional: a GridSim licence above the free personal tier for networks of more than 1,500 buses that are not bundled.

### Building from source

| | Requirement |
|---|---|
| SDK | The .NET 10 SDK (any 10.0.x; the repository pins no `global.json`; RC 1.0 was built with 10.0.401) |
| Windows | Builds every project. `GridSim.Wpf` builds on every OS (`EnableWindowsTargeting`) but runs only on Windows |
| Linux | Builds every project; to run the Avalonia app, the Linux needs above |
| macOS | Xcode (or its command line tools) and the .NET 10 SDK; see "No macOS build" for the commands |
| Tests | `dotnet build GridSim.slnx -c Release` (0 warnings), then `dotnet test GridSim.slnx -c Release --no-build`. Below 16 GB of RAM the largest cases are reported Skipped by the RAM gate, never passed (`tests/CONDITIONAL-TESTING.md`) |
| Optional | Python 3 with `pandapower` for the cross-validation sidecars; Docker for the web image |

## Verification of this release candidate

- Release build of the whole solution with 0 warnings.
- Full test suite at 2.0.193 in normal mode and with hardware intrinsics off (`DOTNET_EnableHWIntrinsic=0`):
  GridSim.Tests 10,249 passed and 31 skipped (RAM gate, 8 GB machine) in both modes; GridSim.App.Tests 310 passed in
  both modes.
- The documentation and contract tests at 2.0.194: 1,997 passed, 2 skipped.
- The published-output tests against the Windows command line build: 5 of 5 passed.
- The Linux command line build run on Ubuntu 26.04: `--version`, a case 9 power flow (the same losses as on Windows,
  4.64 MW) and `simdcheck`.

## Reporting bugs

Send every bug report to **gdatooling@proton.me**. Please give the version (`gridsim --version`), the operating system,
the command or the steps, the case (or a small case that shows the problem) and what you expected.
