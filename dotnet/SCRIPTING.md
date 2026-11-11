# Writing EPLAN scripts for the script host

A script is ordinary C# against the EPLAN API, compiled with EPLAN's own Roslyn and run inside the
open EPLAN session, with no build and no restart. Run one with `eplan_script_run`.

## The smallest script

A statement body is enough. The usual `using`s are added for you, and `ctx` is the script context:

```csharp
var p = ctx.RequireProject();
return new { pages = new DMObjectsFinder(p).GetPages(null).Length };
```

For anything longer, write a full class (start from `dotnet/scripts/_template.cs`):

```csharp
using Eplan.EplApi.DataModel;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx) { ... }
}
```

## What `ctx` gives you

| Member | Use |
|---|---|
| `ctx.Project` / `ctx.RequireProject()` | The target project: the `project` argument, else the focused one, else the only open one |
| `ctx.Args.String("x")`, `.Int`, `.Number`, `.Bool`, `.List`, `.Require` | Arguments passed to the run |
| `ctx.Budget.Check()` | Call it in every long loop. It is the only way a run stops (time limit or cancel) |
| `ctx.Progress(done, total, "msg")` | Progress the caller sees in `eplan_script_status` |
| `ctx.Log("...")` | Lines returned in the result's `log` |
| `ctx.Lock(obj)` | Lock an object before changing it (writing runs only) |
| `ctx.DryRun` | True when the caller asked for a preview: report changes, make none |
| `ctx.WriteAllowed`, `ctx.RunId`, `ctx.RunFolder` | Run facts; `RunFolder` is yours to write files into |

The run already holds a `LockingStep`. Return plain data: numbers, strings, lists, dictionaries,
anonymous objects. EPLAN objects come back as their type and text only.

## Helpers: `EplanMcp.Eplan`

The host also loads `EplanMcp.Eplan`, a library of generic helpers. Snippets get its namespace
automatically; a full class adds `using EplanMcp.Eplan;`. The scripts in `dotnet/scripts/` use it.

| Class | Use |
|---|---|
| `Pages` | `All`, `Find` / `Require` by name (exact, optionally partial), `Matches`, `Placements(page)`, `InScope` |
| `Functions` | `All` (placed or not), `OnPage`, `Find(page, deviceTag, symbolPrefix, occurrence)`, `Named`, `DeviceTag`, `FullDeviceTag`, `SymbolName` |
| `PropertyReader` | Guarded reads by id, index or name (`Read`, `ReadById`, `ReadByName`, `Existing`, `EveryLanguage`, `Supplementary`), `ResolveId`, `Escape` |
| `Describe` | Plain dictionaries for a `Placement`, `Text`, `Function`, `FunctionFull`, `Storable`, `Page`; `Handle` (the id `Edit.SelectObjects` takes) |
| `Parts` | `Row`, `Read`, `Snapshot`, `Restore` of article references |
| `MacroBoxes` | `Describe`, `Survey`, `Restore`, `Links`, `PrepareInspect` |
| `TextFit` | `Snapshot`, `ResolvePlacement`, `SetHeight`, `SetBox` for text size and alignment boxes |
| `Selection`, `Layers`, `SymbolLibraries` | Read the selection, the layer table, a symbol library |
| `PlainJson` | JSON files and record arguments as plain data (`Parse`, `Rows`, `RowsFromArgs`, `Write`, `OutPath`) |
| `Safe` | Guarded reads (`Text`, `Get`, `Maybe`, `Field`), `Error`, `RequireWrite` |

Property reads are where EPLAN surprises most: reading a property the object does not carry
throws, an empty value throws from `ToInt()`, and `PropertyValue`'s implicit conversions do not
apply through reflection or `Convert`. `PropertyReader` handles all three.

Helpers that write take `ctx` and lock through `ctx.Lock` (or call `Safe.RequireWrite`), so they
refuse a read-only run or a dry run at run time: the compile-time check cannot see inside a
library. Keep raw EPLAN calls out of a read-only script where a helper exists. An object
initializer such as `new PlacementsFilter { Page = page }` assigns an EPLAN property and fails
the read-only check; `Pages.Placements(page)` does not.

## Headers

Comment lines at the very top declare what a script is:

```csharp
// @tool     cable_report                  name when used as a library script / tool
// @desc     One sentence on what it does and returns.
// @schema   1
// @requires core>=0.1                     or feature:<name>
// @arg      page  string  optional        Page to limit the report to
// @arg      mode  enum(brief,full) default=brief
// @writes   true                          this script changes the project
// @needs    session                       session | offline | either | gui
// @unsafe                                 allow APIs the safety check rejects (logged)
```

## Writing

A script that changes anything must declare `// @writes true`. Then:

- **Scratch clones:** a run may write when its target project is a scratch clone made by
  `eplan_scratch_project_create`. A writing script may touch only `ctx.Project`: `ProjectManager`
  and the selection are rejected, and a run that changes which projects are open fails.
- **Real projects:** only a saved library script, with `allow_real_project=True` and the
  `confirm_code` from a `compile_only=True` run of the same script, arguments and helpers, after the
  user approved it. Take a backup first.
- Read-only runs reject EPLAN writes at compile time (`EMCP002`).
- Each writing run is one undo step named `EplanMcp <tool> <run id>`. Undo is best effort.

The safety check is a guard against mistakes, not a sandbox: a script is C# running in EPLAN.

## When something goes wrong

- **Compile errors** come back as `diagnostics` with your own line numbers and a "did you mean"
  hint for unknown members and types. A warm compile takes milliseconds, so fix and re-run.
- **Runtime errors** carry the message and your script's line.
- **A long run** returns a `run_id`. Follow it with `eplan_script_status`, stop it with
  `eplan_script_cancel`. It stops at the next `ctx.Budget.Check()`.
- **Large results** go to a file. Read it as the resource `eplan-script://runs/{run_id}/data`.

## Library scripts and helpers

`eplan_script_list` shows every script with a `@tool` header, and
`eplan_script_run(script="<name>")` runs one. Extensions add their own folders with
`eplan_mcp.ext.register_script_dir(path, helpers=[...], prefix="acme_")`; the helper `.cs` files
are compiled into every script from that folder. Set `EPLAN_MCP_SCRIPT_TOOLS=publish` to also
publish each library script as its own MCP tool.
