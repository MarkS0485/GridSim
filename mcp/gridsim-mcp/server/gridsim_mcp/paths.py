"""Path rules: every file the MCP reads or writes lies inside the workspace (or, for reading, the folder the AI tool
started the server in). Private and recorded-data locations are refused by name."""

import os
import re
from pathlib import Path

from .allowlist import Refused, forbidden_path, refusal

_PATHLIKE = re.compile(r"[\\/]|\.(m|raw|rawx|json|dgs|xml|csv|uct|dyr|gss|zip|jsonl|md|html)$", re.IGNORECASE)


def looks_like_path(token):
    return bool(_PATHLIKE.search(str(token)))


def _inside(path, root):
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def roots(config, write=False):
    r = [config.workspace]
    if not write:
        r.append(Path(os.getcwd()).resolve())
    return r


def resolve(config, token, write=False):
    """The absolute path a token names, after the rules; Refused when it lies outside the allowed folders."""
    t = str(token)
    if forbidden_path(t):
        raise Refused(refusal(f"the location '{t}'"))
    if t.startswith("\\\\") or t.startswith("//"):
        raise Refused(f"REFUSED: '{t}' is a network path; use a local file inside the workspace.")
    p = Path(t)
    p = (config.workspace / p).resolve() if not p.is_absolute() else p.resolve()
    allowed = roots(config, write)
    if not any(_inside(p, r) for r in allowed):
        where = "the workspace" if write else "the workspace or the project folder"
        raise Refused(f"REFUSED: '{t}' lies outside {where}; copy the file into the workspace first.")
    return p


def host_arg(config, token, write=False):
    """The argument to pass the host (which runs in the workspace): relative to the workspace when inside it."""
    p = resolve(config, token, write)
    try:
        return p.relative_to(config.workspace).as_posix()
    except ValueError:
        return str(p)
