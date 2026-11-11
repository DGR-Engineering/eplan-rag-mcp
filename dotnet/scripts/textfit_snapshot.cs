// @tool     textfit_snapshot
// @desc     Every property placement on a page with its height, font size (#19017), alignment-box settings, bounding boxes and fit preconditions, addressed by (symbol_ord, pp_ord).
// @schema   1
// @arg      page           string  required   Page name (exact, else the first page whose name contains it)
// @arg      symbol_prefix  string  optional   Only symbol references whose symbol name starts with this (ordinals still count all)
// @arg      include_texts  bool    optional   Also list the free texts (default true)
// @writes   false
// @needs    session
//
// Height and #19017 are shown side by side: if a fit changes one and not the other, the original
// size was kept. use_local_property_placements = false means a write would hit the variant's
// shared placements.
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var page = Pages.Require(ctx.RequireProject(), ctx.Args.Require("page"), allowPartial: true);
        return TextFit.Snapshot(page, ctx.Args.String("symbol_prefix"), ctx.Args.Bool("include_texts", true));
    }
}
