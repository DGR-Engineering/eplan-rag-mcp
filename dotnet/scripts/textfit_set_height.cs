// @tool     textfit_set_height
// @desc     Writes a height to one property placement (TextBase.Height or the #19017 font size) and reports whether it sticks, by reading back.
// @schema   1
// @arg      page          string  required   Page name (exact, else the first page whose name contains it)
// @arg      symbol_ord    int     required   Symbol reference ordinal from textfit_snapshot
// @arg      pp_ord        int     required   Property placement ordinal from textfit_snapshot
// @arg      height        number  optional   Absolute height in mm; -16002 means "from layer"
// @arg      factor        number  optional   Or a multiple of the current height (default 0.8)
// @arg      target        enum(placement,19017) default=placement
// @arg      allow_shared  bool    optional   Allow writing a placement of a non-local symbol reference (may change every instance of the variant)
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
        double? height = ctx.Args.Has("height") ? ctx.Args.Number("height") : (double?)null;
        return TextFit.SetHeight(ctx, pp, owner, height, ctx.Args.Number("factor", 0.8),
            ctx.Args.String("target", "placement")!, ctx.Args.Bool("allow_shared"));
    }
}
