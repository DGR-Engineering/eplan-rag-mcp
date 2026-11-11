"""
The extension API: the only contract between this server and an extension pack.

An extension is a folder of *.py files named in EPLAN_MCP_EXTENSIONS. Each file declares

    TOOL_PREFIX = "acme_"           # its tools become acme_<name>
    EXT_API = (1, 1)                # the range of EXT_API_VERSION it was written for
    __all__ = ["my_tool", ...]      # plain functions; docstring + type hints become the tool

and uses only what is below. The server refuses an extension whose EXT_API range does not
include EXT_API_VERSION, with a plain message, instead of loading it half-working. Anything not
in this module is internal and may change in any release.

Bump EXT_API_VERSION when a function here changes incompatibly; add functions freely.
"""

import os

EXT_API_VERSION = 1


def compatible(declared) -> bool:
    """True when an extension's EXT_API (min, max) - or a single int - includes this version.
    An extension that declares nothing is assumed to be written for version 1."""
    if declared is None:
        declared = (1, 1)
    if isinstance(declared, int):
        declared = (declared, declared)
    lo, hi = declared
    return lo <= EXT_API_VERSION <= hi


def manager():
    """The connected EPLAN connection manager, or raise RuntimeError with a plain message."""
    from api.actions._base import _get_connected_manager
    m, error = _get_connected_manager()
    if error:
        raise RuntimeError(error.get("message") or "Not connected to EPLAN.")
    return m


def run_action(action: str, direct: bool = False, timeout: float = None) -> dict:
    """Run one EPLAN action string. direct=True skips the quiet-mode script wrapper (needed for
    .NET 8 add-in actions that lock objects, S511176)."""
    m = manager()
    kwargs = {"timeout": timeout} if timeout else {}
    return m.execute_action(action, quiet_mode=not direct, **kwargs)


def run_script(code: str = None, script: str = None, args: dict = None, project: str = None,
               **options) -> dict:
    """Compile and run a C# script in EPLAN through the script host (see scripthost.script_run
    for every option). Returns the result envelope."""
    from api.actions import scripthost
    return scripthost.script_run(code=code, script=script, args=args, project=project, **options)


def register_script_dir(path: str, helpers: list = None) -> None:
    """Add a folder of .cs scripts to the script library. `helpers` are .cs files compiled into
    every script from that folder (an extension's own helper library)."""
    from api.actions import scripthost
    scripthost.register_script_dir(path, helpers=helpers)


def scratch_root() -> str:
    """Where scratch clones live (EPLAN_MCP_SCRATCH)."""
    from api.actions import fixtures
    return fixtures.SCRATCH_ROOT


def config_dir(extension_file: str) -> str:
    """The folder of an extension module, for its own presets and manifests."""
    return os.path.dirname(os.path.abspath(extension_file))
