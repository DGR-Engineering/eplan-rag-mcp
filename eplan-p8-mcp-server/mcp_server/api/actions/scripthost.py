"""
Script host - run typed C# scripts inside EPLAN.

A script is ordinary C# against the EPLAN API (`using Eplan.EplApi.DataModel;` works), either a
full class with `public static object Run(ScriptContext ctx)` or just a statement body. The host
(dotnet/ in this repo) compiles it with EPLAN's own Roslyn, checks it, runs it in a collectible
load context and unloads it, so a new script needs no build and no EPLAN restart.

Two runtimes run the SAME scripts:

    addin      the EplanMcp_RunScript action of the EplanMcp.Host.AddIn add-in, started on the
               direct Remote Client path (the quiet-mode wrapper path cannot lock: S511176)
    bootstrap  a reflection-only script run with ExecuteScript that loads the host from bytes;
               needs no registration and no restart

Both need the EPLAN API developer license (the host is unsigned). `runtime="auto"` uses the
add-in when it answers EplanMcp_Hello, else the bootstrap.

Writes (D7): ad-hoc scripts may write only when the TARGET project is a scratch clone made by
scratch_project_create (it leaves a marker file), and a writing script may touch only ctx.Project. A real project needs a SAVED script,
allow_real_project=True, and confirm=<the confirm_code a compile_only run of the same script, args
and helpers returned>; take a backup first (eplan_backup_project). This is a guard against mistakes, not a sandbox: a script is C# running in EPLAN.

Runs are jobs. script_run waits up to wait_seconds and returns the result, or a run_id to follow
with script_status (progress, heartbeat) and stop with script_cancel. A script stops only where it
calls ctx.Budget.Check(): EPLAN cannot abort a running script.
"""

import json
import os
import threading
import time
import uuid

from ._base import _get_connected_manager

PROTOCOL = 1

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO_ROOT = os.path.abspath(os.path.join(_HERE, "..", "..", "..", ".."))
DOTNET_DIR = os.path.join(_REPO_ROOT, "dotnet")

# Where the built host lives (EplanMcp.Host.AddIn output: DLLs + eplanmcp-manifest.txt).
HOST_DIR = os.environ.get("EPLAN_MCP_HOST_DIR") or os.path.join(DOTNET_DIR, "artifacts", "host")

# Per-user run folders: request, result, progress, heartbeat, cancel. Never inside a checkout.
# NOT under %LOCALAPPDATA% / %APPDATA%: measured on EPLAN 2027.0.3, an ExecuteScript file path
# written in the long form C:\Users\<user>\AppData\... is silently ignored - the call "succeeds"
# and nothing runs ("The name of the script file is missing (ScriptFile)" in the system
# messages). The user profile itself, ProgramData and the 8.3-form TEMP all work.
RUNS_DIR = os.environ.get("EPLAN_MCP_RUNS_DIR") or os.path.join(os.path.expanduser("~"), ".eplan-mcp", "runs")

# Script libraries: the public one here, plus any folders an extension adds (folder -> helper
# .cs files compiled into every script from that folder), and each folder's tool-name prefix.
_extra_script_dirs = {}
_dir_prefix = {}
PUBLIC_PREFIX = "eplan_"

_jobs = {}
_jobs_lock = threading.Lock()
_addin_ok = None   # None = unknown, True/False after the first hello
_addin_checked = 0.0
_ADDIN_RECHECK_S = 600   # a "no add-in" answer is re-checked after this long


def register_script_dir(path: str, helpers: list = None, prefix: str = PUBLIC_PREFIX) -> None:
    """Add a folder of .cs scripts to the library (used by extensions through eplan_mcp.ext)."""
    path = os.path.abspath(path)
    _extra_script_dirs[path] = [os.path.abspath(h) for h in (helpers or [])]
    _dir_prefix[path] = prefix


def _prefix_for_dir(folder: str) -> str:
    """Tool-name prefix of a library folder: the extension's own, "eplan_" for the public one."""
    return _dir_prefix.get(os.path.abspath(folder), PUBLIC_PREFIX)


def read_run_file(run_id: str, name: str) -> str:
    """A file from a run's folder (result.json, result_data.json, progress.json)."""
    if not run_id or any(c in run_id for c in ("\\", "/", ":", ".")) or name not in ("result.json", "result_data.json", "progress.json"):
        raise ValueError("Bad run id or file name.")
    with _jobs_lock:
        job = _jobs.get(run_id)
    folders = [job["folder"]] if job and job.get("folder") else []
    if os.path.isdir(RUNS_DIR):
        folders += [os.path.join(RUNS_DIR, day, run_id) for day in sorted(os.listdir(RUNS_DIR), reverse=True)]
    for folder in folders:
        path = os.path.join(folder, name)
        if os.path.isfile(path):
            return _read(path)
    raise FileNotFoundError(f"No {name} for run {run_id}.")


def _helpers_for(script_path: str) -> list:
    """Helper sources registered for the library folder a script lives in."""
    if not script_path:
        return []
    folder = os.path.dirname(os.path.abspath(script_path))
    return list(_extra_script_dirs.get(folder, []))


def _script_dirs():
    dirs = [os.path.join(DOTNET_DIR, "scripts")] + list(_extra_script_dirs.keys())
    for d in (os.environ.get("EPLAN_MCP_SCRIPT_DIRS") or "").split(os.pathsep):
        if d.strip():
            dirs.append(os.path.abspath(d.strip()))
    return [d for d in dirs if os.path.isdir(d)]


def _parse_header(source: str) -> dict:
    """The `// @key value` lines at the top of a script (same rules as HeaderParser.cs)."""
    header = {"args": []}
    for raw in source.splitlines():
        line = raw.strip()
        if not line:
            continue
        if not line.startswith("//"):
            break
        body = line[2:].strip()
        if not body.startswith("@"):
            continue
        key, _, value = body[1:].partition(" ")
        key, value = key.lower(), value.strip()
        if key == "arg":
            header["args"].append(value)
        elif key == "desc":
            header["desc"] = (header.get("desc", "") + " " + value).strip()
        elif key in ("writes", "unsafe"):
            header[key] = value.split(" ")[0].lower() in ("true", "1", "yes", "")
        else:
            header[key] = value
    return header


def _resolve_script(script: str):
    """A library script name, or a path to a .cs file. Returns (path, source) or raises."""
    if os.path.isfile(script):
        path = os.path.abspath(script)
    else:
        name = script[:-3] if script.endswith(".cs") else script
        path = None
        for d in _script_dirs():
            candidate = os.path.join(d, name + ".cs")
            if os.path.isfile(candidate):
                path = candidate
                break
            for f in os.listdir(d):
                if f.endswith(".cs") and _parse_header(_read(os.path.join(d, f))).get("tool") == name:
                    path = os.path.join(d, f)
                    break
            if path:
                break
        if not path:
            raise FileNotFoundError(f"No script named '{script}'. See script_list().")
    return path, _read(path)


def _read(path: str) -> str:
    with open(path, encoding="utf-8") as f:
        return f.read()


def _write_json(path: str, data) -> None:
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f)
    os.replace(tmp, path)


def _read_json(path: str):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def _host_version():
    try:
        with open(os.path.join(HOST_DIR, "eplanmcp-version.txt"), encoding="utf-8-sig") as f:
            return f.read().strip()
    except OSError:
        return None


def _bootstrap_script(run_folder: str, mode: str, request_path: str, result_path: str) -> str:
    """Fill the bootstrap template and write it into the run folder."""
    template = os.path.join(HOST_DIR, "Bootstrap", "bootstrap.cs.template")
    if not os.path.isfile(template):
        raise FileNotFoundError(
            f"Script host not built: {template} is missing. Build dotnet/EplanMcp.sln "
            "(Release) or set EPLAN_MCP_HOST_DIR.")
    text = _read(template)
    # Verbatim C# strings (@"..."): only a double quote needs escaping.
    for key, value in (("HOST_DIR", HOST_DIR), ("REQUEST_PATH", request_path),
                       ("RESULT_PATH", result_path), ("MODE", mode),
                       ("CORE_VERSION", _host_version() or "0.0.0")):
        text = text.replace("{{" + key + "}}", value.replace('"', '""'))
    path = os.path.join(run_folder, "bootstrap.cs")
    # With a BOM, so EPLAN's script engine reads non-ASCII paths (a user name, say) as UTF-8.
    with open(path, "w", encoding="utf-8-sig") as f:
        f.write(text)
    return path


def _invoke(runtime: str, mode: str, run_folder: str, request_path: str, result_path: str,
            timeout: float) -> dict:
    """Start one host call. Blocks until EPLAN returns from the action."""
    manager, error = _get_connected_manager()
    if error:
        return error
    if runtime == "addin":
        if mode == "hello":
            action = f'EplanMcp_Hello /result:"{result_path}"'
        else:
            action = f'EplanMcp_RunScript /request:"{request_path}" /result:"{result_path}"'
        # Direct path: an add-in action started inside the quiet-mode wrapper cannot lock.
        return manager.execute_action(action, quiet_mode=False, timeout=timeout, label="script_host")
    script = _bootstrap_script(run_folder, mode, request_path, result_path)
    return manager.execute_action(f'ExecuteScript /ScriptFile:"{script}"', timeout=timeout,
                                  label="script_host")


def _new_run_folder(run_id: str) -> str:
    folder = os.path.join(RUNS_DIR, time.strftime("%Y%m%d"), run_id)
    os.makedirs(folder, exist_ok=True)
    return folder


def _hello(runtime: str, timeout: float = 30.0) -> dict:
    run_id = "hello-" + uuid.uuid4().hex[:8]
    folder = _new_run_folder(run_id)
    result_path = os.path.join(folder, "result.json")
    try:
        call = _invoke(runtime, "hello", folder, "", result_path, timeout)
    except FileNotFoundError as e:
        return {"success": False, "runtime": runtime, "error": str(e)}
    data = _read_json(result_path)
    if data is None:
        return {"success": False, "runtime": runtime, "action_result": call,
                "error": ("No answer from the host. For runtime 'addin': is EplanMcp.Host.AddIn "
                          "registered (EplApiModuleActionNet) and EPLAN restarted? EPLAN can also "
                          "report success for a script it never ran; check eplan_get_system_messages.")}
    data["success"] = not data.get("error") or bool(data.get("core_version"))
    return data


def _pick_runtime(runtime: str) -> str:
    global _addin_ok, _addin_checked
    if runtime in ("addin", "bootstrap"):
        return runtime
    if _addin_ok is None or (not _addin_ok and time.time() - _addin_checked > _ADDIN_RECHECK_S):
        hello = _hello("addin", timeout=20.0)
        _addin_ok = bool(hello.get("success")) and hello.get("protocol") == PROTOCOL
        _addin_checked = time.time()
    return "addin" if _addin_ok else "bootstrap"


def script_hello(runtime: str = "auto") -> dict:
    """
    Handshake with the script host: core version, protocol, features, EPLAN version, license and
    open projects. Call this first in a session; it also decides which runtime "auto" uses.

    Args:
        runtime: "auto" (add-in if it answers, else bootstrap), "addin" or "bootstrap".
    """
    global _addin_ok
    if runtime == "auto":
        hello = _hello("addin", timeout=20.0)
        _addin_ok = bool(hello.get("success")) and hello.get("protocol") == PROTOCOL
        if _addin_ok:
            return hello
        boot = _hello("bootstrap")
        boot["addin"] = {"available": False, "detail": hello.get("error")}
        return boot
    hello = _hello(runtime)
    if runtime == "addin":
        _addin_ok = bool(hello.get("success"))
    if hello.get("success") and hello.get("protocol") != PROTOCOL:
        hello["success"] = False
        hello["error"] = (f"Host protocol {hello.get('protocol')} does not match server protocol "
                          f"{PROTOCOL}. Update the host (rebuild, restart EPLAN for the add-in).")
    return hello


def script_run(code: str = None, script: str = None, args: dict = None, project: str = None,
               runtime: str = "auto", compile_only: bool = False, dry_run: bool = False,
               budget_seconds: float = 120.0, wait_seconds: float = 60.0,
               allow_real_project: bool = False, confirm: str = None,
               helpers: list = None) -> dict:
    """
    Compile and run a C# script inside EPLAN. Returns a result envelope, or a run_id to follow.

    Write a statement body, e.g. `var p = ctx.RequireProject(); return p.ProjectName;`, or a full
    class with `public static object Run(ScriptContext ctx)`. Usual usings are added for snippets.
    `ctx` gives Project, Args (String/Int/Bool/List), Budget.Check(), Progress(), Log(), Lock(obj),
    DryRun, WriteAllowed. Return plain data. The run already holds a LockingStep.

    Headers at the top (`// @writes true`, `// @arg name type ...`, `// @requires core>=0.1`)
    declare intent. A script that writes must say `// @writes true`; read-only runs reject EPLAN
    writes at compile time. Compile errors come back with your own line numbers and hints.

    Args:
        code: Script source (ad-hoc). Give either code or script.
        script: A library script name (see script_list) or a .cs path.
        args: Arguments for the script (ctx.Args).
        project: .elk path or project name to act on; default the focused or only open project.
        runtime: "auto", "addin" or "bootstrap".
        compile_only: Compile and check only. Also returns confirm_code for real-project writes.
        dry_run: Sets ctx.DryRun; the script should report changes instead of making them.
        budget_seconds: Time limit enforced at each ctx.Budget.Check().
        wait_seconds: How long to wait before returning a run_id instead of the result.
        allow_real_project: Allow writes to a non-scratch project (saved script + confirm only).
        confirm: The confirm_code from a compile_only run of the same saved script.
        helpers: Extra .cs files compiled with the script.
    """
    if bool(code) == bool(script):
        return {"success": False, "error": "Give exactly one of code or script."}
    try:
        script_path, source = (None, code) if code else _resolve_script(script)
    except FileNotFoundError as e:
        return {"success": False, "error": str(e)}

    if allow_real_project and not compile_only:
        if not script_path:
            return {"success": False, "error": "Real-project writes need a saved script, not ad-hoc code."}
        # The host checks confirm against its own hash of source + helpers + these exact args.
        if not confirm or len(confirm) < 12:
            return {"success": False,
                    "error": "Real-project writes need confirm=<confirm_code> from a compile_only "
                             "run of this exact script with these exact args and helpers, shown to "
                             "and approved by the user. Take a backup first (eplan_backup_project)."}

    run_id = uuid.uuid4().hex[:12]
    with _jobs_lock:
        # Check and claim in one step, so two calls can never start two scripts at once.
        busy = [j for j in _jobs.values() if _is_busy(j)]
        if busy:
            return {"success": False, "error": f"Run {busy[0]['run_id']} is still running; EPLAN runs one "
                                               "script at a time. Use script_status or script_cancel."}
        claim = {"run_id": run_id, "claimed": True, "thread": None, "started": time.time(),
                 "deadline": time.time() + budget_seconds + 180}
        _jobs[run_id] = claim

    try:
        chosen = _pick_runtime(runtime)
    except Exception as e:  # noqa: BLE001 - surface any connection failure plainly
        with _jobs_lock:
            _jobs.pop(run_id, None)
        return {"success": False, "error": f"Could not reach EPLAN: {e}"}

    folder = _new_run_folder(run_id)
    request_path = os.path.join(folder, "request.json")
    result_path = os.path.join(folder, "result.json")
    _write_json(request_path, {
        "protocol": PROTOCOL,
        "run_id": run_id,
        "source": source,
        "script_path": script_path,
        "source_name": os.path.basename(script_path) if script_path else "snippet.cs",
        "args": args or {},
        # The host runs with exactly this text and hashes it into the confirm code.
        "args_canonical": json.dumps(args or {}, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=False),
        "project": project,
        "compile_only": compile_only,
        "dry_run": dry_run,
        "allow_real_project": allow_real_project,
        "confirm": confirm,
        "budget_seconds": budget_seconds,
        "helper_sources": _helpers_for(script_path) + [os.path.abspath(h) for h in (helpers or [])],
        "references": [],
        "run_folder": folder,
    })

    job = {"run_id": run_id, "runtime": chosen, "folder": folder, "result_path": result_path,
           "started": time.time(), "call": None, "deadline": claim["deadline"]}

    def work():
        try:
            job["call"] = _invoke(chosen, "run", folder, request_path, result_path,
                                  timeout=budget_seconds + 120)
        except Exception as e:  # noqa: BLE001
            job["call"] = {"success": False, "message": str(e)}

    job["thread"] = threading.Thread(target=work, name=f"script-{run_id}", daemon=True)
    with _jobs_lock:
        _jobs[run_id] = job
    job["thread"].start()
    job["thread"].join(timeout=max(0.0, wait_seconds))
    out = script_status(run_id)
    if compile_only and out.get("ok") and out.get("approval_hash"):
        out["confirm_code"] = out["approval_hash"][:16]
    return out


def _is_busy(job: dict) -> bool:
    """A job blocks new runs while its call is in flight, up to its deadline. Past the deadline
    the call is treated as lost (EPLAN hung or crashed) so the server is not stuck forever."""
    if time.time() > job.get("deadline", 0):
        return False
    thread = job.get("thread")
    return job.get("claimed") and thread is None or (thread is not None and thread.is_alive())


def script_status(run_id: str) -> dict:
    """
    State of a script run: "running" with progress and heartbeat age, or the final result envelope.

    Args:
        run_id: The run_id script_run returned.
    """
    with _jobs_lock:
        job = _jobs.get(run_id)
    if not job:
        return {"success": False, "error": f"Unknown run_id {run_id} (runs are kept per server process)."}
    if job["thread"] is not None and job["thread"].is_alive() and time.time() > job["deadline"]:
        return {"success": False, "state": "lost", "run_id": run_id,
                "error": ("The run passed its deadline and EPLAN has not returned. EPLAN may be hung "
                          "on this script or a dialog; check eplan_list_dialogs. New runs are allowed "
                          "again, but EPLAN executes one call at a time.")}
    if job["thread"] is not None and job["thread"].is_alive():
        heartbeat = os.path.join(job["folder"], "heartbeat")
        age = time.time() - os.path.getmtime(heartbeat) if os.path.exists(heartbeat) else None
        return {"success": True, "state": "running", "run_id": run_id, "runtime": job["runtime"],
                "elapsed_s": round(time.time() - job["started"], 1),
                "progress": _read_json(os.path.join(job["folder"], "progress.json")),
                "heartbeat_age_s": None if age is None else round(age, 1),
                "note": ("No heartbeat yet: the script has not called ctx.Budget.Check() or "
                         "ctx.Progress(), or EPLAN is busy." if age is None else None)}
    result = _read_json(job["result_path"])
    if result is None:
        return {"success": False, "state": "failed", "run_id": run_id, "runtime": job["runtime"],
                "action_result": job["call"],
                "error": ("The host wrote no result. EPLAN may have reported success for a script it "
                          "never ran, the add-in may be missing, or EPLAN crashed. Check "
                          "eplan_get_system_messages and the audit log under %LOCALAPPDATA%\\EplanMcp\\audit.")}
    result["success"] = bool(result.get("ok"))
    result["state"] = "done"
    return result


def script_cancel(run_id: str) -> dict:
    """
    Ask a running script to stop. It stops at its next ctx.Budget.Check(); a script that never
    calls it cannot be stopped short of closing EPLAN.

    Args:
        run_id: The run_id script_run returned.
    """
    with _jobs_lock:
        job = _jobs.get(run_id)
    if not job:
        return {"success": False, "error": f"Unknown run_id {run_id}."}
    with open(os.path.join(job["folder"], "cancel"), "w", encoding="utf-8") as f:
        f.write(time.strftime("%Y-%m-%dT%H:%M:%S"))
    return {"success": True, "run_id": run_id, "note": "Cancel requested; poll script_status."}


def script_list() -> dict:
    """
    The script library: every .cs script with its tool name, description, args and whether it
    writes. Run one with script_run(script=<tool name>).
    """
    scripts = []
    for d in _script_dirs():
        for f in sorted(os.listdir(d)):
            if not f.endswith(".cs") or f.startswith("_"):
                continue
            path = os.path.join(d, f)
            h = _parse_header(_read(path))
            scripts.append({"tool": h.get("tool") or f[:-3], "desc": h.get("desc"),
                            "args": h.get("args"), "writes": bool(h.get("writes")),
                            "needs": h.get("needs", "session"), "path": path})
    return {"success": True, "count": len(scripts), "scripts": scripts,
            "template": os.path.join(DOTNET_DIR, "scripts", "_template.cs")}
