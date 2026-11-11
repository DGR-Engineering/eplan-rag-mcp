using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace EplanMcp.Scripting.Core
{
    /// <summary>
    /// The assemblies a script compiles against. Built from what the process has actually loaded,
    /// so scripts bind to the same assemblies they will run with. Inside EPLAN's native host
    /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> may be missing, so it is only one of three sources.
    /// </summary>
    public sealed class ReferenceSet
    {
        public IReadOnlyList<MetadataReference> References { get; }

        /// <summary>Hash of the EPLAN and host assemblies' identities (name + MVID). Part of the
        /// compile-cache key, so nothing compiled against one EPLAN build loads under another.</summary>
        public string Fingerprint { get; }

        public IReadOnlyDictionary<string, string> Paths { get; }

        private ReferenceSet(Dictionary<string, string> paths, string fingerprint)
        {
            Paths = paths;
            Fingerprint = fingerprint;
            References = paths.Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        }

        /// <param name="searchDirs">Folders searched for loaded assemblies that have no file location
        /// (loaded from bytes by the bootstrap).</param>
        /// <param name="extra">Additional assembly paths from the request.</param>
        public static ReferenceSet FromCurrentProcess(IEnumerable<string>? searchDirs = null, IEnumerable<string>? extra = null)
        {
            var dirs = (searchDirs ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var identity = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. Loaded assemblies win: they are exactly what the script will bind to at run time.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name) || name.StartsWith(ScriptRunner.AssemblyPrefix, StringComparison.Ordinal)) continue;
                string? path = SafeLocation(asm);
                if (string.IsNullOrEmpty(path))
                    path = dirs.Select(d => Path.Combine(d, name + ".dll")).FirstOrDefault(File.Exists);
                if (string.IsNullOrEmpty(path) || paths.ContainsKey(name)) continue;
                paths[name] = path!;
                if (name.StartsWith("Eplan.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("EplanMcp.", StringComparison.Ordinal))
                    identity[name] = asm.ManifestModule.ModuleVersionId.ToString("N");
            }

            // 2. The trusted platform list, when the host provides one.
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
                foreach (var p in tpa.Split(Path.PathSeparator))
                    AddIfManaged(paths, p);

            // 3. The runtime directory (covers framework facades nothing has loaded yet).
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir))
                foreach (var p in Directory.EnumerateFiles(runtimeDir!, "*.dll"))
                    AddIfManaged(paths, p);

            foreach (var p in extra ?? Array.Empty<string>())
                if (File.Exists(p)) paths[Path.GetFileNameWithoutExtension(p)] = Path.GetFullPath(p);

            var fp = string.Join(";", identity.Select(kv => kv.Key + "=" + kv.Value));
            return new ReferenceSet(paths, Hashing.Sha256Hex(fp));
        }

        /// <summary>A reference set from explicit files plus the runtime directory: for compiling
        /// scripts outside EPLAN (CI checks against an installed EPLAN's Bin folder).</summary>
        public static ReferenceSet FromPaths(IEnumerable<string> files)
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in files) AddIfManaged(paths, p);
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir))
                foreach (var p in Directory.EnumerateFiles(runtimeDir!, "*.dll")) AddIfManaged(paths, p);
            return new ReferenceSet(paths, Hashing.Sha256Hex(string.Join(";", paths.Keys.OrderBy(k => k))));
        }

        private static void AddIfManaged(Dictionary<string, string> paths, string p)
        {
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return;
            var name = Path.GetFileNameWithoutExtension(p);
            if (paths.ContainsKey(name)) return;
            try { AssemblyName.GetAssemblyName(p); paths[name] = p; }
            catch (BadImageFormatException) { }   // native DLLs sit in the runtime directory too
        }

        private static string? SafeLocation(Assembly asm)
        {
            try { return asm.Location; } catch (NotSupportedException) { return null; }
        }
    }

    public static class Hashing
    {
        public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

        public static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(bytes);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
