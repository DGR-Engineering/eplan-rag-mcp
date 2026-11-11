// @tool     text_create
// @desc     Creates a free text on a page with Text.Create and reads its contents back with every character code, to see how line breaks are stored. read_back has EPLAN's language prefix removed; read_back_raw is the text as stored.
// @schema   1
// @arg      page    string  required   Page Name or IdentifyingName (exact)
// @arg      text    string  optional   The text (default empty)
// @arg      x       number  optional   Page x in mm (default 100)
// @arg      y       number  optional   Page y in mm (default 100)
// @arg      height  number  optional   Text height in mm (default 2.5)
// @writes   true
// @needs    session
using System.Linq;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel.Graphics;
using EplanMcp.Eplan;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var project = ctx.RequireProject();
        var page = Pages.Require(project, ctx.Args.Require("page"));
        string text = ctx.Args.String("text", "")!;
        if (ctx.DryRun) return new { dry_run = true, page = page.Name, sent_codes = text.Select(c => (int)c).ToList() };

        ctx.Lock(page);
        var t = new Text();
        t.Create(page, text, ctx.Args.Number("height", 2.5));
        t.Location = new PointD(ctx.Args.Number("x", 100.0), ctx.Args.Number("y", 100.0));
        string raw = t.Contents?.ToString() ?? "";
        string back = Safe.PlainText(raw);
        return new
        {
            page = page.Name,
            sent_codes = text.Select(c => (int)c).ToList(),
            read_back = back,
            read_back_raw = raw,
            read_back_codes = back.Select(c => (int)c).ToList(),
        };
    }
}
