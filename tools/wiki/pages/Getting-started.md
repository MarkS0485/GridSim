# Getting started

This is the short version. The full walk-through, with real output for every step, is
[chapter 1 of the user manual](https://marks0485.github.io/GridSim/manual/0001%20Getting%20started/). If you have not installed GridSim yet, see
[Install](Install).

## What `gridsim` means

Every command is written `gridsim <verb> ...`. With a release archive that is the `gridsim` executable in the
unpacked folder. From a source build it is `dotnet run --project src/GridSim.Cli -c Release --` (the `--` ends the
`dotnet` options; everything after it goes to GridSim).

## Check the build

```
gridsim --version
gridsim --help
```

`gridsim <verb> --help` prints the usage, flags, defaults and exit codes for that verb. Help never needs a licence.

## Your first solve

Solve the WSCC 9-bus benchmark:

```
gridsim case9 --once
```

A healthy run reports `Converged` after 4 Newton iterations with a final mismatch of about 1.65E-014 p.u., total
losses of 4.64 MW, and exits 0. A solve that does not converge exits 3. If your numbers differ, something is wrong
with the build, not the case.

Other built-in cases solve the same way: `case14`, `case30`, `case57`, `case118`, `case300`, `case145`, `case24`,
`cigre` and `uk` (the reduced GB model). Any MATPOWER file works too: `gridsim path/to/case.m --once`.

With no arguments at all, `gridsim` runs the reduced GB model live until you press a key. Give `--once` (solve once
and exit) or `--ticks N` whenever a script runs it, so it terminates.

## Estimate a state

```
gridsim estimate case14 --synth
```

This runs a weighted-least-squares state estimate on synthetic telemetry sampled from a solved truth.

## Where next

- [Loading networks](https://marks0485.github.io/GridSim/manual/0002%20Loading%20networks/)
- [Running power flows](https://marks0485.github.io/GridSim/manual/0005%20Running%20power%20flows/)
- [Contingency analysis](https://marks0485.github.io/GridSim/manual/0011%20Contingency%20analysis/)
- [The desktop app](https://marks0485.github.io/GridSim/manual/0008%20The%20desktop%20app/)
- [CLI quick reference](CLI-quick-reference)
- [Troubleshooting](Troubleshooting)
