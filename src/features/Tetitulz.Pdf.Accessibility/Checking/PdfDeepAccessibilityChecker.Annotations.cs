// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    /// <summary>
    /// Extended PDF/UA-1 accessibility checker that supplements the base <c>PdfUaChecker</c>
    /// (which owns UA00–UA09) with deeper structural, content, font and annotation checks.
    ///
    /// <list type="bullet">
    ///   <item>UA06 — The security handler of an <i>encrypted</i> document must permit content
    ///     extraction for accessibility (ISO 32000-1 Table 22, /P bit 10). Encryption itself is
    ///     not a PDF/UA-1 failure.</item>
    ///   <item>UA10 — Heading hierarchy, for both the numbered (H1…H6) and the "strong"
    ///     (&lt;H&gt; nested in &lt;Sect&gt;) PDF/UA models, including the mixed-model failure.</item>
    ///   <item>UA11 — List structure: L → LI → Lbl + LBody, in that order (PDF/UA-1 7.6).</item>
    ///   <item>UA12 — Table structure: Table &gt; TR &gt; TD/TH nesting, TH addressability
    ///     (/Scope or /Headers), and table <i>regularity</i> once /ColSpan and /RowSpan are
    ///     resolved (Matterhorn 15-003).</item>
    ///   <item>UA13 — Role mapping: every element's /S must be standard or resolve — through
    ///     role-map chains — to a standard type, and standard types must not be remapped.</item>
    ///   <item>UA14 — ParentTree coverage <i>and the mapping it encodes</i>: every page's
    ///     /StructParents and every annotation's /StructParent must be present in the ParentTree
    ///     and resolve to the right kind of object — and, beyond the shape of those pointers,
    ///     every MCID a structure element names must really occur on its page, the page's
    ///     /ParentTree array must name that element at that index, and every marked-content
    ///     sequence the page draws must be claimed by some element
    ///     (see <see cref="CheckMarkedContentMapping"/>).</item>
    ///   <item>UA15 — Figure alt-text quality: empty or placeholder /Alt.</item>
    ///   <item>UA16 — <i>Leaf</i> structure elements that reference no content at all.</item>
    ///   <item>UA17 — Pages that draw text belonging to no marked-content sequence.</item>
    ///   <item>UA18 — Pages that carry structural content but no /StructParents, in a document
    ///     that claims to be tagged.</item>
    ///   <item>UA19 — /MarkInfo /Suspects true.</item>
    ///   <item>UA20 — Text that can produce no Unicode (no /ToUnicode, no usable encoding).</item>
    ///   <item>UA21 — Fonts that are not embedded.</item>
    ///   <item>UA22 — Link annotations with no alternate description.</item>
    ///   <item>UA23 — Annotations that are not reachable from the structure tree.</item>
    ///   <item>UA24 — Image-only (likely scanned) pages.</item>
    ///   <item>UA30 — Structure <i>subtrees</i> whose every element, transitively, reaches no
    ///     content: the aggregate finding UA16 cannot make because UA16 is per-leaf.</item>
    ///   <item>UA99 — A check could not be completed; its findings are missing (Info).</item>
    /// </list>
    ///
    /// <para><b>Why UA16–UA19 exist.</b> Before they were added the audit could not tell a
    /// properly tagged document from one whose structure tree points at nothing: a file with a
    /// handful of empty structure elements, no MCIDs and no ParentTree produced a single
    /// Info-level remark and therefore looked very nearly conformant. Those four checks, plus
    /// raising the missing-/ParentTree finding from Info to Error, are what make hollow tagging
    /// fail the audit.</para>
    ///
    /// <para><b>Why UA30 exists on top of them.</b> UA16 asks a purely <i>local</i> question — is
    /// <i>this</i> element a leaf that points at nothing — so a tree in which every element has an
    /// element child slips past it even when the tree as a whole reaches zero content. That shape
    /// is the original hollow-tagging defect wearing a disguise, and only UA17 caught it, by
    /// accident, and only when a page happened to draw unmarked text. UA30 asks the
    /// <i>transitive</i> question instead and reports the highest element of each hollow subtree,
    /// so a remediator gets one actionable entry per broken branch rather than one per element.</para>
    ///
    /// <para><b>Reporting policy.</b> Up to <see cref="IndividualReportLimit"/> occurrences of a
    /// finding are reported one issue each, fully located (page, object number, structure path).
    /// Beyond that they collapse into a single issue carrying
    /// <see cref="AccessibilityIssue.InstanceCount"/> and the first few object numbers in its
    /// message, so a pathological document cannot flood the report while
    /// <see cref="AccessibilityAuditReport.TotalInstanceCount"/> still reflects reality.</para>
    ///
    /// <para><b>Robustness.</b> Every individual check runs inside
    /// <see cref="RunCheck"/>: a failure in one can never take down the audit. It is surfaced as
    /// a UA99 Info issue and through <see cref="AccessibilityDiagnostics"/> — never swallowed.</para>
    ///
    /// <para><b>Encrypted documents are audited, not refused.</b> PDF/UA-1 permits encryption as
    /// long as the security handler grants accessibility extraction, and dictionary structure —
    /// element roles, /K linkage, the /ParentTree, /StructParents, table geometry — is never
    /// ciphertext, so it remains fully checkable. One UA06 Error and a stop would fail a conformant
    /// file, give a non-conformant one half an audit, and contradict the sibling
    /// <c>PdfUaChecker</c>, which reads /P and keeps auditing, in one combined report. The two
    /// halves apply the same rule (see <see cref="CheckEncryption"/>) and the audit continues either way.
    /// What genuinely cannot be read — text and content streams — is named once, exactly, by
    /// <see cref="ReportEncryptionCoverage"/>; no check is silently omitted.</para>
    ///
    /// <para><b>Nor is a page whose content is merely broken.</b> The same rule applies with no
    /// encryption in sight: a stray delimiter, a PostScript calculator brace or a corrupt Flate
    /// payload leaves a page unscannable, and the four checks that need decoded content
    /// (UA14's marked-content mapping, UA17, UA18, UA24) must then skip it. Each such skip is
    /// recorded and named per page by <see cref="ReportContentCoverage"/>, so a report can never
    /// again be byte-identical to one in which those checks ran and found nothing.</para>
    ///
    /// <para><b>Nor is a page that was only half-read.</b> A page whose own <c>/Contents</c> decodes
    /// perfectly can still invoke a Form XObject that will not resolve or decode, or name a property
    /// list that is not in <c>/Properties</c>. Its counts are then lower bounds while
    /// <c>ContentUsable</c> is still true, so <see cref="ReportContentCoverage"/> — which is gated on
    /// that flag — says nothing about it. <see cref="ReportPartialContentCoverage"/> covers exactly
    /// that gap, for the same reason and at the same severity.</para>
    /// </summary>
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA22 / UA23 — Annotations
        // ================================================================

        private sealed class AnnotationInfo
        {
            internal PdfDictionary Dict;
            internal int ObjectNumber = -1;
            internal int PageNumber;
            internal string Subtype;
        }

        private static void CheckAnnotations(CheckContext ctx)
        {
            var linksWithoutDescription = new List<Occurrence>();
            var withoutStructParent     = new List<Occurrence>();
            var withoutObjr             = new List<Occurrence>();
            var misnested               = new List<Occurrence>();

            Dictionary<int, PdfObject> parentTree = GetParentTreeEntries(ctx);

            foreach (PageInfo page in ctx.Pages)
            {
                foreach (AnnotationInfo annot in EnumerateAnnotations(ctx, page))
                {
                    if (annot.Subtype == PdfAnnotationSubtypes.Link && !HasLinkDescription(ctx, annot))
                        linksWithoutDescription.Add(Occurrence.ForAnnotation(annot));

                    if (!ctx.ClaimsTagged) continue;

                    if (annot.Dict[NameStructParent] == null)
                    {
                        var occurrence = Occurrence.ForAnnotation(annot);
                        occurrence.Detail = annot.Subtype ?? "(no /Subtype)";
                        withoutStructParent.Add(occurrence);
                        continue;
                    }

                    // An annotation stored as a direct dictionary has no object number, so no
                    // /OBJR could name it either — do not accuse it of something unprovable.
                    if (annot.ObjectNumber <= 0) continue;
                    if (ctx.ObjrOwners.TryGetValue(annot.ObjectNumber, out StructNode owner))
                    {
                        // ISO 14289-1 7.18.4: a widget is nested in a Form element (ISO 32000-1 Table
                        // 340); 7.18.5: a link in a Link element (14.8.4.4.2) — by what the owner means,
                        // its role resolved through /RoleMap.
                        string wanted = annot.Subtype == PdfAnnotationSubtypes.Widget ? StructureTypes.Form
                            : annot.Subtype == PdfAnnotationSubtypes.Link ? StructureTypes.Link
                            : null;
                        string meaning = RoleOf(ctx, owner);
                        if (wanted != null && meaning != wanted)
                        {
                            var wrong = Occurrence.ForAnnotation(annot);
                            wrong.Detail = annot.Subtype + " annotation is nested in " +
                                (meaning == null ? "the structure tree root" : "a <" + meaning + "> element") +
                                ", not in a <" + wanted + "> element";
                            misnested.Add(wrong);
                        }
                        continue;
                    }

                    var missing = Occurrence.ForAnnotation(annot);
                    missing.Detail = annot.Subtype ?? "(no /Subtype)";
                    withoutObjr.Add(missing);
                }
            }

            // Matterhorn 28-005 is the alternate-description checkpoint for a link annotation;
            // 28-004 is the <Link>-nesting rule and does not apply here.
            EmitOccurrences(ctx, IssueSeverity.Error, "UA22", IssueCategory.Annotations, WcagLevel.A,
                "28-005", "2.4.4", linksWithoutDescription,
                o => "Link annotation has no /Contents string, which is where PDF/UA requires a link's " +
                     "alternate description to live (ISO 14289-1 7.18.5). A screen reader can announce only " +
                     "the link's visible text, which is often \"here\" or a bare URL.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " Link annotation(s) have no /Contents " +
                    "alternate description (e.g. " + objects + ").",
                "Set /Contents on each link annotation to text describing where the link goes. An /Alt on " +
                "the enclosing Link structure element is a different entry and does not satisfy this rule.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA23", IssueCategory.Annotations, WcagLevel.A,
                null, "1.3.1", withoutStructParent,
                o => "Annotation of subtype " + o.Detail + " has no /StructParent, so it cannot be located " +
                     "in the structure tree and will not be announced in reading order.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " annotation(s) have no /StructParent " +
                    "(e.g. " + objects + ") and cannot be located in the structure tree.",
                "Give every visible annotation a /StructParent, add the matching /ParentTree entry, and " +
                "reference the annotation from the structure tree with an /OBJR.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA23", IssueCategory.Annotations, WcagLevel.A,
                null, "1.3.1", withoutObjr,
                o => "Annotation of subtype " + o.Detail + " is not referenced by any /OBJR in the structure " +
                     "tree, so it has no position in the document's reading order.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " annotation(s) are not referenced by any " +
                    "/OBJR in the structure tree (e.g. " + objects + ") and have no position in reading order.",
                "Add an /OBJR kid (<< /Type /OBJR /Obj <annotation ref> /Pg <page ref> >>) to the structure " +
                "element that should own each annotation — a Link element for links, a Form element for widgets.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA32", IssueCategory.Annotations, WcagLevel.A,
                null, "1.3.1", misnested,
                o => o.Detail + " (ISO 14289-1 7.18.4, 7.18.5).",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " widget or link annotation(s) are nested in " +
                    "a structure element other than Form or Link (e.g. " + objects + ").",
                "Move each widget's /OBJR into a Form element, and each link's into a Link element.");
        }

        /// <summary>
        /// Enumerates the annotations on a page that PDF/UA cares about.
        ///
        /// <para>Excluded, because none of them is content a reader navigates to:</para>
        /// <list type="bullet">
        ///   <item><description><c>/Popup</c> — the pop-up of another annotation, never
        ///     independently navigable.</description></item>
        ///   <item><description><c>/PrinterMark</c> — ISO 14289-1 treats printer's marks as
        ///     artifacts and exempts them from structure-tree inclusion and from the
        ///     <c>/Contents</c> description requirement, exactly as it does for <c>/Link</c> and
        ///     <c>/Widget</c>.</description></item>
        ///   <item><description>Annotations flagged Hidden (bit 2) or NoView (bit 6), which are
        ///     presented to nobody.</description></item>
        /// </list>
        /// <para>That scope is <c>Plugins.Conformance.PdfUaAnnotationScope</c>, which UA27 and the
        /// auto-tagger read too — one predicate, not copies "that must stay identical".</para>
        /// <para>A page's list is read once and kept for every check that asks. The entries every
        /// page names are read together under <see cref="PdfLimits.MaxAnnotationsPerDocument"/>:
        /// pages may name one <c>/Annots</c> array, so what they name between them is pages ×
        /// entries. Past the ceiling the rest are not read, and a UA99 notice says from which page.</para>
        /// </summary>
        private static List<AnnotationInfo> EnumerateAnnotations(CheckContext ctx, PageInfo page)
        {
            if (page.PresentedAnnotations != null) return page.PresentedAnnotations;
            var presented = page.PresentedAnnotations = new List<AnnotationInfo>();

            PdfArray annots = page.Annots;
            if (annots == null) return presented;

            for (int i = 0; i < annots.Count; i++)
            {
                if (ctx.AnnotationEntriesRead >= PdfLimits.MaxAnnotationsPerDocument)
                {
                    ReportAnnotationCeiling(ctx, page);
                    break;
                }
                ctx.AnnotationEntriesRead++;

                PdfObject raw = annots[i];
                PdfDictionary dict = ResolveDictionary(ctx.Doc, raw);
                if (dict == null) continue;

                string subtype = (ResolveObject(ctx.Doc, dict[PdfName.Subtype]) as PdfName)?.Value;
                int? flags = PdfAnnotationFlags.FromNumber(ResolveObject(ctx.Doc, dict[PdfName.F]));
                PdfArray cropBox = page.PageIndex >= 0 && page.PageIndex < ctx.Doc.Pages.Count
                    ? ctx.Doc.Pages[page.PageIndex].CropBox
                    : null;
                if (!Plugins.Conformance.PdfUaAnnotationScope.IsPresentedToReader(subtype, flags,
                        ResolveObject(ctx.Doc, dict[PdfName.Rect]) as PdfArray, cropBox, ctx.Doc.ResolveObject)) continue;

                presented.Add(new AnnotationInfo
                {
                    Dict = dict,
                    ObjectNumber = GetObjectNumber(ctx, raw, dict),
                    PageNumber = page.PageNumber,
                    Subtype = subtype,
                });
            }
            return presented;
        }

        /// <summary>
        /// The UA99 notice for <see cref="PdfLimits.MaxAnnotationsPerDocument"/>, written once, at
        /// the page where reading stopped. Info, as every "not examined" notice is.
        /// </summary>
        private static void ReportAnnotationCeiling(CheckContext ctx, PageInfo page)
        {
            if (ctx.AnnotationCeilingReached) return;
            ctx.AnnotationCeilingReached = true;
            ctx.Issues.Add(Issue(IssueSeverity.Info, "UA99",
                "The audit stopped reading annotations at the " +
                PdfLimits.MaxAnnotationsPerDocument.ToString(CultureInfo.InvariantCulture) +
                "-entry document ceiling, from page " + page.PageNumber.ToString(CultureInfo.InvariantCulture) +
                " on, so UA22, UA23, UA32 and the parent-tree checks did not examine the annotations " +
                "after it. Their findings are missing from this report: read a clean result there as " +
                "\"not examined\", not as \"nothing wrong\".",
                "Give each page its own /Annots array of the annotations it shows — the ceiling is " +
                "reached by pages that all name one shared array, which no ordinary document does — " +
                "and re-run the audit.",
                IssueCategory.Annotations));
        }

        /// <summary>
        /// What the report says when UA22's emptiness half could not be judged. A constant rather
        /// than a literal at the call site because <see cref="NoteEncryptionLimit"/> de-duplicates
        /// by message text, and a per-annotation spelling would list the same weakening once per
        /// link.
        /// </summary>
        private const string Ua22EncryptionLimit =
            "UA22's emptiness rule was weakened: a link's alternate description lives in the " +
            "annotation's /Contents string, which is encrypted, so a /Contents that is present " +
            "was accepted without checking whether it says anything. An empty description " +
            "encrypts to a non-empty byte string under AES, so judging it here would make the " +
            "verdict depend on the cipher rather than on the document";

        /// <summary>
        /// Whether a Link annotation carries the alternate description PDF/UA requires.
        ///
        /// <para>
        /// <b>Only <c>/Contents</c> on the annotation counts.</b> ISO 14289-1 §7.18.5 places the
        /// alternate description of a link in the annotation's own <c>/Contents</c>; an <c>/Alt</c>
        /// on the enclosing <c>&lt;Link&gt;</c> structure element is a different entry serving a
        /// different purpose, and accepting it here would pass files that independent checkers fail.
        /// The remediation text names <c>/Contents</c> for the same reason.
        /// </para>
        ///
        /// <para>
        /// <b>On an encrypted document only the <i>presence</i> of the key is judged</b>, and the
        /// weakening is stated in the report. Whether the key exists is a dictionary fact and stays
        /// fully checkable; whether its value is empty is not, because a <c>PdfString</c> read out
        /// of an encrypted document is ciphertext. The two common ciphers disagree about what that
        /// looks like: an empty string encrypts to 16–32 bytes of IV and ciphertext under AES, so
        /// <c>GetText()</c> returns non-whitespace and the emptiness test silently passes, while
        /// under RC4 an empty string stays empty and the same file reports UA22. A verdict that
        /// depends on which cipher the producer chose is not a verdict about the document, and this
        /// class already applies exactly that rule to the identical situation for UA15 (routed
        /// through <see cref="RunContentCheck"/>) and for UA12's <c>/ID</c> matching (see
        /// <see cref="IsHeaderAddressable"/>).
        /// </para>
        /// </summary>
        private static bool HasLinkDescription(CheckContext ctx, AnnotationInfo annot)
        {
            PdfObject contents = annot.Dict[PdfName.Contents];
            if (contents is PdfReference) contents = ResolveObject(ctx.Doc, contents);

            // An absent /Contents — or one that is not a string at all — is a dictionary fact, and
            // encryption does not touch it. UA22 still reports it.
            if (contents is not PdfString s) return false;

            if (ctx.Encrypted)
            {
                NoteEncryptionLimit(ctx, Ua22EncryptionLimit);
                return true;
            }

            return !string.IsNullOrWhiteSpace(s.GetText());
        }
    }
}
