"""The study record (``gridsim-mcp-record/1``): a hash-chained log of every tool call in a session.

A record is a header line and one entry line per call, as JSON Lines. Each entry carries the tool, its arguments, the
reasoning the AI stated (optional), the outcome, the case hash, the result hash and the build identity, plus
``prevHash`` (the previous entry's hash, or the header's for the first) and ``hash``: the SHA-256 of the entry's canonical
JSON without ``hash``. Changing, removing or reordering any line breaks the chain from that line on.

Nothing volatile is hashed: no clock time, no session name, no rpc host token (level 2 handles carry a random token,
and the MCP never shows them; cases are named c1, c2, ... per session). The same calls on the same build therefore give
the same hashes, which is what ``record_replay`` checks. No licence content is ever recorded: the header says only
whether a licence was configured.
"""

import hashlib
import json
from pathlib import Path

SCHEMA = "gridsim-mcp-record/1"

#: Tools that are about the record itself: not recorded.
UNRECORDED = frozenset({"record_export", "record_replay"})

#: Recorded tools that replay does not re-run (session bookkeeping).
NOT_REPLAYED = frozenset({"session_open", "session_close"})


def canonical(obj):
    """Canonical JSON text: sorted keys, no spaces, ASCII, no NaN (GridSim writes NaN and infinity as strings)."""
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=True, allow_nan=False)


def sha256_text(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sha256_obj(obj):
    return sha256_text(canonical(obj))


def entry_hash(entry):
    return sha256_obj({k: v for k, v in entry.items() if k != "hash"})


class Record:
    """The record of one session."""

    def __init__(self, header):
        self.header = dict(header)
        self.header["schema"] = SCHEMA
        self.header.pop("hash", None)
        self.header["hash"] = entry_hash(self.header)
        self.entries = []

    @property
    def head(self):
        return self.entries[-1]["hash"] if self.entries else self.header["hash"]

    def append(self, tool, arguments, reasoning, status, exit_code=None, case_id=None, case_hash=None,
               result_hash=None, result_id=None, build=None, message=None, tuned=None, off_standard=None):
        entry = {
            "seq": len(self.entries) + 1,
            "tool": tool,
            "arguments": arguments,
            "reasoning": reasoning,
            "status": status,
            "exitCode": exit_code,
            "case": case_id,
            "caseHash": case_hash,
            "resultHash": result_hash,
            "result": result_id,
            "build": build,
            "message": message,
            "prevHash": self.head,
        }
        # Only a tuned call carries these, so the entries (and hashes) of untuned calls are as they always were.
        if tuned:
            entry["tuned"] = tuned
            if off_standard:
                entry["offStandard"] = list(off_standard)
        entry["hash"] = entry_hash(entry)
        self.entries.append(entry)
        return entry

    def lines(self):
        return [canonical(self.header)] + [canonical(e) for e in self.entries]

    def write(self, path):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        text = "\n".join(self.lines()) + "\n"
        path.write_text(text, encoding="utf-8", newline="\n")
        return sha256_text(text)


def read(path):
    """Read a record file: (header, entries). ValueError when it is not a gridsim-mcp-record/1 file."""
    lines = [ln for ln in Path(path).read_text(encoding="utf-8").splitlines() if ln.strip()]
    if not lines:
        raise ValueError("the record file is empty")
    header = json.loads(lines[0])
    if header.get("schema") != SCHEMA:
        raise ValueError(f"not a {SCHEMA} file (schema {header.get('schema')!r})")
    return header, [json.loads(ln) for ln in lines[1:]]


def verify(header, entries):
    """Check the chain. Returns (ok, first_bad_seq_or_None, message)."""
    if entry_hash(header) != header.get("hash"):
        return False, 0, "the header hash does not match its content"
    prev = header["hash"]
    for i, e in enumerate(entries, 1):
        if e.get("seq") != i:
            return False, i, f"entry {i} has seq {e.get('seq')!r}"
        if e.get("prevHash") != prev:
            return False, i, f"entry {i}: prevHash does not match the previous hash (chain broken)"
        if entry_hash(e) != e.get("hash"):
            return False, i, f"entry {i}: hash does not match its content (TAMPERED)"
        prev = e["hash"]
    return True, None, f"chain intact: {len(entries)} entries"
