# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "mcp==2.3.0",
# ]
#
# [tool.uv]
# exclude-newer = "2026-11-20T00:00:00Z"
# ///
"""The GridSim MCP server (stdio). Started by the AI tool; talks MCP on stdin/stdout and nothing else."""

import atexit
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gridsim_mcp.app import build_server  # noqa: E402


def main():
    server = build_server()
    atexit.register(server.pool.close_all)
    server.run("stdio")


if __name__ == "__main__":
    main()
