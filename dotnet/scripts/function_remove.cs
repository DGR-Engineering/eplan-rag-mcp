// @tool     function_remove
// @desc     Removes placed functions on one page by placement name, only when placed with a symbol whose name starts with symbol_prefix, never a main function.
// @schema   1
// @arg      page           string  required   Page name (exact, else the first page whose name contains it)
// @arg      names          list    required   Placement names to remove
// @arg      symbol_prefix  string  required   Guard: the function's symbol name must start with this
// @writes   true
// @needs    session
//
// The symbol prefix is a guard, so a name typo cannot delete a device. A main function is never
// removed: its part data would go with it. Each object is locked before Remove(): without the lock
// Remove() fails silently. "removed" is read back from IsValid.
using System;
using System.Collections.Generic;
using System.Linq;
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        string prefix = ctx.Args.Require("symbol_prefix");
        if (prefix.Length == 0) throw new ArgumentException("symbol_prefix must not be empty");
        var page = Pages.Require(project, ctx.Args.Require("page"), allowPartial: true);
        var functions = Functions.OnPage(page);
        var rows = new List<object>();
        foreach (var name in ctx.Args.List("names"))
        {
            ctx.Budget.Check();
            var row = new Dictionary<string, object?> { ["name"] = name };
            try
            {
                // Named() skips removed objects: an earlier name in this run may have removed one.
                var hits = Functions.Named(functions, name).Where(f => Functions.SymbolName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count != 1) { row["error"] = $"{hits.Count} functions named '{name}' with a '{prefix}' symbol on the page"; rows.Add(row); continue; }
                var f = hits[0];
                row["symbol"] = Functions.SymbolName(f);
                row["handle"] = f.ToStringIdentifier();
                if (f.IsMainFunction) { row["error"] = "main function - not removed"; rows.Add(row); continue; }
                if (!ctx.DryRun)
                {
                    ctx.Lock(f);
                    f.LockObject();
                    f.Remove();
                    row["removed"] = !f.IsValid;
                }
            }
            catch (Exception ex) { row["error"] = Safe.Error(ex); }
            rows.Add(row);
        }
        return new { project = project.ProjectFullName, page = page.Name, dry_run = ctx.DryRun, removed = rows };
    }
}
