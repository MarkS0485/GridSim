# 20. The RTU/SCADA interposer

This chapter specifies the live-telemetry ingest path: the pipeline that takes raw SCADA/RTU protocol frames off a TCP
stream, validates and decodes them, folds their quality descriptors into estimator weight, collapses them into
windows, maps their protocol addresses onto buses and branches through a user-authored crosswalk, and emits horizon-guarded
`gridsim-measurements/1` windows and a quarantined GDA-shaped Parquet archive. It names every class, every decoded
IEC 60870-5-104 and Modbus TCP construct, every crosswalk field, every default, and the invariants and tests that hold
the design together. The user-level task is in [ch. 25, Interposing live telemetry](<../manual/0025 Interposing live telemetry.md>).

Two rules govern everything here. Live telemetry is present-time, so the 169 hour horizon guard
([ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>)) stands between it and the estimator and
is never bypassed. Live SCADA is excluded from GDA's public-data lake by rule
([ch. 9, GDA integration](<0009 GDA integration.md>)), so the archive sink is firewalled from the lake in code.

## Pipeline

Three projects hold the subsystem along a strict dependency line. `GridSim.Core` holds decode, correction and mapping
and opens no sockets; its only file I/O is loading the crosswalk and saving and loading window files. `GridSim.Gda`, which carries the Parquet dependency, holds the archive. `GridSim.Cli` holds the
sockets, the loop and the verb.

```
 TCP bytes
   |  IRtuTransport.TryReadFrame        reassemble one frame, resync on a bad lead-in   (Cli)
   v
 FrameCorrection.Validate               framing gate, accepted/rejected tallies          (Core)
   v
 IRtuProtocol.Decode                    frame -> RtuPoint[] (address, value, quality, device time)
   |      Iec104Protocol -> Iec104Frame + Iec104Asdu + QualityFold.FromIec104
   |      ModbusProtocol -> ModbusFrame (+ request pairing)
   v
 InterposePipeline._window              append points, stamped with the frame sequence number
   v   (every --window-ms, and once at shutdown)
 InterposePipeline.FlushWindow          window instant = latest device stamp, else receive clock
   v
 RtuMeasurementSetMapper.Map            horizon guard, latest-per-address, drop, resolve, type, scale, base, datum
   v
 MeasurementSet (source "rtu")
   +--> MeasurementSet.Save             gridsim-measurements/1 file    -> serve
   +--> QuarantineLiveStore.WriteWindow GDA-shaped Parquet, firewalled
```

| Stage | Class | File | Responsibility |
|---|---|---|---|
| Verb and loop | `RunInterpose` | `src/GridSim.Cli/Program.cs` | Flags, endpoint, loop, flush timer, sinks, summary, audit entries |
| Transport | `TcpDelimitedFrameTransport`, `TcpMbapFrameTransport`, `TransportFactory` | `src/GridSim.Cli/InterposeTransport.cs` | Bind, accept one client, buffer, yield whole frames, resync |
| Framing gate | `FrameCorrection` | `src/GridSim.Core/Interpose/FrameCorrection.cs` | Per-protocol yes/no on a delimited frame; accept and reject tallies |
| Protocol | `IRtuProtocol`, `Iec104Protocol`, `ModbusProtocol`, `RtuProtocolRegistry` | `src/GridSim.Core/Interpose/` | Frame to `RtuPoint[]`; protocol lookup by name |
| Object models | `Iec104Frame`, `Iec104Asdu`, `ModbusFrame` | `src/GridSim.Core/IO/Telemetry/` | APCI, ASDU and MBAP codecs |
| Quality | `QualityFold` | `src/GridSim.Core/Interpose/QualityFold.cs` | Descriptor to `RtuQuality`; drop decision; sigma multiplier |
| Time | `TimeDecode` | `src/GridSim.Core/Interpose/TimeDecode.cs` | CP56Time2a, DNP3 and IEC 61850 time tags to UTC |
| Window | `InterposePipeline` | `src/GridSim.Core/Interpose/InterposePipeline.cs` | Ingest, accumulate, flush |
| Crosswalk | `RtuPointMap`, `RtuPointBinding` | `src/GridSim.Core/Interpose/RtuPointMap.cs` | `gridsim-rtu-pointmap/1` load, validate, resolve |
| Mapping | `RtuMeasurementSetMapper` | `src/GridSim.Core/Interpose/RtuMeasurementSetMapper.cs` | Window to guarded `MeasurementSet` |
| Archive | `QuarantineLiveStore` | `src/GridSim.Gda/Interpose/QuarantineLiveStore.cs` | Firewalled Parquet sink |

`RtuPoint` carries a protocol-native `Address` string, an `RtuPointType` (`Analog`, `Counter`, `BinaryStatus`,
`Setpoint`, `StepPosition`, `Bitstring`), a `double` `Value`, folded `RtuQuality` flags, an optional device `StampUtc`, and a `FrameSeq` that
`InterposePipeline.Ingest` stamps (1-based, one per decoded frame) so that a 32-bit value can be assembled from two
registers of one response and never from halves of two polls.

### The verb and its loop

`RunInterpose` (`src/GridSim.Cli/Program.cs`) runs this sequence and fails loud at each step.

1. `--list-protocols` prints `RtuProtocolRegistry.Describe()` and returns (exit 0).
2. `--protocol` must name a registered protocol (case-insensitive). Otherwise the verb prints the usage and, when a name
   was given, `error: unknown protocol '<name>'. Try --list-protocols.`, and exits 2.
3. `--crosswalk` is required (`error: --crosswalk <map.json> is required.`, exit 2). The file is read with
   `RtuPointMap.Load`; any exception prints `error loading crosswalk '<path>': <message>` and exits 1. The file is
   recorded as an audit input by SHA-256 when `--audit-log` is on.
4. The endpoint is `--listen host:port` (two fields split on `:`) or `--listen port`, which binds host `0.0.0.0`. A third
   form fails with exit 2 (`error: --listen expected host:port but got '...'`), so an IPv6 literal is not accepted. With no
   `--listen`, the port is `--port` or the protocol default and the host is `0.0.0.0`. `TcpDelimitedFrameTransport` and
   `TcpMbapFrameTransport` treat `0.0.0.0`, `*` and the empty string as `IPAddress.Any`, parse any other host as an IP
   address, and fall back to the first DNS address.
5. The case label is the positional argument, else the crosswalk's `case`, else `rtu`.
6. `--window-ms` is `Math.Max(1, FlagInt(...))` with default 1000. `--monitor` with `--measurements-dir` or
   `--out-store` is refused (`error: --monitor writes no windows, so it cannot be combined with ...`, exit 2); until
   October 2026 the sink flag was dropped silently. A `QuarantineLiveStore` constructor failure prints
   `error opening quarantine store ...` and exits 1. `--source-id` defaults to `rtu_live`. `--no-parquet` is consumed
   and ignored. `--live-seconds S` (above 0, at most 86400; anything else exits 2) bounds the run by the wall clock,
   counted from when the listener is up. `--tolerate-malformed` accepts malformed frames without failing the run (below).
7. `TransportFactory.Open` binds the listener; an exception prints `error opening <protocol> transport on <host>:<port>`
   and exits 1.
8. The loop runs until a key is pressed (interactive, `--ticks` 0), until `--ticks N` frames have been read, or until
   `--live-seconds` has elapsed. Each iteration calls `TryReadFrame` with a 200 ms timeout. A frame goes through
   `InterposePipeline.Ingest`; `--ticks` counts every frame the transport yields, including control frames and frames
   the gate rejects. The first malformed frame (rejected by the gate, or framed but undecodable) is named on standard
   error; later ones are counted. Then, if the wall clock has passed `nextFlush`, the loop advances `nextFlush` by
   `--window-ms` and calls `FlushWindow(DateTime.UtcNow, freshPolicy)` through `FlushAndEmit`, which judges the
   window against the real horizon (see The horizon guard).
9. After the loop, `FlushAndEmit` runs once more so a short run keeps its last points, and the summary line prints.

`FlushAndEmit` catches an `InvalidDataException` from the mapper (a point that does not fit its binding, or a binding on
a step position or bitstring point), prints `window discarded (crosswalk mismatch): <message>` to stderr and returns; the
listener keeps running. A window older than the horizon is emitted. A window newer than the horizon is held: it is
never written to `--measurements-dir` (the first held window is named on stderr, later ones are counted); with
`--out-store` (and not `--monitor`) it is written to the quarantine store as a quarantine capture, printed as
`window <TakenUtc>: N measurement(s) held (newer than the horizon) [quarantine capture only]`, and recorded as a
`LiveDataQuarantined` security event when auditing is on. `FlushWindow`
clears the window in a `finally` block, so the failed points never reach a later window, increments
`InterposePipeline.MapErrors` and rethrows, so an embedding caller that catches it finds the next window clean. `RtuPointMap.Load` also runs `ValidateRegisterLayout`, which refuses at load a 32-bit
binding on register 65535 and a 32-bit binding whose second register is bound separately.

`Emit` skips a window with no measurements. Otherwise it saves the window file (`yyyyMMddTHHmmssfffZ` of `TakenUtc`,
then `-` and a six-digit sequence number that counts emitted windows from 0 in this run), writes the Parquet partition
if the store is open, and prints `window <TakenUtc>: N measurement(s) -> <file>`. Both sinks number windows from 0 per run, with
separate counters (`seq` in `RunInterpose`, `_seq` in `QuarantineLiveStore`) that advance together, once per emitted window.
Neither sink overwrites: both open the file through `GridSim.Core.IO.NeverOverwrite.CreateNew` (`FileMode.CreateNew`),
which inserts `-dup1`, `-dup2`, ... before the extension when the name is taken (up to 1,000, then `IOException`). A
second run that emits a window with the same `TakenUtc` and sequence number therefore keeps both files; the JSON sink
prints a warning and counts it in `duplicate-names`.

The summary is `stopped: accepted A, rejected R, decode-errors D, map-errors M, points P, step-positions S, bitstrings B,
windows-written W, duplicate-names N, client-resets C, unsupported-type-ids U, held-fresh H, quarantined Q,
discarded-bytes X`, where `points` counts every decoded point and `step-positions` and `bitstrings` the points of those
two types among them (never estimator inputs), `client-resets` is `IRtuTransport.ClientResets`, `unsupported-type-ids`
lists `Iec104Protocol.UnsupportedTypeIds` as `typeId x count` in type order (`none` when empty or for Modbus),
`held-fresh` counts windows newer than the horizon, `quarantined` the held windows written to the quarantine store, and
`discarded-bytes` the octets the transport dropped while resynchronising.

Exit codes, in order of precedence:

| Code | When |
|---|---|
| 3 | `map-errors` is above 0 at the stop (a window was discarded). |
| 2 | Usage (missing or unknown protocol, missing crosswalk, bad `--listen`, `--ticks`, `--window-ms` or `--live-seconds` value, `--monitor` with a sink, unknown flag, `--audit-log` misuse); or, at the stop, `--measurements-dir` was given and windows were held as newer than the horizon (`REJECTED (forward inference): N window(s) newer than the 169 h horizon were not written to --measurements-dir ...`). |
| 1 | A crosswalk, store or transport failure; any malformed frame (`rejected` plus `decode-errors` above 0) without `--tolerate-malformed` (`error: interpose: N malformed frame(s) ...; pass --tolerate-malformed to accept a noisy line.`); or a `--ticks N` run that `--live-seconds` ended before N frames arrived. |
| 0 | Otherwise. With `--tolerate-malformed` the malformed count is still printed as a warning. A run without `--ticks` that `--live-seconds` ended with no frame at all exits 0 with a warning to check the device and the port. |

`--monitor` and a store-only run hold fresh windows by design and do not set exit 2. With `--audit-log`, a `started` entry
is written when the listener opens (protocol, endpoint, `monitor` flag, the crosswalk input hash) and the run's closing entry
is written at exit, its outcome carrying the exit code and the summary counters; frame contents are never logged.

## Transports and framing

Both transports accept one TCP client at a time. A new client is accepted only after the previous one disconnects. Reading
is timeout-bounded (`ReadTimeout` equal to the 200 ms loop timeout), 8192 octets per read. The Modbus transport never
writes to the stream. The IEC 104 transport does, because in IEC 104 terms the interposer is the controlling station
and must drive the session (`Iec104ControllingStation`):

- on connect it sends `STARTDT act`; a `STARTDT con` from the outstation is recorded;
- it answers a `TESTFR act` with `TESTFR con` at once;
- it counts received I-frames in V(R) (mod 32768) and acknowledges them with an S-frame carrying N(R) = V(R) after `W`
  (8) unacknowledged I-frames, or `T2Ms` (10 s) after the first unacknowledged one;
- after `T3Ms` (20 s) with no frame received it sends `TESTFR act`, once per idle period.

- GR-803: a `STARTDT act` or `TESTFR act` not confirmed within `T1Ms` (t1, 15 s) closes the connection (`CloseRequested`,
  with `CloseReason`); the transport drops the client, counted as a reset, and waits for the next one.
- GR-823: an I-frame whose N(S) is not V(R) (an I-frame lost or repeated) closes the connection the same way; that frame is
  still handed up, is not counted in V(R) and is not acknowledged (`SequenceErrors` counts it).

It does not send I-frames or `STOPDT`, and applies no k. The parameter values are the commonly cited clause 5 defaults and
are marked `[VERIFY]` in the code.

**`TcpDelimitedFrameTransport(host, port, startByte)`** serves IEC 104 with `startByte` `0x68`. It scans the buffer for the first
start octet and discards everything before it (resync). A buffer with no start octet is cleared. With the start octet at
offset 0 and at least 2 octets buffered, the frame length is `buffer[1] + 2`; the frame is yielded when that many octets
have arrived.

**`TcpMbapFrameTransport(host, port)`** serves Modbus TCP, whose header has no start octet. While 6 or more octets are
buffered it reads the protocol id (octets 2 and 3) and the length field (octets 4 and 5, big-endian). If the protocol id is
not 0 or the length is outside 2 to 254 (`MinMbapLength`, `MaxMbapLength`: unit id plus the 253-octet maximum PDU), it drops
one octet and retries. Otherwise the frame is `6 + length` octets.

`FrameCorrection.ForProtocol` builds the gate for the protocol. For `iec104` a frame passes when `Length >= 6`, `f[0] == 0x68`
and `f[1] + 2 <= Length`; every other protocol passes any non-empty frame. A failed frame increments `Rejected`, is never decoded,
and does not count as a decode error. The transport already delivers whole frames, so the IEC 104 gate rejects only a frame
whose length octet is below 4: the 4-octet frame `68 02 00 00` fails the length of 6 octets.

## IEC 60870-5-104

`Iec104Protocol` (`iec104`, "IEC 60870-5-104", `RtuTransportKind.Tcp`, port 2404) decodes one APDU per call.

### APCI (`Iec104Frame`)

An APDU is the start octet `0x68`, a length octet, four control octets and an ASDU. The length octet counts the control
octets and the ASDU, so an APDU is `length + 2` octets and `Parse` rejects `length < 4` or `length + 2 > frame.Length` with
`ArgumentException`.

| Format | Control octet 1 | Carries | Decoded to |
|---|---|---|---|
| I | bit 0 = 0 | An ASDU and sequence numbers | `SendSeq = (c1 >> 1) \| (c2 << 7)`, `RecvSeq = (c3 >> 1) \| (c4 << 7)`, the ASDU |
| S | bits 1..0 = `01` | An acknowledgement | `RecvSeq` only |
| U | bits 1..0 = `11` | Link control | `UControl`: `STARTDT act` 0x07, `con` 0x0B; `STOPDT act` 0x13, `con` 0x23; `TESTFR act` 0x43, `con` 0x83 |

`Iec104Protocol.Decode` returns no points for an S or U frame and for an I frame with an empty ASDU. The answers to them
come from the transport's controlling-station logic (above), not from the decoder.

### ASDU (`Iec104Asdu`)

The data unit identifier is 6 octets (the 104 profile: 1-octet type, 1-octet VSQ, 2-octet COT, 2-octet common address):

```
octet 0   TypeId
octet 1   VSQ      bit 7 = SQ, bits 6..0 = N (objects when SQ=0, elements when SQ=1)
octet 2   COT      bits 5..0 = cause, bit 6 = P/N, bit 7 = T (test)
octet 3   originator address
octets 4-5 common address (little-endian)
```

`Parse` throws `ArgumentException` for an ASDU shorter than 6 octets and for a truncated object list. The pipeline counts
that as a decode error. The cause, the P/N bit, the originator and the common address are decoded into the record and are not
used by the pipeline, with one exception: a set T bit marks every point of the ASDU `Invalid` (`Iec104Protocol.Decode`), so a
test ASDU is dropped.

The information objects, with SQ=0 each preceded by a 3-octet little-endian IOA, with SQ=1 sharing one base IOA
(`IOA(i) = base + i`):

| Type | Name | Element | Timed | `RtuPointType` | Decoded value |
|---|---|---|---|---|---|
| 1 | `M_SP_NA_1` | SIQ, 1 octet | no | `BinaryStatus` | `e0 & 1` (0 or 1); quality `e0 & 0xF0` |
| 3 | `M_DP_NA_1` | DIQ, 1 octet | no | `BinaryStatus` | DPI `e0 & 3`: 1 gives 0, 2 gives 1, 0 and 3 give NaN; quality `e0 & 0xF0` |
| 5 | `M_ST_NA_1` | VTI + QDS, 2 octets | no | `StepPosition` | the 7-bit two's complement step in bits 0 to 6 (-64 to +63); quality = QDS |
| 7 | `M_BO_NA_1` | BSI + QDS, 5 octets | no | `Bitstring` | the 32-bit word (least significant octet first) as an unsigned number; quality = QDS |
| 9 | `M_ME_NA_1` | int16 + QDS, 3 octets | no | `Analog` | `int16 / 32768.0` (the raw fraction in [-1, +1)); quality = QDS |
| 11 | `M_ME_NB_1` | int16 + QDS, 3 octets | no | `Analog` | the int16 as a number; quality = QDS |
| 13 | `M_ME_NC_1` | float32 + QDS, 5 octets | no | `Analog` | the IEEE-754 single widened to double; quality = QDS |
| 15 | `M_IT_NA_1` | int32 + BCR octet, 5 octets | no | `Counter` | the int32 as a number; quality = BCR octet `& 0x80` (IV only) |
| 30 | `M_SP_TB_1` | as type 1 + CP56Time2a | yes | `BinaryStatus` | as type 1 |
| 31 | `M_DP_TB_1` | as type 3 + CP56Time2a | yes | `BinaryStatus` | as type 3 |
| 32 | `M_ST_TB_1` | as type 5 + CP56Time2a | yes | `StepPosition` | as type 5 |
| 33 | `M_BO_TB_1` | as type 7 + CP56Time2a | yes | `Bitstring` | as type 7 |
| 34 | `M_ME_TD_1` | as type 9 + CP56Time2a | yes | `Analog` | as type 9 |
| 35 | `M_ME_TE_1` | as type 11 + CP56Time2a | yes | `Analog` | as type 11 |
| 36 | `M_ME_TF_1` | as type 13 + CP56Time2a | yes | `Analog` | as type 13 |
| 37 | `M_IT_TB_1` | as type 15 + CP56Time2a | yes | `Counter` | as type 15 |

Multi-octet fields are little-endian. A double point that is indeterminate (NaN) is skipped by `Iec104Protocol.Decode` and
produces no point. For a counter, only the invalid bit is quality; the carry and adjusted bits of the BCR are not.

A step position (a tap position) and a bitstring are states of equipment, not electrical quantities: no measurement kind
takes them, so the mapper leaves them out of every window, and a crosswalk binding on one is refused as a configuration
fault whatever the point's quality (`InvalidDataException`, the window is discarded and counted in `map-errors`).

Every other type id (the command types such as 100, and the monitor types not in the table) parses as a valid ASDU with an
empty object list. `Iec104Asdu.IsSupportedType` reports which; `Iec104Protocol` increments `UnsupportedTypeIds[typeId]` for each
unsupported ASDU. The count is held by the protocol object and the `interpose` verb prints it in its stop summary
(`unsupported-type-ids`).

`Iec104Protocol.Decode` emits one `RtuPoint` per object: address `IOA:<ioa>` (decimal), type from the table, the value,
`QualityFold.FromIec104(quality)` (with `Invalid` added for a test ASDU), and the object's CP56Time2a stamp.

### CP56Time2a (`TimeDecode.Cp56Time2a`)

Seven octets: milliseconds (u16 little-endian, 0 to 59999), minute in bits 0 to 5 with the invalid bit IV at bit 7, hour in bits 0
to 4 (bit 7 summer time: a tag with it set is an invalid time, as with IV), day of month in bits 0 to 4 (bits 5 to 7 day of week, ignored), month in bits 0 to 3, year in bits
0 to 6 plus 2000. The result is a UTC `DateTime`; the field is read as UTC because the interposer cannot know the station's offset.
It returns `null` when IV is set or a field is out of range (including a day that does not exist in the month), and the point then
counts as untimed. `Dnp3AbsoluteTime` and `IsoUtcTime` exist for DNP3 and IEC 61850 and have no protocol caller.

## Modbus TCP

`ModbusProtocol` (`modbus`, "Modbus TCP", `RtuTransportKind.Tcp`, port 502) is stateful: it holds the outstanding read requests keyed
by the 16-bit MBAP transaction id and, per (unit, function code), the register count of the first unpaired block. One instance
serves one stream; its state is guarded by a lock. GR-329: it implements `IRtuSessionState`, and `interpose` calls
`ResetSession` whenever its client goes away, so a new connection starts with no outstanding requests and no block sizes.

### MBAP and function codes (`ModbusFrame`)

```
octets 0-1  transaction id        octets 2-3  protocol id (must be 0)
octets 4-5  length = unit id + PDU   octet 6     unit id
octet 7     function code
```

Only function codes 3 (read holding registers) and 4 (read input registers) produce points; everything else decodes to no points
without an error (coils, discrete inputs, writes, exception responses where the code has bit 7 set).

- A **request** is recognised by `TryParseReadRequest`: length 6, function code 3 or 4, 12 octets at least. It yields the transaction id,
  unit id, function code, `StartAddress` (u16 big-endian) and `Quantity` (u16). `ModbusProtocol` remembers it and returns no points.
- A **response** has length `3 + byteCount` with an even byte count. `ModbusFrame.Parse` decodes `byteCount / 2` registers, each a
  big-endian u16 with a zero-based `Index`. A response length can never be 6, so a request and a response are never confused.
  `Parse` throws `ArgumentException` for a frame shorter than 8 octets or a non-zero protocol id.

### Addressing and register maps

The address of each register depends on whether the request was seen.

```
paired   (request txn, unit and fc match):   MB:U<unit>:<fc>:@<StartAddress + Index>     e.g. MB:U1:3:@100
unpaired (no matching request):              MB:U<unit>:<fc>:<Index>                    e.g. MB:U1:3:0
```

The register number is the zero-based protocol address as sent on the wire (the `40001` convention is not used). A response pairs
with its request when the pending table holds the transaction id and the unit id and function code agree. The pending entry for the transaction id is removed
whether or not it agrees. A request that is never answered stays in the table, which is bounded by the 65,536 possible transaction ids. GR-801: the
same request sent again under its id is a retry and replaces the first; a DIFFERENT request under an id that is still outstanding (a gateway that
reuses or pipelines ids) marks the id ambiguous, and the next response with that id is refused (`InvalidDataException`, below) and both requests
are forgotten. An exception response (function code with bit 7 set) ends its transaction: the outstanding request is forgotten and no point is made.

Two refusals raise `InvalidDataException`, which the pipeline counts in `DecodeErrors` while the frame contributes no points:

- A paired response whose register count differs from the request's `Quantity`.
- An unpaired response whose register count differs from the first unpaired block seen for the same (unit, function code). Two
  positional blocks of different sizes would collide in the crosswalk. Two blocks of the same size cannot be told apart without their
  requests, so a deployment that polls more than one block per unit and function code must let the interposer see the requests.

Modbus points are `RtuPointType.Analog`, `RtuQuality.Good`, with no device stamp. The raw register is the point value (0 to 65535).
The crosswalk binding gives it meaning with `dataType`, `wordOrder` and `scale`:

| `dataType` | Registers | Value |
|---|---|---|
| `AsDecoded` (default) | 1 | The decoded value unchanged: the unsigned register |
| `U16` | 1 | The register, checked to be an integer 0 to 65535 |
| `S16` | 1 | `(short)register`, two's complement: `0xFF88` gives -120 |
| `U32` | 2 | `(uint)((hi << 16) \| lo)` |
| `S32` | 2 | `(int)((hi << 16) \| lo)` |
| `F32` | 2 | `BitConverter.Int32BitsToSingle((int)((hi << 16) \| lo))`, widened to double |

A 32-bit type reads the bound register and the register numbered one higher, taken from the same frame (`FrameSeq`). With
`wordOrder` `HighWordFirst` (default) the bound register is `hi`; with `LowWordFirst` it is `lo`. A missing companion register, or a value
that is not an integer 0 to 65535, throws `InvalidDataException` from the mapper. A non-default `dataType` on a non-Modbus address is
refused at crosswalk load.

## Quality folding

`QualityFold` turns a protocol quality descriptor into `RtuQuality` flags and then into weight, because `Measurement` has no quality
field and the estimator weight is sigma. The IEC 104 fold is the only one with a protocol caller. The qualities the two selectable protocols produce are:

| Protocol | Source | Bit | `RtuQuality` | Effect |
|---|---|---|---|---|
| IEC 104 | QDS | b7 `0x80` IV | `Invalid` | dropped |
| IEC 104 | QDS | b6 `0x40` NT | `NonTopical` | sigma x 4 |
| IEC 104 | QDS | b5 `0x20` SB | `Substituted` | sigma x 2 |
| IEC 104 | QDS | b4 `0x10` BL | `Blocked` | dropped |
| IEC 104 | QDS | b0 `0x01` OV | `Overflow` | dropped |
| IEC 104 | COT | b7 `0x80` T (test) | `Invalid` | dropped |
| Modbus | none | `Good` | none |

The OV bit exists only in the QDS of the measured-value types (9, 11, 13, 34 to 36); the single-point and double-point
descriptors use the high nibble only, and a counter uses IV only.

```
ShouldDrop(q)       = q has any of  Invalid | Blocked | Overflow | CommLost
SigmaMultiplier(q)  = 1.0
                      * (NonTopicalFactor   if q has NonTopical)     4.0
                      * (SubstitutedFactor  if q has Substituted)    2.0
                      * (RestartFactor      if q has Restart)        4.0
sigma_out           = binding.Sigma * SigmaMultiplier(q)
```

The factors are the `init` properties of `QualityFold` (`NonTopicalFactor`, `SubstitutedFactor`, `RestartFactor`) with the defaults above in
`QualityFold.Default`. Flags multiply: a substituted and stale value has multiplier 8. A dropped point never reaches the crosswalk, so it
cannot raise an error there. `FromDnp3` and `FromGoose` exist for stacks that have no `IRtuProtocol`; no frame reaches them. `FromDnp3` maps a clear
online bit to `Invalid`, b1 `0x02` to `Restart`, b2 `0x04` to `CommLost` and b5 `0x20` to `Overflow`; `FromGoose` maps
not valid, test or needs commissioning to `Invalid`. `Restart` and `CommLost` are therefore produced by no selectable protocol.

## Windowing

`InterposePipeline.Ingest(frame)` validates the frame (`FrameCorrection`), decodes it, and appends the points to the window with the frame's
`FrameSeq`. A decode exception of type `ArgumentException`, `InvalidDataException` or `FormatException` increments `DecodeErrors` and
returns no points. A rejected frame, a control frame and a decode failure all return an empty list.

`FlushWindow(fallbackUtc, utcNow)` returns `null` for an empty window. Otherwise:

```
windowUtc = max(point.StampUtc over all points that carry one)   if any point has a stamp
          = fallbackUtc                                           otherwise
```

Every point in the window contributes its stamp, including points that will be dropped or are not in the crosswalk. The live loop passes
`DateTime.UtcNow` as `fallbackUtc`. `FlushWindow` hands the whole window to `RtuMeasurementSetMapper.Map` and then clears it.

`RtuMeasurementSetMapper.LatestPerAddress` collapses the window to one point per address, in order of first appearance, so a point reported
several times is one measurement and is not weighted N times in the fit:

```
for each address, the winner is
    the point with the later device stamp, when both points have a stamp   (a tie goes to the later arrival)
    otherwise the later arrival
```

A sample dropped for quality that is the latest for its address removes the address from the window altogether: an older good sample does
not stand in for it.

## The crosswalk: `gridsim-rtu-pointmap/1`

`RtuPointMap` is camelCase JSON with enums by name, indented on save and case-insensitive on load (`System.Text.Json`, the same contract as
`MeasurementSet`). `Load` rejects a file whose `schema` is not `gridsim-rtu-pointmap/1` and validates every binding's `dataType`.

Top level:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `schema` | string | `gridsim-rtu-pointmap/1` | Must equal the schema id |
| `case` | string | `""` | The network case the bindings were authored against; the window label when no positional case is given |
| `baseMva` | number | 100.0 | System base (MVA) written to every window's `baseMva`; checked against the model by the estimator |
| `bindings` | array | empty | The address bindings; `Load` refuses a repeated address (`InvalidDataException`). Only a map built in code keeps the last binding of a repeated address |

Each binding (`RtuPointBinding`):

| Field | Type | Default | Meaning |
|---|---|---|---|
| `address` | string | `""` | Protocol-native key: `IOA:<n>`, `MB:U<unit>:<fc>:@<register>` or `MB:U<unit>:<fc>:<index>`; compared ordinally |
| `kind` | `MeasurementKind` | `VmPu` (the enum's first value) | `VmPu`, `PInjMw`, `QInjMvar`, `PFlowMw`, `QFlowMvar`, `VaDeg`, `IMagPu`, `IAngDeg` |
| `bus` | int | 0 | Bus id, for the bus kinds |
| `from`, `to` | int | 0 | Endpoint bus ids, for the branch kinds |
| `circuit` | string | omitted | Branch name or `#k` ordinal that disambiguates parallel branches |
| `end` | `From` or `To` | `From` | The metered terminal of a branch measurement |
| `baseKv` | number | 1.0 | Divides a `VmPu` value that arrives in kV |
| `baseAmps` | number | 1.0 | Divides an `IMagPu` value that arrives in amps |
| `scale` | number | 1.0 | Multiplies the raw value before any base division |
| `sigma` | number | 0.01 | Standard deviation of a good reading, in the units of the written measurement |
| `datumAngleDeg` | number | 0.0 | Subtracted from `VaDeg` and `IAngDeg` values |
| `dataType` | `RtuRegisterType` | `AsDecoded` | Modbus register encoding (see Modbus TCP); a non-default value needs an `MB:` address |
| `wordOrder` | `RtuWordOrder` | `HighWordFirst` | For a 32-bit `dataType`: which register holds the high word |

`TryResolve` is an ordinal dictionary lookup, built on the first lookup. An address with no binding is decoded and skipped. The load does not check `sigma`,
`baseKv`, `baseAmps` or the bus and branch ids; the estimator's `MeasurementSet.Validate` rejects a non-finite value, a `sigma` of zero or less, an
unknown bus and an unresolvable branch.

## Mapping, per-unit conversion and angle datum

`RtuMeasurementSetMapper.Map(points, crosswalk, windowUtc, quality, caseName, utcNow)` runs in this order.

1. **Horizon guard.** `ForwardInferenceGuard.RejectFuture(windowUtc, utcNow, "RTU window")` throws `ForwardInferenceException` when
   `windowUtc > utcNow - 169 h`, before any value is admitted. The live flush calls `Map` only for a window already older than the horizon
   against the real clock; a newer window is held instead (see The horizon guard). A caller that passes `utcNow = DateTime.MaxValue` (a test
   or a replay) makes the check pass and leaves it to the consumer.
2. `LatestPerAddress` collapses the window.
3. For each remaining point: drop it if `ShouldDrop(quality)`; skip it if the address has no binding.
4. Compute the value and the weight:

```
sigma = binding.Sigma * SigmaMultiplier(point.Quality)
raw   = RawValue(point, binding, window)        AsDecoded: point.Value; U16, S16, U32, S32, F32 as in the register table
v     = raw * binding.Scale

VmPu                          value = v / binding.BaseKv
PInjMw, QInjMvar              value = v                    (MW, MVAr: no base)
PFlowMw, QFlowMvar            value = v                    (branch: From, To, Circuit, End)
VaDeg                         value = v - binding.DatumAngleDeg
IMagPu                        value = v / binding.BaseAmps (branch)
IAngDeg                       value = v - binding.DatumAngleDeg   (branch)
```

`scale` is applied before the base division, so a feed in kilovolts under `scale` 0.001 and `baseKv` 132 gives `raw * 0.001 / 132`. `sigma` is
not scaled or divided: it is in the units of the written measurement (per-unit for `VmPu` and `IMagPu`, MW and MVAr for power, degrees for angles). The
angle datum re-references a feed with its own angle reference onto the estimator's slack: the feed's value at the datum bus minus the slack's angle is
`datumAngleDeg`. Each measurement has `source` `"rtu"`.

The returned `MeasurementSet` has `schema` `gridsim-measurements/1`, `case` the label, `baseMva` the crosswalk's, `takenUtc` the window instant, `note`
`"mapped from RTU interposer"` and the measurements in order of first appearance.

## The horizon guard

`ForwardInferenceGuard.HorizonHours` is 169.0 (7 days plus 1 hour). The guard is enforced at two points. At write,
`InterposePipeline.FlushWindow(fallbackUtc, freshPolicy, utcNow)` judges each window against the real horizon (`utcNow`, when a test gives it,
may not be later than the real clock, `ForwardInferenceGuard.RequireNotAfterRealClock`). A window at or older than the horizon maps through the
guarded `Map` and is an estimator input. A newer window is held and counted in `HeldFresh`: under `InterposeFreshPolicy.QuarantineCapture` (the
verb with `--out-store`) it is mapped by `RtuMeasurementSetMapper.MapQuarantineCapture` for the firewalled quarantine store only, its note saying
it is a quarantine capture and never an estimator input; under `InterposeFreshPolicy.Refuse` it is discarded. So a present-time window is never
written to `--measurements-dir`, and such a run exits 2. At consume, `RealtimeEstimatorEngine.Run` (what `gridsim serve` runs)
refuses an undated window and calls `RejectFuture(window.TakenUtc, UtcNow)` for every window, skipping and counting (`rejected-fresh`) any window newer
than `now - 169 h`. A live window with a device stamp from the past is estimable at once; a window of untimed points is stamped with the receive clock
and becomes estimable 169 hours later. `gridsim serve` loads each file with `MeasurementSet.Load(path, now)`, where `now` is the effective clock
(never later than the real one), so the loader's own guard is on too; a window it finds newer than the horizon is skipped, never estimated, and
added to the `rejected-fresh` count. The archive sink is not an estimator input and does not call the guard.

## Sinks

### Measurement-window JSON

`MeasurementSet.Save` writes a `gridsim-measurements/1` document (field reference in
[ch. 10, File and wire formats](<0010 File and wire formats.md>)): `schema`, `case`, `baseMva`, `takenUtc`, `note`, `measurements[]`. The interposer's
files also carry `from`, `to` and `end` on bus measurements (0, 0 and `From`) and a read-only `isBranch` property on every measurement; the loader
ignores `isBranch`. `circuit` is omitted when null.

### The quarantine store (`QuarantineLiveStore`)

`new QuarantineLiveStore(root, sourceId)` resolves `root` with `Path.GetFullPath`, runs the firewall check, and creates `<root>/Parquet/<sourceId>`.
`WriteWindow(set, protocol, cadenceMin)` is a no-op for an empty set and otherwise writes one Parquet file per window:

```
<root>/Parquet/<sourceId>/year=<ISO year>/week=<ISO week, 2 digits>/part-<yyyyMMddTHHmmssfffZ>-<NNNNNN>.parquet
<root>/Parquet/<sourceId>/_QUARANTINE.json
```

The partition key is the ISO 8601 week-year and week of `TakenUtc` (`ISOWeek`), and `NNNNNN` is a per-store counter. The CLI passes `cadenceMin = windowMs / 60000.0`.
One row per measurement (`QuarantineRow`, snake_case column names through `JsonPropertyName`):

| Column | Type | Value |
|---|---|---|
| `timestamp_utc` | timestamp | The window's `TakenUtc` |
| `source` | string | The source id |
| `source_class` | string | Always `telemetry` |
| `cadence_min` | double | `windowMs / 60000` |
| `vm_pu`, `va_deg`, `p_mw`, `q_mvar`, `i_pu`, `i_ang_deg` | double, nullable | Only the column of the measurement's kind is set: `VmPu`, `VaDeg`, `PInjMw` and `PFlowMw` (both to `p_mw`), `QInjMvar` and `QFlowMvar` (both to `q_mvar`), `IMagPu`, `IAngDeg` |
| `bus` | int, nullable | The bus of a bus measurement |
| `from_bus`, `to_bus` | int, nullable | The endpoints of a branch measurement |
| `sigma` | double | The measurement's sigma after quality folding |
| `provenance` | string | `<protocol>:<kind>@<location>`, for example `iec104:VmPu@bus 4` or `iec104:PFlowMw@branch 1->2 (from)` |
| `min_confidence` | double | 0.0 (live telemetry carries no settlement confidence) |
| `year`, `week` | int | The partition values |

The sidecar `_QUARANTINE.json` is written once per store instance, after the first partition: `store`, `source_class` `telemetry`, `evidential` `false`,
`reason`, `never_merge_into` `GDA unified (MERGE-only) tables`, `horizon_hours` 169 and `written_by` `gridsim interpose`.

**The firewall.** `RefuseIfInsideEvidenceLake(fullRoot)` walks from the target directory up to the filesystem root and throws `InvalidOperationException`
(`refusing to write live telemetry into the GDA public lake ...`) if any directory on the way holds a file named `DataSchema.json`, the GDA lake's
master-contract file. The check runs before anything is created. Symbolic links are not resolved. The CLI reports the exception as `error opening quarantine store`
with exit 1.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| IEC 104 port | 2404 | TCP port | Default when no `--listen` or `--port` | `Iec104Protocol.DefaultPort` |
| Modbus port | 502 | TCP port | Default when no `--listen` or `--port` | `ModbusProtocol.DefaultPort` |
| Listen host | `0.0.0.0` | address | Bind address when `--listen` has no host or is absent | `host` in `RunInterpose` |
| Window length | 1000 (minimum 1) | ms | Flush interval | `--window-ms`, `RunInterpose` |
| Read timeout | 200 | ms | `TryReadFrame` timeout per loop pass | `RunInterpose` |
| Read buffer | 8192 | octets | Per socket read | `_rx` in `InterposeTransport.cs` |
| IEC 104 start octet | 0x68 | - | Frame delimiter | `TransportFactory.Open` |
| MBAP length range | 2 to 254 | octets | Valid length field (unit id + PDU) | `MinMbapLength`, `MaxMbapLength` |
| IEC 104 gate | length >= 6, start 0x68, `f[1] + 2 <= length` | - | `FrameCorrection` rule | `FrameCorrection.ForProtocol` |
| Non-topical factor | 4.0 | x sigma | Stale value | `QualityFold.NonTopicalFactor` |
| Substituted factor | 2.0 | x sigma | Operator-substituted value | `QualityFold.SubstitutedFactor` |
| Restart factor | 4.0 | x sigma | Source re-integrating after restart (no selectable protocol produces `Restart`) | `QualityFold.RestartFactor` |
| Dropped qualities | `Invalid`, `Blocked`, `Overflow`, `CommLost` | - | Never enter a window (no selectable protocol produces `CommLost`) | `QualityFold.ShouldDrop` |
| Binding `sigma` | 0.01 | output unit | Standard deviation of a good reading | `RtuPointBinding.Sigma` |
| Binding `scale` | 1.0 | - | Raw to engineering | `RtuPointBinding.Scale` |
| Binding `baseKv`, `baseAmps` | 1.0 | kV, A | Per-unit bases | `RtuPointBinding.BaseKv`, `BaseAmps` |
| Binding `datumAngleDeg` | 0.0 | degrees | Angle re-reference | `RtuPointBinding.DatumAngleDeg` |
| Binding `end` | `From` | - | Metered branch terminal | `RtuPointBinding.End` |
| Binding `dataType`, `wordOrder` | `AsDecoded`, `HighWordFirst` | - | Modbus register encoding | `RtuPointBinding.DataType`, `WordOrder` |
| Crosswalk `baseMva` | 100.0 | MVA | System base written to windows | `RtuPointMap.BaseMva` |
| Horizon | 169.0 | h | Minimum age before estimation (a window exactly 169 h old is accepted) | `ForwardInferenceGuard.HorizonHours` |
| Live flush `utcNow` | the real clock | - | Windows newer than the horizon are held, not written as estimator inputs | `RunInterpose`, `InterposePipeline.FlushWindow` |
| IEC 104 `w`, `t2`, `t3` | 8, 10 s, 20 s | frames, ms | Acknowledge after w I-frames or t2; TESTFR act after t3 idle | `Iec104ControllingStation.W`, `T2Ms`, `T3Ms` |
| Source id | `rtu_live` | - | Quarantine source directory and `source` column | `--source-id`, `RunInterpose` |
| Window `source` tag | `rtu` | - | `Measurement.Source` of every mapped value | `RtuMeasurementSetMapper.Map` |
| Archive `source_class` | `telemetry` | - | Row class | `QuarantineRow.SourceClass` |
| Archive `min_confidence` | 0.0 | - | Row confidence | `QuarantineLiveStore.WriteWindowAsync` |
| Lake marker | `DataSchema.json` | file name | Firewall trigger | `QuarantineLiveStore.EvidenceLakeMarker` |

## Invariants

| Invariant | Pinned by |
|---|---|
| A window newer than the horizon is rejected by the mapper before any value is admitted | `RtuMeasurementSetMapperTests.Present_time_window_is_rejected_by_the_horizon_guard` |
| `Map` with `utcNow = DateTime.MaxValue` (a test or replay override) admits the window | `RtuMeasurementSetMapperTests.Live_path_defers_the_guard_with_maxvalue_now` |
| The live flush holds a window newer than the horizon and never returns it as an estimator input | `Doc3HorizonGuardTests.D018_live_flush_holds_a_fresh_window_and_never_returns_it_as_an_estimator_input` |
| The controlling station sends STARTDT act on connect, answers TESTFR act, acknowledges every eighth I-frame and applies t2 and t3 | `Iec104ControllingStationTests` |
| The consumer skips and counts a window newer than the horizon and refuses an undated window | `RealtimeEstimatorEngineTests.A_window_newer_than_the_horizon_is_skipped_never_estimated`, `RealtimeEstimatorEngineTests.An_undated_window_is_refused` |
| A not-topical point has sigma x 4 and an invalid point is dropped | `RtuMeasurementSetMapperTests.Not_topical_widens_sigma_and_invalid_is_dropped` |
| A crosswalk places bus and branch kinds and skips an unmapped address | `RtuMeasurementSetMapperTests.Maps_points_onto_buses_and_branches_via_the_crosswalk` |
| An angle is re-referenced by subtracting the datum | `RtuMeasurementSetMapperTests.Angle_is_re_referenced_against_the_datum` |
| A point repeated in a window is one measurement at its latest value | `InterposeWindowAndModbusTypeTests.A_point_repeated_in_a_window_becomes_one_measurement_with_its_latest_value` |
| The latest device stamp wins over arrival order; without stamps the later arrival wins and an invalid latest sample drops the point | `InterposeWindowAndModbusTypeTests.The_latest_device_stamp_wins_even_if_it_arrived_first`, `InterposeWindowAndModbusTypeTests.Without_stamps_the_later_arrival_wins_and_an_invalid_latest_sample_drops_the_point` |
| A Modbus request is parsed for its start and quantity | `InterposeWindowAndModbusTypeTests.A_request_is_parsed_for_its_start_and_quantity` |
| A paired response gets absolute addresses, so two blocks never collide | `InterposeWindowAndModbusTypeTests.Paired_responses_get_absolute_register_addresses_so_two_blocks_never_collide` |
| A differently sized unpaired block on one unit and function code is refused | `InterposeWindowAndModbusTypeTests.A_differently_sized_unpaired_block_on_the_same_unit_and_fc_is_refused` |
| A response whose count contradicts its request is refused, and the pipeline counts a refused block as a decode error | `InterposeWindowAndModbusTypeTests.A_response_whose_count_contradicts_its_request_is_refused`, `InterposeWindowAndModbusTypeTests.The_pipeline_counts_a_refused_block_as_a_decode_error` |
| A signed register decodes negative; the default type keeps the unsigned reading | `InterposeWindowAndModbusTypeTests.A_signed_register_decodes_negative`, `InterposeWindowAndModbusTypeTests.The_default_type_keeps_the_unsigned_reading` |
| 32-bit values combine two registers of one response in both word orders | `InterposeWindowAndModbusTypeTests.Thirty_two_bit_values_combine_two_registers_of_the_same_response` |
| A 32-bit binding without its second register fails loud | `InterposeWindowAndModbusTypeTests.A_32_bit_binding_whose_second_register_is_not_in_the_response_fails_loud` |
| A register data type on a non-Modbus address fails at crosswalk load | `InterposeWindowAndModbusTypeTests.A_register_type_on_a_non_modbus_address_fails_at_load` |
| A window that does not fit its binding is counted and discarded and does not poison the next | `InterposeRobustnessTests.A_window_that_does_not_fit_its_binding_is_counted_discarded_and_does_not_poison_the_next` |
| Overlapping 32-bit bindings and a 32-bit binding on register 65535 are refused at load | `InterposeRobustnessTests.Overlapping_32_bit_bindings_are_refused_at_load`, `InterposeRobustnessTests.A_32_bit_binding_on_the_last_register_is_refused_at_load`, `InterposeRobustnessTests.Load_runs_the_register_layout_check` |
| The multipliers are substituted x2, restart x4, stale x4, and they multiply into the mapped sigma | `InterposeRobustnessTests.Default_sigma_multipliers_are_substituted_x2_restart_x4_stale_x4_and_they_multiply`, `InterposeRobustnessTests.The_multipliers_reach_the_mapped_sigma` |
| The DNP3 and GOOSE folds give the documented quality and drop decision | `InterposeRobustnessTests.Dnp3_flags_fold_to_the_documented_quality`, `InterposeRobustnessTests.A_dnp3_restart_point_is_kept_and_down_weighted_but_comm_lost_or_offline_is_dropped`, `InterposeRobustnessTests.Goose_validity_test_and_commissioning_fold_to_good_or_invalid` |
| Window files never overwrite | `InterposeRobustnessTests.A_second_window_with_the_same_name_is_kept_under_a_dup_name`, `InterposeRobustnessTests.NeverOverwrite_opens_the_requested_name_when_it_is_free` |
| Both transports drop a closed or reset client and accept the next; a timeout is not a reset (these run the real socket code, compiled into the tests) | `InterposeRobustnessTests.After_a_client_resets_the_connection_the_next_client_is_accepted`, `InterposeRobustnessTests.After_a_client_closes_cleanly_the_next_client_is_accepted`, `InterposeRobustnessTests.A_read_timeout_is_not_a_reset` |
| The MBAP length bound is 254 (the test checks the source text of the bound; it does not run the transport) | `CliLiteralsAndFlagsContractTests.Modbus_framing_bounds_the_mbap_length_at_254` |
| ASDU information objects decode IOA, value and quality; SQ=1 shares one base IOA; CP56Time2a decodes to UTC | `Iec104AsduTests.Short_float_objects_decode_with_ioa_value_and_quality`, `Iec104AsduTests.Sequence_of_elements_shares_one_base_ioa`, `Iec104AsduTests.Timed_short_float_decodes_cp56time2a_to_utc` |
| A control or unsupported type yields no objects; unsupported ASDUs are counted by type id | `Iec104AsduTests.Control_or_unsupported_type_yields_no_objects`, `Iec104DoublePointTimedTests.Supported_type_ids_are_reported`, `Iec104DoublePointTimedTests.The_protocol_counts_unsupported_asdus_by_type_id` |
| A timed double point decodes with its time tag; off and indeterminate follow the untimed rules | `Iec104DoublePointTimedTests.Timed_double_point_on_decodes_with_its_time_tag`, `Iec104DoublePointTimedTests.Timed_double_point_off_and_indeterminate_follow_the_untimed_rules` |
| APCI I, S and U frames round-trip | `Iec104FrameTests.I_format_round_trips_with_sequence_numbers_and_asdu`, `Iec104FrameTests.S_format_carries_only_the_receive_sequence`, `Iec104FrameTests.U_format_is_unnumbered` |
| Frame to measurement set end to end; a supervisory frame and an empty window yield nothing | `InterposePipelineTests.Frame_to_measurement_set_end_to_end`, `InterposePipelineTests.Supervisory_frame_carries_no_measurement`, `InterposePipelineTests.Empty_window_flushes_to_null` |
| A protocol decodes a frame to an addressed point | `InterposePipelineTests.Protocol_decodes_a_frame_to_an_addressed_point`, `ModbusFrameTests.Protocol_addresses_points_by_unit_function_and_index` |
| A Modbus response decodes each register; a request yields none | `ModbusFrameTests.Read_holding_registers_response_decodes_each_register`, `ModbusFrameTests.A_request_frame_yields_no_registers` |
| The archive is GDA-shaped, tagged `telemetry`, with a non-evidential sidecar | `QuarantineLiveStoreTests.Writes_gda_shaped_partition_with_snake_case_columns_and_sidecar` |
| The archive refuses to open inside the GDA lake | `QuarantineLiveStoreTests.Refuses_to_open_inside_the_gda_public_lake` |

## Worked example

The run is the IEC 104 feed of [ch. 25](<../manual/0025 Interposing live telemetry.md>), captured in `docs/_captures/interpose/`. The client
sent this frame (`iec104-client-log.txt`):

```
  68 37 00 00 00 00 24 03 03 00 01 00 e9 03 00 d3 4d 82 3f 00 d4 30 2d 0d 14 07 1a ea 03 00 08 ac 7c 3f 40 d4 30 2d 0d 14 07 1a eb 03 00 00 00 0a 43 00 d4 30 2d 0d 14 07 1a   # type 36 (timed float): IOA 1001 good, 1002 not topical, 1003 good
```

The octets decode as follows (`Iec104Frame.Parse`, then `Iec104Asdu.Parse`; the right-hand notes are the hand-annotated decode, not program output):

```
68            start octet
37            length 55 = 4 control octets + 51 ASDU octets; the APDU is 57 octets
00 00 00 00   control octets: I format, N(S) = 0, N(R) = 0
24            type 36, M_ME_TF_1 (float32 + QDS + CP56Time2a, 12 octets per element)
03            VSQ: SQ = 0, 3 objects
03 00         COT 3 (spontaneous), T = 0; originator 0
01 00         common address 1
e9 03 00      IOA 0x0003e9 = 1001
d3 4d 82 3f   float32 0x3f824dd3 = 1.0180000066757202
00            QDS: good
d4 30 2d 0d 14 07 1a   CP56Time2a: 0x30d4 = 12500 ms, minute 45, hour 13, day 20, month 7, year 26
                       = 2026-07-20 13:45:12.500 UTC
```

The second object, IOA 1002, carries QDS `0x40` (`NT`), so `FromIec104` gives `NonTopical` and the multiplier is 4. The third, IOA 1003, carries 138.0 kV.
The run's second data frame (`68 19 02 ...`) reports IOA 1001 again at 13:45:13.250 (`c2 33` = 13250 ms) with the float 1.021. The third data frame is type 11 with SQ=1 (`82`): base
IOA 2001 (`d1 07 00`), elements `0x0620` = 1568 with QDS `0x00`, and `0x0190` = 400 with QDS `0x80`, which is IOA 2002 and invalid. The `IOA:3001` frame is type 13
with float -0.31 and no time tag.

`Map` collapses and converts this window with the crosswalk `iec104-case14.json`:

| Address | Winner of the window | Quality | Sigma | Conversion | Written value |
|---|---|---|---|---|---|
| `IOA:1001` | 1.021 (stamp 13:45:13.250, later than 13:45:12.500) | Good | 0.01 | `VmPu`, `baseKv` 1.0 | 1.0210000276565552 |
| `IOA:1002` | 0.987 | `NonTopical` | 0.01 x 4 = 0.04 | `VmPu` | 0.9869999885559082 |
| `IOA:1003` | 138.0 | Good | 0.01 | `138.0 * 1.0 / 132.0` | 1.0454545454545454 |
| `IOA:2001` | 1568 | Good | 2.0 | `PFlowMw`, `1568 * 0.1`, branch 1 to 2 | 156.8 |
| `IOA:2002` | 400 | `Invalid` | - | dropped | - |
| `IOA:3001` | -0.31 | Good | 0.5 | `VaDeg`, `-0.31 - 10.0`, bus 4 | -10.310000002384186 |
| `IOA:4001` | 1 (type 31, ON) | Good | - | no binding, skipped | - |

The window instant is the latest stamp over all 8 points of the window, 13:45:13.250, which names the file `20260720T134513250Z-000000.json`. That instant is 75 days
before the run, so `serve` accepts the window, and on the current build the live flush also finds it older than the horizon and writes it. The
five values in the table are the five measurements of the window file. The capture below is from an earlier build, which printed
`(guard deferred; estimable only once >169 h old)` on the `windows ->` line and a shorter `stopped:` line, and exited 0. The current build prints
`windows -> <dir> (horizon guard on: only windows older than 169 h are written; newer ones are held)`, a `stopped:` line with every field of the
summary above, and, because the run had one rejected and one undecodable frame, `error: interpose: 2 malformed frame(s) (1 rejected, 1
undecodable); pass --tolerate-malformed to accept a noisy line.` and exit 1; with `--tolerate-malformed` it warns and exits 0. The type 5 frame
now decodes to step-position points (counted in `points` and `step-positions`, left out of every window), so `points` also differs from this
capture; the accepted, rejected and decode-error counts and the two windows do not. The capture has not been retaken:

```
$ gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 --crosswalk docs/_captures/interpose/iec104-case14.json --measurements-dir results/interpose-docs/iec104 --out-store results/interpose-docs/iec104-store --window-ms 2000 --ticks 12
interpose IEC 60870-5-104: listening 127.0.0.1:2404, case 'case14', 6 binding(s)
windows -> results/interpose-docs/iec104 (guard deferred; estimable only once >169 h old)
quarantine store -> rtu_live (source_class=telemetry; firewalled from the GDA public lake)
headless: stops after 12 frame(s).
  window 2026-07-20T13:45:13.2500000Z: 5 measurement(s) -> 20260720T134513250Z-000000.json [+quarantine]
  window 2026-07-20T13:45:14.0000000Z: 1 measurement(s) -> 20260720T134514000Z-000001.json [+quarantine]
stopped: accepted 11, rejected 1, decode-errors 1, points 10, windows-written 2
[exit 0]
```

The 12 frames are 11 accepted and 1 rejected (the 4-octet frame `68 02 00 00`); of the 11 accepted, one failed to decode (a type 36 ASDU declaring 2 objects and carrying 1, a
decode error) and the rest decoded. The U and S frames yield no points; on the build the capture was made with, the type 5 frame did not either. The 10 points are the 10 rows of the monitor capture
(`iec104-monitor.txt`), counted before dropping and before the crosswalk. The quarantine rows for the same window (`iec104-store-readback.txt`) carry
`source_class=telemetry`, `sigma=0.04` on the bus 5 row, and `provenance=iec104:VmPu@bus 5`:

```
$ python docs/_captures/interpose/read_parquet.py results/interpose-docs/iec104-store
Parquet/rtu_live/year=2026/week=30/part-20260720T134513250Z-000000.parquet
  timestamp_utc=2026-07-20 13:45:13.250000, source=rtu_live, source_class=telemetry, cadence_min=0.03333333333333333, vm_pu=1.0210000276565552, bus=4, sigma=0.01, provenance=iec104:VmPu@bus 4, min_confidence=0.0, year=2026, week=30
  timestamp_utc=2026-07-20 13:45:13.250000, source=rtu_live, source_class=telemetry, cadence_min=0.03333333333333333, vm_pu=0.9869999885559082, bus=5, sigma=0.04, provenance=iec104:VmPu@bus 5, min_confidence=0.0, year=2026, week=30
...
[exit 0]
```

`cadence_min` is `2000 / 60000`. Timings and stamps differ on every run: the Modbus windows of the capture set carry the receive clock of the machine that produced them.

The Modbus feed shows the register path. The response frame `00 01 00 00 00 0b 01 03 08 04 06 ff 88 42 f6 e9 79` has transaction 1, length 11 = 3 + 8, unit 1, function 3, byte count 8 and four
registers `0x0406` = 1030, `0xFF88`, `0x42F6`, `0xE979`. The request `00 01 00 00 00 06 01 03 00 64 00 04` (start 0x0064 = 100, quantity 4) preceded it, so the points are `MB:U1:3:@100` to `@103`.
With the crosswalk `modbus-case14.json`: `@100` is `1030 * 0.001` = 1.03 pu; `@101` is `S16` `0xFF88` = -120 MW on branch 1 to 2; `@102` is `F32` over `@102` and `@103`, high word first:
`0x42F6E979` = 123.45600128173828 MW injected at bus 3.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Cli/Program.cs` | `RunInterpose`, `KeyPressed` | The verb: flags, endpoint, loop, flush, sinks, audit entries, exit codes |
| `src/GridSim.Cli/InterposeTransport.cs` | `IRtuTransport`, `TcpDelimitedFrameTransport`, `TcpMbapFrameTransport`, `TransportFactory` | Listener, one client, reassembly, resync |
| `src/GridSim.Core/Interpose/IRtuProtocol.cs` | `IRtuProtocol`, `RtuPoint`, `RtuPointType`, `RtuQuality`, `RtuTransportKind` | The protocol abstraction and the point model |
| `src/GridSim.Core/Interpose/RtuProtocolRegistry.cs` | `RtuProtocolRegistry` | Protocol lookup; `--list-protocols` text |
| `src/GridSim.Core/Interpose/FrameCorrection.cs` | `FrameCorrection` | Framing gate and tallies |
| `src/GridSim.Core/Interpose/InterposePipeline.cs` | `InterposePipeline` | Ingest, window, flush, window instant |
| `src/GridSim.Core/Interpose/QualityFold.cs` | `QualityFold` | Quality decode, drop rule, sigma multipliers |
| `src/GridSim.Core/Interpose/TimeDecode.cs` | `TimeDecode` | Time-tag decoders |
| `src/GridSim.Core/Interpose/RtuPointMap.cs` | `RtuPointMap`, `RtuPointBinding`, `RtuRegisterType`, `RtuWordOrder` | Crosswalk schema, load, resolve |
| `src/GridSim.Core/Interpose/RtuMeasurementSetMapper.cs` | `RtuMeasurementSetMapper` | Guard, collapse, convert, emit |
| `src/GridSim.Core/Interpose/Protocols/Iec104Protocol.cs` | `Iec104Protocol` | IEC 104 frame to points; unsupported-type counts |
| `src/GridSim.Core/Interpose/Protocols/ModbusProtocol.cs` | `ModbusProtocol` | Modbus pairing, addressing, refusals |
| `src/GridSim.Core/IO/Telemetry/Iec104Frame.cs` | `Iec104Frame`, `Iec104Apdu`, `Iec104UFunction` | APCI codec |
| `src/GridSim.Core/IO/Telemetry/Iec104Asdu.cs` | `Iec104Asdu`, `Iec104InfoObject` | ASDU object model |
| `src/GridSim.Core/IO/Telemetry/ModbusFrame.cs` | `ModbusFrame`, `ModbusResponse`, `ModbusReadRequest`, `ModbusRegister` | MBAP codec |
| `src/GridSim.Core/Estimation/MeasurementSet.cs` | `MeasurementSet` | The window document and its loader |
| `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs` | `ForwardInferenceGuard` | The 169 h horizon |
| `src/GridSim.Core/Simulation/RealtimeEstimatorEngine.cs` | `RealtimeEstimatorEngine` | The consumer-side guard (`gridsim serve`) |
| `src/GridSim.Gda/Interpose/QuarantineLiveStore.cs` | `QuarantineLiveStore`, `QuarantineRow` | Firewalled Parquet sink |

## Limits

- The verb listens for the outstation to connect. For IEC 104 it then acts as a minimal controlling station (STARTDT act, TESTFR con, S-frame acknowledgements, TESTFR act after t3) applies t1 and the N(S) check by closing the connection (GR-803, GR-823), and sends no commands or `STOPDT`; for Modbus it never writes, so a device that waits for a Modbus request never sends.
- Only `iec104` and `modbus` have an `IRtuProtocol`. The DNP3 and GOOSE codecs, quality folds and time decoders exist and nothing selects them (R089).
- An IEC 104 address is `IOA:<n>` alone. The common address and the originator address are decoded and unused, so two stations with one IOA collide.
- A window with a point that does not fit its binding is discarded whole (counted in `map-errors`), not just that point.
- IEC 104 resync scans to the next `0x68` octet, so a `0x68` inside damaged data starts a frame.
- The transports (one shared `TcpFrameTransportBase`) serve one client at a time and do not time out an idle client. A
  read that ends in an `IOException` is a timeout only when its inner `SocketException` code is `TimedOut` (or
  `WouldBlock`, `TransportErrors.IsReadTimeout`); any other read failure, a reset (RST) included, or a zero-byte read
  drops the client, clears the reassembly buffer and counts a `client-reset`, and the next call accepts the next client.
- CP56Time2a is read as UTC; a tag with the summer-time (SU) bit set is refused as an invalid time (the station must
  stamp UTC), and the day of week is ignored.
- Modbus decodes function codes 3 and 4 only. The Modbus window instant is the receive clock, so it carries no device time.
- A window file name is `TakenUtc` plus a per-run sequence number, so a sequence restarts at 0 per run; a clash is kept
  as `-dupN`, never overwritten.
- `--no-parquet` is accepted and has no effect.

## See also

- [Ch. 25, Interposing live telemetry](<../manual/0025 Interposing live telemetry.md>) - the task chapter with the captured example
- [Ch. 6, State estimation](<0006 State estimation.md>) - the weighted least-squares estimator that consumes the windows
- [Ch. 21, State estimation (manual)](<../manual/0021 State estimation.md>) - running `serve` and reading its result
- [Ch. 10, File and wire formats](<0010 File and wire formats.md>) - `gridsim-measurements/1` and `gridsim-rtu-pointmap/1`
- [Ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the horizon guard
- [Ch. 9, GDA integration](<0009 GDA integration.md>) - the public-data-only boundary the quarantine store keeps
