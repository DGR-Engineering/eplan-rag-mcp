using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using EplanMcp.Scripting;
using EplanMcp.Scripting.Core;

namespace EplanMcp.Eplan
{
    /// <summary>
    /// JSON as plain CLR data: objects become Dictionary&lt;string, object?&gt;, arrays List&lt;object?&gt;,
    /// numbers double, plus string, bool and null. For snapshot files and for arguments that carry
    /// records (a list of moves, the rows of a survey).
    /// </summary>
    public static class PlainJson
    {
        /// <summary>Parses JSON text into plain data.</summary>
        public static object? Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return From(doc.RootElement);
        }

        /// <summary>A JSON array of objects as rows; anything else in the array is skipped.</summary>
        public static List<Dictionary<string, object?>> Rows(string? json) =>
            string.IsNullOrWhiteSpace(json) ? new List<Dictionary<string, object?>>() : Rows(Parse(json!));

        /// <summary>The objects in a parsed array.</summary>
        public static List<Dictionary<string, object?>> Rows(object? value) =>
            (value as IEnumerable<object?>)?.OfType<Dictionary<string, object?>>().ToList() ?? new List<Dictionary<string, object?>>();

        /// <summary>
        /// Rows from the argument <paramref name="rowsArg"/> (inline JSON array) or, only when that
        /// argument is absent, from the file named by <paramref name="fileArg"/>. Throws when neither
        /// is given, or when <paramref name="rowsArg"/> is given but holds no rows.
        /// </summary>
        public static List<Dictionary<string, object?>> RowsFromArgs(ScriptArgs args, string rowsArg, string fileArg)
        {
            if (args.Has(rowsArg)) return RequireRows(args, rowsArg);
            var file = args.String(fileArg);
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                throw new ArgumentException($"Pass '{rowsArg}' (a JSON array) or '{fileArg}' (an existing file).");
            return Rows(File.ReadAllText(file!, Encoding.UTF8));
        }

        /// <summary>
        /// The rows in argument <paramref name="name"/>: a JSON array of objects, or a string holding
        /// one. Throws when the argument is missing, is not JSON, or yields no objects, so a mistyped
        /// argument never reads as "nothing to do".
        /// </summary>
        public static List<Dictionary<string, object?>> RequireRows(ScriptArgs args, string name)
        {
            var raw = args.Json(name);
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException($"Missing argument '{name}' (a JSON array of objects).");
            object? value;
            try
            {
                value = Parse(raw!);
                if (value is string text) value = Parse(text);   // an array sent as JSON text
            }
            catch (JsonException ex) { throw new ArgumentException($"Argument '{name}' is not a JSON array of objects: {ex.Message}"); }
            var rows = Rows(value);
            if (rows.Count == 0)
                throw new ArgumentException($"Argument '{name}' was given but holds no objects; pass a JSON array of objects such as [{{\"name\": ...}}].");
            return rows;
        }

        /// <summary>Writes <paramref name="value"/> as JSON (UTF-8, no BOM), creating the folder;
        /// returns the byte count.</summary>
        public static long Write(string path, object? value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Json.ToPlain(value)?.ToJsonString() ?? "null", new UTF8Encoding(false));
            return new FileInfo(path).Length;
        }

        /// <summary>The path to write a run's output to: <paramref name="requested"/> when given
        /// (relative to the run folder), else <paramref name="defaultName"/> in the run folder. A run
        /// that may not write is kept inside its run folder; a writing run never writes into an EPLAN
        /// project (*.edb) or onto a .elk/.slk/.sdb file. Throws on a refused path.</summary>
        public static string OutPath(ScriptContext ctx, string? requested, string defaultName) =>
            OutputPath.Resolve(ctx.RunFolder, ctx.WriteAllowed, requested, defaultName);

        /// <summary>A row value as text ("" when absent).</summary>
        public static string Str(IDictionary<string, object?> row, string key) =>
            row.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

        /// <summary>A row value as a number (0 when absent or not numeric).</summary>
        public static double Num(IDictionary<string, object?> row, string key)
        {
            if (!row.TryGetValue(key, out var v) || v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        /// <summary>A row value as a flag: true, "true" or "1".</summary>
        public static bool Flag(IDictionary<string, object?> row, string key)
        {
            if (!row.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        private static object? From(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => From(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().Select(From).ToList(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
