// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations
{
    // ======================================================================
    //  Event argument types
    // ======================================================================

    public sealed class UriActionEventArgs : EventArgs
    {
        public string Uri { get; }
        public UriActionEventArgs(string uri) { Uri = uri; }
    }

    public sealed class NavigationEventArgs : EventArgs
    {
        public PdfActionType ActionType  { get; }   // GoTo or GoToRemote
        public string        Destination { get; }   // page name / remote file+dest
        public NavigationEventArgs(PdfActionType type, string dest)
        { ActionType = type; Destination = dest; }
    }

    public sealed class NamedActionEventArgs : EventArgs
    {
        /// <summary>NextPage, PrevPage, FirstPage, LastPage, Print, etc.</summary>
        public string Name { get; }
        public NamedActionEventArgs(string name) { Name = name; }
    }

    public sealed class FormActionEventArgs : EventArgs
    {
        public PdfActionType ActionType { get; }  // SubmitForm / ResetForm / ImportData
        public string Target            { get; }  // server URL (SubmitForm) or filespec
        public string FieldName         { get; }  // null for document-level triggers
        public FormActionEventArgs(PdfActionType type, string target, string fieldName)
        { ActionType = type; Target = target; FieldName = fieldName; }
    }

    public sealed class LaunchActionEventArgs : EventArgs
    {
        public string Target { get; }
        public LaunchActionEventArgs(string target) { Target = target; }
    }

    public sealed class OcgStateActionEventArgs : EventArgs
    {
        public string StateExpression { get; }
        public OcgStateActionEventArgs(string state) { StateExpression = state; }
    }

    /// <summary>
    /// Outcome of dispatching a single <see cref="PdfAction"/>.
    /// </summary>
    public sealed class PdfActionResult
    {
        /// <summary>Which named event the runner raised, or null if no event.</summary>
        public string EventRaised { get; internal set; }

        /// <summary>True when a JavaScript action was actually executed.</summary>
        public bool JavaScriptExecuted { get; internal set; }

        /// <summary>Any soft-failure notes (unknown action, missing JS context, …).</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>
        /// Events raised for actions reached through this one's <c>/Next</c> chain (§12.6.3), in the
        /// order they ran. Empty for the overwhelmingly common case of an action with no chain.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> <see cref="EventRaised"/> names one event because the result
        /// type predates chain support, and a chain is genuinely several actions. Reporting only the
        /// head would give a host asking "what will this document do?" the first answer and no
        /// indication there are more, and since this library's
        /// entire security model is that the <b>host</b> decides whether to act, an incomplete answer
        /// undermines the boundary rather than merely truncating a log.</para>
        /// <para>A chain that is refused for depth, a cycle, or the per-trigger budget says so on
        /// <see cref="Warnings"/> — a silently short chain would be indistinguishable from a short
        /// one.</para>
        /// </remarks>
        public List<string> ChainedEvents { get; } = new List<string>();
    }

    // ======================================================================
    //  Runner
    // ======================================================================

    /// <summary>
    /// Dispatches PDF actions (from /OpenAction, /AA, field /A, etc.) by
    /// translating each <see cref="PdfAction"/> into an appropriate event
    /// or engine call.  The library does not navigate, submit forms, or
    /// launch files itself — the host application subscribes to events and
    /// implements the real behaviour.  This keeps sandboxing decisions
    /// (should we actually launch <c>notepad.exe</c>?) where they belong.
    ///
    /// The runner does NOT take ownership of the passed <see cref="PdfDocument"/>;
    /// the caller keeps the document open for as long as the runner is used.
    /// Construction is cheap (no action extraction at construction time); each
    /// <c>Run*</c> call looks up the relevant action directly from the doc.
    /// </summary>
    public sealed class PdfActionRunner
    {
        private readonly PdfDocument _doc;

        public PdfActionRunner(PdfDocument doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        // ------------------------------------------------------------------
        //  Events
        // ------------------------------------------------------------------

        public event EventHandler<UriActionEventArgs>       UriRequested;
        public event EventHandler<NavigationEventArgs>      NavigationRequested;
        public event EventHandler<NamedActionEventArgs>     NamedActionRequested;
        public event EventHandler<FormActionEventArgs>      FormActionRequested;
        public event EventHandler<LaunchActionEventArgs>    LaunchRequested;
        public event EventHandler<OcgStateActionEventArgs>  OcgStateChangeRequested;

        // ------------------------------------------------------------------
        //  Scope-specific run methods
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs the document's /OpenAction, if any.  Returns null when the
        /// catalog carries no /OpenAction.
        /// </summary>
        public PdfActionResult RunCatalogOpenAction(PdfJsExecutionContext jsCtx = null)
        {
            LastStreamExtractError = null;
            if (_doc.Catalog == null) return null;
            var catalog = _doc.Catalog.Dictionary;
            var oa = _doc.ResolveObject(catalog[PdfName.OpenAction]);
            if (oa is PdfDictionary oaDict)
            {
                return RunActionChain(oaDict, "OpenDocument", null, jsCtx);
            }
            return null;
        }

        /// <summary>
        /// Runs the catalog-level /AA action for a given trigger
        /// ("WillClose", "WillSave", "DidSave", "WillPrint", "DidPrint").
        /// </summary>
        public PdfActionResult RunCatalogAa(string trigger, PdfJsExecutionContext jsCtx = null)
        {
            LastStreamExtractError = null;
            if (_doc.Catalog == null) return null;
            var aa = _doc.ResolveObject(_doc.Catalog.Dictionary[PdfName.AA]) as PdfDictionary;
            if (aa == null) return null;

            string key = PdfActionTypes.AdditionalActionKey(trigger, AdditionalActionsOwner.Catalog);
            if (key == null) return null;

            var actionDict = _doc.ResolveObject(aa[new PdfName(key)]) as PdfDictionary;
            if (actionDict == null) return null;

            return RunActionChain(actionDict, trigger, null, jsCtx);
        }

        /// <summary>
        /// Runs page /AA action for a given trigger ("PageOpen" / "PageClose").
        /// Returns null when the page carries no matching action.
        /// </summary>
        public PdfActionResult RunPageAction(int pageIndex, string trigger,
            PdfJsExecutionContext jsCtx = null)
        {
            LastStreamExtractError = null;
            if (pageIndex < 0 || pageIndex >= _doc.Pages.Count) return null;
            var page = _doc.Pages[pageIndex];
            var aa = _doc.ResolveObject(page.Dictionary[PdfName.AA]) as PdfDictionary;
            if (aa == null) return null;

            string key = PdfActionTypes.AdditionalActionKey(trigger, AdditionalActionsOwner.Page);
            if (key == null) return null;

            var actionDict = _doc.ResolveObject(aa[new PdfName(key)]) as PdfDictionary;
            if (actionDict == null) return null;

            return RunActionChain(actionDict, trigger, null, jsCtx);
        }

        /// <summary>
        /// Runs a form-field action by field name + trigger.  Trigger values are the names
        /// <see cref="PdfActionDispatcher"/> reports, from the same table: "Keystroke", "Format",
        /// "Validate", "Calculate", "MouseEnter", "MouseExit", "MouseDown", "MouseUp", "Focus",
        /// "Blur", "PageOpen", "PageClose", "PageVisible", "PageInvisible".
        /// </summary>
        public PdfActionResult RunFieldAction(string fieldName, string trigger,
            PdfJsExecutionContext jsCtx = null)
        {
            LastStreamExtractError = null;
            if (string.IsNullOrEmpty(fieldName)) return null;
            var fieldDict = FindField(fieldName);
            if (fieldDict == null) return null;

            // "MouseUp" can also come from /A (the primary field action).
            if (trigger == "MouseUp")
            {
                var aAction = _doc.ResolveObject(fieldDict[PdfName.A]) as PdfDictionary;
                if (aAction != null)
                {
                    var chained = RunActionChain(aAction, trigger, fieldName, jsCtx);
                    if (chained != null) return chained;
                }
            }

            var aa = _doc.ResolveObject(fieldDict[PdfName.AA]) as PdfDictionary;
            if (aa == null) return null;
            string key = PdfActionTypes.AdditionalActionKey(trigger, AdditionalActionsOwner.Field);
            if (key == null) return null;

            var actDict = _doc.ResolveObject(aa[new PdfName(key)]) as PdfDictionary;
            if (actDict == null) return null;

            return RunActionChain(actDict, trigger, fieldName, jsCtx);
        }

        // ------------------------------------------------------------------
        //  Core dispatch
        // ------------------------------------------------------------------

        public PdfActionResult RunAction(PdfAction action, PdfJsExecutionContext jsCtx = null)
        {
            var result = new PdfActionResult();
            if (action == null) return result;

            switch (action.ActionType)
            {
                case PdfActionType.JavaScript:
                    if (jsCtx == null)
                    {
                        result.Warnings.Add("JavaScript action skipped: no PdfJsExecutionContext provided.");
                        break;
                    }
                    // The engine is a plugin (GAPS.md §15b), so "nothing registered" is a real and
                    // reportable outcome. It must not look like a script that ran and did nothing:
                    // JavaScriptExecuted stays false and the reason is surfaced verbatim.
                    if (!PluginJavaScriptRunner.TryExecute(action.Target, jsCtx))
                    {
                        result.Warnings.Add(
                            "JavaScript action skipped: " + PluginJavaScriptRunner.LastError);
                        break;
                    }
                    result.JavaScriptExecuted = true;
                    result.EventRaised = "JavaScript";
                    break;

                case PdfActionType.Uri:
                    UriRequested?.Invoke(this, new UriActionEventArgs(action.Target));
                    result.EventRaised = "Uri";
                    break;

                case PdfActionType.GoTo:
                case PdfActionType.GoToRemote:
                    NavigationRequested?.Invoke(this,
                        new NavigationEventArgs(action.ActionType, action.Target));
                    result.EventRaised = "Navigation";
                    break;

                case PdfActionType.Named:
                    NamedActionRequested?.Invoke(this, new NamedActionEventArgs(action.Target));
                    result.EventRaised = "Named";
                    break;

                case PdfActionType.SubmitForm:
                case PdfActionType.ResetForm:
                case PdfActionType.ImportData:
                    FormActionRequested?.Invoke(this,
                        new FormActionEventArgs(action.ActionType, action.Target, action.FieldName));
                    result.EventRaised = "Form";
                    break;

                case PdfActionType.Launch:
                    LaunchRequested?.Invoke(this, new LaunchActionEventArgs(action.Target));
                    result.EventRaised = "Launch";
                    break;

                case PdfActionType.SetOcgState:
                    OcgStateChangeRequested?.Invoke(this, new OcgStateActionEventArgs(action.Target));
                    result.EventRaised = "SetOcgState";
                    break;

                case PdfActionType.Rendition:
                    result.Warnings.Add("Rendition (media) action is out of scope.");
                    break;

                default:
                    result.Warnings.Add($"Unsupported action type: {action.ActionType}");
                    break;
            }
            return result;
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Parses and runs <paramref name="actionDict"/>, then every action reachable through its
        /// <c>/Next</c> chain (§12.6.3), merging the whole chain into one result.
        /// </summary>
        /// <remarks>
        /// <para><b>Following the chain is what a conforming reader does, and not following it left
        /// a hole in this library's security model rather than merely in its output.</b> The runner
        /// never navigates, submits or launches anything — it raises an event and the host decides.
        /// So "run the chain" here means precisely "tell the host about every action the document
        /// asked for". Reporting only the head meant a host enumerating a document's behaviour was
        /// given a confident, incomplete answer.</para>
        /// <para><b>Bounded three ways</b>, because <c>/Next</c> is attacker-controlled: a depth cap
        /// (<see cref="PdfLimits.MaxActionChainDepth"/>), a visited set keyed on dictionary identity so a chain that loops
        /// back terminates, and a total-actions budget so a broad fan-out of arrays cannot cost more
        /// than a deep one. Each refusal is reported; none is silent.</para>
        /// </remarks>
        private PdfActionResult RunActionChain(PdfDictionary actionDict,
            string trigger, string fieldName, PdfJsExecutionContext jsCtx)
        {
            var head = ParseActionDict(actionDict, trigger, fieldName);
            if (head == null) return null;

            // The head is charged like any other action: a trigger whose own action is a script and
            // whose chain is fifteen more must not get a free sixteenth.
            int scripts = MaxJavaScriptActionsPerTrigger;
            bool headRan = ChargeScript(head, ref scripts);
            PdfActionResult result = headRan ? RunAction(head, jsCtx) : ScriptRefused();
            if (result == null) return null;
            if (!headRan) WarnOnce(result, ScriptRefusalMessage);

            // Identity, not equality: two structurally identical action dictionaries are two actions
            // and both should run, whereas one dictionary reached twice is a loop.
            var visited = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            visited.Add(actionDict);

            int budget = MaxActionsPerTrigger;
            var path = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance) { actionDict };
            FollowNext(actionDict, trigger, fieldName, jsCtx, result, visited, path, 1,
                       ref budget, ref scripts);
            return result;
        }

        /// <summary>
        /// Total actions one trigger may run, head included. Bounds a <c>/Next</c> chain that
        /// branches through arrays rather than nesting, which the depth cap alone does not.
        /// </summary>
        /// <remarks>
        /// <c>internal</c> so the DoS-guard test sizes its fan-out from the cap rather than
        /// hardcoding it — a guard test built on a literal stops testing the guard the moment the
        /// cap moves. Base already declares <c>InternalsVisibleTo</c>; this touches no API baseline.
        /// </remarks>
        internal const int MaxActionsPerTrigger = 256;

        /// <summary>
        /// JavaScript actions one trigger may <b>execute</b>, head included. Actions past this are
        /// still parsed and reported; they are simply not run.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a second, much smaller cap.</b> <see cref="MaxActionsPerTrigger"/> bounds
        /// how many actions the host is <i>told about</i>, which is this type's whole product — a
        /// host shown one of a document's five actions is not making the decision it thinks it is.
        /// So that cap must stay generous. But every JavaScript action reaching the plugin builds a
        /// fresh engine with its own ceilings, currently a 5-second timeout and 32 MB, so 256 of
        /// them is ~21 minutes of CPU from one attacker-controlled <c>/Next</c> chain. The
        /// per-action timeout is exactly what makes the total look bounded.</para>
        /// <para><b>Reusing one engine across the chain does not fix it</b> and was rejected on that
        /// basis: a Jint engine resets its constraints before each execution, so a shared engine
        /// hands the attacker the same 5 seconds per action and the worst case is unchanged — while
        /// leaking globals between chained scripts, which weakens the sandbox. The cost is the
        /// number of executions, so that is what is bounded.</para>
        /// <para>16 is well above anything conforming: the established model is one script per
        /// trigger with shared code in the document-level <c>/Names /JavaScript</c> tree, not a
        /// chain of them.</para>
        /// </remarks>
        internal const int MaxJavaScriptActionsPerTrigger = 16;

        /// <summary>
        /// Charges one JavaScript execution against the per-trigger script budget. Non-script
        /// actions are free — they cost a dictionary read, not an interpreter.
        /// </summary>
        private static bool ChargeScript(PdfAction action, ref int scripts)
            => action == null
            || action.ActionType != PdfActionType.JavaScript
            || --scripts >= 0;

        /// <summary>
        /// The outcome for a JavaScript action that was reported but not executed. It still raises
        /// the event, because "this document contains another script" is exactly what the host needs
        /// to know; <c>JavaScriptExecuted</c> stays false and the warning says why.
        /// </summary>
        private static PdfActionResult ScriptRefused() =>
            new PdfActionResult { EventRaised = "JavaScript" };

        /// <summary>The refusal text, added once per trigger rather than once per refused script.</summary>
        private static readonly string ScriptRefusalMessage =
            $"/Next action chain exceeded {MaxJavaScriptActionsPerTrigger} JavaScript actions " +
            "for one trigger; the remainder were reported but not executed.";

        /// <summary>
        /// Appends <paramref name="warning"/> unless it is already present.
        /// </summary>
        /// <remarks>
        /// Every action past a budget takes the same refusing branch, so appending unconditionally
        /// yields one copy per refused node — a warnings list a caller cannot read anything else out
        /// of. Both per-trigger budgets report once, for the reason <c>RenderBudget</c> gives for
        /// reporting once per page.
        /// </remarks>
        private static void WarnOnce(PdfActionResult result, string warning)
        {
            if (result != null && !result.Warnings.Contains(warning)) result.Warnings.Add(warning);
        }

        private void FollowNext(PdfDictionary current, string trigger, string fieldName,
            PdfJsExecutionContext jsCtx, PdfActionResult result,
            HashSet<PdfDictionary> visited, HashSet<PdfDictionary> path, int depth, ref int budget,
            ref int scripts)
        {
            if (current == null) return;

            var nextObj = _doc?.ResolveObject(current[PdfName.Next]) ?? current[PdfName.Next];
            if (nextObj == null) return;

            if (depth > PdfLimits.MaxActionChainDepth)
            {
                result.Warnings.Add(
                    $"/Next action chain exceeded {PdfLimits.MaxActionChainDepth} levels; " +
                    "the remaining actions were not run and are not reported.");
                return;
            }

            if (nextObj is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var entry = _doc?.ResolveObject(arr[i]) ?? arr[i];
                    if (entry is PdfDictionary d)
                        RunChained(d, trigger, fieldName, jsCtx, result, visited, path, depth, ref budget, ref scripts);
                }
            }
            else if (nextObj is PdfDictionary single)
            {
                RunChained(single, trigger, fieldName, jsCtx, result, visited, path, depth, ref budget, ref scripts);
            }
        }

        private void RunChained(PdfDictionary dict, string trigger, string fieldName,
            PdfJsExecutionContext jsCtx, PdfActionResult result,
            HashSet<PdfDictionary> visited, HashSet<PdfDictionary> path, int depth, ref int budget,
            ref int scripts)
        {
            // Identity, not equality: two structurally identical action dictionaries are two actions
            // and both should run, while one dictionary reached twice is already accounted for.
            //
            // "Already seen" is NOT the same as "a loop", and conflating them reported conforming
            // input as hostile: a converging chain — A with /Next [B C], B and C both with /Next D —
            // is acyclic, and D is legitimately reached twice. Only a dictionary that is on the
            // CURRENT ancestor path is a cycle; anything else is convergence and is simply not
            // re-run.
            if (!visited.Add(dict))
            {
                if (path.Contains(dict))
                    result.Warnings.Add(
                        "/Next action chain loops back to an action already on this chain; " +
                        "the cycle was cut here.");
                return;
            }

            if (--budget <= 0)
            {
                WarnOnce(result,
                    $"/Next action chain exceeded {MaxActionsPerTrigger} actions for one trigger; " +
                    "the remainder were not run and are not reported.");
                return;
            }

            var action = ParseActionDict(dict, trigger, fieldName);
            if (action == null) return;

            bool ran = ChargeScript(action, ref scripts);
            PdfActionResult chained = ran ? RunAction(action, jsCtx) : ScriptRefused();
            if (!ran) WarnOnce(result, ScriptRefusalMessage);
            if (chained != null)
            {
                if (chained.EventRaised != null) result.ChainedEvents.Add(chained.EventRaised);
                if (chained.JavaScriptExecuted) result.JavaScriptExecuted = true;
                foreach (string w in chained.Warnings) result.Warnings.Add(w);
            }

            path.Add(dict);
            FollowNext(dict, trigger, fieldName, jsCtx, result, visited, path, depth + 1, ref budget, ref scripts);
            path.Remove(dict);
        }

        /// <summary>
        /// An action dictionary as the <see cref="PdfAction"/> the runner raises, read through
        /// <see cref="PdfActionTypes"/> exactly as <see cref="PdfActionDispatcher"/> reads it.
        /// </summary>
        /// <remarks>
        /// <para>This was a second parse "mirroring" the dispatcher's, and the two disagreed on six
        /// action types, <c>Named</c>, <c>/URI</c> written as a name, and file specification
        /// dictionaries among them. A host shown a document's actions by the dispatcher was then
        /// raised different targets by the runner.</para>
        /// <para>Every entry is resolved (§7.3.10), the subtype included: read raw, <c>/S 9 0 R</c> would make a
        /// GoTo, a URI or a JavaScript action Unknown, silently doing nothing. A stream-valued
        /// <c>/JS</c> is always indirect, and read raw it would run as an empty script.</para>
        /// </remarks>
        private PdfAction ParseActionDict(PdfDictionary actionDict,
            string trigger, string fieldName)
        {
            string s = Rendering.PdfResolve.Name(_doc, actionDict, PdfName.S)?.Value ?? "";
            PdfActionType type = PdfActionTypes.FromSubtypeName(s);

            string target = PdfActionTypes.DescribeTarget(_doc, actionDict, type, out string error);
            if (error != null && LastStreamExtractError == null) LastStreamExtractError = error;

            return new PdfAction
            {
                ActionType = type,
                Target     = target,
                Trigger    = trigger,
                FieldName  = fieldName
            };
        }

        /// <summary>
        /// Why a JavaScript action's script stream could not be read on the last <c>Run*</c> call,
        /// or <c>null</c>. Useful for tooling that needs to distinguish "no script" from "script
        /// could not be loaded".
        /// </summary>
        /// <remarks>
        /// <para>Cleared at the start of <see cref="RunCatalogOpenAction"/>, <see cref="RunCatalogAa"/>,
        /// <see cref="RunPageAction"/> and <see cref="RunFieldAction"/>, and keeping the first failure
        /// of a chain. It was cleared at every decode instead, so a chain whose first script was
        /// unreadable and whose second was fine reported nothing.</para>
        /// <para>Backed by a <c>[ThreadStatic]</c> field, so a concurrent run on another thread cannot
        /// overwrite it — read it on the thread that ran the call (see the Diagnostics Pattern section
        /// of CLAUDE.md).</para>
        /// </remarks>
        public static string LastStreamExtractError
        {
            get => _lastStreamExtractError;
            private set => _lastStreamExtractError = value;
        }

        [System.ThreadStatic]
        private static string _lastStreamExtractError;

        /// <summary>The field dictionary by fully qualified name, built at the first search; null until then.</summary>
        private Dictionary<string, PdfDictionary> _fieldsByName;

        /// <summary>
        /// A field by its fully qualified name, or null — from an index of the AcroForm
        /// <c>/Fields</c> tree built once, at the first search, by the one walk below: the first node
        /// a name reaches in the walk's order, as a search per name finds it. A runner asked for each
        /// of a form's calculations walked the tree once each, the calculations times the fields.
        /// </summary>
        private PdfDictionary FindField(string fullyQualifiedName)
        {
            if (_doc.AcroForm == null) return null;
            if (_fieldsByName == null)
            {
                _fieldsByName = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
                if (_doc.ResolveObject(_doc.AcroForm.Dictionary[PdfName.Fields]) is PdfArray fieldsArr)
                    IndexFields(fieldsArr, null, 0, new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer));
            }
            return _fieldsByName.TryGetValue(fullyQualifiedName, out PdfDictionary field) ? field : null;
        }

        /// <summary>
        /// Depth-first walk of the AcroForm field tree into <see cref="_fieldsByName"/>, bounded by
        /// <see cref="PdfLimits.MaxTreeDepth"/> <b>and</b> by <paramref name="visited"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>A depth cap bounds one branch; it does not bound the number of branches.</b> With
        /// <c>10 0 obj &lt;&lt; /T (a) /Kids [11 0 R 11 0 R] &gt;&gt;</c> and
        /// <c>11 0 obj &lt;&lt; /T (b) /Kids [10 0 R 10 0 R] &gt;&gt;</c> every level doubles the
        /// work, so a cap of 100 alone would permit 2<sup>100</sup> calls from a document of a few
        /// hundred bytes; the visited set admits each node once.</para>
        /// <para><paramref name="visited"/> is keyed on the resolved dictionary instance, so it
        /// catches a repeat whether the file reaches it by the same reference or a different one,
        /// and it covers direct dictionaries that have no object number — the keying
        /// <c>PdfUsageRightsReader.CollectSignatureFields</c> and <c>PdfFormReader</c>'s walk use.</para>
        /// </remarks>
        private void IndexFields(PdfArray fields, string parentName, int depth, HashSet<PdfObject> visited)
        {
            if (depth >= PdfLimits.MaxTreeDepth) return;
            foreach (var entry in fields)
            {
                var fd = _doc.ResolveObject(entry) as PdfDictionary;
                if (fd == null) continue;
                if (!visited.Add(fd)) continue;

                // Resolved: raw, a group written /T 6 0 R contributed no name, so no field below
                // it could be found by the name PdfFormField.FieldName — which resolves — lists.
                string full = Core.Forms.FieldNames.Qualify(
                    parentName, Rendering.PdfResolve.Str(_doc, fd, PdfName.T)?.GetText());

                if (full != null && !_fieldsByName.ContainsKey(full)) _fieldsByName[full] = fd;

                var kids = _doc.ResolveObject(fd[PdfName.Kids]) as PdfArray;
                if (kids != null) IndexFields(kids, full, depth + 1, visited);
            }
        }
    }
}
