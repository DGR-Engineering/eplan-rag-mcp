"""
The published tool surface is a contract: skills, docs and other repos call tools by name. This
pins every tool name and its safety hint to tool_names.lock, so a rename, a removal or a changed
read-only hint is always a deliberate, reviewed change.

When this fails on purpose: run `python tools/gen_tool_lock.py`, review the diff, and for a rename
or removal add the old name to tool_aliases.json.
"""
import json
import os

from mcp_server import server

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCK = os.path.join(ROOT, "tool_names.lock")
ALIASES = os.path.join(ROOT, "tool_aliases.json")


def _locked():
    with open(LOCK, encoding="utf-8") as f:
        return dict(line.rstrip("\n").split("\t") for line in f if line.strip() and not line.startswith("#"))


def _published(monkeypatch):
    monkeypatch.setenv("EPLAN_MCP_EXTENSIONS", "")
    import tool_hints
    app, _, _ = server.build_app(mode="full")
    out = {}
    for name, tool in app._tool_manager._tools.items():
        ann = tool.annotations.model_dump(exclude_none=True) if tool.annotations else {}
        out[name] = tool_hints.lock_tag(ann)
    return out


def test_every_published_tool_is_in_the_lock_with_the_same_hint(monkeypatch):
    locked, published = _locked(), _published(monkeypatch)
    added = sorted(set(published) - set(locked))
    assert not added, "New tools not in tool_names.lock (run tools/gen_tool_lock.py): %s" % added
    changed = sorted(n for n in published if locked.get(n) != published[n])
    assert not changed, "Safety hint changed (review, then regenerate the lock): %s" % (
        [(n, locked.get(n), published[n]) for n in changed])


def test_no_locked_tool_disappears_without_an_alias(monkeypatch):
    locked, published = _locked(), _published(monkeypatch)
    aliases = json.load(open(ALIASES, encoding="utf-8")) if os.path.exists(ALIASES) else {}
    gone = sorted(n for n in set(locked) - set(published) if n not in aliases)
    assert not gone, "Tools removed or renamed without an alias in tool_aliases.json: %s" % gone


def test_script_tools_carry_the_expected_hints(monkeypatch):
    published = _published(monkeypatch)
    for name in ("eplan_script_list", "eplan_script_status", "eplan_script_hello"):
        assert published[name] == "read"
    assert published["eplan_script_run"] == "-"          # may write: never auto-approved
    assert published["eplan_scratch_project_discard"] == "destructive"
