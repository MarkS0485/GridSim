"""Signatures and dedup keys of case-lab findings.

A finding is one triage class seen for one mode and one rival tool, with a signature that says what kind of
divergence it was, independent of the case's numbers: the documented difference's id, the gate reason with its
numbers removed, or which element-diff quantities exceeded the triage thresholds. Two cases that show the same
(class, mode, tool, signature) are one finding with two occurrences.
"""

import hashlib
import json
import re

FINDING_SCHEMA = "gridsim-mcp-caselab-finding/1"

#: Classes that are findings. 'agreement' and 'not compared' are counted, not recorded as findings.
FINDING_CLASSES = ("GridSim gate fail", "rival gate fail", "documented model difference", "genuine disagreement")

_NUMBER = re.compile(r"[-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?")


def normalise(text):
    """A reason with its numbers (values, bus and branch ids) replaced by '#', and spaces collapsed."""
    return re.sub(r"\s+", " ", _NUMBER.sub("#", str(text or ""))).strip()


def signature(cls, reasons, known):
    """What kind of divergence it was, without the case's numbers."""
    if known:
        return f"documented:{known}"
    if cls == "genuine disagreement":
        keys = sorted({m.group(1) for r in reasons for m in [re.search(r"max \|(\w+)\|", r)] if m})
        cross = any(r.startswith("cross-check") for r in reasons)
        return "exceeds:" + ",".join(keys) + (";cross-check" if cross else "")
    return "; ".join(normalise(r) for r in reasons)


def key(cls, mode, tool, sig):
    return {"class": cls, "mode": mode, "tool": tool, "signature": sig}


def finding_id(k):
    text = json.dumps(k, sort_keys=True, separators=(",", ":"))
    return "F-" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:10]


def max_diffs(record, tool):
    """The element-diff maxima of the GridSim / tool pair, or None."""
    for p in (record.get("elementDiff") or {}).get("pairs", []):
        if {p.get("a"), p.get("b")} == {"gridsim", tool}:
            return p.get("max")
    return None


PROPOSALS = {
    "documented model difference": (
        "No change proposed: a documented model difference explains it ({known}). The reproducer stays as an example "
        "of the difference."),
    "rival gate fail": (
        "No GridSim change proposed: {tool}'s answer fails its own physics gate on this case and GridSim's passes. "
        "Record it against {tool}; if it matters, confirm on the reproducer with {tool}'s own settings."),
    "GridSim gate fail": (
        "Question for Mark (no code change proposed): GridSim's answer fails its own gate on the reproducer. Next "
        "steps: reproduce_check on the reproducer, physics_check, explain_solve; any core or determinism change only "
        "on Mark's ruling."),
    "genuine disagreement": (
        "Question for Mark (no code change proposed): both answers pass their gates but differ beyond the triage "
        "thresholds. This is evidence, not proof of which side is wrong. Next steps: residual and physics evidence of "
        "both sides on the reproducer; a test change would need a mathematical proof that the old expectation was "
        "wrong."),
}


#: A gate reason that says the harness could not read a tool's answer: not a verdict on the tool's solver.
UNREADABLE = ("No GridSim change proposed: the harness could not read {tool}'s answer ({reason}), so this is a "
              "question about the harness's adapter for {tool} (its payload format on this case), not a verdict on "
              "{tool}'s solver. Proposal for Mark: check the worker's output for the reproducer; any harness change "
              "is a proposal until ruled on.")


def proposal(cls, tool, known, reasons=()):
    unreadable = next((r for r in reasons if "unreadable result" in str(r)), None)
    if cls in ("rival gate fail", "GridSim gate fail") and unreadable:
        return UNREADABLE.format(tool=tool if cls == "rival gate fail" else "gridsim",
                                 reason=normalise(unreadable.split(": ", 1)[-1]))
    return PROPOSALS[cls].format(tool=tool, known=known or "-")


class Store:
    """The findings of a campaign, deduplicated by key, in order of first occurrence."""

    def __init__(self):
        self.findings = {}

    def add(self, k, occurrence, first):
        """Record one occurrence. Returns (finding, is_new). ``first`` builds the finding on its first occurrence."""
        fid = finding_id(k)
        f = self.findings.get(fid)
        if f is None:
            f = first(fid)
            f["occurrences"] = []
            self.findings[fid] = f
            new = True
        else:
            new = False
        f["occurrences"].append(occurrence)
        return f, new

    def lines(self):
        return [json.dumps(f, sort_keys=True, ensure_ascii=True) for f in self.findings.values()]
