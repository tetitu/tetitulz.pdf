// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.ReadingOrder
{
    /// <summary>
    /// Analyzes and repairs the logical reading order of a PDF structure tree.
    ///
    /// <para>
    /// PDF/UA-1 (and ISO 32000-1 14.8.4.2) require the logical structure tree to present
    /// content in the order it is meant to be read, which for a Latin-script page means
    /// top-to-bottom. This class compares the order in which the structure tree reaches
    /// content against the order in which that content appears on the page.
    /// </para>
    ///
    /// <para><b>How an element's position is determined</b> — in this order:</para>
    /// <list type="number">
    ///   <item><description><b>Marked content (authoritative).</b> The element's <c>/K</c> is read
    ///     for marked-content identifiers — bare integers, and <c>/MCR</c> dictionaries with
    ///     <c>/MCID</c> (plus an optional <c>/Pg</c> that overrides the element's page). The page's
    ///     content stream is scanned once, tracking <c>q</c>/<c>Q</c>/<c>cm</c> and the text
    ///     matrices (<c>Tm</c>/<c>Td</c>/<c>TD</c>/<c>T*</c>), and each MCID is mapped to the
    ///     baseline Y of the <em>first</em> text-showing operator inside its marked-content
    ///     sequence — plus the band that sequence spans (highest and lowest baseline, leftmost and
    ///     rightmost show origin), which is the geometry the single-column proof below needs. This
    ///     is where the content actually is, not where a producer said it is.
    ///     </description></item>
    ///   <item><description><b><c>/ActualText</c> (or <c>/Alt</c>) prefix match.</b> Retained only
    ///     as a fallback for untagged-but-structured files that carry no MCIDs.</description></item>
    ///   <item><description><b>Unknown.</b> The element is <em>excluded</em> from the comparison.
    ///     The previous implementation collapsed every unpositionable element to <c>Y = 0</c>,
    ///     where they all compared equal and produced order findings that were pure noise.
    ///     </description></item>
    /// </list>
    ///
    /// <para>
    /// Only <em>content-bearing</em> elements take part in the comparison: an element is placed by
    /// its own marked content, never by its children's. A container such as <c>Sect</c> therefore
    /// contributes nothing of its own, and the sequence being checked is exactly the sequence of
    /// content the structure tree reaches — which is what reading order means.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here reports "no issues" for a document it could not examine.</b> Every path that
    /// cannot produce an answer — an unparseable or encrypted file, no <c>/StructTreeRoot</c>, no
    /// element that could be located — returns a <see cref="ReadingOrderAnalysis"/> with
    /// <see cref="ReadingOrderAnalysis.Analyzed"/> false and a
    /// <see cref="ReadingOrderAnalysis.NotAnalyzedReason"/>, and reports through
    /// <see cref="AccessibilityDiagnostics"/> as well as <see cref="LastError"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Baseline Y alone never justifies a re-order.</b> A single Y cannot tell one column from
    /// two: the left and right columns of a two-column page interleave perfectly in Y, and so do
    /// the cells of a table row whose baselines are not identical (a vertically centred cell, a
    /// two-line cell beside a one-line cell). Ranking either of those by Y turns a <em>correct</em>
    /// <c>/K</c> order into a wrong one — and because <see cref="Fix"/> writes the result, the new
    /// order is the one assistive technology announces. Both <see cref="Fix"/> and
    /// <see cref="Analyze(Stream)"/> therefore have to <em>prove</em> that the elements they are
    /// about to rank form a single vertical stack:
    /// </para>
    /// <list type="number">
    ///   <item><description>the parent's <c>/RoleMap</c>-resolved role must not be one whose child
    ///     order is semantic rather than visual (<c>Table</c>, <c>TR</c>, <c>L</c>, <c>LI</c>, …):
    ///     a row's cells run left to right by meaning, and an <c>LI</c>'s <c>Lbl</c> precedes its
    ///     <c>LBody</c> on the same baseline;</description></item>
    ///   <item><description>any two of them that share vertical space must also share a baseline —
    ///     overlapping bands are content sitting side by side, and ranking side-by-side content by
    ///     Y is only harmless while the ranking makes no claim about it;</description></item>
    ///   <item><description>the proposed order must not step back to the right after having moved
    ///     left, which is the signature of column-by-column content.</description></item>
    /// </list>
    /// <para>
    /// A set that fails any of these keeps the order it already had, and the reason is reported
    /// through <see cref="AccessibilityDiagnostics"/> and <see cref="LastError"/>. Declining costs
    /// a repair that might have been right; permuting on insufficient evidence manufactures a
    /// defect in a file that was correct.
    /// </para>
    /// </summary>
    public static class PdfReadingOrderAnalyzer
    {
        // ================================================================
        //  Limits
        // ================================================================

        /// <summary>Hard ceiling on structure elements visited by the <see cref="Fix"/> traversal.</summary>
        private const int MaxNodes = 500_000;

        /// <summary>Recursion cap for the <see cref="Fix"/> traversal, matching <see cref="StructTreeWalker"/>.</summary>
        private const int MaxDepth = StructTreeWalker.DefaultMaxDepth;

        /// <summary>
        /// Cap on the <c>q</c>/<c>Q</c> and <c>BDC</c>/<c>EMC</c> stacks while scanning a content
        /// stream. No real page nests either more than a handful deep; a hostile stream of nothing
        /// but <c>q</c> would otherwise allocate one graphics state per two bytes of input. The
        /// value is the repository-wide graphics-state stack ceiling,
        /// <see cref="PdfLimits.MaxGraphicsStateStackDepth"/>, which the marked-content stack has
        /// always shared here.
        /// </summary>
        private static int MaxContentStackDepth => PdfLimits.MaxGraphicsStateStackDepth;

        /// <summary>Prefix length used by the <c>/ActualText</c> fallback match (unchanged behaviour).</summary>
        private const int TextPrefixLength = 20;

        /// <summary>
        /// How far an element may sit from its visual position before it is reported. Kept at the
        /// original ±1 so that a single adjacent transposition (routinely produced by a line-based
        /// tagger) is not flagged.
        /// </summary>
        private const int PositionTolerance = 1;


        /// <summary>
        /// How many <c>/K</c> entries of one element are examined while measuring that element's
        /// extent. The first entry that resolves fixes the element's ordering key — unchanged
        /// behaviour — and the rest only widen its measured band. The cap keeps a hostile tree
        /// whose elements all point <c>/K</c> at one enormous shared array linear rather than
        /// quadratic, the same failure <see cref="StructTreeWalker.DefaultMaxEdges"/> bounds in the
        /// walker. Truncating the measurement can only narrow a band, never invent one.
        /// </summary>
        private const int MaxExtentProbes = 64;

        /// <summary>
        /// Baselines closer together than this are treated as one horizontal band, so side-by-side
        /// content whose baselines differ by a rounding error is still seen to share vertical space.
        /// </summary>
        private const float BandTolerance = 1f;

        /// <summary>
        /// Horizontal extents nearer than this count as touching rather than as separate columns.
        /// </summary>
        private const float ColumnGapTolerance = 1f;

        private static readonly Dictionary<int, PdfObject> NoObjects = new Dictionary<int, PdfObject>();

        // ================================================================
        //  Diagnostics
        // ================================================================

        /// <summary>
        /// Reason the most recent <see cref="Analyze(Stream)"/> or <see cref="Fix"/> call on this
        /// thread could not do its job (or degraded), and <c>null</c> when it ran normally.
        /// <c>[ThreadStatic]</c>, so concurrent operations on a server host cannot read each
        /// other's diagnostics.
        /// </summary>
        [ThreadStatic]
        private static string _lastError;

        /// <summary>Last failure or degradation on this thread, or <c>null</c>.</summary>
        public static string LastError => _lastError;

        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>Analyzes reading order issues in a PDF file.</summary>
        public static ReadingOrderAnalysis Analyze(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Analyze(fs);
        }

        /// <summary>
        /// Analyzes reading order issues in a PDF stream.
        ///
        /// <para>
        /// Check <see cref="ReadingOrderAnalysis.Analyzed"/> before trusting
        /// <see cref="ReadingOrderAnalysis.IsOrdered"/>: a document that could not be examined
        /// carries no issues, which is not the same claim as "its reading order is correct".
        /// </para>
        /// </summary>
        public static ReadingOrderAnalysis Analyze(Stream input) => Analyze(input, null);

        /// <summary>
        /// Analyses the reading order of an <b>encrypted</b> document, given its password.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a reader, so it needs a password — not a plan.</b> The writers in this
        /// assembly have a <c>Plan</c> because they append a revision and an encrypted file needs
        /// that revision encrypted with its own key. <c>Analyze</c> produces no revision at all: it
        /// examines the structure tree and reports. So it does what <c>PdfOperations</c>'s readers
        /// do — decrypt the input through
        /// <c>Base.Encryption.PdfDecryptedInput</c>, which goes via the registered
        /// <c>PdfDecryptorHolder</c> and therefore needs no reference to
        /// <c>Tetitulz.Pdf.Encryption</c> from here.</para>
        /// <para>Kept as a distinct overload rather than widened with an optional parameter: C#
        /// bakes optional-argument defaults in at the call site, so widening a shipped signature is
        /// a <b>binary</b> break for an already-compiled consumer.</para>
        /// <para>Without a registered decryptor an encrypted document still reports
        /// <c>Analyzed == false</c> with the reason, exactly as before — the password is what the
        /// caller can supply, not a guarantee the mechanism is present.</para>
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static ReadingOrderAnalysis Analyze(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;

            byte[] bytes = PdfStructureTreeEditor.ReadAll(input);
            if (!string.IsNullOrEmpty(password))
            {
                try
                {
                    using var encrypted = new MemoryStream(bytes, writable: false);
                    bytes = Tetitulz.Pdf.Base.Encryption.PdfDecryptedInput.ReadDecrypted(
                        encrypted, password);
                }
                catch (Exception ex)
                {
                    return NotAnalyzed("PdfReadingOrderAnalyzer.Analyze",
                        "the document is encrypted and could not be decrypted with the supplied " +
                        "password", ex);
                }
            }

            using var ms = new MemoryStream(bytes);
            PdfDocument doc = null;
            try
            {
                try
                {
                    doc = PdfDocument.Open(ms);
                }
                catch (Exception ex)
                {
                    return NotAnalyzed("PdfReadingOrderAnalyzer.Analyze",
                        "the document could not be parsed, so its reading order was not examined", ex);
                }

                if (doc.IsEncrypted)
                {
                    return NotAnalyzed("PdfReadingOrderAnalyzer.Analyze",
                        "the document is encrypted, so neither its structure tree nor its page content could be read", null);
                }

                PdfDictionary structTreeRoot = ResolveStructTreeRoot(doc, "PdfReadingOrderAnalyzer.Analyze");
                if (structTreeRoot == null)
                {
                    return NotAnalyzed("PdfReadingOrderAnalyzer.Analyze",
                        "the document has no readable /StructTreeRoot, so it has no logical reading order to check", null);
                }

                PdfObjectIndex index = PdfObjectIndex.Build(doc);
                List<StructNode> elements = CollectElements(structTreeRoot, doc, index);
                if (elements.Count == 0)
                {
                    return NotAnalyzed("PdfReadingOrderAnalyzer.Analyze",
                        "the structure tree contains no structure elements", null);
                }

                var resolver = new PositionResolver(doc, index, TryExtractStructuredText(bytes));
                Dictionary<string, string> roleMap =
                    BuildRoleMap(doc, structTreeRoot, "PdfReadingOrderAnalyzer.Analyze");

                var placed = new List<PlacedElement>(elements.Count);
                for (int i = 0; i < elements.Count; i++)
                {
                    StructNode node = elements[i];
                    if (!resolver.TryResolve(node.Dict, node.PageIndex, out ContentPosition position)) continue;

                    // An element whose siblings are ordered by meaning — a table cell, a list
                    // label — has been located but cannot be judged by baseline Y. It still counts
                    // as placed, because it was found on a page, but it takes no part in the
                    // comparison: it can neither be reported nor shift what its neighbours are
                    // compared against.
                    // Raw OR mapped: this gate exists to STOP a rewrite, so widening what trips it
                    // is the fail-safe direction. Testing only the canonical name lets
                    // /RoleMap << /Table /Div >> turn a <Table> into a <Div>, at which point its
                    // cells are ranked by baseline Y — the exact mutation the gate below says it
                    // prevents ("would swap columns and break the header/data associations").
                    bool semantic = IsSemanticChildOrderRole(node.Parent?.Role)
                                 || IsSemanticChildOrderRole(CanonicalRole(node.Parent?.Role, roleMap));
                    placed.Add(new PlacedElement(node.Role, position, semantic));
                }

                if (placed.Count == 0)
                {
                    return NotAnalyzed("PdfReadingOrderAnalyzer.Analyze",
                        "none of the " + elements.Count.ToString(CultureInfo.InvariantCulture) +
                        " structure elements could be located on a page: no element carries a resolvable " +
                        "marked-content identifier and none has /ActualText matching the extracted text", null);
                }

                if (placed.Count < elements.Count)
                {
                    // Not a failure — but the caller must be able to tell a clean pass over the
                    // whole tree from a clean pass over 2% of it. See ReadingOrderAnalysis.IsPartial.
                    Report("PdfReadingOrderAnalyzer.Analyze",
                        "only " + placed.Count.ToString(CultureInfo.InvariantCulture) + " of " +
                        elements.Count.ToString(CultureInfo.InvariantCulture) +
                        " structure elements could be located on a page; the reading-order comparison " +
                        "covers only those and the result is partial", null);
                }

                return new ReadingOrderAnalysis(CompareOrder(placed), analyzed: true, notAnalyzedReason: null,
                    elementsPlaced: placed.Count, elementsTotal: elements.Count);
            }
            finally
            {
                doc?.Dispose();
            }
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Computes the reading-order repair <b>without writing it</b>, so a caller that owns the
        /// write can append it to bytes this method never sees — which is how an <b>encrypted</b>
        /// document gets its reading order repaired.
        /// </summary>
        /// <remarks>
        /// <para><see cref="Fix(Stream, Stream)"/> appends a clear-text revision and therefore
        /// refuses an encrypted document. This assembly cannot decrypt (it does not reference
        /// <c>Tetitulz.Pdf.Encryption</c>, and must not), so the operation travels to the decryptor:</para>
        /// <code>
        /// PdfEncryptedIncrementalUpdate.Run(input, output, password,
        ///     (decrypted, firstFree) =&gt; PdfReadingOrderAnalyzer.Plan(decrypted, firstFree).Objects);
        /// </code>
        /// <para><b>This repair mints no object numbers, and that is worth stating rather than
        /// leaving to be discovered.</b> It re-deals existing <c>/K</c> arrays, so every object it
        /// emits is a revision of one the document already has — the created set is empty. The
        /// <paramref name="firstFreeObjectNumber"/> ceiling is therefore reserved as <b>defence in
        /// depth</b>, not because anything currently needs it: it sits immediately after the index
        /// is built, which is where it must already be if this pass ever starts allocating.
        /// <c>PdfArtifactMarker</c> reserved after minting instead, handed out a number that was
        /// live in the target file, and produced an undecryptable document — the pattern is not
        /// transferable between writers without checking where each one allocates.</para>
        /// <para><b>Report through <see cref="ReadingOrderPlan.Describe"/></b>: whether the revision
        /// reached the file is known only to the caller, and a permutation that was planned and
        /// then lost must read as zero moved rather than as a repaired document.</para>
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate — the object-number ceiling of the file
        /// the revision will be appended to, plus one.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="firstFreeObjectNumber"/> is below 1.
        /// </exception>
        public static ReadingOrderPlan Plan(PdfDocument doc, int firstFreeObjectNumber)
            => Plan(doc, firstFreeObjectNumber, MaxNodes);

        /// <summary>
        /// <see cref="Plan(PdfDocument, int)"/> with the traversal's node ceiling as a parameter, so
        /// the ceiling can be tested on a small tree rather than only at 500,000 nodes.
        /// </summary>
        internal static ReadingOrderPlan Plan(PdfDocument doc, int firstFreeObjectNumber, int maxNodes)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (firstFreeObjectNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(firstFreeObjectNumber),
                    "Object numbering starts at 1; 0 is the free-list head and can never be written.");

            _lastError = null;

            PdfDictionary structTreeRoot = ResolveStructTreeRoot(doc, "PdfReadingOrderAnalyzer.Plan");
            if (structTreeRoot == null) return ReadingOrderPlan.Nothing;

            PdfObjectIndex index = PdfObjectIndex.Build(doc);
            // Defence in depth — see the remarks. Placed here, before any reordering, because this
            // is where it has to be if this pass ever mints an object.
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            var resolver = new PositionResolver(doc, index, null);
            var modified = new Dictionary<int, PdfObject>();
            var state    = new FixState(BuildRoleMap(doc, structTreeRoot, "PdfReadingOrderAnalyzer.Plan"), doc);

            int moved = ReorderTree(structTreeRoot, doc, index, resolver, modified, state, maxNodes);
            if (moved == 0 || modified.Count == 0) return ReadingOrderPlan.Nothing;

            var objects = RevisionObjects.From(modified, index, "PdfReadingOrderAnalyzer.Plan");

            return new ReadingOrderPlan(objects, moved);
        }

        /// <summary>
        /// Fixes reading order by permuting <c>/K</c> arrays so that the content each entry
        /// reaches appears in top-to-bottom order. Writes the original document followed by a
        /// single incremental update.
        ///
        /// <para>
        /// Entries whose position cannot be determined (and non-content entries such as
        /// <c>/OBJR</c>) are <em>pinned</em>: they keep the slot they already occupy, and the
        /// positionable entries are re-dealt into the remaining slots. The array's length and
        /// membership therefore never change — only the order of entries this pass can actually
        /// place.
        /// </para>
        ///
        /// <para>
        /// An array is only permuted when this pass can <em>prove</em> its children are a single
        /// top-to-bottom column — see the type-level remarks. A <c>TR</c>'s cells, a two-column
        /// section and any set of blocks that share vertical space keep the order they had, and the
        /// reason is reported through <see cref="AccessibilityDiagnostics"/>.
        /// </para>
        ///
        /// <para>
        /// <see cref="ReadingOrderFixResult.ElementsReordered"/> counts slots whose occupant
        /// changed, i.e. entries that genuinely moved.
        /// </para>
        ///
        /// <para>
        /// <b>Invariant: <see cref="ReadingOrderFixResult.ElementsReordered"/> &gt; 0 implies the
        /// output differs from the input.</b> The permutation is computed in memory and only then
        /// handed to the incremental writer, which is documented never to throw — it reports a
        /// failure and leaves the output holding just the original bytes. Returning the permutation
        /// count unconditionally would claim a repaired reading order for a file that is
        /// byte-for-byte the input. The count is returned only once the appended revision has
        /// actually reached <paramref name="output"/>; otherwise the result is <c>0</c> (so
        /// <see cref="ReadingOrderFixResult.Modified"/> is <c>false</c>) and <see cref="LastError"/>
        /// carries the reason — the warning channel this result type does not have.
        /// </para>
        /// </summary>
        public static ReadingOrderFixResult Fix(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            _lastError = null;

            byte[] bytes = PdfStructureTreeEditor.ReadAll(input);

            using var ms = new MemoryStream(bytes);
            PdfDocument doc = null;
            try
            {
                try
                {
                    doc = PdfDocument.Open(ms);
                }
                catch (Exception ex)
                {
                    Report("PdfReadingOrderAnalyzer.Fix",
                        "the document could not be parsed; it was copied through unchanged", ex);
                    output.Write(bytes, 0, bytes.Length);
                    return new ReadingOrderFixResult(0);
                }

                if (doc.IsEncrypted)
                {
                    Report("PdfReadingOrderAnalyzer.Fix",
                        "the document is encrypted, so its structure tree could not be read; it was copied through unchanged", null);
                    output.Write(bytes, 0, bytes.Length);
                    return new ReadingOrderFixResult(0);
                }

                PdfObjectIndex index = PdfObjectIndex.Build(doc);

                PdfDictionary structTreeRoot = ResolveStructTreeRoot(doc, "PdfReadingOrderAnalyzer.Fix");
                if (structTreeRoot == null)
                {
                    Report("PdfReadingOrderAnalyzer.Fix",
                        "the document has no readable /StructTreeRoot, so there is no reading order to repair; " +
                        "it was copied through unchanged", null);

                    // Nothing to append, so this is a pure passthrough — but it goes through the
                    // same verified write as the real edit below, so a sink that could not even
                    // take the original document is reported rather than silently producing a
                    // truncated file under a clean "0 reordered".
                    WriteVerifiedFix(output, bytes, doc, index, NoObjects);
                    return new ReadingOrderFixResult(0);
                }

                var resolver = new PositionResolver(doc, index, TryExtractStructuredText(bytes));
                var modified = new Dictionary<int, PdfObject>();
                var state    = new FixState(BuildRoleMap(doc, structTreeRoot, "PdfReadingOrderAnalyzer.Fix"), doc);

                int moved = ReorderTree(structTreeRoot, doc, index, resolver, modified, state, MaxNodes);

                // One appended revision for the whole edit, written by the shared writer (which
                // emits the original bytes itself, handles real generation numbers, /ID and an
                // xref stream when the original used one) — and verified, because that writer
                // falls back to copying the original without throwing. `moved` describes the
                // permutation this pass planned; only a landed revision makes it describe the
                // produced document.
                if (!WriteVerifiedFix(output, bytes, doc, index, modified))
                    return new ReadingOrderFixResult(0);

                return new ReadingOrderFixResult(moved);
            }
            finally
            {
                doc?.Dispose();
            }
        }

        // ================================================================
        //  Analysis helpers
        // ================================================================

        /// <summary>One structure element that could be given a page and a measured position.</summary>
        private readonly struct PlacedElement
        {
            internal readonly string Role;
            internal readonly ContentPosition Position;

            /// <summary>
            /// True when this element's place among its siblings comes from the parent's meaning
            /// (a cell in a <c>TR</c>, a <c>Lbl</c> in an <c>LI</c>) rather than from where it sits
            /// on the page. Such an element is excluded from the visual comparison entirely.
            /// </summary>
            internal readonly bool OrderIsSemantic;

            internal PlacedElement(string role, ContentPosition position, bool orderIsSemantic)
            {
                Role            = role ?? "Unknown";
                Position        = position;
                OrderIsSemantic = orderIsSemantic;
            }
        }

        /// <summary>
        /// Compares document order against visual order, page by page. Elements are already in
        /// pre-order (document order); the expected order is Y descending, because PDF user space
        /// has its origin at the bottom-left, so a larger Y is higher on the page.
        ///
        /// <para>
        /// A page is only judged when the elements on it can be <em>shown</em> to be a single
        /// top-to-bottom column (<see cref="DescribeLayoutRisk"/>). On a two-column page the Y
        /// ranking interleaves the columns, so a correctly ordered document comes out with almost
        /// every element "out of position" — findings that are pure false positives, and which
        /// <see cref="Fix"/> would then act on.
        /// </para>
        /// </summary>
        private static IReadOnlyList<ReadingOrderIssue> CompareOrder(List<PlacedElement> placed)
        {
            var issues = new List<ReadingOrderIssue>();

            var byPage    = new Dictionary<int, List<PlacedElement>>();
            var pageOrder = new List<int>();

            for (int i = 0; i < placed.Count; i++)
            {
                if (placed[i].OrderIsSemantic) continue;

                int page = placed[i].Position.PageIndex;
                if (!byPage.TryGetValue(page, out List<PlacedElement> list))
                {
                    list = new List<PlacedElement>();
                    byPage[page] = list;
                    pageOrder.Add(page);
                }
                list.Add(placed[i]);
            }

            bool riskReported = false;

            for (int p = 0; p < pageOrder.Count; p++)
            {
                int pageIndex = pageOrder[p];
                List<PlacedElement> pageNodes = byPage[pageIndex];
                if (pageNodes.Count < 2) continue;

                // Rank by Y descending. The document-order index is the tie-breaker, which makes
                // the comparison a total order (so an unstable sort is still deterministic) and
                // means elements sharing a baseline keep their tree order.
                var order = new int[pageNodes.Count];
                for (int i = 0; i < order.Length; i++) order[i] = i;

                var nodes = pageNodes;
                Array.Sort(order, (a, b) =>
                {
                    int compare = nodes[b].Position.Y.CompareTo(nodes[a].Position.Y);
                    return compare != 0 ? compare : a.CompareTo(b);
                });

                var ranked = new List<ContentPosition>(order.Length);
                for (int rank = 0; rank < order.Length; rank++) ranked.Add(pageNodes[order[rank]].Position);

                string risk = DescribeLayoutRisk(ranked);
                if (risk != null)
                {
                    if (!riskReported)
                    {
                        riskReported = true;
                        Report("PdfReadingOrderAnalyzer.Analyze",
                            "page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture) +
                            " could not be shown to be a single top-to-bottom column (" + risk +
                            "), so its reading order was not judged and no finding is reported for it", null);
                    }
                    continue;
                }

                var visualPosition = new int[pageNodes.Count];
                for (int rank = 0; rank < order.Length; rank++)
                    visualPosition[order[rank]] = rank;

                for (int treePos = 0; treePos < pageNodes.Count; treePos++)
                {
                    int expected = visualPosition[treePos];
                    if (Math.Abs(expected - treePos) > PositionTolerance)
                        issues.Add(new ReadingOrderIssue(pageNodes[treePos].Role, pageIndex, treePos, expected));
                }
            }

            return issues;
        }

        /// <summary>
        /// Collects every structure element below the root, cycle-safely. The root itself carries
        /// no <c>/S</c> and is dropped; so is any node the walker produced without a role.
        /// </summary>
        private static List<StructNode> CollectElements(PdfDictionary structTreeRoot, PdfDocument doc, PdfObjectIndex index)
        {
            IReadOnlyList<StructNode> all = StructTreeWalker.Collect(structTreeRoot, doc, index, MaxDepth, MaxNodes);

            var elements = new List<StructNode>(all.Count);
            for (int i = 0; i < all.Count; i++)
                if (all[i].Role != null) elements.Add(all[i]);

            return elements;
        }

        private static PdfDictionary ResolveStructTreeRoot(PdfDocument doc, string operation)
        {
            PdfDictionary catalog;
            try
            {
                catalog = doc.Catalog?.Dictionary;
            }
            catch (Exception ex)
            {
                Report(operation, "the document catalog could not be read", ex);
                return null;
            }

            if (catalog == null) return null;

            try
            {
                return PdfStructureTreeEditor.ResolveDict(doc, catalog[PdfName.StructTreeRoot]);
            }
            catch (Exception ex)
            {
                Report(operation, "/StructTreeRoot could not be resolved", ex);
                return null;
            }
        }

        /// <summary>
        /// Extracts the page text layouts used by the <c>/ActualText</c> fallback. A failure here
        /// is <em>not</em> fatal: marked-content positions do not depend on it, so the
        /// analysis continues with the fallback unavailable and says so.
        /// </summary>
        private static IReadOnlyList<PdfPageTextLayout> TryExtractStructuredText(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                return PdfOperations.ExtractStructuredText(ms);
            }
            catch (Exception ex)
            {
                Report("PdfReadingOrderAnalyzer.TryExtractStructuredText",
                    "structured text extraction failed; elements without marked-content identifiers " +
                    "cannot be positioned by their /ActualText", ex);
                return null;
            }
        }

        private static void Report(string operation, string detail, Exception ex)
        {
            _lastError = ex != null ? detail + ": " + ex.Message : detail;
            AccessibilityDiagnostics.Report(operation, detail, ex);
        }

        /// <summary>
        /// Writes the fix as one appended revision and reports whether that revision actually
        /// reached <paramref name="output"/>.
        ///
        /// <para>
        /// Delegates to <see cref="PdfStructureTreeEditor.WriteVerifiedRevision"/> — the assembly's
        /// single write-verification mechanism, shared with <c>PdfAutoTagger</c>,
        /// <c>PdfArtifactMarker</c> and the structure-tree editor — rather than growing a second
        /// one here. On failure the reason is put into <see cref="LastError"/>, which is this
        /// operation's only warning channel: <see cref="ReadingOrderFixResult"/> carries a count
        /// and nothing else.
        /// </para>
        /// </summary>
        private static bool WriteVerifiedFix(
            Stream output, byte[] bytes, PdfDocument doc, PdfObjectIndex index,
            Dictionary<int, PdfObject> modified)
        {
            bool ok = PdfStructureTreeEditor.WriteVerifiedRevision(
                output, bytes, doc, index, modified, NoObjects,
                newInfoObjectNumber: -1,
                operation: "PdfReadingOrderAnalyzer.Fix",
                whatWasPlanned: "the reordered /K arrays",
                failureDetail: out string failureDetail);

            // Report() would re-report to AccessibilityDiagnostics, which WriteVerifiedRevision has
            // already done; this only mirrors the reason into the public per-thread slot.
            if (!ok) _lastError = failureDetail;
            return ok;
        }

        private static ReadingOrderAnalysis NotAnalyzed(string operation, string reason, Exception ex)
        {
            Report(operation, reason, ex);
            return ReadingOrderAnalysis.NotAnalyzed(_lastError);
        }

        // ================================================================
        //  Fix: cycle-guarded traversal and /K permutation
        // ================================================================

        private readonly struct PendingNode
        {
            internal readonly PdfDictionary Dict;
            internal readonly int ObjectNumber;
            internal readonly int Depth;
            internal readonly int InheritedPageIndex;

            internal PendingNode(PdfDictionary dict, int objectNumber, int depth, int inheritedPageIndex)
            {
                Dict               = dict;
                ObjectNumber       = objectNumber;
                Depth              = depth;
                InheritedPageIndex = inheritedPageIndex;
            }
        }

        /// <summary>
        /// One <c>/K</c> entry, with the position it resolved to (if any).
        /// </summary>
        private struct KidEntry
        {
            internal PdfObject Raw;
            internal bool Positioned;
            internal ContentPosition Position;
        }

        /// <summary>
        /// Per-call state for <see cref="ReorderTree"/>: the document's <c>/RoleMap</c>, read once,
        /// and the "already said this" flags that keep one diagnostic per document instead of one
        /// per array.
        /// </summary>
        private sealed class FixState
        {
            internal readonly Dictionary<string, string> RoleMap;

            /// <summary>
            /// Carried so the <c>/S</c> read in <c>PermuteKids</c> can RESOLVE. See
            /// <see cref="StructTreeWalker.GetRole"/> for why an unresolved <c>/S</c> is not merely
            /// imprecise: a null role is the sentinel for "this is the <c>/StructTreeRoot</c>".
            /// </summary>
            internal readonly PdfDocument Doc;

            internal bool SemanticRoleReported;
            internal bool LayoutRiskReported;

            internal FixState(Dictionary<string, string> roleMap, PdfDocument doc)
            {
                RoleMap = roleMap ?? new Dictionary<string, string>(StringComparer.Ordinal);
                Doc = doc;
            }
        }

        /// <summary>
        /// Walks the structure tree iteratively, permuting each <c>/K</c> array it can place.
        ///
        /// <para>
        /// <b>Cycle safety.</b> The traversal keeps a global visited set (object numbers, plus
        /// reference identity for direct dictionaries) exactly as <see cref="StructTreeWalker"/>
        /// does, so every element is expanded at most once: a <c>/K</c> cycle with branching
        /// factor 2 costs O(nodes), not 2^depth. It is iterative rather than recursive, so a deep
        /// tree cannot exhaust the CLR stack, and it is additionally bounded by
        /// <see cref="MaxDepth"/> and <see cref="MaxNodes"/>. The walker itself cannot be used
        /// here because this pass mutates the <c>/K</c> arrays it is traversing.
        /// </para>
        ///
        /// <para>
        /// <b>And by the walker's edge ceiling</b>, <see cref="PdfLimits.MaxStructTreeEdges"/>:
        /// every <c>/K</c> slot examined costs one, whether or not it becomes a node. The visited
        /// sets bound how often an element is expanded, not the cost of expanding one, so half a
        /// million elements naming one shared half-million-entry <c>/K</c> array were each
        /// classified over it — with up to <see cref="MaxExtentProbes"/> probes a slot. A node
        /// whose slots would pass the ceiling ends the pass, reported once; the rest of the tree
        /// keeps its order.
        /// </para>
        /// </summary>
        /// <returns>The number of <c>/K</c> slots whose occupant changed.</returns>
        private static int ReorderTree(
            PdfDictionary structTreeRoot,
            PdfDocument doc,
            PdfObjectIndex index,
            PositionResolver resolver,
            Dictionary<int, PdfObject> modified,
            FixState state,
            int maxNodes)
        {
            int moved = 0;
            int visited = 0;
            long edgesLeft = PdfLimits.MaxStructTreeEdges;
            bool nodeCapReported = false;
            bool depthCapReported = false;

            var visitedNumbers = new HashSet<int>();
            var visitedDirect  = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);
            var stack          = new Stack<PendingNode>();

            int rootNumber = index.GetObjectNumber(structTreeRoot);
            if (rootNumber > 0) visitedNumbers.Add(rootNumber); else visitedDirect.Add(structTreeRoot);
            stack.Push(new PendingNode(structTreeRoot, rootNumber, 0, -1));

            while (stack.Count > 0)
            {
                if (visited >= maxNodes)
                {
                    if (!nodeCapReported)
                    {
                        nodeCapReported = true;
                        Report("PdfReadingOrderAnalyzer.Fix",
                            "the structure tree exceeded the " + maxNodes.ToString(CultureInfo.InvariantCulture) +
                            "-node ceiling; the remainder of the tree was left in its original order", null);
                    }
                    break;
                }

                PendingNode current = stack.Pop();
                visited++;

                int pageIndex = ResolvePageIndex(current.Dict, index, current.InheritedPageIndex);

                // ---- the array we may permute, and the object that owns it ----------
                PdfArray kidsArray = null;
                int arrayOwnerNumber = -1;
                bool ownerIsElement = true;

                PdfObject kObject = current.Dict[PdfName.K];
                if (kObject is PdfArray directArray)
                {
                    kidsArray        = directArray;
                    arrayOwnerNumber = current.ObjectNumber;
                }
                else if (kObject is PdfReference kReference)
                {
                    // /K may be an indirect reference to the kids array; then the array object
                    // itself is what a revision must replace, not the element dictionary.
                    PdfObject resolved = TryResolveObject(doc, kObject,
                        "the indirect /K of a structure element could not be resolved; its order was left unchanged");
                    if (resolved is PdfArray indirectArray)
                    {
                        kidsArray        = indirectArray;
                        arrayOwnerNumber = kReference.ReferenceObjectNumber;
                        ownerIsElement   = false;
                    }
                }

                IReadOnlyList<PdfObject> kids = StructTreeWalker.GetRawKids(current.Dict, doc);

                if (kids.Count > edgesLeft)
                {
                    Report("PdfReadingOrderAnalyzer.Fix",
                        "the structure tree's /K entries exceeded the " +
                        PdfLimits.MaxStructTreeEdges.ToString(CultureInfo.InvariantCulture) +
                        "-entry ceiling (PdfLimits.MaxStructTreeEdges); the remainder of the tree was left in its original order", null);
                    break;
                }
                edgesLeft -= kids.Count;
                ReorderEdgesExamined += kids.Count;

                // ---- classify every entry, and queue the child elements --------------
                var entries = new KidEntry[kids.Count];
                for (int i = 0; i < kids.Count; i++)
                {
                    entries[i].Raw = kids[i];
                    PdfObject kid  = kids[i];
                    if (kid == null) continue;

                    if (kid is PdfNumber mcidNumber)
                    {
                        // A bare integer in /K is a marked-content identifier on this element's page.
                        if (resolver.TryResolveMcid(pageIndex, ToInt(mcidNumber), out ContentPosition mcidPosition))
                        {
                            entries[i].Positioned = true;
                            entries[i].Position   = mcidPosition;
                        }
                        continue;
                    }

                    PdfDictionary childDict = kid as PdfDictionary;
                    int childNumber = -1;

                    if (kid is PdfReference childReference)
                    {
                        childNumber = childReference.ReferenceObjectNumber;
                        childDict = TryResolveObject(doc, kid,
                            "a /K child of a structure element could not be resolved; it was left in place")
                            as PdfDictionary;
                    }

                    if (childDict == null) continue;

                    if (IsStructureElement(childDict, doc))
                    {
                        if (resolver.TryResolve(childDict, pageIndex, out ContentPosition childPosition))
                        {
                            entries[i].Positioned = true;
                            entries[i].Position   = childPosition;
                        }

                        if (current.Depth >= MaxDepth)
                        {
                            if (!depthCapReported)
                            {
                                depthCapReported = true;
                                Report("PdfReadingOrderAnalyzer.Fix",
                                    "the structure tree is deeper than the " + MaxDepth.ToString(CultureInfo.InvariantCulture) +
                                    "-level cap; deeper elements were left in their original order", null);
                            }
                            continue;
                        }

                        bool fresh = childNumber > 0
                            ? visitedNumbers.Add(childNumber)
                            : visitedDirect.Add(childDict);
                        if (!fresh) continue;   // /K cycle or shared subtree: handled at first position

                        stack.Push(new PendingNode(childDict, childNumber, current.Depth + 1, pageIndex));
                        continue;
                    }

                    // /MCR — a marked-content reference with its own optional /Pg.
                    if (resolver.TryResolveMarkedContentReference(childDict, pageIndex, out ContentPosition mcrPosition))
                    {
                        entries[i].Positioned = true;
                        entries[i].Position   = mcrPosition;
                    }
                    // /OBJR and anything else stays pinned.
                }

                if (kidsArray == null || entries.Length < 2) continue;

                moved += PermuteKids(entries, current, arrayOwnerNumber, ownerIsElement, modified, state);
            }

            return moved;
        }

        /// <summary>
        /// <c>/K</c> slots <see cref="ReorderTree"/> has examined on the calling thread: the
        /// evidence that its edge ceiling stops it.
        /// </summary>
        [ThreadStatic] internal static long ReorderEdgesExamined;

        /// <summary>
        /// Re-deals the positionable entries of one <c>/K</c> array into the slots they occupy,
        /// ordered by page then by Y descending, leaving every unpositionable entry exactly where
        /// it was. Returns the number of slots whose occupant changed — the number of entries that
        /// actually moved, not the size of the array.
        ///
        /// <para>
        /// The re-deal happens only when the array's children can be shown to be a single
        /// top-to-bottom column: the parent's role must not be one whose child order is semantic,
        /// and the proposed order must pass <see cref="DescribeLayoutRisk"/>. Both gates are
        /// evaluated <em>after</em> establishing that something would actually move, so a document
        /// that is already in order produces no diagnostics at all.
        /// </para>
        /// </summary>
        private static int PermuteKids(
            KidEntry[] entries,
            PendingNode current,
            int arrayOwnerNumber,
            bool ownerIsElement,
            Dictionary<int, PdfObject> modified,
            FixState state)
        {
            // Slots that take part, in their current order.
            var slots = new List<int>();
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Raw == null)
                {
                    // A parsed /K array never holds a CLR null, so this means the snapshot was
                    // built from something this pass does not understand. Rewriting the array
                    // would drop the entry, so decline rather than guess.
                    Report("PdfReadingOrderAnalyzer.Fix",
                        "a /K array contains an entry that could not be read back; its order was left unchanged", null);
                    return 0;
                }
                if (entries[i].Positioned) slots.Add(i);
            }

            if (slots.Count < 2) return 0;

            var sorted = new int[slots.Count];
            for (int i = 0; i < sorted.Length; i++) sorted[i] = slots[i];

            // Page ascending, then Y descending (PDF Y grows upward), then current order — a total
            // order, so equal baselines never shuffle.
            var byPosition = entries;
            Array.Sort(sorted, (a, b) =>
            {
                int compare = byPosition[a].Position.PageIndex.CompareTo(byPosition[b].Position.PageIndex);
                if (compare != 0) return compare;
                compare = byPosition[b].Position.Y.CompareTo(byPosition[a].Position.Y);
                if (compare != 0) return compare;
                return a.CompareTo(b);
            });

            int movedHere = 0;
            for (int i = 0; i < slots.Count; i++)
                if (sorted[i] != slots[i]) movedHere++;

            if (movedHere == 0) return 0;

            // ---- may this array be re-ordered by Y at all? ----------------------
            //
            // Everything below this point rewrites the document, so the two gates run before it.
            // They are the whole reason this pass is safe: a baseline is a point, and a point
            // cannot distinguish "these blocks are stacked" from "these blocks are side by side".

            // Raw OR mapped, matching the Analyze-side gate: a /RoleMap entry must not be able to
            // turn this gate off. The role NAMED in the report is the canonical one, which is what
            // a reader needs to see; what the gate TESTS is both.
            string rawParentRole = StructTreeWalker.GetRole(current.Dict, state.Doc);
            string parentRole    = CanonicalRole(rawParentRole, state.RoleMap);
            if (IsSemanticChildOrderRole(rawParentRole) || IsSemanticChildOrderRole(parentRole))
            {
                if (!state.SemanticRoleReported)
                {
                    state.SemanticRoleReported = true;
                    Report("PdfReadingOrderAnalyzer.Fix",
                        "the children of a /" + parentRole + " were left in their original order: their sequence " +
                        "is semantic, not visual (a row's cells run left to right, a list item's /Lbl precedes its " +
                        "/LBody on the same baseline), so ranking them by baseline Y would swap columns and break " +
                        "the header/data associations a checker validates", null);
                }
                return 0;
            }

            // Entries are sorted page-major, so each run of one page index is a set of siblings
            // that could be side by side and is judged on its own.
            int runStart = 0;
            for (int i = 1; i <= sorted.Length; i++)
            {
                if (i < sorted.Length &&
                    entries[sorted[i]].Position.PageIndex == entries[sorted[runStart]].Position.PageIndex)
                    continue;

                if (i - runStart >= 2)
                {
                    var run = new List<ContentPosition>(i - runStart);
                    for (int j = runStart; j < i; j++) run.Add(entries[sorted[j]].Position);

                    string risk = DescribeLayoutRisk(run);
                    if (risk != null)
                    {
                        if (!state.LayoutRiskReported)
                        {
                            state.LayoutRiskReported = true;
                            Report("PdfReadingOrderAnalyzer.Fix",
                                "a /K array was left in its original order because its children could not be shown " +
                                "to be a single top-to-bottom column (" + risk + "); re-ordering them by baseline " +
                                "would interleave content that sits side by side, turning a correct reading order " +
                                "into a wrong one", null);
                        }
                        return 0;
                    }
                }

                runStart = i;
            }

            if (arrayOwnerNumber <= 0)
            {
                Report("PdfReadingOrderAnalyzer.Fix",
                    "a /K array is out of visual order but the object that owns it has no object number, " +
                    "so it could not be revised; its order was left unchanged", null);
                return 0;
            }

            var newArray = new PdfArray();
            int next = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Positioned) newArray.Add(entries[sorted[next++]].Raw);
                else                       newArray.Add(entries[i].Raw);
            }

            if (ownerIsElement)
            {
                PdfDictionary clone = PdfStructureTreeEditor.CloneDict(current.Dict);
                clone[PdfName.K] = newArray;
                modified[arrayOwnerNumber] = clone;
            }
            else
            {
                modified[arrayOwnerNumber] = newArray;
            }

            return movedHere;
        }

        // ================================================================
        //  Layout safety: when does baseline Y actually give a reading order?
        // ================================================================

        /// <summary>
        /// Where a structure element's content was measured to be: the ordering key (the baseline
        /// of the first text-showing operator it reaches, which is what the original comparison
        /// used) plus the band that content spans.
        ///
        /// <para>
        /// <b>The extent is measured, and it under-states.</b> <c>Left</c> and <c>Right</c> are the
        /// leftmost and rightmost <em>origins</em> of the show operators inside the element; glyph
        /// advances are not known here (that would need font metrics and the rendering ring), so
        /// the true right edge is at or beyond <c>Right</c>. Every use below is therefore framed so
        /// that under-stating the width makes the pass <em>decline</em> to re-order, never accept.
        /// </para>
        /// </summary>
        private readonly struct ContentPosition
        {
            /// <summary>Zero-based page index the content was found on.</summary>
            internal readonly int PageIndex;

            /// <summary>Ordering key: baseline Y of the first text-showing operator.</summary>
            internal readonly float Y;

            /// <summary>Highest baseline measured for this element.</summary>
            internal readonly float Top;

            /// <summary>Lowest baseline measured for this element.</summary>
            internal readonly float Bottom;

            /// <summary>Leftmost show origin measured for this element.</summary>
            internal readonly float Left;

            /// <summary>Rightmost show origin measured — an under-estimate of the right edge.</summary>
            internal readonly float Right;

            /// <summary>
            /// False for a default-constructed value, i.e. an element that was never placed. No
            /// geometric claim may be made from such a value.
            /// </summary>
            internal readonly bool HasExtent;

            internal ContentPosition(int pageIndex, float y, float top, float bottom, float left, float right)
            {
                PageIndex = pageIndex;
                Y         = y;
                Top       = top;
                Bottom    = bottom;
                Left      = left;
                Right     = right;
                HasExtent = true;
            }

            internal ContentPosition(int pageIndex, MeasuredBox box)
                : this(pageIndex, box.FirstY, box.Top, box.Bottom, box.Left, box.Right)
            {
            }

            /// <summary>
            /// Widens this position to also cover <paramref name="other"/>, keeping this one's page
            /// and ordering key. Used when an element reaches several marked-content sequences: its
            /// position is still where its content starts, but its extent is all of them.
            /// </summary>
            internal ContentPosition Union(ContentPosition other)
            {
                if (!other.HasExtent) return this;
                if (!HasExtent) return other;

                return new ContentPosition(
                    PageIndex, Y,
                    Math.Max(Top, other.Top),
                    Math.Min(Bottom, other.Bottom),
                    Math.Min(Left, other.Left),
                    Math.Max(Right, other.Right));
            }
        }

        /// <summary>
        /// The band a marked-content sequence (or an extracted text line) occupies: the first
        /// baseline seen, the highest and lowest baseline, and the leftmost and rightmost show
        /// origin.
        /// </summary>
        private struct MeasuredBox
        {
            internal float FirstY;
            internal float Top;
            internal float Bottom;
            internal float Left;
            internal float Right;

            internal static MeasuredBox At(float x, float y) => new MeasuredBox
            {
                FirstY = y, Top = y, Bottom = y, Left = x, Right = x,
            };

            internal static MeasuredBox Span(float y, float left, float right) => new MeasuredBox
            {
                FirstY = y, Top = y, Bottom = y,
                Left   = Math.Min(left, right),
                Right  = Math.Max(left, right),
            };

            internal MeasuredBox Extend(float x, float y)
            {
                if (y > Top)    Top    = y;
                if (y < Bottom) Bottom = y;
                if (x < Left)   Left   = x;
                if (x > Right)  Right  = x;
                return this;
            }
        }

        /// <summary>
        /// Whether the children of an element with this (<c>/RoleMap</c>-resolved) role are ordered
        /// by <em>meaning</em> rather than by where they sit on the page.
        ///
        /// <para>
        /// A <c>TR</c>'s cells run left to right; sorting them by baseline reverses columns as soon
        /// as two cells do not share an exact baseline — a vertically centred cell, a cell set in a
        /// larger face, a two-line cell beside a one-line cell — and every header/data association
        /// a PDF/UA checker validates goes with it. An <c>LI</c>'s <c>Lbl</c> precedes its
        /// <c>LBody</c> on the <em>same</em> baseline, so a hair of difference is enough to
        /// announce the text before its bullet. A list's or a table's own children are stacked, but
        /// their grouping (<c>THead</c> before <c>TBody</c> before <c>TFoot</c>) is likewise
        /// semantic. None of that is something a Y coordinate can decide, so this pass does not try.
        /// </para>
        /// </summary>
        private static bool IsSemanticChildOrderRole(string role)
        {
            switch (role)
            {
                case StructureTypes.Table:
                case StructureTypes.TableHead:
                case StructureTypes.TableBody:
                case StructureTypes.TableFoot:
                case StructureTypes.TableRow:
                case StructureTypes.List:
                case StructureTypes.ListItem:
                case StructureTypes.Toc:
                case StructureTypes.Toci:
                case StructureTypes.Ruby:
                case StructureTypes.Warichu:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Decides whether a set of positions on one page may be ranked top-to-bottom, and returns
        /// <c>null</c> when it may. Otherwise the return value says, in one clause fit to embed in
        /// a diagnostic, why the ranking would be an invention.
        /// </summary>
        /// <param name="ranked">
        /// The positions <b>in the order the ranking would produce</b> (Y descending), all on the
        /// same page.
        /// </param>
        /// <remarks>
        /// Three things are checked, and each rejects a layout a Y-only ranking would accept:
        /// <list type="number">
        ///   <item><description><b>Everything must have been measured.</b> Without an extent there
        ///     is nothing to reason about.</description></item>
        ///   <item><description><b>Elements that share vertical space must share a baseline.</b>
        ///     Overlapping bands mean the elements sit beside one another, and "which comes first"
        ///     is then a horizontal question. Ranking them by Y is harmless only while their
        ///     ordering keys agree — then the tie-break keeps document order and no claim is made.
        ///     As soon as two side-by-side elements differ in baseline, the ranking separates them
        ///     on evidence that does not support it: that is exactly the vertically centred table
        ///     cell, the two-line cell beside a one-line cell, and two columns whose paragraphs run
        ///     alongside each other at slightly different heights. Inline runs on one line — a
        ///     <c>Span</c> or <c>Link</c> inside a paragraph — share a baseline exactly and are
        ///     therefore <em>not</em> penalised. Bands are grouped with an interval-merge sweep, so
        ///     this is O(n log n); a pairwise scan would be O(n²) on a page that may hold hundreds
        ///     of thousands of elements.</description></item>
        ///   <item><description><b>No column signature.</b> Even when no two bands overlap — every
        ///     block is a single line, say — a multi-column page interleaves: the ranking walks
        ///     left column, right column, left column. That shows up as the sequence stepping back
        ///     to the right <em>after</em> it has moved left. One leftward step alone is ordinary
        ///     (a centred heading above a left-aligned paragraph), so only the return trip is
        ///     treated as evidence.</description></item>
        /// </list>
        /// Each test can only be triggered by evidence, so a genuinely single-column page still
        /// passes; and because the measured width under-states, every borderline case falls on the
        /// "decline" side.
        /// </remarks>
        private static string DescribeLayoutRisk(List<ContentPosition> ranked)
        {
            if (ranked == null || ranked.Count < 2) return null;

            for (int i = 0; i < ranked.Count; i++)
            {
                if (!ranked[i].HasExtent)
                    return "the horizontal extent of at least one element could not be measured";
            }

            // (2) elements that share vertical space, ranked apart by their baselines.
            var byTop = new List<ContentPosition>(ranked);
            byTop.Sort((a, b) => b.Top.CompareTo(a.Top));

            float clusterBottom = 0f;   // lowest Bottom reached by the group being accumulated
            float clusterMinY   = 0f;
            float clusterMaxY   = 0f;

            for (int i = 0; i < byTop.Count; i++)
            {
                ContentPosition band = byTop[i];

                // Sorted by Top descending, so this band starts at or below every band already in
                // the group: it joins the group exactly when it reaches up to the group's floor.
                if (i == 0 || band.Top < clusterBottom - BandTolerance)
                {
                    clusterBottom = band.Bottom;
                    clusterMinY   = band.Y;
                    clusterMaxY   = band.Y;
                    continue;
                }

                if (band.Bottom < clusterBottom) clusterBottom = band.Bottom;
                if (band.Y < clusterMinY)        clusterMinY   = band.Y;
                if (band.Y > clusterMaxY)        clusterMaxY   = band.Y;

                if (clusterMaxY - clusterMinY > BandTolerance)
                    return "two of its elements share vertical space on the page yet sit on different " +
                           "baselines, so they are beside each other and their order is a horizontal " +
                           "question that a baseline cannot answer";
            }

            // (3) the column signature.
            bool movedLeft = false;
            for (int i = 1; i < ranked.Count; i++)
            {
                ContentPosition previous = ranked[i - 1];
                ContentPosition current  = ranked[i];

                if (current.Right < previous.Left - ColumnGapTolerance)
                {
                    movedLeft = true;
                }
                else if (movedLeft && current.Left > previous.Right + ColumnGapTolerance)
                {
                    return "the top-to-bottom order steps back to the right after having moved left, " +
                           "which is the signature of a multi-column layout — content there is read " +
                           "column by column, not by baseline";
                }
            }

            return null;
        }

        // ================================================================
        //  /RoleMap
        // ================================================================

        /// <summary>
        /// Reads <c>/RoleMap</c> so a producer's own role — <c>/TableRow</c> mapped to <c>/TR</c> —
        /// is judged as what a conforming reader would see. Never throws and never returns null.
        /// </summary>
        private static Dictionary<string, string> BuildRoleMap(PdfDocument doc, PdfDictionary structTreeRoot, string operation)
        {
            try
            {
                // One reader, in Core.Document.PdfRoleMap. The try/catch stays here rather than
                // moving into it: this class never throws out of Analyze/Fix, and the shared
                // reader has no diagnostics sink it could report into from Core.
                return PdfRoleMap.Build(structTreeRoot, o => doc?.ResolveObject(o));
            }
            catch (Exception ex)
            {
                Report(operation,
                    "/RoleMap could not be resolved, so an element whose role is a producer's own name " +
                    "is not recognised as the standard type it stands for", ex);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Follows a role through <c>/RoleMap</c> to the standard type it stands for. One
        /// implementation, in <see cref="PdfRoleMap.Resolve"/>.
        /// </summary>
        /// <remarks>
        /// A copy with a hop cap and <b>no visited set</b> would return, on a three-node cycle,
        /// whichever role 16 hops landed on rather than the one the chain re-enters.
        /// </remarks>
        private static string CanonicalRole(string role, Dictionary<string, string> roleMap)
            => PdfRoleMap.Resolve(role, roleMap);

        /// <summary>
        /// Whether a resolved <c>/K</c> child is a structure element (and not an <c>/MCR</c> /
        /// <c>/OBJR</c> leaf).
        /// </summary>
        /// <remarks>
        /// One ladder, <c>Base.Operations.StructKidClassifier</c>'s — shared with
        /// <see cref="StructTreeWalker"/> rather than, as the comment that stood here said,
        /// "mirroring" it. <b>A doc comment asserting that two implementations agree is not the
        /// agreement</b>, and this pair is the third in the tree to carry that claim.
        /// </remarks>
        private static bool IsStructureElement(PdfDictionary dict, PdfDocument doc)
            => StructKidClassifier.Classify(dict, doc) == StructKidKind.Element;

        private static int ResolvePageIndex(PdfDictionary dict, PdfObjectIndex index, int inherited)
        {
            if (dict == null || index == null) return inherited;

            PdfObject pg = dict[PdfName.Pg];
            if (pg is PdfReference pageRef)
            {
                int pageIndex = index.GetPageIndex(pageRef.ReferenceObjectNumber);
                if (pageIndex >= 0) return pageIndex;
            }
            else if (pg is PdfDictionary pageDict)
            {
                int pageObjectNumber = index.GetObjectNumber(pageDict);
                if (pageObjectNumber > 0)
                {
                    int pageIndex = index.GetPageIndex(pageObjectNumber);
                    if (pageIndex >= 0) return pageIndex;
                }
            }

            // /Pg is set once on a container by most producers and left off the leaves.
            return inherited;
        }

        private static PdfObject TryResolveObject(PdfDocument doc, PdfObject reference, string detail)
        {
            try
            {
                return doc?.ResolveObject(reference);
            }
            catch (Exception ex)
            {
                Report("PdfReadingOrderAnalyzer.Fix", detail, ex);
                return null;
            }
        }

        private static int ToInt(PdfNumber number)
        {
            double value = number.DoubleValue;
            if (double.IsNaN(value) || value < 0d || value > int.MaxValue) return -1;
            return SaturatingCast.ToInt(value);
        }

        // ================================================================
        //  Position resolution
        // ================================================================

        /// <summary>
        /// Answers "where on the page is this structure element?", preferring marked content and
        /// falling back to the historical <c>/ActualText</c> prefix match.
        ///
        /// <para>
        /// Everything is cached per page: a page's content stream is scanned <b>at most once</b>
        /// per operation no matter how many elements point into it, and both lookups are O(1)
        /// dictionary probes.
        /// </para>
        /// </summary>
        private sealed class PositionResolver
        {
            private readonly PdfDocument _doc;
            private readonly PdfObjectIndex _index;
            private readonly IReadOnlyList<PdfPageTextLayout> _textPages;
            private readonly Dictionary<int, PageMap> _pages = new Dictionary<int, PageMap>();
            private readonly int _pageCount;
            private bool _xObjectReported;

            internal PositionResolver(PdfDocument doc, PdfObjectIndex index, IReadOnlyList<PdfPageTextLayout> textPages)
            {
                _doc       = doc;
                _index     = index;
                _textPages = textPages;

                int count = index != null ? index.PageCount : 0;
                if (count == 0 && textPages != null) count = textPages.Count;
                _pageCount = count;
            }

            private sealed class PageMap
            {
                internal Dictionary<int, MeasuredBox> Mcid;
                internal Dictionary<string, MeasuredBox> TextPrefix;
            }

            /// <summary>
            /// Resolves an element's position. Returns false when the element cannot be placed —
            /// callers must then leave it out of any ordering comparison rather than treating it
            /// as being at the origin.
            ///
            /// <para>
            /// The <em>ordering key</em> is the first marked-content kid that resolves, exactly as
            /// before. The <em>extent</em> unions in the element's remaining marked content on that
            /// same page, because a block's band is all of its content, not just its first line —
            /// and the band is what tells a stack apart from two columns. At most
            /// <see cref="MaxExtentProbes"/> entries are examined, so an element pointing at a huge
            /// shared <c>/K</c> array stays cheap; a truncated measurement only narrows the band.
            /// </para>
            /// </summary>
            internal bool TryResolve(PdfDictionary element, int inheritedPageIndex, out ContentPosition position)
            {
                position = default;
                if (element == null) return false;

                int ownPage = NormalizePage(inheritedPageIndex);

                // 1. marked content — the element's own /K, never its children's.
                IReadOnlyList<PdfObject> kids = StructTreeWalker.GetRawKids(element, _doc);
                bool found = false;
                int probes = kids.Count < MaxExtentProbes ? kids.Count : MaxExtentProbes;

                for (int i = 0; i < probes; i++)
                {
                    if (!TryResolveKid(kids[i], ownPage, out ContentPosition kidPosition)) continue;

                    if (!found)
                    {
                        position = kidPosition;
                        found    = true;
                    }
                    else if (kidPosition.PageIndex == position.PageIndex)
                    {
                        // Content on another page cannot widen this element's band on this one.
                        position = position.Union(kidPosition);
                    }
                }

                if (found) return true;

                // 2. /ActualText (then /Alt) prefix match against extracted text.
                if (ownPage >= 0)
                {
                    string text = GetText(_doc?.ResolveObject(element[PdfName.ActualText]) ?? element[PdfName.ActualText])
                               ?? GetText(_doc?.ResolveObject(element[PdfName.Alt]) ?? element[PdfName.Alt]);
                    if (!string.IsNullOrEmpty(text) && TryLookupText(ownPage, text, out MeasuredBox box))
                    {
                        position = new ContentPosition(ownPage, box);
                        return true;
                    }
                }

                position = default;
                return false;
            }

            /// <summary>Resolves a bare <c>/MCID</c> integer against a page's marked-content map.</summary>
            internal bool TryResolveMcid(int pageIndexIn, int mcid, out ContentPosition position)
            {
                position = default;

                int page = NormalizePage(pageIndexIn);
                if (page < 0 || mcid < 0) return false;

                Dictionary<int, MeasuredBox> map = GetMcidMap(page);
                if (!map.TryGetValue(mcid, out MeasuredBox box)) return false;

                position = new ContentPosition(page, box);
                return true;
            }

            /// <summary>
            /// Resolves an <c>/MCR</c> dictionary: <c>/MCID</c> plus an optional <c>/Pg</c> that
            /// overrides the containing element's page.
            /// </summary>
            internal bool TryResolveMarkedContentReference(PdfDictionary mcr, int inheritedPageIndex,
                out ContentPosition position)
            {
                position = default;
                if (mcr == null) return false;

                // Resolved. A `false` here reaches `if (!resolver.TryResolve(...)) continue;` at
                // the caller, so the element is dropped from the comparison — it is counted in
                // ElementsTotal but not ElementsPlaced, which is exactly what IsPartial reports.
                // So the caller is not lied to; it is told less, and only if it asks.
                if (!((_doc?.ResolveObject(mcr[PdfName.MCID]) ?? mcr[PdfName.MCID]) is PdfNumber mcidNumber)) return false;

                int page = ResolvePageIndex(mcr, _index, NormalizePage(inheritedPageIndex));
                return TryResolveMcid(page, ToInt(mcidNumber), out position);
            }

            private bool TryResolveKid(PdfObject kid, int ownPage, out ContentPosition position)
            {
                position = default;
                if (kid == null) return false;

                // Resolve BEFORE the type tests, not after the number test. §14.7.2 Table 323 lets
                // /K be an integer MCID or a dictionary, and §7.3.10 lets either be written
                // indirectly — so resolving only into `PdfDictionary` accepted `/K 9 0 R → <<...>>`
                // while dropping `/K 9 0 R → 0`, which is the commoner of the two.
                if (kid is PdfReference)
                {
                    try
                    {
                        kid = _doc?.ResolveObject(kid) ?? kid;
                    }
                    catch (Exception ex)
                    {
                        Report("PdfReadingOrderAnalyzer.TryResolveKid",
                            "a /K child could not be resolved while locating a structure element", ex);
                        return false;
                    }
                }

                if (kid is PdfNumber number)
                    return TryResolveMcid(ownPage, ToInt(number), out position);

                PdfDictionary dict = kid as PdfDictionary;
                if (dict == null) return false;

                // One ladder, Base.Operations.StructKidClassifier's. An object reference names an
                // annotation, never text; a nested structure element is positioned in its own
                // right, because a parent is never placed by its children — that is what makes the
                // comparison see each piece of content exactly once.
                StructKidKind kind = StructKidClassifier.Classify(dict, _doc);
                if (kind == StructKidKind.ObjectReference || kind == StructKidKind.Element) return false;

                return TryResolveMarkedContentReference(dict, ownPage, out position);
            }

            /// <summary>
            /// <c>/Pg</c> is optional, and a single-page document leaves no ambiguity about which
            /// page an element is on — so a missing page index is only fatal for multi-page files.
            /// </summary>
            private int NormalizePage(int pageIndex)
            {
                if (pageIndex >= 0) return pageIndex;
                return _pageCount == 1 ? 0 : -1;
            }

            private PageMap GetPage(int pageIndex)
            {
                if (!_pages.TryGetValue(pageIndex, out PageMap map))
                {
                    map = new PageMap();
                    _pages[pageIndex] = map;
                }
                return map;
            }

            private Dictionary<int, MeasuredBox> GetMcidMap(int pageIndex)
            {
                PageMap map = GetPage(pageIndex);
                if (map.Mcid == null) map.Mcid = BuildMcidMap(pageIndex);
                return map.Mcid;
            }

            private bool TryLookupText(int pageIndex, string text, out MeasuredBox box)
            {
                box = default;

                string trimmed = text.Trim();
                if (trimmed.Length == 0) return false;

                PageMap map = GetPage(pageIndex);
                if (map.TextPrefix == null) map.TextPrefix = BuildTextPrefixMap(pageIndex);

                string snippet = trimmed.Length > TextPrefixLength
                    ? trimmed.Substring(0, TextPrefixLength)
                    : trimmed;

                return map.TextPrefix.TryGetValue(snippet, out box);
            }

            /// <summary>
            /// Indexes every prefix (up to <see cref="TextPrefixLength"/> characters) of every
            /// extracted line on the page, first line wins. That reproduces the old
            /// "first line whose text starts with this snippet" scan exactly, while turning the
            /// per-element cost from O(lines) into an O(1) probe.
            ///
            /// <para>
            /// The extracted line carries a real width, so an element placed this way gets a true
            /// horizontal extent rather than the origin-only under-estimate the content-stream scan
            /// can produce.
            /// </para>
            /// </summary>
            private Dictionary<string, MeasuredBox> BuildTextPrefixMap(int pageIndex)
            {
                var map = new Dictionary<string, MeasuredBox>(StringComparer.OrdinalIgnoreCase);

                if (_textPages == null || pageIndex < 0 || pageIndex >= _textPages.Count) return map;

                PdfPageTextLayout layout = _textPages[pageIndex];
                IReadOnlyList<PdfTextLine> lines = layout?.Lines;
                if (lines == null) return map;

                for (int i = 0; i < lines.Count; i++)
                {
                    PdfTextLine line = lines[i];
                    if (line == null || string.IsNullOrWhiteSpace(line.Text)) continue;

                    float width = line.Width;
                    if (float.IsNaN(width) || float.IsInfinity(width) || width < 0f) width = 0f;

                    float left = line.X;
                    if (float.IsNaN(left) || float.IsInfinity(left)) continue;

                    float y = line.Y;
                    if (float.IsNaN(y) || float.IsInfinity(y)) continue;

                    MeasuredBox box = MeasuredBox.Span(y, left, left + width);

                    string text = line.Text.Trim();
                    int max = Math.Min(TextPrefixLength, text.Length);
                    for (int length = 1; length <= max; length++)
                    {
                        string key = text.Substring(0, length);
                        if (!map.ContainsKey(key)) map[key] = box;
                    }
                }

                return map;
            }

            // ------------------------------------------------------------
            //  Content-stream scan: MCID → baseline Y
            // ------------------------------------------------------------

            private Dictionary<int, MeasuredBox> BuildMcidMap(int pageIndex)
            {
                var map = new Dictionary<int, MeasuredBox>();

                PdfPage page = null;
                try
                {
                    IReadOnlyList<PdfPage> pages = _doc?.Pages;
                    if (pages != null && pageIndex >= 0 && pageIndex < pages.Count) page = pages[pageIndex];
                }
                catch (Exception ex)
                {
                    Report("PdfReadingOrderAnalyzer.BuildMcidMap",
                        "the page tree could not be read, so marked-content positions are unavailable", ex);
                    return map;
                }

                if (page == null) return map;

                byte[] content = ContentStreamTagger.ConcatenatePageContent(_doc, page.Dictionary);
                if (content == null)
                {
                    // ContentStreamTagger reports through its own [ThreadStatic] slot; bridge it so
                    // a host subscribed to AccessibilityDiagnostics sees why the page went silent.
                    Report("PdfReadingOrderAnalyzer.BuildMcidMap",
                        "page " + PageLabel(pageIndex) + " content stream could not be decoded, so its " +
                        "marked-content positions are unavailable (" + (ContentStreamTagger.LastTagError ?? "no detail") + ")", null);
                    return map;
                }

                if (content.Length == 0) return map;

                ContentScanResult scan = ContentStreamScanner.ScanDetailed(content);
                if (!scan.IsSafe)
                {
                    // Two different outcomes hide behind !IsSafe, and reporting them alike would
                    // overstate what this page yielded. A *malformed* stream still hands back the
                    // tokens found before the failure, and those positions are valid — this pass
                    // only reads them — so the map is partial. A *resource-ceiling* refusal
                    // (ContentScanResult.LimitExceeded) returns no tokens at all by design, so the
                    // page contributes nothing and the map is empty, not partial.
                    Report("PdfReadingOrderAnalyzer.BuildMcidMap",
                        scan.LimitExceeded
                            ? "page " + PageLabel(pageIndex) + " content stream was refused by the scanner (" +
                              (scan.Error ?? "unknown reason") + "); no marked-content positions are available " +
                              "for this page at all"
                            : "page " + PageLabel(pageIndex) + " content stream could not be scanned to the end (" +
                              (scan.Error ?? "unknown reason") + "); marked-content positions after that point are unavailable",
                        null);
                }

                if (scan.XObjectInvocations > 0 && !_xObjectReported)
                {
                    _xObjectReported = true;
                    Report("PdfReadingOrderAnalyzer.BuildMcidMap",
                        "page " + PageLabel(pageIndex) + " invokes Form XObjects; text drawn inside them lives in a " +
                        "different content stream, so structure elements pointing at it fall back to /ActualText matching", null);
                }

                ScanTokens(scan.Tokens, ResolveProperties(page), map);
                return map;
            }

            private PdfDictionary ResolveProperties(PdfPage page)
            {
                try
                {
                    PdfDictionary resources = page.Resources;
                    PdfObject properties = resources?[PdfName.Properties];
                    if (properties == null) return null;

                    return properties as PdfDictionary ?? _doc?.ResolveObject(properties) as PdfDictionary;
                }
                catch (Exception ex)
                {
                    Report("PdfReadingOrderAnalyzer.ResolveProperties",
                        "the page's /Resources /Properties could not be read; marked content whose property list is " +
                        "referenced by name cannot be resolved", ex);
                    return null;
                }
            }

            /// <summary>
            /// Walks the token stream, tracking exactly the state a baseline needs — the CTM stack
            /// (<c>q</c>/<c>Q</c>/<c>cm</c>), the text and text-line matrices, and leading and
            /// rise — and records, for every MCID currently open, the baseline Y of the first
            /// text-showing operator inside it.
            ///
            /// <para>
            /// The arithmetic deliberately mirrors <see cref="ContentStreamTagger"/> operator for
            /// operator, so a page tagged by this library and a page tagged elsewhere are measured
            /// the same way. It cannot call into that type: its state machine is private, and its
            /// public entry point refuses any page that already carries marked content — which is
            /// precisely the population this analyzer exists to measure.
            /// </para>
            /// </summary>
            private void ScanTokens(IReadOnlyList<ContentToken> tokens, PdfDictionary properties, Dictionary<int, MeasuredBox> map)
            {
                if (tokens == null || tokens.Count == 0) return;

                var stateStack = new List<GraphicsState>();
                var gs = new GraphicsState { Ctm = AffineMatrix.Identity };
                AffineMatrix tm  = AffineMatrix.Identity;
                AffineMatrix tlm = AffineMatrix.Identity;

                var marks = new List<int>();
                int stateOverflow = 0;
                int markOverflow  = 0;
                bool inTextObject = false;
                bool depthReported = false;
                var numbers = new float[6];

                for (int i = 0; i < tokens.Count; i++)
                {
                    ContentToken token = tokens[i];
                    switch (token.Operator)
                    {
                        case PdfOperators.Save:
                            if (stateStack.Count < MaxContentStackDepth) stateStack.Add(gs);
                            else { stateOverflow++; ReportDepthOnce(ref depthReported); }
                            break;

                        case PdfOperators.Restore:
                            if (stateOverflow > 0) stateOverflow--;
                            else if (stateStack.Count > 0)
                            {
                                gs = stateStack[stateStack.Count - 1];
                                stateStack.RemoveAt(stateStack.Count - 1);
                            }
                            break;

                        case PdfOperators.ConcatMatrix:
                            if (TryGetTrailingNumbers(token.Operands, 6, numbers))
                                gs.Ctm = AffineMatrix.Multiply(
                                    new AffineMatrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]),
                                    gs.Ctm);
                            break;

                        case PdfOperators.BeginText:
                            // 9.4.1: BT resets Tm and Tlm to identity and nothing else — text state
                            // (font size, leading, rise) lives in the graphics state.
                            TextPositioning.BeginText(ref tm, ref tlm);
                            inTextObject = true;
                            break;

                        case PdfOperators.EndText:
                            inTextObject = false;
                            break;

                        case PdfOperators.SetTextRise:
                            // Rise stays here: it lands in this walk's own state and takes part in
                            // no algebra, so TextPositioning does not handle it.
                            if (TryGetTrailingNumbers(token.Operands, 1, numbers)) gs.Rise = numbers[0];
                            break;

                        // TL, Tm, Td, TD and T* are §9.4.2 algebra, shared with ContentStreamTagger
                        // through Internal.TextPositioning. The two walks keep their own policies --
                        // this one TOLERATES a malformed page and counts, because it is only
                        // measuring bands; the tagger refuses, because it is splicing byte offsets.
                        case PdfOperators.SetLeading:
                        case PdfOperators.SetTextMatrix:
                        case PdfOperators.TextMove:
                        case PdfOperators.TextMoveSetLeading:
                        case PdfOperators.NextLine:
                            TextPositioning.TryApply(token.Operator, token.Operands, numbers,
                                                     ref tm, ref tlm, ref gs.Leading);
                            break;

                        case PdfOperators.ShowText:
                        case PdfOperators.ShowTextArray:
                            if (inTextObject) Record(marks, map, tm, gs);
                            break;

                        case PdfOperators.NextLineShowText:
                        case PdfOperators.NextLineSetSpacingShowText:
                            // Both advance to the next line before showing.
                            TextPositioning.NextLine(ref tm, ref tlm, gs.Leading);
                            if (inTextObject) Record(marks, map, tm, gs);
                            break;

                        case PdfOperators.BeginMarkedContentProps:
                            if (marks.Count < MaxContentStackDepth) marks.Add(ExtractMcid(token.Operands, properties));
                            else { markOverflow++; ReportDepthOnce(ref depthReported); }
                            break;

                        case PdfOperators.BeginMarkedContent:
                            if (marks.Count < MaxContentStackDepth) marks.Add(-1);
                            else { markOverflow++; ReportDepthOnce(ref depthReported); }
                            break;

                        case PdfOperators.EndMarkedContent:
                            if (markOverflow > 0) markOverflow--;
                            else if (marks.Count > 0) marks.RemoveAt(marks.Count - 1);
                            break;
                    }
                }
            }

            private void ReportDepthOnce(ref bool reported)
            {
                if (reported) return;
                reported = true;
                Report("PdfReadingOrderAnalyzer.ScanTokens",
                    "a page content stream nests q/Q or BDC/EMC deeper than " +
                    MaxContentStackDepth.ToString(CultureInfo.InvariantCulture) +
                    " levels; state beyond that depth is not tracked and some marked-content positions may be missing", null);
            }

            /// <summary>
            /// Records this text-showing operator's origin for every MCID currently open. The first
            /// show inside a sequence fixes the sequence's ordering baseline, so an element's
            /// position is where its content <em>starts</em>; every later show widens the band the
            /// sequence occupies, which is what the single-column proof reads. Enclosing sequences
            /// are recorded too, because a <c>/P</c> that wraps a <c>/Span</c> shows no text of its
            /// own but still begins — and ends — where its spans do.
            /// </summary>
            private static void Record(List<int> marks, Dictionary<int, MeasuredBox> map, AffineMatrix tm, GraphicsState gs)
            {
                if (marks.Count == 0) return;

                // Trm = [Tfs·Th 0, 0 Tfs, 0 Trise] × Tm × CTM; the glyph origin is at text-space
                // (0, Trise), so its device position is (Trise·M.C + M.E, Trise·M.D + M.F) where
                // M = Tm × CTM. The X is the origin only: the advance of the glyphs about to be
                // shown needs font metrics, which are the rendering ring's business, so the
                // measured right edge is a lower bound and every consumer treats it as one.
                AffineMatrix m = AffineMatrix.Multiply(tm, gs.Ctm);
                float y = (gs.Rise * m.D) + m.F;
                float x = (gs.Rise * m.C) + m.E;
                if (float.IsNaN(y) || float.IsInfinity(y)) return;
                if (float.IsNaN(x) || float.IsInfinity(x)) return;

                for (int i = 0; i < marks.Count; i++)
                {
                    int mcid = marks[i];
                    if (mcid < 0) continue;

                    if (map.TryGetValue(mcid, out MeasuredBox box)) map[mcid] = box.Extend(x, y);
                    else                                            map[mcid] = MeasuredBox.At(x, y);
                }
            }

            /// <summary>
            /// Reads the <c>/MCID</c> out of a <c>BDC</c> property list, which is either an inline
            /// dictionary or a name that indexes the page's <c>/Resources /Properties</c>.
            /// Returns -1 for a sequence that carries no MCID (an artifact, or a plain <c>BMC</c>).
            /// </summary>
            private PdfDictionary ResolvePropertyList(IReadOnlyList<PdfObject> operands, PdfDictionary properties)
            {
                if (operands == null || operands.Count == 0) return null;

                PdfObject last = operands[operands.Count - 1];
                if (last is PdfDictionary inline) return inline;

                if (last is PdfName name && properties != null)
                {
                    try
                    {
                        return properties[name] as PdfDictionary
                               ?? _doc?.ResolveObject(properties[name]) as PdfDictionary;
                    }
                    catch (Exception ex)
                    {
                        Report("PdfReadingOrderAnalyzer.ResolvePropertyList",
                            "a BDC property list named /" + name.Value + " could not be resolved; that marked-content " +
                            "sequence contributes no position", ex);
                        return null;
                    }
                }

                return null;
            }

            private int ExtractMcid(IReadOnlyList<PdfObject> operands, PdfDictionary properties)
            {
                PdfDictionary propertyList = ResolvePropertyList(operands, properties);
                if (propertyList == null) return -1;

                return (_doc?.ResolveObject(propertyList[PdfName.MCID]) ?? propertyList[PdfName.MCID]) is PdfNumber number ? ToInt(number) : -1;
            }

            private static string PageLabel(int pageIndex)
                => (pageIndex + 1).ToString(CultureInfo.InvariantCulture);

            // One implementation, in Core.Text.ContentOperand.TryTrailingNumbers — the one home of
            // the operand coercions, so no copy drifts under a name of its own.
            private static bool TryGetTrailingNumbers(IReadOnlyList<PdfObject> operands, int count, float[] destination)
                => Core.Text.ContentOperand.TryTrailingNumbers(operands, count, destination);

            private static string GetText(PdfObject value)
            {
                string text = (value as PdfString)?.GetText();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        // ================================================================
        //  Minimal linear algebra
        // ================================================================

        /// <summary>
        /// The slice of graphics state a baseline depends on. Leading and rise live in the
        /// graphics state, not in the text object: <c>2 Ts 14 TL BT … ET</c> is legal, so
        /// resetting them at <c>BT</c> would mis-place every run that relies on it. Font size is
        /// deliberately not tracked — nothing here needs it, only the baseline.
        /// </summary>
        private struct GraphicsState
        {
            internal AffineMatrix Ctm;
            internal float Leading;
            internal float Rise;
        }
    }
}
