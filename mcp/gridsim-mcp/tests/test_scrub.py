"""F13: crossval_single shows no machine path: the interpreter, the program, the workspace, the home folder and any
other absolute path become placeholders."""

import os
from pathlib import Path

from gridsim_mcp import tools_compare


class _Cfg:
    def __init__(self, ws, command):
        self.workspace = Path(ws)
        self.command = command


def test_crossval_output_is_scrubbed(tmp_path):
    drive = "C" + ":"
    py = drive + "\\Program Files\\Python312\\python.exe"
    gs = str(tmp_path / "bin" / "gridsim.exe")
    cfg = _Cfg(tmp_path / "ws", [gs])
    home = str(Path.home())
    doc = {"producer": {"command": "gridsim crossval case9 --out x", "output": [
        f"crossval: running {py} on solve-export.json",
        f"interpreter {py.replace(chr(92), '/')}",
        f"export written to {tmp_path / 'ws' / 'out' / 'solve-export.json'}",
        f"program {gs}",
        f"cache in {home}{os.sep}cache",
        "other " + drive + "\\tools\\x.py and /" + "usr/bin/python3 and /" + "home/someone/x",
        "relative tools/crossval/crossval.py stays"]},
        "case": str(tmp_path / "ws" / "cases" / "a.m")}
    out = tools_compare.scrub(doc, cfg, {"python": py, "crossval_dir": None, "harness": None})
    lines = out["producer"]["output"]
    assert lines[0] == "crossval: running <python> on solve-export.json"
    assert lines[1] == "interpreter <python>"
    assert lines[2].startswith("export written to <workspace>")
    assert lines[3] == "program <gridsim>"
    assert lines[4].startswith("cache in <home>")
    assert lines[5] == "other <path> and <path> and <path>"
    assert lines[6] == "relative tools/crossval/crossval.py stays"
    assert out["case"].startswith("<workspace>")
    flat = repr(out)
    assert drive + "\\" not in flat and str(tmp_path) not in flat and home not in flat
