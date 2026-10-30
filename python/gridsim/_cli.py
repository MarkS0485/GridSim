"""Run the gridsim command-line program and read its result document.

Every verb listed in WRITER_VERBS can write its whole result as one JSON document (schema "gridsim-cli-result/1",
manual chapter 19, "Result files"). This module runs the verb with ``--json <temporary file>``, reads the document back
and hands it over as a Result. Nothing is computed here: the numbers are the CLI's own, written in round-trip form, so
the floats Python reads are exactly the bits GridSim computed.

Only the Python standard library is used.
"""

import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

#: The result document's schema name, checked on every read.
SCHEMA = "gridsim-cli-result/1"

#: The verbs whose result the CLI writes through its shared result writer, by canonical name. A GridSim test keeps this
#: tuple equal to the CLI's own list (CliOutput.WriterVerbs), so a verb added there and not here, or here and not
#: there, fails the build. The other verbs have no complete --json output and are not wrapped (see the README).
WRITER_VERBS = (
    "acopf", "acopf-full", "convert", "cpf", "cycle", "dcopf", "dcpf", "dist3ph", "domains", "experimental", "fault",
    "freq", "grade", "hosting", "iec60909", "inertia-placement", "lodf", "losses", "n-1-1", "nminus1", "nminus2", "pf",
    "physics-check", "pmu-estimate", "pmu-ingest", "protection-check", "ptdf", "qsts", "restore", "scopf", "scuc", "taps", "validate",
    "zonal",
)

#: The CLI's alternative spellings of writer verbs, mapped to the canonical name (the name in the result document).
ALIASES = {"n-1": "nminus1", "n-2": "nminus2", "nminus1minus1": "n-1-1"}

#: The flags the wrapper itself owns: it names the result file, so a caller may not.
_RESULT_FLAGS = ("--json", "--csv", "--out")

#: JSON has no NaN or infinity, so the CLI writes those three values as these strings (manual chapter 19).
_NAMED_FLOATS = {"NaN": math.nan, "Infinity": math.inf, "-Infinity": -math.inf}


class GridSimError(Exception):
    """The CLI could not be found, or a run produced no result document (a usage error, a case that does not load, a
    solve that does not converge). ``exit_code``, ``stdout`` and ``stderr`` hold what the run printed (None when the CLI
    never ran)."""

    def __init__(self, message, exit_code=None, stdout=None, stderr=None, command=None):
        super().__init__(message)
        self.exit_code = exit_code
        self.stdout = stdout
        self.stderr = stderr
        self.command = command


class Result:
    """One verb's result document.

    ``verb`` (canonical name), ``args`` (the arguments the CLI saw), ``tool`` ("gridsim <version>+<commit>"),
    ``exit_code`` (the run's exit code, also written inside the document: a finished result whose verdict fails, such as
    NOT SECURE or INFEASIBLE, is still a result, with a non-zero exit code), ``summary`` (a dict of the verb's scalar
    values, in the CLI's order), ``tables`` (a dict of table name to a list of row dicts), ``stdout`` and ``stderr``
    (what the run printed) and ``document`` (the parsed JSON as read, before the named floats were turned into floats).
    """

    def __init__(self, document, stdout="", stderr=""):
        if not isinstance(document, dict) or document.get("schema") != SCHEMA:
            raise GridSimError(f"not a {SCHEMA} document (schema {document.get('schema') if isinstance(document, dict) else None!r})")
        for key in ("verb", "args", "exitCode", "summary", "tables"):
            if key not in document:
                raise GridSimError(f"the {SCHEMA} document has no '{key}'")
        self.document = document
        self.verb = document["verb"]
        self.args = list(document["args"])
        self.tool = document.get("tool")
        self.exit_code = document["exitCode"]
        self.summary = {k: _floats(v) for k, v in document["summary"].items()}
        self.tables = {name: [{c: _floats(v) for c, v in row.items()} for row in rows]
                       for name, rows in document["tables"].items()}
        self.stdout = stdout
        self.stderr = stderr

    @property
    def ok(self):
        """True when the run's exit code is 0."""
        return self.exit_code == 0

    def table(self, name):
        """The rows of table ``name``; KeyError naming the tables there are when it has none of that name."""
        if name not in self.tables:
            raise KeyError(f"{self.verb} has no table '{name}' (tables: {', '.join(self.tables) or 'none'})")
        return self.tables[name]

    def __repr__(self):
        return f"Result(verb={self.verb!r}, exit_code={self.exit_code}, summary keys={len(self.summary)}, tables={list(self.tables)})"


def _floats(value):
    """The CLI's named floats ("NaN", "Infinity", "-Infinity") as Python floats; every other value as it is."""
    if isinstance(value, str) and value in _NAMED_FLOATS:
        return _NAMED_FLOATS[value]
    return value


def canonical(verb):
    """The canonical name of a writer verb (aliases resolved); ValueError for a verb that writes no result document."""
    v = str(verb).lower()
    v = ALIASES.get(v, v)
    if v not in WRITER_VERBS:
        raise ValueError(f"'{verb}' does not write a result document; the verbs that do: {', '.join(WRITER_VERBS)}")
    return v


def find_cli(cli=None):
    """The command that runs gridsim, as a list.

    In order: ``cli`` (a path, or a list such as ``["dotnet", "gridsim.dll"]``); the ``GRIDSIM_CLI`` environment variable
    (a path); ``gridsim`` on the PATH; a Release, then Debug, build in the GridSim checkout this package sits in.
    GridSimError when none is there.
    """
    if cli is not None:
        cmd = [str(c) for c in cli] if isinstance(cli, (list, tuple)) else [str(cli)]
        if not cmd or not (shutil.which(cmd[0]) or Path(cmd[0]).is_file()):
            raise GridSimError(f"the gridsim CLI given ({cmd[0] if cmd else 'nothing'}) does not exist")
        return cmd
    env = os.environ.get("GRIDSIM_CLI")
    if env:
        if not Path(env).is_file():
            raise GridSimError(f"GRIDSIM_CLI names {env}, which does not exist")
        return [env]
    on_path = shutil.which("gridsim")
    if on_path:
        return [on_path]
    exe = "gridsim.exe" if sys.platform == "win32" else "gridsim"
    repo = Path(__file__).resolve().parents[2]
    for config in ("Release", "Debug"):
        candidate = repo / "src" / "GridSim.Cli" / "bin" / config / "net10.0" / exe
        if candidate.is_file():
            return [str(candidate)]
    raise GridSimError("the gridsim CLI was not found: pass cli=, set GRIDSIM_CLI, put gridsim on the PATH, "
                       "or build src/GridSim.Cli in this checkout")


class GridSim:
    """A handle on the gridsim CLI.

    ``GridSim(cli=None, cwd=None, timeout=None, env=None)``: ``cli`` as for find_cli; ``cwd`` the folder runs start in
    (default outputs such as ``results/`` land there); ``timeout`` in seconds per run (None: no limit); ``env`` extra
    environment variables for the runs.

    ``run(verb, *args)`` runs one writer verb and returns its Result. Each writer verb also has a method of its own,
    named with underscores (``pf``, ``dcopf``, ``n_1_1``, ``physics_check``, ...), taking the same arguments.
    """

    def __init__(self, cli=None, cwd=None, timeout=None, env=None):
        self.command = find_cli(cli)
        self.cwd = None if cwd is None else str(cwd)
        self.timeout = timeout
        self.env = None if env is None else {**os.environ, **{str(k): str(v) for k, v in env.items()}}

    def run(self, verb, *args):
        """Run ``gridsim <verb> <args...> --json <temporary file>`` and return the Result.

        ``args`` are passed as they are (each turned into a string), so a case name or file, flags and their values all
        go in order: ``run("dcopf", "case30", "--no-limits")``. ``--json``, ``--csv`` and ``--out`` are refused: the
        wrapper names the result file. GridSimError when the run writes no result document; its message carries the
        exit code and the CLI's last stderr line.
        """
        name = canonical(verb)
        argv = [str(a) for a in args]
        for flag in _RESULT_FLAGS:
            if flag in argv:
                raise ValueError(f"{flag} is the wrapper's to give: it writes the result to a temporary file and reads it back")
        with tempfile.TemporaryDirectory(prefix="gridsim-py-") as tmp:
            out = os.path.join(tmp, "result.json")
            cmd = self.command + [name, *argv, "--json", out]
            try:
                p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                                   cwd=self.cwd, timeout=self.timeout, env=self.env)
            except subprocess.TimeoutExpired as ex:
                raise GridSimError(f"gridsim {name} did not finish within {self.timeout} s", command=cmd) from ex
            if not os.path.isfile(out):
                last = next((line for line in reversed(p.stderr.splitlines()) if line.strip()), "")
                raise GridSimError(f"gridsim {name} wrote no result (exit {p.returncode}): {last}",
                                   p.returncode, p.stdout, p.stderr, cmd)
            with open(out, encoding="utf-8") as f:
                document = json.load(f)
        result = Result(document, p.stdout, p.stderr)
        if result.verb != name:
            raise GridSimError(f"asked for {name}, the document is for {result.verb}", p.returncode, p.stdout, p.stderr, cmd)
        return result


def _verb_method(verb):
    def method(self, *args):
        return self.run(verb, *args)
    method.__name__ = verb.replace("-", "_")
    method.__doc__ = f"Run ``gridsim {verb} <args...>`` and return its Result (see GridSim.run)."
    return method


for _verb in WRITER_VERBS:
    setattr(GridSim, _verb.replace("-", "_"), _verb_method(_verb))
del _verb


def run(verb, *args, cli=None, cwd=None, timeout=None):
    """``GridSim(cli, cwd, timeout).run(verb, *args)``: one run without keeping a handle."""
    return GridSim(cli=cli, cwd=cwd, timeout=timeout).run(verb, *args)
