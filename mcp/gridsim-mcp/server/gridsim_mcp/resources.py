"""Resources: the plugin's own texts, the manual section of each allowlisted verb (rendered by the installed GridSim
from its embedded manual, else the plugin's docs snapshot), GridSim's served doc sections (docs_index) and the v2
schemas of the installed GridSim when they sit beside it. Nothing outside the GridSim installation and this plugin is
ever read."""

import re
import subprocess
from pathlib import Path

from . import allowlist, docs_index, record, tuning

EVIDENCE_RULES = """GridSim MCP evidence rules

1. GridSim's solver core is verified (to machine precision against independent references). A case built or edited
   here, and any study on it, is NOT validated against measurements. Say "verified solver, unvalidated case".
2. Every claim cites the evidence block of the call it rests on: build version and build tag ([RELEASE] or
   [UNSUPPORTED BUILD]), result hash and case hash.
3. A number moved only if its result hash changed; use diff_results or diff_cases to show what moved.
4. Run physics_check after every build or edit. A FAIL names the law broken; do not explain it away.
5. Exit 3 is a verdict (NOT SECURE, DIVERGED, ...), still a result: report it, do not retry with looser settings
   unless asked.
6. Cases built from scratch are synthetic. Label inferred values as inferred; never name or locate real facilities.
7. Paid-standard tables (cable sizing) are empty by design: report OUT OF SCOPE, never fill them from memory.
"""

RECORD_FORMAT = f"""The study record ({record.SCHEMA})

JSON Lines. Line 1 is the header: schema, server version, build (version, sha256, build_status), licensed (yes/no;
the licence itself is never recorded), protocol, hash. Every later line is one tool call: seq, tool, arguments,
reasoning (the AI's stated reason, if given), status (ok, error, refused), exitCode, case (c1, ...), caseHash
(SHA-256 of the case's load hash plus its edit log), resultHash (SHA-256 of GridSim's document as canonical JSON),
result (r1, ...), build, message, prevHash, hash. A tuned call also carries tuned ("tuned: key=value, ...") and, for a
standard value, offStandard (the standards no longer complied with); untuned calls carry neither.

hash = SHA-256 of the line's canonical JSON (sorted keys, no spaces, ASCII) without "hash". prevHash is the previous
line's hash (the header's for the first call). No clock time, session name or rpc host token is hashed, so the same
calls on the same build give the same hashes: record_replay checks exactly that.
Not recorded: record_export and record_replay. Recorded but not re-run on replay: session_open, session_close.
"""


STARTER_CASES = """GridSim starter cases (bus and branch counts as GridSim loads them; no case here needs a licence: the
licence is needed only above 1,500 buses)

Built-in names (case_load <name>):
| case    | what                               | buses | branches | use                                   |
|---------|------------------------------------|-------|----------|---------------------------------------|
| case9   | IEEE 9-bus (WSCC), with machine H  | 9     | 9        | first lessons; inertia (WSCC H, MBase) |
| case14  | IEEE 14-bus                        | 14    | 20       | N-1, reactive limits                  |
| case30  | IEEE 30-bus                        | 30    | 41       | contingency, OPF                      |
| case24  | IEEE RTS-79 24-bus                 | 24    | 38       | reliability, N-1                      |
| cigre   | CIGRE MV benchmark (TF C6.04.02)   | 15    | 14       | distribution, hosting                 |
| case57  | IEEE 57-bus                        | 57    | 80       | larger N-1                            |
| case118 | IEEE 118-bus                       | 118   | 186      | larger studies                        |
| case145 | IEEE 145-bus (dynamic)             | 145   | 453      | dynamics                              |
| case300 | IEEE 300-bus                       | 300   | 411      | largest with a dense Jacobian export  |

Distribution feeders (MATPOWER files under GridSim's data/matpower/dist; copy one into the workspace, then
case_load it): case18 (18 buses), case22 (22), case33bw (33), case69 (69), case85 (85), case533mt_hi (533). Each is
radial (branches = buses - 1).

Unbalanced feeders with published answers (GridSim's data/dist3ph): the IEEE 4-bus transformer variants and IEEE 123
with published voltages and currents (run_verb dist3ph).

Shipped GB cases are offered to static verbs only (see gridsim://plugin/allowlist); the full GB model is above 1,500
buses and needs a licence.
"""

CONSTRUCT_METHOD = """GridSim construction method (pending review)

The full method for constructing a plausible network from public sources is under review and not shipped yet. Until
it is, keep to these rules:
1. Build in rounds; after every round run case_validate, solve_pf and physics_check, and fix a failed gate first.
2. No fitting to the solver: never adjust a value to make a solve converge or a study pass. A modest overload may be
   real: report it.
3. Every value is cited (document id, version, section) or tagged Synthetic or CuratedPlaceholder with [VERIFY].
   Derive line R, X and B with line_parameters from a stated geometry.
4. Inferred elements are labelled as inferred and kept to the minimum the network needs.
5. Never name, locate or attribute real facilities or customers. Demand is aggregate.
"""

def find_gridsim_root(command, depth=6):
    """The folder above the GridSim program that holds schemas/v2 (a checkout or a release folder), or None."""
    p = Path(command[-1] if len(command) > 1 else command[0]).resolve().parent
    for _ in range(depth):
        if (p / "schemas" / "v2").is_dir():
            return p
        if p.parent == p:
            break
        p = p.parent
    return None


_SCHEMA_NAME = re.compile(r"^[A-Za-z0-9_.-]+\.json$")


def schema_names(root):
    if root is None:
        return []
    base = root / "schemas" / "v2"
    names = [f.name for f in sorted(base.glob("*.json"))]
    names += ["kinds/" + f.name for f in sorted((base / "kinds").glob("*.json"))] if (base / "kinds").is_dir() else []
    return names


def schema_text(root, name):
    if root is None:
        return "The installed GridSim has no schemas/v2 folder beside it."
    parts = name.split("/")
    if len(parts) > 2 or not _SCHEMA_NAME.match(parts[-1]) or (len(parts) == 2 and parts[0] != "kinds"):
        return f"No schema '{name}'."
    f = root / "schemas" / "v2" / Path(*parts)
    return f.read_text(encoding="utf-8") if f.is_file() else f"No schema '{name}'."


def verb_help(config, verb):
    """The help of an allowlisted verb: the installed GridSim's own ``gridsim help <verb>`` (its embedded ch. 19, so it
    matches the program), filtered; when the program gives none, the ch. 19 section from the checkout (when identical)
    or the plugin's snapshot."""
    v = allowlist.canonical(verb)
    if v in allowlist.REFUSED_VERBS or v not in allowlist.ALLOWED_VERBS:
        return f"'{v}' {allowlist.NOT_AVAILABLE}. " + (allowlist.REFUSED_VERBS.get(v) or allowlist.ALLOWLIST_POINTER)
    try:
        p = subprocess.run(config.command + ["help", v], capture_output=True, text=True, encoding="utf-8",
                           errors="replace", env=config.env, timeout=60, stdin=subprocess.DEVNULL)
        if p.returncode == 0 and p.stdout.strip():
            return docs_index.filter_text(p.stdout)
        failure = (p.stderr or p.stdout).strip()
    except (OSError, subprocess.TimeoutExpired) as ex:
        failure = str(ex)
    text = docs_index.verb_help(docs_index.find_docs_root(config.command), v)
    if text is not None:
        return "From GridSim manual ch. 19 CLI reference (the plugin's copy).\n\n" + text
    return f"gridsim help {v} failed: {failure}"


def register(server, pool):
    @server.resource("gridsim://plugin/allowlist", name="allowlist", title="Verb allowlist and refusals",
                     mime_type="text/plain")
    def _allowlist() -> str:
        return allowlist.allowlist_text()

    @server.resource("gridsim://plugin/tunables", name="tunables", title="Values a user may tune",
                     mime_type="application/json")
    def _tunables() -> str:
        return tuning.TUNABLES_FILE.read_text(encoding="utf-8")

    @server.resource("gridsim://plugin/evidence-rules", name="evidence-rules", title="How to cite GridSim evidence",
                     mime_type="text/plain")
    def _rules() -> str:
        return EVIDENCE_RULES

    @server.resource("gridsim://plugin/record-format", name="record-format", title="Study record format",
                     mime_type="text/plain")
    def _record() -> str:
        return RECORD_FORMAT

    @server.resource("gridsim://schemas/index", name="schemas", title="GridSim v2 schemas (index)",
                     mime_type="text/plain")
    def _schemas() -> str:
        names = schema_names(find_gridsim_root(pool.config.command))
        return "\n".join(names) + "\n" if names else "The installed GridSim has no schemas/v2 folder beside it.\n"

    @server.resource("gridsim://schemas/v2/{name}", name="schema", title="A GridSim v2 schema",
                     mime_type="application/json")
    def _schema(name: str) -> str:
        return schema_text(find_gridsim_root(pool.config.command), name)

    def docs_root():
        return docs_index.find_docs_root(pool.config.command)

    @server.resource("gridsim://docs/troubleshooting", name="troubleshooting", mime_type="text/markdown",
                     title="Troubleshooting: index of messages (manual ch. 30)")
    def _troubleshooting() -> str:
        return docs_index.troubleshooting_index(docs_root())

    @server.resource("gridsim://docs/per-unit", name="per-unit", mime_type="text/markdown",
                     title="Per-unit conventions (technical ch. 2)")
    def _per_unit() -> str:
        return docs_index.per_unit(docs_root())

    @server.resource("gridsim://docs/grounded-values", name="grounded-values", mime_type="text/markdown",
                     title="Provenance tags and grounded parameter values (technical ch. 13)")
    def _grounded() -> str:
        return docs_index.grounded_values(docs_root())

    @server.resource("gridsim://docs/dynamic-models", name="dynamic-models", mime_type="text/markdown",
                     title="Standard dynamic-model library: model list (technical ch. 24)")
    def _dynamic() -> str:
        return docs_index.dynamic_models(docs_root())

    @server.resource("gridsim://plugin/starter-cases", name="starter-cases", mime_type="text/markdown",
                     title="Starter cases: bus counts and licence need")
    def _starters() -> str:
        return STARTER_CASES

    @server.resource("gridsim://plugin/construct-method", name="construct-method", mime_type="text/plain",
                     title="Construction method (pending review)")
    def _construct() -> str:
        return CONSTRUCT_METHOD

    @server.resource("gridsim://manual/verb/{verb}", name="verb-manual", title="Manual section of a verb",
                     mime_type="text/markdown")
    def _verb(verb: str) -> str:
        return verb_help(pool.config, verb)
