// @tool     parts_restore
// @desc     Puts the parts saved by parts_snapshot back on the matching functions. Checks the file belongs to the project, refuses more than limit changes, reads every change back and writes one outcome per function to a file. Dry run by default.
// @schema   1
// @arg      snapshot             string  required   A file written by parts_snapshot
// @arg      limit                int     required   Max functions that may be changed; more means nothing is written
// @arg      page_prefix          string  optional   Only rows (and functions) on pages whose name starts with this
// @arg      promote_main         bool    optional   Make a function main again when the snapshot recorded it as main and its device has no main function now
// @arg      allow_other_project  bool    optional   Accept a snapshot taken from a project with another name (a copy)
// @arg      out                  string  optional   File for the per-function outcomes (default parts_restore.json in the run folder)
// @writes   true
// @needs    session
//
// Writes only to a scratch clone (eplan_scratch_project_create). Only snapshot rows WITH parts are
// restored; a row without parts never removes anything. A matched function's parts are set to
// exactly the recorded ones. A match on an auxiliary function goes to the device's single main
// function, where parts live. A fixed device is never changed.
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        var snapshot = PlainJson.ReadSnapshot(ctx.Args, "snapshot", "parts", 1, project.ProjectName, ctx.Args.Bool("allow_other_project"));
        string? prefix = ctx.Args.String("page_prefix");
        bool promote = ctx.Args.Bool("promote_main");
        var result = Parts.Restore(ctx, project, snapshot, prefix, promote, dryRun: true);
        int planned = (int)result["would_change"]! + (int)result["would_promote_to_main"]!;
        if (!ctx.DryRun && planned > 0)
        {
            var refusal = Guards.OverLimit(planned, ctx.Args.Int("limit"), "functions to change");
            if (refusal != null) result["error"] = refusal;
            else result = Parts.Restore(ctx, project, snapshot, prefix, promote, dryRun: false);
        }
        var path = PlainJson.OutPath(ctx, ctx.Args.String("out"), "parts_restore.json");
        result["bytes"] = PlainJson.Write(path, result["rows"]);
        result["out"] = path;
        result.Remove("rows");
        result["project"] = project.ProjectName;
        return result;
    }
}
