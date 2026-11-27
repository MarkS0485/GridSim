"""Start the GridSim MCP server with the network guard installed (used by the end-to-end test)."""

import runpy
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import netguard  # noqa: E402

netguard.install()
server_main = Path(__file__).resolve().parents[1] / "server" / "main.py"
sys.argv = [str(server_main)]
runpy.run_path(str(server_main), run_name="__main__")
