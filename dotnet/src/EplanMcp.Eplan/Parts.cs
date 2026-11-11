using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eplan.EplApi.DataModel;
using EplanMcp.Scripting;

namespace EplanMcp.Eplan
{
    /// <summary>
    /// Snapshot and restore of the parts (article references) on functions, for putting parts back
    /// after an operation that drops them. A function is matched by (page, device tag, symbol,
    /// category, connection point designations 1-4), then loosely by (page, device tag, symbol).
    /// </summary>
    public static class Parts
    {
        /// <summary>FUNC_CONNECTIONDESIGNATION, indexed by connection point.</summary>
        public const int ConnectionDesignation = 20022;

        /// <summary>One function: {page, dt, symbol, category, is_main, fixed, cp[4], parts[], error?}.
        /// An unplaced function has page "".</summary>
        public static Dictionary<string, object?> Row(Function f)
        {
            var page = Safe.Object(() => f.Page);
            var cp = new List<string>();
            for (int i = 1; i <= 4; i++)
            {
                int index = i;
                cp.Add(Safe.Text(() => f.Properties[(AnyPropertyId)ConnectionDesignation, index].ToString()) ?? "");
            }
            var row = new Dictionary<string, object?>
            {
                ["page"] = page == null ? "" : Safe.Text(() => page.Name) ?? "",
                ["dt"] = Functions.DeviceTag(f) ?? "",
                ["symbol"] = Functions.SymbolName(f),
                ["category"] = Safe.Text(() => f.FunctionCategory.ToString()) ?? "",
                ["is_main"] = Safe.Get(() => f.IsMainFunction, false),
                ["fixed"] = Safe.Get(() => f.IsFixedDevice, false),
                ["cp"] = cp,
            };
            var parts = new List<Dictionary<string, object?>>();
            try { parts = Read(f); }
            catch (Exception ex) { row["error"] = Safe.Error(ex); }
            row["parts"] = parts;
            return row;
        }

        /// <summary>The function's article references: [{part, variant, count, pos, assembly}].</summary>
        public static List<Dictionary<string, object?>> Read(Function f) =>
            (f.ArticleReferences ?? Array.Empty<ArticleReference>()).Select(ar => new Dictionary<string, object?>
            {
                ["part"] = Safe.Text(() => ar.PartNr) ?? "",
                ["variant"] = Safe.Text(() => ar.VariantNr) ?? "",
                ["count"] = Safe.Get(() => ar.Count, 0),
                ["pos"] = Safe.Get(() => ar.ReferencePos, 0),
                ["assembly"] = Safe.Get(() => ar.IsAssembly, false),
            }).ToList();

        /// <summary>A <see cref="Row"/> for every function (placed or not) whose page starts with
        /// <paramref name="pagePrefix"/> (all when empty).</summary>
        public static List<Dictionary<string, object?>> Snapshot(ScriptContext ctx, Project project, string? pagePrefix)
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var f in Functions.All(project))
            {
                ctx.Budget.Check();
                var row = Row(f);
                if (!string.IsNullOrEmpty(pagePrefix) && !((string)row["page"]!).StartsWith(pagePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Puts snapshot parts back. Only snapshot rows WITH parts are considered; a row without
        /// parts never removes anything. Parts live on the main function, so a match on an
        /// auxiliary is redirected to the device's single main; with <paramref name="promoteMain"/>,
        /// a function the snapshot recorded as main, whose device tag now has no main at all, is
        /// made main again. A fixed device is never changed. In a dry run nothing is written and
        /// the counts say what would change. Returns the summary; the per-row outcomes are in
        /// "rows".
        /// </summary>
        public static Dictionary<string, object?> Restore(ScriptContext ctx, Project project,
            List<Dictionary<string, object?>> snapshot, string? pagePrefix, bool promoteMain)
        {
            bool dryRun = ctx.DryRun;
            string prefix = pagePrefix ?? "";
            var byFull = new Dictionary<string, List<Function>>(StringComparer.Ordinal);
            var byLoose = new Dictionary<string, List<Function>>(StringComparer.Ordinal);
            var mainByDt = new Dictionary<string, List<Function>>(StringComparer.Ordinal);
            var rowOf = new Dictionary<Function, Dictionary<string, object?>>();
            foreach (var f in Functions.All(project))
            {
                ctx.Budget.Check();
                var r = Row(f);
                rowOf[f] = r;
                string page = (string)r["page"]!, dt = (string)r["dt"]!, sym = (string)r["symbol"]!;
                Add(byFull, FullKey(page, dt, sym, (string)r["category"]!, (List<string>)r["cp"]!), f);
                Add(byLoose, LooseKey(page, dt, sym), f);
                if ((bool)r["is_main"]! && dt.Length > 0) Add(mainByDt, dt, f);
            }

            var rows = new List<Dictionary<string, object?>>();
            var used = new HashSet<Function>();
            int considered = 0, matched = 0, unmatched = 0, ambiguous = 0, unchanged = 0, changed = 0, failed = 0, redirected = 0, promoted = 0;

            foreach (var s in snapshot)
            {
                ctx.Budget.Check();
                var sParts = AsRows(s.TryGetValue("parts", out var p) ? p : null);
                if (sParts.Count == 0) continue;
                string page = PlainJson.Str(s, "page"), dt = PlainJson.Str(s, "dt"), sym = PlainJson.Str(s, "symbol");
                if (prefix.Length > 0 && !page.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                considered++;
                var cp = (s.TryGetValue("cp", out var c) ? c as IEnumerable : null)?.Cast<object?>()
                         .Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").ToList() ?? new List<string>();
                var row = new Dictionary<string, object?> { ["page"] = page, ["dt"] = dt, ["symbol"] = sym, ["want"] = sParts };
                rows.Add(row);

                var target = Pick(byFull, FullKey(page, dt, sym, PlainJson.Str(s, "category"), cp), "exact", out var how)
                          ?? Pick(byLoose, LooseKey(page, dt, sym), "loose", out how);
                if (target == null)
                {
                    if (how == "ambiguous") ambiguous++; else unmatched++;
                    row["action"] = how == "ambiguous" ? "ambiguous" : "unmatched";
                    continue;
                }
                if (!(bool)rowOf[target]["is_main"]!)
                {
                    bool hasMains = mainByDt.TryGetValue(dt, out var mains) && mains.Count > 0;
                    bool wasMain = PlainJson.Flag(s, "is_main");
                    if (hasMains && mains!.Count == 1) { target = mains[0]; how += "+main_redirect"; redirected++; }
                    else if (promoteMain && !hasMains && wasMain && dt.Length > 0)
                    {
                        if (!dryRun)
                        {
                            try
                            {
                                ctx.Lock(target);
                                target.IsMainFunction = true;
                                if (!Safe.Get(() => target.IsMainFunction, false)) { failed++; row["action"] = "failed: promote to main read back false"; continue; }
                            }
                            catch (Exception ex) { failed++; row["action"] = "failed: promote to main: " + Safe.Error(ex); continue; }
                            rowOf[target]["is_main"] = true;
                        }
                        Add(mainByDt, dt, target);   // never promote a second one for this device tag
                        how += "+promoted_main";
                        promoted++;
                    }
                    else { unmatched++; row["action"] = "unmatched: matched an auxiliary and the device has no single main"; continue; }
                }
                if (!used.Add(target)) { ambiguous++; row["action"] = "skipped: target already restored from another snapshot row"; continue; }
                matched++;
                row["match"] = how;

                var have = (List<Dictionary<string, object?>>)rowOf[target]["parts"]!;
                row["have"] = have;
                var wantBag = Bag(sParts);
                if (SameBag(wantBag, Bag(have))) { unchanged++; row["action"] = "unchanged"; continue; }
                if ((bool)rowOf[target]["fixed"]!) { failed++; row["action"] = "failed: device is fixed (IsFixedDevice)"; continue; }
                if (dryRun) { changed++; row["action"] = "would_set"; continue; }

                try
                {
                    ctx.Lock(target);
                    var remaining = new Dictionary<string, int>(wantBag, StringComparer.OrdinalIgnoreCase);
                    foreach (var ar in target.ArticleReferences ?? Array.Empty<ArticleReference>())
                    {
                        string k = ar.PartNr + "\u0001" + ar.VariantNr;
                        if (remaining.TryGetValue(k, out int n))
                        {
                            if (ar.Count != n) { ar.Count = n; ar.StoreToObject(); }
                            remaining.Remove(k);
                        }
                        else target.RemoveArticleReference(ar);
                    }
                    foreach (var kv in remaining)
                    {
                        var pv = kv.Key.Split('\u0001');
                        target.AddArticleReference(pv[0], pv[1], (uint)Math.Max(kv.Value, 1));
                    }
                    var after = Read(target);
                    row["after"] = after;
                    if (SameBag(wantBag, Bag(after))) { changed++; row["action"] = "set"; }
                    else { failed++; row["action"] = "failed: read-back differs"; }
                }
                catch (Exception ex) { failed++; row["action"] = "failed: " + Safe.Error(ex); }
            }

            string Action(Dictionary<string, object?> r) => r.TryGetValue("action", out var a) ? Convert.ToString(a) ?? "" : "";
            return new Dictionary<string, object?>
            {
                ["dry_run"] = dryRun,
                ["promote_main"] = promoteMain,
                ["snapshot_rows"] = snapshot.Count,
                ["considered_with_parts"] = considered,
                ["matched"] = matched,
                ["redirected_to_main"] = redirected,
                [dryRun ? "would_promote_to_main" : "promoted_to_main"] = promoted,
                ["unmatched"] = unmatched,
                ["ambiguous"] = ambiguous,
                ["unchanged"] = unchanged,
                [dryRun ? "would_change" : "changed"] = changed,
                ["failed"] = failed,
                ["samples_changed"] = rows.Where(r => Action(r) is "set" or "would_set").Take(8).ToList(),
                ["samples_unmatched"] = rows.Where(r => Action(r).StartsWith("unmatched") || Action(r) == "ambiguous").Take(8).ToList(),
                ["samples_failed"] = rows.Where(r => Action(r).StartsWith("failed")).Take(8).ToList(),
                ["ok"] = failed == 0,
                ["rows"] = rows,
            };
        }

        /// <summary>(part, variant) -&gt; summed count.</summary>
        public static Dictionary<string, int> Bag(IEnumerable<Dictionary<string, object?>> parts)
        {
            var bag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in parts)
            {
                string k = PlainJson.Str(p, "part") + "\u0001" + PlainJson.Str(p, "variant");
                bag[k] = (bag.TryGetValue(k, out int n) ? n : 0) + (int)PlainJson.Num(p, "count");
            }
            return bag;
        }

        /// <summary>True when both bags hold the same parts with the same counts.</summary>
        public static bool SameBag(Dictionary<string, int> a, Dictionary<string, int> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out int n) && n == kv.Value);

        private static string FullKey(string page, string dt, string symbol, string category, IEnumerable<string> cp) =>
            string.Join("|", new[] { page, dt, symbol, category }.Concat(cp));

        private static string LooseKey(string page, string dt, string symbol) => page + "|" + dt + "|" + symbol;

        private static void Add(Dictionary<string, List<Function>> index, string key, Function f)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new List<Function>();
            list.Add(f);
        }

        /// <summary>One candidate, else the single main among several. <paramref name="how"/>:
        /// the kind on a hit, "ambiguous" when several remain, "none" when the key is absent.</summary>
        private static Function? Pick(Dictionary<string, List<Function>> index, string key, string kind, out string how)
        {
            how = "none";
            if (!index.TryGetValue(key, out var list) || list.Count == 0) return null;
            if (list.Count == 1) { how = kind; return list[0]; }
            var mains = list.Where(f => Safe.Get(() => f.IsMainFunction, false)).ToList();
            if (mains.Count == 1) { how = kind + "+main_of_" + list.Count; return mains[0]; }
            how = "ambiguous";
            return null;
        }

        private static List<Dictionary<string, object?>> AsRows(object? value) =>
            (value as IEnumerable)?.OfType<Dictionary<string, object?>>().ToList() ?? new List<Dictionary<string, object?>>();
    }
}
