using System;
using System.IO;
using System.Linq;

namespace EplanMcp.Scripting.Core
{
    /// <summary>
    /// Where a script may write an output file it was asked for. A run that may not write the
    /// project may not write files outside its own run folder either; a writing run may write
    /// elsewhere, but never into an EPLAN project or library (a stray JSON there can corrupt it).
    /// </summary>
    public static class OutputPath
    {
        private static readonly string[] EplanDataExtensions = { ".elk", ".slk", ".sdb", ".edb" };

        /// <param name="runFolder">The run's own folder.</param>
        /// <param name="writeAllowed">True when the run passed the write gate.</param>
        /// <param name="requested">The path the caller asked for; relative paths are taken inside
        /// <paramref name="runFolder"/>. Null or empty: <paramref name="defaultName"/> there.</param>
        public static string Resolve(string runFolder, bool writeAllowed, string? requested, string defaultName)
        {
            var folder = Path.GetFullPath(string.IsNullOrEmpty(runFolder) ? "." : runFolder);
            if (string.IsNullOrWhiteSpace(requested)) return Path.Combine(folder, defaultName);
            var full = Path.GetFullPath(Path.IsPathRooted(requested) ? requested! : Path.Combine(folder, requested!));

            if (!writeAllowed)
            {
                if (!full.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"A run that may not write can only write files inside its run folder ({folder}); '{requested}' is outside it. Pass a file name, or leave 'out' empty.");
                return full;
            }

            if (EplanDataExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"'{requested}' is an EPLAN project or library file; output never goes there.");
            var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Take(parts.Length - 1).Any(p => p.EndsWith(".edb", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"'{requested}' is inside an EPLAN project folder (*.edb); output never goes there.");
            return full;
        }
    }
}
