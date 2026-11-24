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
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA14 — ParentTree coverage
        // ================================================================

        /// <summary>
        /// Verifies that the /ParentTree really maps content back to structure.
        ///
        /// <para>The previous implementation counted number-tree entries and compared the count to
        /// <c>doc.Pages.Count</c>. ParentTree keys index pages <i>and</i> annotations and every
        /// other object with a /StructParent, so that comparison both false-positived (a tagged
        /// one-page document with three annotations "covers 4 pages") and false-negatived (three
        /// annotations on page 1 of a four-page document make the count look right while three
        /// pages are uncovered). This version resolves the actual keys.</para>
        ///
        /// <para>A missing /ParentTree is an <b>Error</b>, not Info: without it no marked-content
        /// identifier can be mapped back to a structure element, which is the entire mechanism
        /// tagged PDF rests on, and as Info a document with a hollow structure tree would look
        /// conformant.</para>
        /// </summary>
        private static void CheckParentTreeCoverage(CheckContext ctx)
        {
            if (ctx.StructTree == null) return;

            PdfObject parentTreeObj = ctx.StructTree[PdfName.ParentTree];
            if (parentTreeObj == null)
            {
                ctx.Issues.Add(Issue(IssueSeverity.Error, "UA14",
                    "The structure tree has no /ParentTree, so no marked content on any page can be " +
                    "mapped back to a structure element. The structure tree describes content it cannot " +
                    "be connected to.",
                    "Add a /ParentTree number tree to the /StructTreeRoot: each page's /StructParents key " +
                    "must resolve to an array of the structure elements owning that page's MCIDs, and each " +
                    "annotation's /StructParent key to its owning structure element.",
                    IssueCategory.Tagging, WcagLevel.A)
                    .AtObject(ctx.StructTreeObjectNumber)
                    .MappedTo(null, "1.3.1"));
                return;
            }

            PdfDictionary parentTree = ResolveDictionary(ctx.Doc, parentTreeObj);
            if (parentTree == null)
            {
                ctx.Issues.Add(Issue(IssueSeverity.Error, "UA14",
                    "The /ParentTree entry of the structure tree does not resolve to a number tree " +
                    "dictionary, so no content can be mapped back to structure.",
                    "Make /ParentTree a number tree dictionary with /Nums (or /Kids) entries.",
                    IssueCategory.Tagging, WcagLevel.A)
                    .AtObject(ctx.StructTreeObjectNumber)
                    .MappedTo(null, "1.3.1"));
                return;
            }

            Dictionary<int, PdfObject> entries = GetParentTreeEntries(ctx);
            bool partialTree = ctx.ParentTreeError != null;
            if (partialTree)
            {
                ctx.Issues.Add(Issue(IssueSeverity.Info, "UA14",
                    "The /ParentTree could only be read partially (" + ctx.ParentTreeError + "); coverage results " +
                    "below may be incomplete.",
                    "Repair the number tree so it can be traversed in full.",
                    IssueCategory.Tagging)
                    .AtObject(ctx.StructTreeObjectNumber));
            }

            // "Absent from the map" and "absent from the file" are the same observation only when
            // the map is complete. Past a ceiling they are not, and a key the walk never reached is
            // no evidence at all — so the two *missing* verdicts drop to Info, exactly as
            // CheckContext.Truncated's doc comment promises. A well-formed 10 000-page report with
            // more /ParentTree keys than MaxNumberTreeEntries was otherwise declared non-conforming
            // (IsConforming => ErrorCount == 0) for a defect it does not have.
            //
            // The two *shape* verdicts stay Errors: badPageValue / badAnnotValue are facts about an
            // entry that WAS read, and a wrong shape is established whether or not the tail was
            // reached.
            IssueSeverity missingSeverity = partialTree ? IssueSeverity.Info : IssueSeverity.Error;
            string unreadSuffix = partialTree
                ? " The /ParentTree could not be read in full, so this may be an entry the walk " +
                  "never reached rather than one the document omits."
                : "";

            var missingPageKey  = new List<Occurrence>();
            var badPageValue    = new List<Occurrence>();
            var missingAnnotKey = new List<Occurrence>();
            var badAnnotValue   = new List<Occurrence>();

            foreach (PageInfo page in ctx.Pages)
            {
                if (page.Dict == null) continue;

                int? key = GetInteger(ctx, page.Dict[NameStructParents]);
                if (key.HasValue)
                {
                    // A page's key must resolve to an ARRAY of structure elements, indexed by MCID.
                    if (!entries.TryGetValue(key.Value, out PdfObject value))
                    {
                        var occurrence = Occurrence.ForPage(page);
                        occurrence.Detail = key.Value.ToString(CultureInfo.InvariantCulture);
                        missingPageKey.Add(occurrence);
                    }
                    else if (ResolveObject(ctx.Doc, value) is not PdfArray)
                    {
                        var occurrence = Occurrence.ForPage(page);
                        occurrence.Detail = key.Value.ToString(CultureInfo.InvariantCulture);
                        badPageValue.Add(occurrence);
                    }
                }

                foreach (AnnotationInfo annot in EnumerateAnnotations(ctx, page))
                {
                    int? annotKey = GetInteger(ctx, annot.Dict[NameStructParent]);
                    if (!annotKey.HasValue) continue;   // reported by UA23

                    // An object's key resolves to the single structure element that owns it.
                    if (!entries.TryGetValue(annotKey.Value, out PdfObject value))
                    {
                        var occurrence = Occurrence.ForAnnotation(annot);
                        occurrence.Detail = annotKey.Value.ToString(CultureInfo.InvariantCulture);
                        missingAnnotKey.Add(occurrence);
                    }
                    else if (ResolveObject(ctx.Doc, value) is not PdfDictionary)
                    {
                        var occurrence = Occurrence.ForAnnotation(annot);
                        occurrence.Detail = annotKey.Value.ToString(CultureInfo.InvariantCulture);
                        badAnnotValue.Add(occurrence);
                    }
                }
            }

            EmitOccurrences(ctx, missingSeverity, "UA14", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", missingPageKey,
                o => "Page declares /StructParents " + o.Detail + " but the /ParentTree has no entry for " +
                     "that key, so this page's marked content maps to no structure element." + unreadSuffix,
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " page(s) declare a /StructParents key that " +
                    "the /ParentTree does not contain (e.g. " + objects + "); their marked content maps to no " +
                    "structure element." + unreadSuffix,
                "Add a /Nums entry for each page's /StructParents key whose value is the array of structure " +
                "elements owning that page's MCIDs.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA14", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", badPageValue,
                o => "The /ParentTree entry for this page's /StructParents key " + o.Detail +
                     " is not an array. A page key must map to an array of structure elements indexed by MCID.",
                (count, objects) =>
                    "The /ParentTree entry for " + count.ToString(CultureInfo.InvariantCulture) +
                    " page(s) is not an array (e.g. " + objects + "); a page key must map to an array of " +
                    "structure elements indexed by MCID.",
                "Replace each such value with an array whose index i is the structure element owning MCID i.");

            EmitOccurrences(ctx, missingSeverity, "UA14", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", missingAnnotKey,
                o => "Annotation declares /StructParent " + o.Detail + " but the /ParentTree has no entry " +
                     "for that key, so the annotation maps to no structure element." + unreadSuffix,
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " annotation(s) declare a /StructParent key " +
                    "the /ParentTree does not contain (e.g. " + objects + ")." + unreadSuffix,
                "Add a /Nums entry for each annotation's /StructParent key whose value is the structure " +
                "element (usually a Link or Form element) that owns it.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA14", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", badAnnotValue,
                o => "The /ParentTree entry for this annotation's /StructParent key " + o.Detail +
                     " is not a structure element dictionary.",
                (count, objects) =>
                    "The /ParentTree entry for " + count.ToString(CultureInfo.InvariantCulture) +
                    " annotation(s) is not a structure element dictionary (e.g. " + objects + ").",
                "An object's /StructParent key must map to the single structure element that owns it, " +
                "not to an array.");

            // Everything above establishes that the pointers EXIST and have the right shape. The
            // relation they are supposed to encode is checked separately, below.
            CheckMarkedContentMapping(ctx, entries);
        }

        // ----------------------------------------------------------------
        //  UA14 — the mapping itself, not merely its shape
        // ----------------------------------------------------------------

        /// <summary>One MCID a structure element claims, and the page it claims it on.</summary>
        private readonly struct McidClaim
        {
            internal McidClaim(StructNode node, int pageIndex, int mcid)
            {
                Node = node;
                PageIndex = pageIndex;
                Mcid = mcid;
            }

            internal StructNode Node { get; }

            /// <summary>Zero-based page index the MCID is claimed on, or -1 when unknown.</summary>
            internal int PageIndex { get; }

            internal int Mcid { get; }
        }

        /// <summary>
        /// Checks the relation ISO 32000-1 §14.7.4.4 actually defines, in both directions.
        ///
        /// <para>
        /// <b>The hole this closes.</b> Everything else in UA14 asks only whether the pointers are
        /// well-formed: does the page's <c>/StructParents</c> key exist in <c>/Nums</c>, and is its
        /// value an array. Neither question looks at the array's <i>contents</i>, and neither
        /// UA16 nor UA30 looks at an MCID either — <c>ClassifyKid</c> accepts any
        /// <see cref="PdfNumber"/> in <c>/K</c> as "real content" without asking whether that
        /// marked-content sequence exists on the page. So a structure tree wired to nothing passed
        /// every check in this file: elements claiming MCIDs the page does not contain, an array
        /// whose slots name elements that disown them, and a report saying "no blocking issues".
        /// That is the hollow-tagging failure this whole feature exists to prevent, wearing a
        /// different disguise, and writing a syntactically valid integer must not be enough to
        /// reach it.
        /// </para>
        ///
        /// <para>
        /// <b>Three findings, all Errors, all page-scoped.</b>
        /// </para>
        /// <list type="number">
        ///   <item><description>An element names MCID <i>i</i> on a page whose content stream
        ///     contains no such marked-content sequence — the element describes content that does
        ///     not exist.</description></item>
        ///   <item><description>The page's <c>/ParentTree</c> array is shorter than <i>i</i>, or
        ///     its slot <i>i</i> names some other element — the mapping does not lead back, so a
        ///     reader that starts from the content cannot find the structure.</description></item>
        ///   <item><description>The page draws a marked-content sequence that <b>no</b> element
        ///     claims — that content belongs to nothing and is invisible to assistive technology,
        ///     however complete the tree looks.</description></item>
        /// </list>
        ///
        /// <para>
        /// <b>What is deliberately not judged</b>, because judging it would mean inventing
        /// findings: a page whose content could not be decoded or scanned — an encrypted document
        /// is exactly this case and is stated in the report by
        /// <see cref="ReportEncryptionCoverage"/>, any other unreadable page by
        /// <see cref="ReportContentCoverage"/> — a page whose Form XObjects could not all be
        /// read or that holds a named property list this checker could not resolve (both stated by
        /// <see cref="ReportPartialContentCoverage"/>), a page with
        /// no <c>/StructParents</c> or one whose key is missing from the number tree (reported
        /// above and by UA18), a claim through an <c>/MCR</c> with a <c>/Stm</c> naming a stream
        /// that was never tokenised, an element with no object number (nothing in an array can
        /// name it), and any document whose structure walk hit its node ceiling.
        /// </para>
        /// </summary>
        private static void CheckMarkedContentMapping(CheckContext ctx, Dictionary<int, PdfObject> entries)
        {
            IReadOnlyList<StructNode> nodes = ctx.Elements;
            if (nodes == null || nodes.Count == 0 || ctx.Pages.Count == 0) return;

            // A truncated walk turns "no element claims this MCID" into "the walk stopped before
            // it got there", so the whole relation becomes unverifiable rather than false.
            if (nodes.Count >= StructTreeWalker.DefaultMaxNodes)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.ParentTreeCoverage",
                    "the structure tree reached the node ceiling, so the MCID-to-ParentTree mapping " +
                    "could not be verified; it was not reported on, because a partial element list " +
                    "would accuse the document of orphaned content the walk simply never reached");

                ReportStructureWalkGap(ctx,
                    "the " + StructTreeWalker.DefaultMaxNodes.ToString(CultureInfo.InvariantCulture) +
                    "-node structure-tree ceiling, so the element list is partial",
                    "UA14's marked-content mapping — whether every MCID a structure element names " +
                    "really exists on its page, whether the /ParentTree leads back to that element, " +
                    "and whether any marked content is claimed by nothing — did not run.");
                return;
            }

            var pageByIndex      = new Dictionary<int, PageInfo>(ctx.Pages.Count);
            var arrayByPageIndex = new Dictionary<int, PdfArray>(ctx.Pages.Count);
            bool encryptionBlockedAPage = false;

            foreach (PageInfo page in ctx.Pages)
            {
                if (page.Dict == null || page.PageIndex < 0) continue;
                pageByIndex[page.PageIndex] = page;

                if (!page.ContentUsable)
                {
                    // On an encrypted document this is every page, and ReportEncryptionCoverage
                    // states it; otherwise ContentUnreadable records the page and the reason for
                    // ReportContentCoverage. Either way the report says the mapping was not
                    // verified here.
                    if (ctx.Encrypted) encryptionBlockedAPage = true;
                    else NoteContentCoverageGap(ctx, page, CheckNameUa14Mapping);
                    continue;
                }

                if (page.FormXObjectScanIncomplete || page.MarkedContentIdsIncomplete)
                {
                    // Skipped for the same reason as an unreadable page — a partial MCID set turns
                    // "no element claims this content" into "the scan never saw it" — and therefore
                    // named in the report for the same reason too.
                    NotePartialContentGap(ctx, page, CheckNameUa14Mapping, checkRan: false);
                    continue;
                }

                int? key = GetInteger(ctx, page.Dict[NameStructParents]);
                if (!key.HasValue) continue;                                        // UA18's finding
                if (!entries.TryGetValue(key.Value, out PdfObject value)) continue;  // reported above
                if (ResolveObject(ctx.Doc, value) is not PdfArray array) continue;   // reported above

                arrayByPageIndex[page.PageIndex] = array;
            }

            if (encryptionBlockedAPage)
                NoteEncryptionLimit(ctx,
                    "UA14's marked-content mapping (does every MCID a structure element names really " +
                    "exist on its page, and does the /ParentTree array lead back to that element?) was " +
                    "not attempted: it needs the page content streams, which are encrypted");

            if (arrayByPageIndex.Count == 0) return;

            var foreignStreamPages = new HashSet<int>();
            List<McidClaim> claims = CollectMcidClaims(ctx, foreignStreamPages);
            if (claims == null) return;   // edge ceiling; already diagnosed

            var claimedByPage    = new Dictionary<int, HashSet<int>>();
            var mcidNotOnPage    = new List<Occurrence>();
            var mcidNotMappedBack = new List<Occurrence>();

            foreach (McidClaim claim in claims)
            {
                if (claim.PageIndex < 0) continue;
                if (!arrayByPageIndex.TryGetValue(claim.PageIndex, out PdfArray array)) continue;

                PageInfo page = pageByIndex[claim.PageIndex];

                if (!claimedByPage.TryGetValue(claim.PageIndex, out HashSet<int> claimed))
                    claimedByPage[claim.PageIndex] = claimed = new HashSet<int>();
                claimed.Add(claim.Mcid);

                string where = "MCID " + claim.Mcid.ToString(CultureInfo.InvariantCulture) +
                               " on page " + page.PageNumber.ToString(CultureInfo.InvariantCulture);

                if (claim.Mcid < 0 || !page.MarkedContentIds.Contains(claim.Mcid))
                {
                    var occurrence = Occurrence.For(ctx, claim.Node);
                    occurrence.PageNumber = page.PageNumber;
                    occurrence.Detail = where;
                    mcidNotOnPage.Add(occurrence);
                    continue;
                }

                // An element with no object number cannot be named by an array entry at all, so
                // the mapping cannot be judged for it — that is a different defect, and inventing
                // this one on top of it would be wrong.
                if (claim.Node.ObjectNumber <= 0) continue;

                if (claim.Mcid >= array.Count || !ParentTreeSlotIdentifies(ctx, array[claim.Mcid], claim.Node))
                {
                    var occurrence = Occurrence.For(ctx, claim.Node);
                    occurrence.PageNumber = page.PageNumber;
                    occurrence.Detail = where + ", but /Nums entry index " +
                        claim.Mcid.ToString(CultureInfo.InvariantCulture) +
                        (claim.Mcid >= array.Count
                            ? " does not exist — the array holds only " +
                              array.Count.ToString(CultureInfo.InvariantCulture) + " entr" +
                              (array.Count == 1 ? "y" : "ies")
                            : " names a different element");
                    mcidNotMappedBack.Add(occurrence);
                }
            }

            var orphanedContent = new List<Occurrence>();

            foreach (KeyValuePair<int, PdfArray> entry in arrayByPageIndex)
            {
                PageInfo page = pageByIndex[entry.Key];

                // A form's MCIDs live in the form's own numbering space, addressed through an
                // /MCR /Stm, and they are pooled into MarkedContentIds so the first check stays
                // permissive. That pooling makes "which of these are the PAGE's?" unanswerable,
                // so the reverse direction simply is not asked of such a page.
                if (page.InvokesFormXObject) continue;
                if (foreignStreamPages.Contains(entry.Key)) continue;
                if (page.MarkedContentIds.Count == 0) continue;

                claimedByPage.TryGetValue(entry.Key, out HashSet<int> claimed);

                var unowned = new List<int>();
                foreach (int mcid in page.MarkedContentIds)
                    if (claimed == null || !claimed.Contains(mcid)) unowned.Add(mcid);

                if (unowned.Count == 0) continue;
                unowned.Sort();

                var occurrence = Occurrence.ForPage(page);
                occurrence.Detail = JoinCapped(
                    unowned.ConvertAll(m => m.ToString(CultureInfo.InvariantCulture)));
                orphanedContent.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA14", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", mcidNotOnPage,
                o => "This structure element claims " + o.Detail + ", but that page's content stream " +
                     "contains no such marked-content sequence. The element describes content that does " +
                     "not exist, so nothing is announced for it however well-formed the tree looks.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " structure element(s) claim a " +
                    "marked-content identifier their page does not contain (e.g. " + objects + "). Each " +
                    "describes content that does not exist.",
                "Point each element's /K at an MCID the page really draws — the integers used by the " +
                "/Tag <</MCID n>> BDC sequences in its content stream — or re-run auto-tagging on the " +
                "original document, which writes the content and the tree from one pass.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA14", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", mcidNotMappedBack,
                o => "This structure element claims " + o.Detail +
                     ". ISO 32000-1 §14.7.4.4 requires index i of a page's /ParentTree array to be the " +
                     "element that owns MCID i, so the content cannot be mapped back to this element.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " structure element(s) claim an MCID " +
                    "whose /ParentTree slot does not name them (e.g. " + objects + "); the content cannot " +
                    "be mapped back to the elements that describe it.",
                "Rebuild each page's /Nums entry so index i holds a reference to the element owning MCID i, " +
                "with a null for any index no element claims.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA14", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", orphanedContent,
                o => "This page draws marked content that no structure element claims (MCID " + o.Detail +
                     "). That content is tagged in the stream but belongs to nothing in the structure " +
                     "tree, so assistive technology never reaches it.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " page(s) draw marked content that no " +
                    "structure element claims (e.g. " + objects + "); that content belongs to nothing in " +
                    "the structure tree.",
                "Give each unclaimed MCID an owning structure element — add the integer to the right " +
                "element's /K and reference that element from the page's /ParentTree array — or mark the " +
                "content as /Artifact if it is decorative.");
        }

        /// <summary>
        /// Collects every (element, page, MCID) claim the structure tree makes, from bare
        /// <c>/K</c> integers and from <c>/MCR</c> dictionaries alike.
        ///
        /// <para>Returns <c>null</c> — meaning "do not report anything" — when the <c>/K</c> edge
        /// ceiling is hit, for the same reason <see cref="CheckHollowSubtrees"/> bails out there: a
        /// partial claim list would turn the page's real marked content into phantom orphans.</para>
        /// </summary>
        /// <param name="foreignStreamPages">
        /// Receives the page indices reached by an <c>/MCR</c> carrying <c>/Stm</c>. That MCID
        /// lives in a stream this checker never tokenised, so the page's own marked content can no
        /// longer be partitioned into "claimed" and "orphaned" and the reverse check skips it.
        /// </param>
        /// <param name="ctx">Per-check state: the document, the options and the findings list.</param>
        private static List<McidClaim> CollectMcidClaims(CheckContext ctx, HashSet<int> foreignStreamPages)
        {
            var claims = new List<McidClaim>();
            IReadOnlyList<StructNode> nodes = ctx.Elements;
            int edgeBudget = StructTreeWalker.DefaultMaxEdges;

            for (int i = 0; i < nodes.Count; i++)
            {
                StructNode node = nodes[i];

                // The /StructTreeRoot carries no /S and owns no content of its own.
                if (node.Role == null) continue;

                IReadOnlyList<PdfObject> kids = node.RawKids;
                if (kids == null) continue;

                for (int k = 0; k < kids.Count; k++)
                {
                    if (edgeBudget <= 0)
                    {
                        AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.ParentTreeCoverage",
                            "the structure tree exceeded the " +
                            StructTreeWalker.DefaultMaxEdges.ToString(CultureInfo.InvariantCulture) +
                            "-edge ceiling while collecting the MCIDs its elements claim; the " +
                            "MCID-to-ParentTree mapping was not reported on, because a partial claim " +
                            "list would report real content as orphaned");

                        ReportStructureWalkGap(ctx,
                            "the " + StructTreeWalker.DefaultMaxEdges.ToString(CultureInfo.InvariantCulture) +
                            "-edge structure-tree ceiling while collecting the MCIDs its elements claim",
                            "UA14's marked-content mapping — whether every MCID a structure element " +
                            "names really exists on its page, whether the /ParentTree leads back to " +
                            "that element, and whether any marked content is claimed by nothing — " +
                            "did not run.");
                        return null;
                    }
                    edgeBudget--;

                    PdfObject kid = kids[k];
                    if (kid == null) continue;

                    // A bare integer names an MCID in the content stream of the element's own /Pg.
                    if (kid is PdfNumber bare)
                    {
                        claims.Add(new McidClaim(node, node.PageIndex, bare.IntValue));
                        continue;
                    }

                    // ResolveKidDictionary returns null for a reference to a collected structure
                    // element, so a child element costs one set probe here rather than a resolve.
                    PdfDictionary dict = ResolveKidDictionary(ctx, kid);
                    if (dict == null) continue;

                    // /S is Required on a structure element (Table 323); only a leaf /MCR names an
                    // MCID. An /OBJR names an annotation and has no MCID at all.
                    if (dict[PdfName.S] != null) continue;

                    int? mcid = GetInteger(ctx, dict[PdfName.MCID]);
                    if (!mcid.HasValue) continue;

                    int pageIndex = ResolveMcrPageIndex(ctx, dict, node);

                    if (dict[NameStm] != null)
                    {
                        if (pageIndex >= 0) foreignStreamPages.Add(pageIndex);
                        continue;
                    }

                    claims.Add(new McidClaim(node, pageIndex, mcid.Value));
                }
            }

            return claims;
        }

        /// <summary>
        /// The page an <c>/MCR</c> names: its own <c>/Pg</c> when it has one, otherwise the page
        /// inherited by the owning element (ISO 32000-1 Table 324). Returns -1 when the
        /// <c>/MCR</c> carries a <c>/Pg</c> that does not resolve to a page of this document —
        /// falling back to the element's page there would attribute the claim to the wrong page.
        /// </summary>
        private static int ResolveMcrPageIndex(CheckContext ctx, PdfDictionary mcr, StructNode owner)
        {
            PdfObject pg = mcr[PdfName.Pg];
            if (pg == null) return owner.PageIndex;
            if (ctx.Index == null) return -1;

            if (pg is PdfReference pageRef) return ctx.Index.GetPageIndex(pageRef.ReferenceObjectNumber);

            if (ResolveObject(ctx.Doc, pg) is PdfDictionary pageDict)
            {
                int number = ctx.Index.GetObjectNumber(pageDict);
                if (number > 0) return ctx.Index.GetPageIndex(number);
            }

            return -1;
        }

        /// <summary>
        /// Whether one <c>/ParentTree</c> array slot names <paramref name="node"/>. Callers must
        /// already have established that <paramref name="node"/> has an object number, since an
        /// array entry can name nothing else.
        /// </summary>
        private static bool ParentTreeSlotIdentifies(CheckContext ctx, PdfObject slot, StructNode node)
        {
            if (slot == null || slot is PdfNull) return false;

            if (slot is PdfReference reference)
                return reference.ReferenceObjectNumber == node.ObjectNumber;

            PdfDictionary dict = ResolveObject(ctx.Doc, slot) as PdfDictionary ?? slot as PdfDictionary;
            if (dict == null) return false;
            if (ReferenceEquals(dict, node.Dict)) return true;

            int number = ctx.Index?.GetObjectNumber(dict) ?? -1;
            return number > 0 && number == node.ObjectNumber;
        }

        /// <summary>
        /// Reads the /ParentTree once per <see cref="Check(Stream)"/> call and hands the same map
        /// to every check that needs it. Returns an empty map (never null) when the document has
        /// no usable ParentTree, so callers need no null handling.
        /// </summary>
        private static Dictionary<int, PdfObject> GetParentTreeEntries(CheckContext ctx)
        {
            if (ctx.ParentTreeRead) return ctx.ParentTreeEntries;
            ctx.ParentTreeRead = true;

            PdfDictionary tree = ctx.StructTree == null
                ? null
                : ResolveDictionary(ctx.Doc, ctx.StructTree[PdfName.ParentTree]);

            ctx.ParentTreeEntries = tree == null
                ? new Dictionary<int, PdfObject>()
                : ReadNumberTree(ctx, tree, out ctx.ParentTreeError);

            return ctx.ParentTreeEntries;
        }

        /// <summary>
        /// Reads a PDF number tree into a flat key → value map, guarding against cycles, runaway
        /// depth, hostile entry counts and — see <see cref="MaxNumberTreeSlots"/> — a shared or
        /// enormous <c>/Kids</c> array, which none of the other three bound.
        /// <paramref name="error"/> describes any truncation.
        /// </summary>
        private static Dictionary<int, PdfObject> ReadNumberTree(CheckContext ctx, PdfDictionary root, out string error)
        {
            // Core.Document.PdfNumberTree — the §7.9.7 walk CLAUDE.md names as the one
            // implementation. This carried its own: an explicit stack, its own reference-identity
            // visited set, its own depth cap and its own slot budget, under a summary comparing its
            // guards against "the other three". That comparison was the duplication report.
            //
            // The two things that kept it separate are both preserved rather than surrendered. The
            // LIMITS stay ours — a checker reading untrusted documents holds tighter caps than a
            // reader does, which is the distinction PdfLimits' header draws — and they are passed
            // in. And the REASON is still named, because it goes into a user-facing finding; the
            // shared walk answered only "incomplete", which is precisely why a second copy existed.
            var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = MaxNumberTreeEntries;

            PdfNumberTree.TryFlatten(root, ctx.Doc, entries, ref budget,
                MaxNumberTreeDepth, MaxNumberTreeSlots, out PdfTreeTruncation truncation);

            error = truncation switch
            {
                PdfTreeTruncation.Depth =>
                    "the tree is deeper than " +
                    MaxNumberTreeDepth.ToString(CultureInfo.InvariantCulture) + " levels",
                PdfTreeTruncation.Entries =>
                    "more than " +
                    MaxNumberTreeEntries.ToString(CultureInfo.InvariantCulture) + " entries",
                PdfTreeTruncation.KidSlots => TruncatedMessage,
                PdfTreeTruncation.UnreadableNode => "a node of the tree could not be read",
                _ => null,
            };

            var result = new Dictionary<int, PdfObject>();
            foreach (var entry in entries)
            {
                // Keys are direct integers per PDF 32000-1 §7.9.7; a non-integer key is malformed
                // and would silently shadow a real entry, so it is skipped. The walk hands keys
                // back unresolved, so this resolves before testing — §7.3.10 permits an indirect
                // value anywhere, and a raw read would drop a legal entry as malformed.
                if (ResolveObject(ctx.Doc, entry.Key) is not PdfNumber key) continue;
                result[key.IntValue] = entry.Value;
            }

            return result;
        }

        /// <summary>
        /// What a number-tree walk that spent its slot budget reports, naming the cause so
        /// <see cref="CheckParentTreeCoverage"/> downgrades everything that rests on the partial map
        /// to its "read partially" Info issue instead of accusing the document of missing coverage
        /// the walk simply never reached.
        /// </summary>
        /// <remarks>
        /// A string rather than a method: the walk is <c>Core.Document.PdfNumberTree</c>, which
        /// reports <b>which</b> limit it reached, so the
        /// caller selects the message instead of the traversal returning through a helper.
        /// </remarks>
        private static readonly string TruncatedMessage =
            "the number tree exceeded the " +
            MaxNumberTreeSlots.ToString(CultureInfo.InvariantCulture) +
            "-slot ceiling, so the walk was stopped; its /Kids arrays are shared between " +
            "nodes or enormous";
    }
}
