using System;
using System.IO;
using EplanMcp.Contracts;
using EplanMcp.Scripting.Core;

namespace EplanMcp.Scripting
{
    /// <summary>
    /// D7, the one write rule every host applies: ad-hoc code may write when the TARGET project is a
    /// marked scratch clone (the safety check keeps a writing script on ctx.Project). A real project
    /// takes a saved script, the explicit flag, and a confirmation equal to the start of the approval
    /// hash (source + helpers + args) the user was shown.
    /// </summary>
    public static class WriteGate
    {
        public static void Decide(Preflight pre, string? projectLinkFile, RunRequest req, string approvalHash)
        {
            string scratchWhy = "";
            if (!string.IsNullOrEmpty(projectLinkFile) && ScratchMarker.IsScratch(projectLinkFile, out scratchWhy))
            {
                pre.WriteAllowed = true;
                pre.Gate = "the target project is a scratch clone";
            }
            else if (req.AllowRealProject && !string.IsNullOrEmpty(req.ScriptPath)
                     && !string.IsNullOrEmpty(req.Confirm) && req.Confirm!.Length >= 12
                     && approvalHash.StartsWith(req.Confirm, StringComparison.OrdinalIgnoreCase))
            {
                pre.WriteAllowed = true;
                pre.Gate = "saved script confirmed for a real project";
            }
            else
            {
                pre.WriteAllowed = false;
                pre.Gate = string.IsNullOrEmpty(projectLinkFile)
                    ? "no target project"
                    : "the target project " + Path.GetFileName(projectLinkFile) + " is not a scratch clone (" + scratchWhy +
                      "). Writes to a real project need a saved script, allow_real_project and the confirmation code.";
            }
        }
    }
}
