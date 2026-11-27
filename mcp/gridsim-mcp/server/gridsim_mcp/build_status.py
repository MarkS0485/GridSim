"""Build identity and build status: RELEASE or UNSUPPORTED BUILD.

GridSim reports its own build status (``build_status`` in the rpc ``hello``, ``buildStatus`` in every ``--json`` result
document). A GridSim build is RELEASE when it is a CI release build (the SBOM hash injected by the release workflow, a
clean source tree) and its stamped commit is a full git commit, so the binary names the exact release checkout. The
compliance verdict (``gridsim compliance verify``) is reported beside it and does not decide it.

The server prefers GridSim's value and keeps its own check as the cross-check. The check passes only when both hold:
the commit the program names (``gridsim --version`` prints ``gridsim <version>+<commit>``) is listed in
``RELEASE-COMMITS`` beside this module (the published release commits), and the program's SHA-256 is listed in
``RELEASE-SHA256SUMS`` beside it (the published binaries). Both lists are empty until releases are listed. The build
stamp is metadata, not integrity: a self-built binary of a release commit names that commit, but its bytes are not a
published binary, so the SHA-256 is what tells the two apart.

A build is RELEASE only when GridSim reports RELEASE and the server's check passes. When the two disagree the build is
UNSUPPORTED BUILD, and the reasons say which side said what. A GridSim too old to report a status is judged by the
server's check alone. The check is fully offline: it hashes a local file and runs the local program.
"""

import hashlib
import json
import subprocess
from pathlib import Path

RELEASE = "RELEASE"
UNSUPPORTED = "UNSUPPORTED BUILD"

SUMS_FILE = Path(__file__).with_name("RELEASE-SHA256SUMS")
COMMITS_FILE = Path(__file__).with_name("RELEASE-COMMITS")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def program_file(command):
    """The file that carries the build: for a framework-dependent build the apphost (gridsim.exe) is the same for every
    build, so the managed gridsim.dll beside it is hashed; otherwise the program itself."""
    exe = Path(command[-1] if len(command) > 1 and command[0].lower().endswith(("dotnet", "dotnet.exe")) else command[0])
    if exe.suffix.lower() == ".dll":
        return exe
    dll = exe.with_name("gridsim.dll")
    return dll if dll.is_file() else exe


def shipped_sums(path=SUMS_FILE):
    """The SHA-256 values listed in a SHA256SUMS file (``<hex>  <name>`` lines; ``#`` starts a comment)."""
    sums = set()
    try:
        text = Path(path).read_text(encoding="utf-8")
    except OSError:
        return sums
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        h = line.split()[0].lower()
        if len(h) == 64 and all(c in "0123456789abcdef" for c in h):
            sums.add(h)
    return sums


def _is_commit(value):
    return len(value) in (40, 64) and all(c in "0123456789abcdef" for c in value)


def shipped_commits(path=COMMITS_FILE):
    """The published release commits listed in a RELEASE-COMMITS file (``<commit> [tag]`` lines; ``#`` starts a
    comment). Only full commits (40 or 64 hex digits) count."""
    commits = set()
    try:
        text = Path(path).read_text(encoding="utf-8")
    except OSError:
        return commits
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        c = line.split()[0].lower()
        if _is_commit(c):
            commits.add(c)
    return commits


def commit_of(version_line):
    """The full commit in a ``gridsim <version>+<commit>`` line, or None."""
    if not version_line or "+" not in version_line:
        return None
    c = version_line.rsplit("+", 1)[1].strip().lower()
    return c if _is_commit(c) else None


def _run(command, args, env, timeout=60, stdin_text=None):
    try:
        p = subprocess.run(list(command) + list(args), capture_output=True, text=True, encoding="utf-8",
                           errors="replace", env=env, timeout=timeout,
                           input=stdin_text, stdin=subprocess.DEVNULL if stdin_text is None else None)
        return p.returncode, p.stdout, p.stderr
    except (OSError, subprocess.TimeoutExpired) as ex:
        return None, "", str(ex)


def reported_status(command, env=None):
    """The build status GridSim reports about itself in its rpc ``hello``, or None when it reports none (an older
    build) or the hello cannot be read."""
    code, out, _ = _run(command, ["rpc"], env, stdin_text='{"id":1,"op":"hello"}\n')
    for line in out.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            value = json.loads(line).get("result", {}).get("build_status")
        except (ValueError, AttributeError):
            return None
        return value if value in (RELEASE, UNSUPPORTED) else None
    return None


def combine(reported, check):
    """The build status from GridSim's own report and the server's check, with the reasons for a disagreement."""
    if reported is None:
        return check, ["GridSim did not report a build status; the server's check alone decides"]
    if reported == check:
        return reported, []
    return UNSUPPORTED, [f"GridSim reports {reported} but the server's check gives {check}"]


def document_status(document):
    """The ``buildStatus`` a gridsim-cli-result/1 document carries, or None."""
    if isinstance(document, dict) and document.get("schema") == "gridsim-cli-result/1":
        value = document.get("buildStatus")
        return value if isinstance(value, str) else None
    return None


def identify(command, env=None, sums_file=SUMS_FILE, commits_file=COMMITS_FILE):
    """The build identity and status of the GridSim program ``command`` (a list)."""
    code, out, _ = _run(command, ["--version"], env)
    version = out.strip().splitlines()[0] if code == 0 and out.strip() else "unknown"
    commit = commit_of(version)
    pfile = program_file(command)
    try:
        digest = sha256_file(pfile)
    except OSError:
        digest = None
    ccode, cout, _ = _run(command, ["compliance", "verify"], env)
    attestation = {}
    for line in cout.splitlines():
        parts = line.strip().split(None, 1)
        if len(parts) == 2 and parts[0] in ("version", "commit", "verdict"):
            attestation[parts[0]] = parts[1].strip()
        elif line.strip().startswith("build hash"):
            attestation["build_hash"] = line.strip()[len("build hash"):].strip()
        elif line.strip().startswith("release build"):
            attestation["release_build"] = line.strip()[len("release build"):].strip()
    commit_listed = commit is not None and commit in shipped_commits(commits_file)
    program_listed = digest is not None and digest in shipped_sums(sums_file)
    check = RELEASE if commit_listed and program_listed else UNSUPPORTED
    reported = reported_status(command, env)
    status, reasons = combine(reported, check)
    if commit is None:
        reasons.append("'gridsim --version' names no full commit")
    elif not commit_listed:
        reasons.append("the program's commit is not in the list of published release commits")
    if digest is None:
        reasons.append("the program file could not be read to hash it")
    elif not program_listed:
        reasons.append("the program's SHA-256 is not in the list of published release binaries")
    return {
        "version": version,
        "commit": commit,
        "commit_listed": commit_listed,
        "program_file": pfile.name,
        "sha256": digest,
        "program_listed": program_listed,
        "compliance_exit": ccode,
        "attestation": attestation,
        "build_status": status,
        "gridsim_reported": reported,
        "server_check": check,
        "reasons": reasons,
    }


def tag(identity):
    """The stamp put on every answer: ``[RELEASE]`` or ``[UNSUPPORTED BUILD]``."""
    return f"[{identity['build_status']}]"

