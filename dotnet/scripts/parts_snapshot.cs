// @tool     parts_snapshot
// @desc     Records the parts (article references) of every function, placed or not, to a JSON file that parts_restore can put back.
// @schema   1
// @arg      page_prefix  string  optional   Only functions on pages whose name starts with this
// @arg      out          string  optional   JSON file to write (default parts_snapshot.json in the run folder)
// @writes   false
// @needs    session
//
// One row per function: page ("" when unplaced), dt, symbol, category, is_main, fixed, cp (connection
// point designations 1-4) and parts [{part, variant, count, pos, assembly}].
using System.Collections;
using System.Linq;
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        var rows = Parts.Snapshot(ctx, project, ctx.Args.String("page_prefix"));
        var path = PlainJson.OutPath(ctx, ctx.Args.String("out"), "parts_snapshot.json");
        long bytes = PlainJson.Write(path, rows);
        var withParts = rows.Where(r => ((ICollection)r["parts"]!).Count > 0).ToList();
        int readErrors = rows.Count(r => r.ContainsKey("error"));
        return new
        {
            project = project.ProjectFullName,
            functions = rows.Count,
            functions_with_parts = withParts.Count,
            part_references = withParts.Sum(r => ((ICollection)r["parts"]!).Count),
            read_errors = readErrors,
            samples = withParts.Take(5).ToList(),
            ok = readErrors == 0,
            @out = path,
            bytes,
        };
    }
}
