using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eplan.EplApi.Starter;

namespace EplanMcp.Host.Offline
{
    /// <summary>
    /// EplanMcp.Host.Offline --queue &lt;folder&gt; [--bin &lt;EPLAN Bin&gt;] [--idle-minutes 30]
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string? bin = Arg(args, "--bin")
                ?? Assembly.GetEntryAssembly()!.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "EplanBinPath")?.Value;
            string? queue = Arg(args, "--queue");
            double idle = double.TryParse(Arg(args, "--idle-minutes"), out var m) ? m : 30;
            if (string.IsNullOrEmpty(bin) || !Directory.Exists(bin))
            {
                Console.Error.WriteLine("EPLAN Bin folder not found: " + bin);
                return 2;
            }
            if (string.IsNullOrEmpty(queue))
            {
                Console.Error.WriteLine("Usage: EplanMcp.Host.Offline --queue <folder> [--bin <EPLAN Bin>] [--idle-minutes 30]");
                return 2;
            }

            // EPLAN's API assemblies have unmanaged dependencies that load from the current
            // directory, and must load from exactly this Bin: pin BEFORE touching anything EPLAN-bound.
            Environment.CurrentDirectory = bin;
            var resolver = new AssemblyResolver();
            resolver.SetEplanBinPath(bin);
            resolver.PinToEplan();
            return Serve(bin, Path.GetFullPath(queue), TimeSpan.FromMinutes(idle));
        }

        // Separate, non-inlined method: its JIT is what first loads the EPLAN-bound assemblies.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Serve(string bin, string queue, TimeSpan idle) =>
            EplanMcp.Scripting.OfflineHost.Serve(bin, queue, idle);

        private static string? Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
