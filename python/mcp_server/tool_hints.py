"""
MCP tool annotations (readOnlyHint / destructiveHint / idempotentHint).

Clients use these to decide what they may run without asking, so a wrong "read-only" is a safety
bug: it could let a writing tool run unattended. The rules are therefore conservative:

  1. A function may declare its own:  func.__mcp_annotations__ = {"readOnlyHint": True, ...}
  2. Otherwise a tool is read-only only when its name starts with a verb that cannot write
     (READ_ONLY_PREFIXES) or is listed in READ_ONLY_NAMES.
  3. Destructive only for names that clearly delete or replace (DESTRUCTIVE_PREFIXES).
  4. Everything else gets no hint, which MCP treats as "may write".

tool_names.lock records the outcome for every tool; a change in a hint fails the lock test, so it
is always reviewed.
"""

READ_ONLY_PREFIXES = (
    "get_", "list_", "read_", "query_", "search_", "describe", "inspect",
    "verify_actions", "versions", "servers", "status", "ping", "catalog",
    "settings_get_", "pathmap_", "api_types", "api_describe", "action_catalog", "action_describe",
    "ribbon_catalog", "dialog_history", "profile_get", "profile_list", "profile_suggest",
    "parts_db_get", "parts_db_query", "parts_db_count", "parts_db_list",
    "live_query_", "live_read_", "live_objects_read", "live_verify_page", "live_symbol_catalog",
    "live_routing_catalog", "script_list", "script_status", "script_hello", "pdf_inspect",
    "dwg_inspect", "scratch_project_list", "list_extensions", "tools_search", "tools_describe",
)

READ_ONLY_NAMES = frozenset()

DESTRUCTIVE_PREFIXES = (
    "delete_", "remove_", "discard", "purge", "clean_", "restore_", "compress_", "unregister_",
    "app_shutdown", "app_restart", "scratch_project_discard", "lock_unlock",
)


def _bare(tool_name: str, prefix: str) -> str:
    return tool_name[len(prefix):] if prefix and tool_name.startswith(prefix) else tool_name


def annotations_for(tool_name: str, func=None, prefix: str = "") -> dict:
    """The annotation dict for one tool ({} when nothing can be said safely)."""
    declared = getattr(func, "__mcp_annotations__", None)
    if declared is not None:
        return dict(declared)
    name = _bare(tool_name, prefix)
    if name in READ_ONLY_NAMES or name.startswith(READ_ONLY_PREFIXES):
        return {"readOnlyHint": True}
    if name.startswith(DESTRUCTIVE_PREFIXES):
        return {"readOnlyHint": False, "destructiveHint": True}
    return {}


def to_mcp(hints: dict):
    """The mcp.types.ToolAnnotations object, or None."""
    if not hints:
        return None
    from mcp.types import ToolAnnotations
    return ToolAnnotations(**hints)


def lock_tag(hints: dict) -> str:
    if hints.get("readOnlyHint"):
        return "read"
    if hints.get("destructiveHint"):
        return "destructive"
    return "-"
