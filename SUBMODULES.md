# Submodules

GridSim uses git submodules to link optional material that lives in its own repository: reference documents, and in
future data libraries (for example Parquet files for recorded events). This page sets the rules every submodule
follows, lists the ones that exist, and says how to fetch, update and add them.

## The rules

1. **No submodule is ever needed.** A normal clone (no submodules fetched) builds, passes the full test suite in every
   CPU mode, and runs every verb on the bundled cases. Nothing in `src/`, `tests/`, CI or the release workflows reads a
   submodule path. CI and release checkouts do not fetch submodules.
2. **Submodules augment and add; they cannot change the core maths.** The numerical core (power flow, estimation,
   dynamics, faults, harmonics, the deterministic maths in `src/GridSim.Core/Numerics`) is defined only by GridSim's own
   source. A submodule never contains code that GridSim compiles or loads, and never overrides a constant, a default or
   a pinned result. The same case gives the same bits whether or not any submodule is present.
3. **Data from a submodule is ordinary input.** A data library (for example an event dataset in Parquet) is read
   through the same loaders, validation, provenance tags and no-forward-inference horizon guard as any file a user
   points GridSim at. It gets no special trust and no bypass: if the loader would refuse a user file, it refuses the
   submodule file too.
4. **Pinned by commit.** The parent repository records the exact commit of each submodule, so a GridSim version always
   refers to one fixed version of each linked repository. Moving to a newer one is an ordinary commit here (with a
   CHANGELOG line), never automatic.
5. **Public, licensed and kept apart.** A submodule is a separate public repository with its own licence and source
   notes. GridSim's licence does not cover it, and GridSim does not redistribute it in its release artefacts.
6. **Documented here.** Every submodule has a row in the table below and a short README in its parent folder saying what
   it is and how to fetch it.

## Current submodules

| Path | Repository | Kind | What it holds |
|---|---|---|---|
| `docs/Public/GB` | https://github.com/MarkS0485/GridSim-GB-Documents (branch recorded in `.gitmodules`) | Documents | The GB grid documents (NESO, Grid Code, SQSS, FRCR, ETYS, DNO and standards material) from which GridSim's cited values and numbers were taken, with a source catalogue and fetch tools. About 214 MB checked out. |

## Planned kinds

- **Network models (in preparation for RC2).** A separate, free and openly licensed repository of national and
  synchronous-area network models, one folder per grid, each a GridSim case folder (`system.json`, `buses.json`,
  `transmission.json`, `transformer.json`, `generation.json`, `demand.json`, `onloadtapchanger.json`,
  `boundaries.json`) plus:
  - `geography.json` (format `gda-geography/1`): the case's projection, land outline and regions, which the desktop
    map draws in place of the GB coastline (from 2.1.003);
  - `MODEL_CARD.md`, `ATTRIBUTION.txt`, `provenance.json` and `evidence_tags.json`: what each value rests on, every
    source and its licence, and which values are recorded, derived, estimated or synthetic;
  - `BUILD_README.md`: the sources, the licences applied, and how the model was constructed and iterated.

  Every model is a snapshot built from public data, solves and passes `physics-check`, carries `UnvalidatedAtScale`
  in its name, and is general purpose by design, not planning grade. The database is licensed ODbL 1.0, with
  per-source credits in each model. Grids covered: UK, Ireland (all-island), Spain and Portugal, France, Belgium,
  the Netherlands, Germany, Switzerland, Italy (with Sardinia), Austria, Czechia, Poland, Slovakia, Hungary,
  Slovenia, Croatia, Romania, Greece, Bulgaria, the Western Balkans, the Baltic states, Denmark (DK1, DK2), Norway,
  Sweden, Finland, India, the USA (ERCOT), Canada (Quebec, Ontario, Alberta and BC), Australia (NEM, WEM) and Japan
  (east 50 Hz, west 60 Hz).

  Release is subject to licensing checks: models derived from data whose terms need written consent before
  redistribution ship only once that consent is held, or as open-data rebuilds. As with every submodule, GridSim
  needs none of it to build, test or run (rule 1), and the models are ordinary input to the normal loaders (rule 3).
  Bundled, signed models are planned to be exempt from licence-tier bus limits; that is a GridSim change recorded in
  its own release, not something the submodule can grant.
- **Data libraries.** Recorded datasets shipped as Parquet in their own repository, for example the 9 August 2019
  disturbance or the 2026 solar-bashing events. GridSim reads them as replay or evidence inputs through the normal
  loaders (rule 3). A library submodule may add data, never code.

## Fetching and updating

From the repository root:

```
git submodule update --init docs/Public/GB        # fetch one submodule
git submodule update --init --recursive           # fetch every submodule
git clone --recurse-submodules <GridSim url>      # clone with every submodule in one go
git submodule update --remote docs/Public/GB      # move a fetched submodule to the latest commit of its branch
```

`git submodule update --remote` changes only your working copy; recording the newer commit in GridSim is a normal
commit.

## Adding a submodule

1. The content lives in its own public repository with a licence and a README.
2. Add it under the folder that matches its kind (`docs/Public/...` for documents; a data folder for data libraries),
   with `git submodule add -b <branch> <url> <path>`.
3. Add a row to the table above and a README in the parent folder.
4. Check rule 1: build and run the full suite from a clean clone without submodules.
5. If tooling walks the parent folder (for example the docs lint), make it skip the submodule path.
6. Record it in CHANGELOG.md.
