# IEEE C37.118.2 Synchrophasor Ingest + Phasor Data Concentrator - Technical Design

> **Status (verified 1.95.169):** Partly implemented and experimental: the C37.118.2 DATA decoder, CONFIG-1/2 parser, phasor data concentrator, PDC-to-`MeasurementSet` mapper, linear PMU-only estimator and hybrid SCADA/PMU scheduler exist as Core API with tests. The `pmu-ingest` verb decodes a recorded multi-PMU capture file (technical ch. 33). There is no live frame transport, no stateful decoder and no per-PMU stream configuration type.
> **Implemented in:** `src/GridSim.Core/Synchrophasor/C37118Decoder.cs` (`C37118Decoder`, `C37118DataFrame`, `FrameDecodeStatus`), `C37118ConfigFrame.cs` (`C37118ConfigParser`, `C37118ConfigFrame`, `PmuConfig`), `C37118Frame.cs` (`C37118FrameType`, `C37118DataFormat`, `Phasor`, `C37118CommonHeader`, `C37118Crc`), `PhasorDataConcentrator.cs` (`PmuSample`, `PdcSuperframe`, `PhasorDataConcentrator`), `PmuMeasurementSetMapper.cs` (`PmuChannelMap`, `PmuVoltageChannel`, `PmuCurrentChannel`, `PmuAngleReference`, `PmuMeasurementSetMapper`); `src/GridSim.Core/Estimation/LinearPmuStateEstimator.cs` (`LinearPmuStateEstimator`, `PmuVoltageMeasurement`, `PmuCurrentMeasurement`, `LinearPmuResult`); `src/GridSim.Core/Estimation/Measurement.cs` (`MeasurementKind.IAngDeg`); `src/GridSim.Core/Simulation/HybridMultiRateScheduler.cs` (`HybridMultiRateScheduler`, `ScheduledUpdate`).
> **Design-to-code name map:**
>
> | Design name | Implemented as |
> |---|---|
> | `C37118FrameType.cs`, `C37118DataFormat.cs`, `C37118Crc.cs`, `C37118CommonHeader.cs`, `Phasor.cs` | all in `C37118Frame.cs` |
> | `ConfigFrame2.cs`, `PmuConfig.cs` | `C37118ConfigFrame.cs` (`C37118ConfigFrame`, `PmuConfig`; the parser is `C37118ConfigParser`) |
> | `PhasorDescriptor.cs` | not implemented; `PmuConfig.PhasorUnits` and `PmuConfig.PhasorScale(index)` carry the PHUNIT scaling |
> | `DataFrame.cs`, `PmuData.cs` | `C37118DataFrame` in `C37118Decoder.cs` (first PMU block only; analogue and digital channels are not decoded) |
> | `PmuStatus.cs` | not implemented; the STAT word is the raw `ushort` `C37118DataFrame.Stat` |
> | stateful `C37118Decoder` holding CONFIG-2 | not implemented; the static decoder takes the parsed `PmuConfig` per call |
> | `PdcSuperframe.cs` | `PdcSuperframe` in `PhasorDataConcentrator.cs` (`Samples` by PMU id, `Dropped` list) |
> | `IPmuFrameSource.cs`, `FileReplayPmuFrameSource.cs` | not implemented |
> | `PmuStreamConfig.cs` | `PmuChannelMap` with `PmuVoltageChannel`, `PmuCurrentChannel`, `PmuAngleReference` in `PmuMeasurementSetMapper.cs` |
> | `MeasurementKind.cs` | `MeasurementKind` is declared in `Estimation/Measurement.cs` |
>
> This is a design record. The current behaviour is documented in the technical manual chapter [18. Real-time, distribution and dynamic estimation](<../0018 Real-time, distribution and dynamic estimation.md>) (section 18.6).

Governing constraint: `UnderstandingRestrictions.md` (no forward inference) and
`RestrictionReview.Reviewed` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`) (the Dynamics->Estimation review gate). Read
those first; every decision below is subordinate to them.

This document specifies, to the level an implementer follows without further
research: (a) a byte-exact C37.118.2 frame decoder (CONFIG-2 and DATA frames),
(b) a Phasor Data Concentrator (PDC) that time-aligns several PMU streams with a
bounded wait-window and survives dropouts and latency, (c) the adapter that maps
concentrated phasors into the existing estimator as voltage-magnitude,
voltage-angle and current-phasor measurement rows, (d) a linear single-shot
PMU-only state estimator, and (e) a hybrid multi-rate SCADA(~5 s) + PMU(~0.5 s)
fusion scheduler that fits the `RealtimeEstimatorEngine` cadence. No C# is
written here - types are *named and specified* so they land in the existing
code style.

---

## 1. Restriction posture (read this before any code)

This feature ingests synchrophasors, which in the field are a *live, sub-second*
telemetry stream - exactly the kind of data the one rule forbids GridSim from
using to see the present or future of the real GB grid. The design is built so
the capability is real (a validated C37.118 decoder + PDC + linear/hybrid PMU
estimator) while the forbidden operation is structurally impossible.

The three things this design guarantees, mechanically:

1. **Every dated synchrophasor input passes `ForwardInferenceGuard.RejectFuture`
   before it is estimated.** A PMU frame carries an absolute UTC timestamp
   (SOC + FRACSEC, section 4.4). The PDC stamps each aligned superframe with that
   UTC instant and the mapping adapter (`PmuMeasurementSetMapper`, section 10)
   calls the guard on it. A frame newer than `UtcNow - 169 h` is recorded and
   skipped, never mapped, never estimated. Because a *live* PMU frame is by
   definition seconds old, the guard rejects every live frame by construction:
   there is no code path that turns a live synchrophasor into a state estimate.
2. **The feature runs on replay or permitted-age captures only.** The transport
   layer (`IPmuFrameSource`, section 9.1) is fed by a *recorded* C37.118 byte
   stream (a capture file / pcap payload replayed frame by frame). A live-socket
   source may exist as an interface implementation, but its frames still transit
   the guard, so it can only ever deliver frames that are >= 169 h old - which a
   live socket never produces. "Real-time" here means the *engineering* (streaming
   parse, resident PDC, sub-cycle cadence, resilience), never live sight, exactly
   as `UnderstandingRestrictions.md` sec. 3 permits.
3. **Every estimator introduced here is a filter, not a predictor.** The linear
   single-shot PMU estimator (section 11) and the hybrid scheduler (section 12)
   are *measurement-only*: they resolve the state at the frame's own timestamp
   from that frame's observations. They emit a state stamped at the (present/past)
   measurement time, never a future-dated claim. Neither fuses a `Dynamics/`
   process model, so per the review log they are **static filters, unconditionally
   permitted** and need **no** `RestrictionReviewEntry`. (`RestrictionReview.Reviewed`
   already lists `linear-PMU` and `branch-current` among the permitted static
   methods; this design realises the first of those.) If a future variant ever
   adds a dynamic predict step (e.g. a PMU-rate EKF tracker), it becomes a
   Dynamics->Estimation fusion and section 13.3's gate applies - stop and review.

Invariant mapping (against `UnderstandingRestrictions.md` "rules, as testable
invariants"): #1 - the PDC/mapper is a new dated ingestion seam behind the guard
(section 13); #2 - no output is stamped later than its newest input frame
(section 12.4); #4 - proven on replay/permitted-age only (sections 1.2, 15); #5 -
no `Dynamics/` fusion, so the DSE gate is untouched (section 13.3).

---

## 2. Scope, non-goals, and standard baseline

**In scope.** C37.118.2-2011 mapping of the 2005 frame syntax: the common frame
header, CONFIG-2 configuration frames, DATA frames, fixed (16-bit integer) and
floating-point (32-bit IEEE-754) formats, phasors in rectangular and polar form,
FREQ/DFREQ in integer and float, the STAT word and its time-quality/sync bits,
CRC-CCITT frame integrity, and TVE-aware sigma assignment. A PDC that aligns N
PMU streams by timestamp. Mapping to estimator rows. A linear PMU-only estimator
and a hybrid multi-rate scheduler.

**Out of scope (named as future seams, section 17).** CONFIG-1 and CONFIG-3
frames (CONFIG-3 is decoded to the extent needed to reject it cleanly);
HEADER frames (skipped, logged); the command channel (CMD frames) beyond a
documented request enum - GridSim never *drives* a live PMU, it replays captures;
analog and digital channels are parsed and carried but not mapped to estimator
rows; TCP/UDP/multicast socket transport internals (the interface is defined; a
file-replay source is the reference implementation).

**Standard.** IEEE Std C37.118.2-2011 "for Synchrophasor Data Transfer for Power
Systems". All multi-byte integers are network byte order (big-endian). This is
the single most common decoder mistake; use `System.Buffers.Binary.BinaryPrimitives`
big-endian readers throughout, never `BitConverter` (which is host-endian).

---

## 3. Namespace and file layout

New code lives under two existing conventions: transport/decoder/PDC in a new
`GridSim.Core.Synchrophasor` namespace (folder `src/GridSim.Core/Synchrophasor/`),
the estimator/scheduler additions in the existing `GridSim.Core.Estimation` and
`GridSim.Core.Simulation` namespaces so they sit beside `WlsStateEstimator` and
`RealtimeEstimatorEngine`.

```
src/GridSim.Core/Synchrophasor/
  C37118FrameType.cs        enum: Data, Header, Config1, Config2, Config3, Command
  C37118DataFormat.cs       [Flags] enum for the CONFIG FORMAT word (4 bits)
  C37118Crc.cs              static CRC-CCITT (poly 0x1021, init 0xFFFF)
  C37118CommonHeader.cs     readonly struct: Sync/Type/Version/FrameSize/IdCode/Soc/FracSec/TimeQuality
  C37118Decoder.cs          the parser: ReadOnlySpan<byte> -> parsed frames; stateful (holds CONFIG-2)
  ConfigFrame2.cs           sealed record: TimeBase, DataRate, PmuConfig[]
  PmuConfig.cs              sealed record: StationName, IdCode, Format, phasor/analog/digital descriptors, FNom, CfgCount
  PhasorDescriptor.cs       sealed record: Name, Kind(Voltage/Current), ConversionFactor, ScalePu
  DataFrame.cs              sealed record: CommonHeader-derived TimeUtc + PmuData[]
  PmuData.cs                sealed record: StatWord, Phasor[], FreqHz, RocofHzPerS, Analog[], Digital[]
  Phasor.cs                 readonly struct: Real, Imag (engineering units) + Magnitude/AngleRad accessors
  PmuStatus.cs              readonly struct wrapping the STAT word (DataValid, SyncOk, UnlockedTime, ...)
  PhasorDataConcentrator.cs the time-aligner (wait-window buffer -> PdcSuperframe stream)
  PdcSuperframe.cs          sealed record: TimeUtc + per-PMU aligned PmuData (+ per-PMU present/dropped flags)
  IPmuFrameSource.cs        transport seam: IEnumerable<ReadOnlyMemory<byte>> of raw frames
  FileReplayPmuFrameSource.cs   reference source: length-delimited capture file
  PmuMeasurementSetMapper.cs    PdcSuperframe -> MeasurementSet (guarded); PMU->bus/branch crosswalk
  PmuStreamConfig.cs        per-PMU crosswalk + base kV/A + performance class -> sigma policy

src/GridSim.Core/Estimation/
  MeasurementKind.cs        + IAngDeg member (branch current phase angle) - section 10.2
  LinearPmuStateEstimator.cs   rectangular constant-gain single-shot PMU-only WLS - section 11

src/GridSim.Core/Simulation/
  HybridMultiRateScheduler.cs  SCADA(~5 s) + PMU(~0.5 s) window interleaver - section 12
```

Style rules the new files follow (matching the existing tree): `sealed record`
for immutable value carriers, `readonly struct` for hot parse structs, XML doc
comments on every public member (the 0-warning gate requires them, section 16),
`internal` for anything the CLI/UI does not need, culture-invariant parsing,
no `unsafe`.

---

## 4. The C37.118.2 frame model

### 4.1 Common frame header (14 bytes, all frame types)

Every frame begins with the same header. Offsets are from frame start.

```
Off  Size  Field       Meaning
0    2     SYNC        byte0 = 0xAA (leading sync);
                       byte1: bit7 reserved(0);
                              bits6..4 = frame type (see 4.2);
                              bits3..0 = version number (1 = 2005 mapping, 2 = 2011)
2    2     FRAMESIZE   total bytes in the frame, header..CHK inclusive (uint16 BE)
4    2     IDCODE      data stream / PMU / PDC id (uint16 BE)
6    4     SOC         second-of-century: UNIX time, seconds since 1970-01-01T00:00:00Z (uint32 BE)
10   4     FRACSEC     bits31..24 = message time-quality flags;
                       bits23..0  = fraction-of-second, raw count of 1/TIME_BASE (from CONFIG)
... body ...
end-2 2   CHK          CRC-CCITT over bytes [0 .. FRAMESIZE-3] (uint16 BE)
```

`C37118CommonHeader` is a `readonly struct` parsed by
`C37118CommonHeader.Parse(ReadOnlySpan<byte> frame)`; it exposes `FrameType`,
`Version`, `FrameSize`, `IdCode`, `Soc`, `FracSecRaw` (lower 24 bits),
`TimeQuality` (top byte of FRACSEC). It does **not** convert to UTC (that needs
TIME_BASE from CONFIG - see 4.4).

### 4.2 Frame type discrimination

`C37118FrameType` from SYNC byte1 bits 6..4:

```
000 = Data     001 = Header    010 = Config-1
011 = Config-2 100 = Command    101 = Config-3
```

The decoder switches on this. Config-1 is treated as Config-2 syntactically
(same layout for our fields); Config-3, Header and Command are decoded to a
typed "unsupported/ignored" outcome (section 6.4), never a throw in the resident
loop.

### 4.3 CRC-CCITT (frame integrity)

C37.118 uses CRC-CCITT: width 16, polynomial `0x1021`, initial value `0xFFFF`,
**no** input/output reflection, **no** final XOR. Computed over every byte of the
frame from SYNC through the last data byte (i.e. bytes `[0 .. FRAMESIZE-3]`), and
compared to the trailing 2-byte CHK.

```
crc = 0xFFFF
for each byte b in frame[0 .. FRAMESIZE-3]:
    crc = crc XOR (b << 8)
    repeat 8 times:
        if (crc AND 0x8000) != 0: crc = (crc << 1) XOR 0x1021
        else:                     crc = (crc << 1)
        crc = crc AND 0xFFFF
return crc
```

`C37118Crc.Compute(ReadOnlySpan<byte> bytes) -> ushort`. The decoder verifies CHK
before trusting any field; a CRC mismatch yields `FrameDecodeStatus.CrcMismatch`
(section 6.4), and the PDC treats that PMU as *dropped* for that timestamp
(section 9.4). A table-driven variant is permitted for speed but must be
cross-checked bit-for-bit against the bitwise form above in a unit test.

### 4.4 Timestamp: SOC + FRACSEC -> UTC

```
fraction_seconds = FracSecRaw / TIME_BASE      (TIME_BASE from CONFIG-2, typically 1_000_000)
TimeUtc = DateTime(1970,1,1,0,0,0, DateTimeKind.Utc)
          + TimeSpan.FromSeconds(Soc)
          + TimeSpan.FromSeconds(fraction_seconds)
```

Implemented with `DateTime.UnixEpoch.AddSeconds(Soc).AddTicks(...)`; prefer ticks
math on the fraction to avoid double rounding (`ticks = FracSecRaw * 10_000_000L /
TIME_BASE`, integer). This UTC instant is the timestamp the PDC aligns on and the
guard checks. It is always absolute UTC - never local, never relative.

The FRACSEC top byte (time quality) and the STAT unlocked-time bits (section 6.3)
feed the sigma policy: a frame whose PMU clock is unlocked gets inflated sigma or
is dropped.

---

## 5. CONFIG-2 frame decoding

CONFIG-2 is *stateful context*: the decoder must hold the most recent CONFIG-2
per IDCODE, because DATA frames are self-descriptionless and are only decodable
against their CONFIG. `C37118Decoder` keeps `Dictionary<ushort, ConfigFrame2>`
keyed by IDCODE and refuses to decode a DATA frame for an IDCODE it has not yet
seen a CONFIG for (`FrameDecodeStatus.NoConfig`).

### 5.1 CONFIG-2 body layout (after the 14-byte common header)

```
Off   Size            Field       Meaning
14    4               TIME_BASE   resolution of FRACSEC; lower 24 bits significant (uint32 BE)
18    2               NUM_PMU     number of PMU blocks that follow (uint16 BE)
20    ...             PMU blocks  NUM_PMU repetitions of the block below
...   2               DATA_RATE   > 0: frames/second; < 0: seconds/frame (int16 BE)
end-2 2               CHK
```

Per-PMU block (repeated NUM_PMU times):

```
Size                        Field     Meaning
16                          STN       station name, ASCII, space-padded
2                           IDCODE    this PMU's data-source id (uint16 BE)
2                           FORMAT    format word (section 5.2)
2                           PHNMR     number of phasors (uint16 BE)
2                           ANNMR     number of analog values (uint16 BE)
2                           DGNMR     number of digital status words (uint16 BE)
16*(PHNMR+ANNMR+16*DGNMR)   CHNAM     channel names, 16 ASCII bytes each
4*PHNMR                     PHUNIT    per-phasor conversion factor (section 5.3)
4*ANNMR                     ANUNIT    per-analog conversion factor
4*DGNMR                     DIGUNIT   per-digital mask/valid words
2                           FNOM      nominal line frequency: bit0 0=60 Hz, 1=50 Hz
2                           CFGCNT    configuration change count (uint16 BE)
```

### 5.2 FORMAT word (`C37118DataFormat`, a `[Flags]` enum)

Lower 4 bits (bits 15..4 unused):

```
bit0  phasor number format:   0 = 16-bit integer, 1 = 32-bit IEEE float
bit1  analog number format:   0 = 16-bit integer, 1 = 32-bit IEEE float
bit2  freq/dfreq format:      0 = 16-bit integer, 1 = 32-bit IEEE float
bit3  phasor coordinate:      0 = rectangular,    1 = polar
```

The decoder reads FORMAT per PMU and dispatches phasor/freq parsing (section 6)
on these four bits. All four combinations (int/float x rect/polar, int/float
freq) must be supported and each covered by a decode test (section 15).

### 5.3 PHUNIT (phasor scale + type)

Each 4-byte PHUNIT: top byte = phasor type (0 = voltage, 1 = current); lower 3
bytes (24-bit uint BE) = scale factor. For **integer** phasor format the scale is
in units of 10^-5 volts or amperes per bit (multiply the raw integer by
`scale * 1e-5` to get engineering volts/amps). For **float** format the phasor is
already in engineering units and the scale is informational (still carries the
voltage/current flag, which the mapper needs to decide bus-voltage vs
branch-current row). `PhasorDescriptor` records `Kind` (Voltage/Current from the
top byte), `Name` (from CHNAM), the raw `ConversionFactor`, and a mapper-supplied
`ScalePu` (section 8) that turns engineering units into per-unit.

`FNOM` sets each PMU's nominal frequency for FREQ reconstruction (section 6.2).

---

## 6. DATA frame decoding

A DATA frame body is a concatenation of per-PMU blocks in the **exact order** the
PMUs appear in the governing CONFIG-2. There are no delimiters; sizes come from
CONFIG (PHNMR/ANNMR/DGNMR and FORMAT). `C37118Decoder.DecodeData(span)` looks up
the CONFIG by IDCODE, then walks each PMU block.

### 6.1 Per-PMU DATA block

```
Size                    Field    Meaning
2                       STAT     status word (section 6.3)
phasor-size * PHNMR     PHASORS  phasor-size = 8 (float) or 4 (int) bytes each
freq-size               FREQ     4 (float) or 2 (int) bytes
freq-size               DFREQ    4 (float) or 2 (int) bytes
analog-size * ANNMR     ANALOG   4 (float) or 2 (int) bytes each
2 * DGNMR               DIGITAL  16-bit words
```

### 6.2 Phasors, frequency, ROCOF

**Phasor, rectangular integer** (4 bytes): int16 BE real, int16 BE imag; engineering
value `re = raw_re * scale * 1e-5`, `im = raw_im * scale * 1e-5`.

**Phasor, polar integer** (4 bytes): uint16 BE magnitude, int16 BE angle;
`mag_eng = raw_mag * scale * 1e-5`, `angle_rad = raw_angle * 1e-4` (the angle unit
is 10^-4 radians, range -pi..+pi). Convert to rectangular for the `Phasor` struct:
`re = mag_eng * cos(angle_rad)`, `im = mag_eng * sin(angle_rad)`.

**Phasor, rectangular float** (8 bytes): float32 BE real, float32 BE imag, already
engineering units.

**Phasor, polar float** (8 bytes): float32 BE magnitude (engineering units),
float32 BE angle (radians). Convert to rectangular as above.

`Phasor` always stores rectangular engineering units (`Real`, `Imag`) with
`Magnitude => hypot(Real, Imag)` and `AngleRad => atan2(Imag, Real)` accessors, so
downstream code is format-agnostic.

**FREQ.** Integer: int16 BE = deviation from nominal in millihertz; actual
`Hz = FNom + FREQ_raw / 1000.0`. Float: float32 BE = the actual frequency in Hz,
nominal included (IEEE C37.118.2-2011 Table 5, FREQ: fixed point is the deviation
from nominal in mHz, floating point is the actual frequency in Hz; the standard is
not on disk, so this is from the published table, as in pyPMU and openPDC);
`Hz = FREQ_float`. Store `FreqHz` absolute.
(N038, 1.95.239: this section used to say the float field is a deviation from
nominal and proposed a per-stream `PmuStreamConfig.FreqIsAbsolute` switch. That
was wrong against Table 5 and was never built; `C37118Decoder` reads the float
field as absolute Hz, which is the standard's rule.)

**DFREQ (ROCOF).** Integer: int16 BE = ROCOF * 100 in Hz/s; `RocofHzPerS =
DFREQ_raw / 100.0`. Float: float32 BE = ROCOF in Hz/s directly.

### 6.3 STAT word and TVE awareness (`PmuStatus`)

The 16-bit STAT word:

```
bit15     data valid       0 = good, 1 = invalid/error
bit14     PMU error        1 = device error
bit13     PMU sync         0 = in sync, 1 = out of sync
bit12     data sorting      0 = by timestamp, 1 = by arrival
bit11     PMU trigger detected
bit10     configuration change pending
bit09     data modified (post-processed)
bits08..06 PMU time quality
bits05..04 unlocked time:   00 sync locked / <10 s since unlock;
                            01 <10 s, 10 <100 s, 11 >100 s unlocked
bits03..00 trigger reason
```

`PmuStatus` exposes `DataValid`, `SyncOk` (bit13 clear), `UnlockedTime` (enum),
`ConfigChangePending`. These drive the **sigma policy** (section 8.3) and the PDC
drop rule (section 9.4).

**TVE (Total Vector Error) awareness.** C37.118.1 defines a phasor's accuracy by
`TVE = sqrt((Xr_meas - Xr_true)^2 + (Xi_meas - Xi_true)^2) / |X_true|`, with 1 %
the compliance limit. GridSim never *measures* TVE (there is no truth), but it
uses the TVE budget as the *sigma model*: a phasor at compliance carries at most
1 % magnitude error and about 0.01 rad (~0.573 deg) angle error, because for a
pure angle error `TVE ~= |angle_error_rad|`. So (section 8.3) magnitude sigma is
set to `tve_budget * |V|` and angle sigma to `tve_budget` radians, where
`tve_budget` comes from the declared performance class (P vs M) and is inflated
when STAT reports unlock/error. This is how "TVE awareness" enters the estimator:
as principled, class- and status-derived measurement weights, not as a discarded
diagnostic.

### 6.4 Decode outcome

`C37118Decoder` returns a discriminated outcome rather than throwing in the hot
loop:

```
enum FrameDecodeStatus { Ok, CrcMismatch, NoConfig, UnsupportedFrameType,
                         TruncatedFrame, VersionUnsupported }
readonly struct DecodeResult { FrameDecodeStatus Status; C37118FrameType Type;
                               DataFrame? Data; ConfigFrame2? Config; }
```

A `TruncatedFrame` (FRAMESIZE says more bytes than the buffer holds, or a PMU
block overruns) is a hard protocol violation for that frame only; it is recorded
and the frame skipped. Only genuinely un-parseable *program* states (a null
buffer) throw. This mirrors `RealtimeEstimatorEngine`'s "record and skip, never
tear down the resident loop" resilience doctrine.

---

## 7. Parsed data structures (named to fit the tree)

```
sealed record ConfigFrame2(
    uint TimeBase, double DataRateFramesPerSec, IReadOnlyList<PmuConfig> Pmus,
    ushort IdCode, uint Soc, uint FracSecRaw);

sealed record PmuConfig(
    string StationName, ushort IdCode, C37118DataFormat Format,
    IReadOnlyList<PhasorDescriptor> Phasors, int AnalogCount, int DigitalCount,
    double FNomHz, ushort ConfigCount);

sealed record PhasorDescriptor(string Name, PhasorKind Kind, uint ConversionFactor);
enum PhasorKind { Voltage, Current }

readonly struct Phasor { double Real; double Imag;
    double Magnitude => Math.Sqrt(Real*Real + Imag*Imag);
    double AngleRad  => Math.Atan2(Imag, Real); }

readonly struct PmuStatus { ushort Raw; bool DataValid; bool SyncOk;
    UnlockedTime UnlockedTime; bool ConfigChangePending; }

sealed record PmuData(ushort IdCode, PmuStatus Status, IReadOnlyList<Phasor> Phasors,
    double FreqHz, double RocofHzPerS, IReadOnlyList<double> Analog,
    IReadOnlyList<ushort> Digital);

sealed record DataFrame(DateTime TimeUtc, ushort IdCode, IReadOnlyList<PmuData> Pmus);
```

Engineering-unit values are stored; per-unit conversion happens at the mapper
(section 8), keeping the decoder a pure, protocol-faithful layer that a decode
test can pin against reference vectors independent of any grid model.

---

## 8. Phasor scaling and per-unit mapping (`PmuStreamConfig`)

A C37.118 stream carries engineering volts/amps (secondary or primary,
per-device), with no knowledge of the estimator's per-unit base or the network
topology. The bridge between the two is a curated, static crosswalk -
`PmuStreamConfig` - authored once per capture, exactly as the GDA GSP/BMU
crosswalk authors SCADA measurement sets today.

### 8.1 Crosswalk contents

```
sealed record PmuStreamConfig(
    IReadOnlyList<PmuChannelMap> Channels, PmuSigmaPolicy Sigma);

sealed record PmuChannelMap(
    ushort PmuIdCode, int PhasorIndex,       // which stream, which phasor in it
    PmuChannelTarget Target,                 // bus voltage, or branch current end
    double PrimaryToBase);                   // engineering-units -> per-unit factor

// Target is one of:
//   BusVoltage(int BusId)
//   BranchCurrent(int From, int To, BranchEnd End, string? Circuit)
```

`Target` reuses the estimator's addressing exactly: bus **id** for voltages,
`(From, To, End, Circuit)` for branch currents - the same fields
`Measurement`/`MeasurementSet.TryResolveBranch` already validate, so a bad
crosswalk fails in the existing validation path with the existing diagnostics.

### 8.2 Per-unit factors

Voltage phasor -> pu: `Vpu = V_eng * PrimaryToBase`, where for a bus at base kV
`V_base_LN` (line-to-neutral), `PrimaryToBase = 1 / (V_base_LN in the same unit as
V_eng)`. The mapper carries this as one authored scalar per channel so the design
does not assume a specific PT ratio.

Current phasor -> pu: `Ipu = I_eng * PrimaryToBase`, with base current
`I_base = S_base_3ph / (sqrt(3) * V_base_LL)` = `BaseMva * 1e6 / (sqrt(3) *
V_base_LL_volts)`. `PrimaryToBase = 1 / I_base`. Per-unit current magnitude is
directly the `IMagPu` measurement unit the estimator already uses.

### 8.3 Sigma policy (TVE + status derived)

```
sealed record PmuSigmaPolicy(
    double TveBudgetP,      // performance-class P TVE budget, e.g. 0.01 (1 %)
    double TveBudgetM,      // performance-class M budget
    double UnlockInflation, // multiply sigma when unlocked-time > 0, e.g. 10x
    double DropOnInvalid);  // if data invalid, drop the row (true) vs inflate

// Per phasor at magnitude |V| (pu) or |I| (pu):
//   sigma_mag_pu   = tve * |mag|            (relative magnitude error)
//   sigma_angle_rad = tve                    (angle error ~ TVE for small angles)
//   tve = (class == M ? TveBudgetM : TveBudgetP) * (unlocked ? UnlockInflation : 1)
```

If `PmuStatus.DataValid` is false, the mapper drops that PMU's rows for that
timestamp (a NaN/garbage phasor must never reach the gain matrix - the same
doctrine as `MeasurementSet.Validate`). Angle-magnitude coupling (a large phasor
has a tighter *absolute* angle need) is captured adequately by the two
independent sigmas above; a full 2x2 phasor covariance is a documented future
refinement (section 17) - the estimator currently takes scalar per-row sigmas.

---

## 9. The Phasor Data Concentrator

The PDC turns several asynchronous single-PMU DATA streams into one time-aligned
sequence of `PdcSuperframe`s, each carrying every PMU's data for one reporting
instant, with explicit present/dropped flags. It is the classic PDC
wait-window aligner, made deterministic.

### 9.1 Transport seam

```
interface IPmuFrameSource { IEnumerable<ReadOnlyMemory<byte>> Frames { get; } }
```

The reference implementation `FileReplayPmuFrameSource` reads a length-delimited
capture file (each record = uint32 BE length + raw C37.118 frame bytes, optionally
a leading CONFIG-2 then DATA frames), yielding frames in file order. This is the
*replay* source that section 1.2 relies on. A socket source is a future
implementation of the same interface; its frames still pass the guard downstream,
so it cannot deliver a processable (>=169 h old) live frame.

### 9.2 Reporting grid and alignment key

Each PMU reports at a fixed rate (CONFIG DATA_RATE, e.g. 50 fps -> 20 ms; a
downsampled 0.5 s stream -> 2 fps). The PDC quantises every frame's `TimeUtc`
onto a common **reporting grid** of period `T_report` (the slowest common cadence
we align to - for the hybrid scheduler, the 0.5 s PMU tick). The alignment key is

```
slot = round((TimeUtc - Epoch) / T_report)     // integer slot index
```

Round-to-nearest (not truncate) so a frame arriving 1 ms early/late still lands in
its intended slot. Frames whose residual `|TimeUtc - slot*T_report|` exceeds
`T_report/2` are logged as off-grid and dropped (a misconfigured rate).

### 9.3 Wait-window buffer

The PDC holds a bounded, ordered buffer of partially-filled superframes keyed by
`slot`. For each incoming decoded `PmuData`:

1. Compute its `slot`. If `slot` is older than the newest already-emitted slot
   (a late straggler past the window), record `LateArrivalsDropped++` and discard.
2. Otherwise insert the PMU's data into the superframe for `slot` (creating it if
   new).
3. A superframe is **complete** when all expected PMUs (from `PmuStreamConfig`)
   are present.

Emission is driven by a **wait window** `W = WaitSlots * T_report` (author-set,
e.g. 2-3 report periods). A slot is emitted when either it is complete, or the
watermark (newest observed slot) has advanced beyond `slot + WaitSlots` - i.e. we
have waited `W` for the missing PMUs and give up. Emission is strictly in
increasing `slot` order, so downstream sees a monotonic timestamp sequence
(load-bearing for invariant #2 and warm-start chaining).

```
sealed record PdcSuperframe(
    DateTime TimeUtc,                         // slot * T_report, absolute UTC
    IReadOnlyList<PdcPmuSlot> Pmus);          // one per expected PMU, in config order

sealed record PdcPmuSlot(ushort PmuIdCode, PmuData? Data, PmuSlotState State);
enum PmuSlotState { Present, Dropped, Late, CrcFailed, StatusInvalid }
```

### 9.4 Dropouts, latency, and quality

- **Dropout** (a PMU absent when the window closes): `PdcPmuSlot.State = Dropped`,
  `Data = null`. The mapper simply authors no rows for it - the estimator handles
  missing measurements natively (that is the entire point of an estimator vs a
  power flow; a dropped PMU degrades redundancy, not correctness). If dropouts
  make the PMU-observable island unobservable, the linear estimator reports
  `Unobservable` honestly (section 11.4) and the hybrid scheduler falls back to
  the last SCADA state as prior (section 12.3).
- **Latency**: bounded by `W`. A frame later than `W` is a `Late` straggler,
  counted and dropped, never retro-applied to an emitted slot (that would emit a
  second, out-of-order superframe for a past instant - forbidden). Choosing `W`
  trades completeness against emission delay; it is an authored knob, not adaptive
  (determinism, section 14).
- **CRC / status quality**: a frame that failed CRC at decode never reaches the
  PDC as data; the PDC marks that slot `CrcFailed` if it knows a frame was
  expected. A frame present but `StatusInvalid` (STAT data-invalid) is carried as
  `StatusInvalid` and dropped at the mapper per the sigma policy.

### 9.5 Determinism of alignment

Given the same multiset of decoded frames, the emitted `PdcSuperframe` sequence
is identical regardless of arrival interleaving, because slotting is a pure
function of `TimeUtc` and emission order is by `slot`. There is no wall-clock in
the alignment math; the "watermark" is the max observed frame slot, not real time.
A test feeds the same frames in several shuffled orders and asserts identical
superframe output (section 15.4).

---

## 10. Mapping concentrated phasors to estimator rows

`PmuMeasurementSetMapper.Map(PdcSuperframe sf, PmuStreamConfig cfg, DateTime?
utcNow) -> MeasurementSet` produces a `gridsim-measurements/1` set stamped
`TakenUtc = sf.TimeUtc`. **The guard is called here** (section 13). The set is
then handed to the existing `WlsStateEstimator` (hybrid path) or converted to the
linear estimator's rectangular input (section 11).

### 10.1 Voltage phasor -> VmPu + VaDeg

Each voltage phasor mapped to a `BusVoltage(busId)` target authors **two** rows:

- `MeasurementKind.VmPu`, value `|V|_pu`, sigma `sigma_mag_pu`, `Bus = busId`,
  `Source = "pmu"`.
- `MeasurementKind.VaDeg`, value = the **re-referenced** angle in degrees, sigma
  `sigma_angle_rad * 180/pi`, `Bus = busId`, `Source = "pmu"`.

**Re-referencing (the reference problem).** All synchrophasor angles in one
superframe share the C37.118 common time reference (a cosine at nominal frequency
at `TimeUtc`); the estimator's datum is the slack bus. They differ by a common,
slowly-drifting offset `delta(t)`. Exactly as the Gridradar design note
prescribes, the mapper carries only angle *differences* against an authored datum
PMU bus, anchored to the estimator's own current view of that datum:

```
Va_row(node) = angle_pmu(node) - angle_pmu(datum) + Va_prior(datum_bus)
```

`Va_prior(datum_bus)` is the datum bus angle from the warm-start prior state the
scheduler already threads (section 12). Differences cancel `delta(t)` exactly; the
anchor puts the row in the slack frame. The estimator does **not** solve for a
per-feed offset state (one feed, few nodes: the offset would consume a
measurement of information for no gain). A `VaDeg` row on the slack bus is legal
and structurally empty (existing behaviour) and is skipped by the mapper as
uninformative.

### 10.2 Current phasor -> IMagPu + IAngDeg (one new MeasurementKind)

A current phasor carries information the existing `IMagPu` row alone discards: its
*angle*. To use the full phasor without abandoning the estimator's polar state and
its validated nonlinear kernels, add **one** measurement kind:

```
// added to MeasurementKind (Estimation/MeasurementKind.cs):
IAngDeg   // Branch current phase angle into the measured end, degrees, slack-referenced.
          // Pairs with IMagPu to represent a full current phasor. Addresses a branch
          // (From/To/End/Circuit), like the flow/current-magnitude kinds.
```

`h(x)` for `IAngDeg`. The current at end `a` is `I_a = conj(S_a / V_a)`, so its
angle is `angle(I_a) = angle(V_a) - angle(S_a) = Va[a] - atan2(Q_a, P_a)`, where
`P_a`, `Q_a` are the branch's real/reactive flow into end `a` (per-unit) computed
by the existing `FlowP`/`FlowQ` kernels. Thus

```
h_IAngDeg(x) = Va[a] - atan2( Qflow(a), Pflow(a) )        (radians internally; ingest deg->rad)
```

This deliberately mirrors `IMagPu` (`|I| = sqrt(P^2+Q^2)/Vm[a]`), reusing the same
flow kernels - no copy-drift, the same discipline as `CurrentMagRow`.

**Jacobian row** `d h_IAngDeg / dx`. With `S2 = P_a^2 + Q_a^2`:

```
d/dx [ atan2(Q, P) ] = ( P * dQ/dx - Q * dP/dx ) / S2

=> row = e_{Va[a]} - ( P * dQ/dx - Q * dP/dx ) / S2
```

`e_{Va[a]}` is the unit entry in end `a`'s angle column (omitted if `a` is the
slack - then the current-angle row is measured purely through the flow terms).
`dP/dx` and `dQ/dx` are the exact partials `FlowRow` already emits (captured via
the existing `PairSink` pattern, precisely as `CurrentMagRow` does). A branch
carrying no flow (`S2 ~ 0`) contributes a structural near-zero row and is skipped,
same guard as `CurrentMagRow`. `StructuralColumns` for `IAngDeg` is identical to
`IMagPu`/flow rows (the four columns: both end angles + both end Vm). The residual
back-conversion in `WlsStateEstimator.Assemble` gets one `switch` arm:
`IAngDeg => 180/pi` (it is an angle, like `VaDeg`).

The current phasor angle is re-referenced identically to the voltage angle
(section 10.1) because `h` already lives in the slack frame (`Va[a]` is
slack-referenced state): author `I_ang_row = angle_pmu(I) - angle_pmu(Vdatum) +
Va_prior(datum_bus)`.

### 10.3 Why this mapping is the right seam

It keeps the estimator's state polar and its kernels untouched-but-reused, adds
exactly one measurement kind, and lets the *same* `WlsStateEstimator` fuse PMU and
SCADA rows in one solve (the hybrid path). Voltage phasors become the cheapest,
linear `VaDeg`/`VmPu` rows; current phasors reuse the flow algebra. Bad-data
detection, chi-square, observability and warm-start all apply unchanged.

---

## 11. Linear single-shot PMU state estimator

When a subnetwork is fully observed by PMUs (voltage and current phasors), the
measurement equation is **linear** in the rectangular bus-voltage state, so the
estimate is a single non-iterative solve of a constant-gain system - the classic
Phadke linear PMU estimator. This is `LinearPmuStateEstimator`
(`Estimation/LinearPmuStateEstimator.cs`), a **static, measurement-only filter**
(the `linear-PMU` method the review log already lists as unconditionally
permitted; no DSE entry).

### 11.1 State and measurement model (rectangular)

State: complex bus voltages of the PMU-observable island,
`x = [Vr_1..Vr_p, Vi_1..Vi_p]` (real then imaginary parts, `p` buses). All phasors
in a superframe are in one common reference (section 10.1), so no per-bus angle
datum is needed *within* the linear solve; the whole solution is rotated into the
slack frame afterward (section 11.3).

- **Voltage phasor** at bus `k`: measures `V_k` directly. Rows: `Vr_k = Re(V_k)`,
  `Vi_k = Im(V_k)`. Constant, trivially linear.
- **Current phasor** on branch `k->m`, measured end `k`: from the pi-model,
  `I_km = (y_series + y_shunt_k) V_k - y_series V_m`, complex-linear in the
  voltages. With `y_series = g + jb`, `y_shunt_k = jb_c/2` (and tap/phase-shift
  folded into the same `yff`/`yft` stamps `MeasurementModel` already computes),
  the real/imag rows are linear combinations of `Vr_k, Vi_k, Vr_m, Vi_m`.

Stacking real and imaginary parts gives a **real** linear system `z = H x + e`
with a **constant** real matrix `H` (built once from the Y-bus stamps, reused for
every superframe - the standout property of linear PMU SE).

### 11.2 Weighted least squares (single shot)

```
x_hat = (H^T W H)^-1 H^T W z          W = diag(1/sigma_i^2)
```

Formed once: `Gain = H^T W H` (constant if the sigma structure is constant), and
per superframe only the right-hand side `H^T W z` changes. Solve with the existing
`ILinearSolver` (`DenseLuSolver`, or the sparse backend above the bus threshold) -
the same solver seam the WLS estimator pivots on, so the linear PMU estimator
inherits the validated linear algebra and its machine-epsilon behaviour. Because
`H` is constant, the gain can be **factored once** and back-substituted each
superframe (the natural real-time cadence); the design keeps a cached
factorisation keyed by the sigma vector, invalidated when the observed-PMU set or
sigma policy changes.

### 11.3 Output in the estimator's convention

Convert `x_hat` (rectangular, common reference) to `(Vm, VaDeg)` per bus:
`Vm_k = hypot(Vr_k, Vi_k)`, `Va_k = atan2(Vi_k, Vr_k)`. Rotate all angles by the
common offset so the datum bus matches the warm-start prior:
`Va_k <- Va_k - Va_hat(datum) + Va_prior(datum_bus)`. Emit an `EstimationResult`
in the identical shape `WlsStateEstimator` produces (so replay/UI/export and
`ToPowerFlowResult` work unchanged), with `Iterations = 1`, `MaxStateUpdate = 0`
(single shot), and residuals/chi-square computed against the same declared sigmas.

### 11.4 Observability and honesty

If the PMU set does not observe every state direction, `H^T W H` is singular; the
estimator reports `EstimationStatus.Unobservable` (never NaN), exactly as the WLS
estimator does on a singular gain - the reported state is the prior, not an
estimate. This is what the hybrid scheduler uses to decide when the PMU-only fast
path is trustworthy vs when to defer to the last SCADA state.

---

## 12. Hybrid multi-rate SCADA(~5 s) + PMU(~0.5 s) fusion scheduler

`HybridMultiRateScheduler` (`Simulation/HybridMultiRateScheduler.cs`) produces the
`IEnumerable<MeasurementSet>` window stream that `RealtimeEstimatorEngine.Run`
already consumes - so it plugs into the resident engine without changing it. It
interleaves two cadences and threads one warm-start state through both, honouring
the guard on every window.

### 12.1 Cadences and window construction

- **PMU tick** every `T_pmu` (~0.5 s): drain the PDC of any newly-complete/expired
  `PdcSuperframe`s, map each to a `MeasurementSet` (section 10) tagged with source
  `"pmu"`, timestamp = superframe UTC.
- **SCADA tick** every `T_scada` (~5 s, a multiple of `T_pmu`): pull the aligned
  SCADA measurement window (the existing GDA/SCADA row set) for that instant.

The scheduler emits windows in strict timestamp order. It supports two fusion
modes (author-selected):

**Mode A - single hybrid WLS (recommended default).** At each SCADA tick, emit one
combined `MeasurementSet` containing SCADA rows **and** the concurrent PMU rows
(VmPu/VaDeg/IMagPu/IAngDeg), estimated in one nonlinear `WlsStateEstimator` solve.
Between SCADA ticks, at each PMU tick emit a PMU-only window; the engine runs the
**linear** PMU estimator on it (via a scheduler-selected estimator, section 12.2)
for the PMU-observable buses, warm-started from the last full state so unobserved
buses simply retain the prior. This gives full-network accuracy at 5 s and fast
angle/observed-bus tracking at 0.5 s.

**Mode B - two-stage / hierarchical.** SCADA WLS produces the full state at 5 s;
each PMU tick runs the linear estimator on the PMU island and *corrects* only
those buses, leaving the rest at the SCADA state. Simpler, but the PMU update does
not re-weight against SCADA rows; documented as the lighter option.

### 12.2 Estimator selection per window

The window carries a tag (`MeasurementSet.Note` or a scheduler side-channel)
saying whether it is PMU-only or hybrid. The scheduler drives estimation by
choosing the estimator per window: PMU-only -> `LinearPmuStateEstimator`; hybrid
-> `WlsStateEstimator` with PMU + SCADA rows. Both are static filters; neither
touches the DSE gate. (If a future variant wants the resident `RealtimeEstimator-
Engine` to own the selection, extend it with an `IStateEstimator` seam - section
17 - rather than branching inside the engine.)

### 12.3 Warm-start threading and dropout fallback

Each converged estimate becomes the next window's warm-start prior (via
`ToPowerFlowResult`), exactly as `RealtimeEstimatorEngine` already chains. The
prior also supplies the angle anchor `Va_prior(datum_bus)` the mapper needs
(section 10.1). If a PMU tick is `Unobservable` (dropouts), the scheduler does not
emit a bad state: it carries the prior forward unchanged and marks the frame
skipped, so the fast path degrades gracefully to "hold last state until the next
SCADA solve".

### 12.4 Cadence, timestamps, and the no-future guarantee

Every emitted window's `TakenUtc` is a real measurement instant (a PDC slot or a
SCADA instant), always <= the newest input frame's time - the scheduler never
fabricates a future-dated window, and never interpolates *ahead* of data (it may
hold the last state, which is a present/past claim, never a forward one). The
0.5 s "real-time" cadence is wall-clock pacing done by the host loop; the
scheduler and engine process each window as a pure function of its (already-
historical, guard-checked) contents, so the whole pipeline is deterministic and
replay-testable. This is invariant #2 (no output stamped later than its newest
input) and #4 (real-time proven on replay/permitted-age), realised in the
scheduler.

---

## 13. Horizon guard integration and the guarded-seam registry

### 13.1 Where the guard is called

The synchrophasor pipeline introduces one new **dated ingestion seam**:
`PmuMeasurementSetMapper.Map`, which calls
`ForwardInferenceGuard.RejectFuture(sf.TimeUtc, utcNow, "PMU superframe")` before
producing any `MeasurementSet`. Because the produced set carries
`TakenUtc = sf.TimeUtc`, `MeasurementSet.Load`'s existing guard also fires if the
set is ever round-tripped through disk. Belt and braces: a PMU observation cannot
reach an estimator without passing the horizon at least once, by construction.

The PDC itself (`PhasorDataConcentrator`) may additionally guard at slot emission
as defence in depth (reject a superframe whose slot time is newer than the
horizon), but the mapper is the canonical, tested seam.

### 13.2 Registry updates (must ship with the code)

The seam table of technical ch. 31 lists the guarded
seams. Add `PmuMeasurementSetMapper.Map` (and, if implemented, the PDC emission
guard) to that list. Add a per-seam test to `RestrictionInvariantTests` /
`ForwardInferenceGuardTests` that feeds a superframe stamped `UtcNow - 1 h` and
asserts `ForwardInferenceException` (the same shape as the existing
`GdaReplayLoopTests` seam pin), and one stamped `UtcNow - 200 h` that passes.

### 13.3 DSE gate: explicitly not triggered

The linear PMU estimator and the hybrid scheduler consume no `Dynamics/` process
model - they are measurement-only filters. Per `RestrictionReview.AssertPermitted`,
a method that does not use a dynamics model is permitted unconditionally, so
**no** `RestrictionReviewEntry` is added and `RestrictionReview.Reviewed` is
unchanged. The design note for reviewers: should anyone later add a PMU-rate
recursive tracker (an EKF/UKF predicting between frames), that *does* fuse a
process model, and section "How a new DSE method gets added" in the review log
applies - a row plus a `RestrictionReviewEntry` before it may run. This design
deliberately stops short of that line.

---

## 14. Determinism requirements

The whole pipeline is a pure function of (raw frame bytes, crosswalk, sigma
policy, injected `utcNow`). No wall clock, no ambient culture, no floating-point
nondeterminism beyond IEEE-754.

- **No wall-clock in the math path.** `utcNow` is always injected (as
  `RealtimeEstimatorOptions.UtcNow` already is) so the guard boundary is fixed in
  tests. PDC slotting uses frame timestamps only; the watermark is the max
  observed slot, not real time.
- **Fixed iteration structure.** The linear estimator is single-shot; the WLS path
  has its existing deterministic loop. No adaptive wait-window, no random tie-break
  in alignment (`slot` order is total; ties within a slot resolve by PMU config
  order).
- **Byte-exact parsing.** Big-endian reads via `BinaryPrimitives`; polar->rect and
  int-scale conversions are fixed formulae; float32 read is exact IEEE-754. Given
  the same bytes, the parsed `DataFrame` is bit-identical across runs and machines.
- **Deterministic ordering.** Measurement rows are authored in a fixed order (PMU
  config order, then phasor index, VmPu before VaDeg, IMagPu before IAngDeg), so
  the gain matrix assembly order - and thus the last-bit summation - is stable
  (the same bit-for-bit doctrine `MeasurementModel` already upholds).

---

## 15. Validation and test plan

All new tests live beside the existing suites in `tests/GridSim.Tests`. The bar is
the project's: machine-epsilon cross-checks against an independent reference, and
determinism.

### 15.1 Decoder conformance (independent reference vectors)

- **Reference frame vectors.** Decode CONFIG-2 and DATA byte vectors taken from an
  *independent* encoder (IEEE C37.118.2 Annex examples and/or an openPDC / pypmu
  capture checked into `data/synchrophasor/`). Assert every parsed field matches
  the reference to the bit (int fields exactly; float fields exactly, since the
  bytes are exact IEEE-754). This is the independent cross-check that the decoder
  is standard-faithful, not self-consistent.
- **CRC.** Cross-check `C37118Crc.Compute` against the CHK embedded in every
  reference frame, and cross-check the fast table-driven form (if used) against the
  bitwise form over 10k random buffers - byte-exact.
- **Format matrix.** One decode test per FORMAT combination (int/float x
  rect/polar, int/float freq), asserting the same engineering phasor within
  rounding for int forms and exactly for float forms.
- **Timestamp.** SOC/FRACSEC -> UTC checked against hand-computed instants for
  several TIME_BASE values; assert no double-rounding drift (ticks form).
- **Resilience.** Truncated frame, wrong CRC, DATA-before-CONFIG, unsupported
  frame type each yield the correct `FrameDecodeStatus` and never throw.

### 15.2 Encode->decode->estimate round-trip (machine epsilon)

The end-to-end truth test. Take a solved `PowerFlowResult` on a small case
(case9 / case14). Synthesise a C37.118 DATA frame whose voltage phasors are the
solved bus voltages and whose current phasors are the solved branch currents
(computed from the same Y-bus stamps), with a chosen TIME_BASE/FORMAT. Decode it,
concentrate it, map it (section 10). Then:

- **Linear PMU estimator** on a fully-PMU-observed case must recover the solved
  state to ~1e-9 (the analogue of the existing
  `Voltage_only_pmu_set_recovers_the_state_exactly`, extended to include current
  phasors). This is the machine-epsilon cross-check.
- **Hybrid WLS** with the same PMU rows plus a redundant SCADA row set must recover
  the same state to solver tolerance, and the residuals of the exact rows must be
  ~0.
- **Cross-estimator agreement.** On a case observable by *both* the linear PMU
  estimator and the nonlinear WLS (PMU rows fed to `WlsStateEstimator` via the new
  `IAngDeg`/`IMagPu` rows), the two estimates must agree to ~1e-8 - an independent
  numerical cross-check of the new Jacobian against the linear formulation.

### 15.3 New Jacobian correctness (`IAngDeg`)

Finite-difference `h_IAngDeg(x)` against the analytic Jacobian row (section 10.2)
at several random feasible states on case14/case118; assert max abs error <~1e-6
(central differences), matching how the other measurement-kind Jacobians are
pinned. Also assert `StructuralColumns(IAngDeg)` exactly equals the columns the
analytic row writes (no missing/extra structural entry - the sparse backend
depends on it).

### 15.4 PDC alignment determinism

- Feed the same multiset of decoded frames to `PhasorDataConcentrator` in several
  shuffled arrival orders; assert byte-identical `PdcSuperframe` sequences.
- Drop one PMU for a slot; assert the slot emits at window close with that PMU
  `Dropped` and the rest `Present`, and that a late straggler for an already-
  emitted slot is counted and discarded (no out-of-order emission).
- Assert emitted superframe timestamps are strictly increasing.

### 15.5 Restriction invariants

- A superframe stamped `UtcNow - 1 h` fed to `PmuMeasurementSetMapper.Map` throws
  `ForwardInferenceException`; one at `UtcNow - 200 h` maps cleanly. (New rows in
  `ForwardInferenceGuardTests` / `RestrictionInvariantTests`, section 13.2.)
- `RealtimeEstimatorEngine.Run` over a `HybridMultiRateScheduler` window stream
  containing one too-fresh window records it as `WindowsRejectedFresh` and never
  estimates it (reuses the engine's existing behaviour - assert the summary).
- Assert no emitted `RealtimeEstimateFrame.StateTimeUtc` exceeds the newest input
  frame time (invariant #2).
- Assert `RestrictionReview.Reviewed` is unchanged (the static-filter methods add
  no DSE entry) and that `RestrictionInvariantTests` still passes green.

### 15.6 Determinism regression

Run the full replay pipeline twice with a fixed `utcNow`; assert bit-identical
`EstimationResult`s (Vm, VaDeg, ObjectiveJ) across runs.

---

## 16. 0-warning build gate compliance

The new code compiles clean under the project's warnings-as-errors gate:

- **Nullable reference types** enabled and honoured; the decode outcome uses the
  `DecodeResult`/`FrameDecodeStatus` struct rather than nullable returns for the
  hot path, and `PmuData? Data` on a dropped slot is explicitly nullable and
  null-checked at every use.
- **XML docs on every public member** (types, records, enum members, public
  methods) - the decoder, PDC, mapper, estimator and scheduler all expose only
  documented public surface; internals are `internal`.
- **No `unsafe`, no `BitConverter`** for multi-byte reads - `BinaryPrimitives`
  big-endian readers only, so there is no host-endianness or alignment warning
  surface.
- **Culture-invariant** parsing/formatting; ASCII station/channel names decoded
  with an explicit encoding.
- **No unused usings, no unreachable arms**; the FORMAT/kind switches are
  exhaustive with an explicit `default` that throws `InvalidOperationException`
  naming the unhandled case (the pattern `MeasurementModel` uses).
- **No analyser suppressions** introduced; if a span-based parser trips a
  CA1062/CA1031-style rule, the code is restructured, not suppressed.
- Deterministic, allocation-conscious parsing (`ReadOnlySpan<byte>`,
  `stackalloc` for small fixed buffers) so no perf-analyser noise.

The design adds no dependency: C37.118 is decoded with the BCL
(`System.Buffers.Binary`, `System.Numerics` for phasors) only - no external NuGet,
consistent with the self-contained core.

---

## 17. Seams to extend (future work, explicitly deferred)

- **CONFIG-3** (fractional scaling, long names, baseline PMU coordinates): decode
  path parallels CONFIG-2; `C37118Decoder` already discriminates the type and
  rejects it cleanly, so support drops in behind the same interface.
- **Command channel / live socket source**: `IPmuFrameSource` has a socket
  implementation shape; it remains guard-bounded (cannot deliver processable live
  frames). A CMD-frame request enum is documented but GridSim never drives a live
  device - it replays captures.
- **Full 2x2 phasor covariance** (magnitude/angle correlation) instead of two
  scalar sigmas: needs a block-weighted WLS row; a documented refinement of the
  sigma policy, not a change to the mapping.
- **Rectangular current-phasor kinds** (`IRePu`/`IImPu`) as an alternative to
  `IMagPu`+`IAngDeg` for better conditioning near zero flow: an additive
  measurement-kind option, same kernel reuse.
- **`IStateEstimator` seam** so `RealtimeEstimatorEngine` can own linear-vs-WLS
  selection internally rather than the scheduler choosing; a clean refactor when a
  third estimator (e.g. branch-current DSSE) arrives.
- **PMU-rate recursive tracker (EKF/UKF)**: the moment a predict step integrates a
  process model this becomes a Dynamics->Estimation fusion - it must go through the
  review-log gate (section 13.3) before it can run.

---

## 18. Implementation checklist (order of work)

1. `C37118Crc` + CRC tests against reference frames.
2. `C37118CommonHeader`, `C37118FrameType`, `C37118DataFormat`, SOC/FRACSEC->UTC +
   tests.
3. `ConfigFrame2` decode (`C37118Decoder` config path) + config-vector tests.
4. `DataFrame`/`PmuData`/`Phasor`/`PmuStatus` decode (all four FORMAT combos) +
   format-matrix tests.
5. `PhasorDataConcentrator` + `PdcSuperframe` + alignment/determinism/dropout
   tests.
6. `MeasurementKind.IAngDeg` + `MeasurementModel` h/Jacobian/StructuralColumns arms
   + `Assemble` residual arm + finite-difference Jacobian test.
7. `PmuStreamConfig`/`PmuMeasurementSetMapper` (with the guard call) + mapping and
   guard-seam tests; update the seam table of technical ch. 31.
8. `LinearPmuStateEstimator` + machine-epsilon recovery and cross-estimator
   agreement tests.
9. `HybridMultiRateScheduler` + end-to-end round-trip and no-future invariant tests
   over `RealtimeEstimatorEngine`.
10. `FileReplayPmuFrameSource` + a small checked-in capture for the round-trip
    replay test.

Each step lands with its tests green and the 0-warning gate intact before the
next begins.
