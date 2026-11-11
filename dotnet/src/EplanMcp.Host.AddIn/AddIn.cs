using System;
using Eplan.EplApi.ApplicationFramework;
using EplanMcp.Scripting;

namespace EplanMcp.Host.AddIn
{
    /// <summary>Registers nothing visible: the add-in only provides two actions for the MCP server.</summary>
    public class AddInModule : IEplAddIn
    {
        public bool OnRegister(ref bool bLoadOnStart)
        {
            bLoadOnStart = true;
            return true;
        }

        public bool OnUnregister() => true;
        public bool OnInit() => true;
        public bool OnInitGui() => true;
        public bool OnExit() => true;
    }

    /// <summary><c>EplanMcp_RunScript /request:"...json" /result:"...json"</c>. Start it on the direct
    /// Remote Client path: an action started from inside the quiet-mode script wrapper cannot lock
    /// anything on the .NET 8 host (S511176).</summary>
    public class RunScriptAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "EplanMcp_RunScript";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            var request = Param(ctx, "request");
            var result = Param(ctx, "result");
            if (string.IsNullOrEmpty(request) || string.IsNullOrEmpty(result)) return false;
            // Scripts compile against the assemblies already loaded; load the helper library first.
            _ = typeof(EplanMcp.Eplan.Pages).Assembly;
            HostEntry.Run(request!, result!, "addin");
            return true;
        }

        internal static string? Param(ActionCallingContext ctx, string name)
        {
            try
            {
                string value = string.Empty;
                ctx.GetParameter(name, ref value);   // returns void; the value comes back by ref
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary><c>EplanMcp_Hello /result:"...json"</c>: versions, features, license, open projects.</summary>
    public class HelloAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "EplanMcp_Hello";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            var result = RunScriptAction.Param(ctx, "result");
            if (string.IsNullOrEmpty(result)) return false;
            HostEntry.Hello(result!, "addin");
            return true;
        }
    }
}
