# Understanding the Restrictions

**What GridSim will and will not do with UK public grid data - and why the distinction is the whole point.**

This is a governing document, not a technical note. If you are extending GridSim, adding an ingester, wiring in a new data source, or building anything that touches "real time," read this first. The line drawn here is deliberate, it is enforced in code, and it is not negotiable for convenience.

---

## The one rule

> **We do not run inference *forward* on UK public data.**

You cannot use GridSim to look into the future of the real GB grid, to reconstruct operational state that has not been published, or to run "what-if" scenarios against the live system. That capability - not the data, not the maths - is the thing that can do harm, and GridSim is built so it cannot be that thing.

Everything else in this document exists to explain why that rule is *not* the same as "GridSim cannot solve," and *not* the same as "GridSim cannot do real-time state estimation." Those are different operations. Conflating them is the mistake this document exists to prevent.

---

## Three operations that get confused

There are three things that look superficially similar because they all involve grid physics and a clock. They are not the same, and they do not carry the same risk.

| Operation | Question it answers | About what? | Direction | Status |
|---|---|---|---|---|
| **Forward inference** | "What will the *real* grid do next?" / "What is it doing that hasn't been published?" | **Reality** | Forward | **Forbidden on public data** |
| **Solving** | "What does this set of equations do when you integrate them?" | **A model** | Any | **Allowed** |
| **State estimation** | "Given these measurements, what *is / was* the state right now / then?" | **Reality, observed** | Present / past | **Allowed** |

The single most important distinction is the middle column: **about reality, or about a model?** And for anything about reality, the direction: **does it point forward, or not?**

### 1. Forward inference - a claim about the real grid's future

Forward inference takes published GB data and produces a *claim about the actual system* that runs ahead of what has been observed: what the grid will do, how it will respond, or what its unpublished internal state is right now. This is the capability of *forward visibility beyond real-time* and *inference of system conditions not explicitly published*. It is what turns public data into operational insight - and, in an adversary's hands, into **timing**: knowing when inertia is low and recovery margins are thin is knowing when a disturbance does the most damage.

That is the harm. GridSim does not do this on public data. Full stop.

### 2. Solving - a claim about a model, not the grid

Solving is a physics engine integrating governing equations. You hand it an initial condition and it computes the trajectory of *the model* - swing dynamics, multi-machine, distributed inertia, grid-forming/following inverters. The output is a property of the equations, **not a prediction of the real UK grid.**

This is why solving forward in time is *not* forward inference:

- You may seed a solve from **week-old public data** (the `ForwardInferenceGuard` floor below) and step forward. What you are stepping forward is the *simulation*, not the country. The forward trajectory is what the model says the model does - a statement about mathematics and about GridSim, offered for understanding and validation.
- It never consumes live or near-live public data to project the *actual* grid ahead of the public record. The moment a solve's forward output would become a claim about the real grid's unpublished future, it has stopped being solving and become forbidden forward inference. That transition is the line.

> Solving your way from a week ago into the present is **reconstruction by physics**. Inferring the real grid's next hour from its current published state is **forward inference**. Same equations; categorically different act. One is about the model, the other is a claim about reality's future.

### 3. State estimation - including real-time - is not forward inference

State estimation answers *"given noisy, incomplete measurements and the laws of physics, what is the best estimate of the system state?"* It is a **filter, not a predictor.** It looks at the present (or the past) and resolves what *is* (or *was*), never what *will be*. There is no forward component in the estimation problem itself.

That is why **real-time state estimation is compatible with the rule.** "Real-time" here is a statement about *engineering* - streaming ingest, a resident service, a sub-cycle solve cadence, resilience - not about *seeing the future*. A real-time estimator estimating the present state from present observations is still pointed at *now*, not *next*. It is the leading edge of reconstruction, not a forecast.

The subtlety GridSim has to hold carefully: a genuine grid-operator RTSE estimates the *present* from *present* telemetry, and our data floor (below) forbids present public telemetry. So GridSim's real-time capability is **real-time engineering on the most-recent-permitted data**, and/or real-time-*capable* estimation demonstrated on historical replay - not live operation on the current grid. We say that plainly rather than implying live sight. The capability is real; the pretence of live operational vision is exactly what we refuse.

---

## Why the line is where it is

The core finding behind GridSim is that the grid *cannot lie*: its observable outputs are coupled by physics, so enough aligned public observations, at enough precision, collapse the solution space to a **single consistent system state**. That convergence is genuinely powerful - and that is precisely why it is dangerous when pointed *forward*.

- Pointed at the **past**: it is reconstruction. Evidence. You are recovering what already happened and is already, in principle, knowable. This is GridSim's mission - an evidence engine.
- Pointed at a **model**: it is simulation. A statement about equations. No operational secret is revealed because no claim about the real grid's future is made.
- Pointed at the **real grid's future or unpublished present**: it is operational visibility. It hands an adversary timing and anticipation. This is the capability that does not ship.

The maths does not care which direction you run it. **We do.** The restriction is a choice about *impact*, applied on top of a capability that is otherwise directionally neutral.

---

## How the line is enforced (not just asserted)

The rule is mechanical, not a matter of trust:

- **`ForwardInferenceGuard`** rejects any timestamp newer than `UtcNow - 169h` (7 days + 1 hour), a floor wired into **every dated ingester**. GridSim *can only ever lag.* No estimate, solve or output consumes data newer than the horizon. Two verbs do receive present telemetry, and both hold it back: `interpose` decodes live RTU frames and never writes a window newer than the horizon as an estimator input; with `--out-store` it keeps such a window only as a quarantine capture in the firewalled `QuarantineLiveStore`, which refuses to open inside the GDA public lake. `pmu-ingest --live` refuses a live stream unless `--live-quarantine <dir>` is given, and then writes the fresh frames byte for byte to a quarantine capture tagged non-evidential and non-recorded, which `pmu-ingest` itself refuses as input whatever its age. Quarantined data becomes evidence only by re-sourcing the same instants from a historical archive. The quarantine stores are the one place live data sits on disk (`QuarantineLiveStoreTests`, `Cf6LivePmuQuarantineTests`, `Doc3HorizonGuardTests`).
- **`Dynamics/` and `Estimation/` are separate seams by design.** The dynamic models run as *forward simulation* (about the model). The estimator resolves *state from measurement* (about observed reality). Fusing them into forecasting-aided / predictive estimation of the **real** grid is the boundary case that must be reviewed against this document before it is built - because that is the seam where solving could quietly turn into forward inference.

If you add a data source: it goes through the guard. If you add a "real-time" feature: it operates on permitted-age data or on replay, and it estimates *now/then*, never *next*. If you connect the dynamics to the estimator: stop and check this document first.

---

## The rules, as testable invariants

These are the lines a baseline / regression suite should assert (see also the no-forward-inference invariant tests):

1. **No ingester accepts a timestamp newer than `UtcNow - 169h`.** The guard is universal and cannot be bypassed per-source.
2. **No public-data code path produces an output stamped later than its newest input.** Reconstruction and estimation are backward/present; nothing emits a future-dated claim about the real grid.
3. **Solver forward trajectories are labelled as model output, seeded only from guard-permitted data, and never presented as a forecast of the real grid.**
4. **Real-time features are proven on replay or permitted-age streams** - never on live public telemetry - and estimate the current/historical state, not a future one.
5. **Any fusion of `Dynamics/` into `Estimation/` (forecasting-aided / predictive SE) is gated by explicit review against this document.**

An invariant that isn't tested is a hope. These belong in the suite next to the machine-epsilon solver checks.

---

## Why we hold the line even as the data closes

The UK public data landscape is tightening - much of what made this possible is on its way to being restricted. That does not change anything here. **The restriction is not a response to what is available; it is a statement about what we will build.** We will not ship a capability that can select the moment of maximum harm to critical national infrastructure, regardless of whether the raw data to build it stays open.

GridSim exists to *understand and to prove* - to reconstruct what happened, to validate physics, to serve as evidence. It does not exist to see the grid's future. That is the fine line, and it is walked on purpose.

> The goal is **understanding, not exposure.** Reconstruction and simulation, not anticipation. The distinction is everything.
