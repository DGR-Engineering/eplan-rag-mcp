// @tool     macrobox_restore
// @desc     Re-creates the macro boxes a project is missing from macrobox_survey rows; existing boxes are re-styled, never duplicated. A dry run reports the plan.
// @schema   1
// @arg      from_file        string  optional   A macrobox_survey JSON file (or pass rows)
// @arg      rows             list    optional   Survey rows inline, instead of from_file
// @arg      update_existing  bool    optional   Re-apply flags, drawing order and appearance to boxes that already exist (default true)
// @writes   true
// @needs    session
//
// A box is identified by page, macro, variant and its rectangle (rounded to 0.01 mm), so several
// boxes of one macro on one page stay distinct. Safe to re-run.
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        var rows = PlainJson.RowsFromArgs(ctx.Args, "rows", "from_file");
        var result = MacroBoxes.Restore(ctx, project, rows, ctx.Args.Bool("update_existing", true));
        result["project"] = project.ProjectFullName;
        return result;
    }
}
