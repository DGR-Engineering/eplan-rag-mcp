// @tool     graphic_pens
// @desc     The pen (style, style factor, width, colour, pattern) and layer of every line and arc inside an area of a page; with style or factor, writes them first and reads back.
// @schema   1
// @arg      page    string  required   Page Name or IdentifyingName (exact)
// @arg      x0      number  optional   Area left in mm (default 0)
// @arg      y0      number  optional   Area bottom in mm (default 0)
// @arg      x1      number  optional   Area right in mm (default unbounded)
// @arg      y1      number  optional   Area top in mm (default unbounded)
// @arg      style   int     optional   Line style id to write
// @arg      factor  number  optional   Line style factor to write
// @writes   true
// @needs    session
//
// Without style or factor nothing is written; the run still needs write permission because the
// script can write. At most 60 graphics are reported.
using System;
using System.Collections.Generic;
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
        double x0 = ctx.Args.Number("x0", 0), y0 = ctx.Args.Number("y0", 0), x1 = ctx.Args.Number("x1", 1e9), y1 = ctx.Args.Number("y1", 1e9);
        bool setStyle = ctx.Args.Has("style"), setFactor = ctx.Args.Has("factor");
        bool write = (setStyle || setFactor) && !ctx.DryRun;

        var rows = new List<object>();
        foreach (var g in Pages.Placements(page).OfType<GraphicalPlacement>())
        {
            if (!(g is Line) && !(g is Arc)) continue;
            PointD a, b;
            try { var bb = g.GetBoundingBox(); a = bb[0]; b = bb[1]; } catch { continue; }
            if (a.X < x0 || a.Y < y0 || b.X > x1 || b.Y > y1) continue;
            var row = new Dictionary<string, object?> { ["type"] = g.GetType().Name, ["box"] = $"({a.X:0.##},{a.Y:0.##})-({b.X:0.##},{b.Y:0.##})" };
            try
            {
                if (write)
                {
                    ctx.Lock(g);
                    g.LockObject();
                    var pen = g.Pen;
                    if (setStyle) pen.StyleId = (short)ctx.Args.Int("style");
                    if (setFactor) pen.StyleFactor = ctx.Args.Number("factor");
                    g.Pen = pen;   // the pen is a copy: assign it back
                }
                var p = g.Pen;
                row["style"] = (int)p.StyleId; row["factor"] = p.StyleFactor; row["width"] = p.Width;
                row["colour"] = (int)p.ColorId; row["pattern"] = p.PatternLength;
            }
            catch (Exception ex) { row["pen_error"] = Safe.Error(ex); }
            row["layer"] = Safe.Text(() => g.Layer?.Name);
            rows.Add(row);
            if (rows.Count >= 60) break;
        }
        return new { page = page.Name, written = write, graphics = rows };
    }
}
