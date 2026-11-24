// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>Action type for a PDF action dictionary (gaps 23.1/23.2/23.3).</summary>
    public enum PdfActionType
    {
        GoTo,
        GoToRemote,
        Launch,
        Uri,
        JavaScript,
        SubmitForm,
        ResetForm,
        ImportData,
        Named,
        SetOcgState,
        Rendition,
        Unknown
    }

    /// <summary>Where in a document an action was found.</summary>
    public enum PdfActionSource
    {
        /// <summary>
        /// The document as a whole: a document-level script in <c>/Names /JavaScript</c>, the
        /// catalog's <c>/OpenAction</c>, or the catalog's <c>/AA</c>.
        /// </summary>
        Document,

        /// <summary>A page's <c>/AA</c>. <see cref="PdfAction.PageIndex"/> names the page.</summary>
        Page,

        /// <summary>
        /// An interactive form field's <c>/A</c> or <c>/AA</c>, widgets reached through the field
        /// tree included. <see cref="PdfAction.FieldName"/> names the field.
        /// </summary>
        Field,

        /// <summary>
        /// An annotation's <c>/A</c> or <c>/AA</c> that no form field reaches. A link is the common
        /// one. <see cref="PdfAction.PageIndex"/> names the page whose <c>/Annots</c> lists it.
        /// </summary>
        Annotation,

        /// <summary>An outline item's <c>/A</c>.</summary>
        Outline,
    }

    /// <summary>Describes a single PDF action extracted from the document.</summary>
    public sealed class PdfAction
    {
        /// <summary>The action subtype.</summary>
        public PdfActionType ActionType { get; init; }

        /// <summary>
        /// The primary target: URI string, file path, page destination description,
        /// or script text for JavaScript actions.
        /// </summary>
        public string Target { get; init; }

        /// <summary>
        /// The event that runs the action: <c>OpenDocument</c> for a document-level script and the
        /// <c>/OpenAction</c>; <c>MouseUp</c> for an <c>/A</c> entry; for an <c>/AA</c> entry, the
        /// name its owner's table gives the key (<c>WillClose</c>, <c>PageOpen</c>,
        /// <c>Keystroke</c>, <c>Calculate</c>, …), or the key itself where the table defines none.
        /// </summary>
        public string Trigger { get; init; }

        /// <summary>
        /// The field's fully qualified name for a <see cref="PdfActionSource.Field"/> action, or
        /// null.
        /// </summary>
        public string FieldName { get; init; }

        /// <summary>Where the action was found.</summary>
        public PdfActionSource Source { get; init; }

        /// <summary>
        /// The zero-based page for a <see cref="PdfActionSource.Page"/> or
        /// <see cref="PdfActionSource.Annotation"/> action, or <c>-1</c>.
        /// </summary>
        public int PageIndex { get; init; } = -1;

        /// <summary>
        /// How far along a <c>/Next</c> chain (§12.6.3) this action sits: <c>0</c> for the one the
        /// trigger names directly, <c>1</c> for its <c>/Next</c>, and so on.
        /// </summary>
        /// <remarks>
        /// <b>Every action in a chain carries the same <see cref="Trigger"/> and
        /// <see cref="FieldName"/>, because it is the same event that runs them all.</b> Without
        /// this a caller sees several actions on one trigger and cannot tell an ordered chain from
        /// an unordered set — and the order is the whole meaning of <c>/Next</c>.
        /// </remarks>
        public int ChainDepth { get; init; }

        /// <summary>
        /// The resolved action dictionary itself.
        /// </summary>
        /// <remarks>
        /// The fixed field set above (<see cref="ActionType"/>, <see cref="Target"/>, …) answers the
        /// questions this dispatcher already anticipated. A caller asking a question it did not —
        /// whether a <c>/Launch</c> action carries a deprecated <c>/Win</c>/<c>/Mac</c>/<c>/Unix</c>
        /// entry, say — needs the raw dictionary, not a new field added here for every such question.
        /// This walk already visits every action once with full DoS guards in place; keeping the
        /// reference costs nothing extra.
        /// </remarks>
        public PdfDictionary Dictionary { get; init; }
    }

    /// <summary>
    /// Extracts all actions from a PDF document and exposes them as structured
    /// <see cref="PdfAction"/> descriptors (gaps 23.1/23.2/23.3).
    /// </summary>
    /// <remarks>
    /// <para>Walks, in this order, with each action's <see cref="PdfAction.Source"/>:</para>
    /// <list type="number">
    ///   <item><description>The document-level scripts in the catalog's <c>/Names /JavaScript</c>
    ///   tree, on <c>OpenDocument</c>. §12.6.4.17: "When the document is opened, all of the actions
    ///   in this name tree shall be executed, defining ECMAScript functions for use by other
    ///   scripts", so they come before anything that may call them.</description></item>
    ///   <item><description>The catalog's <c>/OpenAction</c>, on <c>OpenDocument</c>, and the
    ///   catalog's <c>/AA</c>.</description></item>
    ///   <item><description>Each page's <c>/AA</c>.</description></item>
    ///   <item><description>Each AcroForm field's <c>/A</c> and <c>/AA</c>, widgets reached through
    ///   the field tree included.</description></item>
    ///   <item><description>Each annotation's <c>/A</c> and <c>/AA</c>, in page order, unless the
    ///   field walk already listed it.</description></item>
    ///   <item><description>Each outline item's <c>/A</c>.</description></item>
    /// </list>
    /// <para>Every action on each one's <c>/Next</c> chain is listed after it (§12.6.3). An
    /// <c>/AA</c> entry is named by the trigger its owner's table gives it
    /// (<see cref="PdfActionTypes.Triggers"/>), and an entry under a key the table does not define
    /// is still listed, under the key.</para>
    /// <para><b>The last four sources matter most</b>: they are where a document usually keeps a
    /// <c>/Launch</c> or a script — a link, an annotation's page-open trigger, a document-level
    /// script.</para>
    /// </remarks>
    public static class PdfActionDispatcher
    {
        /// <summary>
        /// Why the last extraction's list is incomplete, or <c>null</c> when it is complete: a
        /// <c>/Next</c> chain cut at a bound, a name tree cut at its limit, the field tree or the
        /// outline cut at <see cref="PdfLimits.MaxTreeDepth"/>, or the document-wide ceiling
        /// (<see cref="MaxActionsListed"/>) reached.
        /// </summary>
        /// <remarks>
        /// <para><b>Silence is the one unacceptable answer here.</b> This method's job is to tell a
        /// caller what a document will do, and a scanner looking for a <c>/Launch</c> or a script
        /// acts on the absence of one. A list that is short because a bound was hit has to be
        /// distinguishable from a list that is short because the document is small.</para>
        /// <para><c>[ThreadStatic]</c>, and cleared as the first statement of every entry point on
        /// every path — before the argument checks, so a throw cannot leave the previous call's
        /// answer standing.</para>
        /// </remarks>
        public static string LastChainDiagnostic => _lastChainDiagnostic;

        [ThreadStatic]
        private static string _lastChainDiagnostic;

        /// <summary>
        /// Why an action on the last extraction has no <see cref="PdfAction.Target"/> although it
        /// has one in the file (a script stream that would not decode), or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>A slot of its own because it is a different concern from
        /// <see cref="LastChainDiagnostic"/>: every action is listed, and one of them could not be
        /// read. Outside any handler, one corrupt script stream would throw out of the extraction and
        /// the caller get no list at all.</para>
        /// <para><c>[ThreadStatic]</c>, cleared with <see cref="LastChainDiagnostic"/>, and keeping
        /// the first failure.</para>
        /// </remarks>
        public static string LastTargetError => _lastTargetError;

        [ThreadStatic]
        private static string _lastTargetError;

        /// <summary>
        /// Total actions one <c>/Next</c> chain may contribute, head included.
        /// </summary>
        /// <remarks>
        /// <b>The depth cap is not this cap.</b> <c>/Next</c> may be an array, so a chain is a tree
        /// and a depth of 64 permits an unbounded number of nodes — the same "a depth cap bounds one
        /// branch, never the number of branches" shape that <see cref="CollectFieldActions"/> was
        /// fixed for. Matches <c>PdfActionRunner.MaxActionsPerTrigger</c>, because the two walk the
        /// same structure and a document the runner will execute in full should not be one the
        /// dispatcher describes in part.
        /// </remarks>
        internal const int MaxActionsPerChain = 256;

        /// <summary>
        /// Total actions one extraction lists, across every source and chain.
        /// </summary>
        /// <remarks>
        /// <para><b><see cref="MaxActionsPerChain"/> bounds a chain, not the number of chains.</b>
        /// Each trigger's chain has its own budget and its own visited set, because two triggers
        /// running one chain both run all of it. So a document can point any number of triggers at
        /// one 256-action chain and have it listed once per trigger: a hundred thousand links sharing
        /// one chain is 25.6 million descriptors from a file of a few megabytes.</para>
        /// <para>65,536 is far above a real form. Two thousand fields with four scripts each is
        /// eight thousand actions. Reaching it is reported on <see cref="LastChainDiagnostic"/>.</para>
        /// </remarks>
        internal const int MaxActionsListed = 65536;

        /// <summary>Extracts all actions from a PDF file.</summary>
        public static IReadOnlyList<PdfAction> ExtractActions(string inputPath)
        {
            ResetDiagnostics();
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ExtractActions(fs);
        }

        /// <summary>Extracts all actions from a PDF stream.</summary>
        public static IReadOnlyList<PdfAction> ExtractActions(Stream input)
        {
            ResetDiagnostics();
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            return CollectActions(doc);
        }

        /// <summary>
        /// Extracts all actions from an already-open <see cref="PdfDocument"/>, for a caller that
        /// has one open already and would otherwise have to re-serialize it to a stream just to
        /// reach this walk.
        /// </summary>
        public static IReadOnlyList<PdfAction> ExtractActions(PdfDocument doc)
        {
            ResetDiagnostics();
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return CollectActions(doc);
        }

        /// <summary>
        /// Extracts the AcroForm calculation order (/CO array) — field names in evaluation order.
        /// </summary>
        public static IReadOnlyList<string> GetCalculationOrder(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return GetCalculationOrder(fs);
        }

        /// <summary>
        /// Extracts the AcroForm calculation order (/CO array) — field names in evaluation order.
        /// </summary>
        /// <remarks>
        /// Each name is the field's fully qualified name, the one
        /// <see cref="PdfActionRunner.RunFieldAction"/> finds it by. With the raw leaf <c>/T</c>, a
        /// calculated field inside a group could not be run by the name listed for it, and an
        /// indirect <c>/T</c> would leave the field out of the order.
        /// </remarks>
        public static IReadOnlyList<string> GetCalculationOrder(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            return CollectCalculationOrder(doc);
        }

        // -------------------------------------------------------------------
        //  Implementation
        // -------------------------------------------------------------------

        private static void ResetDiagnostics()
        {
            _lastChainDiagnostic = null;
            _lastTargetError = null;
        }

        /// <summary>One extraction's output and the budget it shares across every source.</summary>
        private sealed class Walk
        {
            internal Walk(PdfDocument doc) => Doc = doc;

            internal PdfDocument Doc { get; }
            internal List<PdfAction> Result { get; } = new List<PdfAction>();

            /// <summary>Actions this extraction may still list. See <see cref="MaxActionsListed"/>.</summary>
            internal int Remaining = MaxActionsListed;

            /// <summary>
            /// The field and widget dictionaries the field walk visited, so the annotation walk does
            /// not list a widget's actions a second time under no field name.
            /// </summary>
            internal HashSet<PdfObject> FieldNodes { get; } = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);
        }

        private static IReadOnlyList<PdfAction> CollectActions(PdfDocument doc)
        {
            var walk = new Walk(doc);

            if (doc.Catalog == null) return walk.Result;
            var catalog = doc.Catalog.Dictionary;

            CollectDocumentScripts(walk, catalog);

            var openAction = doc.ResolveObject(catalog[PdfName.OpenAction]);
            if (openAction is PdfDictionary openActionDict)
                CollectActionChain(walk, openActionDict, "OpenDocument", null, PdfActionSource.Document, -1);

            if (doc.ResolveObject(catalog[PdfName.AA]) is PdfDictionary catalogAa)
                CollectAaActions(walk, catalogAa, AdditionalActionsOwner.Catalog, null, PdfActionSource.Document, -1);

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                if (doc.ResolveObject(doc.Pages[i].Dictionary[PdfName.AA]) is PdfDictionary pageAa)
                    CollectAaActions(walk, pageAa, AdditionalActionsOwner.Page, null, PdfActionSource.Page, i);
            }

            if (doc.AcroForm != null
                && doc.ResolveObject(doc.AcroForm.Dictionary[PdfName.Fields]) is PdfArray fieldsArray)
                CollectFieldActions(walk, fieldsArray, null, depth: 0);

            CollectAnnotationActions(walk);
            CollectOutlineActions(walk, catalog);

            return walk.Result.AsReadOnly();
        }

        /// <summary>
        /// The document-level scripts alone — the actions of the catalog's <c>/Names /JavaScript</c>
        /// tree, in tree order — which a viewer runs once, when the document opens, before any other.
        /// </summary>
        internal static IReadOnlyList<PdfAction> DocumentLevelScripts(PdfDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var walk = new Walk(doc);
            if (doc.Catalog != null) CollectDocumentScripts(walk, doc.Catalog.Dictionary);
            return walk.Result.AsReadOnly();
        }

        /// <summary>The actions in the catalog's <c>/Names /JavaScript</c> tree, in tree order.</summary>
        private static void CollectDocumentScripts(Walk walk, PdfDictionary catalog)
        {
            if (!(walk.Doc.ResolveObject(catalog[PdfName.Names]) is PdfDictionary names)) return;

            var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            if (!PdfNameTree.TryFlatten(names[PdfName.JavaScript], walk.Doc, pairs, ref budget))
                ReportOnce($"The /Names /JavaScript tree was cut at its limit ({PdfLimits.MaxNameTreeEntries} " +
                           "entries or its depth cap); document-level scripts past that point are not listed.");

            foreach (var pair in pairs)
                if (walk.Doc.ResolveObject(pair.Value) is PdfDictionary script)
                    CollectActionChain(walk, script, "OpenDocument", null, PdfActionSource.Document, -1);
        }

        /// <summary>
        /// Collects the <c>/A</c> and <c>/AA</c> actions of every field in an AcroForm field tree,
        /// bounded by <see cref="PdfLimits.MaxTreeDepth"/> <b>and</b> by a visited set.
        /// </summary>
        /// <remarks>
        /// <para><b>A depth cap alone is not a guard.</b> It prevents a stack overflow on thousands
        /// of nested kid arrays, but with no record of what has already been visited, a field whose
        /// <c>/Kids</c> names the same object twice <i>doubles</i> the work at every level, so sixty
        /// such levels — well under the cap, and therefore never throwing — would be 2<sup>60</sup>
        /// calls out of the public <see cref="ExtractActions(System.IO.Stream)"/>.
        /// <b>A depth cap bounds one branch; it does not bound the number of branches.</b></para>
        /// <para>The cut is at the repeat, not the whole walk: aborting the walk on a two-node
        /// <c>/Kids</c> cycle would lose every action in the document — including fields listed
        /// after the cycle — to an exception thrown out of a public API.</para>
        /// <para>A cycle guard is an <i>absence</i>: nothing asserts that a walk terminated, and
        /// every non-adversarial fixture is a tree, where the guard is invisible.</para>
        /// <para>The visited set is keyed on the resolved dictionary instance, so it catches a repeat
        /// whether the file reaches it by the same reference or a different one, and it covers direct
        /// dictionaries that have no object number. It is also the set the annotation walk skips.</para>
        /// <para>A node's name is <see cref="FieldNames.Qualify"/> of its parent's and its own
        /// resolved <c>/T</c>: the name <c>PdfFormField.FullyQualifiedName</c> gives it, so an
        /// indirect <c>/T</c> names its node and an empty one adds no trailing period.</para>
        /// </remarks>
        private static void CollectFieldActions(Walk walk, PdfArray fieldsArray, string parentName, int depth)
        {
            if (depth >= PdfLimits.MaxTreeDepth)
            {
                // A cut only when something is below it: an empty /Kids at the cap loses nothing.
                if (fieldsArray.Count > 0)
                    ReportOnce($"The AcroForm field tree was cut at {PdfLimits.MaxTreeDepth} levels; " +
                               "the actions of fields below that depth are not listed.");
                return;
            }
            var doc = walk.Doc;

            for (int i = 0; i < fieldsArray.Count; i++)
            {
                if (!(doc.ResolveObject(fieldsArray[i]) is PdfDictionary fieldDict)) continue;
                if (!walk.FieldNodes.Add(fieldDict)) continue;

                string fieldName = FieldNames.Qualify(parentName, Rendering.PdfResolve.Str(doc, fieldDict, PdfName.T)?.GetText());

                if (doc.ResolveObject(fieldDict[PdfName.A]) is PdfDictionary fieldAction)
                    CollectActionChain(walk, fieldAction, "MouseUp", fieldName, PdfActionSource.Field, -1);

                if (doc.ResolveObject(fieldDict[PdfName.AA]) is PdfDictionary fieldAa)
                    CollectAaActions(walk, fieldAa, AdditionalActionsOwner.Field, fieldName, PdfActionSource.Field, -1);

                if (doc.ResolveObject(fieldDict[PdfName.Kids]) is PdfArray kidsArray)
                    CollectFieldActions(walk, kidsArray, fieldName, depth + 1);
            }
        }

        /// <summary>
        /// Each annotation's <c>/A</c> (on <c>MouseUp</c>: Table 197 gives it precedence over
        /// <c>/AA /U</c>) and <c>/AA</c>, in page order, skipping any the field walk reached.
        /// </summary>
        /// <remarks>
        /// Through <c>AnnotationSubtypeWalk.All</c>, which resolves each entry and its subtype. An
        /// annotation listed twice, on one page or two, is walked once.
        /// </remarks>
        private static void CollectAnnotationActions(Walk walk)
        {
            var seen = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);
            foreach (var (pageIndex, annot, _) in Extraction.AnnotationSubtypeWalk.All(walk.Doc,
                         new Extraction.AnnotationWalkReport(m => _lastChainDiagnostic ??= m)))
            {
                if (walk.FieldNodes.Contains(annot) || !seen.Add(annot)) continue;

                if (walk.Doc.ResolveObject(annot[PdfName.A]) is PdfDictionary action)
                    CollectActionChain(walk, action, "MouseUp", null, PdfActionSource.Annotation, pageIndex);

                if (walk.Doc.ResolveObject(annot[PdfName.AA]) is PdfDictionary aa)
                    CollectAaActions(walk, aa, AdditionalActionsOwner.Annotation, null, PdfActionSource.Annotation, pageIndex);
            }
        }

        /// <summary>
        /// Each outline item's <c>/A</c> (Table 151), on <c>MouseUp</c>, depth first in reading order.
        /// </summary>
        /// <remarks>
        /// Through <see cref="Structure.OutlineItems"/>'s walk: bounded by <see cref="PdfLimits.MaxTreeDepth"/>
        /// for the recursion into <c>/First</c> and by one visited set across the whole outline, so an
        /// item reached twice — a <c>/Next</c> that loops, a <c>/First</c> naming an ancestor — is walked
        /// once; a cut is reported.
        /// </remarks>
        private static void CollectOutlineActions(Walk walk, PdfDictionary catalog)
        {
            Structure.OutlineItems.Walk(walk.Doc, catalog, item =>
                {
                    if (walk.Doc.ResolveObject(item[PdfName.A]) is PdfDictionary action)
                        CollectActionChain(walk, action, "MouseUp", null, PdfActionSource.Outline, -1);
                    return true;
                },
                cut => ReportOnce($"The outline was cut at {PdfLimits.MaxTreeDepth} levels; the actions of " +
                                  "items below that depth are not listed."));
        }

        /// <summary>
        /// Each action in an <c>/AA</c> dictionary: the keys its owner's table defines, in the
        /// table's order, then any other entry that holds an action, under its own key.
        /// </summary>
        /// <remarks>
        /// The second pass is what makes the list a floor a caller can rely on. A key the table does
        /// not define is not one a conforming reader runs, and it is still an action in the file, so
        /// it is listed rather than skipped.
        /// </remarks>
        private static void CollectAaActions(Walk walk, PdfDictionary aa, AdditionalActionsOwner owner,
            string fieldName, PdfActionSource source, int pageIndex)
        {
            var triggers = PdfActionTypes.Triggers(owner);
            var defined = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (key, trigger) in triggers)
            {
                defined.Add(key);
                if (walk.Doc.ResolveObject(aa[new PdfName(key)]) is PdfDictionary action)
                    CollectActionChain(walk, action, trigger, fieldName, source, pageIndex);
            }

            foreach (var entry in aa)
            {
                if (defined.Contains(entry.Key.Value)) continue;
                if (walk.Doc.ResolveObject(entry.Value) is PdfDictionary action)
                    CollectActionChain(walk, action, entry.Key.Value, fieldName, source, pageIndex);
            }
        }

        /// <summary>
        /// Adds the action <paramref name="head"/> names <b>and every action on its <c>/Next</c>
        /// chain</b> (§12.6.3), which one trigger runs in order.
        /// </summary>
        /// <remarks>
        /// <para><b>Reading only the head would give a confident, incomplete answer</b>, and this is
        /// the method a security scanner reaches: an <c>/OpenAction</c> whose second link is a
        /// <c>/Launch</c> or a script would be reported as a single harmless <c>/GoTo</c>, with
        /// nothing saying a chain had been ignored. <c>PdfActionRunner</c> follows <c>/Next</c> too,
        /// so the two agree: what one executes the other describes.</para>
        ///
        /// <para><b>Bounded four ways, because <c>/Next</c> is attacker-controlled</b> — depth
        /// (<see cref="PdfLimits.MaxActionChainDepth"/>), a per-chain node budget
        /// (<see cref="MaxActionsPerChain"/>) because an array-valued <c>/Next</c> makes the chain a
        /// tree, the document-wide <see cref="MaxActionsListed"/>, and reference identity so a loop
        /// terminates. The first three report on <see cref="LastChainDiagnostic"/>; the fourth does
        /// not, and that is the point below.</para>
        ///
        /// <para><b>A repeated action is dedupe, not truncation, so it is silent.</b> Describing an
        /// action twice tells a caller nothing new, and a diamond — two branches reaching one
        /// action — is perfectly legal. This is where extraction and execution genuinely differ:
        /// <c>PdfActionRunner</c> keeps a separate <c>path</c> set precisely because re-*running* a
        /// shared action is correct behaviour, while re-*listing* it is noise.</para>
        /// </remarks>
        private static void CollectActionChain(Walk walk, PdfDictionary head, string trigger,
            string fieldName, PdfActionSource source, int pageIndex)
        {
            int budget = MaxActionsPerChain;
            var at = new Origin(trigger, fieldName, source, pageIndex);
            FollowChain(walk, head, at, new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer),
                        depth: 0, budget: ref budget);
        }

        /// <summary>What every action on one chain shares: the event that runs them and where it is.</summary>
        private readonly struct Origin
        {
            internal Origin(string trigger, string fieldName, PdfActionSource source, int pageIndex)
            {
                Trigger = trigger;
                FieldName = fieldName;
                Source = source;
                PageIndex = pageIndex;
            }

            internal string Trigger { get; }
            internal string FieldName { get; }
            internal PdfActionSource Source { get; }
            internal int PageIndex { get; }
        }

        private static void FollowChain(Walk walk, PdfDictionary action, in Origin at,
            HashSet<PdfObject> visited, int depth, ref int budget)
        {
            if (action == null) return;
            if (!visited.Add(action)) return;      // already described; see the remarks above

            if (depth > PdfLimits.MaxActionChainDepth)
            {
                ReportOnce($"A /Next action chain was cut at {PdfLimits.MaxActionChainDepth} levels; " +
                           "actions past that point are not listed.");
                return;
            }

            if (budget <= 0)
            {
                ReportOnce($"A /Next action chain reached the {MaxActionsPerChain}-action ceiling; " +
                           "actions past that point are not listed.");
                return;
            }

            if (walk.Remaining <= 0)
            {
                ReportOnce($"The document reached the {MaxActionsListed}-action ceiling; " +
                           "actions past that point are not listed.");
                return;
            }

            budget--;
            walk.Remaining--;
            walk.Result.Add(ParseAction(walk.Doc, action, at, depth));

            var next = walk.Doc.ResolveObject(action[PdfName.Next]);
            if (next is PdfDictionary single)
            {
                FollowChain(walk, single, at, visited, depth + 1, ref budget);
                return;
            }

            // §12.6.3: /Next may be an ARRAY of actions, all performed after this one.
            if (next is PdfArray branches)
            {
                for (int i = 0; i < branches.Count; i++)
                    if (walk.Doc.ResolveObject(branches[i]) is PdfDictionary branch)
                        FollowChain(walk, branch, at, visited, depth + 1, ref budget);
            }
        }

        /// <summary>
        /// Records a truncation, keeping the first reason rather than the last.
        /// </summary>
        /// <remarks>
        /// <para>One extraction walks many chains and a hostile document can trip a bound on more
        /// than one of them, so with a single slot something has to decide which survives.</para>
        /// <para><b>First, because the walk starts at the document's entry point.</b> The
        /// document-level scripts and <c>/OpenAction</c> are read before the catalog's <c>/AA</c>,
        /// the pages, the fields, the annotations and the outline — so the first truncation is the
        /// one nearest what a reader does on opening the file, and the last is whichever item
        /// happened to be enumerated last. Keeping the last would also mean a document could bury the
        /// interesting cut behind a thousand uninteresting ones.</para>
        /// </remarks>
        private static void ReportOnce(string reason)
        {
            if (_lastChainDiagnostic == null) _lastChainDiagnostic = reason;
        }

        private static PdfAction ParseAction(PdfDocument doc, PdfDictionary actionDict, in Origin at, int chainDepth)
        {
            // Resolved: `/S 9 0 R` is legal (§7.3.10) and read raw it reported the action as Unknown.
            string sValue = Rendering.PdfResolve.Name(doc, actionDict, PdfName.S)?.Value ?? "";
            PdfActionType actionType = PdfActionTypes.FromSubtypeName(sValue);

            string target = PdfActionTypes.DescribeTarget(doc, actionDict, actionType, out string error);
            if (error != null && _lastTargetError == null) _lastTargetError = error;

            return new PdfAction
            {
                ActionType = actionType,
                Target     = target,
                Trigger    = at.Trigger,
                FieldName  = at.FieldName,
                Source     = at.Source,
                PageIndex  = at.PageIndex,
                ChainDepth = chainDepth,
                Dictionary = actionDict
            };
        }

        internal static IReadOnlyList<string> CollectCalculationOrder(PdfDocument doc)
        {
            var result = new List<string>();
            if (doc.Catalog == null) return result;

            var acroFormObj = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);
            if (!(acroFormObj is PdfDictionary acroForm)) return result;

            if (!(doc.ResolveObject(acroForm[PdfName.CO]) is PdfArray coArray)) return result;

            var names = QualifiedFieldNames(doc);
            for (int i = 0; i < coArray.Count; i++)
            {
                if (!(doc.ResolveObject(coArray[i]) is PdfDictionary fieldDict)) continue;

                // A /CO entry the field tree does not reach still has its own name to offer.
                string name = names.TryGetValue(fieldDict, out string qualified)
                    ? qualified
                    : Rendering.PdfResolve.Str(doc, fieldDict, PdfName.T)?.GetText();
                if (!string.IsNullOrEmpty(name)) result.Add(name);
            }

            return result.AsReadOnly();
        }

        /// <summary>
        /// Every field dictionary in the form's tree, by identity, with its fully qualified name as
        /// <see cref="Core.Forms.PdfFormField.FullyQualifiedName"/> gives it.
        /// </summary>
        /// <remarks>
        /// Through the field model rather than a walk of its own, so the calculation order names a
        /// field exactly as the form reader and <see cref="PdfActionRunner.RunFieldAction"/> do. The
        /// model's recursion is bounded here the way <c>PdfAcroForm.GetSignatureFields</c> bounds
        /// its own: <see cref="PdfLimits.MaxTreeDepth"/> and a visited set.
        /// </remarks>
        private static Dictionary<PdfDictionary, string> QualifiedFieldNames(PdfDocument doc)
        {
            var names = new Dictionary<PdfDictionary, string>(PdfReferenceEquality.Instance);
            if (doc.AcroForm != null) AddNames(doc.AcroForm.Fields, names, 0);
            return names;
        }

        private static void AddNames(IReadOnlyList<Core.Forms.PdfFormField> fields,
            Dictionary<PdfDictionary, string> names, int depth)
        {
            if (depth >= PdfLimits.MaxTreeDepth) return;
            foreach (var field in fields)
            {
                var dict = field?.Dictionary;
                if (dict == null || names.ContainsKey(dict)) continue;
                names.Add(dict, field.FullyQualifiedName);
                AddNames(field.Kids, names, depth + 1);
            }
        }
    }
}
