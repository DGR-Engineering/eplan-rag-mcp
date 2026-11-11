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
