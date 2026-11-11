using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Diag = EplanMcp.Contracts.Diagnostic;

namespace EplanMcp.Scripting.Core
{
    /// <summary>
    /// Compile-time checks on script code. A script is C# running in-process with the user's
    /// rights, so this cannot be a security boundary: it is a speed bump that catches the common
    /// mistakes (writing to a real project, changing which projects are open, unbounded loops).
    /// </summary>
    public static class SafetyAnalyzer
    {
        /// <summary>Calls that change which projects are open, start processes or delete files.
        /// Allowed only with an explicit <c>// @unsafe</c> header.</summary>
        private static readonly (string type, string[] members)[] Denied =
        {
            ("Eplan.EplApi.DataModel.ProjectManager", new[] { "OpenProject", "CreateProject", "RemoveProject", "CopyProject", "UpgradeProjectDatabase" }),
            ("Eplan.EplApi.DataModel.Project", new[] { "Close", "Remove" }),
            ("System.Diagnostics.Process", new[] { "*" }),
            ("System.IO.File", new[] { "Delete", "Move", "Replace" }),
            ("System.IO.Directory", new[] { "Delete", "Move" }),
            ("System.Environment", new[] { "Exit", "FailFast" }),
        };

        /// <summary>Method-name prefixes on <c>Eplan.*</c> types that change data. Used only in
        /// read-only mode, together with assignments to EPLAN properties and indexers.</summary>
        private static readonly string[] WriteVerbs =
        {
            "Create", "Remove", "Delete", "Set", "Lock", "SmartLock", "Store", "Move", "Rename", "Insert",
            "Paste", "Update", "Apply", "Assign", "Place", "Import", "Execute", "Copy", "Add", "Clear",
            "Generate", "Renumber", "Translate", "Synchronize", "Upgrade", "Reset", "DockText",
        };

        /// <summary>Ways to reach a project other than ctx.Project. A writing script may not use
        /// them: the write gate checks only the TARGET project is a scratch clone, so a writing
        /// script must stay inside it (D7).</summary>
        private static readonly string[] OtherProjectTypes =
        {
            "Eplan.EplApi.DataModel.ProjectManager",
            "Eplan.EplApi.HEServices.SelectionSet",
        };

        /// <param name="readOnly">True when the run may not write: no assignments to EPLAN
        /// properties and no EPLAN method whose name starts with a write verb. False (a writing
        /// run): the script may not reach any project but ctx.Project.</param>
        public static List<Diag> Check(CSharpCompilation compilation, bool readOnly, bool allowUnsafe)
        {
            var found = new List<Diag>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!(model.GetSymbolInfo(inv).Symbol is IMethodSymbol m)) continue;
                    var type = m.ContainingType?.ToDisplayString() ?? "";
                    if (!allowUnsafe && IsDenied(type, m.Name))
                        found.Add(Make("EMCP001", inv, $"{type}.{m.Name} is not allowed in scripts (it changes open projects, processes or files). Add '// @unsafe' only if you really mean it."));
                    else if (readOnly && IsEplan(type) && IsWriteVerb(m.Name))
                        found.Add(Make("EMCP002", inv, $"{type}.{m.Name} looks like a write, and this run is read-only. Writes need a scratch clone (or a saved script with confirmation on a real project) and '// @writes true'."));
                }

                // new SomeEplanType(...) is fine for readers (finders, filters, LockingStep), so only
                // the explicit write verbs and assignments are checked.
                if (readOnly)
                {
                    foreach (var asg in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        var target = model.GetSymbolInfo(asg.Left).Symbol;
                        var owner = target?.ContainingType?.ToDisplayString() ?? "";
                        if ((target is IPropertySymbol || target is IFieldSymbol) && IsEplan(owner) && !IsHarmlessSetting(owner, target!.Name))
                            found.Add(Make("EMCP002", asg, $"Assigning {owner}.{target!.Name} is a write, and this run is read-only."));
                    }
                }

                if (!readOnly && !allowUnsafe)
                {
                    foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                    {
                        var sym = model.GetSymbolInfo(name).Symbol;
                        var type = sym as ITypeSymbol ?? sym?.ContainingType;
                        var full = type?.ToDisplayString() ?? "";
                        if (OtherProjectTypes.Contains(full))
                        {
                            found.Add(Make("EMCP004", name, $"A writing script may only touch ctx.Project; {full} can reach other open projects. Use ctx.Project."));
                            break;
                        }
                    }
                }

                foreach (var loop in root.DescendantNodes().Where(IsUnboundedLoop))
                    if (!loop.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(CallsBudgetCheck))
                        found.Add(Make("EMCP003", loop, "Unbounded loop without ctx.Budget.Check(): a script that never ends freezes EPLAN and cannot be stopped. Call ctx.Budget.Check() inside the loop."));
            }
            return found;
        }

        private static bool IsDenied(string type, string member) =>
            Denied.Any(d => d.type == type && (d.members.Contains("*") || d.members.Contains(member)));

        private static bool IsEplan(string type) => type.StartsWith("Eplan.", StringComparison.Ordinal);

        private static bool IsWriteVerb(string name) => WriteVerbs.Any(v => name.StartsWith(v, StringComparison.Ordinal));

        // Settings on helper objects that only shape a read.
        private static bool IsHarmlessSetting(string owner, string member) =>
            owner.EndsWith(".SelectionSet", StringComparison.Ordinal) && member == "LockSelectionByDefault";

        private static bool IsUnboundedLoop(SyntaxNode n) => n switch
        {
            WhileStatementSyntax w => w.Condition.IsKind(SyntaxKind.TrueLiteralExpression),
            DoStatementSyntax d => d.Condition.IsKind(SyntaxKind.TrueLiteralExpression),
            ForStatementSyntax f => f.Condition == null,
            _ => false,
        };

        private static bool CallsBudgetCheck(InvocationExpressionSyntax inv) =>
            inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.ValueText == "Check"
            && ma.Expression.ToString().EndsWith("Budget", StringComparison.Ordinal);

        private static Diag Make(string id, SyntaxNode node, string message)
        {
            var span = node.GetLocation().GetMappedLineSpan();
            return new Diag
            {
                Id = id,
                Severity = "error",
                File = span.Path ?? "",
                Line = span.StartLinePosition.Line + 1,
                Col = span.StartLinePosition.Character + 1,
                Message = message,
                Snippet = node.ToString().Split('\n')[0].Trim(),
            };
        }
    }
}
