using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Eplan.EplApi.DataModel;
using EplanMcp.Contracts;
using EplanMcp.Scripting.Core;

namespace EplanMcp.Scripting
{
    /// <summary>
    /// The script host without an EPLAN window: one long-lived process that initialises EPLAN's
    /// offline API once, then serves requests dropped into a queue folder until told to stop.
    ///
    /// EPLAN's rules for offline applications shape it (2027 docs, "Eplan API offline applications"):
    /// Init once and Exit once, Exit on the main thread (never the finalizer), API assemblies loaded
    /// from EPLAN's Bin (the exe pins to it before calling here), no GUI, and a LockingStep before a
    /// project is opened. Each request opens its project and closes it again, so the interactive
    /// session can open it afterwards; a project the interactive session holds cannot be opened here.
    ///
    /// Queue protocol: the server writes &lt;run_id&gt;.json = {"request": path, "result": path} into the
    /// queue folder; the host runs it and deletes the queue file. "stop" in the folder ends the host.
    /// "status.json" there is rewritten every few seconds with pid, versions and a timestamp.
    /// </summary>
    public static class OfflineHost
    {
        public const string Runtime = "offline";

        /// <param name="binPath">The product variant's Bin (e.g. ...\Electric P8\&lt;version&gt;\Bin).</param>
        public static int Serve(string binPath, string queueDir, TimeSpan idleTimeout)
        {
            Directory.CreateDirectory(queueDir);
            var app = new Eplan.EplApi.System.EplApplication { EplanBinFolder = binPath };
            app.Init("");
            try
            {
                app.QuietMode = Eplan.EplApi.System.EplApplication.QuietModes.ShowNoDialogs;
                var lastWork = DateTime.UtcNow;
                var lastStatus = DateTime.MinValue;
                while (true)
                {
                    if (File.Exists(Path.Combine(queueDir, "stop"))) break;
                    if (DateTime.UtcNow - lastWork > idleTimeout) break;
                    if (DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(3))
                    {
                        WriteStatus(queueDir, app);
                        lastStatus = DateTime.UtcNow;
                    }
                    var next = Directory.GetFiles(queueDir, "*.json")
                        .Where(f => !Path.GetFileName(f).Equals("status.json", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(File.GetCreationTimeUtc).FirstOrDefault();
                    if (next == null) { Thread.Sleep(200); continue; }
                    lastWork = DateTime.UtcNow;
                    ServeOne(next);
                }
            }
            finally
            {
                TryDelete(Path.Combine(queueDir, "stop"));
                TryDelete(Path.Combine(queueDir, "status.json"));
                app.Exit();   // once, on this (the main) thread
            }
            return 0;
        }

        private static void ServeOne(string queueFile)
        {
            string? resultPath = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(queueFile));
                var requestPath = doc.RootElement.GetProperty("request").GetString()!;
                resultPath = doc.RootElement.GetProperty("result").GetString()!;
                var req = Json.Deserialize<RunRequest>(File.ReadAllText(requestPath, Encoding.UTF8))
                          ?? throw new InvalidDataException("The request file is empty.");
                if (string.IsNullOrEmpty(req.RunFolder)) req.RunFolder = Path.GetDirectoryName(resultPath) ?? "";
                // In this process EPLAN loads its API assemblies lazily, so the compile references
                // would miss any not yet touched (HEServices, MasterData...). Reference them all.
                foreach (var dll in Directory.GetFiles(Environment.CurrentDirectory, "Eplan.EplApi.*Netu.dll"))
                    if (!req.References.Contains(dll)) req.References.Add(dll);
                var result = RunPipeline.Execute(req, new OfflineEnvironment());
                Write(resultPath, Json.Serialize(result));
            }
            catch (Exception e)
            {
                if (resultPath != null)
                    Write(resultPath, Json.Serialize(new RunResult
                    {
                        CoreVersion = RunPipeline.CoreVersion, Runtime = Runtime, Phase = "request",
                        Error = "Offline host could not run the request: " + e.Message,
                    }));
            }
            finally
            {
                TryDelete(queueFile);
            }
        }

        private static void WriteStatus(string queueDir, Eplan.EplApi.System.EplApplication app)
        {
            string? version = null, license = null;
            try { version = app.Version; license = app.License; } catch { }
            Write(Path.Combine(queueDir, "status.json"), Json.Serialize(new HelloResult
            {
                CoreVersion = RunPipeline.CoreVersion, Runtime = Runtime, ProcessId = Environment.ProcessId,
                EplanVersion = version, License = license,
            }));
        }

        private static void Write(string path, string text)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>The offline host's environment: the project is opened for the run and closed after.</summary>
    public sealed class OfflineEnvironment : IRunEnvironment
    {
        public string RuntimeName => OfflineHost.Runtime;
        public SnippetOptions Snippets => EplanEnvironment.DefaultSnippets;
        public IEnumerable<string> ReferenceSearchDirs
        {
            get
            {
                var own = Path.GetDirectoryName(typeof(OfflineEnvironment).Assembly.Location);
                return string.IsNullOrEmpty(own) ? Array.Empty<string>() : new[] { own! };
            }
        }

        public IRunSession Open(RunRequest request) => new Session(request);

        private sealed class Session : IRunSession
        {
            private readonly RunRequest _req;
            private readonly LockingStep _locking = new LockingStep();   // before any project access
            private Project? _project;
            private UndoStep? _undo;

            public Session(RunRequest req) { _req = req; }

            public Preflight Prepare(ScriptManifest manifest, string approvalHash)
            {
                var pre = new Preflight();
                if (manifest.Needs == "session" || manifest.Needs == "gui")
                {
                    pre.Ok = false;
                    pre.Error = $"This script declares '@needs {manifest.Needs}': it needs the interactive EPLAN, not the offline host. Run it with runtime='auto'.";
                    return pre;
                }
                if (string.IsNullOrWhiteSpace(_req.Project) || !File.Exists(_req.Project))
                {
                    pre.Ok = false;
                    pre.Error = "The offline host needs 'project': the full path of an existing .elk file.";
                    return pre;
                }
                WriteGate.Decide(pre, _req.Project, _req, approvalHash);
                try
                {
                    var mode = manifest.Writes && pre.WriteAllowed ? ProjectManager.OpenMode.Exclusive : ProjectManager.OpenMode.ReadOnly;
                    _project = new ProjectManager().OpenProject(_req.Project, mode);
                }
                catch (Exception e)
                {
                    pre.Ok = false;
                    pre.Error = "Could not open the project offline (is it open in EPLAN or locked by another user?): " +
                                (e.InnerException ?? e).Message;
                    return pre;
                }
                pre.Project = _project.ProjectLinkFilePath;
                return pre;
            }

            public object CreateContext(ScriptManifest manifest, Preflight pre, Budget budget, ProgressWriter progress, List<string> log) =>
                new ScriptContext(_project, new ScriptArgs(_req.Args), budget, progress, log, _req.DryRun, pre.WriteAllowed,
                    _req.RunId, _req.RunFolder);

            public string? BeginWrite(string description)
            {
                try
                {
                    _undo = new UndoManager().CreateUndoStep();
                    _undo.SetUndoDescription(description);
                    return description;
                }
                catch { return null; }
            }

            public string? Verify() => null;   // the offline host opens nothing else; EMCP004 keeps scripts on ctx.Project

            public void Dispose()
            {
                try { _undo?.CloseUndo(); } catch { }
                try { _undo?.Dispose(); } catch { }
                try { _project?.Close(); } catch { }
                _locking.Dispose();
            }
        }
    }
}
