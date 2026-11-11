"""Offline tests for api/actions/scripthost.py and the scratch marker. No EPLAN: a fake manager
plays the host by writing the result file the real host would write."""

import json
import os

import pytest

from api.actions import fixtures, scripthost


class FakeManager:
    """Answers the two host calls the way the add-in or the bootstrap would."""

    def __init__(self, addin=True, result=None):
        self.addin = addin
        self.result = result or {"protocol": 1, "ok": True, "phase": "done", "result": 42}
        self.calls = []

    def execute_action(self, action, quiet_mode=True, **kwargs):
        self.calls.append((action, quiet_mode))
        if action.startswith("EplanMcp_"):
            if not self.addin:
                return {"success": False, "message": "action not found"}
            result_path = action.split('/result:"')[1].rstrip('"')
            if action.startswith("EplanMcp_Hello"):
                payload = {"protocol": 1, "core_version": "0.1.0", "runtime": "addin"}
            else:
                req = json.load(open(action.split('/request:"')[1].split('"')[0], encoding="utf-8"))
                payload = dict(self.result, run_id=req["run_id"], runtime="addin",
                               approval_hash="ab" * 32, args_seen=req["args_canonical"])
            json.dump(payload, open(result_path, "w", encoding="utf-8"))
            return {"success": True}
        if action.startswith("ExecuteScript"):
            script = action.split('/ScriptFile:"')[1].rstrip('"')
            text = open(script, encoding="utf-8").read()
            result_path = text.split('string resultPath = @"')[1].split('";')[0].replace('""', '"')
            json.dump({"protocol": 1, "core_version": "0.1.0", "runtime": "bootstrap", "ok": True,
                       "phase": "done"}, open(result_path, "w", encoding="utf-8"))
            return {"success": True}
        raise AssertionError("unexpected action " + action)


@pytest.fixture
def host(tmp_path, monkeypatch):
    host_dir = tmp_path / "host"
    (host_dir / "Bootstrap").mkdir(parents=True)
    template = os.path.join(scripthost.DOTNET_DIR, "src", "EplanMcp.Scripting", "Bootstrap", "bootstrap.cs.template")
    (host_dir / "Bootstrap" / "bootstrap.cs.template").write_text(open(template, encoding="utf-8").read(), encoding="utf-8")
    (host_dir / "eplanmcp-version.txt").write_text("0.1.0")
    monkeypatch.setattr(scripthost, "HOST_DIR", str(host_dir))
    monkeypatch.setattr(scripthost, "RUNS_DIR", str(tmp_path / "runs"))
    monkeypatch.setattr(scripthost, "_addin_ok", None)
    scripthost._jobs.clear()
    return host_dir


def use(monkeypatch, manager):
    monkeypatch.setattr(scripthost, "_get_connected_manager", lambda: (manager, None))
    return manager


def test_run_uses_addin_on_the_direct_path(host, monkeypatch):
    m = use(monkeypatch, FakeManager(addin=True))
    out = scripthost.script_run(code="return 42;")
    assert out["success"] is True and out["state"] == "done" and out["result"] == 42
    run_calls = [c for c in m.calls if c[0].startswith("EplanMcp_RunScript")]
    assert run_calls and run_calls[0][1] is False      # quiet_mode False = direct path


def test_request_file_carries_the_contract(host, monkeypatch):
    use(monkeypatch, FakeManager())
    out = scripthost.script_run(code="return 1;", args={"page": "=A/1"}, project="x.elk", budget_seconds=5)
    req = json.load(open(os.path.join(os.path.dirname(out_path(out)), "request.json"), encoding="utf-8"))
    assert req["protocol"] == scripthost.PROTOCOL
    assert req["args"] == {"page": "=A/1"} and req["project"] == "x.elk" and req["budget_seconds"] == 5
    assert req["source_name"] == "snippet.cs" and req["script_path"] is None


def out_path(out):
    return scripthost._jobs[out["run_id"]]["result_path"]


def test_falls_back_to_bootstrap_when_addin_missing(host, monkeypatch):
    m = use(monkeypatch, FakeManager(addin=False))
    out = scripthost.script_run(code="return 1;")
    assert out["success"] is True and out["runtime"] == "bootstrap"
    script = [c[0] for c in m.calls if c[0].startswith("ExecuteScript")][0].split('"')[1]
    text = open(script, encoding="utf-8").read()
    assert "{{" not in text and str(host) in text


def test_bootstrap_escapes_quotes_in_paths(host, monkeypatch):
    folder = host / "run"
    folder.mkdir()
    path = scripthost._bootstrap_script(str(folder), "run", 'C:\\a"b\\req.json', 'C:\\a"b\\res.json')
    text = open(path, encoding="utf-8").read()
    assert '@"C:\\a""b\\req.json"' in text


def test_real_project_write_needs_saved_script_and_confirm(host, monkeypatch):
    use(monkeypatch, FakeManager())
    out = scripthost.script_run(code="return 1;", allow_real_project=True)
    assert out["success"] is False and "saved script" in out["error"]

    script = host / "w.cs"
    script.write_text("// @writes true\nreturn 1;", encoding="utf-8")
    out = scripthost.script_run(script=str(script), allow_real_project=True, confirm="nope")
    assert out["success"] is False and "confirm" in out["error"]

    ok = scripthost.script_run(script=str(script), compile_only=True)
    assert len(ok["confirm_code"]) == 16
    assert ok["confirm_code"] == "ab" * 8
    out = scripthost.script_run(script=str(script), allow_real_project=True, confirm=ok["confirm_code"])
    assert out["success"] is True


def test_args_are_sent_canonically(host, monkeypatch):
    use(monkeypatch, FakeManager())
    out = scripthost.script_run(code="return 1;", args={"b": 1, "a": "x"})
    assert out["args_seen"] == '{"a":"x","b":1}'


def test_exactly_one_of_code_or_script(host, monkeypatch):
    use(monkeypatch, FakeManager())
    assert scripthost.script_run()["success"] is False
    assert scripthost.script_run(code="x", script="y")["success"] is False


def test_long_run_returns_run_id_then_status(host, monkeypatch):
    import threading
    gate = threading.Event()

    class Slow(FakeManager):
        def execute_action(self, action, quiet_mode=True, **kwargs):
            if action.startswith("EplanMcp_RunScript"):
                gate.wait(5)
            return super().execute_action(action, quiet_mode, **kwargs)

    use(monkeypatch, Slow())
    out = scripthost.script_run(code="return 1;", wait_seconds=0.1)
    assert out["state"] == "running"
    busy = scripthost.script_run(code="return 2;")
    assert busy["success"] is False and "still running" in busy["error"]
    assert scripthost.script_cancel(out["run_id"])["success"] is True
    gate.set()
    scripthost._jobs[out["run_id"]]["thread"].join(5)
    assert scripthost.script_status(out["run_id"])["state"] == "done"


def test_no_result_file_is_reported_plainly(host, monkeypatch):
    class Silent(FakeManager):
        def execute_action(self, action, quiet_mode=True, **kwargs):
            if action.startswith("EplanMcp_RunScript"):
                return {"success": True}     # EPLAN "succeeded" but nothing ran
            return super().execute_action(action, quiet_mode, **kwargs)

    use(monkeypatch, Silent())
    out = scripthost.script_run(code="return 1;")
    assert out["success"] is False and "wrote no result" in out["error"]


def test_script_list_reads_headers(host, monkeypatch, tmp_path):
    lib = tmp_path / "lib"
    lib.mkdir()
    (lib / "a.cs").write_text("// @tool  alpha\n// @desc  Does A.\n// @writes true\n// @arg x string\nreturn 1;")
    (lib / "_template.cs").write_text("// @tool skip_me\n")
    monkeypatch.setenv("EPLAN_MCP_SCRIPT_DIRS", str(lib))
    listed = {s["tool"]: s for s in scripthost.script_list()["scripts"]}
    assert listed["alpha"]["writes"] is True and listed["alpha"]["desc"] == "Does A."
    assert "skip_me" not in listed
    assert "project_info" in listed            # the public library ships with the repo
    path, source = scripthost._resolve_script("alpha")
    assert path.endswith("a.cs")


def test_scratch_clone_gets_a_marker_naming_itself(tmp_path, monkeypatch):
    monkeypatch.setattr(fixtures, "SCRATCH_ROOT", str(tmp_path / "scratch"))
    elk = tmp_path / "T.elk"
    elk.write_text("link")
    (tmp_path / "T.edb").mkdir()
    out = fixtures.scratch_project_create(str(elk), open_after=False)
    marker = json.load(open(out["project"] + fixtures.SCRATCH_MARKER_SUFFIX, encoding="utf-8"))
    assert marker["kind"] == fixtures.SCRATCH_MARKER_KIND
    assert os.path.normcase(marker["project_link"]) == os.path.normcase(os.path.abspath(out["project"]))
    assert len(marker["nonce"]) == 32
    gone = fixtures.scratch_project_discard(out["project"], close_first=False)
    assert gone["success"] is True
    assert not os.path.exists(out["project"] + fixtures.SCRATCH_MARKER_SUFFIX)


def _fake_offline_host(queue, stop_event):
    """Plays EplanMcp.Host.Offline: keeps status.json fresh and answers queued requests."""
    import threading
    import time as _t

    def loop():
        while not stop_event.is_set():
            json.dump({"protocol": 1, "core_version": "0.1.0", "runtime": "offline"},
                      open(os.path.join(queue, "status.json"), "w", encoding="utf-8"))
            for f in os.listdir(queue):
                if f.endswith(".json") and f != "status.json":
                    job = json.load(open(os.path.join(queue, f), encoding="utf-8"))
                    req = json.load(open(job["request"], encoding="utf-8"))
                    json.dump({"protocol": 1, "ok": True, "phase": "done", "runtime": "offline",
                               "run_id": req["run_id"], "result": req["project"]},
                              open(job["result"], "w", encoding="utf-8"))
                    os.remove(os.path.join(queue, f))
            _t.sleep(0.05)

    t = threading.Thread(target=loop, daemon=True)
    t.start()
    return t


def test_offline_runtime_queues_and_needs_no_eplan_connection(host, monkeypatch, tmp_path):
    import threading
    queue = tmp_path / "queue"
    queue.mkdir()
    monkeypatch.setattr(scripthost, "OFFLINE_QUEUE", str(queue))
    monkeypatch.setattr(scripthost, "_get_connected_manager",
                        lambda: (None, {"success": False, "message": "Not connected"}))
    stop = threading.Event()
    _fake_offline_host(str(queue), stop)
    try:
        assert scripthost.script_hello(runtime="offline")["success"] is True
        out = scripthost.script_run(code="// @needs offline\nreturn 1;", runtime="offline", project="C:/x/a.elk")
        assert out["success"] is True and out["runtime"] == "offline" and out["result"] == "C:/x/a.elk"
    finally:
        stop.set()


def test_offline_host_missing_is_reported_plainly(host, monkeypatch, tmp_path):
    monkeypatch.setattr(scripthost, "OFFLINE_QUEUE", str(tmp_path / "q"))
    monkeypatch.setattr(scripthost, "OFFLINE_DIR", str(tmp_path / "nothing"))
    out = scripthost.script_hello(runtime="offline")
    assert out["success"] is False and "not built" in out["error"]


def test_offline_stop_writes_the_stop_file(host, monkeypatch, tmp_path):
    queue = tmp_path / "queue"
    queue.mkdir()
    monkeypatch.setattr(scripthost, "OFFLINE_QUEUE", str(queue))
    json.dump({"protocol": 1}, open(queue / "status.json", "w"))
    assert scripthost.script_offline_stop()["success"] is True
    assert (queue / "stop").exists()
