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
using Tetitulz.Pdf.Core.Text;
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
        //  Page collection (shared by UA17, UA18, UA20–UA24)
        // ================================================================

        private static void CollectPages(CheckContext ctx)
        {
            // doc.Pages is empty for a document whose catalog the parser did not load, which is
            // every encrypted document (PdfDocument.Load). Walking the tree by hand in that case
            // is what keeps UA14, UA21, UA22 and UA23 — all of which are page-scoped and all of
            // which read only dictionary structure — from silently seeing a zero-page document.
            if (ctx.Doc.Catalog != null) CollectPagesFromParser(ctx);
            else CollectPagesFromPageTree(ctx);

            for (int i = 0; i < ctx.Pages.Count; i++) LoadPageContent(ctx, ctx.Pages[i]);
        }

        private static void CollectPagesFromParser(CheckContext ctx)
        {
            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = ctx.Doc.Pages;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                    "the page tree could not be read; no page-scoped check can run", ex);
                throw;
            }
            if (pages == null) return;

            for (int i = 0; i < pages.Count; i++)
            {
                var info = new PageInfo { PageIndex = i, PageNumber = i + 1 };
                ctx.Pages.Add(info);

                try
                {
                    info.Dict = pages[i].Dictionary;
                    info.Resources = pages[i].Resources;
                    info.Annots = pages[i].GetAnnotations();
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                        "page " + info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                        " could not be read; its page-scoped checks were skipped", ex);
                    info.ContentError = ex.GetType().Name + ": " + ex.Message;
                    continue;
                }

                info.ObjectNumber = ctx.Index?.GetPageObjectNumber(i) ?? -1;
            }
        }

        /// <summary>
        /// Walks <c>/Root → /Pages</c> by hand, for a document whose catalog the parser
        /// deliberately did not load — in practice, an encrypted one. Attribute inheritance is
        /// limited to <c>/Resources</c>, the only inherited entry any check here reads. Depth- and
        /// node-capped and cycle-guarded, exactly like the parser's own traversal, because this one
        /// runs over a page tree nothing has validated.
        /// </summary>
        private static void CollectPagesFromPageTree(CheckContext ctx)
        {
            PdfDictionary root = ResolveDictionary(ctx.Doc, ctx.Catalog[PdfName.Pages]);
            if (root == null)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                    "the catalog's /Pages entry does not resolve to a dictionary; no page-scoped " +
                    "check can run");
                return;
            }

            int budget = MaxPageTreeNodes;
            var visited = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);
            CollectPagesFromNode(ctx, root, null, visited, ref budget, 0);
        }

        private static void CollectPagesFromNode(
            CheckContext ctx,
            PdfDictionary node,
            PdfDictionary inheritedResources,
            HashSet<PdfObject> visited,
            ref int budget,
            int depth)
        {
            if (node == null) return;

            if (depth > MaxPageTreeDepth)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                    "the page tree is deeper than " + MaxPageTreeDepth.ToString(CultureInfo.InvariantCulture) +
                    " levels; the pages below that point were not collected");
                return;
            }

            // Exhausted AND already reported by the loop below (the -1 sentinel), so ancestors
            // unwinding through this point neither continue nor repeat the diagnostic. Note the
            // strict test: a node reached with exactly 0 left is still collected — a leaf costs
            // nothing — and only its children charge, which is what reports the truncation.
            if (budget < 0) return;

            if (!visited.Add(node)) return;   // a /Kids cycle

            PdfDictionary resources = ResolveDictionary(ctx.Doc, node[PdfName.Resources]) ?? inheritedResources;
            var kids = ResolveObject(ctx.Doc, node[PdfName.Kids]) as PdfArray;
            string type = (ResolveObject(ctx.Doc, node[PdfName.Type]) as PdfName)?.Value;

            // A node with no /Kids is a leaf when its /Type does not say otherwise: a page tree
            // whose leaves forgot /Type /Page is common enough that the parser tolerates it too.
            //
            // Not "whatever its /Type claims": a node declaring /Type /Pages with no /Kids is an
            // EMPTIED BRANCH — what a page-removal tool leaves behind — not a leaf. Counting it as a
            // page would shift every later page index and run UA14/UA21/UA22/UA23 against a
            // non-page; Core's walk and PdfUaChecker's apply the same rule.
            // One rule, in Core.Document.PdfPageTreeShape. This copy omitted Core's second clause —
            // that an untyped, childless node must also be page-SHAPED — so a node with neither
            // /Contents nor its own /MediaBox counted as a page, and UA14/UA21/UA22/UA23 then ran
            // against a non-page: the same failure the comment above describes, one case further out.
            if (Core.Document.PdfPageTreeShape.IsLeafPage(
                    ResolveObject(ctx.Doc, node[PdfName.Type]) as PdfName, kids,
                    hasContents: node[PdfName.Contents] != null,
                    hasMediaBox: ResolveObject(ctx.Doc, node[PdfName.MediaBox]) is PdfArray))
            {
                ctx.Pages.Add(new PageInfo
                {
                    PageIndex    = ctx.Pages.Count,
                    PageNumber   = ctx.Pages.Count + 1,
                    Dict         = node,
                    Resources    = resources,
                    Annots       = ResolveObject(ctx.Doc, node[PdfName.Annots]) as PdfArray,
                    ObjectNumber = ctx.Index?.GetObjectNumber(node) ?? -1,
                });
                return;
            }

            // An emptied branch reaches here with no /Kids to walk. The leaf test does not call it
            // a page, so the loop below needs its own guard.
            if (kids == null) return;

            for (int i = 0; i < kids.Count; i++)
            {
                // The charge belongs here, on the SLOT, not on the node that owns the array: the
                // visited set stops a shared /Kids array being expanded twice but not the n
                // resolves each expansion costs. See MaxPageTreeNodes. Charging before the resolve
                // also means exhaustion stops the walk immediately instead of finishing the
                // remaining slots of every frame on the way out.
                if (budget <= 0)
                {
                    if (budget == 0)
                    {
                        budget = -1;   // report once, however deep the recursion unwinding is
                        AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                            "the page-tree walk stopped after " +
                            MaxPageTreeNodes.ToString(CultureInfo.InvariantCulture) +
                            " /Kids slots; the remaining pages were not collected");
                    }
                    return;
                }
                budget--;

                PdfDictionary kid = ResolveDictionary(ctx.Doc, kids[i]);
                if (kid != null) CollectPagesFromNode(ctx, kid, resources, visited, ref budget, depth + 1);
            }
        }

        /// <summary>
        /// Decodes and summarises one page's content stream. Everything downstream reads scalars
        /// and name sets from the summary, never the bytes.
        ///
        /// <para>
        /// <b>Size ceilings.</b> The decoded bytes are attacker-chosen — a page's <c>/Contents</c>
        /// inflates from a few kilobytes of Flate to whatever Core's
        /// <c>PdfLimits.MaxDecompressedBytes</c> allows (512 MiB), and tokenising them costs a
        /// multiple of that again. Two ceilings bound it: <see cref="CheckContext.MaxPageContentBytes"/>
        /// per page and <see cref="CheckContext.ContentByteBudget"/> across the document (see
        /// <see cref="DefaultMaxPageContentBytes"/> and <see cref="DefaultMaxDocumentContentBytes"/>
        /// for the values and the reasoning). The lower of the two is handed to
        /// <see cref="ContentStreamTagger.ConcatenatePageContent(PdfDocument, PdfDictionary, long)"/>, so an oversized page is
        /// abandoned <em>while</em> it is being assembled rather than after — and a page that is
        /// refused sets <see cref="PageInfo.ContentError"/>, which leaves
        /// <see cref="PageInfo.ContentUsable"/> false, which is the single gate every content-level
        /// check already applies and which <see cref="ReportContentCoverage"/> already turns into a
        /// per-page Info issue naming the reason. A refused page is therefore never confused with a
        /// page that was checked and found clean.
        /// </para>
        /// </summary>
        private static void LoadPageContent(CheckContext ctx, PageInfo info)
        {
            if (info.Dict == null || info.ContentError != null) return;

            if (ctx.Encrypted)
            {
                // The bytes are ciphertext and this assembly holds no key: decoding them either
                // throws or yields noise, and a check fed noise invents findings. Not attempted,
                // and every check that consequently has no input is named in the report by
                // ReportEncryptionCoverage — PageInfo.ContentUsable stays false, which is already
                // the gate each of those checks applies.
                info.ContentError = "the document is encrypted and this checker does not decrypt, " +
                                    "so the page's content stream was not read";
                return;
            }

            // ---- size ceilings, applied before any content is assembled ---------
            // The document-wide budget is checked first because it can be spent, in which case
            // nothing is decoded for this page at all: that is the whole point of a budget.
            if (ctx.ContentByteBudget <= 0)
            {
                info.ContentError = "the audit's document-wide " +
                                    DescribeDocumentContentBudget(ctx) +
                                    " ceiling on decoded page content was already spent by earlier " +
                                    "pages, so this page's content stream was not read";
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                    "page " + info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                    " was not read: " + info.ContentError);
                return;
            }

            // The ceiling handed to the decoder is the tightest of the three in force. Core's own
            // per-page maximum is the floor of last resort: ConcatenatePageContent applies it
            // anyway, and there is deliberately no way to ask it for an unbounded concatenation.
            long ceiling = ContentStreamTagger.MaxConcatenatedContentBytes;
            if (ctx.MaxPageContentBytes > 0 && ctx.MaxPageContentBytes < ceiling) ceiling = ctx.MaxPageContentBytes;
            if (ctx.ContentByteBudget < ceiling) ceiling = ctx.ContentByteBudget;

            // ConcatenatePageContent is the assembly's single decoder for /Contents; it
            // returns null on decode failure — including a page past `ceiling`, whose reason names
            // the limit and the page — and an empty array when there is no /Contents.
            byte[] content = ContentStreamTagger.ConcatenatePageContent(
                ctx.Doc, info.Dict, ceiling, out long decodedBytes);
            if (content == null)
            {
                // The failure path is charged too, and that is the whole point of the out
                // parameter. A page whose /Contents is a Flate bomb with no /DL cannot be measured
                // before it is inflated (ContentStreamTagger.TryGetKnownDecodedLength says so
                // plainly), so it costs a full ceiling's worth of decompression and then returns
                // null. Leaving that free meant DefaultMaxDocumentContentBytes bounded nothing at
                // all for such pages — ten thousand copies of one bomb spent ten thousand ceilings
                // against a budget that never moved, which is precisely the "same attack with an
                // extra loop" its own doc comment names. At least one byte is charged even for a
                // refusal made before decoding, so a run of them still terminates.
                ctx.ContentByteBudget -= Math.Max(1L, decodedBytes);

                info.ContentError = ContentStreamTagger.LastTagError ?? "content stream could not be decoded";
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                    "page " + info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                    " content could not be decoded; content-level checks skipped for it: " + info.ContentError);
                return;
            }

            // Charge the budget for what was actually decoded, whether or not the scan below
            // succeeds: the bytes were assembled either way, and a page that fails to scan must not
            // be free to repeat.
            ctx.ContentByteBudget -= content.Length;

            info.Content = content;
            if (content.Length == 0) { info.Scan = ContentStreamScanner.ScanDetailed(content); return; }

            // Defensive second gate. `ceiling` above makes this unreachable, but it costs one
            // comparison and it is the assertion that the bytes about to be tokenised really are
            // bounded — the scanner applies its own ceiling too, and neither should be the only one.
            if (ctx.MaxPageContentBytes > 0 && content.Length > ctx.MaxPageContentBytes)
            {
                info.Content = null;
                info.ContentError = "its content stream decodes to " +
                                    content.Length.ToString(CultureInfo.InvariantCulture) +
                                    " bytes, past the audit's " +
                                    ctx.MaxPageContentBytes.ToString(CultureInfo.InvariantCulture) +
                                    "-byte per-page ceiling, so it was not scanned";
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                    "page " + info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                    " was not scanned: " + info.ContentError);
                return;
            }

            info.Scan = ContentStreamScanner.ScanDetailed(content);
            if (!info.Scan.IsSafe)
            {
                // Released for the same reason as on the success path below: ctx.Pages lives until
                // the audit ends, and nothing reads Content once ContentUsable is false — so
                // holding it made an unscannable page cost its full decoded size for the whole run.
                info.Content = null;
                info.Scan.Tokens = ContentStreamScanner.EmptyTokens;
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CollectPages",
                    "page " + info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                    " content could not be scanned safely; content-level checks skipped for it: " +
                    info.Scan.Error);
                return;
            }

            info.HasInlineImage = info.Scan.HasInlineImage;
            info.ContentLength  = content.Length;
            SummariseOperators(ctx, info);

            // Release the two large per-page buffers now that everything downstream needs has
            // been reduced to scalars and name sets. ctx.Pages lives until the whole audit
            // finishes, so holding them made peak memory the sum of every page's decoded
            // content plus its token list — for a document of ten pages that each expand to
            // the decoder's 512 MB cap, several gigabytes retained before a single check runs.
            info.Content = null;
            info.Scan.Tokens = ContentStreamScanner.EmptyTokens;
        }

        /// <summary>
        /// Names the document-wide content budget as the caller configured it, for a message that
        /// has to state the limit rather than the remainder (which is zero by the time it is said).
        /// </summary>
        private static string DescribeDocumentContentBudget(CheckContext ctx)
            => ctx.MaxDocumentContentBytes > 0
                ? ctx.MaxDocumentContentBytes.ToString(CultureInfo.InvariantCulture) + "-byte"
                : "unlimited";

        /// <summary>
        /// Recursion cap for nested Form XObjects. A form may legally invoke another. Stricter than
        /// <see cref="PdfLimits.MaxFormXObjectDepth"/> (32) on purpose: exceeding it here is
        /// reported as an incomplete scan rather than failing the operation, so a lower cap costs
        /// coverage on pathological files instead of correctness on normal ones.
        /// </summary>
        private const int MaxFormXObjectDepth = 12;

        /// <summary>
        /// Counts text-showing operators and XObject invocations across the page's content
        /// <b>and every Form XObject it invokes</b>, classifying each text operator by the
        /// marked-content sequence enclosing it.
        ///
        /// <para>
        /// <b>Why forms are followed.</b> Text drawn inside a Form XObject lives in a different
        /// stream but is just as much page content. Counting only page-level <c>/Contents</c>
        /// makes UA17 miss untagged text entirely, and makes UA24 accuse a page of being a scanned
        /// image when all its text simply sits in a form. Recursion is cycle-guarded (a form can
        /// reference itself) and depth-capped; anything not reachable is flagged through
        /// <see cref="PageInfo.FormXObjectScanIncomplete"/> so the counts are never presented as
        /// complete when they are not.
        /// </para>
        /// </summary>
        private static void SummariseOperators(CheckContext ctx, PageInfo info)
        {
            var visitedForms = new HashSet<int>();
            SummariseTokens(ctx, info, info.Scan.Tokens, info.Resources, visitedForms, 0);
        }

        /// <param name="enclosedByMarkedSequence">
        /// True when the stream is a Form XObject invoked from inside a marked-content sequence
        /// that is <em>open</em> but is not an artifact — an <c>/OC</c> wrapper is the everyday
        /// case. Its only effect is to seed the marked stack, so text inside the form is not
        /// counted as belonging to no sequence at all; everything else the form yields accumulates
        /// on <paramref name="info"/> as it should.
        /// </param>
        /// <param name="ctx">Per-check state: the document, the options and the findings list.</param>
        /// <param name="info">Accumulates what this page was found to contain.</param>
        /// <param name="tokens">The scanned content tokens to summarise.</param>
        /// <param name="resources">
        /// The stream's <c>/Resources</c>, for resolving the XObjects and fonts the tokens name.
        /// </param>
        /// <param name="visitedForms">
        /// Form XObject object numbers already walked on this path, so a self-drawing form
        /// terminates.
        /// </param>
        /// <param name="depth">Current nesting depth, against the recursion cap.</param>
        private static void SummariseTokens(
            CheckContext ctx,
            PageInfo info,
            IReadOnlyList<ContentToken> tokens,
            PdfDictionary resources,
            HashSet<int> visitedForms,
            int depth,
            bool enclosedByMarkedSequence = false)
        {
            PdfDictionary xobjects = ResolveDictionary(ctx.Doc, resources?[PdfName.XObject]);

            // /Properties holds the named property lists a `/Tag /Name BDC` refers to
            // (ISO 32000-1 §14.6.2). Resolved once per stream rather than per BDC.
            PdfDictionary properties = ResolveDictionary(ctx.Doc, resources?[PdfName.Properties]);

            var subtypeCache = new Dictionary<string, string>(StringComparer.Ordinal);

            // Marked-content nesting. Each frame records whether that sequence is an artifact;
            // a text operator is an artifact if ANY enclosing frame is one.
            var markedStack = new List<bool>();
            int artifactDepth = 0;

            // The sequence the invoking stream had open really does enclose this content
            // (ISO 32000-1 s14.6.1), so it is a frame like any other. Without it every text
            // operator in a form drawn from inside an /OC block counted as unmarked and UA17
            // reported a page whose content is perfectly well marked.
            if (enclosedByMarkedSequence) markedStack.Add(false);

            // Which operators show text. The font each one paints with is not recorded here: UA21
            // asks Plugins.Conformance.PdfUaFontUse, which the plugin's UA25 asks too and which reads
            // tiling patterns, Type 3 glyph procedures and appearances and restores the text state
            // at Q, so the two checkers answer alike.
            var paint = new TextPaintTracker();

            for (int i = 0; i < tokens.Count; i++)
            {
                ContentToken token = tokens[i];
                bool showsText = paint.Observe(token.Operator, token.Operands);

                switch (token.Operator)
                {
                    case PdfOperators.BeginMarkedContentProps:
                    case PdfOperators.BeginMarkedContent:
                    {
                        bool isArtifact = IsArtifactTag(token);
                        markedStack.Add(isArtifact);
                        if (isArtifact) artifactDepth++;
                        else if (artifactDepth == 0)
                        {
                            // A sequence is a structural CONTENT ITEM only once it names an /MCID
                            // (ISO 32000-1 §14.7.4.2) — that number is the only handle the
                            // structure tree has on it. A non-artifact BDC without one (an /OC
                            // wrapper, a /Span carrying nothing but /ActualText, a bare BMC) is
                            // legal producer output that Table 30 does not make /StructParents
                            // required for, so counting it here made UA18 accuse correct pages.
                            if (RecordMarkedContentId(ctx, info, token, properties))
                                info.StructuralMarkedSequences++;
                        }
                        continue;
                    }

                    case PdfOperators.EndMarkedContent:
                    {
                        if (markedStack.Count > 0)
                        {
                            if (markedStack[markedStack.Count - 1]) artifactDepth--;
                            markedStack.RemoveAt(markedStack.Count - 1);
                        }
                        continue;
                    }
                }

                if (PaintingOperators.Contains(token.Operator))
                {
                    info.PaintingOperators++;
                    continue;
                }

                if (showsText)
                {
                    info.TextShowingOperators++;
                    if (markedStack.Count == 0) info.UnmarkedTextShowingOperators++;
                    continue;
                }

                if (!string.Equals(token.Operator, PdfOperators.InvokeXObject, StringComparison.Ordinal)) continue;
                if (xobjects == null || token.Operands.Count == 0) continue;
                if (token.Operands[token.Operands.Count - 1] is not PdfName resourceName) continue;

                if (!subtypeCache.TryGetValue(resourceName.Value, out string subtype))
                {
                    PdfDictionary xo = ResolveDictionary(ctx.Doc, xobjects[resourceName]);
                    subtype = (ResolveObject(ctx.Doc, xo?[PdfName.Subtype]) as PdfName)?.Value ?? string.Empty;
                    subtypeCache[resourceName.Value] = subtype;
                }

                if (string.Equals(subtype, "Image", StringComparison.Ordinal))
                {
                    info.ImageXObjectInvocations++;
                    continue;
                }

                if (!string.Equals(subtype, "Form", StringComparison.Ordinal)) continue;

                info.InvokesFormXObject = true;

                // The two conditions are split; one parameter name would hide conflating them. Only genuine artifact content is diverted into a scratch PageInfo;
                // a form merely enclosed by a NON-artifact sequence is ordinary page content whose
                // structural sequences and font usage belong to this page.
                DescendIntoForm(ctx, info, xobjects[resourceName], resources, visitedForms, depth,
                    enclosedByArtifact: artifactDepth > 0,
                    enclosedByMarkedSequence: markedStack.Count > 0);
            }
        }

        /// <summary>
        /// Records the <c>/MCID</c> of one non-artifact marked-content sequence into
        /// <see cref="PageInfo.MarkedContentIds"/>.
        ///
        /// <para>The property list is either inline (<c>/P &lt;&lt;/MCID 0&gt;&gt; BDC</c>) or named
        /// (<c>/P /Pr1 BDC</c>, resolved through the resource dictionary's <c>/Properties</c>).
        /// A named list that cannot be resolved does <b>not</b> mean "no MCID" — it means the MCID
        /// could not be read — so it sets <see cref="PageInfo.MarkedContentIdsIncomplete"/> and the
        /// page is then excluded from every finding that rests on this set. A sequence with a
        /// property list and no <c>/MCID</c> at all (an <c>/OC</c> sequence, a <c>/Span</c> carrying
        /// only <c>/ActualText</c>) is legal and simply contributes nothing.</para>
        ///
        /// <para><b>Returns</b> whether an <c>/MCID</c> was actually read. That is also the test for
        /// "this sequence is a structural content item", which is why
        /// <see cref="PageInfo.StructuralMarkedSequences"/> is incremented from the result rather
        /// than from the mere presence of a non-artifact tag — see the call site.</para>
        /// </summary>
        private static bool RecordMarkedContentId(
            CheckContext ctx, PageInfo info, ContentToken token, PdfDictionary properties)
        {
            // BMC carries only a tag, so it can name no MCID; BDC carries [tag, properties].
            if (token.Operands.Count < 2) return false;

            PdfObject operand = token.Operands[token.Operands.Count - 1];

            PdfDictionary list = operand as PdfDictionary;
            if (list == null)
            {
                if (operand is not PdfName named) return false;

                list = ResolveDictionary(ctx.Doc, properties?[named]);
                if (list == null)
                {
                    // Reported once per page: a document that gets this wrong usually gets it
                    // wrong on every sequence, and one diagnostic per BDC would be a flood.
                    if (!info.MarkedContentIdsIncomplete)
                    {
                        info.MarkedContentIdsIncomplete = true;
                        AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.SummariseOperators",
                            "a marked-content sequence on page " +
                            info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                            " names the property list /" + named.Value + ", which is not in the resource " +
                            "dictionary's /Properties; its MCID could not be read, so this page is excluded " +
                            "from the MCID-mapping checks rather than judged on a partial set");
                    }

                    // "Not known to be structural", not "known not to be". The page carries
                    // MarkedContentIdsIncomplete from here, and the checks that rest on these
                    // counts say so in the report rather than clearing the page.
                    return false;
                }
            }

            int? mcid = GetInteger(ctx, list[PdfName.MCID]);
            if (!mcid.HasValue || mcid.Value < 0) return false;

            info.MarkedContentIds.Add(mcid.Value);
            return true;
        }

        /// <summary>Whether a BDC/BMC operand names the <c>/Artifact</c> tag.</summary>
        private static bool IsArtifactTag(ContentToken token)
        {
            // Operands are [tag] for BMC and [tag, properties] for BDC; the tag is the first.
            for (int i = 0; i < token.Operands.Count; i++)
            {
                if (token.Operands[i] is PdfName name)
                    return string.Equals(name.Value, MarkedContentTags.Artifact, StringComparison.Ordinal);
            }
            return false;
        }

        private static void DescendIntoForm(
            CheckContext ctx,
            PageInfo info,
            PdfObject formRef,
            PdfDictionary parentResources,
            HashSet<int> visitedForms,
            int depth,
            bool enclosedByArtifact,
            bool enclosedByMarkedSequence)
        {
            if (depth >= MaxFormXObjectDepth)
            {
                info.FormXObjectScanIncomplete = true;
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.SummariseOperators",
                    "Form XObject nesting exceeded " + MaxFormXObjectDepth.ToString(CultureInfo.InvariantCulture) +
                    " levels on page " + info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                    "; its content was not counted");
                return;
            }

            // Once either document-wide budget is spent the answer for every remaining form is
            // the same, so it is given in O(1) rather than re-derived per invocation.
            if (ctx.FormBudgetReported)
            {
                info.FormXObjectScanIncomplete = true;
                return;
            }

            // Cycle guard: a Form XObject whose own content invokes it again. Per PATH, not per
            // page — the number is taken back once the form's walk returns — so a page drawing one
            // form twice, once inside an artifact and once outside, has its second drawing's text
            // counted as the untagged content it is (UA17); the form work budget and the per-form
            // scan cache bound what the repeated walks cost.
            int formNumber = formRef is PdfReference reference ? reference.ReferenceObjectNumber : -1;
            if (formNumber > 0 && !visitedForms.Add(formNumber)) return;
            try
            {
                DescendIntoVisitedForm(ctx, info, formRef, formNumber, parentResources, visitedForms, depth,
                    enclosedByArtifact, enclosedByMarkedSequence);
            }
            finally
            {
                if (formNumber > 0) visitedForms.Remove(formNumber);
            }
        }

        /// <summary><see cref="DescendIntoForm"/>'s walk of a form the cycle guard admitted.</summary>
        private static void DescendIntoVisitedForm(
            CheckContext ctx,
            PageInfo info,
            PdfObject formRef,
            int formNumber,
            PdfDictionary parentResources,
            HashSet<int> visitedForms,
            int depth,
            bool enclosedByArtifact,
            bool enclosedByMarkedSequence)
        {
            PdfStream form;
            try
            {
                form = ResolveObject(ctx.Doc, formRef) as PdfStream;
            }
            catch (Exception ex)
            {
                info.FormXObjectScanIncomplete = true;
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.SummariseOperators",
                    "a Form XObject could not be resolved on page " +
                    info.PageNumber.ToString(CultureInfo.InvariantCulture) + "; its content was not counted", ex);
                return;
            }

            if (form == null) { info.FormXObjectScanIncomplete = true; return; }

            ContentScanResult scan = ScanFormXObject(ctx, info, form, formNumber);
            if (scan == null) return;                   // unreadable, budget exhausted, or empty

            // Gathered the same way ImageXObjectInvocations already is, and the omission was
            // asymmetric: image XObject invocations and text operators were counted through forms
            // while an inline image inside one was not, so a scanned page whose /Contents is just
            // `/Fm0 Do` — with the BI…ID…EI inside the form — had TextShowingOperators 0,
            // ImageXObjectInvocations 0 and HasInlineImage false, and UA24 skipped it in silence.
            // That is a genuinely image-only, unreadable page passing the image-only check.
            info.HasInlineImage |= scan.HasInlineImage;

            if (scan.Tokens.Count == 0) return;

            // Walking the token list is the other half of the cost, and it is the half the cache
            // cannot remove: N pages sharing one /Contents that invokes N forms walk N² token
            // lists over an O(N)-byte file. Charge it, and stop honestly when it is spent.
            if (!ChargeFormWork(ctx, info, scan.Tokens.Count)) return;

            // A form invoked from inside an artifact sequence is itself artifact content; its
            // text must not be reported as untagged.
            //
            // Only an ARTIFACT sequence diverts. "artifactDepth > 0 || markedStack.Count > 0" is not
            // the same thing: an /OC wrapper is a non-artifact sequence that names no MCID, so a page
            // whose content is `/OC /MC0 BDC  /Fm0 Do  EMC` — with the structural sequence inside
            // Fm0 — would have the form's StructuralMarkedSequences counted into a scratch PageInfo
            // and dropped at the roll-up below, and UA18 would see a page with no structural content
            // although it holds a content item no /StructParents key can address.
            var nested = enclosedByArtifact ? new PageInfo { PageNumber = info.PageNumber } : info;

            // Resources are inherited from the invoking context when the form declares none
            // (ISO 32000-1 §8.10.2).
            PdfDictionary formResources = ResolveDictionary(ctx.Doc, form[PdfName.Resources]) ?? parentResources;

            // A form with a /StructParents of its own keys its marked content through that entry
            // (ISO 32000-1 §14.7.4.4), so its structural sequences do not make the invoking page
            // need /StructParents (UA18; test 7.20-t02-pass-a).
            int structuralBefore = nested.StructuralMarkedSequences;
            SummariseTokens(ctx, nested, scan.Tokens, formResources, visitedForms, depth + 1,
                            enclosedByMarkedSequence);
            if (form[NameStructParents] != null) nested.StructuralMarkedSequences = structuralBefore;

            if (!ReferenceEquals(nested, info))
            {
                // Roll up only what still matters when the whole form is an artifact: it draws,
                // but none of it is untagged content.
                info.TextShowingOperators       += nested.TextShowingOperators;
                info.ImageXObjectInvocations    += nested.ImageXObjectInvocations;
                info.FormXObjectScanIncomplete  |= nested.FormXObjectScanIncomplete;

                // The MCIDs are rolled up too, deliberately. They are only ever read to answer
                // "does the MCID this structure element names exist at all?", and answering "no"
                // for content the checker chose not to attribute would be an invented finding.
                info.MarkedContentIds.UnionWith(nested.MarkedContentIds);
                info.MarkedContentIdsIncomplete |= nested.MarkedContentIdsIncomplete;
            }
        }

        /// <summary>
        /// Tokenises one Form XObject, once per audit. Returns null when there is nothing to walk
        /// — the stream would not decode, would not scan safely, is empty, or the document-wide
        /// byte budget is spent — having already set
        /// <see cref="PageInfo.FormXObjectScanIncomplete"/> in every case but "empty".
        /// </summary>
        private static ContentScanResult ScanFormXObject(
            CheckContext ctx, PageInfo info, PdfStream form, int formNumber)
        {
            if (formNumber > 0 && ctx.FormScans.TryGetValue(formNumber, out ContentScanResult cached))
            {
                if (cached == null) info.FormXObjectScanIncomplete = true;
                return cached;
            }

            // Decoded under what the budget has left, stopped there, or a form past it would be decoded
            // whole to be refused, each one, for the few hundred bytes a double deflate takes.
            if (!form.TryGetDecodedData(ctx.FormScanByteBudget, out byte[] data, out string refused))
            {
                // What it cost is the decoder's to know, and up to the ceiling: half of what is left is
                // spent, so the refused decodes together cost no more than twice the budget.
                ctx.FormScanByteBudget -= ctx.FormScanByteBudget / 2;
                info.FormXObjectScanIncomplete = true;
                if (formNumber > 0) ctx.FormScans[formNumber] = null;
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.SummariseOperators",
                    "a Form XObject's content stream could not be decoded on page " +
                    info.PageNumber.ToString(CultureInfo.InvariantCulture) + " within the " +
                    ctx.FormScanByteBudget.ToString(CultureInfo.InvariantCulture) +
                    " bytes the audit has left for forms (" + refused + "); its content was not counted");
                if (refused.IndexOf("limit", StringComparison.Ordinal) >= 0)
                    ReportFormBudgetExhausted(ctx,
                        "the audit reached its " + MaxFormScanBytes.ToString(CultureInfo.InvariantCulture) +
                        "-byte ceiling on Form XObject content tokenised (distinct streams)");
                return null;
            }

            if (data == null || data.Length == 0)
            {
                // Cached as an empty (not a failed) scan: an empty form is perfectly legal and
                // must not set FormXObjectScanIncomplete, now or on any later page.
                if (formNumber > 0) ctx.FormScans[formNumber] = EmptyFormScan;
                return null;
            }

            if (ctx.FormScanByteBudget < data.Length)
            {
                info.FormXObjectScanIncomplete = true;
                ReportFormBudgetExhausted(ctx,
                    "the audit reached its " + MaxFormScanBytes.ToString(CultureInfo.InvariantCulture) +
                    "-byte ceiling on Form XObject content tokenised (distinct streams)");
                if (formNumber > 0) ctx.FormScans[formNumber] = null;
                return null;
            }
            ctx.FormScanByteBudget -= data.Length;

            ContentScanResult scan = ContentStreamScanner.ScanDetailed(data);
            if (!scan.IsSafe)
            {
                info.FormXObjectScanIncomplete = true;
                if (formNumber > 0) ctx.FormScans[formNumber] = null;
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.SummariseOperators",
                    "a Form XObject's content stream on page " +
                    info.PageNumber.ToString(CultureInfo.InvariantCulture) +
                    " could not be scanned safely; its content was not counted — " + scan.Error);
                return null;
            }

            if (formNumber > 0) ctx.FormScans[formNumber] = scan;
            return scan;
        }

        /// <summary>
        /// The scan of a zero-length form, shared so an empty Form XObject invoked from a
        /// thousand pages is still one cache entry with no tokens.
        /// </summary>
        private static readonly ContentScanResult EmptyFormScan =
            ContentStreamScanner.ScanDetailed(new byte[0]);

        /// <summary>
        /// Spends <paramref name="tokens"/> of the document-wide form-walking budget. Returns
        /// false once it is exhausted, having marked the page's counts as incomplete.
        /// </summary>
        private static bool ChargeFormWork(CheckContext ctx, PageInfo info, long tokens)
        {
            if (ctx.FormTokenWalkBudget < tokens)
            {
                info.FormXObjectScanIncomplete = true;
                ReportFormBudgetExhausted(ctx,
                    "the audit reached its " + MaxFormTokenWalk.ToString(CultureInfo.InvariantCulture) +
                    "-token ceiling on Form XObject content walked");
                return false;
            }

            ctx.FormTokenWalkBudget -= tokens;
            return true;
        }

        private static void ReportFormBudgetExhausted(CheckContext ctx, string what)
        {
            if (ctx.FormBudgetReported) return;
            ctx.FormBudgetReported = true;

            AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.SummariseOperators",
                what + "; the Form XObjects beyond that point were not counted. The affected pages " +
                "are marked as incompletely scanned, so no finding rests on the partial counts, and " +
                "each of them is named in the report by ReportPartialContentCoverage.");
        }
    }
}
