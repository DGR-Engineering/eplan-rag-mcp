using System;
using System.Collections.Generic;
using System.Linq;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using EplanMcp.Scripting;

namespace EplanMcp.Eplan
{
    /// <summary>
    /// Text size and alignment-box state of the texts on a page, and checked writes to them.
    /// <para>A property placement is addressed by (symbol_ord, pp_ord): its index among the page's
    /// symbol references and its index in that reference's PropertyPlacements, as
    /// <see cref="Snapshot"/> reports them. The displayed property id is not a usable key (a symbol
    /// may show the same property twice). Ordinals hold only until the page changes.</para>
    /// <para>Every write is judged by reading the value back, never by the absence of an
    /// exception.</para>
    /// </summary>
    public static class TextFit
    {
        /// <summary>EPLAN's "take the value from the layer" sentinel (height, rotation, colour...).</summary>
        public const double FromLayer = -16002.0;

        /// <summary>Font size as a property (#19017); can differ from <see cref="TextBase.Height"/>.</summary>
        public const int InstanceSize = 19017;

        /// <summary>Every symbol reference on the page (optionally only those whose symbol name
        /// starts with <paramref name="symbolPrefix"/>) with its property placements, and when
        /// <paramref name="includeTexts"/> the free texts.</summary>
        public static Dictionary<string, object?> Snapshot(Page page, string? symbolPrefix, bool includeTexts)
        {
            var data = new Dictionary<string, object?> { ["page"] = Safe.Text(() => page.Name) };
            var placements = Pages.Placements(page);
            var symbols = new List<object>();
            int ord = 0;
            foreach (var sr in placements.OfType<SymbolReference>())
            {
                var name = Safe.Text(() => sr.SymbolVariant?.SymbolName) ?? "";
                if (string.IsNullOrEmpty(symbolPrefix) || name.StartsWith(symbolPrefix, StringComparison.OrdinalIgnoreCase))
                    symbols.Add(DescribeSymbolReference(sr, ord));
                ord++;
            }
            data["symbols"] = symbols;
            data["symbol_count"] = symbols.Count;
            if (includeTexts)
            {
                var texts = new List<object>();
                int textOrd = 0;
                foreach (var t in placements.OfType<Text>())
                {
                    var item = DescribeText(t, textOrd++);
                    item["display_string"] = Safe.Text(() => t.GetDisplayString());
                    texts.Add(item);
                }
                data["texts"] = texts;
            }
            return data;
        }

        /// <summary>
        /// One symbol reference and its property placements. use_local_property_placements = false
        /// means the instance shows its VARIANT's placements, so a write may reach every other
        /// instance of that variant.
        /// </summary>
        public static Dictionary<string, object?> DescribeSymbolReference(SymbolReference sr, int symbolOrd)
        {
            var item = new Dictionary<string, object?>
            {
                ["symbol_ord"] = symbolOrd,
                ["symbol"] = Safe.Text(() => sr.SymbolVariant?.SymbolName),
                ["variant"] = Safe.Maybe(() => sr.SymbolVariant?.VariantNr ?? -1),
                ["dt"] = sr is Function f ? Functions.DeviceTag(f) : null,
                ["use_local_property_placements"] = Safe.Maybe(() => sr.UseLocalPropertyPlacements),
            };
            var pps = new List<object>();
            try
            {
                int ppOrd = 0;
                foreach (var pp in sr.PropertyPlacements ?? Array.Empty<PropertyPlacement>())
                {
                    var d = DescribeText(pp, ppOrd++);
                    // A placement from a default scheme has no project: it is not a project object.
                    d["is_project_object"] = Safe.Maybe(() => pp.Project != null);
                    d["displayed_property"] = Safe.Maybe(() => pp.DisplayedProperty.AsInt);
                    d["displayed_index"] = Safe.Maybe(() => (int)pp.DisplayedProperty.Index);
                    d["relation_index"] = Safe.Maybe(() => pp.RelationIndex);
                    d["display_string"] = Safe.Text(() => pp.GetDisplayString());
                    // The PropertyPlacement overload returns coordinates RELATIVE to the placement,
                    // Placement's own returns page coordinates: never mix the two. Empty text throws.
                    AddBox(d, "bbox_relative", () => pp.GetBoundingBox(true));
                    AddBox(d, "bbox_absolute", () => pp.GetBoundingBox(false));
                    pps.Add(d);
                }
            }
            catch (Exception ex) { item["pps_error"] = Safe.Error(ex); }
            item["pps"] = pps;
            item["pp_count"] = pps.Count;
            return item;
        }

        /// <summary>Height (and #19017), alignment-box flags and size, location, spacing and rotation
        /// of one text, plus whether all four preconditions of EPLAN's own fit are met (box, fixed
        /// width, fixed height, fitting permitted).</summary>
        public static Dictionary<string, object?> DescribeText(TextBase t, int ord)
        {
            var d = new Dictionary<string, object?>
            {
                ["ord"] = ord,
                ["type"] = Safe.Text(() => t.GetType().Name),
                ["height"] = Safe.Maybe(() => t.Height),
                ["height_is_from_layer"] = Safe.Maybe(() => Math.Abs(t.Height - FromLayer) < 0.0001),
                ["instance_size_19017"] = PropertyReader.Read(t, InstanceSize),
                ["has_box"] = Safe.Maybe(() => t.HasBox),
                ["draw_alignment_box"] = Safe.Text(() => t.DrawAlignmentBox.ToString()),
                ["box_fixed_width"] = Safe.Maybe(() => t.AlignmentBoxFixedWidth),
                ["box_fixed_height"] = Safe.Maybe(() => t.AlignmentBoxFixedHeight),
                ["box_width"] = Safe.Maybe(() => t.AlignmentBoxWidth),
                ["box_height"] = Safe.Maybe(() => t.AlignmentBoxHeight),
                ["fitting_permitted"] = Safe.Maybe(() => t.AlignmentBoxTextFittingPermitted),
                ["never_hyphenate"] = Safe.Maybe(() => t.AlignmentBoxNeverHyphenateWords),
                ["remove_breaks"] = Safe.Maybe(() => t.AlignmentBoxRemoveBreaks),
                ["location_x"] = Safe.Maybe(() => t.Location.X),
                ["location_y"] = Safe.Maybe(() => t.Location.Y),
                ["row_spacing"] = Safe.Maybe(() => t.RowSpacing),
                ["paragraph_spacing"] = Safe.Maybe(() => t.ParagraphSpacing),
                ["rotation"] = Safe.Maybe(() => t.Rotation),
            };
            d["fit_preconditions_met"] = (d["has_box"] as bool?) == true && (d["box_fixed_width"] as bool?) == true
                                         && (d["box_fixed_height"] as bool?) == true && (d["fitting_permitted"] as bool?) == true;
            return d;
        }

        /// <summary>The property placement at (<paramref name="symbolOrd"/>, <paramref name="ppOrd"/>)
        /// on the page, or null with the reason.</summary>
        public static PropertyPlacement? ResolvePlacement(Page page, int symbolOrd, int ppOrd, out SymbolReference? owner, out string? error)
        {
            owner = null;
            error = null;
            if (symbolOrd < 0 || ppOrd < 0) { error = "symbol_ord and pp_ord are required (both 0-based, as the snapshot reports them)"; return null; }
            var symbols = Pages.Placements(page).OfType<SymbolReference>().ToList();
            if (symbolOrd >= symbols.Count)
            {
                error = $"symbol_ord {symbolOrd} out of range - page '{Safe.Text(() => page.Name)}' has {symbols.Count} symbol reference(s)";
                return null;
            }
            owner = symbols[symbolOrd];
            PropertyPlacement[] pps;
            try { pps = owner.PropertyPlacements ?? Array.Empty<PropertyPlacement>(); }
            catch (Exception ex) { error = "PropertyPlacements: " + Safe.Error(ex); return null; }
            if (ppOrd >= pps.Length) { error = $"pp_ord {ppOrd} out of range - symbol_ord {symbolOrd} has {pps.Length} property placement(s)"; return null; }
            return pps[ppOrd];
        }

        /// <summary>
        /// Writes a height to the placement (<paramref name="store"/> "placement": TextBase.Height;
        /// "19017": the font-size property) and reads both back. Refuses a placement of a
        /// non-local symbol reference unless <paramref name="allowShared"/>: that write may change
        /// every instance of the variant.
        /// </summary>
        public static Dictionary<string, object?> SetHeight(ScriptContext ctx, PropertyPlacement pp, SymbolReference owner,
            double? height, double factor, string store, bool allowShared)
        {
            var data = new Dictionary<string, object?> { ["error"] = null, ["written"] = false, ["sticks"] = null };
            bool local = Safe.Maybe(() => owner.UseLocalPropertyPlacements) ?? false;
            data["use_local_property_placements"] = local;
            if (!local && !allowShared)
            {
                data["error"] = "refusing to write: this symbol reference shows its VARIANT's property placements " +
                                "(UseLocalPropertyPlacements == false), so the write may change every instance of the variant. " +
                                "Pass allow_shared=true to do it anyway.";
                return data;
            }

            double? before = Safe.Maybe(() => pp.Height);
            data["height_before"] = before;
            data["size_19017_before"] = PropertyReader.Read(pp, InstanceSize);
            double target;
            if (height.HasValue) target = height.Value;
            else if (before.HasValue) target = before.Value * factor;
            else { data["error"] = "current height unreadable and no explicit height given"; return data; }
            data["height_target"] = target;
            if (ctx.DryRun) { data["dry_run"] = true; return data; }

            ctx.Lock(pp);
            if (string.Equals(store, "19017", StringComparison.OrdinalIgnoreCase)) pp.Properties[(AnyPropertyId)InstanceSize] = target;
            else pp.Height = target;
            data["written"] = true;

            double? after = Safe.Maybe(() => pp.Height);
            data["height_after"] = after;
            data["size_19017_after"] = PropertyReader.Read(pp, InstanceSize);
            data["sticks"] = after.HasValue && Math.Abs(after.Value - target) < 0.0001;
            data["hint"] = "in-memory read-back only; check again after a save and a reopen, since a setter that does not persist throws nothing.";
            return data;
        }

        /// <summary>
        /// Writes the alignment-box settings present in <paramref name="args"/> (has_box,
        /// box_fixed_width, box_fixed_height, fitting_permitted, never_hyphenate, remove_breaks,
        /// box_width, box_height), each read back on its own:
        /// {applied: [{field, requested, after, sticks, error?}]}. Same shared-variant guard as
        /// <see cref="SetHeight"/>.
        /// </summary>
        public static Dictionary<string, object?> SetBox(ScriptContext ctx, PropertyPlacement pp, SymbolReference owner, ScriptArgs args, bool allowShared)
        {
            var applied = new List<object>();
            var data = new Dictionary<string, object?> { ["error"] = null, ["applied"] = applied };
            bool local = Safe.Maybe(() => owner.UseLocalPropertyPlacements) ?? false;
            data["use_local_property_placements"] = local;
            if (!local && !allowShared) { data["error"] = "refusing to write a non-local placement; pass allow_shared=true to do it anyway."; return data; }
            if (ctx.DryRun) { data["dry_run"] = true; return data; }

            ctx.Lock(pp);
            ApplyBool(args, "has_box", applied, v => pp.HasBox = v, () => pp.HasBox);
            ApplyBool(args, "box_fixed_width", applied, v => pp.AlignmentBoxFixedWidth = v, () => pp.AlignmentBoxFixedWidth);
            ApplyBool(args, "box_fixed_height", applied, v => pp.AlignmentBoxFixedHeight = v, () => pp.AlignmentBoxFixedHeight);
            ApplyBool(args, "fitting_permitted", applied, v => pp.AlignmentBoxTextFittingPermitted = v, () => pp.AlignmentBoxTextFittingPermitted);
            ApplyBool(args, "never_hyphenate", applied, v => pp.AlignmentBoxNeverHyphenateWords = v, () => pp.AlignmentBoxNeverHyphenateWords);
            ApplyBool(args, "remove_breaks", applied, v => pp.AlignmentBoxRemoveBreaks = v, () => pp.AlignmentBoxRemoveBreaks);
            ApplyDouble(args, "box_width", applied, v => pp.AlignmentBoxWidth = v, () => pp.AlignmentBoxWidth);
            ApplyDouble(args, "box_height", applied, v => pp.AlignmentBoxHeight = v, () => pp.AlignmentBoxHeight);
            return data;
        }

        private static void ApplyBool(ScriptArgs args, string key, List<object> applied, Action<bool> write, Func<bool> read)
        {
            if (!args.Has(key)) return;
            bool requested = args.Bool(key);
            var row = new Dictionary<string, object?> { ["field"] = key, ["requested"] = requested };
            try
            {
                write(requested);
                bool? after = Safe.Maybe(read);
                row["after"] = after;
                row["sticks"] = after == requested;
            }
            catch (Exception ex) { row["error"] = Safe.Error(ex); row["sticks"] = false; }
            applied.Add(row);
        }

        private static void ApplyDouble(ScriptArgs args, string key, List<object> applied, Action<double> write, Func<double> read)
        {
            if (!args.Has(key)) return;
            double requested = args.Number(key);
            var row = new Dictionary<string, object?> { ["field"] = key, ["requested"] = requested };
            try
            {
                write(requested);
                double? after = Safe.Maybe(read);
                row["after"] = after;
                row["sticks"] = after.HasValue && Math.Abs(after.Value - requested) < 0.0001;
            }
            catch (Exception ex) { row["error"] = Safe.Error(ex); row["sticks"] = false; }
            applied.Add(row);
        }

        private static void AddBox(Dictionary<string, object?> d, string key, Func<PointD[]> read)
        {
            try
            {
                var box = read();
                if (box == null || box.Length < 2) { d[key] = null; return; }
                d[key] = new Dictionary<string, object?>
                {
                    ["x1"] = box[0].X, ["y1"] = box[0].Y, ["x2"] = box[1].X, ["y2"] = box[1].Y,
                    ["w"] = box[1].X - box[0].X, ["h"] = box[1].Y - box[0].Y,
                };
            }
            catch (Exception ex) { d[key] = null; d[key + "_error"] = Safe.Error(ex); }
        }
    }
}
