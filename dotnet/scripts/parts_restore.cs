// @tool     parts_restore
// @desc     Puts parts recorded by parts_snapshot back on the matching functions. A dry run reports what would change.
// @schema   1
// @arg      from_file     string  optional   A parts_snapshot JSON file (or pass rows)
// @arg      rows          list    optional   Snapshot rows inline, instead of from_file
// @arg      page_prefix   string  optional   Only rows (and functions) on pages whose name starts with this
// @arg      promote_main  bool    optional   Make a recorded main function main again when its device has no main now
// @arg      out           string  optional   JSON file for the per-row outcomes (default parts_restore.json in the run folder)
// @writes   true
// @needs    session
//
// Only snapshot rows WITH parts are restored; a row without parts never removes anything. A match
// on an auxiliary function goes to the device's single main function, where parts live. A fixed
// device is never changed. Every change is read back.
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        var snapshot = PlainJson.RowsFromArgs(ctx.Args, "rows", "from_file");
        var result = Parts.Restore(ctx, project, snapshot, ctx.Args.String("page_prefix"), ctx.Args.Bool("promote_main"));
        var path = PlainJson.OutPath(ctx, ctx.Args.String("out"), "parts_restore.json");
        result["bytes"] = PlainJson.Write(path, result["rows"]);
        result["out"] = path;
        result.Remove("rows");
        result["project"] = project.ProjectFullName;
        return result;
    }
}
