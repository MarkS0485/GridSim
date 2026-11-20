# GridSim RC2 additions

## Addition 1 - Outbound request headers

GridSim RC2 will gain the following headers for all outbound requests:

```text
X-Data-Pipeline: GridSim/2.x.x
User-Agent: GridSim Identifier.
```

## Addition 2 - Collective sensitivity of open datasets

Primary scope: the GDA module.

Assess the collective sensitivity of open-data datasets, including information that becomes inferable only when datasets are combined. The aim is a mathematically rigorous, auditable and governable basis for informed decisions about what is published and what is withheld, supporting truly informed consent.

Use a solver to evaluate an assessment function C over dataset combinations. C is to be defined for this purpose; this addition does not restrict it to a WLS residual, information matrix or individual sensor contribution.

For a baseline dataset collection D and candidate datasets D_i and D_j:

```text
Delta C_add = C(D union D_i) - C(D)

Delta C_rem = C(D) - C(D setminus D_i)

I_ij = C(D union D_i union D_j)
       - C(D union D_i)
       - C(D union D_j)
       + C(D)
```

The addition and removal terms assess marginal contributions in their respective contexts. The interaction term assesses the additional effect of combining two datasets beyond their separate effects, including synergy or redundancy. The assessment concerns datasets and their combinations, rather than only individual records, measurements or devices.

The intended result is evidence for publication governance: make the collective consequences of releasing open data visible and auditable, so decisions address the information exposed by combinations as well as by each dataset alone. The assessment function, evaluated combinations and decision criteria must be explicit in that evidence.

This records the requested direction; implementation and the definition of C remain outstanding. The supplied 6 November 2026 discussion is the basis for this addition.

## Addition 3 - Probabilistic protection modelling in lockstep

Expand the protection simulation suite with probabilistic protection modelling alongside the deterministic simulator.

Use GridSim's existing fanout: perform the base solve first, then fan out the protection work. Force the deterministic and probabilistic paths to remain in lockstep in simulation time, using the same timestamps rather than independently advancing clocks.

The intended sequence is:

1. Perform the base solve for the simulation instant.
2. Fan out the deterministic and probabilistic protection work from that solved state.
3. Complete the work for that instant before advancing the shared simulation time.

This preserves the deterministic core while allowing probabilistic protection outcomes to be assessed at the same simulation instants. Lockstep concerns simulated time; parallel workers need not complete at the same wall-clock instant. Probabilistic execution must remain reproducible under the simulator's determinism requirements.

Context: the supplied 8 November 2026 discussion raised timestamp alignment between deterministic and probabilistic results. Mark's proposed solution is to use the base-solve/fanout structure to enforce alignment by construction. The other participant's separate-clock assumption and suggested acceptance conditions are context, not additional requirements adopted here.

This records the requested direction. Probability models, sampling, protection actions and result presentation remain to be specified; no implementation has been made.

## Scope decision - 8 November 2026

These three additions are the complete RC2 additions list. RC3 is not starting yet. This records scope only; it does not authorise implementing the additions or change the ongoing read-only review.
