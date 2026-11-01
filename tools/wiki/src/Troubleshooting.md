# Troubleshooting

A short summary. The full lookup table, with the exact messages and fixes, is
[chapter 30 of the user manual]({{SITE}}manual/0030%20Troubleshooting/).

## Exit codes first

Every verb ends with one of these codes, so read the code as well as the report
([details]({{SITE}}manual/0030%20Troubleshooting/#exit-codes)).

| Code | Meaning |
|---|---|
| 0 | The run finished and produced its report. A finding is not always a failure, so read the report too. |
| 1 | Input error (missing or unloadable file, invalid data) or a failed check. The message on standard error names it. |
| 2 | Usage error: unknown flag, verb or case, a malformed value, a case over the licence bus limit, or data newer than the 169 hour horizon. |
| 3 | A finding: for example a power flow that did not converge. Do not use the numbers as results. |
| 77 | `crossval` only: the check never ran because Python or pandapower is missing. A skip, not a pass. |

Any other non-zero code, or a message starting `gridsim: internal error`, is a defect in GridSim; keep the crash log
([Crash logs]({{SITE}}manual/0030%20Troubleshooting/#crash-logs)).

## Where to look

The manual chapter groups problems by area:

- [Build and test problems]({{SITE}}manual/0030%20Troubleshooting/#build-and-test-problems)
- [Loading networks]({{SITE}}manual/0030%20Troubleshooting/#loading-networks): unknown case, missing file, bus limit
- [Power flow does not converge]({{SITE}}manual/0030%20Troubleshooting/#power-flow-does-not-converge)
- [Contingency]({{SITE}}manual/0030%20Troubleshooting/#contingency)
- [Dynamics and stability]({{SITE}}manual/0030%20Troubleshooting/#dynamics-and-stability)
- [Electromagnetic transients]({{SITE}}manual/0030%20Troubleshooting/#electromagnetic-transients)
- [Harmonics]({{SITE}}manual/0030%20Troubleshooting/#harmonics)
- [State estimation]({{SITE}}manual/0030%20Troubleshooting/#state-estimation)
- [GDA and the bridge]({{SITE}}manual/0030%20Troubleshooting/#gda-and-the-bridge)
- [Interposer]({{SITE}}manual/0030%20Troubleshooting/#interposer)
- [Web host and desktop app]({{SITE}}manual/0030%20Troubleshooting/#web-host-and-desktop-app)
- [GPU fallback]({{SITE}}manual/0030%20Troubleshooting/#gpu-fallback): `--gpu` falls back to the CPU with one line on standard error and exit 0

## Reporting a problem

Send these, in this order, and the report can be reproduced without a question back:

1. The whole output of `gridsim --version`.
2. The exact command, with every flag.
3. The whole output (standard output and standard error) and the exit code.
4. The crash log, if the run printed `gridsim: internal error`.
5. The input: the case name, or the file or folder.
6. The environment: operating system and any `DOTNET_*` or `GRIDSIM_*` variable that is set (name the variable, never send a key).

Send reports to gdatooling@proton.me. Full list:
[How to report a problem]({{SITE}}manual/0030%20Troubleshooting/#how-to-report-a-problem).
