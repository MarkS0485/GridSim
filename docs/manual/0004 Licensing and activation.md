# 4. Licensing and activation

GridSim is source-available, proprietary software (`LICENSE.md`). Personal and academic use covers network
models of up to 1,500 buses; commercial licences, on application, have no bus limit. The only thing it counts is buses, and it
counts them per network model. This chapter explains what is checked and when, how to install a licence file, how
GridSim finds it, and what each program does when it refuses a network. Everything happens offline: a licence key is
verified with a public key built into GridSim, no program ever contacts a licence server, and activation works on an
air-gapped machine. With no licence installed GridSim does not lock you out. It runs at the free Personal tier.

## Before you start

- You need a licence only for a user network of more than 1,500 buses. Every built-in case and every Bundled Network
  (see below) loads at any size, with no licence.
- A licence arrives as a `.gridsim` file: a text file holding one `GSK1 ...` key line, optionally preceded by `#`
  comment lines. Keep it out of version control and out of screenshots. The key carries the holder's email address and
  organisation in the clear (see [chapter 22](<../technical/0022 Licensing - key format and trust model.md>)).
- The captures in this chapter set `GRIDSIM_LICENCE` for each run so the machine's real licence is neither used nor
  shown. A bash user types `GRIDSIM_LICENCE=none gridsim licence status`; a PowerShell user sets
  `$env:GRIDSIM_LICENCE = "none"` first. `$LOCALAPPDATA` in a captured line stands for the real per-user folder.
  The key used in the examples was signed by a throw-away key pair made for the documentation, so GridSim rejects it
  exactly as it would reject a forgery. Only keys signed by the GridSim issuing key verify, so no accepted key can be
  shown here; the decoded fields of a test key are in the technical chapter.

## Worked example

`gridsim licence status` prints the active licence and where the licence file lives. With `GRIDSIM_LICENCE` set to
the word `none`, which is not a key, the environment source is present but rejected, so the run is at the free tier
and the line says why:

```bash
GRIDSIM_LICENCE=none gridsim licence status
```

```
$ gridsim licence status
Personal * unregistered * 1,500 buses * no expiry  (licence key from GRIDSIM_LICENCE rejected: licence key too short)
licence file: $LOCALAPPDATA\GridSim\licence.gridsim
[exit 0]
```

The program prints a middle dot between the fields; the captures, like every output in this documentation, show it as
`*`. Installing a key that GridSim did not issue fails and installs nothing:

```bash
gridsim licence import docs/_captures/licence/inputs/test-key.gridsim
```

```
$ gridsim licence import docs/_captures/licence/inputs/test-key.gridsim
error: Licence key rejected: licence signature is invalid (key was altered or not issued by GridSim)
[exit 2]
```

A network that is not bundled and has more than 1,500 buses is refused at load. This is the shipped
`case9241pegase.m` copied to a path outside `data/`, so it is a user network, solved at the free tier:

```bash
gridsim results/licence-tmp/mycase9241.m --once
```

```
$ gridsim results/licence-tmp/mycase9241.m --once
error: This network has 9,241 buses; your Personal licence allows 1,500. A larger or commercial licence removes the limit: gdatooling@proton.me
[exit 2]
```

The same network loaded from the shipped `data/matpower/` folder is a Bundled Network and solves at the same tier:

```bash
gridsim pf data/matpower/pegase/case9241pegase.m --top 3
```

```
$ gridsim pf data/matpower/pegase/case9241pegase.m --top 3
Power flow - case9241pegase (9241 buses)
  converged in 14 iteration(s), mismatch 9.94E-011 pu   losses 8010.800 MW / -8029.429 MVAr
  voltage range 0.7888 - 1.1559 pu
  lowest voltages:
      bus 2159   0.7888 pu    -39.448 deg
      bus 7822   0.7888 pu    -39.448 deg
      bus 2635   0.8159 pu    -37.603 deg
note: case9241pegase.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case9241pegase.m: read mpc.gencost (1445 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
[exit 0]
```

Timings and iteration counts in captured output differ by machine and run; the licence messages do not.

## Reading the output

The status line is six fields separated by ` | `: tier, holder, limit, expiry, `for GridSim N.x` and `id` followed by
the licence id. A note in parentheses follows when there is one. The captures in this chapter are from an earlier
build, which separated the fields with a middle dot (shown as `*`); the fields and their order are the same.

| Part | Meaning |
|---|---|
| `tier` | `Personal`, `Academic`, `Commercial Code`, `Commercial Deployment` or `Network Operator`. |
| `holder` | The organisation the key names; the holder's email address when the key names no organisation; `unregistered` for the free default. |
| `limit` | The bus cap that applies, with a thousands separator: the cap written into the key (`5,000 buses`), or `unlimited buses` when the key encodes 0; for a key that is expired, not yet valid or for another major version, `1,500 buses (the free tier applies)`. The CLI, the web host and the case editor format numbers with the invariant culture, so the separator is a comma. The desktop app uses the machine's culture, so on a machine whose culture writes a comma as the decimal mark its log and About tab print `1.500 buses`. |
| `expiry` | `no expiry`, `expires 2027-10-03`, `EXPIRED 2025-12-31`, or `NOT YET VALID (starts 2027-01-01)` for a key dated in the future. The expiry day and the issue day themselves are valid. |
| `for GridSim N.x` | The major version the key was issued for (see "A licence covers one major version"). Absent for the free default. |
| `id` | The key's short licence id, for support. Absent for the free default. |
| `(note)` | Present only when a key was found and rejected, or when the build cannot verify keys at all. It names the source (`GRIDSIM_LICENCE`, `licence.gridsim` or `explicit key`) and the reason. |

An `EXPIRED` line still names the lapsed tier, and its limit field shows the free tier's 1,500 buses, which is what
applies meanwhile, as described under "Expiry". The second line, `licence file:`, is always printed, whether or not the
file exists.

The text after `rejected:` is one of: `licence key is not valid base64`, `licence key too short`,
`not a GridSim licence key`, `unsupported licence version N`, `licence key length mismatch`, `licence signature could
not be checked`, `licence signature is invalid (key was altered or not issued by GridSim)`, `unsupported licence
schema 'X'`, `malformed licence payload`, `unknown licence tier 'X'`, `invalid bus limit in licence`, `invalid issue
date in licence`, `invalid expiry date in licence`, `invalid major version in licence`, or the major-version reason
below. `empty licence key` is the reason the parsing function gives for a blank token, but a blank source is skipped
before it is parsed, so it never appears after `rejected:`. A build that carries no usable public key cannot verify
anything: its note is `no public key embedded in this build; running at the free tier`, with no `rejected:`. The
technical chapter says which check raises each reason.

## Tiers and bus limits

The metric is the bus, one electrical busbar (node) per network model, and the limit applies to each network model on
its own, never cumulatively across models. Nothing else is counted: no generators, lines, studies, scenarios or compute.

| Tier | Terms | Bus limit |
|---|---|---|
| Personal (the default with no key) | Non-commercial | 1,500 |
| Academic | Attribution in publications | 1,500, larger on request |
| Commercial Code | Per major version, on application | None |
| Commercial Deployment | By signed agreement, on application | None |
| Network Operator (TSO, ESO, DNO) | Site-wide, on application | None |

The tiers are those of `LICENSE.md`, which governs. The limit GridSim enforces is the number inside the key: a key for
the Academic tier with a larger allowance carries that allowance, and a key that encodes 0 is unlimited. The free
default is fixed at 1,500. A model is refused when its bus count is greater than the limit, so a model of exactly
1,500 buses loads at the free tier and one of 1,501 does not. Licence terms and commercial arrangements:
`gdatooling@proton.me`.

## Bundled networks and user networks

A Bundled Network never counts toward any limit, at any tier, with or without a key. Everything else is a user network.
Two rules decide which a network is.

**Built-in cases.** `case9` (alias `ieee9`), `case14`, `case30`, `case57`, `case118`, `case300`, `case145`, `case24`
(alias `rts`) and `cigre` are compiled in and never reach the licence check. The case `uk` loads the shipped
`data/uk` folder, so it is bundled by the path rule.

**The shipped-file rule.** A file or folder is bundled when it is in place and unchanged. In place: it sits in one of
these folders of the program's own `data/` folder (the install's, or the source checkout's when the program runs from a
build inside it): `matpower`, `uk`, `gb-synthetic`, `gb-full`, `intl`, `dist3ph`. Unchanged: every file loaded is listed
in the shipped-content manifest `data/bundled.sha256` with a matching SHA-256 (line endings normalised, so a Windows and
a Linux checkout agree). A folder is bundled only when every file in it, at any depth, matches. A file added to a
bundled folder, or a shipped file edited, makes it a user network.

| Path | Bundled |
|---|---|
| `data/matpower/pegase/case9241pegase.m` | yes |
| `data/gb-full` (3,539 buses) | yes |
| `data/intl/france` | yes |
| `data/dist3ph/<feeder>.json` | yes |
| `data/uk` with a file you added to it | no, the added file is not in the manifest |
| `C:/work/data/gb-full`, a copy outside the program's own `data/` folder | no |
| `C:/work/gb-full` | no |
| `results/licence-tmp/mycase9241.m` | no |
| a materialised `gda-*` model or a `gda-out/` tree | no |

A copy of a shipped case in any other folder is a user network, and it counts even though its content is identical.
The web catalogue applies the same shipped-file rule to its entries: every built-in case is bundled; a MATPOWER entry
is bundled when its file passes the rule; and a JSON entry is bundled only when its id is `gb-synthetic`, `gb-reduced` or
`gb-full` and its folder passes the rule, so any other JSON case in its list, such as a `gda-*` model, counts. A path
that cannot be resolved is not bundled.

The bundled lists are in one class in Core, `BundledNetworks`. `DataFolders` and the manifest are the shipped-file
rule, used by the CLI, the desktop app, the case editor and the web catalogue; `JsonCaseIds` is the extra id list the
web catalogue checks for its JSON cases. Each program therefore applies the same rule to the same file or case.

## Where the licence lives and how it is found

The licence file is `licence.gridsim` in a `GridSim` folder under the per-user application-data folder of the
operating system:

| OS | Licence file |
|---|---|
| Windows | `%LOCALAPPDATA%\GridSim\licence.gridsim` (`C:\Users\<you>\AppData\Local\GridSim\licence.gridsim`) |
| Linux | `$XDG_DATA_HOME/GridSim/licence.gridsim`, or `~/.local/share/GridSim/licence.gridsim` when `XDG_DATA_HOME` is unset |
| macOS | `GridSim/licence.gridsim` under the folder .NET returns for `Environment.SpecialFolder.LocalApplicationData` on macOS |

No test covers the macOS row. `gridsim licence path` prints the exact path on the machine in use. The CLI, the desktop app, the web host and the case
editor all read this one file, so one installation serves all four programs.

The active licence is resolved once, when the program starts, from the first of these sources that holds anything:

1. An explicit key. Only the web host has one: the `Licence:Key` configuration value (`appsettings.json`, or the
   environment variable `Licence__Key`).
2. The environment variable `GRIDSIM_LICENCE`, whose value is the key itself (not a path), for a single run with
   nothing installed.
3. The installed `licence.gridsim` file.
4. Otherwise the free Personal tier.

A source that is empty or blank is skipped. A source that holds a key which verifies is used. A source that holds a key
which is rejected stops the search: GridSim runs at the free tier and says why, and does not try the later sources. A
file that cannot be read (missing, locked, no permission) counts as absent, silently.

## Installing a licence

```bash
gridsim licence import acme.gridsim
gridsim --importLicence acme.gridsim
```

The two forms do the same work (`--importLicence=acme.gridsim` also works). GridSim reads the file, takes the first line
that is neither blank nor a `#` comment, verifies it, and only then writes it to the licence file, creating the folder
if needed. On success it prints two lines and exits 0: `Installed:` followed by the status line of the new licence, then
`Saved to:` followed by the licence file path.

On any failure it prints `error: ...` on standard error, writes nothing, and exits 2. The three failures seen in
practice, each captured:

```
$ gridsim --importLicence docs/_captures/licence/inputs/not-a-key.gridsim
error: Licence key rejected: licence key is not valid base64
[exit 2]
```

```
$ gridsim licence import docs/_captures/licence/inputs/absent.gridsim
error: Could not read licence file 'docs/_captures/licence/inputs/absent.gridsim': Could not find file 'docs\_captures\licence\inputs\absent.gridsim'.
[exit 2]
```

The first path is the name as typed. The second is the absolute path that the operating system reports; the capture
shows it relative to the repository.

```
$ gridsim licence import
usage: gridsim licence import <file.gridsim>
[exit 2]
```

`--importLicence` with no value, an empty value or another flag in place of the file name exits 2 with `error:
--importLicence expected a licence file path (<file.gridsim>) but got '...'`, before anything else runs. An unknown
sub-command is a usage error as well:

```
$ gridsim licence renew
unknown licence subcommand 'renew'. Use: status | import | path
[exit 2]
```

An import replaces the installed file, and a key that does not fit this build (another major version) is refused before
it can replace a working one. `gridsim licence path` prints only the file location:

```
$ gridsim licence path
$LOCALAPPDATA\GridSim\licence.gridsim
[exit 0]
```

## Expiry

A key carries an expiry date, which is inclusive: on that day the licence still applies in full. From the next UTC day
the free Personal tier applies, with exactly the rules of an unlicensed install:

- a user network of 1,500 buses or fewer still loads;
- a larger user network is refused, with a message that names the expiry date;
- Bundled Networks load as always.

The lines below come from the licence gate itself, given a test licence that expired on 2025-12-31 (a command-line run
cannot show them, because only keys signed by GridSim verify):

```
...
lapsed status: Commercial Code * Example Ltd * unlimited buses * EXPIRED 2025-12-31 * for GridSim 1.x * id doc002
lapsed, 1500 buses: True
lapsed, 9000 buses: False
Your Commercial Code licence expired on 2025-12-31, so the free Personal tier applies: this network has 9,000 buses and the free tier allows 1,500. Renew or ask about a new term: gdatooling@proton.me
...
```

The status line in this capture is from an earlier build, which printed the key's own limit, `unlimited buses`. The
current build prints the limit that applies: `Commercial Code | Example Ltd | 1,500 buses (the free tier applies) |
EXPIRED 2025-12-31 | for GridSim 1.x | id doc002`. The decisions and the message are unchanged.

A lapsed customer is never worse off than an unlicensed user, and nothing is deleted or locked. Installing a renewed
key replaces the lapsed one. Expiry is judged against the computer's UTC date, so a wrong system clock changes it.

## A licence covers one major version

A licence is issued per major version, the first number of the release (the 1 of 1.x). A key says, inside its signature,
which major version it was issued for, and `licence status` shows it as `for GridSim 1.x`. A key for 1.x works on every
1.x release and is refused by a 2.x build, and a key for a newer major version is refused by an older build in the same
way: strict equality. A refused key is treated like any rejected key: the program runs at the free tier, the status
line ends with the reason, and `licence import` of such a key exits 2 and installs nothing. The reason reads:

```
this key was issued for GridSim 1.x and this build is major version 2 (GridSim 2.x); a licence covers every release of the major version it was issued for (LICENSE.md section 1), so GridSim 2.x needs a 2.x key: gdatooling@proton.me
```

Keys issued before keys carried a major version count as 1.x keys. The key and its decoded fields are the subject of
[chapter 22](<../technical/0022 Licensing - key format and trust model.md>).

## What each program does when it refuses a network

All four programs ask the same question of the same code and receive the same message. They differ in how they show it.

| Program | When it checks | On refusal |
|---|---|---|
| CLI (`gridsim`) | At every user model load, before anything is solved. | `error: <message>` on standard error and exit 2. The whole process ends, including a batch verb working through a list of cases. |
| Desktop app | When a model is selected, after it loads. The title bar names the tier, and the start-up line in the log shows the status line. | The message is written to the log pane and the model is not loaded. The previous model stays. |
| Web host | When a case is loaded for a session or a REST call. The footer badge shows the tier, limit and expiry; its tooltip is the status line. | `/api/solve` and `/api/topology` return HTTP 403 with `{"error": "<message>"}`. The dashboard shows the message on its load-error panel and the WebSocket stream sends an error frame. |
| Case editor (`GridSim.SchemaEditor`) | When a case opens, and again at every save, because Add and Duplicate can raise the bus count. | At open: `Licence refused the case at open (N buses): <message>`, exit 2. At save: the save gate reports `licence: <message>` and the file is not written. |

The refusing message for a bus limit is always `This network has N buses; your <Tier> licence allows L. A larger or
commercial licence removes the limit: <contact>`. In the case editor's `--check` form, which never writes, a user copy of the shipped GB case gives:

```
$ GridSim.SchemaEditor results/licence-tmp/mygb --check
Licence refused the case at open (3539 buses): This network has 3,539 buses; your Personal licence allows 1,500. A larger or commercial licence removes the limit: gdatooling@proton.me
[exit 2]
```

The shipped case itself passes at the same tier:

```
$ GridSim.SchemaEditor data/gb-full --check
CHECK PASSED: the case loads, validates and is within the licence (3539 buses).
  not part of the case (never written): README.md
[exit 0]
```

The case editor is described in [chapter 33](<0033 The case editor.md>) and the desktop app in
[chapter 8](<0008 The desktop app.md>).

## Exit codes

| Code | Meaning in licensing |
|---|---|
| 0 | `licence status`, `licence path` and a successful import, including a status line that reports a rejected key. Help (`gridsim --help`, `gridsim help licence`) needs no licence and exits 0. |
| 2 | A network refused by the bus limit; a rejected or unreadable key at import; a missing import file name; an unknown `licence` sub-command; the case editor refusing a case. |
| 1 | A case file that does not exist or cannot be read, which is an input error and is reported before any licence check. |

## Pitfalls

- **`GRIDSIM_LICENCE` holds the key, not a file name.** Pointing it at a `.gridsim` path gives `licence key is not valid
  base64` or `licence key too short`. The variable also takes no `#` comment lines. Install the file instead, or paste
  the `GSK1 ...` line.
- **A bad variable hides a good installation.** A rejected `GRIDSIM_LICENCE` ends the search, so the installed file is
  not tried and you run at the free tier. `licence status` shows the note. Unset the variable.
- **A copy of a bundled case is a user network.** Moving `case9241pegase.m` out of `data/matpower/` makes it count.
  Keep the shipped files where they are, or install a licence.
- **`licence status` reporting a note is not an error.** It exits 0 so a script can read the line. A script that must
  fail on a rejected key searches the line for the word `rejected`.
- **Always give `--importLicence` a file name.** The token after the switch is taken as the file name, whatever it is,
  even a flag, so `gridsim --importLicence --once` tries to read a file called `--once` and exits 2:

  ```
  $ gridsim --importLicence --once
  error: Could not read licence file '--once': Could not find file '--once'.
  [exit 2]
  ```

  The switch is dropped only when it is the last token, and the rest of the command line then runs as if it were
  absent, so `gridsim --importLicence` alone starts the live view and keeps running until stopped. An empty name
  (`--importLicence=`) is not handled as a licensing error: it ends in an internal-error report and writes a crash log.
- **A rejected key names its source.** `explicit key` is the web `Licence:Key`, `GRIDSIM_LICENCE` the environment, and
  `licence.gridsim` the installed file.
- **A key for another major version is refused, not downgraded.** A 2.x build never falls back to a 1.x key, and the
  reason names both versions.

## Limits

- A key is not bound to a machine or a person and cannot be revoked; the only way to retire a key early is to rotate the
  signing key, which invalidates every issued key.
- Expiry is judged against the system clock, which the user controls.
- The bus count is checked when a model loads. A model edited after load is rechecked only by the case editor at save.
- The desktop app checks only when a model is selected, not while a loaded model runs.
- The macOS licence file location is whatever .NET returns for `LocalApplicationData`; no test run on macOS has confirmed it.

## See also

- [Chapter 22, Licensing: key format and trust model](<../technical/0022 Licensing - key format and trust model.md>): the key bytes,
  the signature, the verification steps, the trust model and a worked decode.
- [The `licence` verb in the CLI reference](<0019 CLI reference.md#licence>).
- [Chapter 2, Loading networks](<0002 Loading networks.md>), for the cases and paths a network can come from.
- [Chapter 33, The case editor](<0033 The case editor.md>) and
  [chapter 8, The desktop app](<0008 The desktop app.md>).
- [Chapter 30, Troubleshooting](<0030 Troubleshooting.md>).
- `LICENSE.md`, the governing terms.
