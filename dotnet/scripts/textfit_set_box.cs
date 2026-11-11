// @tool     textfit_set_box
// @desc     Writes the given alignment-box settings of one property placement and reads each back; only the settings passed are written.
// @schema   1
// @arg      page               string  required   Page name (exact, else the first page whose name contains it)
// @arg      symbol_ord         int     required   Symbol reference ordinal from textfit_snapshot
// @arg      pp_ord             int     required   Property placement ordinal from textfit_snapshot
// @arg      has_box            bool    optional
// @arg      box_fixed_width    bool    optional
// @arg      box_fixed_height   bool    optional
// @arg      fitting_permitted  bool    optional
// @arg      never_hyphenate    bool    optional
// @arg      remove_breaks      bool    optional
// @arg      box_width          number  optional
// @arg      box_height         number  optional
// @arg      allow_shared       bool    optional   Allow writing a placement of a non-local symbol reference
// @writes   true
// @needs    session
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var page = Pages.Require(ctx.RequireProject(), ctx.Args.Require("page"), allowPartial: true);
        var pp = TextFit.ResolvePlacement(page, (int)ctx.Args.Int("symbol_ord", -1), (int)ctx.Args.Int("pp_ord", -1), out var owner, out var error);
        if (pp == null || owner == null) return new { error };
        return TextFit.SetBox(ctx, pp, owner, ctx.Args, ctx.Args.Bool("allow_shared"));
    }
}
