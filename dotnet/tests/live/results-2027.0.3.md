# Live results: EPLAN 2027.0.3, .NET 8 host

Measured 2026-11-10 on one Windows PC with an EPLAN API developer license (Electric P8 Premium),
script host core 0.1.0, runtime **bootstrap** (`ExecuteScript`, host loaded from bytes). The
target was a scratch clone of a mid-size schematic project, opened alone.

| Check | Result |
|---|---|
| Handshake (`script_hello`, bootstrap) | Pass. EPLAN 2027.0.3, Roslyn 4.12.0.0 found in EPLAN's own `Bin`, license reported. |
| `identity.cs` | Pass. One copy of `Eplan.EplApi.DataModelNetu` (Default context); `ctx` comes from the one host copy (`EplanMcp.Core/<version>/<manifest hash>` context); the script runs in its own collectible context. |
| `references.cs` | Pass. EPLAN's native host DOES provide `TRUSTED_PLATFORM_ASSEMBLIES` (356 entries); 25 of ~237 loaded assemblies have no file location. |
| `project_info.cs` | Pass. Page and function counts of the clone in about 0.6 s per run (compile included). |
| `hang.cs`, budget 3 s | Pass. Stopped at 3.4 s with `ScriptStoppedException`; the error points at `hang.cs:line 6`. EPLAN stayed responsive. |
| `lock_check.cs` | Pass. `ctx.Lock` (SmartLock) succeeded on the bootstrap path: script-context runs CAN lock. |
| Compile error | Pass. `CS1061` on the author's line 2, hint "Did you mean: ProjectName, ProjectType". |
| Read-only write | Pass. `SmartLock` in a script without `@writes true` rejected at compile time (`EMCP002`). |
| Unload | Mostly pass. 20 consecutive runs (half of them using the DataModel) unloaded, with flat private memory (about 1.66 GB) and a flat count of loaded script assemblies. The FIRST run on each new server connection, and the class-file scripts run in one earlier batch, stayed loaded: one small assembly each, no visible memory growth. Cause not found yet; EPLAN's own `AssemblyLoadContext.AssemblyLoad` subscribers (API module, license handler, script log, WPF) are the suspects. |
| Add-in runtime | Pass, same day. `EplApiModuleActionNet /register:` on a copy outside the build folder made `EplanMcp_RunScript` and `EplanMcp_Hello` resolvable at once, with NO EPLAN restart (first registration). Every check above passed through the add-in too (identity: one data-model copy, host in the Default context; lock taken; budget stop; hints; read-only rejection), with the same unload pattern. `/unregister:` removed both actions again. |

## Gotcha found: action file paths under `AppData`

An `ExecuteScript /ScriptFile:"..."` whose path is written in the long form
`C:\Users\<user>\AppData\Local\...` or `...\AppData\Roaming\...` is silently ignored: the remote
call reports success, nothing runs, and EPLAN logs "The name of the script file is missing
(ScriptFile)". The same files under the user profile (`~\.eplan-mcp`), ProgramData, Documents or the
8.3-form TEMP (`C:\Users\<SHORT~1>\AppData\Local\Temp`) run fine. The script host therefore keeps
its run folders in `~\.eplan-mcp\runs`. Not tested yet: whether add-in action parameters
(`/request:` `/result:`) are affected the same way.

## Script host 0.2.0 (final live test)

Measured 2026-11-11 on the same PC and EPLAN 2027.0.3.27481 (Electric P8 Premium), host core 0.2.0
built in Release from this branch. The add-in was registered with `EplApiModuleActionNet /register:`
from `dotnet/artifacts/host`, then EPLAN was restarted. The target was a scratch clone of a mid-size
schematic project (75 pages, 8,687 functions), carrying the scratch marker, opened alone. All three
runtimes ran in the same session; the legacy probe add-in stayed registered for the parity check.

Two builds were used. These checks ran on 26ecc2914: the add-in handshake, the public-script reads
and `text_create`, the read-only refusal, the bootstrap checks, the write gate, and the first
`identity.cs` run. These ran on e73e291a4, which carries the reference fix below: probe parity,
the probe write, `lock_check.cs` on the add-in, the MasterData check on both runtimes, the offline
host, and an `identity.cs` re-run on both runtimes with the same result.

| Check | Result |
|---|---|
| Handshake, add-in | Pass. `script_hello(runtime="addin")` reports core 0.2.0, protocol 1, no version warning. `EplanMcp_RunScript` and `EplanMcp_Hello` resolve after the restart. |
| Reads, add-in | Pass. `project_info` (75 pages, 8,687 functions, about 1.8 s), `page_inspect` (all pages, then one page with 442 functions), `function_inspect` and `function_dump` all return data for the clone named in `project`. |
| Write, add-in | Pass. `text_create` writes a two-line text and reads it back. `lock_check.cs` locks a function. A write probe from a helper library (about 50 files) creates a text. No `LockingException`: the add-in no longer opens its own nested `LockingStep`. |
| Read-only run that writes | Pass. `Text.Create` in a script without `@writes true` is rejected at compile time (`EMCP002`). `ctx.Lock` in such a script throws "This run may not write" at run time. |
| Probe parity | Pass. The helper-library probes `project_info`, `page_inspect`, `function_inspect` and `function_dump`, run through the script host, give output identical to the legacy probe add-in on the same clone: zero differences, field by field. Both sides were given the project explicitly, and both report its name, 75 pages and 442 functions on the sampled page. The legacy add-in matches `project` against the full name without `.elk`; the script host takes the `.elk` path. |
| Public scripts vs legacy probes | Expected differences only. `function_dump` is identical. `page_inspect` adds an `err: null` per item and omits `include`. `function_inspect` reports an empty supplementary field as `null` where the legacy probe puts an `EmptyPropertyException` in `err`. The public `project_info` is a different, smaller summary by design. |
| Handshake, bootstrap | Pass. Core 0.2.0, same EPLAN, license and Roslyn as the add-in. |
| Read and write, bootstrap | Pass. `project_info` takes about 0.5 s. `text_create` writes and reads back. `lock_check.cs` locks a function through `ctx.Lock`. |
| `identity.cs` | Pass on both runtimes. One copy of `Eplan.EplApi.DataModelNetu` (Default context), and `ctx` comes from the runtime's own host copy. With both runtimes used in one session, the process holds two host copies, one per runtime: the add-in's in the Default context and the bootstrap's in `EplanMcp.Core/0.2.0/<hash>`. That is by design. |
| Offline host | Pass. Started on the closed clone in 167 s (first start). `project_info` read 75 pages and 8,687 functions, about 16-20 s per run because each run opens and closes the project. Session-only scripts (`page_inspect`, `text_create`) were refused at the guard ("needs the interactive EPLAN"). `script_offline_stop` stopped it, and no host process remained after 1 s. |
| Write gate | Pass. With the scratch marker removed from the same clone, ad-hoc writes were refused at the guard on both the add-in and the bootstrap: "the target project ... is not a scratch clone (no marker file)". `allow_real_project=True` without the server switch was refused before anything ran. The marker was restored afterwards. |
| Unload | Not measured in steady state. About half the runs (13 of 25) reported `unloaded: false`. Every run here came from a fresh server connection, which is the case 0.1.0 already saw stay loaded. The warm-connection series that 0.1.0 measured was not repeated. |

### Fixed during this test

- **MasterData scripts did not compile in a fresh session.** The compile references come from the
  loaded assemblies, and EPLAN loads `Eplan.EplApi.MasterDataNetu` lazily. So any script or helper
  using `Eplan.EplApi.MasterData` failed with `CS0234` on the add-in and bootstrap runtimes until
  something else loaded it. The probe helper library hit this. Both in-EPLAN runtimes now reference
  every `Eplan.EplApi.*Netu.dll` in EPLAN's `Bin`, as the offline host already did. After a restart,
  the library compiled at once.
- The write-gate refusal said "is not a scratch clone" twice. Reworded.

### Seen, not changed

- `write_allowed` is `true` in a result that failed the compile-time safety check of a read-only
  script, because it reports the gate and not the script. It is `false` in a result that reached
  the run phase.
- During one batch, EPLAN showed its "Open symbol library" and "Symbol selection" dialogs.
  They were already open before the first refused call. Three add-in runs then reported
  `EplanBlockedByDialog` and "the host wrote no result". The test closed no dialog. Shortly after,
  the dialogs were gone, and a retry passed. The cause was not identified. EPLAN's system messages
  show only read-only symbol-library opens, each closed again.
