---
name: Bug report
about: Report something GridSim does wrong
title: ''
labels: bug
assignees: ''
---

**What happened**
A clear, concise description of the bug.

**What you expected**
What should have happened instead. If it's a numerical issue, the reference
value and where it comes from (MATPOWER case, published solution, hand
calculation)  -  that's worth a hundred words.

**Minimal reproduction**
The smallest case or command that triggers it  -  e.g. the CLI invocation and
the JSON case data (or a cut-down version of it).

**Solver / case details (if relevant)**
- Case: IEEE 9-bus / UK 10-zone / GDA replica / custom JSON
- Solver: dense NR / sparse NR
- Convergence behaviour: iterations, max mismatch, divergence pattern

**Environment**
- GridSim version or commit:
- .NET version:
- OS:

**Logs / output**
Solver output, exception messages, or stack traces.
