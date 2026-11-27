"""Rebuild on demand (maintainer option, off by default): a user-owned GridSim build with allowlisted compile-time values
changed.

The tool never touches the source checkout. It copies the files a CLI build needs into a build folder outside the
checkout, applies the edits there, and proves before building that

- every edit is to an allowlisted NUMERICS constant (``rebuild_allowlist`` of ``tunables.json``), in its own file, with
  a value inside the safe range, and the constant's declaration appears exactly once;
- no edited file is a guard file, and every guard file in the copy is byte-identical to the source checkout, whose guard
  files are themselves unmodified against its own git HEAD;
- every other copied file is byte-identical too, and each edited file differs from the source only at the constant.

Guard files are the files that enforce the data-age horizon, the clock override, the data-root checks, the licence bus
limit and bundled-network rule, the telemetry guard and the build-status stamp, plus the build logic itself (the
.props, .targets and .csproj files). They are found by name and by grepping the source for their enforcement symbols
(``GUARD_PATTERN``), so a guard moved to a new file is still found.

Then it runs ``dotnet build src/GridSim.Cli -c Release`` in the copy, with the release-stamp properties forced empty and
the environment stripped of build-property overrides, and checks that the built program reports UNSUPPORTED BUILD. A
rebuilt GridSim is the user's own build: every result it gives is stamped [UNSUPPORTED BUILD].
"""

import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Optional, Union

from . import build_status, tuning
from .allowlist import Refused
from .host import McpError, _unset_config
from .registry import Outcome, tool

ENV_ENABLE = "GRIDSIM_MCP_ENABLE_REBUILD"
ENV_DIR = "GRIDSIM_MCP_REBUILD_DIR"

#: What a CLI build reads, relative to the checkout root (folders end in '/').
COPY_SET = ("src/", "data/", "schemas/", "tools/crossval/", "docs/manual/0019 CLI reference.md",
            "Directory.Build.props", "Directory.Build.targets", "global.json", ".editorconfig", "LICENSE.md",
            "THIRD-PARTY-NOTICES.md", "GridSim.slnx")

#: Guard files by name (relative to the checkout root; folders end in '/').
GUARD_NAMED = ("Directory.Build.props", "Directory.Build.targets", "global.json", "data/bundled.sha256",
               "src/GridSim.Cli/Program.cs", "src/GridSim.Core/Licensing/", "src/GridSim.Core/Diagnostics/SupplyChain/",
               "src/GridSim.Gda/")

#: Build logic is a guard too: a property or target could change what is stamped or compiled.
GUARD_SUFFIXES = (".csproj", ".props", ".targets")

#: The enforcement symbols: horizon and clock (ForwardInferenceGuard, UtcTime, --utc-now), data roots and quarantine,
#: licence limit and bundled networks, telemetry, and the build-status stamp.
GUARD_PATTERN = re.compile(
    r"ForwardInferenceGuard|HorizonHours|\bUtcTime\b|utc-now|GdaRoot|GdaPaths|\bLake[A-Z]\w*|lake[-_]conformance|"
    r"PmuLiveQuarantine|QuarantineLiveStore|LicenceGate|\bEntitlement\b|BundledNetworks|LicenceKey|LicencePublicKey|"
    r"TelemetryGuard|ComplianceAttestation|\bBuildStatus\b|BuildProvenance|GridSimSbomSha256|GridSimSourceTree")

#: Environment variables a build must not inherit (MSBuild reads every variable as a property).
_STRIP_ENV = re.compile(r"^(gridsim|gitcommit|github_|ci$|tf_build$|build_|msbuild|nuget_|dotnet_cli_home$)",
                        re.IGNORECASE)


def _truthy(value):
    return not _unset_config(value) and value.strip().lower() in ("1", "true", "yes", "on")


def enabled(environ=None):
    env = os.environ if environ is None else environ
    return _truthy(env.get(ENV_ENABLE))


def _refuse(msg):
    raise Refused(f"REFUSED (rebuild): {msg}. Nothing was built.")


def _sha(path):
    return build_status.sha256_file(path)


def _git(source, *args):
    try:
        p = subprocess.run(["git", "-C", str(source), *args], capture_output=True, stdin=subprocess.DEVNULL,
                           timeout=120)
    except (OSError, subprocess.TimeoutExpired) as ex:
        _refuse(f"git could not be run on the source checkout ({type(ex).__name__})")
    return p.returncode, p.stdout


def tracked_files(source):
    code, out = _git(source, "ls-files", "-z")
    if code != 0:
        _refuse("the source is not a git checkout")
    return [f for f in out.decode("utf-8").split("\0") if f]


def _in_set(rel, entries):
    return any(rel == e or (e.endswith("/") and rel.startswith(e)) for e in entries)


def guard_files(source, files):
    """The guard files among ``files`` (paths relative to ``source``), sorted."""
    found = set()
    for rel in files:
        if _in_set(rel, GUARD_NAMED) or (rel.startswith("src/") and rel.endswith(GUARD_SUFFIXES)):
            found.add(rel)
        elif rel.startswith("src/") and rel.endswith(".cs"):
            try:
                text = (Path(source) / rel).read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            if GUARD_PATTERN.search(text):
                found.add(rel)
    return sorted(found)


def check_edits(edits):
    """The edits, checked against the rebuild allowlist: [(entry, value)]. Refused on any problem."""
    allow = {r["symbol"]: r for r in tuning.load().get("rebuild_allowlist", [])}
    if not isinstance(edits, list):
        _refuse("edits is a list of {symbol, value} items")
    out, seen = [], set()
    for e in edits:
        if not isinstance(e, dict) or set(e) - {"symbol", "file", "value"} or "symbol" not in e or "value" not in e:
            _refuse("each edit is {symbol, value} (and optionally file); nothing else")
        sym = str(e["symbol"])
        if sym not in allow:
            _refuse(f"'{sym}' is not on the rebuild list (only a few NUMERICS constants are; physics never)")
        entry = allow[sym]
        if "file" in e and str(e["file"]).replace("\\", "/") != entry["file"]:
            _refuse(f"'{sym}' lives in {entry['file']}; an edit may not touch {e['file']}")
        if sym in seen:
            _refuse(f"'{sym}' is edited twice")
        seen.add(sym)
        v = e["value"]
        if isinstance(v, bool) or not isinstance(v, (int, float)) or not (entry["min"] <= float(v) <= entry["max"]):
            _refuse(f"{sym} = {v!r} is outside its safe range ({entry['min']} to {entry['max']} {entry['unit']})")
        out.append((entry, float(v)))
    return out


def _edited_text(original, entry, value):
    """The file text with the constant's value replaced; refused unless the declaration occurs exactly once."""
    rx = re.compile(entry["pattern"])
    hits = list(rx.finditer(original))
    if len(hits) != 1:
        _refuse(f"the declaration of {entry['symbol']} occurs {len(hits)} times in {entry['file']} (expected once); "
                "the source does not match this plugin's rebuild list")
    m = hits[0]
    a, b = m.span("value")
    return original[:a] + repr(value) + original[b:]


def _inside(child, parent):
    try:
        Path(child).resolve().relative_to(Path(parent).resolve())
        return True
    except ValueError:
        return False


def build_env(build_dir, base=None):
    env = {k: v for k, v in (os.environ if base is None else base).items() if not _STRIP_ENV.match(k)}
    env["GIT_CEILING_DIRECTORIES"] = str(Path(build_dir).resolve().parent)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    return env


BUILD_ARGS = ["dotnet", "build", "src/GridSim.Cli", "-c", "Release", "-nologo",
              "-p:GridSimSbomSha256=", "-p:GridSimSourceTree="]


def prepare(source, edits, build_root):
    """Copy, edit and verify. Returns the report dict (and the build folder). Refused on any guard problem."""
    source = Path(source).resolve()
    if not (source / "src" / "GridSim.Cli" / "GridSim.Cli.csproj").is_file():
        _refuse("the source folder is not a GridSim checkout (no src/GridSim.Cli/GridSim.Cli.csproj)")
    checked = check_edits(edits)
    files = tracked_files(source)
    guards = guard_files(source, files)
    for entry, _ in checked:
        if entry["file"] in guards:
            _refuse(f"{entry['file']} is a guard file; it may not be edited")
    # The source's own guard files must be as committed: a guard changed in the checkout is never built.
    code, out = _git(source, "status", "--porcelain", "--", *guards)
    if code != 0:
        _refuse("git could not report the state of the guard files")
    if out.strip():
        changed = [ln[3:] for ln in out.decode("utf-8", "replace").splitlines() if ln.strip()]
        _refuse(f"guard file(s) differ from the checkout's own commit: {', '.join(changed[:8])}")
    code, head = _git(source, "rev-parse", "HEAD")
    head = head.decode().strip() if code == 0 else "unknown"

    build_root = Path(build_root).resolve()
    if _inside(build_root, source) or _inside(source, build_root):
        _refuse("the build folder must lie outside the source checkout (set GRIDSIM_MCP_REBUILD_DIR)")
    build_root.mkdir(parents=True, exist_ok=True)
    n = 1 + len([p for p in build_root.iterdir() if p.is_dir()])
    build_dir = build_root / f"{n:04d}-{head[:12]}"
    build_dir.mkdir(parents=True, exist_ok=False)

    copy = [f for f in files if _in_set(f, COPY_SET) and (source / f).is_file()]
    for rel in copy:
        dst = build_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source / rel, dst)

    edited = {}
    for entry, value in checked:
        p = build_dir / entry["file"]
        with open(p, encoding="utf-8", newline="") as f:
            original = f.read()
        new = _edited_text(original, entry, value)
        with open(p, "w", encoding="utf-8", newline="") as f:
            f.write(new)
        edited[entry["file"]] = (original, new, entry["symbol"], value)

    # Proof: guard files identical; every other file identical; edited files differ only at the constant.
    guard_rows, bad = [], []
    for rel in guards:
        if rel not in copy:
            continue
        same = _sha(source / rel) == _sha(build_dir / rel)
        guard_rows.append({"file": rel, "sha256": _sha(build_dir / rel), "identical": same})
        if not same:
            bad.append(rel)
    others_differ = []
    for rel in copy:
        if rel in edited or rel in guards:
            continue
        if _sha(source / rel) != _sha(build_dir / rel):
            others_differ.append(rel)
    for rel, (original, new, sym, value) in edited.items():
        with open(source / rel, encoding="utf-8", newline="") as f:
            src_text = f.read()
        with open(build_dir / rel, encoding="utf-8", newline="") as f:
            got = f.read()
        if src_text != original or got != new:
            others_differ.append(rel)
    if bad or others_differ:
        shutil.rmtree(build_dir, ignore_errors=True)
        _refuse("the copy is not the source plus the allowed edits: "
                + ", ".join((bad + others_differ)[:8]))
    report = {
        "source_commit": head,
        "files_copied": len(copy),
        "edits": [{"symbol": sym, "file": rel, "value": value} for rel, (_, _, sym, value) in sorted(edited.items())],
        "guard_files": guard_rows,
        "guard_files_identical": True,
        "other_files_identical": True,
    }
    return report, build_dir


def _built_program(build_dir):
    exe = "gridsim.exe" if sys.platform == "win32" else "gridsim"
    return build_dir / "src" / "GridSim.Cli" / "bin" / "Release" / "net10.0" / exe


def verify_built(program, env):
    """What the built program says about itself: version, GridSim's reported status, the server's identity."""
    ident = build_status.identify([str(program)], env)
    return {"version": ident["version"], "gridsim_reported": ident["gridsim_reported"],
            "build_status": ident["build_status"], "sha256": ident["sha256"]}


def _settings_or_refuse(config):
    if not enabled():
        raise Refused("REFUSED: rebuild_on_demand is not available through the GridSim MCP server.")
    root = os.environ.get(ENV_DIR)
    return Path(root).resolve() if not _unset_config(root) else config.workspace / ".gridsim-mcp" / "rebuild"


def _shown(path, config, build_root):
    p = Path(path).resolve()
    for base, mark in ((config.workspace, "<workspace>"), (build_root, "<rebuild>")):
        try:
            return f"{mark}/{p.relative_to(Path(base).resolve()).as_posix()}"
        except ValueError:
            pass
    return "<path>"


@tool("rebuild_on_demand", "Build a user-owned GridSim with allowlisted constants changed", read_only=False,
      destructive=False, idempotent=False, group="rebuild")
def rebuild_on_demand(ctx, source: str, edits: Optional[list[dict[str, Union[str, float, int]]]] = None,
                      dry_run: bool = True, timeout_s: float = 7200.0):
    """Maintainer option. Copy a GridSim source checkout to a build folder outside it, change allowlisted compile-time
    constants there (edits: [{symbol, value}], symbols from tunables_list 'rebuild_allowlist'; NUMERICS only, physics
    never), prove every guard file and every other file byte-identical to the checkout, then (dry_run false) run
    `dotnet build src/GridSim.Cli -c Release` in the copy and check the program reports UNSUPPORTED BUILD. Refused,
    with nothing built, if any check fails. The checkout itself is never changed. A cold build can take over half an
    hour."""
    config = ctx.pool.config
    build_root = _settings_or_refuse(config)
    src = Path(source)
    if not src.is_absolute():
        src = config.workspace / src
    t0 = time.monotonic()
    report, build_dir = prepare(src, edits or [], build_root)
    report["build_dir"] = _shown(build_dir, config, build_root)
    report["command"] = " ".join(BUILD_ARGS)
    report["prepare_s"] = round(time.monotonic() - t0, 1)
    if dry_run:
        report["built"] = False
        report["note"] = "Dry run: the copy is prepared and verified; nothing was built."
        return Outcome(document=report, store=False, summary=f"rebuild dry run: {report['files_copied']} files, "
                       f"{len(report['edits'])} edit(s), guard files identical")
    env = build_env(build_dir)
    t1 = time.monotonic()
    try:
        p = subprocess.run(BUILD_ARGS, cwd=str(build_dir), env=env, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", stdin=subprocess.DEVNULL, timeout=timeout_s)
        code, tail = p.returncode, (p.stdout + p.stderr).strip().splitlines()[-15:]
    except subprocess.TimeoutExpired:
        code, tail = None, [f"the build did not finish within {timeout_s} s"]
    except OSError as ex:
        raise McpError(f"dotnet could not be run: {ex}") from ex
    report["build_s"] = round(time.monotonic() - t1, 1)
    report["build_exit"] = code
    report["build_output"] = [_scrub_line(ln, build_dir, src) for ln in tail]
    program = _built_program(build_dir)
    if code != 0 or not program.is_file():
        report["built"] = False
        return Outcome(document=report, store=False, status="ok", summary=f"rebuild failed (exit {code})")
    report["built"] = True
    report["program"] = _shown(program, config, build_root)
    report["identity"] = verify_built(program, dict(os.environ))
    status_ok = (report["identity"]["build_status"] == build_status.UNSUPPORTED
                 and report["identity"]["gridsim_reported"] in (None, build_status.UNSUPPORTED))
    report["reports_unsupported"] = status_ok
    if not status_ok:
        try:
            program.unlink()
        except OSError:
            pass
        report["note"] = "The rebuilt program did not report UNSUPPORTED BUILD; its program file was removed."
        return Outcome(document=report, store=False, summary="rebuild REFUSED: the build did not report UNSUPPORTED")
    report["note"] = ("A user-owned build: every result it gives is stamped [UNSUPPORTED BUILD]. Point the plugin's "
                      "GridSim program at it to use it.")
    return Outcome(document=report, store=False,
                   summary=f"rebuild done in {report['build_s']} s: [{build_status.UNSUPPORTED}]")


def _scrub_line(line, build_dir, source):
    for base, mark in ((build_dir, "<build>"), (source, "<source>")):
        for form in {str(base), str(base).replace("\\", "/")}:
            line = line.replace(form, mark)
    return re.sub(r"(?<![A-Za-z0-9<])[A-Za-z]:[\\/][^\s\"'<>|,;)]*", "<path>", line)
