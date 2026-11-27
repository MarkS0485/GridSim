"""MCP prompts: short lesson starters that mirror the gridsim-teach skill, and a study write-up."""

_EVIDENCE = ("Cite the build tag, case hash, result hash and weakest provenance tag from each evidence block. Say "
             "'verified solver, unvalidated case'. Explain each step before the call and the result after it. A refused "
             "verb is not available: do not work around it.")

#: name -> (title, description, text template with {case})
LESSONS = {
    "lesson_per_unit": (
        "Lesson: per-unit",
        "Per-unit quantities: bases, Zbase = kV^2 / MVA, one line by hand and by GridSim.",
        "Teach per-unit on {case}. Read gridsim://docs/per-unit. Load the case, pick one line, convert its impedance "
        "between ohms and per unit by hand (show the arithmetic), then check with line_parameters (per-unit flags) "
        "and case_inspect."),
    "lesson_pv_pq": (
        "Lesson: PV and PQ buses",
        "What a slack, PV and PQ bus each hold, seen in the solve.",
        "Teach PV, PQ and slack buses on {case}. Solve it, run explain_solve, and use the bus types in busSpec to say "
        "what each bus holds fixed and what the solve finds."),
    "lesson_reactive_limits": (
        "Lesson: reactive limits",
        "A generator at its reactive limit switches from PV to PQ.",
        "Teach reactive limits on {case}. Solve, then lower one generator's qmax with case_edit, re-solve and run "
        "explain_solve: find the PV->PQ switch and explain the voltage change. Show the change with diff_results."),
    "lesson_n_minus_1": (
        "Lesson: N-1 security",
        "Run N-1, explain each violation, then break the worst outage.",
        "Teach N-1 security on {case}. Run study nminus1, explain the verdict and each violation, then break_it the "
        "worst outage and read GridSim's message and the Troubleshooting entry."),
    "lesson_fault_level": (
        "Lesson: fault level",
        "Short-circuit current at a bus and what sets it.",
        "Teach fault level on {case}. Run study iec60909 (or fault) at one bus, relate the current to the Thevenin "
        "impedance seen there, then strengthen or weaken a source and show what moved."),
    "lesson_inertia_rocof": (
        "Lesson: inertia and RoCoF",
        "RoCoF after a loss of infeed: dP * f0 / (2 * sum(H * S)).",
        "Teach inertia and RoCoF on {case} (case9 carries machine H and MBase). Compute the initial RoCoF for losing "
        "one unit by hand from the H and MBase values in case_inspect, then run the dynamics verbs that are "
        "allowlisted (stability, inertia-placement) and compare. Read gridsim://docs/dynamic-models for the models."),
}


def register(server):
    for name, (title, desc, text) in LESSONS.items():
        _add(server, name, title, desc, text)

    @server.prompt(name="study_writeup", title="Write up a GridSim study",
                   description="Turn a finished study into a short write-up with a GridSim-rendered report.")
    def study_writeup(case: str = "the case studied") -> str:
        return (f"Write up the study of {case}. Render the main result with report_render, then narrate it: what "
                "was studied, the verdict, the two or three numbers that matter (quoted from the report, not retyped), "
                "and one footer line of limits. " + _EVIDENCE)


def _add(server, name, title, desc, text):
    def fn(case: str = "case9") -> str:
        return text.format(case=case) + " " + _EVIDENCE
    fn.__name__ = name
    fn.__doc__ = desc
    server.prompt(name=name, title=title, description=desc)(fn)
