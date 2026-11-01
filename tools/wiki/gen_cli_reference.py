#!/usr/bin/env python3
"""Generate the GridSim wiki pages.

What this script does, in plain English
---------------------------------------
1. Reads the hand-written page templates in tools/wiki/src/ and writes them
   into tools/wiki/pages/, replacing a few placeholders such as {{SITE}} with
   the constants defined just below.  That is how the site base URL lives in
   exactly one place.
2. Reads docs/manual/0019 CLI reference.md (the source of truth; it is
   compiled into the binary) and writes pages/CLI-quick-reference.md: the
   verb index table, then one row per verb with a one-line summary and a
   link to the full section on the site.

Modes
-----
  gen_cli_reference.py          write everything into pages/
  gen_cli_reference.py --check  write nothing; exit 1 if pages/ differs from
                                what would be generated (CI drift check)

The script fails loud: any problem prints "error: ..." on stderr and exits
non-zero.  Nothing is silently skipped.  Standard library only, Python 3.12.
"""

import argparse
import re
import sys
from pathlib import Path
from urllib.parse import quote

# ---------------------------------------------------------------------------
# Constants.  Change these and re-run to retarget every link in the wiki.
# ---------------------------------------------------------------------------
SITE_BASE = "https://marks0485.github.io/GridSim/"   # single place for the site URL
REPO_URL = "https://github.com/MarkS0485/GridSim"
RELEASE_URL = REPO_URL + "/releases/tag/v2.1.000-RC-1"   # as used by README.md

CLI_CHAPTER_REL = "docs/manual/0019 CLI reference.md"
CLI_PAGE_NAME = "CLI-quick-reference.md"
HEADER_LINE = ("Generated from docs/manual/0019 CLI reference.md by "
               "tools/wiki/gen_cli_reference.py; do not edit by hand.")

HERE = Path(__file__).resolve().parent          # tools/wiki
REPO_ROOT = HERE.parent.parent                  # repository root
SRC_DIR = HERE / "src"
PAGES_DIR = HERE / "pages"


def fail(message):
    """Print a clear error and stop with a non-zero exit code."""
    print("error: " + message, file=sys.stderr)
    sys.exit(1)


def site(path):
    """Build a site URL from a path relative to the site root (no encoding)."""
    return SITE_BASE.rstrip("/") + "/" + path.lstrip("/")


def cli_chapter_url(anchor=""):
    """URL of the CLI reference chapter on the site, optionally with an anchor.

    MkDocs turns 'manual/0019 CLI reference.md' into the directory-style URL
    manual/0019%20CLI%20reference/ .
    """
    url = site("manual/" + quote("0019 CLI reference") + "/")
    return url + ("#" + anchor if anchor else "")


# ---------------------------------------------------------------------------
# Heading slugs, the way the MkDocs toc extension writes them: lower case,
# drop everything that is not a word character, space or hyphen, then turn
# each space into one hyphen.  Hyphens are kept one for one, so
# "bench --estimate" becomes "bench---estimate", which is exactly what the
# chapter's own internal links use.
# ---------------------------------------------------------------------------
def slugify(heading):
    text = heading.replace("`", "").strip().lower()
    text = re.sub(r"[^\w\s-]", "", text)       # strip punctuation except - and _
    return re.sub(r"\s", "-", text)            # each space becomes one hyphen


def strip_md(text):
    """Reduce markdown inline links and emphasis to plain text for a summary."""
    text = re.sub(r"\[([^\]]+)\]\((?:<[^>]*>|[^)]*)\)", r"\1", text)   # [t](url) -> t
    text = text.replace("*", "")
    return re.sub(r"\s+", " ", text).strip()


def first_sentence(paragraph):
    """First sentence of a paragraph.

    A sentence ends at '. ' followed by a capital letter or a backtick, and
    only outside brackets (so 'ch. 4', 'e.g. x' and full stops inside a
    parenthesis do not split).  A very short first sentence (such as
    'EXPERIMENTAL.') is joined to the next one so the summary says something.
    """
    text = strip_md(paragraph)
    ends, depth = [], 0
    for i, ch in enumerate(text):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth = max(0, depth - 1)
        elif ch in ".!?" and depth == 0 and re.match(r"\s+[A-Z`(]", text[i + 1:]):
            ends.append(i + 1)
    cut = len(text)
    if ends:
        cut = ends[0]
        if cut < 25 and len(ends) > 1:
            cut = ends[1]
        elif cut < 25:
            cut = len(text)
    sentence = text[:cut]
    if len(sentence) > 240:                      # keep table rows readable
        sentence = sentence[:237].rsplit(" ", 1)[0] + "..."
    return sentence


# The no-verb section opens with a table, not prose, so its summary is fixed
# here (taken from that table: a bare case name is solved once and reported).
SUMMARY_OVERRIDES = {
    "running-a-case-no-verb":
        "Solve a case without naming a verb, for example `gridsim case14 --once`; "
        "a built-in case name, a MATPOWER file or `--dir <path>`.",
}


# ---------------------------------------------------------------------------
# Parse the CLI chapter.
# ---------------------------------------------------------------------------
def parse_sections(lines):
    """Return {slug: (heading_text, [body lines])} for every ## and ### heading.

    Fenced code blocks are respected so a '#' comment inside a block is not
    mistaken for a heading.  Two headings with the same slug are an error,
    because the site would suffix one of them and our anchors would be wrong.
    """
    sections = {}
    current = None
    in_fence = False
    for line in lines:
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
        m = None if in_fence else re.match(r"^(#{2,3})\s+(.*\S)\s*$", line)
        if m:
            heading = m.group(2)
            slug = slugify(heading)
            if slug in sections:
                fail(f"duplicate heading slug '{slug}' in {CLI_CHAPTER_REL}")
            current = (heading, [])
            sections[slug] = current
        elif current is not None:
            current[1].append(line)
    return sections


def section_summary(slug, body):
    """First prose sentence of a section, skipping usage lines, tables, code."""
    paragraphs, buf, in_fence = [], [], False
    for line in body:
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
            buf = []
            continue
        if in_fence:
            continue
        if line.strip() == "":
            if buf:
                paragraphs.append(buf)
                buf = []
        else:
            buf.append(line)
    if buf:
        paragraphs.append(buf)
    for para in paragraphs:
        first = para[0].lstrip()
        if all(p.lstrip().startswith("`gridsim") for p in para):
            continue                                   # usage line(s)
        if first.startswith(("|", "-", "#", ">", "!")):
            continue                                   # table, list, quote
        return first_sentence(" ".join(p.strip() for p in para))
    fail(f"section '{slug}' has no prose paragraph to summarise")


def parse_index(lines):
    """Read the table under '## Verb index' into [(group, row_text)]."""
    try:
        start = lines.index("## Verb index")
    except ValueError:
        fail(f"no '## Verb index' heading in {CLI_CHAPTER_REL}")
    rows = []
    for line in lines[start + 1:]:
        if line.startswith("## "):
            break
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 2 or cells[0] == "Group" or set(cells[0]) <= set("-"):
            continue
        rows.append((cells[0], cells[1]))
    if not rows:
        fail("the verb index table has no rows")
    return rows


LINK_RE = re.compile(r"\[(`?)([^\]`]+)\1\]\(#([^)]+)\)")   # [`verb`](#anchor)


def build_cli_page():
    chapter = REPO_ROOT / CLI_CHAPTER_REL
    if not chapter.is_file():
        fail(f"cannot find {chapter}")
    lines = chapter.read_text(encoding="utf-8").splitlines()
    sections = parse_sections(lines)
    index = parse_index(lines)

    verbs = []                       # (group, verb, anchor)
    for group, cell in index:
        found = LINK_RE.findall(cell)
        if not found:
            fail(f"index row '{group}' contains no verb links")
        for _tick, verb, anchor in found:
            if anchor not in sections:
                fail(f"verb '{verb}' (group '{group}') links to #{anchor}, "
                     f"but the chapter has no heading with that slug")
            verbs.append((group, verb, anchor))
    if not verbs:
        fail("parsed zero verbs from the verb index")

    out = []
    out.append(HEADER_LINE)
    out.append("")
    out.append("# CLI quick reference")
    out.append("")
    out.append("The `gridsim` command line tool, verb by verb. Every verb answers "
               "`gridsim <verb> --help` with its usage, flags, defaults and exit codes. "
               f"The full chapter is [CLI reference]({cli_chapter_url()}) on the site.")
    out.append("")
    out.append("## Verb index")
    out.append("")
    out.append("| Group | Verbs |")
    out.append("|---|---|")
    for group, cell in index:
        linked = LINK_RE.sub(
            lambda m: f"[{m.group(1)}{m.group(2)}{m.group(1)}]({cli_chapter_url(m.group(3))})",
            cell)
        out.append(f"| {group} | {linked} |")
    out.append("")
    out.append(f"## All verbs ({len(verbs)})")
    out.append("")
    out.append("| Verb | Summary | Full section |")
    out.append("|---|---|---|")
    for group, verb, anchor in verbs:
        heading, body = sections[anchor]
        summary = SUMMARY_OVERRIDES.get(anchor) or section_summary(anchor, body)
        summary = summary.replace("|", "\\|")
        out.append(f"| `{verb}` | {summary} | [{heading.replace('`', '')}]({cli_chapter_url(anchor)}) |")
    out.append("")
    return "\n".join(out)


# ---------------------------------------------------------------------------
# Render the hand-written templates.
# ---------------------------------------------------------------------------
PLACEHOLDERS = {
    "{{SITE}}": SITE_BASE,
    "{{REPO}}": REPO_URL,
    "{{RELEASE}}": RELEASE_URL,
}


def render_templates():
    """Return {file name: text} for every template in src/."""
    if not SRC_DIR.is_dir():
        fail(f"template folder missing: {SRC_DIR}")
    templates = sorted(SRC_DIR.glob("*.md"))
    if not templates:
        fail(f"no templates found in {SRC_DIR}")
    rendered = {}
    for path in templates:
        text = path.read_text(encoding="utf-8")
        for key, value in PLACEHOLDERS.items():
            text = text.replace(key, value)
        left = re.findall(r"\{\{[^}]*\}\}", text)
        if left:
            fail(f"{path.name}: unresolved placeholder(s) {sorted(set(left))}")
        rendered[path.name] = text
    return rendered


def check_ascii(name, text):
    """The wiki is plain ASCII.  Report the first offending character."""
    for lineno, line in enumerate(text.splitlines(), 1):
        for ch in line:
            if ord(ch) > 126 or (ord(ch) < 32 and ch != "\t"):
                fail(f"{name} line {lineno}: non-ASCII character U+{ord(ch):04X}")


def main():
    ap = argparse.ArgumentParser(description="Generate the GridSim wiki pages.")
    ap.add_argument("--check", action="store_true",
                    help="write nothing; exit 1 if pages/ differs from the generated output")
    args = ap.parse_args()

    wanted = render_templates()
    if CLI_PAGE_NAME in wanted:
        fail(f"src/ must not contain {CLI_PAGE_NAME}; it is generated")
    wanted[CLI_PAGE_NAME] = build_cli_page()
    for name in list(wanted):
        text = wanted[name]
        check_ascii(name, text)
        if not text.endswith("\n"):
            wanted[name] = text + "\n"

    if args.check:
        problems = []
        existing = {p.name for p in PAGES_DIR.glob("*.md")} if PAGES_DIR.is_dir() else set()
        for name, text in wanted.items():
            path = PAGES_DIR / name
            if not path.is_file():
                problems.append(f"missing: pages/{name}")
            # Compare with line endings normalised: a Windows checkout with core.autocrlf=true holds the committed
            # LF pages as CRLF, which is the same content. Any other byte difference still fails.
            elif path.read_bytes().replace(b"\r\n", b"\n") != text.encode("ascii"):
                problems.append(f"differs: pages/{name}")
        for name in sorted(existing - set(wanted)):
            problems.append(f"stale: pages/{name}")
        if problems:
            print("error: wiki pages are out of date; run tools/wiki/gen_cli_reference.py",
                  file=sys.stderr)
            for p in problems:
                print("  " + p, file=sys.stderr)
            sys.exit(1)
        print(f"ok: {len(wanted)} wiki pages are up to date")
        return

    PAGES_DIR.mkdir(exist_ok=True)
    for stale in PAGES_DIR.glob("*.md"):
        if stale.name not in wanted:
            stale.unlink()                      # page no longer exists in src/
    for name, text in wanted.items():
        (PAGES_DIR / name).write_bytes(text.encode("ascii"))   # LF endings, always
    print(f"wrote {len(wanted)} pages to {PAGES_DIR}")


if __name__ == "__main__":
    main()
