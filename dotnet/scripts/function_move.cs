// @tool     function_move
// @desc     Moves placed functions on one page to absolute page coordinates, by placement name. A dry run reports from/to only.
// @schema   1
// @arg      page   string  required   Page name (exact, else the first page whose name contains it)
// @arg      moves  list    required   JSON array of {name, x, y}; name is the placement Name, a missing x or y keeps the current value
// @writes   true
// @needs    session
using System;
using System.Collections.Generic;
using Eplan.EplApi.Base;
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        var page = Pages.Require(project, ctx.Args.Require("page"), allowPartial: true);
        var functions = Functions.OnPage(page);
        var moved = new List<object>();
        foreach (var spec in PlainJson.Rows(ctx.Args.Json("moves")))
        {
            ctx.Budget.Check();
            string name = PlainJson.Str(spec, "name");
            var row = new Dictionary<string, object?> { ["name"] = name };
            try
            {
                var hits = Functions.Named(functions, name);
                if (hits.Count != 1) { row["error"] = $"{hits.Count} functions named '{name}' on the page"; moved.Add(row); continue; }
                var f = hits[0];
                var from = f.Location;
                var to = new PointD(spec.ContainsKey("x") ? PlainJson.Num(spec, "x") : from.X, spec.ContainsKey("y") ? PlainJson.Num(spec, "y") : from.Y);
                row["from_x"] = from.X; row["from_y"] = from.Y;
                row["to_x"] = to.X; row["to_y"] = to.Y;
                if (!ctx.DryRun)
                {
                    ctx.Lock(f);
                    f.Location = to;
                    row["after_x"] = f.Location.X;
                    row["after_y"] = f.Location.Y;
                }
            }
            catch (Exception ex) { row["error"] = Safe.Error(ex); }
            moved.Add(row);
        }
        return new { project = project.ProjectFullName, page = page.Name, dry_run = ctx.DryRun, moved };
    }
}
