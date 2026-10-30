# 25. Interposing live telemetry

A real grid publishes its live state over industrial protocols: IEC 60870-5-104 and Modbus TCP carry bus voltages,
branch flows and currents. IEC 104 measured values carry a quality descriptor and, in types 30 to 37, a device
timestamp; Modbus registers carry neither. None of them speaks GridSim's `gridsim-measurements/1` measurement model. `gridsim interpose` is the bridge. It listens on a TCP port,
reassembles the protocol frames that arrive, decodes each frame to typed points, maps the points onto your network
model through a crosswalk file you author, and writes the result as measurement windows that `gridsim serve` can
estimate. It can also archive the same telemetry as GDA-shaped Parquet in a quarantined store. You need this chapter
when you have a feed (or a recording of one) and want it in the estimator, and you need it before you wire a real
station to GridSim, because it explains the one rule that cannot be bypassed: live data is never an estimator input,
and a window is written for the estimator only when it is at least 169 hours old. The same chapter covers recorded and
live IEEE C37.118 synchrophasor streams (`pmu-ingest`).

## Before you start

You need three things.

- **A sender.** `interpose` is a listener. It binds a port, accepts one TCP client at a time and reads what that
  client sends. It never connects out and never polls a device. For IEC 104 it acts as the controlling station once
  the outstation has connected: it sends `STARTDT act`, answers a `TESTFR act` with `TESTFR con`, acknowledges the
  outstation's frames with S-frames (after 8 frames or 10 s) and sends `TESTFR act` after 20 s of silence, so it does
  write to the network. For Modbus it never writes a byte. The sender is whatever connects to the port: an outstation
  set to connect to its master, a protocol forwarder, a tap relay, a replay tool, or the test client used in this
  chapter. For Modbus the sender must forward the poll requests as well as the responses to get absolute register
  addresses (see [Modbus TCP](#modbus-tcp)).
- **A crosswalk.** Protocol addresses (an IEC 104 information object address, a Modbus register) carry no bus
  meaning, so a `gridsim-rtu-pointmap/1` file tells GridSim which address is which measurement. The worked example
  below shows one.
- **A case label.** The optional first argument (`case14` below) is a label written into each window. `interpose`
  does not load a network. `serve` loads the network later, and its case must be the one the bus and branch ids in
  the crosswalk refer to. With no argument the label is the crosswalk's `case`, else `rtu`.

`gridsim interpose --list-protocols` lists the stacks that `--protocol` accepts:

```
$ gridsim interpose --list-protocols
Registered RTU/SCADA protocols:
  name       transport port   description
  iec104     Tcp       2404   IEC 60870-5-104
  modbus     Tcp       502    Modbus TCP
  Not selectable: DNP3 and IEC 61850 GOOSE exist only as link-framing scaffolding (no application layer).
[exit 0]
```

The worked example drives `interpose` from a short Python client, `docs/_captures/interpose/rtu_client.py`. It
connects to the listener on the loopback interface and sends hand-built valid frames. Run it from a second terminal,
or let a script start both. Every command here is bound to `127.0.0.1`, so nothing leaves the machine.

## Worked example

The feed is an IEC 104 station reporting six points of the IEEE 14-bus case. The crosswalk,
`docs/_captures/interpose/iec104-case14.json`, binds each point:

```json
{
  "schema": "gridsim-rtu-pointmap/1",
  "case": "case14",
  "baseMva": 100.0,
  "bindings": [
    { "address": "IOA:1001", "kind": "VmPu",    "bus": 4,               "sigma": 0.01 },
    { "address": "IOA:1002", "kind": "VmPu",    "bus": 5,               "sigma": 0.01 },
    { "address": "IOA:1003", "kind": "VmPu",    "bus": 9, "baseKv": 132.0, "sigma": 0.01 },
    { "address": "IOA:2001", "kind": "PFlowMw", "from": 1, "to": 2, "scale": 0.1, "sigma": 2.0 },
    { "address": "IOA:2002", "kind": "PFlowMw", "from": 1, "to": 5, "scale": 0.1, "sigma": 2.0 },
    { "address": "IOA:3001", "kind": "VaDeg",   "bus": 4, "datumAngleDeg": 10.0, "sigma": 0.5 }
  ]
}
```

An address is the protocol-native key the decoder emits: `IOA:<n>` for IEC 104. `kind` is the measurement; bus kinds
take `bus`, branch kinds take `from` and `to`. `sigma` is the standard deviation of a good reading, in the units of
the written measurement, and it is the weight the estimator gives the value. `scale` multiplies the raw value
(the scaled-integer feed `1568` times `0.1` is 156.8 MW), `baseKv` divides a kV voltage into per-unit, and
`datumAngleDeg` is subtracted from an angle so a feed with its own angle reference lands on the estimator's slack.

### Step 1: watch the feed

Monitor mode decodes to the console and writes nothing. It is how you discover the addresses a feed produces before
you author the crosswalk, and how you check that the feed decodes at all.

```bash
gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 \
    --crosswalk docs/_captures/interpose/iec104-case14.json --monitor --ticks 12
python docs/_captures/interpose/rtu_client.py iec104 --port 2404
```

The first command waits for the client; the second sends 12 frames and one burst of 3 octets of line noise. `--ticks 12` stops the listener after 12 frames. The listener prints:

```
$ gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 --crosswalk docs/_captures/interpose/iec104-case14.json --monitor --ticks 12
interpose IEC 60870-5-104: listening 127.0.0.1:2404, case 'case14', 6 binding(s)
monitor mode: decoding to console only, no windows written.
headless: stops after 12 frame(s).
  IOA:1001                  1.018  Good         2026-07-20T13:45:12.5000000Z
  IOA:1002                  0.987  NonTopical   2026-07-20T13:45:12.5000000Z
  IOA:1003                    138  Good         2026-07-20T13:45:12.5000000Z
  IOA:1001                  1.021  Good         2026-07-20T13:45:13.2500000Z
  IOA:2001                   1568  Good         -
  IOA:2002                    400  Invalid      -
  IOA:3001                  -0.31  Good         -
  IOA:4001                      1  Good         2026-07-20T13:45:12.5000000Z
  IOA:1001                  1.019  Good         2026-07-20T13:45:14.0000000Z
  IOA:3001                  -0.35  Invalid      -
stopped: accepted 11, rejected 1, decode-errors 1, points 10, windows-written 0
[exit 0]
```

### Step 2: write measurement windows

Drop `--monitor` and name a directory. Add `--out-store` to archive the telemetry as well (see
[The quarantine archive](#the-quarantine-archive)). `--window-ms 2000` closes a window every 2 seconds, counted from the moment the listener starts. The client
pauses 2.6 seconds between its two bursts of frames. The number of windows and the split of points between them depend on
when the client connects, because the flush clock does not wait for it. The captured run, with a client that connected
promptly, produced two windows; a client that connects late can produce a different split.

```bash
gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 \
    --crosswalk docs/_captures/interpose/iec104-case14.json \
    --measurements-dir results/interpose-docs/iec104 --out-store results/interpose-docs/iec104-store \
    --window-ms 2000 --ticks 12
python docs/_captures/interpose/rtu_client.py iec104 --port 2404
```

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

This capture is from an earlier build. On the current build:

- the `windows ->` line reads `(horizon guard on: only windows older than 169 h are written; newer ones are held)`;
  these windows are stamped 2026-07-20, older than the horizon, so they are still written;
- the `stopped:` line carries every counter: `accepted`, `rejected`, `decode-errors`, `map-errors`, `points`,
  `step-positions`, `bitstrings`, `windows-written`, `duplicate-names`, `client-resets`, `unsupported-type-ids`,
  `held-fresh`, `quarantined` and `discarded-bytes` (the type 5 frame now decodes to a step-position point, so
  `points` is higher than shown);
- a malformed frame fails the run: this one had a rejected and an undecodable frame, so it ends with
  `error: interpose: 2 malformed frame(s) (1 rejected, 1 undecodable); pass --tolerate-malformed to accept a noisy line.`
  and exit 1. Add `--tolerate-malformed` to accept a noisy line: the count is then a warning and the exit stays 0.

The capture has not been retaken. Add `--live-seconds S` to bound a run by the wall clock as well as by `--ticks`.

The first window file, `results/interpose-docs/iec104/20260720T134513250Z-000000.json`, holds five measurements:

```json
{
  "schema": "gridsim-measurements/1",
  "case": "case14",
  "baseMva": 100,
  "takenUtc": "2026-07-20T13:45:13.25Z",
  "note": "mapped from RTU interposer",
  "measurements": [
    {
      "kind": "VmPu",
      "value": 1.0210000276565552,
      "sigma": 0.01,
      "bus": 4,
      "from": 0,
      "to": 0,
      "end": "From",
      "source": "rtu",
      "isBranch": false
    },
    {
      "kind": "VmPu",
      "value": 0.9869999885559082,
      "sigma": 0.04,
      "bus": 5,
...
    {
      "kind": "VaDeg",
      "value": -10.310000002384186,
      "sigma": 0.5,
      "bus": 4,
      "from": 0,
      "to": 0,
      "end": "From",
      "source": "rtu",
      "isBranch": false
    }
  ]
}
```

Read it against the monitor listing:

- Bus 4 voltage is `1.0210000276565552`, the later of the two reports of `IOA:1001` in the window (1.018, then 1.021).
  A point reported several times in one window counts once, at its current value. The listing shows 1.019 for the
  same address; that report arrived in the second window.
- Bus 5 voltage carries `sigma` 0.04, four times the crosswalk's 0.01, because the feed flagged the value `NonTopical`
  (stale).
- Bus 9 voltage is 138 kV divided by `baseKv` 132, which is 1.0454545454545454 pu.
- The flow on branch 1 to 2 is the scaled integer 1568 times `scale` 0.1, 156.8 MW. The second point of that frame,
  `IOA:2002`, was flagged `Invalid`, and an invalid point never enters a window.
- The angle is the feed's -0.31 minus `datumAngleDeg` 10, which is -10.31 degrees.
- `IOA:4001` (a breaker position from a type 31 ASDU) decodes and is not in the crosswalk, so it is skipped.
- The window time, `takenUtc`, is the latest device timestamp in the window: the `13:45:13.250` stamp of the second
  report of `IOA:1001`.

Short-float values are single precision, so a feed value of 1.021 arrives as `1.0210000276565552`. That is the
precision the feed sent, not a conversion error.

The second window holds one measurement, bus 4 at 1.019 pu. The truncated frame, the 4-octet frame whose
length octet is 2 and the test-flagged angle in the second burst contributed nothing; the next section explains each.

### Step 3: estimate

`gridsim serve` runs the estimator over a directory of windows. The five-measurement window above is far too thin to make
the 14-bus network observable, so the estimator reports it unobservable:

```
$ gridsim serve case14 --measurements-dir results/interpose-docs/iec104 --method wls
serving wls estimator over 2 window(s) from results/interpose-docs/iec104
horizon guard: now=2026-10-03T12:58:13.5087441Z, newest processable=2026-09-26T11:58:13.5087441Z
  #0 2026-07-20T13:45:13.2500000Z Unobservable J=6596.92 chi2=not-run
  #1 2026-07-20T13:45:14.0000000Z Unobservable J=3.61 chi2=not-run
done: processed 2, converged 0, rejected-fresh 0, failed 0
error: no window produced a usable estimate: none of the 2 estimated window(s) converged (see the status on each line).
[exit 3]
```

Both windows reached the estimator, because their device timestamps (20 July) are more than 169 hours before the
`now` that `serve` prints. They did not converge, because 5 and 1 measurements cannot determine 27 unknowns. A run in
which no window converges exits 3 (validation failure) with the reason on stderr; a run with at least one converged
window exits 0, and each line shows the status of its window.

A feed that does observe the network converges. The second crosswalk,
`docs/_captures/interpose/iec104-case14-full.json`, binds all 14 voltage magnitudes (`IOA:1001` to `IOA:1014`) and all
14 angles (`IOA:1101` to `IOA:1114`); `gen_case14_full_crosswalk.py` writes it. Its first bindings are:

```json
{
  "schema": "gridsim-rtu-pointmap/1",
  "case": "case14",
  "baseMva": 100.0,
  "bindings": [
    {"address": "IOA:1001", "kind": "VmPu", "bus": 1, "sigma": 0.01},
    {"address": "IOA:1002", "kind": "VmPu", "bus": 2, "sigma": 0.01},
...
```

The client's `iec104-full` mode sends the solved case14 state as two type 36 ASDUs in sequence-of-elements form: one
frame carries 14 consecutive information objects from a single base address.

```bash
gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 \
    --crosswalk docs/_captures/interpose/iec104-case14-full.json \
    --measurements-dir results/interpose-docs/iec104-full --window-ms 5000 --ticks 2
python docs/_captures/interpose/rtu_client.py iec104-full --port 2404
gridsim serve case14 --measurements-dir results/interpose-docs/iec104-full --method wls
```

```
$ gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 --crosswalk docs/_captures/interpose/iec104-case14-full.json --measurements-dir results/interpose-docs/iec104-full --window-ms 5000 --ticks 2
interpose IEC 60870-5-104: listening 127.0.0.1:2404, case 'case14', 28 binding(s)
windows -> results/interpose-docs/iec104-full (guard deferred; estimable only once >169 h old)
headless: stops after 2 frame(s).
  window 2026-07-20T13:45:12.5000000Z: 28 measurement(s) -> 20260720T134512500Z-000000.json
stopped: accepted 2, rejected 0, decode-errors 0, points 28, windows-written 1
[exit 0]
```

This capture is from the same earlier build as the first: the current build prints the `horizon guard on` banner and
the full `stopped:` line described there. The run had no malformed frame, so it still exits 0.

```
$ gridsim serve case14 --measurements-dir results/interpose-docs/iec104-full --method wls
serving wls estimator over 1 window(s) from results/interpose-docs/iec104-full
horizon guard: now=2026-10-03T12:58:13.9315822Z, newest processable=2026-09-26T11:58:13.9315822Z
  #0 2026-07-20T13:45:12.5000000Z OK J=0.00 chi2=PASS
done: processed 1, converged 1, rejected-fresh 0, failed 0
[exit 0]
```

The estimator converged and the chi-square test passed. `J=0.00` is the weighted sum of squared residuals: the feed
is the rounded power-flow solution of the same case, so the fit leaves almost no residual. A real feed reports
measurement noise, and `J` and `chi2` then say how well the telemetry agrees with the model (see
[ch. 21, State estimation](<0021 State estimation.md>)).

Timings and stamps in these captures (the `now=` lines, the receive-clock window times in the Modbus runs below) differ
on every machine and every run.

## Reading the output

The console lines, in the order they appear:

| Line | Meaning |
|---|---|
| `interpose <protocol>: listening <host>:<port>, case '<label>', N binding(s)` | The port is bound and the crosswalk loaded with N bindings. |
| `monitor mode: ...` | `--monitor` is on: points print, nothing is written. |
| `windows -> <dir> (horizon guard on: only windows older than 169 h are written; newer ones are held)` | Windows older than the horizon are written to `<dir>`; newer ones are held (see [The horizon rule](#the-horizon-rule)). |
| `quarantine store -> <source> (...)` | The Parquet archive is open for the source id `<source>`. |
| `no --measurements-dir/--out-store: decoding only ...` | Neither sink and no `--monitor`: nothing is saved. Window lines still print (without a file name) and `windows-written` still counts each window. |
| `headless: stops after N frame(s).` or `press any key to stop.` | The stop condition. |
| `window <time>: N measurement(s) -> <file>` | One window was saved with N measurements. `[+quarantine]` marks a window also archived. |
| `window discarded (crosswalk mismatch): <reason>` | A window whose points did not fit their bindings was dropped whole (stderr); the listener keeps running. See [Pitfalls](#pitfalls). |
| `window <time>: N measurement(s) held (newer than the horizon) [quarantine capture only]` | A window newer than the horizon, kept only in the `--out-store` archive. |
| `warning: window <time> is newer than the 169 h horizon; ...` | The first held window, named once on stderr; later ones are counted in `held-fresh`. |
| `warning: window file '<name>' already exists; kept this window as '<name>-dupN.json' ...` | A window file of the same name was already in the folder. Window files are never overwritten (stderr). |
| `stopped: accepted A, rejected R, decode-errors D, map-errors M, points P, step-positions S, bitstrings B, windows-written W, duplicate-names N, client-resets C, unsupported-type-ids U, held-fresh H, quarantined Q` | The run summary. |
| `REJECTED (forward inference): N window(s) newer than the 169 h horizon were not written to --measurements-dir.` | A run with `--measurements-dir` that held any window ends with this line on stderr and exit 2 (a map error's exit 3 takes precedence). |

The captures in this chapter show the summary without the counters from `map-errors` onwards.

The summary counters:

- `accepted` counts frames that passed the framing gate, including control frames (U and S format) that carry no
  data. `rejected` counts frames that failed it. For IEC 104 the gate is: at least 6 octets, first octet `0x68`, and
  the length octet fits the frame. For Modbus every non-empty frame passes.
- `decode-errors` counts frames that passed framing and then failed to decode: a truncated ASDU, a bad APDU length, or a
  Modbus response the decoder refuses (see [Pitfalls](#pitfalls)). Those frames contribute no points. The listener
  keeps running.
- `points` counts decoded points from all frames, including points that are dropped for quality or absent from the
  crosswalk. An indeterminate IEC 104 double point (state 0 or 3) is skipped before the count and is in no counter. The
  ten points of the example are the ten rows of the monitor listing. `step-positions` and `bitstrings` count the IEC
  104 step-position (types 5 and 32) and 32-bit bitstring (types 7 and 33) points among them: they are decoded and
  shown in the monitor, but neither is an estimator input: a crosswalk binding on one discards its window as a map
  error, naming the address.
- `windows-written` counts windows that hold at least one measurement, whether or not a sink saves them. A window whose
  points are all unmapped or dropped is not emitted and does not count.
- `map-errors` counts windows discarded because a point did not fit its binding. Any value above 0 makes the verb exit 3
  when it stops.
- `duplicate-names` counts windows saved under a `-dupN` name because the planned file name was taken.
- `client-resets` counts connections that ended (a clean close, a reset or any other read failure). After each one the
  listener accepts the next client.
- `unsupported-type-ids` lists the IEC 104 ASDU types received that the decoder does not handle (command types, for
  example), as `type x count`, or `none`. Modbus always shows `none`.
- `held-fresh` counts windows newer than the horizon, which are never written to `--measurements-dir`, and
  `quarantined` those of them kept in the `--out-store` archive.

A monitor row has four columns: the point address (`IOA:1001`), the decoded value after the protocol's own scaling
and before the crosswalk (printed to six significant digits, so 123456789 prints as `1.23457E+08`), the folded quality, and the device timestamp in UTC or `-` when the protocol carried none.
The quality of a point is one or more of the names below; several can appear together. Modbus produces only `Good`. IEC 104
produces the other five, and `Invalid` also marks every point of a test ASDU. The `CommLost` and `Restart` qualities exist in the
enum, but no selectable protocol produces them.

What each quality does to a point:

| Quality | Produced by | Effect |
|---|---|---|
| `Good` | IEC 104 and Modbus | No change. |
| `Invalid` | IEC 104 (IV bit, or a test ASDU) | The point is dropped. It never enters a window. |
| `Blocked` | IEC 104 (BL bit) | The point is dropped. |
| `Overflow` | IEC 104 (OV bit, measured-value types only) | The point is dropped. |
| `NonTopical` | IEC 104 (NT bit) | `sigma` is multiplied by 4. |
| `Substituted` | IEC 104 (SB bit) | `sigma` is multiplied by 2. |

Several flags multiply: a value that is both substituted and stale carries `sigma` times 8. A larger sigma lowers the
weight of the value in the estimate. The weight is the crosswalk `sigma` times the quality multiplier, so at run time
quality is the only thing that changes it.

The window file name is the window time and a six-digit sequence number: `<yyyyMMddTHHmmssfffZ>-<NNNNNN>.json`. Sorted
by name, the files are in time order, which is the order `serve` consumes them. The file is a
`gridsim-measurements/1` document (field reference in
[technical ch. 10](<../technical/0010 File and wire formats.md>)). Each measurement has the `kind`, the `value`
in engineering units, the `sigma`, the `bus` or the `from`, `to` and `end` of a branch, and `source` `rtu`. The
`isBranch` field is informational; `serve` ignores it on load. The window time `takenUtc` is the latest device
timestamp in the window, or the receive clock when no point carried one.

A window file never replaces an existing one. Two windows can share a name: a listener restarted into the same folder
starts its sequence at 0 again, and a replayed feed repeats its device times. The second is saved as
`<name>-dup1.json` (then `-dup2`, and so on), the console prints a warning, and `duplicate-names` counts it. The
quarantine archive below follows the same rule for its Parquet parts.

## Variations

### Modbus TCP

A Modbus register has no native units, quality or timestamp, so the crosswalk carries the type and scale. This one,
`docs/_captures/interpose/modbus-case14.json`, binds a voltage stored in thousandths, a signed flow and a
two-register float:

```json
{
  "schema": "gridsim-rtu-pointmap/1",
  "case": "case14",
  "baseMva": 100.0,
  "bindings": [
    { "address": "MB:U1:3:@100", "kind": "VmPu",    "bus": 5, "scale": 0.001, "sigma": 0.01 },
    { "address": "MB:U1:3:@101", "kind": "PFlowMw", "from": 1, "to": 2, "dataType": "S16", "sigma": 2.0 },
    { "address": "MB:U1:3:@102", "kind": "PInjMw",  "bus": 3, "dataType": "F32", "wordOrder": "HighWordFirst", "sigma": 1.0 }
  ]
}
```

The address `MB:U1:3:@100` is unit 1, function code 3 (read holding registers) and register 100. The `@` form means the
interposer saw the poll request, which names the start register, and paired the response with it by transaction id.
The `dataType` says how to read the raw register: `S16` is a signed 16-bit value (the register `0xFF88` is -120, not
65,416), and `F32` combines this register with the next into an IEEE single (`wordOrder` says which word is high).
Monitor mode shows the raw registers before any type or scale:

```
$ gridsim interpose case14 --protocol modbus --listen 127.0.0.1:5020 --crosswalk docs/_captures/interpose/modbus-case14.json --monitor --ticks 6
interpose Modbus TCP: listening 127.0.0.1:5020, case 'case14', 3 binding(s)
monitor mode: decoding to console only, no windows written.
headless: stops after 6 frame(s).
  MB:U1:3:@100               1030  Good         -
  MB:U1:3:@101              65416  Good         -
  MB:U1:3:@102              17142  Good         -
  MB:U1:3:@103              59769  Good         -
  MB:U1:3:@100               1031  Good         -
  MB:U1:3:@101              65436  Good         -
  MB:U1:3:@102              17154  Good         -
  MB:U1:3:@103              32768  Good         -
  MB:U2:3:0                    11  Good         -
  MB:U2:3:1                    22  Good         -
stopped: accepted 6, rejected 0, decode-errors 1, points 10, windows-written 0
[exit 0]
```

The last two rows are a response with no request: it carries no start register, so its points are addressed by position,
`MB:U2:3:0` and `MB:U2:3:1`. The client then sent a 3-register response for the same unit and function code, and the
decoder refused it (the `decode-errors 1`), because two blocks of different sizes addressed by position would collide in
the crosswalk.

The register 1030 is 1.03 pu once the binding's scale of 0.001 is applied, `65416` (`0xFF88`) is -120 MW as a signed
16-bit value, and the register pair `17142 59769` (`0x42F6 0xE979`) is the single-precision float 123.456. Register 103,
the second half of the float, has no binding of its own and is skipped.

Modbus carries no device time, so a window's time is the receive clock of the interposer: the present. Writing windows
from this feed therefore writes none. With `--measurements-dir` every window is newer than the horizon, so it is held:

```bash
gridsim interpose case14 --protocol modbus --listen 127.0.0.1:5020 --crosswalk docs/_captures/interpose/modbus-case14.json --measurements-dir results/interpose-docs/modbus --window-ms 2000 --ticks 6
```

The first held window is named on standard error (`warning: window <time> is newer than the 169 h horizon; it is not
written to --measurements-dir (live telemetry is never an estimator input). ...`), the stop line counts `held-fresh 2`,
and the run ends with `REJECTED (forward inference): 2 window(s) newer than the 169 h horizon were not written to
--measurements-dir.` and exit 2. With `--out-store` as well, each held window is kept in the quarantine archive only,
printed as `window <time>: N measurement(s) held (newer than the horizon) [quarantine capture only]`, and counted in
`quarantined`. See [The horizon rule](#the-horizon-rule).

### The quarantine archive

`--out-store <dir>` also writes every window as GDA-shaped Parquet (`<dir>/Parquet/<source>/year=YYYY/week=WW/`),
where `<source>` is `--source-id` (default `rtu_live`). The step 2 run above wrote one file per window. Reading it
back, one row per measurement (columns that are empty for a row are left out of the listing):

```
$ python docs/_captures/interpose/read_parquet.py results/interpose-docs/iec104-store
Parquet/rtu_live/year=2026/week=30/part-20260720T134513250Z-000000.parquet
  timestamp_utc=2026-07-20 13:45:13.250000, source=rtu_live, source_class=telemetry, cadence_min=0.03333333333333333, vm_pu=1.0210000276565552, bus=4, sigma=0.01, provenance=iec104:VmPu@bus 4, min_confidence=0.0, year=2026, week=30
...
Parquet/rtu_live/year=2026/week=30/part-20260720T134514000Z-000001.parquet
  timestamp_utc=2026-07-20 13:45:14, source=rtu_live, source_class=telemetry, cadence_min=0.03333333333333333, vm_pu=1.0190000534057617, bus=4, sigma=0.01, provenance=iec104:VmPu@bus 4, min_confidence=0.0, year=2026, week=30
[exit 0]
```

The store is deliberately not part of GDA's evidence lake. GDA is built from public data only and excludes live SCADA
by rule, so the store tags every row `source_class = telemetry`, writes a `_QUARANTINE.json` marker beside the
partitions with `evidential` set to `false`, and refuses to open at or below any directory that holds the lake's
`DataSchema.json`. A directory standing in for the lake shows the refusal:

```
$ gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 --crosswalk docs/_captures/interpose/iec104-case14.json --out-store results/interpose-docs/fake-lake/telemetry --ticks 1
error opening quarantine store 'results/interpose-docs/fake-lake/telemetry': refusing to write live telemetry into the GDA public lake (found DataSchema.json at 'results\interpose-docs\fake-lake'). The interposer's quarantine store must live OUTSIDE the evidence estate.
[exit 1]
```

Never merge this store into GDA's unified tables. `--monitor` writes nothing, so `--out-store` or `--measurements-dir`
with `--monitor` is refused with exit 2. The `--no-parquet` flag is accepted and does
nothing; omit `--out-store` to skip the archive.

### Other ways to run it

- **Another endpoint.** `--listen host:port` binds one address; a bare port (`--listen 2404`) or `--port 2404` binds all
  interfaces. With neither flag the listener binds all interfaces on the protocol's default port, 2404 for `iec104` and
  502 for `modbus`. Binding all interfaces opens the port to the network: use `127.0.0.1` for a local feed.
- **A different window.** `--window-ms N` sets the window length in milliseconds (default 1000, minimum 1).
- **Unattended.** Without `--ticks`, the listener runs until a key is pressed, and a process with redirected input never
  receives one. Scripts pass `--ticks N` (a frame cap) and stop when the sender is done.
- **Audited.** `--audit-log <file>` records the run in the tamper-evident audit log: a `started` entry when the listener
  opens, with the protocol, the endpoint and the crosswalk's SHA-256, and a closing entry whose outcome carries the exit
  code and the summary counters when it ends. Frame contents are never logged. The switch is described in
  [ch. 19, CLI reference](<0019 CLI reference.md#global-switches>).

The flags of the verb are in [ch. 19, CLI reference](<0019 CLI reference.md#interpose>).

### The horizon rule

GridSim is a historical estimator: it never processes data newer than the current time minus 169 hours (7 days and 1
hour). Live telemetry is newer by definition, so it is never an estimator input. The rule is enforced twice.

- `interpose` judges every window against the real clock when it closes. A window older than the horizon (a replayed,
  recorded feed) is written to `--measurements-dir`. A window newer than the horizon is held: never written there, kept
  only as a quarantine capture when `--out-store` is given, and counted in `held-fresh`; a run with
  `--measurements-dir` that held any window ends `REJECTED (forward inference)` with exit 2. `interpose` takes no
  `--utc-now`.
- `serve` re-checks every window against the real clock and skips any window newer than the horizon. That is the
  `rejected-fresh` count. Its `--utc-now` may only move the horizon back, never past the real clock.

Nothing you interpose can nowcast. To prove the chain, feed `interpose` timestamped data from the past, as the IEC 104
examples above do (device time 20 July).

A window's time is the latest device timestamp among its points, whether or not those points are mapped or dropped, or
the receive clock when no point carried one. A device clock that runs ahead gives a window dated in the future, which
is held like any other fresh window.

### Synchrophasor streams: `pmu-ingest`

`gridsim pmu-ingest` reads IEEE C37.118.2 synchrophasor data: a recorded capture file (the frames as sent, a CONFIG-1
or CONFIG-2 frame followed by DATA frames), or, with `--live`, a stream from a PMU or phasor data concentrator. It
checks every frame's CRC, decodes each DATA frame with the configuration in force when it arrived, drops samples by
their STAT and time-quality bits, aligns the usable samples on their reporting instants, and prints the frame counts,
one line per PMU and a `quality:` line. The verb is EXPERIMENTAL.

```bash
gridsim pmu-ingest docs/_captures/c37118/two-pmu.bin
```

With `--channels <map.json> --measurements-dir <dir>` it maps each PMU phasor to a bus voltage or a branch current
(`gridsim-pmu-channels/1`) and writes one `gridsim-measurements/1` set per aligned instant, angles re-referenced to the
map's reference channel. Those sets are the input of `pmu-estimate`, the linear PMU state estimate
([ch. 21](<0021 State estimation.md>)).

The horizon applies to synchrophasors as to every input: a capture with any frame newer than 169 hours is refused whole
with `REJECTED (forward inference)` and exit 2. `--live tcp://host:port` connects to the device and asks it to stream;
`--live udp://[bind]:port` listens for its datagrams; `--live-frames N` stops after N DATA frames and `--live-seconds`
caps the time. A live device streams the present, so without more it is refused the same way. `--live-quarantine <dir>`
holds every frame newer than the horizon back from the pipeline and writes the received frames byte for byte to
`<dir>/pmu-live-<received UTC>.quarantine.c37`, with a `gridsim-pmu-quarantine/1` sidecar tagged `CuratedPlaceholder`,
`evidential` false and `recorded` false; frames older than the horizon (a concentrator replaying history) go through the
recorded pipeline as usual. The quarantine folder is refused inside the GDA public lake, and `pmu-ingest` refuses a
quarantine capture as input whatever its age: it becomes recorded data only by re-sourcing the same instants from an
archive. Every flag and exit code is in [ch. 19, pmu-ingest](<0019 CLI reference.md#pmu-ingest>), and the decoder in
[technical ch. 33](<../technical/0033 C37.118 synchrophasor ingest.md>).

## Pitfalls

- **Nothing arrives and the listener sits idle.** `interpose` listens; it does not connect to a device. A station that
  waits for its master to connect to it never sends: configure the outstation to connect to the interposer, or point a
  forwarder or relay at the port. Once an IEC 104 outstation is connected the interposer sends `STARTDT act` itself.
  With `--live-seconds` a run that received nothing says so on standard error.
- **A window newer than 169 hours is held, not written.** Live telemetry is never an estimator input. A window
  newer than the horizon is not written to `--measurements-dir`; with `--out-store` it is kept as a quarantine capture.
  A run that held windows while `--measurements-dir` was given ends with `REJECTED (forward inference): N window(s)
  newer than the 169 h horizon were not written to --measurements-dir ...` and exits 2.
- **A crosswalk binding on a tap position or a status word discards the window.** IEC 104 types 5 and 32 (step
  position) and 7 and 33 (bitstring) decode to points that no measurement kind takes; a binding on one is refused, the
  window is discarded, and the run exits 3.
- **`error opening ... transport on ...`** The port is in use or the host is not bindable. Pick another `--listen`.
- **`error loading crosswalk '...'` and exit 1.** The file is missing, is not valid JSON, declares a schema other than
  `gridsim-rtu-pointmap/1`, or has a `dataType` on an address that is not a Modbus register:

  ```
  $ gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 --crosswalk docs/_captures/interpose/bad-datatype.json --ticks 1
  error loading crosswalk 'docs/_captures/interpose/bad-datatype.json': Binding 'IOA:1001': dataType S16 applies only to a Modbus register address (MB:U<unit>:<fc>:<index> or MB:U<unit>:<fc>:@<register>).
  [exit 1]
  ```

- **`error: --crosswalk <map.json> is required.`** Every run needs a crosswalk (exit 2). An unknown `--protocol` prints
  the usage and `error: unknown protocol '<name>'. Try --list-protocols.` (exit 2).
- **`windows-written 0` with points decoded.** No decoded address matched a binding, or every matched point was dropped for
  quality. Compare the monitor listing with the `address` strings in the crosswalk, character for character.
- **A Modbus address that never matches.** Without the poll requests an address is the position in the response
  (`MB:U1:3:0`), and with them it is the register itself (`MB:U1:3:@100`). The register number is the zero-based
  protocol address as sent on the wire, not the `40001` convention of vendor manuals. Run `--monitor` and copy the
  address it prints.
- **A decode error on Modbus.** A response with no request, whose register count differs from an earlier unpaired block of
  the same unit and function code, is refused. A response whose count contradicts its request is refused. Let the
  interposer see the requests to get absolute addresses.
- **A negative flow reads as a large positive number.** A Modbus register is unsigned unless the binding says
  `"dataType": "S16"` (or `S32`). A flow of -120 MW reads as 65,416 MW (65,536 minus 120), as the monitor listing above
  shows. Set the type for anything that can go negative.
- **`sigma` is in the written unit, not the feed unit.** A kV voltage divided by `baseKv` becomes per-unit, and `sigma` is
  not divided with it. For a `VmPu` binding give `sigma` in per-unit. An unreasonable `sigma` does not fail at load:
  the estimator rejects a non-finite value or a `sigma` of zero or less when it validates the window.
- **A 32-bit binding that cannot find its second register discards the window.** An `F32`, `U32` or `S32` binding
  needs the next register in the same response. If it is missing, or a register value is not an integer 0 to 65535,
  that window fails to map: it is discarded whole, the reason is printed, `map-errors` counts it, and the listener
  keeps running. A later window that does hold both registers maps normally. The verb exits 3 when it stops if any
  window was discarded. In
  this run the first client polled registers 102 and 103 only, then reset the connection; a second client polled 103
  and 104:

  ```
  $ gridsim interpose case14 --protocol modbus --listen 127.0.0.1:15022 --crosswalk docs/_captures/interpose/modbus-f32-missing-register.json --measurements-dir <dir> --ticks 4
  interpose Modbus TCP: listening 127.0.0.1:15022, case 'case14', 1 binding(s)
  windows -> <dir> (guard deferred; estimable only once >169 h old)
  headless: stops after 4 frame(s).
    window discarded (crosswalk mismatch): Binding 'MB:U1:3:@103' is F32 (two registers) but register 'MB:U1:3:@104' is not in the same response. Poll a block that includes both registers.
    window 2026-10-03T14:36:22.6504228Z: 1 measurement(s) -> 20261003T143622650Z-000000.json
  stopped: accepted 4, rejected 0, decode-errors 0, map-errors 1, points 4, windows-written 1, duplicate-names 0, client-resets 1, unsupported-type-ids none
  [exit 3]
  ```

  Two layout faults are caught when the crosswalk loads instead (exit 1): a 32-bit binding on register 65535, which
  has no next register, and a 32-bit binding whose second register is also bound on its own.
- **`serve` shows `rejected-fresh` for every window and exits 3.** The windows are present-time. This is the horizon
  rule: wait, or feed device-timestamped history.
- **`serve` reports `Unobservable` and exits 3.** The window has too few measurements for the network. A run in which no
  window converges exits 3; when some do, it exits 0 and the per-window lines show which did not. A window needs enough
  telemetry to observe every bus (see [ch. 21](<0021 State estimation.md>)).
- **A device clock in local time.** The interposer reads every device timestamp as UTC and cannot know the station's
  offset. Run the station clock in UTC.
- **An IEC 104 test frame yields nothing.** A frame whose cause-of-transmission test bit is set is marked invalid on every
  point, so it is dropped. The last frame of the example shows it: `IOA:3001` reads `Invalid`.
- **Stray octets between frames are discarded.** The transport drops bytes ahead of the next frame (IEC 104: up to the
  next `0x68`; Modbus: one octet at a time until a valid header), so line noise that does not imitate a header costs no
  frame and shows in no counter. The `iec104` and `modbus` runs of the example send 3 such octets each.

## Limits

- `interpose` is a listener only: it never connects to a device, never polls, and never writes to the peer, so it cannot
  answer `STARTDT act`, `TESTFR act` or a Modbus request.
- Only `iec104` and `modbus` can be selected with `--protocol`. DNP3 and IEC 61850 GOOSE have framing codecs and quality
  folds in the code and no application decoder (R089).
- It serves one client at a time. A second connection waits until the first disconnects. A client that closes or
  resets its connection is dropped (counted in `client-resets`) and the next client is accepted; a half-received frame
  from the old client is discarded, never joined to the new one.
- An IEC 104 address is the information object address alone. The common address of the ASDU is decoded and ignored, so two
  stations with the same IOA collide in the crosswalk.
- IEC 104 coverage is the monitor types 1, 3, 5, 7, 9, 11, 13, 15 and 30 to 37. Step positions (5, 32) and bitstrings
  (7, 33) decode to points that never enter a window (a binding on one is a map error); command types decode to no points, and the stop summary lists
  how many of each it received (`unsupported-type-ids`).
- Untimed telemetry (every Modbus feed) is dated by the receive clock, so it is always newer than the horizon and is
  never written for the estimator.
- Modbus coverage is function codes 3 and 4. Coils, discrete inputs, writes and exception responses yield no points.
- `--no-parquet` is accepted and has no effect.

## See also

- [Technical ch. 20, The RTU/SCADA interposer](<../technical/0020 The RTU and SCADA interposer.md>) - the pipeline, the protocol decoders, every crosswalk field and the invariants
- [Ch. 21, State estimation](<0021 State estimation.md>) - what `serve` does with a window
- [Technical ch. 6, State estimation](<../technical/0006 State estimation.md>) - the estimator the windows feed
- [Technical ch. 10, File and wire formats](<../technical/0010 File and wire formats.md>) - `gridsim-measurements/1` and `gridsim-rtu-pointmap/1`
- [Ch. 24, The GDA bridge](<0024 The GDA bridge.md>) - the public-data side of the same boundary
- [Ch. 19, CLI reference](<0019 CLI reference.md#interpose>) - every `interpose` flag and exit code
- [Ch. 19, CLI reference](<0019 CLI reference.md#serve>) - the `serve` verb
