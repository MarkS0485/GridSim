# tools/wiki

Source for the public GridSim GitHub wiki (https://github.com/MarkS0485/GridSim/wiki). The wiki is a short front
door; the full documentation is on the project site, and the wiki links into it.

## Layout

| Path | What it is |
|---|---|
| `src/*.md` | Hand-written page templates (Home, Install, Getting-started, FAQ, Troubleshooting, `_Sidebar`, `_Footer`). Edit these. Placeholders: `{{SITE}}` (site base URL), `{{REPO}}`, `{{RELEASE}}`. |
| `gen_cli_reference.py` | Renders `src/` into `pages/` and generates `pages/CLI-quick-reference.md` from `docs/manual/0019 CLI reference.md`. |
| `pages/*.md` | The generated wiki pages, in GitHub wiki naming (spaces become hyphens). Do not edit by hand. |
| `push_wiki.ps1` | Publish `pages/` to the wiki from Windows PowerShell 5.1. |
| `push_wiki.sh` | Publish `pages/` to the wiki from bash (used by CI). |

The site base URL, repository URL and release URL are constants at the top of `gen_cli_reference.py`. Change one
there and re-run the generator to retarget every link.

## Regenerate

```
python tools/wiki/gen_cli_reference.py            # write pages/
python tools/wiki/gen_cli_reference.py --check    # exit 1 if pages/ is out of date (CI drift check)
```

Python 3.12, standard library only. The generator fails loud: a verb in the CLI chapter's index with no matching
section, zero verbs parsed, an unresolved placeholder or a non-ASCII character each stop it with a message and a
non-zero exit code. The CLI chapter is compiled into the binary, so the generated quick reference cannot drift from
the tool as long as `--check` runs in CI.

## How sync works

The wiki-sync workflow runs `push_wiki.sh` on push to main. The script:

1. clones the wiki repository (`<repo>.wiki.git`) into a temporary folder,
2. runs `gen_cli_reference.py`,
3. mirrors `pages/` into the clone: adds and updates pages, deletes top-level wiki pages that are no longer in `pages/`,
4. commits as "GridSim wiki sync" with the message `Sync wiki from tools/wiki`, only if something changed,
5. pushes.

The remote comes from `WIKI_REMOTE`, or is built from `GITHUB_TOKEN` and `GITHUB_REPOSITORY` in the
`x-access-token` form. The token is never echoed. `push_wiki.sh --dry-run` clones and pushes nothing: it stages into
a local temporary folder (an empty `git init` stands in for the clone), commits there and prints the folder.

`push_wiki.ps1` does the same by hand from Windows, with your own git credentials (or `WIKI_REMOTE`).

## One-off setup

The wiki must be enabled in the repository settings (Settings, Features, Wikis) and its first page created once in
the web UI. Until then the `.wiki.git` repository does not exist and both scripts fail at the clone step with a
message saying so.
