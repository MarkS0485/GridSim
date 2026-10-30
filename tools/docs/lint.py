"""Lint the GridSim documentation against docs/developer/STYLE.md.

Usage:  python tools/docs/lint.py [paths...]      (default: docs/, README.md and PAYWALLS.md)
        python tools/docs/lint.py --strict ...     (hedge/history findings also fail)

Checks, each reported as  file:line: CODE message
  ASCII   any non-ASCII character
  SPELL   US spelling outside backticks / code blocks
  HEDGE   hedge words and repair-history phrases outside code (warning unless --strict)
  LINK    relative link whose target file does not exist
  ANCHOR  #anchor into 0019 CLI reference.md that matches no heading there
  NAME    chapter file not named "NNNN Chapter title.md" (manual/technical only)
  CLI     a verb from Program.cs --help that has no heading in 0019 CLI reference.md

Exit code: 0 clean, 1 findings that fail, 2 usage error. It fails loud: an unreadable file is a failure.
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

# US -> EN-GB words that should not appear in prose. Stems are matched as whole-word prefixes.
US_SPELLINGS = [
    "normaliz", "utiliz", "initializ", "modeled", "modeling", "factoriz", "analyz", "summariz", "optimiz",
    "minimiz", "maximiz", "lineariz", "discretiz", "parameteriz", "synchroniz", "characteriz", "visualiz",
    "behavior", "color", "center", "catalog(?!ue)", "favor", "labeled", "labeling", "canceled", "traveled",
    "artifact", "defense", "license",  # 'license' is the US noun; EN-GB noun is licence (verb license is rare here)
]
HEDGES = [
    r"\bused to\b", r"\bpreviously\b", r"\bnot yet\b", r"\bcurrently\b", r"\bapproximately\b", r"\broughly\b",
    r"\bgenerally\b", r"\btypically\b", r"\bin most cases\b", r"\bit is possible\b", r"\bmight\b",
    r"\bpossibly\b", r"\bfor now\b", r"\bBefore October\b", r"\bnow reproduces\b", r"\(follow-up\)",
    r"\bsince 1\.95\.\d+", r"~\s?\d",
]
# R/N finding references are allowed only under a '## Limits' heading.
FINDING_REF = re.compile(r"\b[RN]\d{3}\b")
# A link target is either plain (no spaces) or in angle brackets, which is how a target with spaces is written.
LINK = re.compile(r"\[[^\]]*\]\((<[^>\n]+>|[^)\s]+)\)")
CHAPTER_NAME = re.compile(r'^\d{4} [^/\\:*?"<>|]+\.md$')
CHAPTER_DIRS = ("docs/manual", "docs/technical")
NAME_EXEMPT = {"0019 CLI reference.md", "0000 Contents.md"}


def slug(heading):
    """GitHub-style anchor slug for a markdown heading."""
    s = heading.strip().lower()
    s = re.sub(r"[^a-z0-9 \-_]", "", s)
    return s.replace(" ", "-")


def strip_code(line):
    """Remove inline `code` spans so identifiers are not spell- or hedge-checked."""
    return re.sub(r"`[^`]*`", "", line)


def cli_verbs():
    """Every dispatched verb, scanned exactly as CliHelpContractTests.DispatchedVerbs does."""
    path = os.path.join(ROOT, "src", "GridSim.Cli", "Program.cs")
    with open(path, encoding="utf-8") as f:
        text = f.read()
    verbs = {"licence"}
    verbs.update(re.findall(r'rest\[0\]\.Equals\("([a-z][\w-]*)"', text))
    verbs.update(re.findall(r'rest\[0\] is "([a-z][\w-]*)"', text))
    if len(verbs) <= 30:
        raise SystemExit("verb scan found only %d verbs; the dispatch pattern may have changed" % len(verbs))
    return sorted(verbs)


def cli_documented(doc, verb):
    """The test's rule: its own ##/### section, a named alias, or a `gridsim <verb>` usage line."""
    return (re.search(r"^#{2,3} " + re.escape(verb) + r"\b", doc, re.MULTILINE) is not None
            or ("(alias `%s`)" % verb) in doc
            or ("`gridsim %s" % verb) in doc)


def lint_file(path, strict, findings, anchors19):
    rel = os.path.relpath(path, ROOT).replace("\\", "/")
    try:
        with open(path, encoding="utf-8") as f:
            lines = f.read().split("\n")
    except (OSError, UnicodeDecodeError) as e:
        findings.append((rel, 0, "READ", str(e), True))
        return
    in_code = False
    in_limits = False
    for i, raw in enumerate(lines, 1):
        if raw.lstrip().startswith("```"):
            in_code = not in_code
            continue
        for ch in raw:
            if ord(ch) > 127:
                findings.append((rel, i, "ASCII", "non-ASCII character U+%04X" % ord(ch), True))
                break
        if raw.startswith("#"):
            in_limits = raw.lstrip("#").strip().lower().startswith("limits")
        if in_code:
            continue
        prose = strip_code(raw)
        low = prose.lower()
        for stem in US_SPELLINGS:
            m = re.search(r"\b" + stem, low)
            if m and not re.search(r"https?://\S*" + stem, low):
                findings.append((rel, i, "SPELL", "US spelling '%s'" % prose[m.start():m.start() + len(stem) + 3], True))
        for h in HEDGES:
            if re.search(h, prose, re.IGNORECASE):
                findings.append((rel, i, "HEDGE", "hedge/history phrase /%s/" % h, strict))
        if not in_limits and FINDING_REF.search(prose):
            findings.append((rel, i, "HEDGE", "finding reference %s outside ## Limits" % FINDING_REF.search(prose).group(0), strict))
        for target in LINK.findall(raw):
            target = target.strip("<>").replace("%20", " ")
            if re.match(r"^[a-z]+:", target) or target.startswith("#"):
                continue
            file_part, _, anchor = target.partition("#")
            dest = os.path.normpath(os.path.join(os.path.dirname(path), file_part))
            if not os.path.exists(dest):
                findings.append((rel, i, "LINK", "broken link -> %s" % target, True))
            elif anchor and os.path.basename(dest) == "0019 CLI reference.md" and anchor not in anchors19:
                findings.append((rel, i, "ANCHOR", "no heading #%s in 0019 CLI reference.md" % anchor, True))
    d = os.path.dirname(rel)
    base = os.path.basename(rel)
    if d in CHAPTER_DIRS and base not in NAME_EXEMPT and not CHAPTER_NAME.match(base):
        # Redirect stubs (one short paragraph pointing elsewhere) are exempt.
        if len([l for l in lines if l.strip()]) > 3:
            findings.append((rel, 0, "NAME", "chapter file not named \"NNNN Chapter title.md\"", True))


def main(argv):
    # Report in plain ASCII whatever the console code page, so a finding never crashes the report.
    sys.stdout.reconfigure(encoding="ascii", errors="backslashreplace")
    strict = "--strict" in argv
    args = [a for a in argv if not a.startswith("--")]
    targets = args or [os.path.join(ROOT, "docs"), os.path.join(ROOT, "README.md"), os.path.join(ROOT, "PAYWALLS.md")]
    files = []
    for t in targets:
        t = os.path.abspath(t)
        if os.path.isdir(t):
            for dp, dn, fn in os.walk(t):
                dn[:] = [x for x in dn if x not in ("_captures", "_work", "Public")]   # Public: git submodules (external repos), not our chapters
                files += [os.path.join(dp, x) for x in fn if x.endswith(".md")]
        elif os.path.isfile(t):
            files.append(t)
        else:
            print("no such path: " + t)
            return 2
    cli_ref = os.path.join(ROOT, "docs", "manual", "0019 CLI reference.md")
    anchors19 = set()
    with open(cli_ref, encoding="utf-8") as f:
        doc19 = f.read()
    for l in doc19.split("\n"):
        if l.startswith("#"):
            anchors19.add(slug(l.lstrip("#")))
    findings = []
    for p in sorted(files):
        lint_file(p, strict, findings, anchors19)
    for verb in cli_verbs():
        if not cli_documented(doc19, verb):
            findings.append(("docs/manual/0019 CLI reference.md", 0, "CLI", "verb '%s' undocumented (test rule)" % verb, True))
    failing = 0
    for rel, line, code, msg, fails in findings:
        print("%s:%d: %s %s%s" % (rel, line, code, msg, "" if fails else " (warning)"))
        failing += 1 if fails else 0
    counts = {}
    for f in findings:
        counts[f[2]] = counts.get(f[2], 0) + 1
    print("files %d, findings %d (%s), failing %d" % (
        len(files), len(findings), ", ".join("%s %d" % kv for kv in sorted(counts.items())), failing))
    return 1 if failing else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
