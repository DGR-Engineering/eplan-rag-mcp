"""
Library scripts as MCP tools, and the script host's MCP resources.

Every .cs file in a script library with a `// @tool name` header can become its own tool. They are
always indexed in the tool registry (so discovery mode's eplan_tools_search finds them), but only
PUBLISHED as separate tools when EPLAN_MCP_SCRIPT_TOOLS=publish: dozens of per-script tools would
otherwise crowd every request. Without publishing, run any of them with
eplan_script_run(script="<name>") after eplan_script_list.

Header -> tool:
    // @tool   name          tool name (prefixed with the library's prefix, "eplan_" for the public one)
    // @desc   ...           description
    // @arg    name type [optional|required] [default=x] description
    // @writes true|false    false gives the tool readOnlyHint; true leaves it "may write"
"""

import inspect
import os
import types

from api.actions import scripthost

_TYPES = {"string": str, "int": int, "number": float, "bool": bool, "list": list, "enum": str}


def _parse_arg(spec: str) -> dict:
    words = spec.split()
    arg = {"name": words[0], "type": "string", "required": False, "default": None, "desc": ""}
    if len(words) > 1:
        t = words[1]
        arg["type"] = "enum" if t.lower().startswith("enum(") else t.lower()
        arg["choices"] = t[5:-1].split(",") if arg["type"] == "enum" else None
    rest = []
    for w in words[2:]:
        if w.lower() == "required":
            arg["required"] = True
        elif w.lower() == "optional":
            arg["required"] = False
        elif w.lower().startswith("default="):
            arg["default"] = w[8:]
        else:
            rest.append(w)
    arg["desc"] = " ".join(rest)
    return arg


def make_tool(path: str, header: dict):
    """A plain function whose signature mirrors the script's @arg lines."""
    args = [_parse_arg(a) for a in header.get("args", [])]
    params = []
    for a in args:
        py_type = _TYPES.get(a["type"], str)
        default = inspect.Parameter.empty if a["required"] else a["default"]
        params.append(inspect.Parameter(a["name"], inspect.Parameter.KEYWORD_ONLY,
                                        default=default, annotation=py_type if a["required"] else py_type | None))
    for name, typ, default in (("project", str | None, None), ("dry_run", bool, False),
                               ("budget_seconds", float, 120.0), ("wait_seconds", float, 60.0)):
        params.append(inspect.Parameter(name, inspect.Parameter.KEYWORD_ONLY, default=default, annotation=typ))
    arg_names = [a["name"] for a in args]

    def tool(**kwargs) -> dict:
        script_args = {k: kwargs[k] for k in arg_names if kwargs.get(k) is not None}
        return scripthost.script_run(script=path, args=script_args, project=kwargs.get("project"),
                                     dry_run=kwargs.get("dry_run", False),
                                     budget_seconds=kwargs.get("budget_seconds", 120.0),
                                     wait_seconds=kwargs.get("wait_seconds", 60.0))

    tool.__signature__ = inspect.Signature(params, return_annotation=dict)
    tool.__annotations__ = {p.name: p.annotation for p in params} | {"return": dict}
    lines = [header.get("desc") or f"Run the {header.get('tool')} script."]
    if args:
        lines.append("\nArgs:")
        lines += [f"    {a['name']}: {a['desc']}" + (f" One of: {', '.join(a['choices'])}." if a.get("choices") else "")
                  for a in args]
    lines.append("    project: .elk path or project name; default the focused or only open project.")
    lines.append(f"\nScript: {path}")
    tool.__doc__ = "\n".join(lines)
    tool.__name__ = header.get("tool") or "script"
    tool.__mcp_annotations__ = {} if header.get("writes") else {"readOnlyHint": True}
    return tool


def library():
    """(prefix, path, header) for every script with a @tool header, public library first."""
    out = []
    for d in scripthost._script_dirs():
        prefix = scripthost._prefix_for_dir(d)
        for f in sorted(os.listdir(d)):
            if f.endswith(".cs") and not f.startswith("_"):
                path = os.path.join(d, f)
                header = scripthost._parse_header(scripthost._read(path))
                if header.get("tool"):
                    out.append((prefix, path, header))
    return out


def register(app, registry, mode, register_fn):
    """Index (and, on opt-in, publish) one tool per library script."""
    publish = os.environ.get("EPLAN_MCP_SCRIPT_TOOLS", "").lower() == "publish"
    taken = set(app._tool_manager._tools) if app is not None else set()
    by_prefix = {}
    registered = []
    for prefix, path, header in library():
        name = header["tool"]
        ns = by_prefix.setdefault(prefix, types.SimpleNamespace(__all__=[]))
        if f"{prefix}{name}" in taken or name in ns.__all__:
            continue          # a real tool of that name wins; never shadow one
        setattr(ns, name, make_tool(path, header))
        ns.__all__.append(name)
        registered.append(f"{prefix}{name}")
    # Full mode has no search tool, so an unpublished tool there would be unreachable: index only
    # in discovery mode (searchable) or when publishing. script_list / script_run always work.
    if publish or mode == "discovery":
        for prefix, ns in by_prefix.items():
            register_fn(ns, prefix=prefix, app=app, registry=registry, mode=mode, publish=publish)
    return registered


def register_resources(app):
    """Script sources, run results and the scripting guide as MCP resources: large data costs no
    context until the agent asks for it."""

    @app.resource("eplan-script://library/{name}", mime_type="text/x-csharp",
                  description="Source of a library script (see eplan_script_list).")
    def library_source(name: str) -> str:
        path, source = scripthost._resolve_script(name)
        return source

    @app.resource("eplan-script://runs/{run_id}/result", mime_type="application/json",
                  description="Full result of a script run, including large results kept out of the reply.")
    def run_result(run_id: str) -> str:
        return scripthost.read_run_file(run_id, "result.json")

    @app.resource("eplan-script://runs/{run_id}/data", mime_type="application/json",
                  description="The large-result file of a run (result_file in the reply).")
    def run_data(run_id: str) -> str:
        return scripthost.read_run_file(run_id, "result_data.json")

    @app.resource("eplan-help://scripting-guide", mime_type="text/markdown",
                  description="How to write EPLAN scripts for the script host: ctx, headers, writes, helpers.")
    def scripting_guide() -> str:
        with open(os.path.join(scripthost.DOTNET_DIR, "SCRIPTING.md"), encoding="utf-8") as f:
            return f.read()

    return ["eplan-script://library/{name}", "eplan-script://runs/{run_id}/result",
            "eplan-script://runs/{run_id}/data", "eplan-help://scripting-guide"]
