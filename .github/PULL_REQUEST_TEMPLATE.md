<!--
Thanks for contributing! Please keep this PR a focused patch.

Fill in the section below and tick the boxes that apply.
-->

## What does this change?

<!-- A sentence or two: what changed and why. Link any related issue, e.g. "Fixes #123". -->

## Checklist

- [ ] This PR does **one thing**  -  unrelated changes are split into separate PRs.
- [ ] The diff is a **focused patch**: only the lines that needed to change, no wholesale file rewrites.
- [ ] **No drive-by reformatting** of untouched code (whitespace, braces, usings).
- [ ] Tests added or updated for any behaviour change  -  physics changes come with a
      reference case (published solution, hand calculation, or invariant check).
- [ ] `dotnet test GridSim.slnx` passes locally (CI runs on release tags and by hand, not on pull requests).
- [ ] No real grid data (GDA / LTDS extracts) is committed  -  the repo carries
      schema mappings and synthetic fixtures only.
