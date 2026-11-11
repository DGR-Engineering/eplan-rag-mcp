using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using EplanMcp.Contracts;

namespace EplanMcp.Scripting.Core
{
    /// <summary>What a runtime (add-in, bootstrap, offline host, tests) provides to the pipeline.</summary>
    public interface IRunEnvironment
    {
        /// <summary>addin | bootstrap | offline | test.</summary>
        string RuntimeName { get; }
        SnippetOptions Snippets { get; }

        /// <summary>Where to find files for loaded assemblies that have no location.</summary>
        IEnumerable<string> ReferenceSearchDirs { get; }

        /// <summary>Opens the run: e.g. takes EPLAN's LockingStep for the whole run.</summary>
        IRunSession Open(RunRequest request);
    }

    public sealed class Preflight
    {
        public bool Ok { get; set; } = true;
        public string? Error { get; set; }
        public string? Project { get; set; }
        public bool WriteAllowed { get; set; }

        /// <summary>Why writes are or are not allowed, in plain words.</summary>
        public string Gate { get; set; } = "";
        public List<string> Warnings { get; } = new List<string>();
    }

    public interface IRunSession : IDisposable
    {
        /// <summary>Resolves the target project and decides whether this run may write.</summary>
        Preflight Prepare(ScriptManifest manifest, string sourceHash);

        /// <summary>The object passed to the script's Run(ctx).</summary>
        object CreateContext(ScriptManifest manifest, Preflight preflight, Budget budget, ProgressWriter progress, List<string> log);

        /// <summary>Called before a run that may write (opens one undo step).</summary>
        string? BeginWrite(string description);

        /// <summary>Called after the run; returns an error when the run broke an invariant (for
        /// example it changed which projects are open).</summary>
        string? Verify();
    }

    /// <summary>
    /// The whole life of one run: header, write gate, compile, safety check, run, unload, result.
    /// Every exit path produces a <see cref="RunResult"/>; nothing escapes as an exception.
    /// </summary>
    public static class RunPipeline
    {
        public static readonly string CoreVersion =
            typeof(RunPipeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? typeof(RunPipeline).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        private static readonly ConcurrentDictionary<string, byte[]> Cache = new ConcurrentDictionary<string, byte[]>();

        public static RunResult Execute(RunRequest req, IRunEnvironment env, AuditLog? audit = null)
        {
            var sw = Stopwatch.StartNew();
            var res = new RunResult { CoreVersion = CoreVersion, Runtime = env.RuntimeName, RunId = req.RunId };
            audit ??= new AuditLog();
            try
            {
                if (req.Protocol != Protocol.Version)
                    return Fail(res, "request", $"Protocol {req.Protocol} is not supported by host {CoreVersion} (protocol {Protocol.Version}). Update the server or the host.");
                if (string.IsNullOrWhiteSpace(req.Source))
                    return Fail(res, "request", "The request has no script source.");

                var headerErrors = new List<string>();
                var manifest = HeaderParser.Parse(req.Source, headerErrors);
                res.Manifest = manifest;
                res.Warnings.AddRange(headerErrors);
                res.SourceHash = Hashing.Sha256Hex(req.Source);

                if (!RequirementMet(manifest.Requires, out var why))
                    return Fail(res, "request", why);

                // The arguments the script sees are exactly the canonical text that was approved.
                if (req.ArgsCanonical != null)
                    req.Args = Json.Deserialize<Dictionary<string, object?>>(req.ArgsCanonical) ?? new Dictionary<string, object?>();
                var helpers = req.HelperSources.Select(p => (path: p, text: File.ReadAllText(p))).ToList();
                res.ApprovalHash = Approval.Hash(req.Source, helpers.Select(h => h.text), req.ArgsCanonical);

                using var session = env.Open(req);
                var pre = session.Prepare(manifest, res.ApprovalHash);
                res.Project = pre.Project;
                res.WriteAllowed = pre.WriteAllowed;
                res.WriteGate = pre.Gate;
                res.Warnings.AddRange(pre.Warnings);
                if (!pre.Ok) return Fail(res, "guard", pre.Error ?? "Refused.");
                // A compile_only run goes on, so the caller gets the approval hash it needs to ask for
                // a real-project write; the gate is still reported in write_gate.
                if (manifest.Writes && !pre.WriteAllowed && !req.CompileOnly)
                    return Fail(res, "guard", "This script declares '// @writes true' but this run may not write: " + pre.Gate);

                // A compile_only check of a writing script uses the write rules it would run under.
                bool readOnly = !(manifest.Writes && (pre.WriteAllowed || req.CompileOnly));

                // ---- compile (cached) + safety check
                var composed = SourceComposer.Compose(req.Source, SafeName(req.SourceName), env.Snippets);
                var refs = ReferenceSet.FromCurrentProcess(env.ReferenceSearchDirs, req.References);
                var key = Hashing.Sha256Hex(string.Join("\n", new[] { CoreVersion, refs.Fingerprint, readOnly.ToString(), manifest.Unsafe.ToString(), composed }
                    .Concat(helpers.Select(h => h.path + "\n" + h.text))
                    .Concat(req.References.Select(FileStamp))));

                if (!Cache.TryGetValue(key, out var image))
                {
                    var compiled = ScriptCompiler.Compile(composed, SafeName(req.SourceName), helpers, refs,
                        ScriptRunner.AssemblyPrefix + key.Substring(0, 16));
                    res.Diagnostics.AddRange(compiled.Diagnostics);
                    if (!compiled.Success)
                        return Fail(res, "compile", "The script did not compile. See diagnostics.");
                    var safety = SafetyAnalyzer.Check(compiled.Compilation!, readOnly, manifest.Unsafe);
                    if (safety.Count > 0)
                    {
                        res.Diagnostics.InsertRange(0, safety);
                        return Fail(res, "compile", "The script failed the safety check. See diagnostics.");
                    }
                    image = compiled.Assembly!;
                    Cache[key] = image;
                }

                if (req.CompileOnly)
                {
                    res.Ok = true;
                    res.Phase = "done";
                    res.Log.Add("compile_only: compiled and checked; not run.");
                    return res;
                }

                // ---- run
                audit.Start(req.RunId, res.SourceHash!, req.Source, new Dictionary<string, object?>
                {
                    ["runtime"] = env.RuntimeName, ["project"] = pre.Project, ["write_allowed"] = pre.WriteAllowed,
                    ["script_path"] = req.ScriptPath, ["source_name"] = req.SourceName, ["args"] = req.Args,
                    ["dry_run"] = req.DryRun, ["core_version"] = CoreVersion,
                });

                var budget = new Budget(req.BudgetSeconds, req.RunFolder);
                var progress = new ProgressWriter(req.RunFolder, budget);
                // The script may write only if the gate allows it AND it declared '// @writes true'.
                pre.WriteAllowed = !readOnly;
                var ctx = session.CreateContext(manifest, pre, budget, progress, res.Log);
                if (!readOnly && !req.DryRun)
                    res.UndoDescription = session.BeginWrite($"EplanMcp {manifest.Tool ?? SafeName(req.SourceName)} {req.RunId}");

                res.Phase = "run";
                var run = RunIsolated(image, ctx);
                ctx = null!;

                var verify = session.Verify();
                res.Unloaded = ScriptRunner.WaitForUnload(run.Context);
                if (res.Unloaded == false)
                    res.Warnings.Add("The script's code was still loaded after the run. Usually harmless (one small assembly, measured with no visible memory growth); if it keeps happening the script may leave an event handler, timer or static reference behind.");
                string? errorText = run.Error;
                bool stopped = run.Stopped;

                var placed = Json.Place(run.Value, req.RunFolder);
                res.Result = placed.inline;
                res.ResultFile = placed.file;
                res.Truncated = placed.truncated;

                if (verify != null) { res.Error = verify; res.Phase = "guard"; }
                else if (errorText != null) { res.Error = errorText; res.Phase = "run"; }
                else { res.Ok = true; res.Phase = "done"; }
                if (!res.Ok && res.UndoDescription != null)
                    res.Warnings.Add($"The run may have changed data before it stopped. Undo it in EPLAN with Edit > Undo \"{res.UndoDescription}\", or discard the scratch clone.");
                if (stopped) res.Warnings.Add("Stopped by the budget or a cancel request.");

                audit.End(req.RunId, new Dictionary<string, object?>
                {
                    ["ok"] = res.Ok, ["phase"] = res.Phase, ["error"] = res.Error, ["unloaded"] = res.Unloaded,
                    ["duration_ms"] = sw.ElapsedMilliseconds,
                });
                return res;
            }
            catch (Exception e)
            {
                return Fail(res, res.Phase == "request" ? "request" : res.Phase, "Host error: " + Describe(e));
            }
            finally
            {
                res.DurationMs = sw.ElapsedMilliseconds;
            }
        }

        private sealed class Isolated
        {
            public System.Text.Json.Nodes.JsonNode? Value;
            public string? Error;
            public bool Stopped;
            public WeakReference? Context;
        }

        /// <summary>Runs the script and reduces everything it produced to plain data before
        /// returning. Its own frame, so no temporary (the exception, the raw return value) outlives
        /// it and keeps the script's context from unloading, Debug builds included.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Isolated RunIsolated(byte[] image, object ctx)
        {
            var outcome = ScriptRunner.Run(image, ctx, value => Json.ToPlain(value));
            return new Isolated
            {
                Value = outcome.Value as System.Text.Json.Nodes.JsonNode,
                Error = outcome.Error == null ? null : Describe(outcome.Error),
                Stopped = outcome.Error is ScriptStoppedException,
                Context = outcome.Context,
            };
        }

        private static RunResult Fail(RunResult res, string phase, string error)
        {
            res.Ok = false;
            res.Phase = phase;
            res.Error = error;
            return res;
        }

        /// <summary>Exception text with the inner chain flattened and the stack cut to script frames
        /// first (EPLAN wraps real failures in TargetInvocationException).</summary>
        internal static string Describe(Exception e)
        {
            var parts = new List<string>();
            for (var cur = e; cur != null; cur = cur.InnerException)
                parts.Add(cur.GetType().Name + ": " + cur.Message);
            var frames = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(8);
            return string.Join(" -> ", parts) + "\n" + string.Join("\n", frames);
        }

        /// <summary>Supports <c>core&gt;=X.Y.Z</c> (several separated by commas).</summary>
        internal static bool RequirementMet(string? requires, out string why)
        {
            why = "";
            if (string.IsNullOrWhiteSpace(requires)) return true;
            foreach (var part in requires!.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                if (part.StartsWith("core>=", StringComparison.OrdinalIgnoreCase))
                {
                    if (Version.TryParse(part.Substring(6), out var need) && Version.TryParse(CoreVersion, out var have) && have < need)
                    {
                        why = $"The script needs host core {need} or newer; this host is {CoreVersion}. Update the host (restart EPLAN after installing it).";
                        return false;
                    }
                }
                else if (part.StartsWith("feature:", StringComparison.OrdinalIgnoreCase))
                {
                    var f = part.Substring(8);
                    if (!Protocol.Features.Contains(f, StringComparer.OrdinalIgnoreCase))
                    {
                        why = $"The script needs host feature '{f}', which this host ({CoreVersion}) does not have.";
                        return false;
                    }
                }
            }
            return true;
        }

        private static string FileStamp(string path)
        {
            try { var f = new FileInfo(path); return path + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks; }
            catch { return path; }
        }

        private static string SafeName(string name) =>
            string.IsNullOrWhiteSpace(name) ? "script.cs" : Path.GetFileName(name);
    }
}
