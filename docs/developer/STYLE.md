# GridSim documentation house style

These rules apply to every file under `docs/` and to the root `README.md`. `tools/docs/lint.py` checks the
mechanical ones; the rest are checked in review.

## 1. Characters and spelling

- Plain ASCII only. No em or en dashes, no smart quotes, no arrows, no maths symbols, no pound sign.
  Write ` - ` or rephrase instead of a dash, `->` for an arrow, `x` for times, `GBP 6,000` for money,
  `sigma`, `omega`, `Delta` for Greek letters outside code.
- EN-GB spelling: normalised, utilisation, initialise, modelled, factorisation, licence (noun), behaviour,
  catalogue, centre, analyse. Code identifiers, flags, file names and quoted output keep their own spelling
  and always sit in backticks (`--catalog`, `ColorVoltage`, `gridsim-event-catalog/1`).

## 2. Voice

- Present tense, assertive. Say what the code does now: "The solver stops when the largest mismatch is below
  `1e-8` pu." Not "The solver should generally stop once...".
- No repair history in the body. No "it used to", "previously", "Before October 2026", "now reproduces",
  "(follow-up)", "since 1.95.011", and no R-numbers or N-numbers. History lives in `CHANGELOG.md` and git.
- A real limitation is stated once, plainly, under `## Limits` at the end of the chapter: "`acopf` solves the
  DC problem and adds a loss loop; it does not solve the full AC equations (R082)." An R-number is allowed only
  there, and only while the limitation is open.
- No stacked qualifiers. Delete "generally", "typically", "in most cases", "it is possible that", "approximately",
  "roughly", "about", "~" unless the sentence is about a real distribution of outcomes, and then give the numbers.
- Provenance tags (`[VERIFY]`, `CuratedPlaceholder`, `INDICATIVE`) are features: describe what each one means
  once, in the chapter that owns it, and do not repeat the warning in every paragraph that mentions the data.

- Placeholder sites (`data/library/placeholders/unmodelled-sites.json`: Nuclear-Sub, Carrier, PCI-Datacentres and the
  rest) are listed exactly as named. Each is described as what it is, a named site whose values are deliberately empty
  because no public dataset covers it. Never soften, rename, drop or explain them away, and never imply that the code
  models them.

## 3. Numbers

- Exact, with units. Defaults, tolerances, limits, timings and counts come from the source or a captured run.
- Give the source for a default in technical chapters: `` `NewtonOptions.Tolerance` (`src/GridSim.Core/PowerFlow/NewtonOptions.cs`) ``.
- Timings carry their machine and case: "case118, 0.41 ms per solve on the reference machine (see ch. 28)".
  Captured output keeps its timings as captured; the text around it says they differ by machine and run. On large
  servers with compute cards the first GPU call is dominated by kernel compilation, not the solve, so a GPU timing
  is quoted with that context.
- No test counts or version numbers in prose. They go stale the next day.

## 4. Commands and output

- Every command shown is one that runs on the current build. Output blocks are pasted exactly from
  `docs/_captures/`, produced by `tools/docs/capture.ps1`. Never type output by hand and never use
  `<placeholders>` inside an output block.
- One invocation style throughout: `gridsim <verb> ...`. Chapter 1 explains that `gridsim` means
  `dotnet run --project src/GridSim.Cli -c Release --` when not installed.
- No machine-specific paths (`W:\...`, `D:\...`). Use repo-relative paths or an environment variable.

## 5. Chapter templates

User manual (task chapters):

1. `# N. Title` (every chapter title carries its number, manual and technical) and one paragraph: what this task is and when you need it.
2. `## Before you start` (only if there are prerequisites).
3. `## Worked example` - a real command and its real output.
4. `## Reading the output` - every column or field explained.
5. `## Variations` - the other common ways to run it, each with a command.
6. `## Pitfalls` - what goes wrong and what the message means.
7. `## Limits`
8. `## See also` - always includes the matching technical chapter and the verb's section in ch. 19.

Flag tables live only in the CLI reference (`0019 CLI reference.md`). Task chapters show examples and link to the
verb's section there instead of repeating its flags.

Technical manual:

1. `# N. Title` and a scope paragraph.
2. `## Model` / `## Algorithm` - the equations as implemented.
3. `## Parameters and defaults` - a table: name, value, unit, meaning, source symbol.
4. `## Invariants` - each with the test that pins it.
5. `## Worked example` - numbers from a real run.
6. `## Source map` - file, type, responsibility.
7. `## Limits`
8. `## See also`

Length follows the subject. A chapter is finished when a competent engineer can use or audit the feature from it
without opening the code, not when it reaches a word count.

## 6. Files and links

- Chapter files are named `NNNN Chapter title.md`: NNNN is the four-digit chapter number and the title is the
  chapter's H1 without its number (a `/` in the title is written ` and `, a `:` is written ` -`). Sorted, they read
  in chapter order. A rename updates every link to the file in the same change; the lint checks the name and the
  links.
- A link target with spaces is written in angle brackets, for example `(<0019 CLI reference.md#estimate>)` after the
  link text.
- Links are relative. Link to a chapter file; heading anchors in another file are allowed only into
  `0019 CLI reference.md` verb sections (`#estimate`), which the lint checks.
- Screenshots come from `docs/screenshots/`, embedded with alt text that says what the picture shows.
