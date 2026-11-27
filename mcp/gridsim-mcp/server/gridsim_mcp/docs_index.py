"""GridSim's own chapters, as the sections this server serves: the Troubleshooting index and the entries the message map
points at (manual ch. 30), the per-unit section (technical ch. 2), the provenance tags and grounded values (technical
ch. 13), the dynamic-model list (technical ch. 24) and the CLI reference section of each allowlisted verb (manual
ch. 19).

The plugin ships a snapshot of exactly those sections in ``server/docs_snapshot`` (``tools/sync_docs.py`` writes it
from a GridSim checkout, with a manifest of each source chapter's path and SHA-256), so they are there with a release
install, which carries only the CLI reference. At run time the server extracts the same sections from the GridSim
checkout above the program when there is one, and serves that only when it is identical to the snapshot; otherwise it
serves the snapshot. Every extract passes through ``filter_text``, which drops each paragraph, list item or table row
about services and data this server does not offer. GridSim's documents themselves are never changed.

Also the Troubleshooting map: a GridSim message is matched to the entry of manual chapter 30 that explains it.
"""

import hashlib
import json
import re
from pathlib import Path

#: The chapters served, as (folder, file name).
TROUBLESHOOTING = ("manual", "0030 Troubleshooting.md")
PER_UNIT = ("technical", "0002 Data model and per-unit conventions.md")
GROUNDING = ("technical", "0013 Asset-dynamics data grounding.md")
DYNAMIC_MODELS = ("technical", "0024 Standard dynamic-model library.md")
CLI_REFERENCE = ("manual", "0019 CLI reference.md")

#: Sections of the Troubleshooting chapter that are about services, devices or data the MCP server does not offer.
#: They are left out of the index served here.
_TROUBLESHOOTING_LEFT_OUT = ("GDA and the bridge", "Interposer", "Web host and desktop app", "State estimation",
                             "Build and test problems", "GPU fallback", "Crash logs")


def find_docs_root(command, depth=6):
    """The ``docs`` folder above the GridSim program (a checkout), or None."""
    p = Path(command[-1] if len(command) > 1 else command[0]).resolve().parent
    for _ in range(depth):
        if (p / "docs" / "manual").is_dir() and (p / "docs" / "technical").is_dir():
            return p / "docs"
        if p.parent == p:
            break
        p = p.parent
    return None


def read_chapter(root, chapter):
    if root is None:
        return None
    f = root / chapter[0] / chapter[1]
    try:
        return f.read_text(encoding="utf-8").replace("\r\n", "\n")
    except OSError:
        return None


def not_available(chapter):
    kind = "manual" if chapter[0] == "manual" else "technical reference"
    return (f"The GridSim {kind} chapter '{chapter[1][:-3]}' is neither installed beside this GridSim program nor in "
            "the plugin's snapshot. Read it in GridSim's documentation.\n")


def section(text, heading, level=None):
    """The text of the section that starts with the heading line ``heading`` (exact text after the #'s), up to the next
    heading of the same or a higher level; None when there is no such heading."""
    if text is None:
        return None
    lines = text.split("\n")
    for i, line in enumerate(lines):
        m = re.match(r"^(#+)\s+(.*)$", line)
        if m and m.group(2).strip() == heading and (level is None or len(m.group(1)) == level):
            lvl = len(m.group(1))
            out = [line]
            for nxt in lines[i + 1:]:
                n = re.match(r"^(#+)\s", nxt)
                if n and len(n.group(1)) <= lvl:
                    break
                out.append(nxt)
            return "\n".join(out).rstrip() + "\n"
    return None


def headings(text, levels=(2, 3)):
    """(level, heading) for every heading of the given levels, in order (not inside a code fence)."""
    out, fence = [], False
    for line in text.split("\n"):
        if line.startswith("```"):
            fence = not fence
            continue
        m = None if fence else re.match(r"^(#+)\s+(.*)$", line)
        if m and len(m.group(1)) in levels:
            out.append((len(m.group(1)), m.group(2).strip()))
    return out


# -- The filter ---------------------------------------------------------------------------------------------------------

#: A paragraph, list item or table row that matches is about a service or data this server does not offer (material
#: outside the public product scope) and is left out.
FILTER = re.compile(r"\bGDA|\bGda[A-Z]|\blakes?\b|lake[- ]product|GB history|history of GB|state[- ]estimat|"
                    r"\bnowcast", re.IGNORECASE)
_LIST_ITEM = re.compile(r"^\s*(?:[-*+]|\d+[.)])\s")


def _blocks(text):
    """Split Markdown (or plain text) into blocks: a code fence, a heading line, a table row, a list item with its
    indented continuation lines, a paragraph, or a run of blank lines."""
    lines = text.split("\n")
    blocks, i = [], 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("```"):
            j = i + 1
            while j < len(lines) and not lines[j].startswith("```"):
                j += 1
            blocks.append(lines[i:j + 1])
            i = j + 1
        elif not line.strip():
            j = i
            while j < len(lines) and not lines[j].strip():
                j += 1
            blocks.append(lines[i:j])
            i = j
        elif line.startswith("#") or line.lstrip().startswith("|"):
            blocks.append([line])
            i += 1
        else:
            item = bool(_LIST_ITEM.match(line))
            j = i + 1
            while (j < len(lines) and lines[j].strip() and not lines[j].startswith(("#", "```"))
                   and not lines[j].lstrip().startswith("|") and not _LIST_ITEM.match(lines[j])
                   and (not item or lines[j].startswith((" ", "\t")))):
                j += 1
            blocks.append(lines[i:j])
            i = j
    return blocks


def filter_text(text):
    """``text`` without the blocks FILTER matches (only the matching block goes; its neighbours stay)."""
    if text is None:
        return None
    kept = [b for b in _blocks(text) if not FILTER.search("\n".join(b))]
    out = "\n".join(line for b in kept for line in b)
    return re.sub(r"\n{3,}", "\n\n", out).strip("\n") + "\n"


# -- Extracts: the exact text each resource serves ----------------------------------------------------------------------

def troubleshooting_entries(text):
    """{section: [entry headings]} of chapter 30, without the sections left out."""
    entries, current = {}, None
    for lvl, h in headings(text):
        if lvl == 2:
            current = None if h in _TROUBLESHOOTING_LEFT_OUT else h
            if current is not None:
                entries[current] = []
        elif current is not None:
            entries[current].append(h)
    return entries


def _troubleshooting_index(text):
    lines = ["GridSim manual ch. 30 Troubleshooting: index of entries (each heading is the message or symptom).",
             "Read an entry with break_it, or in the manual. Sections about services this server does not offer are "
             "left out.", ""]
    for sec, items in troubleshooting_entries(text).items():
        lines.append(f"## {sec}")
        lines += [f"- {h}" for h in items] or ["- (section text only)"]
        lines.append("")
    return "\n".join(lines)


def _troubleshooting_mapped(text):
    """The ch. 30 entries the message map points at, each as its own level-3 section."""
    out = ["GridSim manual ch. 30 Troubleshooting: the entries the server's message map points at.", ""]
    seen = set()
    for _, heading, _ in TROUBLESHOOTING_MAP:
        if heading in seen:
            continue
        seen.add(heading)
        body = section(text, heading, level=3)
        if body:
            out.append(body)
    return "\n".join(out)


def _per_unit(text):
    return ("From GridSim technical ch. 2 Data model and per-unit conventions.\n\n"
            + (section(text, "Per-unit conventions", 2) or "(section not found)\n"))


def _grounded_values(text):
    body = [section(text, "Provenance tags", 3), section(text, "Grounded values per class", 2)]
    return ("From GridSim technical ch. 13 Asset-dynamics data grounding: the provenance tags and the grounded value "
            "of every class parameter. A value tagged CuratedPlaceholder carries [VERIFY]: cite it as a placeholder, "
            "never as a measured value.\n\n" + "\n".join(b for b in body if b))


def _dynamic_models(text):
    lines = ["From GridSim technical ch. 24 Standard dynamic-model library: the models (headings of the Model "
             "section), then how the CLI exposes them.", ""]
    in_model = False
    for lvl, h in headings(text):
        if lvl == 2:
            in_model = h == "Model"
        elif in_model:
            lines.append(f"- {h}")
    lines.append("")
    cli = section(text, "How the CLI exposes the library", 3)
    if cli:
        lines.append(cli)
    return "\n".join(lines)


def verb_section(text, verb):
    """The ch. 19 section of a verb, found as GridSim's own help finds it: a heading equal to the verb, else the level-3
    section that names it as ``(alias `verb`)``. None when there is none."""
    v = verb.strip().lower()
    for lvl, h in headings(text):
        if h.lower() == v:
            return section(text, h, lvl)
    alias = f"(alias `{v}`)"
    for _, h in headings(text, levels=(3,)):
        body = section(text, h, 3)
        if body and alias in body:
            return body
    return None


#: The ch. 19 section of each allowlisted verb is its own snapshot file, verb-help/<verb>.md: small files, each well
#: under the plugin directory's per-file review threshold.
VERB_HELP_DIR = "verb-help"


def verb_help_file(verb):
    return f"{VERB_HELP_DIR}/{verb}.md"


def _verb_help(verb):
    def fn(text):
        return verb_section(text, verb)
    return fn


SNAPSHOT_DIR = Path(__file__).resolve().parents[1] / "docs_snapshot"
MANIFEST = "MANIFEST.json"
MANIFEST_SCHEMA = "gridsim-mcp-docs-snapshot/1"


def _extracts():
    """Every snapshot file: (file name, source chapter, extractor over the chapter text). The extractor's output, after
    the filter, is the exact text served."""
    from .allowlist import ALLOWED_VERBS
    return (
        ("troubleshooting-index.md", TROUBLESHOOTING, _troubleshooting_index),
        ("troubleshooting-entries.md", TROUBLESHOOTING, _troubleshooting_mapped),
        ("per-unit.md", PER_UNIT, _per_unit),
        ("grounded-values.md", GROUNDING, _grounded_values),
        ("dynamic-models.md", DYNAMIC_MODELS, _dynamic_models),
    ) + tuple((verb_help_file(v), CLI_REFERENCE, _verb_help(v)) for v in sorted(ALLOWED_VERBS))


def extract(root, name):
    """The text of the snapshot file ``name``, extracted from the checkout's docs folder ``root``; None when the
    chapter or the section is not there."""
    entry = next((e for e in _extracts() if e[0] == name), None)
    if entry is None:
        return None
    _, chapter, fn = entry
    text = read_chapter(root, chapter)
    if text is None:
        return None
    out = fn(text)
    return None if out is None else filter_text(out)


def source_sha256(root, chapter):
    """SHA-256 of a source chapter's text with LF line ends (the same for a CRLF or an LF checkout)."""
    return hashlib.sha256(read_chapter(root, chapter).encode("utf-8")).hexdigest()


def extract_all(root):
    """{file name: text} for every snapshot file plus the manifest, from the checkout's docs folder ``root``."""
    files, sources = {}, []
    for name, chapter, _ in _extracts():
        files[name] = extract(root, name)
        if files[name] is None:
            raise FileNotFoundError(f"docs/{chapter[0]}/{chapter[1]} not found")
        sources.append({"file": name, "source": f"docs/{chapter[0]}/{chapter[1]}",
                        "source_sha256": source_sha256(root, chapter),
                        "sha256": hashlib.sha256(files[name].encode("utf-8")).hexdigest()})
    manifest = {"schema": MANIFEST_SCHEMA, "filter": FILTER.pattern, "files": sources}
    files[MANIFEST] = json.dumps(manifest, indent=1, ensure_ascii=True) + "\n"
    return files


def snapshot(name, folder=None):
    f = (folder or SNAPSHOT_DIR) / name
    try:
        return f.read_bytes().decode("utf-8")
    except OSError:
        return None


def served(root, name, folder=None):
    """(text, source) for the snapshot file ``name``: the checkout's extract when it is identical to the snapshot
    (source "checkout"), else the snapshot ("snapshot", or "snapshot (checkout differs)" when a checkout's extract
    differs), else, with no snapshot in the plugin, the checkout's extract; else (None, None)."""
    snap = snapshot(name, folder)
    live = extract(root, name) if root is not None else None
    if snap is not None:
        if live is None:
            return snap, "snapshot"
        return (live, "checkout") if live == snap else (snap, "snapshot (checkout differs)")
    return (live, "checkout") if live is not None else (None, None)


# -- Troubleshooting ----------------------------------------------------------------------------------------------------

def troubleshooting_index(root):
    text, _ = served(root, "troubleshooting-index.md")
    return text if text is not None else not_available(TROUBLESHOOTING)


#: Message patterns (a regular expression over GridSim's printed output) and the Troubleshooting entry heading each one
#: is explained by. The first match wins. Headings are the chapter's own text.
TROUBLESHOOTING_MAP = [
    (r"power-flow state it starts from did not converge",
     "`<study>: the power-flow state it starts from did not converge (status ..., max mismatch ... p.u. after N iterations)`",
     "exact"),
    (r"DID NOT CONVERGE|did not converge|not converged|MaxIterations|Diverged|NonFinite",
     "`DID NOT CONVERGE` and exit 3", "exact"),
    (r"ISLANDED|NO SOLUTION|solver error:", "`ISLANDED`, `NO SOLUTION` and `solver error:` rows", "exact"),
    (r"[Nn]o slack \(reference\) bus|not connected to any slack",
     "`ISLANDED`, `NO SOLUTION` and `solver error:` rows",
     "closest: the chapter has no entry for a refused edit; this entry explains a part cut off from the slack"),
    (r"found no eligible generator to trip", "`trip found no eligible generator to trip in <case>`", "exact"),
    (r"No synchronous machines with inertia",
     "`stability: No synchronous machines with inertia: the classical stability model has no rotor to integrate.`",
     "exact"),
    (r"fault bus \S+ is not in case", "`error: stability: fault bus N is not in case '<name>'.`", "exact"),
    (r"licence allows", "`error: This network has N buses; your Personal licence allows 1,500. ...`", "exact"),
    (r"unknown command or case", "`error: unknown command or case '<name>'.`", "exact"),
    (r"MATPOWER case file not found", "`error: MATPOWER case file not found: '<name>.m' (full path ...).`", "exact"),
    (r"case folder not found", "`error: case folder not found: '<folder>' (full path ...).`", "exact"),
    (r"Taps NOT SETTLED", "`Taps NOT SETTLED: ...` under a converged report", "exact"),
]


def match_troubleshooting(root, message):
    """The Troubleshooting entry that explains ``message``: {chapter, heading, match, text, installed, source}. None
    when no entry matches. ``installed`` says whether the entry text is available (checkout or snapshot)."""
    for rx, heading, quality in TROUBLESHOOTING_MAP:
        if re.search(rx, message or ""):
            text, source = served(root, "troubleshooting-entries.md")
            body = section(text, heading, level=3)
            return {"chapter": "manual ch. 30 Troubleshooting", "heading": heading, "match": quality, "text": body,
                    "installed": body is not None, "source": source}
    return None


# -- Technical chapters ---------------------------------------------------------------------------------------------------

def per_unit(root):
    text, _ = served(root, "per-unit.md")
    return text if text is not None else not_available(PER_UNIT)


def grounded_values(root):
    text, _ = served(root, "grounded-values.md")
    return text if text is not None else not_available(GROUNDING)


def dynamic_models(root):
    text, _ = served(root, "dynamic-models.md")
    return text if text is not None else not_available(DYNAMIC_MODELS)


def verb_help(root, verb):
    """The ch. 19 section of an allowlisted verb (checkout when identical, else snapshot), or None."""
    from .allowlist import ALLOWED_VERBS
    if verb not in ALLOWED_VERBS:
        return None
    text, _ = served(root, verb_help_file(verb))
    return text
