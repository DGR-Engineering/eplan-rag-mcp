using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EplanMcp.Scripting.Core
{
    /// <summary>
    /// Append-only record of every script run, under %LOCALAPPDATA%\EplanMcp\audit (or
    /// EPLAN_MCP_AUDIT_DIR). The start line is written BEFORE the script runs, because a crashed
    /// script takes EPLAN down with it and nothing after that point is written. Each distinct source
    /// is kept once, named by its hash.
    /// </summary>
    public sealed class AuditLog
    {
        public string Directory { get; }

        public AuditLog(string? directory = null)
        {
            Directory = directory
                ?? Environment.GetEnvironmentVariable("EPLAN_MCP_AUDIT_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EplanMcp", "audit");
        }

        public void Start(string runId, string sourceHash, string source, IDictionary<string, object?> fields) =>
            Safe(() =>
            {
                var sources = Path.Combine(Directory, "sources");
                System.IO.Directory.CreateDirectory(sources);
                var copy = Path.Combine(sources, sourceHash + ".cs");
                if (!File.Exists(copy)) File.WriteAllText(copy, source, Encoding.UTF8);
                Append("start", runId, fields);
            });

        public void End(string runId, IDictionary<string, object?> fields) => Safe(() => Append("end", runId, fields));

        private void Append(string ev, string runId, IDictionary<string, object?> fields)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var line = new Dictionary<string, object?>(fields)
            {
                ["event"] = ev,
                ["run_id"] = runId,
                ["at"] = DateTime.UtcNow.ToString("o"),
                ["user"] = Environment.UserName,
                ["machine"] = Environment.MachineName,
                ["pid"] = Environment.ProcessId,
            };
            var file = Path.Combine(Directory, "audit-" + DateTime.UtcNow.ToString("yyyy-MM") + ".jsonl");
            File.AppendAllText(file, Json.Serialize(line) + "\n", Encoding.UTF8);
        }

        // Auditing must never be the reason a run fails.
        private static void Safe(Action a)
        {
            try { a(); } catch { }
        }
    }
}
