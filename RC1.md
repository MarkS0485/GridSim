# GridSim 2.1.000 RC-1

| | |
|---|---|
| Version | 2.1.000 (release candidate RC-1) |
| Build date | 2026-10-31 |
| Source | the commit `f85f6e73` ("GridSim 2.1.000 RC-1: version"); every binary reports `2.1.000+f85f6e7390bb...` from `--version` |
| Platforms | Windows x64 and Linux x64 |

RC-1 ships builds for **Windows and Linux only**. Every build is Release, self-contained (it carries the .NET 10
runtime, so nothing else from Microsoft needs installing) and single-file where the project allows it.

## No macOS build

There is no macOS build in RC-1 because the author has no Mac to compile and test a compatible build on. A build
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
| `gridsim-cli-2.1.000-win-x64.zip` | Command line tool `gridsim.exe` for Windows x64, with the bundled `data/`, `schemas/`, the validation sidecars under `tools/`, `LICENSE.md` and the third-party notices | `56e84ba3e667e49768c8ab1fdcd233ba32909fccdfb2813371938d52272fb02d` |
| `gridsim-cli-2.1.000-linux-x64.tar.gz` | Command line tool `gridsim` for Linux x64, with the same folders | `c08e4094c734c0ad42e0fdce71400d626da6a931d542ac2aa74d525346e5b297` |
| `gridsim-wpf-2.1.000-win-x64.zip` | Windows desktop app `GridSim.Wpf.exe` (WPF), with the GB and MATPOWER cases it lists | `8e305ab5c17ccd489adde1eb2bd85203ab8ebdee749db445596c09303292ffa1` |
| `gridsim-app-2.1.000-win-x64.zip` | Cross-platform desktop app `GridSim.App.Desktop.exe` (Avalonia) for Windows x64, with the bundled `data/` and `schemas/` | `3b04e60cfc1320f3522091c80ddc32f7128602f0272e659334c8d426099e3e96` |
| `gridsim-app-2.1.000-linux-x64.tar.gz` | Cross-platform desktop app `GridSim.App.Desktop` (Avalonia) for Linux x64, with the same folders | `d0ccb78df650d6bc03f45b026014c4e14729176674b63ddf49f57848329a0839` |
| `GridSim.Core.2.1.0.nupkg` | The GridSim.Core library as a NuGet package (platform neutral). NuGet writes the version 2.1.000 as 2.1.0 | `d13e7e4ed83c4d2a73c95e9518a7d65c895f6c946ff477331cd43e93f3556e49` |
| `gridsim-2.1.000.sbom.json` | CycloneDX software bill of materials of the command line tool; its SHA-256 is stamped into `gridsim` (`gridsim compliance verify` reports it) | `fcf0cb79c2dae1e3412cb4d12452c510dcd57ec9f6881aac925e94d88d69ce32` |

The executables inside the archives, for checking an unpacked copy:

| File | SHA-256 |
|---|---|
| `gridsim.exe` (Windows CLI) | `2e5f29aa0787807b2bf1f8c1d7b1e9dc856b5de6eaf281545d7ddc5e2f4f273a` |
| `gridsim` (Linux CLI) | `3d8b18f25aaf850e6be290d8bad8c8fddd2244fc5c26600282ac71885c2f33fb` |
| `GridSim.Wpf.exe` | `dab521f0d3e3b7f5e647842ae92640d47ae4134854d89d45a6e0a9fac533fdfe` |
| `GridSim.App.Desktop.exe` (Windows) | `b200ec8083efae4549aebf7afca60be53cd4fb1e9c75479dd332d2fee743396b` |
| `GridSim.App.Desktop` (Linux) | `45c6e50c169512193ce339adac4f6cbcb7bedc7fe03b3fc9265d4526692e78ed` |

Check a download with `sha256sum <file>` (Linux) or `Get-FileHash <file> -Algorithm SHA256` (Windows PowerShell).

**Signing.** The three Windows executables carry an Authenticode signature (SHA-256, RFC 3161 timestamp) and the
NuGet package an author signature, all from the GridSim certificate `signing/gridsim-signing.cer` (thumbprint
`86DD43B731D2B8A5133972F7B7C91C0FDA8EB721`). The certificate is self-signed, so Windows reports the signature as not
trusted and SmartScreen may still warn; the check is that the signer thumbprint matches
(`Get-AuthenticodeSignature .\gridsim.exe | Format-List Status, SignerCertificate`). Linux binaries are not signed
(Authenticode is a Windows format); check them against `SHA256SUMS.txt`.

## System requirements

### Running the builds

| | Minimum | Recommended |
|---|---|---|
| Operating system, Windows build | Windows 10 or Windows 11, 64-bit (x64), a release still supported by .NET 10 | Windows 11 x64 (the release candidate was tested on Windows 11, build 26300) |
| Operating system, Linux build | x64 Linux with glibc 2.27 or later (the newest glibc symbol the binaries need is 2.27), for example Ubuntu 18.04, Debian 10 or RHEL 8 and later; glibc based only, so not Alpine (musl) | A current LTS distribution (tested on Ubuntu 22.04, 24.04 and 26.04) |
| .NET | Nothing to install: every build is self-contained and carries the .NET 10 runtime | |
| CPU | A 64-bit x86 (x64) processor supported by the .NET 10 runtime; no ARM build in RC-1 | 8 cores or more, with AVX2 or AVX-512 |
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
| SDK | The .NET 10 SDK (any 10.0.x; the repository pins no `global.json`; RC-1 was built with 10.0.401) |
| Windows | Builds every project. `GridSim.Wpf` builds on every OS (`EnableWindowsTargeting`) but runs only on Windows |
| Linux | Builds every project; to run the Avalonia app, the Linux needs above |
| macOS | Xcode (or its command line tools) and the .NET 10 SDK; see "No macOS build" for the commands |
| Tests | `dotnet build GridSim.slnx -c Release` (0 warnings), then `dotnet test GridSim.slnx -c Release --no-build`. Below 16 GB of RAM the largest cases are reported Skipped by the RAM gate, never passed (`tests/CONDITIONAL-TESTING.md`) |
| Optional | Python 3 with `pandapower` for the cross-validation sidecars; Docker for the web image |

## Verification of this release candidate

- Release build of the whole solution with 0 warnings.
- Full test suite on the 2.0.200 code (2.1.000 is that code with the version changed): GridSim.Tests 10,291 passed and
  31 skipped (RAM gate, 8 GB machine), GridSim.App.Tests 310 passed. The QSTS, impedance-stability and PSS/E slices
  also pass with hardware intrinsics off (`DOTNET_EnableHWIntrinsic=0`) and with AVX2 off (`DOTNET_EnableAVX2=0`).
- Every build checked after signing: both command line builds answer `--version` with `gridsim 2.1.000+f85f6e73...`,
  solve case 9 (exit 0) and report `release build yes` with the SBOM hash from `gridsim compliance verify` (Linux on
  Ubuntu 26.04 under WSL); both Windows desktop apps start and show v2.1.000; every archive carries `data/`.
- All 105 pages of the Avalonia app render on the headless renderer (uk, case9, gb-full).

**Not done for RC-1, and why.**

- The full suite in every SIMD tier (AVX-512 forced, AVX2 off, FMA off, 256-bit forced) and the large cases the RAM
  gate skips need a bigger machine than the author has at present.
- The Linux desktop app has not been run on a desktop Linux (X11 or Wayland). On Linux it needs the X11 libraries
  listed above; without `libICE` and `libSM` it stops at start-up.

## Reporting bugs

Send every bug report to **gdatooling@proton.me**. Please give the version (`gridsim --version`), the operating system,
the command or the steps, the case (or a small case that shows the problem) and what you expected.
