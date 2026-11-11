"""Library scripts as tools, and the script host's MCP resources."""
import asyncio
import json
import os

from mcp.server.fastmcp import FastMCP

from mcp_server import server
import script_tools
from api.actions import scripthost
from tool_registry import ToolRegistry


def _lib(tmp_path, monkeypatch, prefix="acme_"):
    lib = tmp_path / "lib"
    lib.mkdir()
    (lib / "count.cs").write_text(
        "// @tool count_pages\n// @desc Count pages.\n// @arg page string optional Page filter\n"
        "// @arg limit int required Max pages\nreturn 1;", encoding="utf-8")
    (lib / "fix.cs").write_text("// @tool fix_things\n// @writes true\nreturn 1;", encoding="utf-8")
    (lib / "_template.cs").write_text("// @tool not_me\n", encoding="utf-8")
    monkeypatch.setattr(scripthost, "_extra_script_dirs", {})
    monkeypatch.setattr(scripthost, "_dir_prefix", {})
    scripthost.register_script_dir(str(lib), prefix=prefix)
    return lib


def test_scripts_are_indexed_but_not_published_by_default(tmp_path, monkeypatch):
    _lib(tmp_path, monkeypatch)
    monkeypatch.delenv("EPLAN_MCP_SCRIPT_TOOLS", raising=False)
    app, reg = FastMCP("t"), ToolRegistry()
    names = script_tools.register(app, reg, "discovery", server.register_actions)
    assert "acme_count_pages" in reg.hidden_names()           # searchable in discovery mode
    assert "acme_count_pages" in names and "acme_fix_things" in names and "acme_not_me" not in names
    assert "acme_count_pages" not in app._tool_manager._tools
    assert "eplan_project_info" in names                      # the public library is there too


def test_published_tools_mirror_the_header(tmp_path, monkeypatch):
    _lib(tmp_path, monkeypatch)
    monkeypatch.setenv("EPLAN_MCP_SCRIPT_TOOLS", "publish")
    app, reg = FastMCP("t"), ToolRegistry()
    script_tools.register(app, reg, "full", server.register_actions)
    count = app._tool_manager._tools["acme_count_pages"]
    props = count.parameters["properties"]
    assert set(props) >= {"page", "limit", "project", "dry_run"}
    assert "limit" in count.parameters.get("required", [])
    assert count.annotations.readOnlyHint is True             # no @writes: read-only
    assert app._tool_manager._tools["acme_fix_things"].annotations is None   # writes: no hint


def test_published_tool_calls_script_run_with_the_args(tmp_path, monkeypatch):
    lib = _lib(tmp_path, monkeypatch)
    monkeypatch.setenv("EPLAN_MCP_SCRIPT_TOOLS", "publish")
    seen = {}
    monkeypatch.setattr(scripthost, "script_run", lambda **kw: seen.update(kw) or {"ok": True})
    app, reg = FastMCP("t"), ToolRegistry()
    script_tools.register(app, reg, "full", server.register_actions)
    out = asyncio.run(app.call_tool("acme_count_pages", {"limit": 3, "project": "x.elk"}))
    assert seen["script"] == os.path.join(str(lib), "count.cs")
    assert seen["args"] == {"limit": 3} and seen["project"] == "x.elk"


def test_a_script_never_shadows_a_real_tool(tmp_path, monkeypatch):
    lib = _lib(tmp_path, monkeypatch, prefix="eplan_")
    (lib / "ping.cs").write_text("// @tool ping\nreturn 1;", encoding="utf-8")
    monkeypatch.setenv("EPLAN_MCP_EXTENSIONS", "")
    monkeypatch.setenv("EPLAN_MCP_SCRIPT_TOOLS", "publish")
    app, _, _ = server.build_app(mode="full")
    assert app._tool_manager._tools["eplan_ping"].fn.__module__ != "script_tools"


def test_resources(tmp_path, monkeypatch):
    _lib(tmp_path, monkeypatch)
    monkeypatch.setattr(scripthost, "RUNS_DIR", str(tmp_path / "runs"))
    run = tmp_path / "runs" / "20261110" / "abc123"
    run.mkdir(parents=True)
    (run / "result.json").write_text('{"ok": true}', encoding="utf-8")
    app = FastMCP("t")
    uris = script_tools.register_resources(app)
    assert "eplan-help://scripting-guide" in uris

    def read(uri):
        contents = asyncio.run(app.read_resource(uri))
        return contents[0].content if hasattr(contents[0], "content") else contents[0].text

    assert "fix_things" in read("eplan-script://library/fix_things")
    assert json.loads(read("eplan-script://runs/abc123/result"))["ok"] is True
    assert "ctx.Budget.Check()" in read("eplan-help://scripting-guide")


def test_run_file_reader_rejects_path_tricks(tmp_path, monkeypatch):
    monkeypatch.setattr(scripthost, "RUNS_DIR", str(tmp_path))
    for bad in ("../x", "a/b", "..", "c:x"):
        try:
            scripthost.read_run_file(bad, "result.json")
        except ValueError:
            continue
        raise AssertionError("accepted " + bad)
