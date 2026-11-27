### dcpf

`gridsim dcpf <case> [--top 12] [--out <file.csv|file.json>] [--json <file.json>] [--csv <file.csv>]`

DC power flow: bus angles and branch flows from the linearised network, through the same DC model the PTDF, LODF and
DC-OPF use (`DistributionFactors`; Core `DcPowerFlow`). Only series reactance enters; resistance, line charging,
shunts, tap ratios and phase shifts are ignored, voltage magnitudes are 1 pu and there are no losses, so each DC
island's reference bus (its slack, else its largest generator) injects exactly the island's imbalance. Injections are
the in-service generators' output minus the bus demand. Prints a line per island (reference bus, buses, demand,
scheduled generation, what the reference injects), how many rated branches are above 100% of RateA, and the `--top`
largest flows with their loading. Overloads are reported, not an error.

| Flag | Default | Meaning |
|---|---|---|
| `--top` | 12 | Branches to list, largest flow first; 0 lists none, a negative value exits 2. |
| `--out` / `--json` / `--csv` | - | Write the result through the shared [result writer](#result-files). With a `.csv` name: every branch flow to that file (`index,name,from,to,flow_mw,rate_mva,loading_pct`) and every bus angle and net injection to `<stem>-buses.csv` beside it (`bus_id,va_deg,p_inj_mw`), plus `<stem>-islands.csv` and `<stem>-summary.csv`; with a `.json` name (or `--json`), the same tables as one document. Numbers in round-trip form. An `--out` name ending in neither `.csv` nor `.json` exits 2. |

Exit 0 on a solve; 1 when the case does not load or its DC model cannot be formed (an in-service zero-reactance branch
that is the only link between two islands, or a singular B'); 3 when an island carries load but has no generator (the
island line says `NO GENERATION: load cannot be supplied`); 2 for a usage error or a flag another verb owns.
