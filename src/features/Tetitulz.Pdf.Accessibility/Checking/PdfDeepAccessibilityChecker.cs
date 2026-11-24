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
using Tetitulz.Pdf.Core.IO;
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
    ///   <item>UA31 — Structure element nesting: the parent and child rules ISO 14289-1 7.2 takes
    ///     from ISO 32000-1 Tables 337–340 (TR, THead, TBody, TFoot, TH, TD, LI, LBody, TOCI parents;
    ///     Table, THead/TBody/TFoot, TOC and L children) — <see cref="CheckStructureNesting"/>.</item>
    ///   <item>UA32 — Annotation nesting: a widget annotation's /OBJR is held by an element that means
    ///     Form, a link annotation's by one that means Link (ISO 14289-1 7.18.4, 7.18.5) — an
    ///     annotation no /OBJR names is UA23's.</item>
    ///   <item>UA99 — A check could not be completed; its findings are missing (Info).</item>
    /// </list>
    ///
    /// <para><b>Why UA16–UA19 exist.</b> Without them the audit could not tell a properly tagged
    /// document from one whose structure tree points at nothing: a file with a handful of empty
    /// structure elements, no MCIDs and no ParentTree would produce a single Info-level remark and
    /// look very nearly conformant. Those four checks, with the missing-/ParentTree finding an
    /// Error rather than Info, are what make hollow tagging fail the audit.</para>
    ///
    /// <para><b>Why UA30 exists on top of them.</b> UA16 asks a purely <i>local</i> question — is
    /// <i>this</i> element a leaf that points at nothing — so a tree in which every element has an
    /// element child slips past it even when the tree as a whole reaches zero content. That shape
    /// is hollow tagging in disguise, and without UA30 only UA17 would catch it, by accident, and
    /// only when a page happened to draw unmarked text. UA30 asks the
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
    /// be byte-identical to one in which those checks ran and found nothing.</para>
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
        // ----------------------------------------------------------------
        //  Standard structure types
        // ----------------------------------------------------------------

        /// <summary>Sectioning containers whose nesting depth defines the level of a strong &lt;H&gt;.</summary>
        private static readonly HashSet<string> SectioningRoles = new HashSet<string>(StringComparer.Ordinal)
        {
            StructureTypes.Sect, StructureTypes.Part, StructureTypes.Art,
        };

        /// <summary>
        /// The twelve non-symbolic standard-14 faces. Their built-in encoding is
        /// StandardEncoding, which does map to Unicode — so they satisfy UA20 without a
        /// /ToUnicode. They do <b>not</b> satisfy UA21: PDF/UA grants no embedding exemption.
        /// </summary>
        private static readonly HashSet<string> Standard14TextFonts = new HashSet<string>(StringComparer.Ordinal)
        {
            StandardFonts.Courier,         StandardFonts.CourierBold,
            StandardFonts.CourierOblique,  StandardFonts.CourierBoldOblique,
            StandardFonts.Helvetica,       StandardFonts.HelveticaBold,
            StandardFonts.HelveticaOblique, StandardFonts.HelveticaBoldOblique,
            StandardFonts.TimesRoman,      StandardFonts.TimesBold,
            StandardFonts.TimesItalic,     StandardFonts.TimesBoldItalic,
        };

        /// <summary>The two symbolic standard-14 faces, which map to no Unicode without a /ToUnicode.</summary>
        private static readonly HashSet<string> Standard14SymbolicFonts = new HashSet<string>(StringComparer.Ordinal)
        {
            StandardFonts.Symbol, StandardFonts.ZapfDingbats,
        };

        /// <summary>Base-encoding names that carry a well-defined code → Unicode mapping.</summary>
        private static readonly HashSet<string> UnicodeCapableBaseEncodings = new HashSet<string>(StringComparer.Ordinal)
        {
            "WinAnsiEncoding","MacRomanEncoding","MacExpertEncoding","StandardEncoding","PDFDocEncoding",
        };

        /// <summary>
        /// Operators that put marks on the page without showing text or invoking an image
        /// (Table 60 path-painting, plus <c>sh</c>).
        /// </summary>
        /// <remarks>
        /// <para><b><c>n</c> is deliberately absent.</b> "End the path without filling or stroking"
        /// is the one path-painting operator that paints nothing — it exists to establish a clip —
        /// so counting it would report a page whose only content is a clipping path as having
        /// content.</para>
        ///
        /// <para>Counted so that "blank" can mean <i>nothing was painted</i> rather than "no text and
        /// no images". Without it a page of vector artwork — a chart, a logo, a signature — is
        /// indistinguishable from an empty one.</para>
        /// </remarks>
        private static readonly HashSet<string> PaintingOperators = new HashSet<string>(StringComparer.Ordinal)
        {
            PdfOperators.Stroke,
            PdfOperators.CloseStroke,
            PdfOperators.Fill,
            PdfOperators.FillObsolete,
            PdfOperators.FillEvenOdd,
            PdfOperators.FillStroke,
            PdfOperators.FillStrokeEvenOdd,
            PdfOperators.CloseFillStroke,
            PdfOperators.CloseFillStrokeEvenOdd,
            PdfOperators.ShadingFill,
        };

        // ----------------------------------------------------------------
        //  Name constants
        // ----------------------------------------------------------------
        //  Tetitulz.Pdf.Core.Objects.PdfName owns most of these; the ones below are the keys it
        //  does not expose, kept here rather than allocated per lookup.

        private static readonly PdfName NameStructParents = PdfName.StructParents;
        private static readonly PdfName NameStructParent  = PdfName.StructParent;

        /// <summary>
        /// <c>/Stm</c> — an <c>/MCR</c>'s optional pointer at the content stream holding the
        /// marked-content sequence, when that stream is <b>not</b> the page's own
        /// (ISO 32000-1 Table 324). Its presence means the MCID lives in a stream this checker
        /// has not tokenised, so the MCID-to-page relation cannot be judged for it.
        /// </summary>
        private static readonly PdfName NameStm           = PdfName.Stm;

        private static readonly PdfName NameColSpan       = PdfName.ColSpan;
        private static readonly PdfName NameRowSpan       = PdfName.RowSpan;

        /// <summary>
        /// <c>/ClassMap</c> — the <c>/StructTreeRoot</c> entry mapping a class name to an attribute
        /// object (ISO 32000-1 §14.7.5.2). It is the second, equally normative way to attach
        /// attributes to a structure element: the element carries <c>/C &lt;class name&gt;</c> (or
        /// an array of them) instead of, or as well as, an inline <c>/A</c>.
        /// </summary>

        /// <summary>
        /// The encryption dictionary's <c>/P</c> permissions key. Aliased rather than written as
        /// <see cref="PdfName.P"/> at the call site because everywhere else in this assembly
        /// <c>/P</c> is the structure element's parent — an entirely different entry that happens
        /// to share a name. Same instance, so the alias costs nothing.
        /// </summary>
        private static readonly PdfName NamePermissions   = PdfName.P;

        // ----------------------------------------------------------------
        //  Tunables
        // ----------------------------------------------------------------

        /// <summary>Occurrences of one finding reported individually before they aggregate.</summary>
        private const int IndividualReportLimit = 8;

        /// <summary>Object numbers listed inside an aggregated issue's message.</summary>
        private const int ObjectNumbersInMessage = 5;

        /// <summary>Depth cap when descending a number tree (/ParentTree).</summary>
        private const int MaxNumberTreeDepth = 32;

        /// <summary>Hard ceiling on entries read out of a number tree.</summary>
        private const int MaxNumberTreeEntries = 200_000;

        /// <summary>
        /// Hard ceiling on <c>/Kids</c> and <c>/Nums</c> <em>slots</em> enumerated across one
        /// number-tree walk.
        ///
        /// <para>
        /// <b>Why the entry, node and depth caps are not enough.</b> The visited set bounds how many
        /// times a node is expanded; it does not bound the cost of expanding one, and
        /// <see cref="MaxNumberTreeEntries"/> only counts <c>/Nums</c> pairs that were actually
        /// stored. A number tree may legally share nodes and may express <c>/Kids</c> as an indirect
        /// reference, so <i>n</i> intermediate nodes can all point <c>/Kids</c> at a single
        /// <i>n</i>-entry array: every one of the <i>n</i> expansions walks all <i>n</i> slots —
        /// O(n²) resolves — while the entry cap stays at zero and
        /// <see cref="MaxNumberTreeDepth"/> never fires, because the shape is two levels deep.
        /// This is the same defect <see cref="StructTreeWalker.DefaultMaxEdges"/> exists to stop in
        /// the structure tree.
        /// </para>
        ///
        /// <para>
        /// Twice <see cref="MaxNumberTreeEntries"/>: a well-formed tree spends one slot per stored
        /// entry plus roughly one <c>/Kids</c> slot per node, so no tree the entry cap admits can
        /// reach this, while a shared-<c>/Kids</c> bomb reaches it after a few hundred nodes.
        /// </para>
        /// </summary>
        private const int MaxNumberTreeSlots = 2 * MaxNumberTreeEntries;

        /// <summary>
        /// Depth cap when descending Form XObject resource dictionaries for fonts. Deliberately
        /// far stricter than <see cref="PdfLimits.MaxResourceDepth"/> (50): that bounds a
        /// <c>/Resources</c> inheritance chain the renderer must actually follow, whereas this is
        /// an audit that only needs to reach the fonts a real document puts within a few levels.
        /// </summary>
        private const int MaxResourceDepth = 8;

        /// <summary>Rows examined per table when checking regularity.</summary>
        private const int MaxTableRows = 20_000;

        /// <summary>
        /// Grid-layout steps one table may spend before UA12's regularity walk gives up on it.
        /// </summary>
        /// <remarks>
        /// <para><see cref="MaxTableRows"/> bounds the rows and <c>GetSpan</c> clamps one span to
        /// 1000, but neither bounds their <i>product</i>: the walk tracks outstanding row-spans as
        /// one dictionary key per occupied column, so cells × /ColSpan columns are inserted and then
        /// re-swept once per row. A single row of 2 000 cells at <c>/ColSpan 1000 /RowSpan 1000</c>
        /// makes that two million entries swept a thousand times — ~6×10^9 operations from a
        /// ~500 KB file, and the audit hangs rather than fails, because <c>RunCheck</c> catches
        /// exceptions and not time.</para>
        /// <para>Sized so a legitimate table cannot reach it: 20 000 rows of 100 columns spends
        /// 2 000 000, half the budget. A table that does reach it is named in a UA99 Info issue and
        /// its regularity verdict is withheld — a partial grid would report raggedness this walk
        /// manufactured rather than raggedness the document contains.</para>
        /// </remarks>
        private const int MaxTableGridWork = 4_000_000;

        /// <summary>Longest structure path emitted before it is elided from the front.</summary>
        private const int MaxStructPathLength = 200;

        /// <summary>
        /// Depth cap when walking the page tree by hand (see <see cref="CollectPagesFromPageTree"/>).
        /// Stricter than <see cref="PdfLimits.MaxPageTreeDepth"/> (1024), which has to admit
        /// generators that emit one <c>/Pages</c> node per page; a checker that hits 256 levels is
        /// looking at a file it should report on rather than keep descending.
        /// </summary>
        private const int MaxPageTreeDepth = 256;

        /// <summary>
        /// Ceiling for that same walk, spent per <c>/Kids</c> <em>slot</em> examined rather than
        /// per node expanded.
        ///
        /// <para>
        /// <b>Why per slot.</b> Charging per node bounds how many nodes are expanded but not the
        /// cost of expanding one: <i>n</i> <c>/Pages</c> nodes that all point <c>/Kids</c> at a
        /// single shared <i>n</i>-entry array cost <i>n</i> resolves each — O(n²) — while the
        /// counter is touched only <i>n</i> times and <see cref="MaxPageTreeDepth"/> never fires,
        /// because the tree is three levels deep. A well-formed page tree contains each node in
        /// exactly one <c>/Kids</c> array, so slots and nodes differ by one there and this ceiling
        /// still admits every real document.
        /// </para>
        /// </summary>
        private const int MaxPageTreeNodes = 200_000;

        /// <summary>
        /// Document-wide ceiling on <b>distinct</b> Form XObject bytes tokenised by the audit.
        ///
        /// <para>Only a cache <i>miss</i> spends it (see <see cref="CheckContext.FormScans"/>), so
        /// this bounds the total size of the forms in the file rather than the number of times they
        /// are invoked. 32 MB of distinct form content is far past any real document and small
        /// enough that the retained token lists cannot exhaust memory.</para>
        /// </summary>
        private const long MaxFormScanBytes = 32L * 1024 * 1024;

        /// <summary>
        /// Document-wide ceiling on Form XObject tokens <b>walked</b>.
        ///
        /// <para><b>Why caching the scan is not enough.</b> Pages may share one <c>/Contents</c>
        /// and one <c>/Resources</c>, so N pages that each invoke N forms cost N² walks over the
        /// forms' token lists while the file itself stays O(N) bytes. The scan cache removes the
        /// re-tokenising; only a budget on the walking removes the quadratic. Exhausting it sets
        /// <see cref="PageInfo.FormXObjectScanIncomplete"/>, which every consumer of these counts
        /// treats as "do not accuse" <i>and</i> records through
        /// <see cref="NotePartialContentGap"/>, so the numbers stay honest and the report says which
        /// pages they are short for, instead of the walk running unbounded.</para>
        /// </summary>
        private const long MaxFormTokenWalk = 20_000_000L;

        /// <summary>
        /// ISO 32000-1 Table 22, <c>/P</c> bit 10 — "Extract text and graphics … for use by
        /// assistive technology". Bit numbering in that table is 1-based, so bit 10 is
        /// <c>1 &lt;&lt; 9</c> = 512. Deprecated in ISO 32000-2, where it is treated as always set.
        ///
        /// <para>This is deliberately the same constant, with the same value and the same reading
        /// of the spec, as <c>PdfUaChecker.PermissionExtractForAccessibility</c>. Two halves of one
        /// audit disagreeing about the same document is the defect this exists to prevent, so if
        /// one interpretation ever changes the other must change with it.</para>
        /// </summary>
        private const int PermissionExtractForAccessibility = 1 << 9;

        // ----------------------------------------------------------------
        //  Content-size ceilings for the audit path
        // ----------------------------------------------------------------

        /// <summary>
        /// Default ceiling, in bytes, on the <em>decoded</em> content stream the audit will assemble
        /// and analyse for a single page. Default: 8 MiB.
        ///
        /// <para>
        /// <b>Why the audit needs one at all.</b> A page's <c>/Contents</c> is Flate-compressed, and
        /// Core admits a single stream that inflates to <c>PdfLimits.MaxDecompressedBytes</c>
        /// (512 MiB) — a compression bomb is a few kilobytes of upload. Every byte assembled is then
        /// tokenised by <see cref="ContentStreamScanner"/> into an object per operation, so the cost
        /// of a page is a multiple of its decoded size, not a fraction of it. This library runs
        /// server-side on files it did not produce; without a ceiling one hostile page decides how
        /// much memory and CPU the audit spends. <see cref="ContentStreamTagger"/> holds the tagging
        /// pass to such a ceiling (4 MiB) for the same reason.
        /// </para>
        ///
        /// <para>
        /// <b>Why 8 MiB and not the tagger's 4.</b> Auditing a page is one scan whose token list is
        /// released as soon as the page is reduced to scalars, where tagging builds three parallel
        /// representations of the same bytes and keeps them; the audit's cost per byte is roughly a
        /// third of the tagger's, so it can afford twice the input for the same peak. It is still
        /// far above any real page — a text-heavy A4 page decodes to tens of kilobytes, and a dense
        /// vector illustration to a few hundred.
        /// </para>
        ///
        /// <para>
        /// <b>Residual limit, stated honestly.</b> The ceiling bounds what is <em>assembled</em> and
        /// what is <em>scanned</em>. One individual <c>/Contents</c> stream is still decoded in full
        /// by Core before its length can be measured, so a single-part page can transiently allocate
        /// up to Core's own 512 MiB maximum; only the concatenation of further parts and the whole of
        /// the (far more expensive) tokenising pass are bounded here.
        /// </para>
        ///
        /// <para>A value of <c>0</c> or less disables the per-page ceiling — see the overload
        /// <see cref="Check(Stream, int, long)"/>. Only for a caller that has already bounded its
        /// input itself.</para>
        /// </summary>
        public const int DefaultMaxPageContentBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Default ceiling on the total decoded page content one audit will analyse across the whole
        /// document. Default: 128 MiB.
        ///
        /// <para>
        /// <see cref="DefaultMaxPageContentBytes"/> alone bounds one page and therefore nothing at
        /// all: a document with a thousand pages that each decode to just under the per-page ceiling
        /// is the same attack with an extra loop, and it costs the attacker only the page objects.
        /// This budget is spent as pages are loaded, in page order; once it is gone the remaining
        /// pages keep every check that reads dictionary structure (which is most of them) and lose
        /// only the content-level ones, each of which is named per page in the report exactly as a
        /// broken content stream is. 128 MiB of decoded page content is far more than any real
        /// document — a 1,000-page text PDF decodes to a few tens of megabytes in total.
        /// </para>
        ///
        /// <para>A value of <c>0</c> or less disables the document-wide budget.</para>
        /// </summary>
        public const long DefaultMaxDocumentContentBytes = 128L * 1024 * 1024;

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Runs the extended deep checks on a PDF file path.</summary>
        public static IReadOnlyList<AccessibilityIssue> Check(string pdfPath)
            => Check(pdfPath, DefaultMaxPageContentBytes, DefaultMaxDocumentContentBytes);

        /// <summary>
        /// Runs the extended deep checks on a PDF file path under caller-chosen content ceilings.
        /// See <see cref="Check(Stream, int, long)"/>.
        /// </summary>
        public static IReadOnlyList<AccessibilityIssue> Check(
            string pdfPath, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Check(fs, maxPageContentBytes, maxDocumentContentBytes);
        }

        /// <summary>Runs the extended deep checks on a PDF stream.</summary>
        public static IReadOnlyList<AccessibilityIssue> Check(Stream input)
            => Check(input, DefaultMaxPageContentBytes, DefaultMaxDocumentContentBytes);

        /// <summary>
        /// Runs the extended deep checks on a PDF stream under caller-chosen content ceilings, for a
        /// host that legitimately processes documents larger than the defaults (or that wants to be
        /// stricter still on untrusted upload).
        ///
        /// <para>
        /// A page outside the ceilings is not audited silently and is not audited wrongly: its
        /// content-level checks (UA14's marked-content mapping, UA17, UA18, UA24) are skipped and the
        /// page is named — with the limit that stopped it — in a UA99 <b>Info</b> issue, the same way
        /// a page whose content stream will not decode already is. Every check that reads only
        /// dictionary structure still runs for it, and the rest of the document is audited in full.
        /// </para>
        /// </summary>
        /// <param name="input">The PDF.</param>
        /// <param name="maxPageContentBytes">
        /// Per-page decoded-content ceiling; <c>0</c> or less disables it.
        /// See <see cref="DefaultMaxPageContentBytes"/>.
        /// </param>
        /// <param name="maxDocumentContentBytes">
        /// Document-wide decoded-content budget; <c>0</c> or less disables it.
        /// See <see cref="DefaultMaxDocumentContentBytes"/>.
        /// </param>
        public static IReadOnlyList<AccessibilityIssue> Check(
            Stream input, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using var doc = PdfDocument.Open(ms);
            return CheckDocument(doc, maxPageContentBytes, maxDocumentContentBytes);
        }

        // One implementation, in Core.IO.StreamBytes. It rewinds a seekable stream before
        // reading, where a copy reading from the current position would see only the rest.
        private static byte[] ReadAll(Stream s) => StreamBytes.ReadAll(s);

        // ================================================================
        //  Orchestration
        // ================================================================

        private static IReadOnlyList<AccessibilityIssue> CheckDocument(
            PdfDocument doc, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var issues = new List<AccessibilityIssue>();

            // UA06 is a statement about the security handler's permissions, never about encryption
            // as such: ISO 14289-1 §7.1 requires only that assistive technology be able to extract
            // the content. Whatever the verdict, the audit continues — names, numbers, booleans and
            // dictionary structure are not ciphertext, so every structural check below stays
            // meaningful. `encrypted` is threaded into the checks that read text or content streams.
            bool encrypted = CheckEncryption(doc, issues);

            PdfDictionary catalog = ResolveCatalog(doc);
            if (catalog == null)
            {
                issues.Add(Issue(IssueSeverity.Error, "UA00",
                    "No document catalog found.",
                    "The PDF is malformed — it must have a document catalog.", IssueCategory.Structure));
                return issues;
            }

            var ctx = new CheckContext
            {
                Doc                  = doc,
                Catalog              = catalog,
                Issues               = issues,
                Encrypted            = encrypted,
                MaxPageContentBytes  = maxPageContentBytes,
                // 0 or less disables the budget; long.MaxValue is how "disabled" is expressed
                // without a second flag, and it can never be spent by an int-bounded page.
                ContentByteBudget       = maxDocumentContentBytes > 0 ? maxDocumentContentBytes : long.MaxValue,
                MaxDocumentContentBytes = maxDocumentContentBytes,
            };

            // The object index is built ONCE and shared by every check: it is what turns
            // "which object number is this?" from an O(objects) xref walk into a dictionary probe.
            try
            {
                ctx.Index = PdfObjectIndex.Build(doc);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CheckDocument",
                    "the object index could not be built; findings will carry no object numbers", ex);
                ctx.Index = null;
            }

            ctx.StructTree = ResolveDictionary(doc, catalog[PdfName.StructTreeRoot]);
            ctx.StructTreeObjectNumber = GetObjectNumber(ctx, catalog[PdfName.StructTreeRoot], ctx.StructTree);
            ctx.MarkInfo = ResolveDictionary(doc, catalog[PdfName.MarkInfo]);
            // Resolved, not a bare cast: an indirect /Marked would otherwise read as false and
            // silently switch off UA17 and UA18 for the whole document.
            ctx.DeclaredMarked = (ResolveObject(doc, ctx.MarkInfo?[PdfName.Marked]) as PdfBoolean)?.Value == true;
            ctx.ClaimsTagged = ctx.StructTree != null || ctx.DeclaredMarked;

            // Cycle-safe collection: unguarded, a /K cycle with branching factor 2 would cost
            // 2^depth visits.
            RunCheck(ctx, "CollectStructure", () =>
            {
                string truncation = null;
                ctx.Elements = ctx.StructTree != null
                    ? StructTreeWalker.Collect(ctx.StructTree, doc, ctx.Index, out truncation)
                    : Array.Empty<StructNode>();

                // Every element-level check below reads this list, and each of them concludes
                // something from an *absence* — no heading of the right level, no /K reaching
                // content, no element claiming an MCID. On a truncated list those absences are
                // artefacts of the ceiling, so the report has to say the list is partial; the
                // walker's own diagnostics event is not part of it.
                if (truncation != null)
                    ReportStructureWalkGap(ctx, truncation,
                        "the structure tree was collected only in part, and every element-level " +
                        "check below (UA10–UA16, UA30) therefore ran on a partial list.");

                BuildTopology(ctx);
            });
            ctx.Elements ??= Array.Empty<StructNode>();

            // The call order is unchanged from before encryption support: on a document that is
            // not encrypted RunContentCheck is exactly RunCheck, so the issue sequence a caller
            // sees for such a file is byte-for-byte what it was.
            RunCheck(ctx, "CollectPages",           () => CollectPages(ctx));
            RunCheck(ctx, "HeadingHierarchy",       () => CheckHeadingHierarchy(ctx));
            RunCheck(ctx, "ListStructure",          () => CheckListStructure(ctx));
            RunCheck(ctx, "TableStructure",         () => CheckTableStructure(ctx));
            RunCheck(ctx, "StructureNesting",       () => CheckStructureNesting(ctx));
            RunCheck(ctx, "RoleMapping",            () => CheckRoleMapping(ctx));
            RunCheck(ctx, "ParentTreeCoverage",     () => CheckParentTreeCoverage(ctx));

            RunContentCheck(ctx, "FigureAltTextQuality",
                "UA15 (figure alt-text quality) was not attempted: /Alt is a string, and an " +
                "encrypted string cannot be judged empty, generic or meaningful",
                () => CheckFigureAltTextQuality(ctx));

            RunCheck(ctx, "EmptyStructureElements", () => CheckEmptyStructureElements(ctx));
            RunCheck(ctx, "HollowSubtrees",         () => CheckHollowSubtrees(ctx));

            RunContentCheck(ctx, "UnmarkedPageContent",
                "UA17 (text drawn outside every marked-content sequence) was not attempted: it " +
                "reads the page content streams, which are encrypted",
                () => CheckUnmarkedPageContent(ctx));

            RunContentCheck(ctx, "PageStructParents",
                "UA18 (a page holding structural content but no /StructParents) was not attempted: " +
                "whether a page holds structural content is established from its encrypted content " +
                "stream, so the /StructParents key cannot be judged required or correctly absent",
                () => CheckPageStructParents(ctx));

            RunCheck(ctx, "SuspectTagging",         () => CheckSuspectTagging(ctx));
            RunCheck(ctx, "Fonts",                  () => CheckFonts(ctx));
            RunCheck(ctx, "Annotations",            () => CheckAnnotations(ctx));

            RunContentCheck(ctx, "ImageOnlyPages",
                "UA24 (image-only, probably scanned pages) was not attempted: it counts drawing " +
                "operators in the encrypted page content streams",
                () => CheckImageOnlyPages(ctx));

            // All three coverage notices are emitted last, after every check has had its chance to
            // record what it could not do. The encryption notice is exclusive of the other two by
            // construction: NoteContentCoverageGap and NotePartialContentGap are both no-ops on an
            // encrypted document, where ReportEncryptionCoverage is the authority on the same facts.
            ReportContentCoverage(ctx);
            ReportPartialContentCoverage(ctx);
            ReportEncryptionCoverage(ctx);

            return issues;
        }

        /// <summary>
        /// Runs one check in isolation. A check that throws must never take the audit down with
        /// it, and must never fail silently either: the failure becomes a UA99 Info issue (so it
        /// is visible in the report) <i>and</i> an <see cref="AccessibilityDiagnostics"/> event
        /// (so a host can log it).
        /// </summary>
        private static void RunCheck(CheckContext ctx, string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker." + name,
                    "the check failed and was skipped; the audit is incomplete for this check", ex);

                ctx.Issues.Add(Issue(IssueSeverity.Info, "UA99",
                    "The '" + name + "' check could not be completed (" + ex.GetType().Name + ": " +
                    ex.Message + "). Any problems it would have found are missing from this report.",
                    "Repair the document (for example with the Maintenance plugin's sanitizer) and " +
                    "re-run the audit; treat this report as incomplete until the check runs cleanly.",
                    IssueCategory.Structure));
            }
        }

        /// <summary>
        /// Runs a check whose input is <i>decrypted</i> text or content-stream bytes.
        ///
        /// <para>
        /// This assembly does not reference <c>Tetitulz.Pdf.Encryption</c> and never decrypts, so
        /// on an encrypted document such a check has no real input: its stream either fails to
        /// decode or decodes to bytes that tokenise as noise. Running it anyway is the worse of the
        /// two failure modes — it would report findings the document does not contain, and an
        /// invented Error on a valid file is what teaches users to ignore the report. So the check
        /// is not attempted, and <paramref name="whatIsLost"/> is recorded verbatim for
        /// <see cref="ReportEncryptionCoverage"/>, which states it in the report. Skipping quietly
        /// is not an option: a check that neither ran nor said so is indistinguishable from a check
        /// that ran and found nothing.
        /// </para>
        ///
        /// <para>
        /// On a document that is not encrypted this is <see cref="RunCheck"/> and nothing else.
        /// </para>
        /// </summary>
        private static void RunContentCheck(CheckContext ctx, string name, string whatIsLost, Action body)
        {
            if (!ctx.Encrypted)
            {
                RunCheck(ctx, name, body);
                return;
            }

            NoteEncryptionLimit(ctx, whatIsLost);
            AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker." + name,
                "the document is encrypted and this checker does not decrypt, so the check was not " +
                "attempted; it is reported as missing rather than run on unreadable input");
        }

        /// <summary>
        /// Records, once, one thing the audit could not do because the document is encrypted.
        /// Duplicates are ignored so a per-element weakening (see
        /// <see cref="IsHeaderAddressable"/>) is stated once however many elements hit it.
        /// </summary>
        private static void NoteEncryptionLimit(CheckContext ctx, string whatIsLost)
        {
            if (string.IsNullOrEmpty(whatIsLost)) return;
            if (ctx.EncryptionLimitationsSeen.Add(whatIsLost))
                ctx.EncryptionLimitations.Add(whatIsLost);
        }

        /// <summary>
        /// Records, in the <b>report</b>, that a structure-tree walk hit a ceiling and that named
        /// checks were consequently not performed.
        ///
        /// <para>
        /// The bail-outs this serves abandon a partial reachability or claim computation rather than
        /// accuse elements the walk never reached, and they say so in the report. An
        /// <see cref="AccessibilityDiagnostics"/> event alone is not part of
        /// <c>AccessibilityAuditReport</c>: the result would be byte-identical to one in which those
        /// checks ran and found nothing — the condition <see cref="ReportContentCoverage"/> and
        /// <see cref="ReportEncryptionCoverage"/> exist to eliminate, and the one
        /// <see cref="RunCheck"/> answers with a UA99 when a check throws — and <c>IsConforming</c>
        /// could come back true for a document never examined for the hollow-tagging defect this
        /// feature exists to detect.
        /// </para>
        ///
        /// <para>
        /// <b>Info, not Error.</b> "This was not examined" is not a finding against the document,
        /// and flipping <c>IsConforming</c> on a ceiling would accuse a file of a defect nobody
        /// looked for — the same rule the encryption and content-coverage notices already follow.
        /// </para>
        /// </summary>
        /// <param name="ceiling">
        /// The ceiling that fired, named with its value, e.g. "the 2000000-edge structure-tree
        /// ceiling".
        /// </param>
        /// <param name="checksNotRun">What is consequently missing from the report.</param>
        /// <param name="ctx">Per-check state: the document, the options and the findings list.</param>
        private static void ReportStructureWalkGap(CheckContext ctx, string ceiling, string checksNotRun)
        {
            if (ctx?.Issues == null || string.IsNullOrEmpty(ceiling)) return;

            // One ceiling can fire in two independent passes over the same tree (reachability and
            // MCID claims each keep their own budget), and two identical Info issues would say
            // nothing the first does not.
            string key = ceiling + "\0" + checksNotRun;
            if (!ctx.StructureWalkGapsSeen.Add(key)) return;

            ctx.Issues.Add(Issue(IssueSeverity.Info, "UA99",
                "The audit stopped at " + ceiling + ", so " + checksNotRun +
                " Their findings are missing from this report: read a clean result here as " +
                "\"not examined\", not as \"nothing wrong\".",
                "Simplify the structure tree — the ceiling is reached by /K arrays that are shared " +
                "between many elements or are enormous, which no ordinary tagged document has — and " +
                "re-run the audit.",
                IssueCategory.Structure));
        }

    }
}











