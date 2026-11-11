// @tool     macrobox_survey
// @desc     Records every macro box in the project (identity, flags, geometry, layer, pen, texts) to a JSON file that macrobox_restore can re-create them from.
// @schema   1
// @arg      out  string  optional   JSON file to write (default macrobox_survey.json in the run folder)
// @writes   false
// @needs    session
//
// A macro box is its own placement type, so a check that counts functions, texts and property
// placements cannot see one go missing. Run this before and after any change that can delete
// placed objects (removing master data, for example) and compare.
using System;
using System.Linq;
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        var (rows, problems, pages) = MacroBoxes.Survey(ctx, project);
        var path = PlainJson.OutPath(ctx, ctx.Args.String("out"), "macrobox_survey.json");
        long bytes = PlainJson.Write(path, rows);
        return new
        {
            project = project.ProjectFullName,
            pages,
            macro_boxes = rows.Count + problems.Count,
            failed = problems.Count,
            problems,
            distinct_macros = rows.Select(r => PlainJson.Str(r, "macro")).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ok = problems.Count == 0,
            @out = path,
            bytes,
        };
    }
}
