using System;
using System.Collections.Generic;
using System.IO;
using EplanMcp.Eplan;
using EplanMcp.Scripting;
using EplanMcp.Scripting.Core;
using Xunit;

namespace EplanMcp.Scripting.Tests
{
    /// <summary>The write guards of the library scripts: the bulk-write limit and the snapshot
    /// envelope a restore checks before it writes. Plain data only; EPLAN is never started.</summary>
    public class GuardTests
    {
        [Fact]
        public void Bulk_write_refuses_nothing_too_many_and_a_bad_limit()
        {
            Assert.Null(Guards.OverLimit(3, 3, "lines"));
            Assert.Contains("nothing matched", Guards.OverLimit(0, 10, "lines"));
            Assert.Contains("more than limit=2", Guards.OverLimit(3, 2, "lines"));
            Assert.Contains("at least 1", Guards.OverLimit(1, 0, "lines"));
        }

        private static Dictionary<string, object?> Envelope(string kind = "parts", object? schema = null, string project = "Demo",
            List<object?>? rows = null) => new Dictionary<string, object?>
        {
            ["kind"] = kind, ["schema"] = schema ?? 1.0, ["project"] = project,
            ["rows"] = rows ?? new List<object?> { new Dictionary<string, object?> { ["page"] = "1" } },
        };

        [Fact]
        public void Snapshot_is_checked_for_kind_schema_project_and_rows()
        {
            Assert.Null(Guards.CheckSnapshot(Envelope(), "parts", 1, "demo", false, out var rows));
            Assert.Single(rows);
            Assert.Contains("bare array", Guards.CheckSnapshot(new List<object?> { new Dictionary<string, object?>() }, "parts", 1, "Demo", false, out _));
            Assert.Contains("not a 'parts' snapshot", Guards.CheckSnapshot(Envelope(kind: "macrobox"), "parts", 1, "Demo", false, out _));
            Assert.Contains("schema 2", Guards.CheckSnapshot(Envelope(schema: 2.0), "parts", 1, "Demo", false, out _));
            Assert.Contains("schema 0", Guards.CheckSnapshot(Envelope(schema: "x"), "parts", 1, "Demo", false, out _));
            Assert.Contains("allow_other_project", Guards.CheckSnapshot(Envelope(project: "Other"), "parts", 1, "Demo", false, out _));
            Assert.Null(Guards.CheckSnapshot(Envelope(project: "Other"), "parts", 1, "Demo", true, out _));
            Assert.Contains("no rows", Guards.CheckSnapshot(Envelope(rows: new List<object?>()), "parts", 1, "Demo", false, out _));
        }

        [Fact]
        public void Snapshot_file_round_trips_and_a_foreign_file_throws()
        {
            ScriptArgs Args(string file) => new ScriptArgs(Json.Deserialize<Dictionary<string, object?>>("{\"snapshot\":" + Json.Serialize(file) + "}"));
            var file = Path.GetTempFileName();
            try
            {
                PlainJson.Write(file, Guards.SnapshotEnvelope("parts", 1, "Demo", @"C:\x\Demo.elk",
                    new List<Dictionary<string, object?>> { new Dictionary<string, object?> { ["page"] = "1", ["dt"] = "K1" } }));
                var rows = PlainJson.ReadSnapshot(Args(file), "snapshot", "parts", 1, "Demo", false);
                Assert.Equal("K1", rows[0]["dt"]);
                Assert.Throws<ArgumentException>(() => PlainJson.ReadSnapshot(Args(file), "snapshot", "macrobox", 1, "Demo", false));
                Assert.Throws<ArgumentException>(() => PlainJson.ReadSnapshot(Args(file), "snapshot", "parts", 1, "Copy", false));
                File.WriteAllText(file, "[{\"page\":\"1\"}]");   // an older, envelope-less file
                Assert.Throws<ArgumentException>(() => PlainJson.ReadSnapshot(Args(file), "snapshot", "parts", 1, "Demo", false));
                Assert.Throws<ArgumentException>(() => PlainJson.ReadSnapshot(Args(file + ".missing"), "snapshot", "parts", 1, "Demo", false));
            }
            finally { File.Delete(file); }
        }
    }
}
