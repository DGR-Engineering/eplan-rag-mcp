using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;
using EplanMcp.Contracts;
using EplanMcp.Scripting.Core;

namespace EplanMcp.Scripting
{
    /// <summary>The script environment inside a running EPLAN session (add-in or bootstrap).</summary>
    public sealed class EplanEnvironment : IRunEnvironment
    {
        private readonly List<string> _searchDirs;

        public EplanEnvironment(string runtimeName, IEnumerable<string>? searchDirs = null)
        {
            RuntimeName = runtimeName;
            _searchDirs = (searchDirs ?? Array.Empty<string>()).ToList();
            var own = Path.GetDirectoryName(typeof(EplanEnvironment).Assembly.Location);
            if (!string.IsNullOrEmpty(own)) _searchDirs.Add(own!);
        }

        public string RuntimeName { get; }

        public SnippetOptions Snippets => DefaultSnippets;

        /// <summary>Plain strings on purpose: reading them must not load any EPLAN assembly, so
        /// tooling outside EPLAN (the script compile check) can use them.</summary>
        public static readonly SnippetOptions DefaultSnippets = new SnippetOptions
        {
            ContextType = "EplanMcp.Scripting.ScriptContext",
            Usings = new[]
            {
                "System", "System.Linq", "System.Collections.Generic", "System.IO",
                "Eplan.EplApi.Base", "Eplan.EplApi.DataModel", "Eplan.EplApi.HEServices", "EplanMcp.Scripting",
            },
        };

        public IEnumerable<string> ReferenceSearchDirs => _searchDirs;

        public IRunSession Open(RunRequest request) => new Session(request);

        private sealed class Session : IRunSession
        {
            private readonly RunRequest _req;
            private readonly LockingStep _locking;
            private Project? _project;
            private string[] _openBefore = Array.Empty<string>();
            private UndoStep? _undo;

            public Session(RunRequest req)
            {
                _req = req;
                // Every project access needs a LockingStep (else NoLockingStepException S063110).
                _locking = new LockingStep();
            }

            public Preflight Prepare(ScriptManifest manifest, string sourceHash)
            {
                var pre = new Preflight();
                _openBefore = OpenProjectLinks();
                _project = ProjectResolver.Resolve(_req.Project, out var resolveError);
                if (_project == null && !string.IsNullOrEmpty(_req.Project))
                {
                    pre.Ok = false;
                    pre.Error = resolveError;
                    return pre;
                }
                if (_project == null && resolveError != null) pre.Warnings.Add(resolveError);
                pre.Project = _project?.ProjectLinkFilePath;

                // D7: ad-hoc code writes only when EVERY open project is a marked scratch clone (a
                // script can reach any open project). A real project takes a saved script, the
                // explicit flag, and a confirmation equal to the source hash the user was shown.
                var notScratch = _openBefore.Where(p => !ScratchMarker.IsScratch(p, out _)).ToList();
                if (_openBefore.Length > 0 && notScratch.Count == 0)
                {
                    pre.WriteAllowed = true;
                    pre.Gate = "all open projects are scratch clones";
                }
                else if (_req.AllowRealProject && !string.IsNullOrEmpty(_req.ScriptPath)
                         && !string.IsNullOrEmpty(_req.Confirm) && sourceHash.StartsWith(_req.Confirm!, StringComparison.OrdinalIgnoreCase)
                         && _req.Confirm!.Length >= 12)
                {
                    pre.WriteAllowed = true;
                    pre.Gate = "saved script confirmed for a real project";
                }
                else
                {
                    pre.WriteAllowed = false;
                    pre.Gate = _openBefore.Length == 0
                        ? "no project is open"
                        : "not every open project is a scratch clone (" + string.Join(", ", notScratch.Select(Path.GetFileName)) +
                          "). Writes to a real project need a saved script, allow_real_project and the confirmation code.";
                }
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
                catch
                {
                    return null;   // undo is best effort; the run still goes ahead
                }
            }

            public string? Verify()
            {
                var after = OpenProjectLinks();
                if (!after.OrderBy(x => x).SequenceEqual(_openBefore.OrderBy(x => x), StringComparer.OrdinalIgnoreCase))
                    return "The script changed which projects are open (before: " + string.Join(", ", _openBefore.Select(Path.GetFileName)) +
                           "; after: " + string.Join(", ", after.Select(Path.GetFileName)) + "). Scripts must not open or close projects.";
                return null;
            }

            public void Dispose()
            {
                try { _undo?.CloseUndo(); } catch { }
                try { _undo?.Dispose(); } catch { }
                _locking.Dispose();
            }

            private static string[] OpenProjectLinks()
            {
                try
                {
                    // A project without a link path still counts: as "<unnamed>" it is never a
                    // scratch clone, so it closes the write gate instead of slipping past it.
                    return new ProjectManager().OpenProjects
                        .Select(p => string.IsNullOrEmpty(p.ProjectLinkFilePath) ? "<unnamed:" + p.ProjectName + ">" : p.ProjectLinkFilePath)
                        .ToArray();
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }
        }
    }

    /// <summary>
    /// Which project a run acts on: the <c>project</c> argument (link-file path or project name)
    /// among the open projects, else the focused one, else the only open one. Never opens a project
    /// and never shows a dialog.
    /// </summary>
    public static class ProjectResolver
    {
        public static Project? Resolve(string? wanted, out string? error)
        {
            error = null;
            Project[] open;
            try { open = new ProjectManager().OpenProjects; }
            catch (Exception e) { error = "Could not list open projects: " + e.Message; return null; }

            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var match = open.FirstOrDefault(p => SamePath(p.ProjectLinkFilePath, wanted!))
                            ?? open.FirstOrDefault(p => string.Equals(p.ProjectName, wanted, StringComparison.OrdinalIgnoreCase))
                            ?? open.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p.ProjectLinkFilePath), wanted, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    error = $"Project '{wanted}' is not open. Open projects: " +
                            (open.Length == 0 ? "none" : string.Join(", ", open.Select(p => p.ProjectLinkFilePath)));
                return match;
            }

            try
            {
                var sel = new SelectionSet { LockSelectionByDefault = false };   // must be cleared before reading
                var focused = sel.GetCurrentProject(false);                      // false: never a dialog
                if (focused != null) return focused;
            }
            catch { }

            if (open.Length == 1) return open[0];
            if (open.Length > 1)
                error = "Several projects are open and none is focused; pass 'project'. Open: " + string.Join(", ", open.Select(p => p.ProjectLinkFilePath));
            return null;
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
