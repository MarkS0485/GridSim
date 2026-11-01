"""MkDocs hooks for the GridSim documentation site.

The pages under docs/ are written to be read in the repository, so they link
to things that the published site does not contain:

  * files outside docs/ (../MATH.md, ../CHANGELOG.md, ../RC1.md, ../src/...),
  * folders deliberately left off the site (docs/assurance, docs/Public,
    docs/reference; see exclude_docs in mkdocs.yml),
  * plain folders (technical/, validation/, ...), which have no web page.

Left alone, each of those would be a dead link and fail "mkdocs build --strict".
This hook rewrites them before MkDocs checks links:

  * a link to a file or folder that exists in the repository but not on the
    site becomes an absolute GitHub link (blob/ for files, tree/ for folders);
  * a folder link whose folder has a contents page on the site becomes a link
    to that page;
  * a link whose target does not exist at all is an ERROR naming the page and
    the link. Nothing is ever skipped silently, and no dead link is emitted.

Links inside fenced code blocks and inline code spans are never touched.
Links to other pages and files that ARE on the site are never touched either:
MkDocs validates those itself.

Only the Python standard library is used.
"""

import os
import posixpath
import re
from urllib.parse import quote, unquote

# Where the repository lives on GitHub, and the branch the links point at.
GITHUB_BASE = "https://github.com/MarkS0485/GridSim"
GITHUB_BRANCH = "main"

# Pages that act as the "front page" of a folder, in order of preference.
# A folder link to a folder holding one of these goes to that page.
FOLDER_INDEX_NAMES = ("README.md", "index.md", "0000 Contents.md")

# A link target: either <angle bracketed> (may hold spaces and brackets) or a
# bare run of non-space characters up to the closing bracket.
_TARGET = r'(?P<target><[^>\n]*>|[^)\s]+)'
# Inline links and images:  [text](target)  or  [text](target "title")
_INLINE_LINK = re.compile(
    r'(?P<head>\]\()' + _TARGET + r'(?P<tail>(?:\s+(?:"[^"\n]*"|\'[^\'\n]*\'))?\s*\))'
)
# Reference definitions:  [label]: target  (at the start of a line)
_REF_DEF = re.compile(
    r'^(?P<head> {0,3}\[[^\]\n]+\]:\s*)' + _TARGET + r'(?P<tail>[^\n]*)$',
    re.MULTILINE,
)
# Inline code spans: `code` or ``code``. Masked out before links are searched.
_CODE_SPAN = re.compile(r'(`+)(?:(?!\1).)+?\1', re.DOTALL)
# Opening/closing line of a fenced code block.
_FENCE = re.compile(r'^ {0,3}(`{3,}|~{3,})')
# Anything that already has a scheme (https:, mailto:, ...) or is an anchor
# only or a root-absolute path: not ours to rewrite.
_NOT_RELATIVE = re.compile(r'^(?:[A-Za-z][A-Za-z0-9+.\-]*:|#|/|\?)')

# Heading anchors that the docs link to but that do not exist. Each entry is
# (target page, wrong anchor) -> right anchor. The docs themselves belong to
# other people and are not edited here, so the hook corrects the link on the
# way through. Every entry must be used on every build: if the docs get fixed
# and an entry goes stale, the build fails so the entry is removed.
ANCHOR_FIXES = {
    # The heading is "PSS/E RAW files", whose anchor is "psse-raw-files".
    ("manual/0002 Loading networks.md", "pss-e-raw-files"): "psse-raw-files",
    # There is no "Loading a pre-solved run" heading; this is the section on it.
    ("manual/0008 The desktop app.md", "loading-a-pre-solved-run"): "replaying-a-run-directory",
}

# Filled in by on_config.
_STATE = {}


class DocsLinkError(Exception):
    """Raised when a link cannot be resolved or rewritten safely."""


def on_config(config):
    """Work out the repository root and the submodule paths, once per build."""
    # mkdocs.yml sits in the repository root; docs_dir is relative to it.
    docs_dir = os.path.abspath(config["docs_dir"])
    repo_root = os.path.dirname(docs_dir)
    docs_prefix = os.path.basename(docs_dir)  # "docs"

    # Anything inside a git submodule cannot be linked as a file of this
    # repository, so note the submodule paths from .gitmodules.
    submodules = []
    gitmodules = os.path.join(repo_root, ".gitmodules")
    if os.path.isfile(gitmodules):
        with open(gitmodules, encoding="utf-8") as handle:
            for line in handle:
                match = re.match(r"\s*path\s*=\s*(\S+)\s*$", line)
                if match:
                    submodules.append(match.group(1).strip("/"))

    _STATE.update(
        repo_root=repo_root,
        docs_prefix=docs_prefix,
        submodules=submodules,
        rewrites={},  # (kind, repo path) -> count, for the end-of-build summary
        anchor_fixes_used=set(),
    )
    return config


def on_page_markdown(markdown, page, config, files):
    """Rewrite out-of-site relative links on one page."""
    if not _STATE:
        raise DocsLinkError("mkdocs_hooks: on_config did not run before on_page_markdown")

    page_uri = page.file.src_uri  # e.g. "manual/0001 Getting started.md"

    def rewrite_target(raw):
        """Return the new text for one link target (or the original text)."""
        angle = raw.startswith("<")
        target = raw[1:-1] if angle else raw
        new = _rewrite(target, page_uri, files)
        if new is None:
            return raw
        # Always angle-bracket the result: it may hold spaces.
        return "<" + new + ">"

    def inline_sub(match):
        return match.group("head") + rewrite_target(match.group("target")) + match.group("tail")

    def refdef_sub(match):
        return match.group("head") + rewrite_target(match.group("target")) + match.group("tail")

    out_lines = []
    pending = []  # lines of the current prose stretch (outside any fence)
    fence = None  # the opening fence string while inside a fenced block

    def flush_prose():
        if not pending:
            return
        text = "\n".join(pending)
        out_lines.append(_apply_outside_code_spans(text, inline_sub, refdef_sub))
        pending.clear()

    for line in markdown.split("\n"):
        match = _FENCE.match(line)
        if fence is None:
            if match:
                flush_prose()
                fence = match.group(1)
                out_lines.append(line)
            else:
                pending.append(line)
        else:
            out_lines.append(line)
            # A fence closes with the same character, at least as long.
            if match and match.group(1)[0] == fence[0] and len(match.group(1)) >= len(fence):
                fence = None
    flush_prose()
    return "\n".join(out_lines)


def on_post_build(config):
    """Print a short summary of what was rewritten (useful in CI logs)."""
    stale = set(ANCHOR_FIXES) - _STATE.get("anchor_fixes_used", set())
    if stale:
        raise DocsLinkError(
            "mkdocs_hooks: ANCHOR_FIXES entries no longer match any link (remove them): %s"
            % sorted(stale)
        )
    rewrites = _STATE.get("rewrites", {})
    if not rewrites:
        return
    print("mkdocs_hooks: rewrote %d link(s) that leave the site:" % sum(rewrites.values()))
    for (kind, path), count in sorted(rewrites.items()):
        print("  %-6s %-50s x%d" % (kind, path, count))


# ---------------------------------------------------------------------------
# Internals
# ---------------------------------------------------------------------------

def _apply_outside_code_spans(text, inline_sub, refdef_sub):
    """Apply the link substitutions to text, skipping inline code spans.

    Code spans are swapped for placeholders, the substitutions run, then the
    placeholders are swapped back, so a link written inside backticks is
    never changed.
    """
    spans = []

    def stash(match):
        spans.append(match.group(0))
        return "\x00%d\x00" % (len(spans) - 1)

    masked = _CODE_SPAN.sub(stash, text)
    masked = _INLINE_LINK.sub(inline_sub, masked)
    masked = _REF_DEF.sub(refdef_sub, masked)
    return re.sub(r"\x00(\d+)\x00", lambda m: spans[int(m.group(1))], masked)


def _rewrite(target, page_uri, files):
    """Return the replacement target, or None to leave the link as it is."""
    if target.startswith("#"):
        # Same-page anchor: only the anchor table can apply.
        return _fix_anchor(page_uri, target[1:], "#")
    if not target or _NOT_RELATIVE.match(target):
        return None

    # Split off an #anchor or ?query and undo %20 style escapes.
    path_part, hash_sep, anchor = target.partition("#")
    path_part = unquote(path_part)
    if not path_part:
        return None

    docs = _STATE["docs_prefix"]
    page_dir = posixpath.dirname(page_uri)
    # Repository-relative path of the target, e.g. "docs/technical/x.md" or "MATH.md".
    repo_path = posixpath.normpath(posixpath.join(docs, page_dir, path_part))
    is_dir_link = path_part.endswith("/") or os.path.isdir(os.path.join(_STATE["repo_root"], repo_path))

    if repo_path == ".." or repo_path.startswith("../"):
        raise DocsLinkError(
            "page '%s': link '%s' climbs out of the repository" % (page_uri, target)
        )

    inside_docs = repo_path == docs or repo_path.startswith(docs + "/")
    site_uri = posixpath.relpath(repo_path, docs) if inside_docs else None

    # 1. Target is a page or file that is on the site: MkDocs checks it.
    if inside_docs and not is_dir_link and _on_site(files, site_uri):
        return _fix_anchor(site_uri, anchor, hash_sep, path_part)

    # 2. Target is a folder on the site that has a contents page: link there.
    if inside_docs and is_dir_link:
        index_uri = _folder_index(site_uri, files)
        if index_uri is not None:
            rel = posixpath.relpath(index_uri, page_dir or ".")
            return rel + (hash_sep + anchor if hash_sep else "")

    # 3. Otherwise it must exist in the repository, and goes to GitHub.
    return _github_link(repo_path, is_dir_link, hash_sep, anchor, page_uri, target)


def _folder_index(folder_uri, files):
    """Return the site path of a folder's front page, or None if it has none."""
    for name in FOLDER_INDEX_NAMES:
        candidate = posixpath.normpath(posixpath.join(folder_uri, name))
        if _on_site(files, candidate):
            return candidate
    return None


def _github_link(repo_path, is_dir_link, hash_sep, anchor, page_uri, original):
    """Build an absolute GitHub URL for repo_path, failing loudly if it is absent."""
    root = _STATE["repo_root"]

    # Inside a submodule, the submodule folder is the furthest we can name:
    # GitHub shows a submodule as a link to the other repository.
    for submodule in _STATE["submodules"]:
        if repo_path == submodule or repo_path.startswith(submodule + "/"):
            if not os.path.isdir(os.path.join(root, submodule)):
                raise DocsLinkError(
                    "page '%s': link '%s' is inside submodule '%s', which is not present"
                    % (page_uri, original, submodule)
                )
            return _emit("tree", submodule, "")

    disk_path = os.path.join(root, *repo_path.split("/"))
    if not os.path.exists(disk_path):
        raise DocsLinkError(
            "page '%s': link '%s' points to '%s', which does not exist in the repository"
            % (page_uri, original, repo_path)
        )
    # A folder always uses tree/; the anchor is meaningless there.
    if os.path.isdir(disk_path):
        return _emit("tree", repo_path, "")
    return _emit("blob", repo_path, hash_sep + anchor if hash_sep else "")


def _emit(kind, repo_path, suffix):
    """Format the URL and record it for the end-of-build summary."""
    key = (kind, repo_path)
    _STATE["rewrites"][key] = _STATE["rewrites"].get(key, 0) + 1
    # Percent-encode spaces and other awkward characters in the path only.
    url = "%s/%s/%s/%s" % (GITHUB_BASE, kind, GITHUB_BRANCH, quote(repo_path, safe="/"))
    return url + suffix


def _on_site(files, uri):
    """True if uri is a file that will really be published.

    MkDocs keeps files matched by exclude_docs in its file list, marked as
    excluded, so a plain lookup is not enough: the mark must be checked too.
    """
    found = files.get_file_from_path(uri)
    return found is not None and not found.inclusion.is_excluded()


def _fix_anchor(target_page, anchor, hash_sep, path_part=""):
    """Apply ANCHOR_FIXES to a link to target_page; None means leave it alone."""
    fixed = ANCHOR_FIXES.get((target_page, anchor))
    if fixed is None:
        return None
    _STATE["anchor_fixes_used"].add((target_page, anchor))
    return path_part + hash_sep + fixed
