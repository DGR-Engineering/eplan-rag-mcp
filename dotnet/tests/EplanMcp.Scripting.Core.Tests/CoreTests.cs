using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using EplanMcp.Contracts;
using EplanMcp.Scripting.Core;
using Xunit;

namespace EplanMcp.Scripting.Core.Tests
{
    /// <summary>Stand-in for the EPLAN ScriptContext: the pipeline only needs some object to pass.</summary>
    public sealed class TestContext
    {
        public Dictionary<string, object?> Args { get; } = new Dictionary<string, object?>();
        public List<string> Log { get; set; } = new List<string>();
        public Budget Budget { get; set; } = new Budget(5, null);
        public void Write(string s) => Log.Add(s);
    }

    internal sealed class TestEnvironment : IRunEnvironment
    {
        public bool WriteAllowed { get; set; }
        public string? LastApprovalHash { get; private set; }
        public bool? ContextWriteAllowed { get; private set; }
        public string? VerifyError { get; set; }
        public int WriteSteps { get; private set; }
        public string RuntimeName => "test";
        public SnippetOptions Snippets { get; } = new SnippetOptions
        {
            ContextType = typeof(TestContext).FullName!,
            Usings = new[] { "System", "System.Linq", "System.Collections.Generic", "EplanMcp.Scripting.Core.Tests" },
        };
        public IEnumerable<string> ReferenceSearchDirs => Array.Empty<string>();
        public IRunSession Open(RunRequest request) => new Session(this);

        private sealed class Session : IRunSession
        {
            private readonly TestEnvironment _env;
            public Session(TestEnvironment env) { _env = env; }
            public Preflight Prepare(ScriptManifest manifest, string approvalHash)
            {
                _env.LastApprovalHash = approvalHash;
                return new Preflight { Project = "test.elk", WriteAllowed = _env.WriteAllowed, Gate = _env.WriteAllowed ? "scratch" : "real project" };
            }
            public object CreateContext(ScriptManifest m, Preflight p, Budget b, ProgressWriter pr, List<string> log)
            {
                _env.ContextWriteAllowed = p.WriteAllowed;
                return new TestContext { Log = log, Budget = b };
            }
            public string? BeginWrite(string description) { _env.WriteSteps++; return description; }
            public string? Verify() => _env.VerifyError;
            public void Dispose() { }
        }
    }

    public class CoreTests
    {
        private static RunRequest Req(string source, Action<RunRequest>? tweak = null)
        {
            var r = new RunRequest { Protocol = Protocol.Version, RunId = Guid.NewGuid().ToString("N"), Source = source, SourceName = "t.cs" };
            tweak?.Invoke(r);
            return r;
        }

        private static readonly AuditLog Audit = new AuditLog(Path.Combine(Path.GetTempPath(), "eplanmcp-test-audit"));

        [Fact]
        public void Header_parses_tool_args_and_flags()
        {
            var errors = new List<string>();
            var m = HeaderParser.Parse(
                "// @tool  cable_core_links\n// @desc  Map cores.\n// @writes true\n// @needs offline\n" +
                "// @arg project string optional Scratch project path\n// @arg mode enum(read,assign) default=read Mode\n" +
                "// @requires core>=0.1\nusing System;\n// @tool not_a_header_after_code\n", errors);
            Assert.Empty(errors);
            Assert.Equal("cable_core_links", m.Tool);
            Assert.True(m.Writes);
            Assert.Equal("offline", m.Needs);
            Assert.Equal(2, m.Args.Count);
            Assert.Equal(new[] { "read", "assign" }, m.Args[1].Choices);
            Assert.Equal("read", m.Args[1].Default);
            Assert.Equal("Scratch project path", m.Args[0].Description);
        }

        [Fact]
        public void Snippet_runs_and_returns_plain_data()
        {
            var res = RunPipeline.Execute(Req("var xs = new[] { 1, 2, 3 };\nreturn new { sum = xs.Sum() };"), new TestEnvironment(), Audit);
            Assert.True(res.Ok, res.Error);
            Assert.Equal("done", res.Phase);
            Assert.Equal(6, ((JsonNode)res.Result!)["sum"]!.GetValue<int>());
            Assert.True(res.Unloaded);
        }

        [Fact]
        public void Full_unit_runs()
        {
            const string src = "using EplanMcp.Scripting.Core.Tests;\npublic static class Script {\n  public static object Run(TestContext ctx) { ctx.Write(\"hi\"); return 42; }\n}\n";
            var res = RunPipeline.Execute(Req(src), new TestEnvironment(), Audit);
            Assert.True(res.Ok, res.Error);
            Assert.Equal(42, ((JsonNode)res.Result!).GetValue<int>());
            Assert.Contains("hi", res.Log);
        }

        [Fact]
        public void Compile_error_maps_to_author_line_with_hint()
        {
            // Line 3 of the snippet, after a header comment and a blank line.
            var res = RunPipeline.Execute(Req("// @desc test\n\nvar s = \"abc\".Lenght;\nreturn s;"), new TestEnvironment(), Audit);
            Assert.False(res.Ok);
            Assert.Equal("compile", res.Phase);
            var d = res.Diagnostics.First(x => x.Severity == "error");
            Assert.Equal("CS1061", d.Id);
            Assert.Equal(3, d.Line);
            Assert.Equal("t.cs", d.File);
            Assert.Contains("Length", d.Hint);
        }

        [Fact]
        public void Runtime_exception_reports_message_and_unloads()
        {
            var res = RunPipeline.Execute(Req("throw new InvalidOperationException(\"boom\");"), new TestEnvironment(), Audit);
            Assert.False(res.Ok);
            Assert.Equal("run", res.Phase);
            Assert.Contains("boom", res.Error);
            Assert.True(res.Unloaded);
        }

        [Fact]
        public void Unbounded_loop_without_budget_check_is_rejected()
        {
            var res = RunPipeline.Execute(Req("while (true) { }"), new TestEnvironment(), Audit);
            Assert.False(res.Ok);
            Assert.Contains(res.Diagnostics, d => d.Id == "EMCP003");
        }

        [Fact]
        public void Budget_stops_a_long_loop()
        {
            var res = RunPipeline.Execute(Req("while (true) { ctx.Budget.Check(); }", r => r.BudgetSeconds = 0.3), new TestEnvironment(), Audit);
            Assert.False(res.Ok);
            Assert.Contains("budget", res.Error);
            Assert.True(res.Unloaded);
        }

        [Fact]
        public void Denied_api_is_rejected_unless_unsafe()
        {
            var bad = RunPipeline.Execute(Req("System.IO.File.Delete(\"x\"); return 1;"), new TestEnvironment(), Audit);
            Assert.Contains(bad.Diagnostics, d => d.Id == "EMCP001");
            var ok = RunPipeline.Execute(Req("// @unsafe\nreturn System.IO.File.Exists(\"x\");"), new TestEnvironment(), Audit);
            Assert.True(ok.Ok, ok.Error);
        }

        [Fact]
        public void Writes_script_refused_when_gate_is_closed()
        {
            var res = RunPipeline.Execute(Req("// @writes true\nreturn 1;"), new TestEnvironment { WriteAllowed = false }, Audit);
            Assert.False(res.Ok);
            Assert.Equal("guard", res.Phase);
        }

        [Fact]
        public void Writes_script_opens_an_undo_step_when_allowed()
        {
            var env = new TestEnvironment { WriteAllowed = true };
            var res = RunPipeline.Execute(Req("// @writes true\nreturn 1;"), env, Audit);
            Assert.True(res.Ok, res.Error);
            Assert.Equal(1, env.WriteSteps);
            Assert.NotNull(res.UndoDescription);
        }

        [Fact]
        public void Compile_only_of_a_writing_script_returns_the_approval_hash_even_when_the_gate_is_closed()
        {
            var res = RunPipeline.Execute(Req("// @writes true\nreturn 1;", r => r.CompileOnly = true), new TestEnvironment { WriteAllowed = false }, Audit);
            Assert.True(res.Ok, res.Error);
            Assert.Equal(64, res.ApprovalHash!.Length);
        }

        [Fact]
        public void Approval_hash_covers_args_and_helpers()
        {
            var a = Approval.Hash("src", new string[0], "{\"x\":1}");
            Assert.NotEqual(a, Approval.Hash("src", new string[0], "{\"x\":2}"));
            Assert.NotEqual(a, Approval.Hash("src", new[] { "helper" }, "{\"x\":1}"));
            var env = new TestEnvironment();
            var res = RunPipeline.Execute(Req("return ctx.Args.Count;", r => { r.ArgsCanonical = "{\"x\":1}"; r.Args = new Dictionary<string, object?>(); }), env, Audit);
            Assert.Equal(env.LastApprovalHash, res.ApprovalHash);
        }

        [Fact]
        public void Script_without_writes_header_gets_a_read_only_context_on_a_scratch_clone()
        {
            var env = new TestEnvironment { WriteAllowed = true };
            var res = RunPipeline.Execute(Req("return 1;"), env, Audit);
            Assert.True(res.Ok, res.Error);
            Assert.False(env.ContextWriteAllowed);
        }

        [Fact]
        public void Verify_failure_fails_the_run()
        {
            var res = RunPipeline.Execute(Req("return 1;"), new TestEnvironment { VerifyError = "project set changed" }, Audit);
            Assert.False(res.Ok);
            Assert.Equal("guard", res.Phase);
        }

        [Fact]
        public void Compile_only_does_not_run()
        {
            var res = RunPipeline.Execute(Req("throw new Exception(\"must not run\");", r => r.CompileOnly = true), new TestEnvironment(), Audit);
            Assert.True(res.Ok, res.Error);
            Assert.Null(res.Result);
        }

        [Fact]
        public void Protocol_mismatch_is_refused_plainly()
        {
            var res = RunPipeline.Execute(Req("return 1;", r => r.Protocol = 99), new TestEnvironment(), Audit);
            Assert.False(res.Ok);
            Assert.Contains("Protocol 99", res.Error);
        }

        [Fact]
        public void Requires_newer_core_is_refused()
        {
            var res = RunPipeline.Execute(Req("// @requires core>=99.0\nreturn 1;"), new TestEnvironment(), Audit);
            Assert.False(res.Ok);
            Assert.Contains("99.0", res.Error);
        }

        [Fact]
        public void Large_result_goes_to_a_file()
        {
            var folder = Path.Combine(Path.GetTempPath(), "eplanmcp-test-" + Guid.NewGuid().ToString("N"));
            var res = RunPipeline.Execute(Req("return new string('x', 50000);", r => r.RunFolder = folder), new TestEnvironment(), Audit);
            Assert.True(res.Ok, res.Error);
            Assert.True(res.Truncated);
            Assert.True(File.Exists(res.ResultFile));
        }

        [Fact]
        public void Audit_records_start_and_end()
        {
            var dir = Path.Combine(Path.GetTempPath(), "eplanmcp-audit-" + Guid.NewGuid().ToString("N"));
            var req = Req("return 1;");
            RunPipeline.Execute(req, new TestEnvironment(), new AuditLog(dir));
            var lines = File.ReadAllLines(Directory.GetFiles(dir, "*.jsonl").Single());
            Assert.Contains(lines, l => l.Contains("\"event\":\"start\"") && l.Contains(req.RunId));
            Assert.Contains(lines, l => l.Contains("\"event\":\"end\"") && l.Contains(req.RunId));
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "sources")));
        }

        [Fact]
        public void Second_run_of_same_source_hits_the_cache_and_still_unloads()
        {
            var env = new TestEnvironment();
            var a = RunPipeline.Execute(Req("return 7;"), env, Audit);
            var b = RunPipeline.Execute(Req("return 7;"), env, Audit);
            Assert.True(a.Ok && b.Ok);
            Assert.Empty(b.Diagnostics);
            Assert.True(b.Unloaded);
        }
    }
}
