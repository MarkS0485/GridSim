import os
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
PLUGIN = HERE.parent
SERVER = PLUGIN / "server"
sys.path.insert(0, str(SERVER))
sys.path.insert(0, str(HERE))

import netguard  # noqa: E402

netguard.install()


def find_cli():
    env = os.environ.get("GRIDSIM_MCP_CLI")
    if env and Path(env).is_file():
        return env
    exe = "gridsim.exe" if sys.platform == "win32" else "gridsim"
    repo = PLUGIN.parents[1]
    found = [p for c in ("Release", "Debug") for p in [repo / "src" / "GridSim.Cli" / "bin" / c / "net10.0" / exe]
             if p.is_file()]
    if not found:
        return None
    return str(max(found, key=lambda p: (p.with_name("gridsim.dll").stat().st_mtime
                                         if p.with_name("gridsim.dll").is_file() else p.stat().st_mtime)))


CLI = find_cli()
needs_cli = pytest.mark.skipif(CLI is None, reason="no built GridSim CLI (set GRIDSIM_MCP_CLI)")


@pytest.fixture(scope="module")
def workspace(tmp_path_factory):
    return tmp_path_factory.mktemp("ws")


@pytest.fixture(scope="module")
def pool(workspace):
    if CLI is None:
        pytest.skip("no built GridSim CLI")
    from gridsim_mcp import registry  # noqa: F401
    from gridsim_mcp import tools_case, tools_check, tools_plan, tools_study, tools_teach  # noqa: F401
    from gridsim_mcp.host import Config, Pool
    p = Pool(Config(cli=CLI, workspace=str(workspace), licence_file="", environ=dict(os.environ)))
    yield p
    p.close_all()


@pytest.fixture
def call(pool):
    from gridsim_mcp.registry import dispatch

    def run(tool, session="t", reasoning=None, **args):
        structured, text, is_error, entry = dispatch(pool, tool, args, session, reasoning)
        return structured, text, is_error, entry
    return run
