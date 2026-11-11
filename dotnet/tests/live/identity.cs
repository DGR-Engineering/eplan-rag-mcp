// @desc  Live check: scripts must bind to the one loaded copy of the EPLAN API and of the host.
// @writes false
using System;
using System.Linq;
using System.Runtime.Loader;
using Eplan.EplApi.DataModel;
using EplanMcp.Scripting;

public static class Script
{
    public static object Run(ScriptContext ctx)
    {
        var all = AppDomain.CurrentDomain.GetAssemblies();
        var dm = typeof(LockingStep).Assembly;
        var host = typeof(ScriptContext).Assembly;
        return new
        {
            data_model_copies = all.Count(a => a.GetName().Name == "Eplan.EplApi.DataModelNetu"),
            data_model_context = AssemblyLoadContext.GetLoadContext(dm)?.Name,
            data_model_location = dm.Location,
            context_from_host = ctx.GetType().Assembly == host,
            host_context = AssemblyLoadContext.GetLoadContext(host)?.Name,
            host_copies = all.Count(a => a.GetName().Name == "EplanMcp.Scripting"),
            script_context = AssemblyLoadContext.GetLoadContext(typeof(Script).Assembly)?.Name,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        };
    }
}
