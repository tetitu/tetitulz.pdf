// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Accessibility.Checking.Ua2
{
    // §8.2.5.20 (link annotations that target different locations are in separate elements), §8.9.2.3
    // (a markup annotation's /RC and /Contents textually equivalent) and §8.10.3.3 (a text field's /RV and
    // /V textually equivalent).
    public static partial class PdfUa2StructureChecker
    {
        // ----------------------------------------------------------------
        //  Clause 8.2.5.20 — one target a Link or Reference element
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.20: "Link annotations that target different locations shall be in separate
        /// Link or Reference structure elements." Each Link annotation an <c>/OBJR</c> names is grouped under the
        /// nearest element enclosing it that is a Link or a Reference (<see cref="TypeOf(PdfDocument, StructNode)"/>),
        /// and an element whose annotations reach more than one target is reported. Shares <c>UA2-8.2.5.20</c>
        /// with <see cref="CheckLinkAnnotationEnclosedInLinkOrReference"/>.
        /// </summary>
        /// <remarks>
        /// <para>A target is what <see cref="LinkTarget"/> says: a destination in this document by the page or
        /// structure element it names — a named destination followed to it — a URI by its text, a remote GoTo
        /// by its file and destination. The view a destination asks for is not compared: two positions on one
        /// page are one location here, so a link whose parts open the same page at different zooms is not
        /// reported. An annotation whose target cannot be read — another action, a named destination the
        /// document does not define — is not compared at all. The clause's "should" about which of Link and
        /// Reference suits a target is not judged.</para>
        /// </remarks>
        private static void CheckLinkTargetsAreSeparate(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            Dictionary<(bool InNameTree, string Key), PdfObject> named = null;
            var targets = new Dictionary<StructNode, HashSet<string>>();
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Dict == null) return;
                foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                {
                    var objr = Resolve(doc, rawKid) as PdfDictionary;
                    if (objr == null || StructKidClassifier.Classify(objr, doc) != StructKidKind.ObjectReference) continue;
                    var annot = Resolve(doc, objr[PdfName.Obj]) as PdfDictionary;
                    if (annot == null || !HasSubtype(doc, annot, PdfAnnotationSubtypes.Link)) continue;

                    StructNode link = null;
                    for (StructNode n = node; n != null && link == null; n = n.Parent)
                    {
                        string type = TypeOf(doc, n);
                        if (type == "Link" || type == "Reference") link = n;
                    }
                    if (link == null) continue;

                    named ??= NamedDestinationLookup(doc, catalog);
                    string target = LinkTarget(doc, annot, named);
                    if (target == null) continue;
                    if (!targets.TryGetValue(link, out HashSet<string> seen)) targets[link] = seen = new HashSet<string>(StringComparer.Ordinal);
                    seen.Add(target);
                }
            });

            int found = 0;
            foreach (var entry in targets)
            {
                if (entry.Value.Count < 2) continue;
                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.20",
                    $"A {Described(doc, entry.Key)} structure element" +
                    (entry.Key.ObjectNumber > 0 ? $" (object {entry.Key.ObjectNumber})" : "") +
                    $" encloses link annotations that target {entry.Value.Count} different locations; link " +
                    "annotations that target different locations are in separate Link or Reference structure " +
                    "elements (ISO 14289-2 §8.2.5.20)."));
            }
            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.20",
                    $"…and {found - IndividualReportLimit} more, not listed individually."));
        }

        /// <summary>The named destinations by where a lookup goes: a name to <c>/Dests</c>, a string to the name tree; the first of a key wins.</summary>
        private static Dictionary<(bool InNameTree, string Key), PdfObject> NamedDestinationLookup(PdfDocument doc, PdfDictionary catalog)
        {
            var lookup = new Dictionary<(bool, string), PdfObject>(
                KeyedHash.Comparer<(bool InNameTree, string Key)>(k => unchecked(KeyedHash.Of(k.Key) * 31 + (k.InNameTree ? 1 : 0))));
            foreach (NamedDestination entry in NamedDestinations(doc, catalog))
                if (!lookup.ContainsKey((entry.InNameTree, entry.Key))) lookup[(entry.InNameTree, entry.Key)] = entry.Value;
            return lookup;
        }

        /// <summary>
        /// The location a Link annotation targets, as a key two annotations share exactly when they target the
        /// same one, or <see langword="null"/> when it cannot be read: a destination in this document (its
        /// <c>/Dest</c>, or a GoTo action's <c>/SD</c> else <c>/D</c>, through <see cref="DestinationOf"/>) by the
        /// object its array names; a URI action by its URI; a GoToR action by its file and destination.
        /// </summary>
        private static string LinkTarget(PdfDocument doc, PdfDictionary annot,
            Dictionary<(bool InNameTree, string Key), PdfObject> named)
        {
            PdfObject destination = DestinationOf(doc, annot);
            if (destination != null)
            {
                string page = DestinationTarget(doc, destination, named);
                return page == null ? null : "D " + page;
            }

            var action = Resolve(doc, annot[PdfName.A]) as PdfDictionary;
            switch ((Resolve(doc, action?[PdfName.S]) as PdfName)?.Value)
            {
                case "URI":
                    return Resolve(doc, action[PdfName.URI]) is PdfString uri ? "URI " + Latin1Text.Decode(uri.RawBytes) : null;
                case "GoToR":
                {
                    PdfObject file = Resolve(doc, action[PdfName.F]);
                    string fileName = file is PdfString s ? s.GetText()
                        : file is PdfDictionary spec ? ((Resolve(doc, spec[PdfName.UF]) ?? Resolve(doc, spec[PdfName.F])) as PdfString)?.GetText()
                        : null;
                    string remote = RemoteDestination(doc, Resolve(doc, action[PdfName.D]));
                    return fileName == null || remote == null ? null : "R " + fileName + " " + remote;
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// The object an explicit destination names — a page, or a structure element for a structure
        /// destination — as its object number; a name or string followed through the named destinations first,
        /// and a <c>&lt;&lt;/D …&gt;&gt;</c> wrapper unwrapped. Null when it names none.
        /// </summary>
        private static string DestinationTarget(PdfDocument doc, PdfObject destination,
            Dictionary<(bool InNameTree, string Key), PdfObject> named)
        {
            if (destination is PdfName name)
                destination = named.TryGetValue((false, name.Value), out PdfObject byName) ? Resolve(doc, byName) : null;
            else if (destination is PdfString text)
                destination = named.TryGetValue((true, text.GetText()), out PdfObject byString) ? Resolve(doc, byString) : null;
            if (destination is PdfDictionary wrapper) destination = Resolve(doc, wrapper[PdfName.D]);
            if (!(destination is PdfArray array) || array.Count == 0) return null;
            return array[0] is PdfReference target && target.ReferenceObjectNumber > 0
                ? target.ReferenceObjectNumber.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>A remote destination as written: a name or string by its text, an array by the page number it opens.</summary>
        private static string RemoteDestination(PdfDocument doc, PdfObject destination)
        {
            if (destination is PdfName name) return "/" + name.Value;
            if (destination is PdfString text) return "(" + text.GetText() + ")";
            if (destination is PdfArray array && array.Count > 0 && Resolve(doc, array[0]) is PdfNumber page && page.IsInteger)
                return "page " + page.IntValue.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        // ----------------------------------------------------------------
        //  Clauses 8.9.2.3 and 8.10.3.3 — rich text textually equivalent to its plain text
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.3: "When both RC and Contents entries are present, they shall be textually
        /// equivalent" — for the markup annotations the clause lists (<see cref="IsMarkupAnnotationSubtypeRequiringAnnot"/>).
        /// Shares <c>UA2-8.9.2.3</c> with <see cref="CheckMarkupAnnotationsEnclosedInAnnot"/>.
        /// </summary>
        /// <remarks>
        /// Equivalent when the text the rich text shows (<see cref="RichText.ToPlain(string)"/>: the XFA rich text
        /// a reader displays, a paragraph's or a line break's boundary a space) is the plain text, both with their
        /// whitespace collapsed (<see cref="RichText.Collapsed"/>) — so a paragraph break written as a carriage
        /// return in <c>/Contents</c> is the same text. A rich text stream that will not decode is not judged, and
        /// a UA2-99 says how many.
        /// </remarks>
        private static void CheckMarkupRichTextMatchesContents(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int unjudged = 0;
            int found = 0;
            foreach (PageAnnotation entry in AnnotationsOf(doc))
            {
                PdfDictionary annot = entry.Dict;
                if (!IsMarkupAnnotationSubtypeRequiringAnnot((Resolve(doc, annot[PdfName.Subtype]) as PdfName)?.Value)) continue;
                PdfObject rc = Resolve(doc, annot[PdfName.RC]);
                PdfObject contents = Resolve(doc, annot[PdfName.Contents]);
                if (rc == null || contents == null) continue;

                bool? equivalent = TextuallyEquivalent(rc, contents);
                if (equivalent == null) { unjudged++; continue; }
                if (equivalent.Value) continue;

                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.9.2.3",
                    "A markup annotation's /RC and /Contents are not textually equivalent — the rich text shows " +
                    $"\"{Clip(RichText.ToPlain(rc, out _))}\" and /Contents says \"{Clip(RichText.Collapsed(RichText.TextOf(contents, out _)))}\"; " +
                    "when both are present they are (ISO 14289-2 §8.9.2.3)." +
                    (entry.ObjectNumber > 0 ? $" Object {entry.ObjectNumber}." : "")));
            }
            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.9.2.3",
                    $"…and {found - IndividualReportLimit} more, not listed individually."));
            if (unjudged > 0)
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    $"§8.9.2.3's textual equivalence was not judged for {unjudged} markup annotation(s) whose " +
                    "/RC or /Contents text stream would not decode."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.10.3.3's second half: a text field's <c>/RV</c> and <c>/V</c> "shall be textually
        /// equivalent" — read as <see cref="CheckMarkupRichTextMatchesContents"/> reads <c>/RC</c> against
        /// <c>/Contents</c>, over the dictionaries <see cref="CheckTextFieldRVRequiresV"/> judges: a widget whose
        /// own <c>/FT</c> is <c>/Tx</c>. Shares <c>UA2-8.10.3.3</c> with it.
        /// </summary>
        private static void CheckTextFieldRichValueMatchesValue(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int unjudged = 0;
            int found = 0;
            foreach (PageAnnotation entry in AnnotationsOf(doc))
            {
                PdfDictionary field = entry.Dict;
                if (!HasSubtype(doc, field, PdfAnnotationSubtypes.Widget)
                    || (Resolve(doc, field[PdfName.FT]) as PdfName)?.Value != "Tx") continue;
                PdfObject rv = Resolve(doc, field[PdfName.RV]);
                PdfObject value = Resolve(doc, field[PdfName.V]);
                if (rv == null || value == null) continue;

                bool? equivalent = TextuallyEquivalent(rv, value);
                if (equivalent == null) { unjudged++; continue; }
                if (equivalent.Value) continue;

                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.10.3.3",
                    "A text field's /RV and /V are not textually equivalent — the rich text shows " +
                    $"\"{Clip(RichText.ToPlain(rv, out _))}\" and /V is \"{Clip(RichText.Collapsed(RichText.TextOf(value, out _)))}\"; " +
                    "when /RV is present they are (ISO 14289-2 §8.10.3.3)." +
                    (entry.ObjectNumber > 0 ? $" Object {entry.ObjectNumber}." : "")));
            }
            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.10.3.3",
                    $"…and {found - IndividualReportLimit} more, not listed individually."));
            if (unjudged > 0)
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    $"§8.10.3.3's textual equivalence was not judged for {unjudged} text field(s) whose /RV or /V " +
                    "text stream would not decode."));
        }

        /// <summary>
        /// Whether a rich text string or stream shows the text a plain text string or stream holds, both
        /// collapsed; <see langword="null"/> when either is a stream that will not decode, false when either is
        /// neither a string nor a stream.
        /// </summary>
        private static bool? TextuallyEquivalent(PdfObject rich, PdfObject plain)
        {
            string shown = RichText.ToPlain(rich, out string richUnreadable);
            string text = RichText.TextOf(plain, out string plainUnreadable);
            if (richUnreadable != null || plainUnreadable != null) return null;
            if (shown == null || text == null) return false;
            return string.Equals(shown, RichText.Collapsed(text), StringComparison.Ordinal);
        }

        /// <summary>A text for a message: its first 60 characters, an ellipsis after a longer one.</summary>
        private static string Clip(string text)
            => text == null ? "" : text.Length <= 60 ? text : text.Substring(0, 60) + "…";

        // ----------------------------------------------------------------
        //  Clause 8.13.3 — a URI action with IsMap, an advisory
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.13.3: "When the IsMap entry in a URI action dictionary … has a value of true,
        /// equivalent functionality shall be provided elsewhere in the real content." Each annotation whose
        /// <c>/A</c>, or an action on its <c>/Next</c> chain, is a URI action with <c>/IsMap</c> true, through
        /// the reading PDF/UA-1's UA35 asks (<c>PdfDeepAccessibilityChecker.SendsPointerPosition</c>).
        /// </summary>
        /// <remarks>
        /// Whether the function is offered elsewhere is a person's to confirm, so the finding is an
        /// advisory (<see cref="PdfUa2StructureResult.Advisories"/>) — an audit's warning — and never makes the
        /// document non-conforming. A chain read to <see cref="PdfLimits.MaxActionChainDepth"/> and cut there
        /// is said in a UA2-99.
        /// </remarks>
        private static void CheckUriActionsWithIsMap(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            bool cut = false;
            int found = 0;
            foreach (PageAnnotation entry in AnnotationsOf(doc))
            {
                if (!PdfDeepAccessibilityChecker.SendsPointerPosition(doc, entry.Dict[PdfName.A], ref cut)) continue;
                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.13.3",
                    "An annotation's URI action has /IsMap true, so following it sends the pointer's position, " +
                    "which a keyboard or screen-reader user cannot give; equivalent functionality is to be " +
                    "provided elsewhere in the real content, which a person confirms (ISO 14289-2 §8.13.3)." +
                    (entry.ObjectNumber > 0 ? $" Object {entry.ObjectNumber}." : "")));
            }
            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.13.3",
                    $"…and {found - IndividualReportLimit} more, not listed individually."));
            if (cut)
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    "§8.13.3 stopped reading an annotation's action chain at the " +
                    PdfLimits.MaxActionChainDepth.ToString(CultureInfo.InvariantCulture) +
                    "-action ceiling, so a URI action with /IsMap further along it was not looked for."));
        }
    }
}
