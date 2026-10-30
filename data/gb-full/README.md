# gb-full: the full GB transmission model (3,539 buses, 5,918 branches)

A standard GridSim case directory (`system`, `buses`, `generation`, `demand`, `transmission`, `transformer`,
`onloadtapchanger`, `boundaries` and the asset-dynamics files). It was assembled from published data (the NESO transmission spine,
the DNO LTDS tables and the NESO registers) plus synthesised elements, and is committed here as a pinned reference model: every gb-full number in
the tests and the docs is computed from these files.

```bash
gridsim pf gb-full                # or: gridsim pf --dir data/gb-full
```

| Item | Count |
|---|---:|
| Buses | 3,539 |
| Lines (`transmission.json`) | 3,298 |
| Transformers (`transformer.json`) | 2,620 (1,207 of them synthesised supergrid transformers, `SGT ...`) |
| Demand rows | 1,958 |
| Generation rows | 5,318 |
| On-load tap changers | 1,383 |

This README records what is known to be wrong or unverified in the DATA. None of it is an engine defect, and none of it
is fixed here: the fix belongs to the upstream model export, and a corrected export would move gb-full numbers
everywhere, so it needs its own before / after. Because of the synthesised elements, gb-full is not a public-export
model (see docs/assurance/gb-full-licence-and-provenance-audit.md).

## Supergrid transformer names (N169, N190; renamed October 2026)

The upstream export wrote 58 branch names twice or more (133 rows), all of them supergrid transformers. Names are how OLTCs,
boundaries and protections address branches, and the SchemaEditor's v2 save rule requires them to be unique, so the
bundled data now carries unique names (Ruling R1, N190). **Names only:** the same 2,620 transformer rows in the same
order, every other field identical (compared as literal JSON text, so every number is bit-identical); no OLTC or
boundary referred to any renamed row. 86 rows were renamed:

- **Class A, 47 pairs (94 rows).** Two rows identical in every field: same ends, impedance and rating, named
  `SGT <spine key> <licence>:<label> x<n>/<k>@<bus id>`. The second row of each pair is now `<name>#2` (the suffix the
  export already uses for a parallel unit). Cause: the export writes one coupling row per NESO GSP entry, and two GSP entries
  that share the spine site and the DNO region label produce the same name and the same fan-out to the same DNO buses.
- **Class B, 39 rows (11 former names).** `SGT default <licence>:<gsp> x<n>` from the leftover coupling pass, each row
  to a DIFFERENT bus: distinct transformers whose name omitted the bus fed. Every such row is now
  `SGT default <licence>:<gsp> x<n>@<to bus id>`, the form the export's main coupling pass already uses.

### Possible double-counted SGT capacity at 7 GSP groups (UNVERIFIED)

Each class-A row is sized by the export to carry the WHOLE GSP group's demand, so each pair gives its group twice the SGT
capacity and half the reactance of one bank. Whether that is physical (two GSPs, each with its own SGTs) or a double
count (one GSP listed twice) needs the NESO GSP table, which is not on disk. **[VERIFY: NESO GSP GIS table.]** The groups:

| Spine key | Site (bus fed from) | `#2` rows | Rating per row (MVA) | Added by the `#2` rows (MVA) |
|---|---|---:|---:|---:|
| ACTL_2 | Willesden (bus 4, 275 kV) | 8 | 60 | 480 |
| ALST_3 | Uskmouth (bus 6, 400 kV) | 8 | 60 | 480 |
| BARKC1 | Barking (bus 16, 275 kV) | 3 | 160 | 480 |
| HACK_1 | Hackney (bus 136, 275 kV) | 5 | 144 | 720 |
| TYNE_1 | Tynemouth (bus 290, 275 kV) | 8 | 60 | 480 |
| UPPB_1 | Upper Boat (bus 291, 400 kV) | 8 | 60 | 480 |
| WIMBN1 | Wimbledon (bus 307, 275 kV) | 7 | 102.857... | 720 |
| | | **47** | | **3,840** |

If they are double counts, removing the `#2` rows (or halving each pair) moves every gb-full number; that is a data
decision for when the GSP table is available, not done here.

**For the upstream export:** emit unique names at source, for example `SGT <spine key> <licence>:<label> [<gsp key>]
x<n>/<k>@<bus id>` for class A (the NESO GSP key tells the two entries apart) and the `@<bus id>` suffix for class B,
so a regenerated gb-full does not bring the duplicates back.

## Open line ends at 66 kV and above (N147)

142 buses at 66 kV and above (97 at 132 kV, 45 at 66 kV) have exactly one in-service branch and no load, generator or
shunt: open line ends. 46 of them are named `<site> T<n>` (for example 4992 SILSDEN T2, 4843 HOLMFIELD T2, 4595
Newburn Haugh T2 132kV). They are circuit terminals that the export did not join to the site's grid-transformer buses;
the site's demand is fed instead by synthesised `SGT island ...` / `SGT ... x<n>/<k>` transformers straight from the
supergrid. In the real network the circuit feeds the grid transformer, whose load damps it. In this data the open ends
form undamped LC pockets: the N006 harmonic resonance at bus 4992 (driving-point |Z| 157.445 pu at h 4.9818) is one of
them, and the engine computes exactly what the data says. A data limitation of the assembled model; the fix belongs to
the upstream export.

## Supergrid transformers with zero resistance (N148)

All 1,207 synthesised supergrid transformers have r = 0 (22 other transformers also do). The commonest reactance
families are 0.3333333333333333 pu (440 rows), 0.2222222222222222 pu (248 rows, plus one non-SGT transformer with the
same x and r > 0), 0.0416665 pu (154 rows: 99 `SGT island` and 55 `SGT default` banks) and 0.16666666666666666 pu (138
rows). With r = 0 these branches add no resistive damping in a harmonic study, and the fan-out naming `x<n>/<k>` means
each of the k DNO nodes sits behind one share of a single SGT's reactance, not a transformer of its own. A data
limitation of the assembled model; the fix (resistance from the X/R of real SGTs, and the fan-out) belongs to the
upstream export. No gb-full number is changed here.
