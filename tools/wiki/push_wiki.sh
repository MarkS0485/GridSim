#!/usr/bin/env bash
# push_wiki.sh - publish tools/wiki/pages to the GridSim GitHub wiki.
#
# BEFORE THIS CAN EVER WORK: the wiki must be enabled in the repository
# settings (Settings > Features > Wikis) and its first page must be created
# once by hand in the web UI. Until then the .wiki.git repository does not
# exist and the clone below fails.
#
# What it does:
#   1. clones the wiki repository into a temporary folder,
#   2. runs gen_cli_reference.py to (re)build pages/,
#   3. mirrors pages/ into the clone (adds and updates pages, deletes wiki
#      pages that no longer exist in pages/),
#   4. commits with the fixed message "Sync wiki from tools/wiki", but only
#      if something changed,
#   5. pushes.
#
# Remote URL, in order of preference:
#   WIKI_REMOTE                         full URL, token included if needed
#   GITHUB_TOKEN + GITHUB_REPOSITORY    built as
#       https://x-access-token:<token>@github.com/<owner>/<repo>.wiki.git
#   neither                             https://github.com/MarkS0485/GridSim.wiki.git
#                                       (uses your own git credentials)
#
# The token is never echoed: shell tracing is switched off while it is
# handled and all git output is passed through a filter that masks it.
#
# Options:
#   --dry-run   do not clone or push. Stage into a fresh local temp folder
#               (an empty "git init" stands in for the clone), commit there
#               and print what would be pushed. The folder is kept so you can
#               look at it; its path is printed.
#
# Any failure stops the script with a non-zero exit code and a clear message.

set -euo pipefail

DEFAULT_REMOTE="https://github.com/MarkS0485/GridSim.wiki.git"
COMMIT_MESSAGE="Sync wiki from tools/wiki"
AUTHOR_NAME="GridSim wiki sync"
AUTHOR_EMAIL="noreply@github.com"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PAGES="$HERE/pages"

die() { echo "error: $*" >&2; exit 1; }

DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '2,38p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "unknown argument '$arg' (only --dry-run is accepted)" ;;
  esac
done

command -v git >/dev/null 2>&1 || die "git is not installed or not on PATH"
PYTHON=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import sys; sys.exit(0 if sys.version_info[0] == 3 else 1)" >/dev/null 2>&1; then PYTHON="$candidate"; break; fi
done
[ -n "$PYTHON" ] || die "python 3 is not installed or not on PATH"

# --- work out the remote, without ever printing the token -----------------
set +x
REMOTE="${WIKI_REMOTE:-}"
if [ -z "$REMOTE" ] && [ -n "${GITHUB_TOKEN:-}" ] && [ -n "${GITHUB_REPOSITORY:-}" ]; then
  REMOTE="https://x-access-token:${GITHUB_TOKEN}@github.com/${GITHUB_REPOSITORY}.wiki.git"
fi
[ -n "$REMOTE" ] || REMOTE="$DEFAULT_REMOTE"

# Mask the token (and any user:password part of the URL) in anything we print.
mask() {
  sed -E -e 's#(https?://)[^/@[:space:]]*@#\1***@#g' \
         -e "s#${GITHUB_TOKEN:-__no_token_set__}#***#g"
}

# --- generate the pages ----------------------------------------------------
"$PYTHON" "$HERE/gen_cli_reference.py" || die "gen_cli_reference.py failed"
[ -d "$PAGES" ] || die "pages folder missing: $PAGES"
ls "$PAGES"/*.md >/dev/null 2>&1 || die "no pages found in $PAGES"

# --- get a working copy of the wiki ---------------------------------------
WORK="$(mktemp -d)"
if [ "$DRY_RUN" -eq 0 ]; then
  trap 'rm -rf "$WORK"' EXIT
fi
CLONE="$WORK/wiki"

if [ "$DRY_RUN" -eq 1 ]; then
  echo "dry run: no clone, no push. Staging in $CLONE"
  git init -q "$CLONE" || die "git init failed"
else
  echo "cloning the wiki repository ..."
  if ! git clone -q "$REMOTE" "$CLONE" 2>&1 | mask; then :; fi
  [ -d "$CLONE/.git" ] || die "clone failed. Is the wiki enabled in the repository settings and its first page created in the web UI? Is the token valid?"
fi

# --- mirror pages/ into the clone -----------------------------------------
cd "$CLONE"

# Delete wiki pages (top-level .md files) that no longer exist in pages/.
shopt -s nullglob
for existing in ./*.md; do
  name="$(basename "$existing")"
  if [ ! -f "$PAGES/$name" ]; then
    echo "removing $name"
    rm -f "$existing"
  fi
done
# Add or update everything else.
for page in "$PAGES"/*.md; do
  cp "$page" "./$(basename "$page")" || die "could not copy $page"
done

git add -A || die "git add failed"
if git diff --cached --quiet; then
  echo "wiki is already up to date; nothing to commit"
  exit 0
fi

echo "changes to publish:"
git diff --cached --stat | cat

git -c "user.name=$AUTHOR_NAME" -c "user.email=$AUTHOR_EMAIL" \
    commit -q -m "$COMMIT_MESSAGE" || die "git commit failed"

if [ "$DRY_RUN" -eq 1 ]; then
  echo "dry run complete: committed locally in $CLONE (not pushed)"
  git log --oneline -1
  exit 0
fi

echo "pushing ..."
set +e
git push -q origin HEAD 2>&1 | mask
rc=${PIPESTATUS[0]}
set -e
[ "$rc" -eq 0 ] || die "git push failed (exit $rc)"
echo "wiki updated"
