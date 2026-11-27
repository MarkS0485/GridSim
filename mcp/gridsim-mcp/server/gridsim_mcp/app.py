"""The MCP server: every registered tool, wrapped so each call goes through registry.dispatch."""

import inspect
import os
from typing import Annotated, Any, Optional

from mcp.server.mcpserver import MCPServer
from mcp_types import CallToolResult, TextContent, ToolAnnotations
from pydantic import BaseModel, ConfigDict

from . import prompts, registry, resources
from . import tools_case, tools_check, tools_plan, tools_study, tools_teach  # noqa: F401
from .host import SERVER_VERSION, Pool

INSTRUCTIONS = """GridSim through MCP: a deterministic power-system solver on the user's machine, offline.
Load, import or build a case (case_load, import_model, case_new + case_edit add; line_parameters derives line R, X, B
from geometry), solve it (solve_pf, study, study_case_run), check it (physics_check after every edit), compare
(diff_cases, diff_results), explain it (explain_solve, break_it), report it (report_render), and keep the evidence: every answer carries an evidence block (build
and build tag, case hash, result hash, record entry). Cite it. Pass a short 'reasoning' with each call so the study
record shows why. GridSim's solver is verified; cases built here are not validated. Only allowlisted verbs are
available (see gridsim://plugin/allowlist)."""


class Evidence(BaseModel):
    model_config = ConfigDict(extra="allow")
    tool: str
    status: str
    session: str
    build: Optional[dict[str, Any]] = None
    build_tag: Optional[str] = None
    case: Optional[str] = None
    case_hash: Optional[str] = None
    result: Optional[str] = None
    result_hash: Optional[str] = None
    record_seq: Optional[int] = None
    record_hash: Optional[str] = None
    note: str


class ToolOutput(BaseModel):
    """GridSim's own JSON under 'document' (exactly as GridSim returned it), and the MCP evidence block."""
    model_config = ConfigDict(extra="allow")
    document: Any = None
    evidence: Evidence


def _wrapper(pool, spec):
    sig = inspect.signature(spec.fn)
    params = list(sig.parameters.values())[1:]   # drop ctx
    params += [
        inspect.Parameter("session", inspect.Parameter.KEYWORD_ONLY, default="default", annotation=str),
        inspect.Parameter("reasoning", inspect.Parameter.KEYWORD_ONLY, default=None, annotation=Optional[str]),
    ]
    names = [p.name for p in params]

    def call(**kwargs) -> Annotated[CallToolResult, ToolOutput]:
        session = kwargs.pop("session", "default") or "default"
        reasoning = kwargs.pop("reasoning", None)
        args = {k: v for k, v in kwargs.items() if k in names}
        structured, text, is_error, _ = registry.dispatch(pool, spec.name, args, session, reasoning)
        if is_error:
            return CallToolResult(content=[TextContent(type="text", text=text)], is_error=True)
        return CallToolResult(content=[TextContent(type="text", text=text)], structured_content=structured)

    call.__name__ = spec.name
    call.__doc__ = spec.description
    call.__signature__ = sig.replace(parameters=params, return_annotation=Annotated[CallToolResult, ToolOutput])
    call.__annotations__ = {p.name: p.annotation for p in params}
    call.__annotations__["return"] = Annotated[CallToolResult, ToolOutput]
    return call


def build_server(pool=None):
    registry.load_maintainer_modules()
    for k in [k for k in os.environ if k.upper().startswith("OTEL_")]:
        del os.environ[k]
    os.environ["OTEL_SDK_DISABLED"] = "true"
    pool = pool or Pool()
    server = MCPServer(name="gridsim", title="GridSim", version=SERVER_VERSION, instructions=INSTRUCTIONS,
                       log_level="WARNING")
    # The SDK installs an OpenTelemetry middleware by default (a no-op without an exporter). Remove it anyway.
    server.middleware[:] = [m for m in server.middleware if type(m).__name__ != "OpenTelemetryMiddleware"]
    descriptions = (" Optional arguments on every tool: session (default \"default\") and reasoning (why you make this "
                    "call; it goes into the hash-chained study record).")
    for spec in registry.enabled_tools().values():
        server.add_tool(
            _wrapper(pool, spec), name=spec.name, title=spec.title, description=spec.description + descriptions,
            annotations=ToolAnnotations(title=spec.title, read_only_hint=spec.read_only,
                                        destructive_hint=spec.destructive, idempotent_hint=spec.idempotent,
                                        open_world_hint=False),
            structured_output=True)
    resources.register(server, pool)
    prompts.register(server)
    server.pool = pool
    return server
