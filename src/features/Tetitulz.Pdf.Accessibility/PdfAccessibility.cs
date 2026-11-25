// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility
{
    /// <summary>
    /// Main entry point for the Tetitulz.Pdf.Accessibility feature.
    ///
    /// <para>
    /// This static facade exposes the full PDF accessibility feature set as a simple,
    /// discoverable API for building accessibility-checking and remediation applications.
    /// </para>
    ///
    /// <para><b>Features:</b></para>
    /// <list type="bullet">
    ///   <item><b>Audit</b> — Combined PDF/UA-1 + extended check: the base checker's
    ///     <c>UA00</c>–<c>UA09</c>/<c>UA25</c>–<c>UA29</c>/<c>UA99</c> plus the deep checker's
    ///     <c>UA00</c>/<c>UA06</c>/<c>UA10</c>–<c>UA24</c>/<c>UA30</c>/<c>UA99</c>, WCAG-mapped.
    ///     See <see cref="Models.AccessibilityAuditReport"/> for how the two are combined.</item>
    ///   <item><b>AutoTag</b> — Heuristic structure-tree generation for untagged PDFs, <i>plus</i>
    ///     the content-stream marked-content tagging the tree points at.</item>
    ///   <item><b>SetFigureAltText</b> — Set /Alt (alternate text) on Figure elements.</item>
    ///   <item><b>SetLinkDescriptions</b> — Set /Contents (alternate description) on links (UA22).</item>
    ///   <item><b>SetDocumentLanguage</b> — Inject /Lang into the document catalog.</item>
    ///   <item><b>SetDocumentTitle</b> — Set /Info /Title and /DisplayDocTitle.</item>
    ///   <item><b>SetPdfUaIdentifier</b> — Write the <c>pdfuaid:part</c> XMP declaration (UA03).</item>
    ///   <item><b>FixHeadingHierarchy</b> — Renumber H1–H6 so levels never skip.</item>
    ///   <item><b>SetFieldTooltip</b> — Set /TU (accessible name) on a form field.</item>
    ///   <item><b>SetStructureTabOrder</b> — Set /Tabs /S on annotated pages (UA09).</item>
    ///   <item><b>AnalyzeReadingOrder</b> — Detect structure tree vs visual order mismatches.</item>
    ///   <item><b>FixReadingOrder</b> — Reorder structure elements to match visual reading order.</item>
    ///   <item><b>MarkHeadersAndFooters</b> — Mark page header/footer text as PDF artifacts.</item>
    ///   <item><b>EmbedMissingFonts</b> — Embed a font program into every font the document uses
    ///     but does not carry (UA21/UA25). Needs a font source from the caller.</item>
    ///   <item><b>RemediateWithReport</b> — All of the above as one pipeline, fully reported.</item>
    /// </list>
    ///
    /// <para><b>What a clean report does and does not prove.</b> Neither <see cref="Audit(Stream)"/> nor
    /// <see cref="RemediateWithReport"/> issues a PDF/UA-1 conformance certificate. They report the
    /// absence of the problems <i>this library checks for</i>, and several of those checks are
    /// structural only — they look at dictionary keys, not at the marked content those keys claim
    /// to describe. Validate with an independent PDF/UA checker, and have a human review the result, before making
    /// a conformance claim. See the remarks on <see cref="Audit(Stream)"/> for the specifics.</para>
    ///
    /// <para><b>Typical remediation workflow for an app developer:</b></para>
    /// <code>
    /// // One call runs the whole pipeline and tells you exactly what happened.
    /// using var input  = File.OpenRead("input.pdf");
    /// using var output = File.Create("remediated.pdf");
    ///
    /// var result = PdfAccessibility.RemediateWithReport(input, output, new RemediationOptions
    /// {
    ///     Language = "en-US",
    ///     Title    = "Quarterly Report",
    ///     // SetPdfUaIdentifier is on by default and is what actually closes UA03.
    /// });
    ///
    /// Console.WriteLine(result);                     // before -&gt; after, one line
    /// Console.WriteLine(result.ToDetailedString());  // per-step log + remaining issues
    ///
    /// foreach (var step in result.SkippedSteps)
    ///     Console.WriteLine($"{step.Name} did nothing: {step.SkipReason}");
    ///
    /// if (!result.IsConformingAfter)
    ///     foreach (var issue in result.AfterAudit.Issues)
    ///         Console.WriteLine($"{issue} -&gt; {issue.Remediation}");
    /// </code>
    ///
    /// <para>The individual operations remain available for callers that need to drive the steps
    /// themselves (each writes an incremental update, so they can be chained stream to stream):</para>
    /// <code>
    /// using var ms1 = new MemoryStream();
    /// PdfAccessibility.AutoTag(File.OpenRead("input.pdf"), ms1);
    ///
    /// ms1.Position = 0;
    /// using var ms2 = new MemoryStream();
    /// PdfAccessibility.SetFigureAltText(ms1, ms2, ctx =&gt; $"Figure {ctx.FigureIndex + 1}");
    ///
    /// ms2.Position = 0;
    /// using var ms3 = new MemoryStream();
    /// PdfAccessibility.SetPdfUaIdentifier(ms2, ms3, part: 1, title: "Quarterly Report", language: "en-US");
    ///
    /// ms3.Position = 0;
    /// Console.WriteLine(PdfAccessibility.Audit(ms3));
    /// </code>
    /// </summary>
    public static class PdfAccessibility
    {
        // ================================================================
        //  Diagnostics / step names
        // ================================================================

        private const string DiagnosticOperation = "PdfAccessibility";

        private const string StepAutoTag             = "AutoTag";
        private const string StepMarkArtifacts       = "MarkArtifacts";
        private const string StepFixReadingOrder     = "FixReadingOrder";
        private const string StepSetDocumentMetadata = "SetDocumentMetadata";
        private const string StepSetFigureAltText    = "SetFigureAltText";
        private const string StepSetLinkDescriptions = "SetLinkDescriptions";
        private const string StepFixHeadingHierarchy = "FixHeadingHierarchy";
        private const string StepSetTabOrder         = "SetTabOrder";
        private const string StepEmbedFonts          = "EmbedFonts";
        private const string StepSetPdfUaIdentifier  = "SetPdfUaIdentifier";

        /// <summary>
        /// The "a check could not be completed" code, shared by <c>PdfUaChecker</c> and
        /// <see cref="PdfDeepAccessibilityChecker"/>. It is <b>not</b> an ISO 14289-1 requirement:
        /// it says the report is incomplete, never that the document is wrong, and so is always
        /// <see cref="IssueSeverity.Info"/> whichever checker raised it.
        /// </summary>
        private const string IncompleteCheckCode = "UA99";

        /// <summary>
        /// The <c>/Tabs</c> <i>value</i> <c>/S</c> — "annotation tab order follows the structure
        /// tree" (ISO 32000-1 Table 30). Named separately from <see cref="PdfName.S"/>, which
        /// everywhere else in this assembly is the structure-element <i>key</i>.
        /// </summary>
        private const string StructureTabOrderValue = "S";

        // ================================================================
        //  Public diagnostics surface
        // ================================================================

        /// <summary>
        /// The most recent tolerated failure or anomaly reported by any accessibility operation
        /// <b>on the current thread</b>, or <c>null</c> when the last operation had nothing to
        /// report.
        ///
        /// <para>
        /// Read this after an operation returns a "nothing happened" result — <c>0</c> figures
        /// updated, <c>false</c>, an empty issue list, an unchanged byte count — to tell
        /// <i>"there was nothing to do"</i> from <i>"something went wrong and was tolerated"</i>.
        /// The two are otherwise indistinguishable through the return value alone.
        /// </para>
        ///
        /// <para>
        /// Thread-static, so a value read here always belongs to this thread's call and a
        /// concurrent operation on another thread cannot overwrite it. Cleared at the start of
        /// each public operation.
        /// </para>
        /// </summary>
        public static string LastDiagnostic => AccessibilityDiagnostics.LastError;

        /// <summary>
        /// Raised for every diagnostic any accessibility operation reports, on the reporting
        /// thread, so a host can forward them to logging or telemetry. Never raised for a clean
        /// operation.
        ///
        /// <para>
        /// This is the <b>only</b> way to observe diagnostics that a single
        /// <see cref="LastDiagnostic"/> read would miss — one operation can tolerate several
        /// distinct failures (per-page skips, unresolvable structure children, writer fallbacks)
        /// and the slot keeps only the last.
        /// </para>
        ///
        /// <para>
        /// An exception thrown by a subscriber is caught and discarded: these events fire from
        /// inside failure-tolerant paths, and a throwing logger must not be able to turn a
        /// tolerated failure into a failed operation. Subscribers should still not throw, since
        /// one that does costs the remaining subscribers their notification.
        /// </para>
        /// </summary>
        public static event Action<string> DiagnosticReported
        {
            add    => AccessibilityDiagnostics.OnEvent += value;
            remove => AccessibilityDiagnostics.OnEvent -= value;
        }

        // ================================================================
        //  Audit
        // ================================================================

        /// <summary>
        /// Performs a full accessibility audit on the PDF at <paramref name="pdfPath"/>.
        /// See <see cref="Audit(Stream)"/> for what the audit does and does not prove.
        /// </summary>
        /// <param name="pdfPath">Path of the PDF to audit.</param>
        /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> is null.</exception>
        public static AccessibilityAuditReport Audit(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Audit(fs);
        }

        // ================================================================
        //  Page content classification
        // ================================================================

        /// <summary>
        /// Classifies every page as blank, text, image (i.e. scanned), vector or mixed.
        /// </summary>
        /// <param name="input">Stream containing the PDF. Read in full; never modified.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        /// <remarks>
        /// <para>The two questions this answers — <b>is this page blank?</b> and <b>is this page a
        /// scan?</b> — were computable but not askable: the scan behind UA24 counts exactly what is
        /// needed, and a caller wanting the answer had to run a whole audit and string-match an issue
        /// code, which yields nothing at all for a blank page.</para>
        ///
        /// <para><b>It lives here rather than on <c>PdfOperations</c> because the engine does.</b>
        /// The content scanner is <c>internal</c> to this assembly and the ring model forbids
        /// <c>Base → feature</c>; the alternative was a second implementation of "count the marks on
        /// a page", which is this repository's recurring defect.</para>
        ///
        /// <para><b>Two limits, both real.</b> Annotation appearance streams are not walked, so a
        /// page whose only marks are a stamp or a signature block classifies as blank. And a page
        /// whose content would not decode, or whose Form XObject scan was cut short, is
        /// <see cref="PageContentKind.Undetermined"/> rather than guessed at — on a partial scan
        /// "no text found" is a fact about the ceiling, not about the page.</para>
        /// </remarks>
        public static IReadOnlyList<PageContentClassification> ClassifyPageContent(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(new MemoryStream(ReadAll(input)));
            return PdfDeepAccessibilityChecker.ClassifyPageContent(doc);
        }

        /// <summary>Classifies every page of the PDF at <paramref name="pdfPath"/>.</summary>
        /// <param name="pdfPath">Path of the PDF to classify.</param>
        /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> is null.</exception>
        public static IReadOnlyList<PageContentClassification> ClassifyPageContent(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return ClassifyPageContent(fs);
        }

        /// <summary>
        /// Performs a full accessibility audit on a PDF stream, combining the base PDF/UA-1
        /// checks (<c>UA00</c>–<c>UA09</c>, <c>UA25</c>–<c>UA29</c>, <c>UA50</c> and up, <c>UA99</c>) with
        /// the extended deep checks (<c>UA00</c>, <c>UA06</c>, <c>UA10</c>–<c>UA24</c>,
        /// <c>UA30</c>–<c>UA35</c>, <c>UA99</c>). <c>UA28</c>, <c>UA29</c> and <c>UA50</c> are dropped from the
        /// base checker's contribution, superseded by the deep checker's more precise <c>UA14</c>,
        /// <c>UA16</c> and <c>UA16</c>–<c>UA18</c>; see <see cref="AccessibilityAuditReport"/>.
        ///
        /// <para><b>What this proves.</b> Each issue in the returned report is a real finding: the
        /// document does not satisfy a requirement this library knows how to test. Fixing every
        /// reported issue is necessary for PDF/UA-1 conformance.</para>
        ///
        /// <para><b>What this does NOT prove.</b> An empty report — equivalently
        /// <see cref="AccessibilityAuditReport.IsConforming"/> — is <i>not</i> a PDF/UA-1
        /// conformance certificate and must never be surfaced to a user as "this file is
        /// accessible":</para>
        /// <list type="bullet">
        ///   <item><b>Several checks are structural only.</b> UA01 reads
        ///   <c>/MarkInfo /Marked</c>, UA05 reads <c>/StructTreeRoot</c>, UA07 counts Figure
        ///   elements that carry no <c>/Alt</c> and UA08 counts fields that carry no <c>/TU</c>.
        ///   None of them verifies that the marked content those keys describe actually exists,
        ///   is correctly nested, or corresponds to what is drawn on the page. A document can pass
        ///   every one of them with a structure tree that describes nothing.</item>
        ///   <item><b>Content-level requirements are covered partially or not at all</b> —
        ///   complete Unicode mapping for every glyph, table header/scope association, list
        ///   structure, and the correctness (rather than the presence) of the reading order.</item>
        ///   <item><b>The human requirements cannot be machine-checked at all.</b> Whether alt
        ///   text is <i>accurate</i>, whether heading levels reflect the real document outline,
        ///   and whether the reading order is the intended one all require human review; PDF/UA-1
        ///   mandates that review.</item>
        ///   <item>Issues reported as <see cref="IssueSeverity.Warning"/> or
        ///   <see cref="IssueSeverity.Info"/> do not affect
        ///   <see cref="AccessibilityAuditReport.IsConforming"/> even though a stricter validator
        ///   may fail the file for them.</item>
        /// </list>
        /// <para>Read a clean report as "no blocking issue was <i>detected</i>", and validate with
        /// an independent PDF/UA checker before publishing a conformance claim.</para>
        ///
        /// <para><b>Ordering.</b> Issues are sorted by severity, then page number (document-level
        /// issues first), then check code, then object number — a total order, so two audits of
        /// the same document always produce the same sequence.</para>
        /// </summary>
        /// <param name="input">Stream containing the PDF. Read in full; never modified.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        /// <remarks>
        /// A document that cannot be parsed at all is reported as a single Error-severity
        /// <c>UA00</c> issue naming the parse failure, rather than throwing: the caller of an
        /// audit wants a report, and "this file is unreadable" is the most important finding
        /// there is. <see cref="AccessibilityAuditReport.IsConforming"/> is therefore false, which
        /// is the honest answer. The cause is also raised on the accessibility diagnostics sink.
        /// </remarks>
        public static AccessibilityAuditReport Audit(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return AuditBytes(ReadAll(input));
        }

        /// <summary>
        /// Performs a full accessibility audit under caller-chosen content ceilings. Identical to
        /// <see cref="Audit(Stream)"/> in every other respect — see its remarks for what the audit
        /// does and does not prove.
        ///
        /// <para>
        /// The deep checker bounds the decoded page content it will read, because a page's
        /// <c>/Contents</c> is attacker-chosen and inflates from a few kilobytes of Flate to as much
        /// as 512 MiB, with tokenising costing a multiple of that again. The defaults
        /// (<see cref="PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes"/>, 8 MiB per page,
        /// and <see cref="PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes"/>, 128 MiB
        /// across the document) suit ordinary files; this overload exists so a host that
        /// legitimately audits larger documents can raise them, and one auditing untrusted upload
        /// can lower them, rather than being stuck with a limit nobody can adjust.
        /// </para>
        ///
        /// <para>
        /// <b>A page outside the ceilings is not audited silently and is not audited wrongly.</b>
        /// Its content-level checks are skipped, every dictionary-level check still runs for it, the
        /// rest of the document is audited in full, and the page is named — with the limit that
        /// stopped it — in a UA99 <b>Info</b> issue. A clean report therefore still cannot be read
        /// as "this page is fine".
        /// </para>
        /// </summary>
        /// <param name="input">Stream containing the PDF. Read in full; never modified.</param>
        /// <param name="maxPageContentBytes">
        /// Per-page decoded-content ceiling in bytes; <c>0</c> or less disables it.
        /// </param>
        /// <param name="maxDocumentContentBytes">
        /// Document-wide decoded-content budget in bytes; <c>0</c> or less disables it.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static AccessibilityAuditReport Audit(
            Stream input, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return AuditBytes(ReadAll(input), maxPageContentBytes, maxDocumentContentBytes);
        }

        /// <summary>
        /// Performs a full accessibility audit on the PDF at <paramref name="pdfPath"/> under
        /// caller-chosen content ceilings. See <see cref="Audit(Stream,int,long)"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> is null.</exception>
        public static AccessibilityAuditReport Audit(
            string pdfPath, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Audit(fs, maxPageContentBytes, maxDocumentContentBytes);
        }

        /// <summary>
        /// Performs a full accessibility audit under caller-chosen options — in particular the part
        /// of ISO 14289 to judge the document against (<see cref="AuditOptions.Target"/>). With
        /// <c>null</c> or a default <see cref="AuditOptions"/> this is exactly
        /// <see cref="Audit(Stream)"/>; see its remarks for what the audit does and does not prove.
        ///
        /// <para><b>Under <see cref="PdfUaVersion.Ua2"/> the PDF/UA-1 checkers do not run.</b> The
        /// report carries <c>Plugins.Conformance.PdfUa2Checker</c>'s Clause 5 finding
        /// (<c>UA2-5</c>) and the deep <c>PdfUa2StructureChecker</c>'s clause findings
        /// (<c>UA2-8.x</c>) instead, every one an <see cref="IssueSeverity.Error"/> except
        /// <c>UA2-99</c>, which — like UA99 — says a check did not run and maps to
        /// <see cref="IssueSeverity.Info"/> so that an incomplete report does not condemn the document
        /// on its own. <see cref="AccessibilityAuditReport.ConformanceTarget"/> names the part that
        /// was applied, which under <see cref="PdfUaVersion.Auto"/> is the one the document's own
        /// <c>pdfuaid:part</c> selected.</para>
        ///
        /// <para><b>Audit only.</b> Auto-tagging and remediation still produce PDF/UA-1 output; a
        /// PDF/UA-2 target here does not make them write PDF 2.0 structure (GAPS.md §22d).</para>
        /// </summary>
        /// <param name="input">Stream containing the PDF. Read in full; never modified.</param>
        /// <param name="options">The audit's options; <c>null</c> means the defaults.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static AccessibilityAuditReport Audit(Stream input, AuditOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            options ??= new AuditOptions();
            return AuditBytes(ReadAll(input), options.MaxPageContentBytes, options.MaxDocumentContentBytes,
                              options.Target);
        }

        /// <summary>
        /// Performs a full accessibility audit on the PDF at <paramref name="pdfPath"/> under
        /// caller-chosen options. See <see cref="Audit(Stream,AuditOptions)"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> is null.</exception>
        public static AccessibilityAuditReport Audit(string pdfPath, AuditOptions options)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Audit(fs, options);
        }

        // ================================================================
        //  Auto-tagging
        // ================================================================

        /// <summary>
        /// Heuristically auto-tags an untagged PDF by analysing text layout, then writes the
        /// result as an incremental update.
        ///
        /// <para><b>This rewrites page content streams.</b> Tagging is two halves, and both are
        /// performed: each page's content stream is re-emitted with real marked content
        /// (<c>/P &lt;&lt;/MCID n&gt;&gt; BDC … EMC</c>, and running headers/footers as
        /// <c>/Artifact … BDC … EMC</c>), and a logical structure tree
        /// (<c>/StructTreeRoot</c>, headings, paragraphs, a <c>/ParentTree</c>, per-page
        /// <c>/StructParents</c>) is written pointing at those MCIDs. A structure tree whose
        /// elements point at no content is invisible to assistive technology, so emitting one
        /// alone would not be tagging at all. Set
        /// <see cref="AutoTagOptions.RewriteContentStreams"/> to <c>false</c> to suppress the
        /// content half — but note that no structure tree is then written either, for the same
        /// reason.</para>
        ///
        /// <para><b>Pages are skipped, not forced.</b> A page whose content stream already
        /// contains marked content, cannot be scanned unambiguously, or draws no text is left
        /// byte-for-byte untouched and named in
        /// <see cref="AutoTagResult.SkippedPageReasons"/>. If <i>no</i> page could be marked, no
        /// structure tree is written at all and <see cref="AutoTagResult.ContentMarked"/> is
        /// <c>false</c> — check it before treating the output as tagged. Text drawn inside a Form
        /// XObject is not marked; pages that carry any are counted in
        /// <see cref="AutoTagResult.PagesPartiallyTagged"/>.</para>
        ///
        /// <para><b>An existing structure tree is replaced, not merged.</b> The generated tree
        /// supersedes it, and stale <c>/StructParent(s)</c> keys that the new
        /// <c>/ParentTree</c> does not define are stripped so the output stays valid; the
        /// objects they belonged to (typically annotations) become untagged. Each of these is
        /// reported in <see cref="AutoTagResult.Warnings"/>.</para>
        /// </summary>
        public static AutoTagResult AutoTag(string inputPath, string outputPath, AutoTagOptions options = null)
            => PdfAutoTagger.AutoTag(inputPath, outputPath, options);

        /// <summary>
        /// Heuristically auto-tags an untagged PDF stream. Rewrites page content streams to carry
        /// marked content as well as writing the structure tree — see
        /// <see cref="AutoTag(string,string,AutoTagOptions)"/> for the full behaviour, the
        /// page-skip policy and the effect on an existing structure tree.
        /// </summary>
        public static AutoTagResult AutoTag(Stream input, Stream output, AutoTagOptions options = null)
            => PdfAutoTagger.AutoTag(input, output, options);

        // ================================================================
        //  Structure tree editing
        // ================================================================

        /// <summary>
        /// Sets alt text on Figure structure elements using a callback.
        /// Return a string from <paramref name="altTextProvider"/> to set /Alt,
        /// or null to skip that figure.
        /// </summary>
        public static SetAltTextResult SetFigureAltText(
            Stream input, Stream output,
            Func<FigureContext, string> altTextProvider)
            => PdfStructureTreeEditor.SetFigureAltText(input, output, altTextProvider);

        /// <summary>
        /// Sets the same alt text on all Figure elements that currently lack one.
        /// </summary>
        public static SetAltTextResult SetFigureAltText(
            Stream input, Stream output, string altText)
            => PdfStructureTreeEditor.SetFigureAltText(input, output, altText);

        /// <summary>
        /// Sets /ActualText on structure elements of the given type.
        /// The <paramref name="actualTextProvider"/> callback receives (index, existingText)
        /// and returns the new text, or null to skip.
        /// </summary>
        public static int SetActualText(
            Stream input, Stream output,
            string elementType,
            Func<int, string, string> actualTextProvider)
            => PdfStructureTreeEditor.SetActualText(input, output, elementType, actualTextProvider);

        /// <summary>
        /// Sets the document language (/Lang) in the catalog.
        /// </summary>
        public static bool SetDocumentLanguage(Stream input, Stream output, string languageTag)
            => PdfStructureTreeEditor.SetDocumentLanguage(input, output, languageTag);

        /// <summary>
        /// Sets the document title in /Info and enables /ViewerPreferences /DisplayDocTitle.
        /// </summary>
        public static bool SetDocumentTitle(Stream input, Stream output, string title)
            => PdfStructureTreeEditor.SetDocumentTitle(input, output, title);

        /// <summary>
        /// Renumbers H1–H6 heading elements in the structure tree so that levels
        /// never skip (e.g., H1 directly followed by H3 becomes H1→H2).
        /// Returns the count of elements modified.
        /// </summary>
        public static int FixHeadingHierarchy(Stream input, Stream output)
            => PdfStructureTreeEditor.FixHeadingHierarchy(input, output);

        /// <summary>
        /// Sets a /TU (tooltip / accessible name) on the form field with the given
        /// partial name (/T). Returns true if the field was found and updated.
        /// </summary>
        public static bool SetFieldTooltip(
            Stream input, Stream output,
            string fieldPartialName, string tooltip)
            => PdfStructureTreeEditor.SetFieldTooltip(input, output, fieldPartialName, tooltip);

        /// <summary>
        /// Sets the alternate description (<c>/Contents</c>) of the link annotations a reader is
        /// presented, which closes <b>UA22</b>. The provider receives each link's
        /// <see cref="LinkContext"/> — its page, rectangle and target — in page order and returns
        /// the description, or <c>null</c> to leave that link. See
        /// <see cref="PdfStructureTreeEditor.SetLinkDescriptions"/>.
        /// </summary>
        public static SetAltTextResult SetLinkDescriptions(
            Stream input, Stream output,
            Func<LinkContext, string> descriptionProvider)
            => PdfStructureTreeEditor.SetLinkDescriptions(input, output, descriptionProvider);

        /// <summary>
        /// Sets <c>/Tabs /S</c> ("tab order follows the structure tree", ISO 32000-1 Table 30) on
        /// every page that carries at least one annotation and does not already declare it, and
        /// returns the number of pages changed.
        ///
        /// <para>This is what closes <b>UA09</b>. PDF/UA-1 (ISO 14289-1, 7.18.3) requires the key
        /// only on annotated pages, and this operation touches exactly the pages the UA09 check
        /// flags — pages without annotations are deliberately left alone rather than given a key
        /// nothing asks for.</para>
        ///
        /// <para>A page carrying the legacy <c>/TabOrder /S</c> is treated as not yet fixed and
        /// receives a real <c>/Tabs /S</c>; the legacy entry is left in place. When no page needs
        /// the key, no revision is appended and the output is byte-identical to the input.</para>
        ///
        /// <para>Also runs inside <see cref="RemediateWithReport"/> as the <c>SetTabOrder</c> step
        /// (see <see cref="RemediationOptions.FixTabOrder"/>); call it directly only when driving
        /// the steps yourself.</para>
        /// </summary>
        /// <param name="input">Source PDF. Read in full; never modified.</param>
        /// <param name="output">Receives the whole document (original bytes plus, when anything
        /// changed, one appended revision).</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        public static int SetStructureTabOrder(Stream input, Stream output)
            => PdfStructureTreeEditor.SetStructureTabOrder(input, output);

        // ================================================================
        //  Metadata (PDF/UA identifier)
        // ================================================================

        /// <summary>
        /// Writes the PDF/UA identification schema (<c>pdfuaid:part</c>) into the document's XMP
        /// metadata, optionally also setting <c>dc:title</c> and <c>dc:language</c>, and appends
        /// the result to <paramref name="output"/> as an incremental update.
        ///
        /// <para>This is the only operation that can close <b>UA03</b> ("XMP metadata must declare
        /// <c>pdfuaid:part</c>"): no amount of structure-tree work satisfies it. An existing XMP
        /// packet is merged, not clobbered.</para>
        ///
        /// <para>On failure the source bytes are copied to <paramref name="output"/> unchanged and
        /// <c>false</c> is returned; inspect <see cref="PdfUaXmpWriter.LastError"/> for the cause.
        /// Encrypted documents are not supported and are passed through untouched.</para>
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream that receives the updated (or, on failure, the
        /// unmodified) PDF.</param>
        /// <param name="part">PDF/UA part to declare: 1 for PDF/UA-1, 2 for PDF/UA-2.</param>
        /// <param name="title">Optional <c>dc:title</c>; null or whitespace leaves any existing
        /// title untouched.</param>
        /// <param name="language">Optional BCP-47 <c>dc:language</c> (e.g. "en-US"); null or
        /// whitespace leaves any existing value untouched.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is not 1 or 2.</exception>
        public static bool SetPdfUaIdentifier(
            Stream input, Stream output,
            int part = 1, string title = null, string language = null)
            => PdfUaXmpWriter.SetPdfUaIdentifier(input, output, part, title, language);

        // ================================================================
        //  Reading order
        // ================================================================

        /// <summary>
        /// Analyzes the logical structure tree order against the visual (top-to-bottom)
        /// reading order and returns any mismatches.
        /// </summary>
        public static ReadingOrderAnalysis AnalyzeReadingOrder(string pdfPath)
            => PdfReadingOrderAnalyzer.Analyze(pdfPath);

        /// <summary>
        /// Analyzes the logical reading order of a PDF stream.
        /// </summary>
        public static ReadingOrderAnalysis AnalyzeReadingOrder(Stream input)
            => PdfReadingOrderAnalyzer.Analyze(input);

        /// <summary>
        /// Repairs the reading order by reordering structure elements to match
        /// visual top-to-bottom, left-to-right order. Writes an incremental update.
        /// </summary>
        public static ReadingOrderFixResult FixReadingOrder(Stream input, Stream output)
            => PdfReadingOrderAnalyzer.Fix(input, output);

        // ================================================================
        //  Artifact marking
        // ================================================================

        /// <summary>
        /// Detects running headers and footers (text within the specified margin from the top or
        /// bottom of each page) and marks them as artifacts <b>in the page content stream</b>,
        /// wrapping each run in
        /// <c>/Artifact &lt;&lt;/Type /Pagination /Subtype /Header|/Footer&gt;&gt; BDC … EMC</c>.
        ///
        /// <para>
        /// <b>No structure elements are created.</b> An artifact is by definition content that is
        /// <i>not</i> part of the logical structure (ISO 32000-1 §14.8.2.2), so putting it into
        /// the structure tree as an <c>/S /Artifact</c> element inverts its meaning: assistive
        /// technology would then read out the page furniture it is supposed to skip. Marking
        /// happens purely in the content stream, and artifacts consume no MCID and never appear
        /// in the <c>/ParentTree</c>.
        /// </para>
        ///
        /// <para>
        /// Pages whose content stream cannot be rewritten safely are left untouched. This
        /// overload reports only a count; use
        /// <see cref="PdfArtifactMarker.MarkHeadersAndFootersDetailed"/> to tell "no headers or
        /// footers were found" apart from "these pages were refused", which produce the same
        /// count here.
        /// </para>
        /// </summary>
        public static ArtifactMarkResult MarkHeadersAndFooters(
            Stream input, Stream output,
            float headerMarginPt = 50f,
            float footerMarginPt = 50f)
            => PdfArtifactMarker.MarkHeadersAndFooters(input, output, headerMarginPt, footerMarginPt);

        // ================================================================
        //  Font embedding
        // ================================================================

        /// <summary>
        /// Embeds a font program into every font the document uses but does not carry, and writes
        /// the result as one appended revision — the operation that closes <b>UA21</b> and
        /// <b>UA25</b>.
        ///
        /// <para>
        /// PDF/UA-1 §7.21.4.1 requires every font to be embedded and grants no exemption for the
        /// Standard 14, so a document that draws with Helvetica, Times or Courier fails those checks
        /// until a font program is put into it. No structural fix can do that, because the program
        /// is not in the document — <paramref name="options"/> is where the caller supplies one.
        /// </para>
        ///
        /// <para><b>Licensing.</b> Embedding a font redistributes it, and most fonts installed on a
        /// machine are licensed in ways that may not permit that. The caller is responsible for
        /// holding redistribution rights to whatever it supplies through
        /// <see cref="FontEmbeddingOptions.FontProvider"/> or authorises through
        /// <see cref="FontEmbeddingOptions.AllowSystemFontSubstitution"/> (which is off by
        /// default).</para>
        ///
        /// <para>The document's own advance widths are always preserved — a font relying on the
        /// built-in Standard-14 metrics gets a <c>/Widths</c> array synthesised from those
        /// <i>original</i> metrics, never from the substitute — so embedding never moves a glyph.
        /// See <see cref="PdfFontEmbeddingRemediator.EmbedMissingFonts"/> for the full contract.</para>
        /// </summary>
        /// <param name="input">Source PDF. Read in full; never modified.</param>
        /// <param name="output">Receives the document. Always written, even when nothing changed.</param>
        /// <param name="options">Font source and subsetting options; null means no font source is
        /// available, in which case every non-embedded font is reported as skipped with that reason.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        public static FontEmbeddingResult EmbedMissingFonts(
            Stream input, Stream output, FontEmbeddingOptions options = null)
            => PdfFontEmbeddingRemediator.EmbedMissingFonts(input, output, options);

        // ================================================================
        //  Full auto-remediation pipeline
        // ================================================================

        /// <summary>
        /// Runs the full remediation pipeline and returns the final audit report.
        /// <para>Thin wrapper over <see cref="RemediateWithReport"/>, kept for source
        /// compatibility; it returns <see cref="RemediationReport.AfterAudit"/> and therefore
        /// discards the per-step log, the warnings and the before/after comparison. Prefer
        /// <see cref="RemediateWithReport"/> in new code.</para>
        /// </summary>
        /// <param name="input">Source PDF. Read in full; never modified.</param>
        /// <param name="output">Receives the remediated PDF. Always written, even when every
        /// step is skipped or fails.</param>
        /// <param name="options">Pipeline options; null uses <see cref="RemediationOptions"/> defaults.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        public static AccessibilityAuditReport Remediate(
            Stream input, Stream output,
            RemediationOptions options = null)
            => RemediateWithReport(input, output, options).AfterAudit;

        /// <summary>
        /// Runs the full remediation pipeline in a single call and reports everything it did.
        ///
        /// <para><b>Pipeline — eight passes, in this order.</b> A "pass" is one appended PDF
        /// revision. Every pass is skipped outright when it provably cannot change the document,
        /// so a typical untagged file costs <b>two</b> appended revisions (AutoTag + XMP) and the
        /// worst case is <b>eight</b> (ten if <see cref="RemediationOptions.SingleTextExtraction"/>
        /// is turned off). The previous implementation appended up to seven revisions
        /// unconditionally — re-serialising the whole file each time — and ran three independent
        /// full text extractions.</para>
        ///
        /// <list type="number">
        ///   <item><b>Layout pass</b> (<see cref="RemediationOptions.AutoTagIfUntagged"/>,
        ///   <see cref="RemediationOptions.MarkArtifacts"/>,
        ///   <see cref="RemediationOptions.FixReadingOrder"/>) — the only pass that extracts text.
        ///   The three layout steps are tried in that order and, by default, the first one that is
        ///   both enabled and applicable runs while the rest are skipped with a stated reason; see
        ///   <see cref="RemediationOptions.SingleTextExtraction"/>. On an untagged document nothing
        ///   is lost by that rule: AutoTag already emits Artifact elements for running
        ///   headers/footers and builds the tree in visual reading order, which is exactly what the
        ///   other two steps would have done — at the cost of two more extractions and two more
        ///   revisions.</item>
        ///   <item><b>Document metadata pass</b> — <c>/Lang</c>, <c>/Info /Title</c>,
        ///   <c>/ViewerPreferences /DisplayDocTitle</c> and <c>/MarkInfo /Marked</c> are written as
        ///   <i>one</i> revision, not one per editor call. Entries that already
        ///   hold the wanted value are not rewritten, and <c>/MarkInfo /Marked true</c> is only
        ///   written when the document really has a structure tree — claiming a document is tagged
        ///   when it is not is worse than the violation it hides. Runs after the layout pass
        ///   because AutoTag changes object numbers and creates the structure tree.</item>
        ///   <item><b>Figure alt text</b> — runs only when the structure tree actually contains a
        ///   Figure that would be given text.</item>
        ///   <item><b>Link descriptions</b> (<see cref="RemediationOptions.LinkDescriptionFactory"/>)
        ///   — writes each link's <c>/Contents</c> from the caller's text, which closes
        ///   <b>UA22</b>. Runs only when a factory is supplied: a description is never
        ///   invented.</item>
        ///   <item><b>Heading hierarchy</b> — runs only when replaying the renumbering over the
        ///   current headings would change at least one level.</item>
        ///   <item><b>Page tab order</b> (<see cref="RemediationOptions.FixTabOrder"/>) — writes
        ///   <c>/Tabs /S</c> on the pages that carry annotations, which is what closes
        ///   <b>UA09</b>. Runs only when at least one annotated page is missing the key, so a
        ///   document with no annotations (or one already tabbed) costs no revision.</item>
        ///   <item><b>Font embedding</b> (<see cref="RemediationOptions.EmbedMissingFonts"/>,
        ///   <b>off by default</b>) — embeds a font program into every font the document uses but
        ///   does not carry, which is what closes <b>UA21</b> and <b>UA25</b>. It needs a font
        ///   source (<see cref="RemediationOptions.FontEmbedding"/>) because the program is not in
        ///   the document; without one the step is skipped with a stated reason and a warning naming
        ///   the two violations it leaves open. Embedding redistributes a font — read the licensing
        ///   remark on <see cref="FontEmbeddingOptions"/> before enabling it.</item>
        ///   <item><b>PDF/UA identifier</b> (<see cref="RemediationOptions.SetPdfUaIdentifier"/>) —
        ///   writes the <c>pdfuaid:part</c> XMP declaration. Without it the pipeline can never
        ///   produce a PDF/UA-conformant file, because UA03 is unreachable by any structural fix.
        ///   Skipped when the document's existing packet is already byte-identical to the one that
        ///   would be written. Runs last so that no later pass can invalidate it.</item>
        /// </list>
        ///
        /// <para><b>Why the passes are not collapsed into one.</b> The layout steps rewrite content
        /// streams and mint new objects, so every later step must re-parse to see correct object
        /// numbers; the structure-tree editors are public API in their own right and each owns its
        /// own incremental write. What was removed is the <i>unconditional</i> re-append: a step
        /// that changes nothing now costs no revision at all.</para>
        ///
        /// <para><b>File growth.</b> Each appended revision costs its rewritten objects plus a
        /// few hundred bytes of cross-reference and trailer overhead. The XMP pass is the dominant
        /// term for small files — a conformant XMP packet with its spec-recommended padding is
        /// roughly 3 KB regardless of document size — so a one-page document grows by a few
        /// kilobytes in absolute terms and a real document by well under a percent. Set
        /// <see cref="RemediationOptions.SetPdfUaIdentifier"/> to false to skip it, at the price of
        /// leaving UA03 open.</para>
        ///
        /// <para><b>Failure handling.</b> A step that throws is recorded as a failed
        /// <see cref="RemediationStepResult"/>, the pre-step document is kept, and the pipeline
        /// carries on — the output document is always written. Encrypted documents are passed
        /// through unchanged with every step skipped, because every editor here writes clear-text
        /// incremental updates. A document that cannot be parsed at all is reported as a UA00
        /// error in both audits rather than throwing.</para>
        ///
        /// <para><b>Content ceilings.</b> The before- and after-audits honour
        /// <see cref="RemediationOptions.MaxPageContentBytes"/> and
        /// <see cref="RemediationOptions.MaxDocumentContentBytes"/>, which default to the deep
        /// checker's own defaults. A page outside them keeps every dictionary-level check, loses
        /// only the content-level ones, and is named — with the limit that stopped it — in a UA99
        /// <b>Info</b> issue in <i>both</i> audits, so a clean report can never quietly mean "that
        /// page was not examined". See <see cref="Audit(Stream,int,long)"/>.</para>
        ///
        /// <para>The returned report is <i>not</i> a conformance certificate; see
        /// <see cref="Audit(Stream)"/> for what the audits behind it do and do not prove.</para>
        /// </summary>
        /// <param name="input">Source PDF. Read in full; never modified.</param>
        /// <param name="output">Receives the remediated PDF. Always written.</param>
        /// <param name="options">Pipeline options; null uses <see cref="RemediationOptions"/> defaults.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        public static RemediationReport RemediateWithReport(
            Stream input, Stream output,
            RemediationOptions options = null)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options = options ?? new RemediationOptions();

            byte[] original = ReadAll(input);
            byte[] current  = original;

            var steps       = new List<RemediationStepResult>();

            // Both audits run under the caller's ceilings, not the checker's defaults. They used to
            // be hardwired here, so a host that had raised them for its own Audit() call still got a
            // pipeline report computed at 8 MiB per page: the oversized pages were refused — named
            // in a UA99 Info issue, never silently — but never examined, and the after-audit that
            // decides whether remediation worked was measuring a document it had only partly read.
            // ...and against the part the caller is DECLARING, not always PDF/UA-1: with
            // RemediationOptions.PdfUaPart set to 2, audits run against UA-1 alone would produce a
            // PDF/UA-2 declaration over a document judged only against PDF/UA-1 — and a report whose
            // "after" audit called it conformant. That is the same
            // self-asserted claim SetPdfUaIdentifier refuses to make for a hollow tree.
            var beforeAudit = AuditBytes(original, options.MaxPageContentBytes, options.MaxDocumentContentBytes,
                                         options.EffectiveTarget);

            var snapshot = Inspect(original);

            // An encrypted document reveals nothing to PdfDocument.Open — no catalog, so no page
            // tree, no structure tree, no figure counts — and every gate below reads exactly those.
            // Re-inspect through the caller's writer, the only thing here that can decrypt.
            if (snapshot.Parsed && snapshot.IsEncrypted && options.EncryptedRevisionWriter != null)
                snapshot = InspectThroughWriter(original, options.EncryptedRevisionWriter);

            if (!snapshot.Parsed)
            {
                SkipAll(steps, "not applicable: the document could not be parsed (" +
                               (snapshot.Error ?? "no reason recorded") +
                               "), so no step had a document to work on");
            }
            else if (snapshot.IsEncrypted && options.EncryptedRevisionWriter == null)
            {
                SkipAll(steps,
                    "not applicable: the document is encrypted; every remediation step writes a " +
                    "clear-text incremental update, which would not match the encryption dictionary. " +
                    "Set RemediationOptions.EncryptedRevisionWriter to remediate it in place, or " +
                    "decrypt the document first.");
            }
            else if (!snapshot.HasCatalog)
            {
                SkipAll(steps, "not applicable: the document has no catalog dictionary; it is malformed.");
            }
            else
            {
                current = RunPipeline(current, options, snapshot, steps);
            }

            output.Write(current, 0, current.Length);

            // Same ceilings as the before-audit, necessarily: the report's whole value is the
            // comparison between the two, and two audits taken under different limits are not
            // comparable — a violation could disappear because it was fixed or because the page it
            // was on stopped being read. The target is shared for the same reason, one step
            // stronger: two audits against different parts of ISO 14289 are not two measurements of
            // one thing at all.
            var afterAudit = ReferenceEquals(current, original)
                ? beforeAudit
                : AuditBytes(current, options.MaxPageContentBytes, options.MaxDocumentContentBytes,
                             options.EffectiveTarget);
            return new RemediationReport(beforeAudit, afterAudit, steps, original.Length, current.Length);
        }

        // ================================================================
        //  Pipeline
        // ================================================================

        private static byte[] RunPipeline(
            byte[] current,
            RemediationOptions options,
            DocumentSnapshot snapshot,
            List<RemediationStepResult> steps)
        {
            // The one place the route is decided. Everything below asks `enc != null` rather than
            // `snapshot.IsEncrypted`, because a caller may supply a writer for a document that is
            // not encrypted -- in which case the clear-text path is still correct and preserves
            // more, so the writer is simply unused.
            EncryptedRevisionWriter enc = snapshot.IsEncrypted ? options.EncryptedRevisionWriter : null;

            // ---- Pass 1: layout (the only text-extracting pass) --------------
            bool layoutPassUsed = false;
            bool autoTagRan     = false;

            if (!options.AutoTagIfUntagged)
            {
                steps.Add(SkipStep(StepAutoTag,
                    "not requested: RemediationOptions.AutoTagIfUntagged is false",
                    snapshot.HasStructTree && snapshot.StructTreeNamesContent
                        ? null
                        : "no structure element in this document names any page content, and "
                          + "auto-tagging was not requested; "
                          + "UA05 (and every check that depends on a structure tree) remains open. "
                          + "Set RemediationOptions.AutoTagIfUntagged to true, or tag the document "
                          + "in an authoring tool."));
            }
            else if (snapshot.HasStructTree && snapshot.StructTreeNamesContent)
            {
                // A tree whose elements name content is the author's, and this pipeline does not
                // replace one. The test is StructTreeNamesContent rather than HasStructTree —
                // presence is not connection — and deliberately NOT StructTreeReachesContent, which
                // additionally requires a /ParentTree: a missing parent tree is a repairable
                // omission, while the element-to-content links are unrecoverable once overwritten.
                //
                // The permissiveness is the same as elsewhere: one element naming one MCID is
                // enough. A partially built tree represents work a human did, and discarding that
                // is a far worse trade than discarding one that does nothing; a caller who wants it
                // replaced anyway can call PdfAccessibility.AutoTag directly, which supersedes any
                // tree and warns that it did.
                steps.Add(RemediationStepResult.Skip(StepAutoTag,
                    "not applicable: this document's structure elements already name page content, "
                    + "and this pipeline does not replace a tree a human built. That is not a claim "
                    + "the tree is good — check the remaining issues for UA16/UA17/UA18/UA30, which "
                    + "report the parts of it that reach nothing; UA30 is the one that fires when "
                    + "every element has a child element but no branch reaches content, which UA16 "
                    + "cannot see. To replace it regardless, call PdfAccessibility.AutoTag "
                    + "directly: it supersedes any existing tree and reports that it did."));
            }
            else
            {
                // The tree is written for the part this run declares: a PDF/UA-2 declaration over a
                // tree with no PDF 2.0 namespace is one the after-audit refuses (§8.2.5.2).
                // /Lang is the metadata step's (RemediationOptions.Language, none when that is null),
                // never the tagger's own default.
                AutoTagOptions tagOptions = (options.AutoTagOptions ?? new AutoTagOptions()).WithoutLanguage();
                if (options.PdfUaPart == 2)
                    tagOptions = tagOptions.ForTarget(PdfUaVersion.Ua2);
                // A link the tagger finds without a /Contents is the link step's to describe and,
                // when the provider gives it none, to report (BuildLinkDescriptionWarnings).
                if (options.LinkDescriptionFactory != null)
                    tagOptions = tagOptions.WithLinkDescriptionsToFollow();
                current = RunStep(current, StepAutoTag, steps,
                    enc == null
                        ? (i, o) => PdfAutoTagger.AutoTag(i, o, tagOptions)
                        : ViaEncryptedWriter(enc,
                            (doc, ff) => PdfAutoTagger.Plan(doc, ff, tagOptions),
                            pl => new PlannedRevision(pl.Objects, pl.NewInfoObjectNumber),
                            (pl, appended) => pl?.Describe(appended)),
                    r => RemediationStepResult.Ran(StepAutoTag, r?.ElementsAdded ?? 0, r?.Warnings),
                    out AutoTagResult autoTagResult);

                // `layoutPassUsed` is the second gate, and it is a claim in its own right: the two
                // steps below are recorded with SingleExtractionSkipReason, which asserts that
                // "another layout step already performed the pipeline's single text-extraction
                // pass". Setting it unconditionally made that assertion about work AutoTag may
                // never have done — PdfAutoTagger.BuildPlan returns without touching any page
                // content when the page tree cannot be read, when the document has no pages, and
                // when AutoTagOptions.RewriteContentStreams is false (it returns before
                // ComputeMedianFontSize), and a step that threw records Fail and leaves
                // `autoTagResult` null. In every one of those cases the caller silently lost
                // artifact marking *and* reading-order repair, told they had been superseded.
                //
                // So the flag is set only from evidence that the extraction really happened:
                //   * a result at all            — RunStep hands back default(T) when the step threw;
                //   * PagesProcessed > 0         — covers the null-plan and no-pages early returns;
                //   * RewriteContentStreams true — the one early return that reports the real page
                //                                  count while extracting nothing.
                layoutPassUsed = autoTagResult != null
                              && autoTagResult.PagesProcessed > 0
                              && options.AutoTagOptions?.RewriteContentStreams != false;

                // The two steps below are also skipped on the strength of this flag, and both skip
                // reasons assert that AutoTag already did their work. Only ContentMarked justifies
                // that claim: it is true exactly when a page's content stream was really rewritten
                // with marked content. "AutoTag did not throw" does not — it is true even when the
                // result says "No page could be marked, so no structure tree was written", which
                // would silently discard both remaining steps.
                autoTagRan = autoTagResult != null && autoTagResult.ContentMarked;
            }

            if (!options.MarkArtifacts)
            {
                steps.Add(RemediationStepResult.Skip(StepMarkArtifacts,
                    "not requested: RemediationOptions.MarkArtifacts is false"));
            }
            else if (autoTagRan)
            {
                steps.Add(RemediationStepResult.Skip(StepMarkArtifacts,
                    "superseded: AutoTag already marked running headers and footers as artifacts " +
                    "in the same pass"));
            }
            else if (layoutPassUsed && options.SingleTextExtraction)
            {
                steps.Add(RemediationStepResult.Skip(StepMarkArtifacts, SingleExtractionSkipReason));
            }
            else
            {
                // The detailed overload, so "0 artifacts marked" can be told apart from "3 pages
                // were refused as unsafe to rewrite" — indistinguishable through the narrow one,
                // which reports the same count and no warnings for both.
                current = RunStep(current, StepMarkArtifacts, steps,
                    enc == null
                        ? (i, o) => PdfArtifactMarker.MarkHeadersAndFootersDetailed(
                              i, o, options.HeaderMarginPt, options.FooterMarginPt)
                        : ViaEncryptedWriter(enc,
                            (doc, ff) => PdfArtifactMarker.Plan(
                                doc, ff, options.HeaderMarginPt, options.FooterMarginPt),
                            pl => new PlannedRevision(pl.Objects),
                            (pl, appended) => pl?.Describe(appended)),
                    r => RemediationStepResult.Ran(StepMarkArtifacts, r?.ArtifactsMarked ?? 0, r?.Warnings),
                    out ArtifactMarkDetail artifactDetail);

                // Same rule as the AutoTag branch above: the flag may only claim an extraction that
                // really happened. A step that threw hands back null, and a run that examined no
                // page (unreadable page tree, no pages, unreadable input) extracted nothing — in
                // either case FixReadingOrder must still run rather than be told it was superseded.
                layoutPassUsed = layoutPassUsed
                              || (artifactDetail != null && artifactDetail.PagesProcessed > 0);
            }

            if (!options.FixReadingOrder)
            {
                steps.Add(RemediationStepResult.Skip(StepFixReadingOrder,
                    "not requested: RemediationOptions.FixReadingOrder is false"));
            }
            else if (autoTagRan)
            {
                steps.Add(RemediationStepResult.Skip(StepFixReadingOrder,
                    "superseded: AutoTag already built the structure tree in visual reading order"));
            }
            else if (layoutPassUsed && options.SingleTextExtraction)
            {
                steps.Add(RemediationStepResult.Skip(StepFixReadingOrder, SingleExtractionSkipReason));
            }
            else if (!snapshot.HasStructTree)
            {
                steps.Add(RemediationStepResult.Skip(StepFixReadingOrder,
                    "not applicable: the document has no structure tree to reorder"));
            }
            else
            {
                current = RunStep(current, StepFixReadingOrder, steps,
                    enc == null
                        ? (i, o) => PdfReadingOrderAnalyzer.Fix(i, o)
                        : ViaEncryptedWriter(enc,
                            (doc, ff) => PdfReadingOrderAnalyzer.Plan(doc, ff),
                            pl => new PlannedRevision(pl.Objects),
                            (pl, appended) => pl?.Describe(appended)),
                    r => RemediationStepResult.Ran(StepFixReadingOrder, r?.ElementsReordered ?? 0));
            }

            // The layout pass rewrites content streams and mints objects, so everything below
            // needs a fresh view of the document. Re-inspecting costs one parse and saves up to
            // three pointless revisions; when the layout pass changed nothing, the original
            // snapshot is still exact and is reused.
            if (steps.Exists(s => s.Applied && IsLayoutStep(s.Name)))
            {
                // Same route as the first inspection, for the same reason: re-reading an encrypted
                // document directly would replace a populated snapshot with an empty one and send
                // every remaining step down its "not applicable" branch.
                var refreshed = enc == null ? Inspect(current) : InspectThroughWriter(current, enc);
                if (refreshed.Parsed && refreshed.HasCatalog) snapshot = refreshed;
            }

            // ---- Pass 2: document metadata (one revision for four entries) ---
            if (enc != null)
                current = ApplyDocumentMetadataEncrypted(enc, current, options, steps);
            else
                current = ApplyDocumentMetadata(current, options, steps, ref snapshot);

            // ---- Pass 3: figure alt text -------------------------------------
            if (!options.SetFigureAltText)
            {
                steps.Add(SkipStep(StepSetFigureAltText,
                    "not requested: RemediationOptions.SetFigureAltText is false",
                    snapshot.FiguresMissingAlt > 0
                        ? snapshot.FiguresMissingAlt.ToString(CultureInfo.InvariantCulture) +
                          " Figure element(s) have no /Alt and alt text was not requested; " +
                          "UA07 remains open. Set RemediationOptions.SetFigureAltText to true " +
                          "(and supply an AltTextFactory that writes real descriptions)."
                        : null));
            }
            else if (!snapshot.HasStructTree)
            {
                steps.Add(RemediationStepResult.Skip(StepSetFigureAltText,
                    "not applicable: the document has no structure tree, so it has no Figure " +
                    "elements to describe"));
            }
            else if (snapshot.FigureCount == 0)
            {
                steps.Add(RemediationStepResult.Skip(StepSetFigureAltText,
                    "not applicable: the structure tree contains no Figure elements"));
            }
            else if (options.AltTextFactory == null && snapshot.FiguresMissingAlt == 0)
            {
                steps.Add(RemediationStepResult.Skip(StepSetFigureAltText,
                    "already satisfied: all " +
                    snapshot.FigureCount.ToString(CultureInfo.InvariantCulture) +
                    " Figure element(s) already carry alternate text"));
            }
            else
            {
                // Counted, not inferred: only the provider knows whether a given figure received a
                // machine-generated placeholder or a caller-supplied description, and the step
                // result cannot tell the two apart. The count is read back in `describe`, which
                // runs after the step.
                int fabricated = 0;

                // One provider, both routes -- the encrypted path must describe exactly the figures
                // the clear-text path describes, and two copies of this closure could drift.
                Func<FigureContext, string> provider = ctx =>
                {
                    if (options.AltTextFactory != null) return options.AltTextFactory(ctx);
                    if (!string.IsNullOrEmpty(ctx.ExistingAltText)) return null;

                    fabricated++;
                    return "Figure " +
                           (ctx.FigureIndex + 1).ToString(CultureInfo.InvariantCulture);
                };

                current = RunStep(current, StepSetFigureAltText, steps,
                    enc == null
                        ? (i, o) => PdfStructureTreeEditor.SetFigureAltText(i, o, provider)
                        : ViaEncryptedWriter(enc,
                            (doc, ff) => PdfStructureTreeEditor.PlanFigureAltText(doc, ff, provider),
                            pl => new PlannedRevision(pl.Objects),
                            (pl, appended) => pl?.Describe(appended)),
                    r => RemediationStepResult.Ran(StepSetFigureAltText, r?.Updated ?? 0,
                        BuildFigureAltTextWarnings(r, fabricated)));
            }

            // ---- Pass 3b: link descriptions ----------------------------------
            if (options.LinkDescriptionFactory == null)
            {
                steps.Add(RemediationStepResult.Skip(StepSetLinkDescriptions,
                    "not requested: RemediationOptions.LinkDescriptionFactory is null, and a link's " +
                    "description is not invented"));
            }
            else
            {
                Func<LinkContext, string> describeLink = options.LinkDescriptionFactory;
                current = RunStep(current, StepSetLinkDescriptions, steps,
                    enc == null
                        ? (i, o) => PdfStructureTreeEditor.SetLinkDescriptions(i, o, describeLink)
                        : ViaEncryptedWriter(enc,
                            (doc, ff) => PdfStructureTreeEditor.PlanLinkDescriptions(doc, ff, describeLink),
                            pl => new PlannedRevision(pl.Objects),
                            (pl, appended) => pl?.Describe(appended)),
                    r => RemediationStepResult.Ran(StepSetLinkDescriptions, r?.Updated ?? 0,
                        BuildLinkDescriptionWarnings(r)));
            }

            // ---- Pass 4: heading hierarchy -----------------------------------
            if (!options.FixHeadingHierarchy)
            {
                steps.Add(RemediationStepResult.Skip(StepFixHeadingHierarchy,
                    "not requested: RemediationOptions.FixHeadingHierarchy is false"));
            }
            else if (options.PdfUaPart == 2)
            {
                // The no-skip sequence is ISO 14289-1 7.4.2's. ISO 14289-2 §8.2.5.12 states that it
                // does not include that requirement, so a part-2 target keeps the author's levels.
                steps.Add(RemediationStepResult.Skip(StepFixHeadingHierarchy,
                    "not applicable: sequential heading levels are an ISO 14289-1 (PDF/UA-1) " +
                    "requirement that ISO 14289-2 §8.2.5.12 does not include, so the document's " +
                    "heading levels are kept; call PdfAccessibility.FixHeadingHierarchy to renumber " +
                    "them regardless"));
            }
            else if (!snapshot.HasStructTree)
            {
                steps.Add(RemediationStepResult.Skip(StepFixHeadingHierarchy,
                    "not applicable: the document has no structure tree, so it has no headings " +
                    "to renumber"));
            }
            else if (!snapshot.HeadingHierarchyNeedsFixing)
            {
                steps.Add(RemediationStepResult.Skip(StepFixHeadingHierarchy,
                    DescribeHeadingHierarchySatisfied(snapshot)));
            }
            else
            {
                current = RunStep(current, StepFixHeadingHierarchy, steps,
                    enc == null
                        ? (i, o) => PdfStructureTreeEditor.FixHeadingHierarchy(i, o)
                        : ViaEncryptedWriter(enc,
                            (doc, ff) => PdfStructureTreeEditor.PlanHeadingHierarchy(doc, ff),
                            pl => new PlannedRevision(pl.Objects),
                            (pl, appended) => pl?.Describe(appended) ?? 0),
                    changed => RemediationStepResult.Ran(StepFixHeadingHierarchy, changed));
            }

            // ---- Pass 5: page tab order (/Tabs /S on annotated pages) --------
            current = ApplyTabOrder(enc, current, options, snapshot, steps);

            // ---- Pass 6: embed the fonts the document does not carry ---------
            current = ApplyFontEmbedding(enc, current, options, snapshot, steps);

            // ---- Pass 7: PDF/UA identifier (XMP) -----------------------------
            // "Does the document have a structure tree *now*" — the snapshot answers for the
            // input, and AutoTag may have built one since. ContentMarked is the only honest
            // signal that it did: AutoTag writes no tree when it could mark no content.
            //
            // The second flag is the one that matters for the claim: pass 2 recomputed
            // StructTreeReachesContent against the document as it now stands, so a tree that is
            // present but hollow answers false here and the identifier is refused — see
            // DocumentSnapshot.StructTreeReachesContent. `autoTagRan` is kept as a fallback for the
            // case where pass 2 could not re-open the document to recompute it.
            current = ApplyPdfUaIdentifier(
                enc, current, options, snapshot,
                snapshot.HasStructTree || autoTagRan,
                snapshot.StructTreeReachesContent || autoTagRan,
                steps);

            return current;
        }

        /// <summary>
        /// Why the heading pass was skipped when the replay found nothing to renumber. Kept apart
        /// from the branch so the three cases can be stated exactly: no headings at all, headings
        /// that exist but that this step does not touch (unnumbered <c>&lt;H&gt;</c>), and a
        /// numbered sequence that is already unbroken.
        /// </summary>
        private static string DescribeHeadingHierarchySatisfied(DocumentSnapshot snapshot)
        {
            string strong = snapshot.StrongHeadingCount.ToString(CultureInfo.InvariantCulture);

            if (snapshot.HeadingCount == 0)
                return snapshot.StrongHeadingCount == 0
                    ? "not applicable: the structure tree contains no headings"
                    : "not applicable: the structure tree's " + strong + " heading(s) are all " +
                      "unnumbered <H> elements, whose level comes from /Sect nesting rather than " +
                      "from the tag name; this step renumbers H1-H6 only and leaves <H> untouched";

            return "already satisfied: the " +
                   snapshot.HeadingCount.ToString(CultureInfo.InvariantCulture) +
                   " numbered heading(s) already form an unbroken level sequence" +
                   (snapshot.StrongHeadingCount == 0
                       ? string.Empty
                       : " (the " + strong + " unnumbered <H> element(s) take their level from " +
                         "/Sect nesting and are left untouched, exactly as the step would)");
        }

        private const string SingleExtractionSkipReason =
            "superseded: another layout step already performed the pipeline's single " +
            "text-extraction pass; set RemediationOptions.SingleTextExtraction to false to run " +
            "every enabled layout step";

        private static bool IsLayoutStep(string name)
            => name == StepAutoTag || name == StepMarkArtifacts || name == StepFixReadingOrder;

        /// <summary>
        /// Warnings for the figure alt-text pass: the elements the writer could not revise, and —
        /// the important one — the elements that were given <i>machine-generated</i> alternate text.
        ///
        /// <para>
        /// <b>Why the second warning exists.</b> With no
        /// <see cref="RemediationOptions.AltTextFactory"/> the pass writes "Figure 1", "Figure 2", …
        /// That string closes UA07 ("Figure elements missing /Alt") structurally while conveying
        /// nothing whatsoever to a screen-reader user, so without this warning a document whose only
        /// substantive fault was an undescribed image came back as <c>IsConformingAfter = true</c>
        /// with nothing in <see cref="RemediationReport.AllWarnings"/> to say the description had
        /// been invented. The auto-tagger already refuses to invent a link <c>/Contents</c> for the
        /// same reason and warns about UA22; figures now get the same treatment. The text is written
        /// (an undescribed figure is a hard PDF/UA failure and a placeholder at least carries the
        /// author's own numbering), but never silently.
        /// </para>
        /// </summary>
        /// <param name="result">The editor's result; null when the step produced none.</param>
        /// <param name="fabricated">
        /// How many times the pipeline's built-in provider generated a placeholder. Non-zero only
        /// when the caller supplied no <see cref="RemediationOptions.AltTextFactory"/>.
        /// </param>
        private static IReadOnlyList<string> BuildFigureAltTextWarnings(
            SetAltTextResult result, int fabricated)
        {
            if (result == null) return null;

            var warnings = new List<string>(2);

            if (result.Failed > 0)
                warnings.Add(result.Failed.ToString(CultureInfo.InvariantCulture) +
                             " Figure element(s) could not be given alternate text " +
                             "(their structure element is not an indirect object).");

            // Every element the writer updated in this branch carries generated text: the built-in
            // provider returns null (skip) for a figure that already had a description.
            if (fabricated > 0 && result.Updated > 0)
                warnings.Add(result.Updated.ToString(CultureInfo.InvariantCulture) +
                             " Figure element(s) were given generated placeholder alternate text " +
                             "(\"Figure 1\", \"Figure 2\", …) because no " +
                             "RemediationOptions.AltTextFactory was supplied. That satisfies UA07 " +
                             "structurally but describes nothing to a screen-reader user, so the " +
                             "document is not usable even though the check passes; supply " +
                             "RemediationOptions.AltTextFactory with real descriptions and have a " +
                             "human review them.");

            return warnings.Count > 0 ? warnings : null;
        }

        /// <summary>
        /// The link step's warnings: the links it could not revise, and the links the provider gave
        /// no description that have none of their own. The second is the auto-tagger's UA22 warning
        /// said after the step that could have closed it, so it names only what is still open.
        /// </summary>
        /// <param name="result">The editor's result; null when the step produced none.</param>
        private static IReadOnlyList<string> BuildLinkDescriptionWarnings(SetAltTextResult result)
        {
            if (result == null) return null;

            var warnings = new List<string>(2);

            if (result.Failed > 0)
                warnings.Add(result.Failed.ToString(CultureInfo.InvariantCulture) +
                             " link annotation(s) could not be given a description (a direct " +
                             "dictionary in /Annots, or the provider threw); UA22 stays open for them.");

            if (result.LeftWithoutDescription > 0)
                warnings.Add(result.LeftWithoutDescription.ToString(CultureInfo.InvariantCulture) +
                             " link annotation(s) have no /Contents alternate description: " +
                             "RemediationOptions.LinkDescriptionFactory returned none for them, and a " +
                             "description is not invented, so an audit will report UA22 for them.");

            return warnings.Count > 0 ? warnings : null;
        }

        /// <summary>
        /// Records every pipeline step as skipped for a single shared reason (unparseable,
        /// encrypted or catalog-less input). The document is passed through untouched.
        /// </summary>
        private static void SkipAll(List<RemediationStepResult> steps, string reason)
        {
            AccessibilityDiagnostics.Report(
                DiagnosticOperation + ".RemediateWithReport",
                "The whole pipeline was skipped: " + reason);

            steps.Add(RemediationStepResult.Skip(StepAutoTag, reason));
            steps.Add(RemediationStepResult.Skip(StepMarkArtifacts, reason));
            steps.Add(RemediationStepResult.Skip(StepFixReadingOrder, reason));
            steps.Add(RemediationStepResult.Skip(StepSetDocumentMetadata, reason));
            steps.Add(RemediationStepResult.Skip(StepSetFigureAltText, reason));
            steps.Add(RemediationStepResult.Skip(StepSetLinkDescriptions, reason));
            steps.Add(RemediationStepResult.Skip(StepFixHeadingHierarchy, reason));
            steps.Add(RemediationStepResult.Skip(StepSetTabOrder, reason));
            steps.Add(RemediationStepResult.Skip(StepEmbedFonts, reason));
            steps.Add(RemediationStepResult.Skip(StepSetPdfUaIdentifier, reason));
        }

        /// <summary>
        /// A skipped step that also carries a forward-looking warning.
        ///
        /// <para>
        /// <see cref="RemediationStepResult.Skip"/> takes no warnings, so a step skipped as
        /// "not requested" could state its reason but not the <i>consequence</i> — that the
        /// violation it would have closed is still open in the after-audit. That consequence is
        /// what a caller reading <see cref="RemediationReport.AllWarnings"/> needs in order to
        /// know what to do next, so it is attached here rather than left for the reader to infer
        /// by cross-referencing a step list against an issue list.
        /// </para>
        /// </summary>
        /// <param name="name">Step name.</param>
        /// <param name="reason">Why the step did not run.</param>
        /// <param name="warning">The consequence, or null when there is none to state.</param>
        private static RemediationStepResult SkipStep(string name, string reason, string warning)
            => new RemediationStepResult(
                name,
                applied: false,
                skipped: true,
                skipReason: reason,
                itemsChanged: 0,
                warnings: string.IsNullOrEmpty(warning) ? null : new[] { warning });

        /// <summary>
        /// Runs one pipeline step over <paramref name="current"/> and records its outcome.
        ///
        /// <para>Returns the produced document, or <paramref name="current"/> unchanged when the
        /// step threw or produced something that cannot be an incremental update (empty, or
        /// shorter than its input — an appended revision can only grow the file). Keeping the
        /// pre-step bytes is what lets the pipeline carry on after a failed step instead of
        /// writing a truncated document.</para>
        /// </summary>
        private static byte[] RunStep<TResult>(
            byte[] current,
            string name,
            List<RemediationStepResult> steps,
            Func<Stream, Stream, TResult> action,
            Func<TResult, RemediationStepResult> describe)
            => RunStep(current, name, steps, action, describe, out _);

        /// <summary>
        /// As <see cref="RunStep{TResult}(byte[],string,List{RemediationStepResult},Func{Stream,Stream,TResult},Func{TResult,RemediationStepResult})"/>,
        /// additionally handing back the step's own result object.
        ///
        /// <para>
        /// Needed because "the step ran" and "the step changed anything" are different facts, and
        /// later steps are skipped on the strength of the second. Comparing byte arrays cannot tell
        /// them apart: the success path always allocates a fresh array, so a reference comparison
        /// answers "did not throw", not "did something".
        /// <paramref name="captured"/> is <c>default</c> when the step failed.
        /// </para>
        /// </summary>
        private static byte[] RunStep<TResult>(
            byte[] current,
            string name,
            List<RemediationStepResult> steps,
            Func<Stream, Stream, TResult> action,
            Func<TResult, RemediationStepResult> describe,
            out TResult captured)
        {
            captured = default;
            try
            {
                using var inMs  = new MemoryStream(current, writable: false);
                using var outMs = new MemoryStream(current.Length + 4096);

                TResult result = action(inMs, outMs);
                byte[] produced = outMs.ToArray();

                if (produced.Length < current.Length)
                {
                    string detail = produced.Length == 0
                        ? "the step wrote no output"
                        : "the step wrote " + produced.Length.ToString(CultureInfo.InvariantCulture) +
                          " bytes, fewer than the " + current.Length.ToString(CultureInfo.InvariantCulture) +
                          " it was given, so it cannot be an incremental update";

                    AccessibilityDiagnostics.Report(
                        DiagnosticOperation + ".RunStep",
                        name + ": " + detail + "; the previous revision was kept.");
                    steps.Add(RemediationStepResult.Fail(name,
                        detail + "; the previous revision was kept"));
                    return current;
                }

                captured = result;
                steps.Add(describe(result));
                return produced;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".RunStep", name + " threw; the previous revision was kept.", ex);
                steps.Add(RemediationStepResult.Fail(name, ex.Message));
                return current;
            }
        }

        /// <summary>
        /// Adapts a plan-producing writer into the <c>(input, output)</c> shape
        /// <see cref="RunStep{TResult}(byte[],string,List{RemediationStepResult},Func{Stream,Stream,TResult},Func{TResult,RemediationStepResult})"/>
        /// runs, by routing the plan through the caller's <see cref="EncryptedRevisionWriter"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>This is what keeps one pipeline rather than two.</b> Every gate above — the
        /// requested/applicable/superseded skips, the single-extraction rule, the snapshot refresh —
        /// is identical for an encrypted document, and duplicating it into a parallel encrypted
        /// pipeline is precisely the divergence this repository keeps finding. Only the *action*
        /// differs, so only the action is substituted.</para>
        /// <para><b>Each plan type's own <c>Describe(bool)</c> returns the same result type the
        /// clear-text entry point returns</b>, so both routes hand the same <c>describe</c> the same
        /// shape and a step cannot report one thing when encrypted and another when not.</para>
        /// <para>The <c>revisionWasAppended</c> answer is taken from the output length, which is the
        /// only available signal: the encrypted writer does not throw when a revision is lost, it
        /// falls back to the original bytes. This is the same rule <c>RunStep</c> applies afterwards,
        /// asked one moment earlier because <c>Describe</c> needs the answer.</para>
        /// </remarks>
        private static Func<Stream, Stream, TResult> ViaEncryptedWriter<TPlan, TResult>(
            EncryptedRevisionWriter writer,
            Func<PdfDocument, int, TPlan> plan,
            Func<TPlan, PlannedRevision> revision,
            Func<TPlan, bool, TResult> describe)
            where TPlan : class
        {
            return (input, output) =>
            {
                TPlan captured = null;

                writer(input, output, (doc, firstFree) =>
                {
                    captured = plan(doc, firstFree);
                    return revision(captured) ?? PlannedRevision.Nothing;
                });

                bool appended = output.Length > input.Length;
                return describe(captured, appended);
            };
        }

        /// <summary>
        /// Records that a step has no encrypted route, and was therefore not run.
        /// </summary>
        /// <remarks>
        /// <b>Fail closed, always.</b> Running a clear-text writer against an encrypted document
        /// would append a revision the encryption dictionary does not describe — the result is a
        /// file no reader can open, from an operation the caller asked for in order to *improve*
        /// the document. A recorded skip is the only acceptable outcome.
        /// </remarks>
        private static void SkipUnencryptable(
            List<RemediationStepResult> steps, string name, string consequence)
        {
            steps.Add(RemediationStepResult.Skip(name,
                "not applicable: this step has no encrypted route, and running its clear-text " +
                "writer against an encrypted document would append a revision the encryption " +
                "dictionary does not describe, producing a file no reader can open. " + consequence));
        }

        /// <summary>
        /// The document-metadata pass for an <b>encrypted</b> document: <c>/Lang</c> and
        /// <c>/Info /Title</c> (which carries <c>/ViewerPreferences /DisplayDocTitle</c> with it),
        /// as up to two appended revisions instead of the clear-text path's one.
        /// </summary>
        /// <remarks>
        /// <para><b>Why three revisions rather than one.</b> The clear-text pass builds a single
        /// revision by hand because it can see the whole document at once. Here each entry has its
        /// own <c>Plan</c>, and composing three plans into one revision would require each to see
        /// the previous one's staged-but-unwritten changes — two of the three revise the same
        /// catalog, so the last would silently drop the first two. Three cycles are correct by
        /// construction; the cost is two extra appended revisions on an in-memory copy.</para>
        /// <para><b><c>/DisplayDocTitle</c> is not a cycle of its own</b>, because
        /// <c>PlanDocumentTitle</c> already writes it: it asks a reader to display a title, so it
        /// belongs in the same revision as the title rather than in one that could land without
        /// it.</para>
        /// <para>Reported as the <b>one</b> step the clear-text path reports, so the two routes give
        /// a caller the same shape of report. A count above zero still implies the output differs
        /// from the input: each cycle is counted only once its revision has actually landed.</para>
        /// </remarks>
        private static byte[] ApplyDocumentMetadataEncrypted(
            EncryptedRevisionWriter enc,
            byte[] current,
            RemediationOptions options,
            List<RemediationStepResult> steps)
        {
            bool wantsLanguage = !string.IsNullOrEmpty(options.Language);
            bool wantsTitle    = !string.IsNullOrEmpty(options.Title);

            var written       = new List<string>();
            var notRequested  = new List<string>();
            var satisfied     = new List<string>();
            var writeFailures = new List<string>();
            int changed = 0;

            if (!wantsLanguage) notRequested.Add("/Lang (RemediationOptions.Language was not set)");
            else
            {
                current = RunMetadataCycle(enc, current, "/Lang",
                    doc => string.Equals(ReadCatalogLanguage(doc), options.Language, StringComparison.Ordinal),
                    (doc, ff) => PdfStructureTreeEditor.PlanDocumentLanguage(doc, ff, options.Language),
                    written, satisfied, writeFailures, ref changed);
            }

            if (!wantsTitle)
            {
                notRequested.Add("/Info /Title (RemediationOptions.Title was not set)");

                // The document's own /Info /Title, carried to a packet holding no dc:title, which is
                // where ISO 14289-1 7.1 reads it (TitleToWrite) — as the clear-text route carries it.
                current = RunMetadataCycle(enc, current, "dc:title (from the document's /Info /Title)",
                    doc => PdfUaXmpWriter.TitleToWrite(
                               null, ReadXmpPacket(doc, doc.Catalog?.Dictionary), GetDocumentTitle(doc)) == null,
                    (doc, ff) => PdfStructureTreeEditor.PlanDocumentTitle(doc, ff, GetDocumentTitle(doc)),
                    written, new List<string>(), writeFailures, ref changed);
            }
            else
            {
                // PlanDocumentTitle writes /ViewerPreferences /DisplayDocTitle and the XMP dc:title in
                // the same plan -- a title a reader will not display, or one in /Info alone, still
                // fails UA04, so the three belong in one revision.
                current = RunMetadataCycle(enc, current, "/Info /Title (with /DisplayDocTitle and dc:title)",
                    doc => string.Equals(GetDocumentTitle(doc), options.Title, StringComparison.Ordinal)
                        && IsDisplayDocTitleTrue(doc, doc.Catalog?.Dictionary)
                        && XmpCarriesTitle(doc, options.Title),
                    (doc, ff) => PdfStructureTreeEditor.PlanDocumentTitle(doc, ff, options.Title),
                    written, satisfied, writeFailures, ref changed);
            }

            // An entry not asked for is read from the decrypted document, so a missing one is said
            // as the clear-text route says it.
            var stepWarnings = new List<string>(writeFailures);
            if (!wantsLanguage || !wantsTitle)
                stepWarnings.AddRange(OpenMetadataIssuesEncrypted(enc, current, !wantsLanguage, !wantsTitle));

            if (changed == 0)
            {
                steps.Add(new RemediationStepResult(
                    StepSetDocumentMetadata,
                    applied: false,
                    skipped: true,
                    skipReason: writeFailures.Count > 0
                        ? "nothing could be written: " + string.Join("; ", writeFailures)
                        : DescribeUnwrittenMetadata(satisfied, notRequested, new List<string>()),
                    itemsChanged: 0,
                    warnings: stepWarnings.Count > 0 ? stepWarnings : null));
                return current;
            }

            steps.Add(RemediationStepResult.Ran(
                StepSetDocumentMetadata, changed,
                stepWarnings.Count > 0 ? stepWarnings : null));
            return current;
        }

        /// <summary>
        /// Whether the catalog's XMP packet already carries <paramref name="title"/> as its
        /// <c>dc:title</c>: merging it in changes nothing (<see cref="PdfUaXmpWriter.BuildTitlePacket"/>,
        /// the merge <c>DocumentTitleXmp</c> writes).
        /// </summary>
        private static bool XmpCarriesTitle(PdfDocument doc, string title)
        {
            string packet = ReadXmpPacket(doc, doc.Catalog?.Dictionary);
            return packet != null
                && string.Equals(PdfUaXmpWriter.BuildTitlePacket(packet, title), packet, StringComparison.Ordinal);
        }

        /// <summary>The metadata step's notice for a language neither asked for nor present.</summary>
        private const string LanguageStaysOpen =
            "no Language was supplied and the catalog has no /Lang, so UA02 remains open; set " +
            "RemediationOptions.Language (e.g. \"en-US\") to close it.";

        /// <summary>The metadata step's notice for a title neither asked for nor present.</summary>
        private const string TitleStaysOpen =
            "no Title was supplied and the /Info dictionary carries no /Title, so UA04 (\"requires " +
            "a non-empty document title\") remains open unless the XMP packet already carries a " +
            "dc:title; set RemediationOptions.Title to close it.";

        /// <summary>
        /// The encrypted route's <see cref="LanguageStaysOpen"/> and <see cref="TitleStaysOpen"/>:
        /// the entries the caller did not ask for, read from the document the writer decrypts, with
        /// nothing appended. A document the writer cannot open is said, not taken for one that
        /// carries both.
        /// </summary>
        private static List<string> OpenMetadataIssuesEncrypted(
            EncryptedRevisionWriter enc, byte[] current, bool language, bool title)
        {
            var issues = new List<string>(2);
            string existingLanguage = null, existingTitle = null;
            bool read = false;
            try
            {
                using var input  = new MemoryStream(current, writable: false);
                using var output = new MemoryStream();
                enc(input, output, (doc, firstFree) =>
                {
                    existingLanguage = ReadCatalogLanguage(doc);
                    existingTitle    = GetDocumentTitle(doc);
                    read = true;
                    return PlannedRevision.Nothing;
                });
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".ApplyDocumentMetadataEncrypted",
                    "the document could not be read for its /Lang and /Info /Title", ex);
            }

            if (!read)
            {
                issues.Add("the encrypted document could not be read for the entries not asked " +
                           "for (/Lang, /Info /Title), so whether UA02 or UA04 remains open is not known.");
                return issues;
            }

            if (language && string.IsNullOrEmpty(existingLanguage)) issues.Add(LanguageStaysOpen);
            if (title && string.IsNullOrEmpty(existingTitle)) issues.Add(TitleStaysOpen);
            return issues;
        }

        /// <summary>
        /// One decrypt/plan/append cycle for a single metadata entry, counted only when its revision
        /// actually landed.
        /// </summary>
        private static byte[] RunMetadataCycle(
            EncryptedRevisionWriter enc,
            byte[] current,
            string what,
            Func<PdfDocument, bool> alreadySatisfied,
            Func<PdfDocument, int, StructureTreePlan> plan,
            List<string> written,
            List<string> satisfied,
            List<string> writeFailures,
            ref int changed)
        {
            try
            {
                using var input  = new MemoryStream(current, writable: false);
                using var output = new MemoryStream(current.Length + 4096);

                StructureTreePlan captured = null;
                bool wasSatisfied = false;

                enc(input, output, (doc, firstFree) =>
                {
                    // Asked HERE, against the decrypted document, because it is the only place this
                    // assembly can read the current value. The plan writers do not make this check
                    // themselves -- PlanDocumentLanguage writes unconditionally -- so without it the
                    // encrypted route appends a revision setting a value that is already set and
                    // reports it as a change, while the clear-text pass correctly reports
                    // "already satisfied". The cross-route agreement test caught exactly that.
                    if (alreadySatisfied != null && alreadySatisfied(doc))
                    {
                        wasSatisfied = true;
                        return PlannedRevision.Nothing;
                    }

                    captured = plan(doc, firstFree);
                    return captured == null
                        ? PlannedRevision.Nothing
                        : new PlannedRevision(captured.Objects, captured.NewInfoObjectNumber);
                });

                if (wasSatisfied)
                {
                    satisfied.Add(what + " already holds the requested value");
                    return current;
                }

                // The plan staged nothing: the entry already holds the wanted value, or the
                // document cannot carry it. Either way nothing was appended and nothing is claimed.
                if (captured == null || !captured.WouldChangeDocument)
                {
                    satisfied.Add(what + " needed no change");
                    return current;
                }

                byte[] produced = output.ToArray();
                if (produced.Length <= current.Length)
                {
                    writeFailures.Add(what + " was not written: the appended revision did not reach " +
                                      "the output, so the previous revision was kept");
                    return current;
                }

                written.Add(what);
                changed++;
                return produced;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".ApplyDocumentMetadataEncrypted",
                    what + " could not be written; the previous revision was kept.", ex);
                writeFailures.Add(what + " was not written: " + ex.Message);
                return current;
            }
        }

        // ----------------------------------------------------------------
        //  Pass 2 — merged document metadata
        // ----------------------------------------------------------------

        /// <summary>
        /// Writes <c>/Lang</c>, <c>/Info /Title</c>, <c>/ViewerPreferences /DisplayDocTitle</c>
        /// and <c>/MarkInfo /Marked</c> in a single appended revision, skipping any entry that
        /// already holds the wanted value. Refreshes <paramref name="snapshot"/> with the XMP
        /// packet it saw so the later PDF/UA-identifier pass does not need its own parse.
        ///
        /// <para>
        /// <b>Each of the four entries is accounted for separately</b> — already satisfied, not
        /// requested, not applicable, or written — and the skip reason for a pass that wrote
        /// nothing names them individually. A single blanket "the catalog and /Info already carry
        /// the requested values" was printed even when the caller supplied no title at all, which
        /// read as reassurance that the document had one while the same run's audit reported UA04
        /// against it.
        /// </para>
        /// </summary>
        private static byte[] ApplyDocumentMetadata(
            byte[] current,
            RemediationOptions options,
            List<RemediationStepResult> steps,
            ref DocumentSnapshot snapshot)
        {
            bool wantsLanguage = !string.IsNullOrEmpty(options.Language);
            bool wantsTitle    = !string.IsNullOrEmpty(options.Title);

            try
            {
                using var ms  = new MemoryStream(current, writable: false);
                using var doc = PdfDocument.Open(ms);

                if (doc.IsEncrypted)
                {
                    steps.Add(RemediationStepResult.Skip(StepSetDocumentMetadata,
                        "not applicable: the document is encrypted, so no clear-text metadata " +
                        "revision can be appended"));
                    return current;
                }

                var catalog = doc.Catalog?.Dictionary;
                if (catalog == null)
                {
                    steps.Add(RemediationStepResult.Skip(StepSetDocumentMetadata,
                        "not applicable: the document has no catalog dictionary"));
                    return current;
                }

                // Cache the packet for pass 6 while the document is open anyway, with the /Info
                // /Title that pass carries into a packet holding no dc:title.
                snapshot.XmpPacket     = ReadXmpPacket(doc, catalog);
                snapshot.XmpPacketRead = true;
                snapshot.InfoTitle     = GetDocumentTitle(doc);

                var index = PdfObjectIndex.Build(doc);
                int catalogObjectNumber = index.GetObjectNumber(catalog);

                var modified   = new Dictionary<int, PdfObject>();
                var newObjects = new Dictionary<int, PdfObject>();
                int newInfoObjectNumber = -1;
                int changed = 0;

                // Four entries, four independent outcomes. Kept apart so the skip reason can say
                // which of them was verified present and which was never asked for — the two are
                // not interchangeable, and conflating them is what made a title-less document
                // report "already carries the requested values".
                var writeFailures = new List<string>();  // wanted, but the document would not take it
                var satisfied     = new List<string>();  // verified already in the wanted state
                var notRequested  = new List<string>();  // the caller supplied no value
                var notApplicable = new List<string>();  // the entry is meaningless for this document
                var openIssues    = new List<string>();  // "…and the violation stays open"

                PdfDictionary newCatalog = null;
                PdfDictionary Catalog()
                {
                    return newCatalog ?? (newCatalog = CloneDictionary(catalog));
                }

                // ---- /Lang ------------------------------------------------
                string existingLanguage = GetTextValue(doc, catalog[PdfName.Lang]);
                if (!wantsLanguage)
                {
                    notRequested.Add("no Language was supplied, so /Lang was left as it is" +
                                     (string.IsNullOrEmpty(existingLanguage)
                                         ? " (the catalog has none)"
                                         : " (the catalog holds \"" + existingLanguage + "\")"));

                    if (string.IsNullOrEmpty(existingLanguage))
                        openIssues.Add(LanguageStaysOpen);
                }
                else if (string.Equals(existingLanguage, options.Language, StringComparison.Ordinal))
                {
                    satisfied.Add("/Lang already holds the requested \"" + options.Language + "\"");
                }
                else if (catalogObjectNumber > 0)
                {
                    Catalog()[PdfName.Lang] = Editing.PdfStructureTreeEditor.MakeTextString(options.Language);
                    changed++;
                }
                else
                {
                    writeFailures.Add("/Lang was not written: the document catalog is not an " +
                                      "indirect object, so it cannot be revised.");
                }

                // ---- /MarkInfo /Marked ------------------------------------
                // Only for a document that really has a structure tree *and whose tree reaches
                // content*: asserting /Marked true (ISO 32000-1 §14.7.1 — "this document follows
                // the Tagged PDF conventions") on an untagged file trades a true violation for a
                // false claim, and a hollow tree is untagged in every sense assistive technology
                // cares about. Recomputed from the document being written rather than taken from
                // the snapshot, so the layout pass's output is judged, not its input; pass 7 reads
                // the answer back off the snapshot.
                var structTreeRoot = ResolveDictionary(doc, catalog[PdfName.StructTreeRoot]);
                bool reachesContent = StructTreeReachesContent(doc, structTreeRoot, index);
                snapshot.StructTreeReachesContent = reachesContent;

                if (structTreeRoot == null)
                {
                    notApplicable.Add("/MarkInfo /Marked was not written because the document has " +
                                      "no structure tree — declaring an untagged document tagged " +
                                      "hides UA01 instead of fixing it");
                }
                else if (!reachesContent)
                {
                    notApplicable.Add("/MarkInfo /Marked was not written because the document's " +
                                      "structure tree reaches no page content (no /ParentTree, or " +
                                      "no element holding an MCID, /MCR or /OBJR) — such a tree is " +
                                      "untagged as far as assistive technology is concerned, so " +
                                      "declaring the document tagged would hide UA01 rather than " +
                                      "fix it");

                    openIssues.Add("the document's structure tree reaches no page content, so " +
                                   "/MarkInfo /Marked was left unwritten and UA14/UA16/UA30 remain " +
                                   "open; run PdfAccessibility.AutoTag, which supersedes a hollow " +
                                   "tree with a generated one, or re-tag the document in an " +
                                   "authoring tool.");
                }
                else if (IsMarkedTrue(doc, catalog))
                {
                    satisfied.Add("/MarkInfo /Marked is already true");
                }
                else
                {
                    var markInfoObj = catalog[PdfName.MarkInfo];
                    var newMarkInfo = CloneDictionary(ResolveDictionary(doc, markInfoObj));
                    newMarkInfo[PdfName.Marked] = new PdfBoolean(true);

                    if (markInfoObj is PdfReference markInfoRef && markInfoRef.ReferenceObjectNumber > 0)
                    {
                        modified[markInfoRef.ReferenceObjectNumber] = newMarkInfo;
                        changed++;
                    }
                    else if (catalogObjectNumber > 0)
                    {
                        Catalog()[PdfName.MarkInfo] = newMarkInfo;
                        changed++;
                    }
                    else
                    {
                        writeFailures.Add("/MarkInfo /Marked was not written: the document catalog is " +
                                          "not an indirect object, so it cannot be revised.");
                    }
                }

                // ---- /Info /Title -----------------------------------------
                string existingTitle = GetDocumentTitle(doc);
                if (!wantsTitle)
                {
                    notRequested.Add("no Title was supplied, so /Info /Title was left as it is" +
                                     (string.IsNullOrEmpty(existingTitle)
                                         ? " (the document has none)"
                                         : " (the document holds one already)"));

                    if (string.IsNullOrEmpty(existingTitle))
                        openIssues.Add(TitleStaysOpen);
                }
                else if (string.Equals(existingTitle, options.Title, StringComparison.Ordinal))
                {
                    satisfied.Add("/Info /Title already holds the requested title");
                }
                else
                {
                    var infoObj = doc.Trailer?[PdfName.Info];
                    var info    = infoObj != null ? ResolveDictionary(doc, infoObj) : null;

                    if (info != null)
                    {
                        int infoObjectNumber = index.GetObjectNumber(info);
                        if (infoObjectNumber > 0)
                        {
                            var newInfo = CloneDictionary(info);
                            // MakeTextString is a one-line delegation to `new PdfString(string)`, the
                            // §7.9.2.2 encoder: it emits UTF-16BE with the mandatory FE FF marker for
                            // anything a single PDFDocEncoding byte cannot carry, so a Hebrew or CJK title
                            // survives. A destroyed /Info /Title beside a correct XMP dc:title would be a
                            // self-contradictory document (Matterhorn 06-003).
                            newInfo[PdfName.Title] = Editing.PdfStructureTreeEditor.MakeTextString(options.Title);
                            modified[infoObjectNumber] = newInfo;
                            changed++;
                            existingTitle = options.Title;
                        }
                        else
                        {
                            writeFailures.Add("/Info /Title was not written: the /Info dictionary is " +
                                              "not an indirect object, so it cannot be revised.");
                        }
                    }
                    else
                    {
                        newInfoObjectNumber = index.AllocateObjectNumber();
                        var createdInfo = new PdfDictionary();
                        createdInfo[PdfName.Title] = Editing.PdfStructureTreeEditor.MakeTextString(options.Title);
                        newObjects[newInfoObjectNumber] = createdInfo;
                        changed++;
                        existingTitle = options.Title;
                    }
                }

                // ---- XMP dc:title ------------------------------------------
                // ISO 14289-1 7.1 reads the title from the XMP dc:title alone: the caller's title, or
                // the document's own /Info /Title where the packet has none (TitleToWrite).
                string xmpTitle = PdfUaXmpWriter.TitleToWrite(
                    wantsTitle ? options.Title : null, snapshot.XmpPacket, existingTitle);
                if (catalogObjectNumber > 0
                    && DocumentTitleXmp.Stage(doc, index, catalog, Catalog, xmpTitle, modified, newObjects,
                        DiagnosticOperation + ".ApplyDocumentMetadata"))
                    changed++;

                // ---- /ViewerPreferences /DisplayDocTitle -------------------
                // Only meaningful once the document has a title to display.
                if (string.IsNullOrEmpty(existingTitle))
                {
                    notApplicable.Add("/ViewerPreferences /DisplayDocTitle was not set because the " +
                                      "document has no title for a reader to display");
                }
                else if (IsDisplayDocTitleTrue(doc, catalog))
                {
                    satisfied.Add("/ViewerPreferences /DisplayDocTitle is already true");
                }
                else
                {
                    var viewerPrefsObj  = catalog[PdfName.ViewerPreferences];
                    var newViewerPrefs  = CloneDictionary(ResolveDictionary(doc, viewerPrefsObj));
                    newViewerPrefs[PdfName.DisplayDocTitle] = new PdfBoolean(true);

                    if (viewerPrefsObj is PdfReference viewerPrefsRef && viewerPrefsRef.ReferenceObjectNumber > 0)
                    {
                        modified[viewerPrefsRef.ReferenceObjectNumber] = newViewerPrefs;
                        changed++;
                    }
                    else if (catalogObjectNumber > 0)
                    {
                        Catalog()[PdfName.ViewerPreferences] = newViewerPrefs;
                        changed++;
                    }
                    else
                    {
                        writeFailures.Add("/ViewerPreferences /DisplayDocTitle was not written: the " +
                                          "document catalog is not an indirect object, so it cannot be revised.");
                    }
                }

                if (newCatalog != null && catalogObjectNumber > 0)
                    modified[catalogObjectNumber] = newCatalog;

                // Write failures first (they are about this step), then the "…and the violation
                // stays open" notices (they are about what the caller should do next).
                var stepWarnings = new List<string>(writeFailures.Count + openIssues.Count);
                stepWarnings.AddRange(writeFailures);
                stepWarnings.AddRange(openIssues);

                if (changed == 0)
                {
                    steps.Add(new RemediationStepResult(
                        StepSetDocumentMetadata,
                        applied: false,
                        skipped: true,
                        skipReason: writeFailures.Count > 0
                            ? "nothing could be written: " + string.Join("; ", writeFailures)
                            : DescribeUnwrittenMetadata(satisfied, notRequested, notApplicable),
                        itemsChanged: 0,
                        warnings: stepWarnings.Count > 0 ? stepWarnings : null));
                    return current;
                }

                using var outMs = new MemoryStream(current.Length + 4096);
                DocumentRevisionWriter.Write(
                    outMs, current, doc, index, modified, newObjects, newInfoObjectNumber);
                byte[] produced = outMs.ToArray();

                if (produced.Length <= current.Length)
                {
                    // The writer reports its own failures and falls back to the original bytes.
                    string detail = "the incremental update could not be appended" +
                                    (AccessibilityDiagnostics.LastError != null
                                        ? ": " + AccessibilityDiagnostics.LastError
                                        : ".");
                    steps.Add(RemediationStepResult.Fail(StepSetDocumentMetadata, detail));
                    return current;
                }

                steps.Add(RemediationStepResult.Ran(
                    StepSetDocumentMetadata, changed, stepWarnings.Count > 0 ? stepWarnings : null));
                return produced;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".ApplyDocumentMetadata",
                    "The document metadata pass failed; the previous revision was kept.", ex);
                steps.Add(RemediationStepResult.Fail(StepSetDocumentMetadata, ex.Message));
                return current;
            }
        }

        /// <summary>
        /// Builds the skip reason for a metadata pass that wrote nothing, naming each entry under
        /// the heading that actually applies to it. Never claims a value is present unless the
        /// pass verified it.
        /// </summary>
        private static string DescribeUnwrittenMetadata(
            List<string> satisfied, List<string> notRequested, List<string> notApplicable)
        {
            var parts = new List<string>(3);

            if (satisfied.Count > 0)
                parts.Add("already satisfied — " + string.Join("; ", satisfied));
            if (notRequested.Count > 0)
                parts.Add("not requested — " + string.Join("; ", notRequested));
            if (notApplicable.Count > 0)
                parts.Add("not applicable — " + string.Join("; ", notApplicable));

            // Unreachable in practice (every entry lands in exactly one bucket), but a blank
            // reason would be the one output worse than an overstated one.
            return parts.Count > 0
                ? string.Join(" | ", parts)
                : "no metadata entry needed writing";
        }

        // ----------------------------------------------------------------
        //  Pass 5 — page tab order (/Tabs /S)
        // ----------------------------------------------------------------

        /// <summary>
        /// Writes <c>/Tabs /S</c> on every page that carries annotations and does not already
        /// declare it — the step that closes <b>UA09</b>.
        ///
        /// <para>
        /// The audit has always emitted UA09 with the advice "set /Tabs /S on every page that has
        /// annotations", but no pipeline step did it: the only implementation lived in a separate
        /// conformance plugin that a caller of this facade has no reason to know about, so a
        /// remediated document with annotations failed UA09 for ever. This closes that.
        /// </para>
        ///
        /// <para>
        /// The step is skipped without appending a revision when the snapshot proves it would
        /// change nothing. The snapshot's condition is the same one
        /// <see cref="PdfStructureTreeEditor.SetStructureTabOrder"/> writes on, so "skipped"
        /// here can never mean "would have changed something".
        /// </para>
        /// </summary>
        private static byte[] ApplyTabOrder(
            EncryptedRevisionWriter enc,
            byte[] current,
            RemediationOptions options,
            DocumentSnapshot snapshot,
            List<RemediationStepResult> steps)
        {
            var c = CultureInfo.InvariantCulture;

            if (!options.FixTabOrder)
            {
                steps.Add(SkipStep(StepSetTabOrder,
                    "not requested: RemediationOptions.FixTabOrder is false",
                    snapshot.PagesMissingStructureTabOrder > 0
                        ? snapshot.PagesMissingStructureTabOrder.ToString(c) +
                          " page(s) carrying annotations do not declare /Tabs /S and the tab-order " +
                          "step was not requested, so UA09 remains open; set " +
                          "RemediationOptions.FixTabOrder to true to close it."
                        : null));
                return current;
            }

            if (snapshot.AnnotatedPageCount == 0)
            {
                steps.Add(RemediationStepResult.Skip(StepSetTabOrder,
                    "not applicable: no page in the document carries an annotation, and PDF/UA-1 " +
                    "(ISO 14289-1, 7.18.3) requires /Tabs only on annotated pages"));
                return current;
            }

            if (snapshot.PagesMissingStructureTabOrder == 0)
            {
                steps.Add(RemediationStepResult.Skip(StepSetTabOrder,
                    "already satisfied: all " + snapshot.AnnotatedPageCount.ToString(c) +
                    " page(s) that carry annotations already declare /Tabs /S"));
                return current;
            }

            return RunStep(current, StepSetTabOrder, steps,
                enc == null
                    ? (i, o) => PdfStructureTreeEditor.SetStructureTabOrder(i, o)
                    : ViaEncryptedWriter(enc,
                        (doc, ff) => PdfStructureTreeEditor.PlanStructureTabOrder(doc, ff),
                        pl => new PlannedRevision(pl.Objects),
                        (pl, appended) => pl?.Describe(appended) ?? 0),
                changed => RemediationStepResult.Ran(StepSetTabOrder, changed));
        }

        // ----------------------------------------------------------------
        //  Pass 6 — embed missing font programs
        // ----------------------------------------------------------------

        /// <summary>
        /// Embeds a font program into every font the document uses but does not carry — the step
        /// that closes <b>UA21</b> (deep checker) and <b>UA25</b> (conformance plugin).
        ///
        /// <para>
        /// PDF/UA-1 §7.21.4.1 requires every font to be embedded and grants the Standard 14 no
        /// exemption, so a document that draws with Helvetica, Times or Courier reported those two
        /// codes for ever and nothing in the pipeline could touch them: closing the gap needs a font
        /// program, which does not exist inside the document. That is why the step is off by default
        /// and why, when it is off or has no font source, it says so <i>and</i> names the
        /// consequence — exactly as the metadata step does for UA04.
        /// </para>
        ///
        /// <para>
        /// Off by default for two reasons that are worth stating: it cannot run without a font
        /// source the caller supplies, and unlike every other step it changes the document
        /// materially (a font program is typically far larger than everything else the pipeline
        /// writes) and redistributes a font — see
        /// <see cref="FontEmbeddingOptions.AllowSystemFontSubstitution"/> on the licensing.
        /// </para>
        /// </summary>
        private static byte[] ApplyFontEmbedding(
            EncryptedRevisionWriter enc,
            byte[] current,
            RemediationOptions options,
            DocumentSnapshot snapshot,
            List<RemediationStepResult> steps)
        {
            string consequence = snapshot.NonEmbeddedFontCount > 0
                ? PdfFontEmbeddingRemediator.BuildResidualWarning(snapshot.NonEmbeddedFontCount)
                : null;

            if (!options.EmbedMissingFonts)
            {
                steps.Add(SkipStep(StepEmbedFonts,
                    "not requested: RemediationOptions.EmbedMissingFonts is false", consequence));
                return current;
            }

            if (snapshot.NonEmbeddedFontCount == 0)
            {
                steps.Add(RemediationStepResult.Skip(StepEmbedFonts,
                    "already satisfied: every font the document uses already carries an embedded " +
                    "program (or is a Type 3 font, which has none to embed)"));
                return current;
            }

            FontEmbeddingOptions fontOptions = options.FontEmbedding;
            bool hasFontSource = fontOptions != null
                && (fontOptions.FontProvider != null || fontOptions.AllowSystemFontSubstitution);

            if (!hasFontSource)
            {
                steps.Add(SkipStep(StepEmbedFonts,
                    "not applicable: no font source is available — " +
                    (fontOptions == null
                        ? "RemediationOptions.FontEmbedding is null"
                        : "RemediationOptions.FontEmbedding.FontProvider is null and " +
                          "AllowSystemFontSubstitution is false") +
                    ", and a font program cannot be recovered from a document that does not contain one",
                    consequence));
                return current;
            }

            return RunStep(current, StepEmbedFonts, steps,
                enc == null
                    ? (i, o) => PdfFontEmbeddingRemediator.EmbedMissingFonts(i, o, fontOptions)
                    : ViaEncryptedWriter(enc,
                        (doc, ff) => PdfFontEmbeddingRemediator.Plan(doc, ff, fontOptions),
                        pl => new PlannedRevision(pl.Objects),
                        (pl, appended) => pl?.Describe(appended)),
                r => RemediationStepResult.Ran(StepEmbedFonts, r?.FontsEmbedded ?? 0, r?.Warnings));
        }

        // ----------------------------------------------------------------
        //  Pass 7 — PDF/UA identifier
        // ----------------------------------------------------------------

        private static byte[] ApplyPdfUaIdentifier(
            EncryptedRevisionWriter enc,
            byte[] current,
            RemediationOptions options,
            DocumentSnapshot snapshot,
            bool documentHasStructTree,
            bool structTreeReachesContent,
            List<RemediationStepResult> steps)
        {
            // A pdfuaid:part declaration is the document *asserting* PDF/UA conformance. Writing
            // it onto a file with no structure tree is the same trade the /MarkInfo /Marked step
            // already refuses to make: it replaces a true violation with a false claim. It is also
            // actively harmful rather than merely optimistic — every structure-dependent check
            // keys off that claim, so stamping it turns on (for example) UA23 annotation
            // reachability against a document that was never tagged, and remediation ends up
            // *introducing* findings. Refuse, and say why.
            if (options.SetPdfUaIdentifier && !documentHasStructTree)
            {
                steps.Add(SkipStep(StepSetPdfUaIdentifier,
                    "not applicable: the document has no structure tree, so no pdfuaid:part " +
                    "declaration was written — claiming PDF/UA conformance for an untagged " +
                    "document replaces a true UA03 with a false claim",
                    "the pdfuaid:part declaration was not written because the document has no " +
                    "structure tree (AutoTag could mark no content, or was not requested), so " +
                    "UA03 remains open. Tag the document — in an authoring tool or by enabling " +
                    "RemediationOptions.AutoTagIfUntagged on a document that has taggable text — " +
                    "and run this again."));
                return current;
            }

            // Same refusal, one step subtler: the tree exists but reaches nothing. "Has a
            // /StructTreeRoot" is not "is tagged" — a tree with no /ParentTree, or whose elements
            // hold no MCID, /MCR or /OBJR, is invisible to assistive technology, and this pipeline
            // has no step that can connect it (AutoTag deliberately does not replace an existing
            // tree here). Writing pdfuaid:part anyway bakes a self-asserted PDF/UA-1 conformance
            // claim into a document the very same call audits as non-conformant, and every
            // downstream consumer that triages on the XMP identifier then reads the broken file as
            // conformant.
            if (options.SetPdfUaIdentifier && !structTreeReachesContent)
            {
                steps.Add(SkipStep(StepSetPdfUaIdentifier,
                    "not applicable: the document's structure tree reaches no page content (no " +
                    "/ParentTree, or no element holding an MCID, /MCR or /OBJR), so no pdfuaid:part " +
                    "declaration was written — a hollow tree is untagged as far as assistive " +
                    "technology is concerned, and declaring PDF/UA conformance for it replaces a " +
                    "true UA03 with a false claim",
                    "the pdfuaid:part declaration was not written because the document's structure " +
                    "tree reaches no page content, so UA03 remains open alongside the UA14/UA16/UA30 " +
                    "findings against that tree. Run PdfAccessibility.AutoTag directly — it " +
                    "supersedes a hollow tree with a generated one that points at real marked " +
                    "content — or re-tag the document in an authoring tool, then run this again."));
                return current;
            }

            if (!options.SetPdfUaIdentifier)
            {
                steps.Add(SkipStep(StepSetPdfUaIdentifier,
                    "not requested: RemediationOptions.SetPdfUaIdentifier is false",
                    "the pdfuaid:part declaration was not written and no other step can write it, " +
                    "so UA03 remains open and the document cannot claim PDF/UA conformance; set " +
                    "RemediationOptions.SetPdfUaIdentifier to true once the structure has been " +
                    "reviewed."));
                return current;
            }

            if (options.PdfUaPart != 1 && options.PdfUaPart != 2)
            {
                steps.Add(RemediationStepResult.Fail(StepSetPdfUaIdentifier,
                    "RemediationOptions.PdfUaPart is " +
                    options.PdfUaPart.ToString(CultureInfo.InvariantCulture) +
                    "; PDF/UA defines part 1 (ISO 14289-1) and part 2 (ISO 14289-2) only."));
                return current;
            }

            // The writer is deterministic, so an already-remediated document would get a
            // byte-identical /Metadata stream appended for nothing.
            // A PDF/UA-2 declaration in a file below 2.0 also needs the catalog's /Version, so a packet
            // already right is not the whole answer there.
            if (snapshot.XmpPacketRead && !string.IsNullOrEmpty(snapshot.XmpPacket)
                && !(options.PdfUaPart == 2 && snapshot.NeedsPdf20Version))
            {
                try
                {
                    string desired = PdfUaXmpWriter.BuildPdfUaXmpPacket(
                        snapshot.XmpPacket, options.PdfUaPart,
                        PdfUaXmpWriter.TitleToWrite(options.Title, snapshot.XmpPacket, snapshot.InfoTitle),
                        options.Language, options.XmpModifyDate);

                    if (string.Equals(desired, snapshot.XmpPacket, StringComparison.Ordinal))
                    {
                        steps.Add(RemediationStepResult.Skip(StepSetPdfUaIdentifier,
                            "already satisfied: the document's XMP packet is byte-identical to the " +
                            "one this step would write"));
                        return current;
                    }
                }
                catch (Exception ex)
                {
                    // Falling through to the writer is always safe: it merges the same packet and
                    // reports its own failures.
                    AccessibilityDiagnostics.Report(
                        DiagnosticOperation + ".ApplyPdfUaIdentifier",
                        "The existing XMP packet could not be compared with the one to be written; " +
                        "the identifier will be written unconditionally.", ex);
                }
            }

            return RunStep(current, StepSetPdfUaIdentifier, steps,
                enc == null
                    ? (i, o) => PdfUaXmpWriter.SetPdfUaIdentifier(
                              i, o, options.PdfUaPart, options.Title, options.Language, options.XmpModifyDate)
                          ? IdentifierOutcome.Written : IdentifierOutcome.Failed
                    : ViaEncryptedWriter<IReadOnlyList<IndirectObject>, IdentifierOutcome>(enc,
                        (doc, ff) => PdfUaXmpWriter.Plan(
                            doc, ff, options.PdfUaPart, options.Title, options.Language,
                            options.XmpModifyDate),
                        objs => new PlannedRevision(objs),
                        // No Describe() here: Plan returns the objects themselves, so "did it work"
                        // is "something was staged and the revision landed" — and an empty plan with no
                        // error is Plan's real answer that the packet already carries the identifier,
                        // which the clear-text route's gate above sees from the snapshot it alone fills.
                        (objs, appended) => appended && objs != null && objs.Count > 0 ? IdentifierOutcome.Written
                            : objs != null && objs.Count == 0 && PdfUaXmpWriter.LastError == null ? IdentifierOutcome.AlreadySatisfied
                            : IdentifierOutcome.Failed),
                outcome => outcome switch
                {
                    IdentifierOutcome.Written => RemediationStepResult.Ran(StepSetPdfUaIdentifier, 1),
                    IdentifierOutcome.AlreadySatisfied => RemediationStepResult.Skip(StepSetPdfUaIdentifier,
                        "already satisfied: the document's XMP packet already carries the identifier this step would write"),
                    _ => RemediationStepResult.Fail(StepSetPdfUaIdentifier,
                        PdfUaXmpWriter.LastError ?? "the XMP metadata could not be written"),
                });
        }

        /// <summary>What the PDF/UA identifier step did, on either route.</summary>
        private enum IdentifierOutcome { Failed, Written, AlreadySatisfied }

        // ================================================================
        //  Document inspection
        // ================================================================

        /// <summary>
        /// Everything the pipeline needs to know before deciding whether a step can change
        /// anything. Gathering it costs one parse and saves an appended revision per step that
        /// would have been a no-op.
        /// </summary>
        private sealed class DocumentSnapshot
        {
            internal bool   Parsed;
            internal string Error;
            internal bool   IsEncrypted;
            internal bool   HasCatalog;
            internal bool   HasStructTree;

            /// <summary>
            /// Whether the structure tree is actually <i>connected to the document's content</i> —
            /// a resolvable <c>/ParentTree</c> plus at least one element whose <c>/K</c> holds an
            /// MCID, an <c>/MCR</c> or an <c>/OBJR</c>.
            ///
            /// <para>
            /// <b>Why <see cref="HasStructTree"/> is not enough.</b> A hollow tree — elements with
            /// no <c>/K</c>, no <c>/ParentTree</c>, no marked content — is untagged in every sense
            /// that matters to assistive technology, yet <c>/StructTreeRoot != null</c> is true for
            /// it. Two writes are conformance <i>claims</i> rather than repairs and must key off
            /// this flag, not off mere presence: <c>/MarkInfo /Marked true</c> (ISO 32000-1
            /// §14.7.1 — an assertion that the document follows the Tagged PDF conventions) and the
            /// <c>pdfuaid:part</c> XMP declaration. Stamping either onto a hollow tree bakes a
            /// self-asserted PDF/UA claim into a document the same call audits as non-conformant,
            /// and any consumer that triages on the XMP identifier then reads a broken file as
            /// conformant.
            /// </para>
            /// </summary>
            internal bool StructTreeReachesContent;

            /// <summary>
            /// Whether any structure element names page content at all — an MCID, an <c>/MCR</c> or
            /// an <c>/OBJR</c> — <b>without</b> requiring a <c>/ParentTree</c> above it.
            /// </summary>
            /// <remarks>
            /// <para><b>A weaker question than <see cref="StructTreeReachesContent"/>, asked for a
            /// different decision.</b> That flag gates a conformance <i>claim</i>, so it insists on
            /// navigability in both directions: without a <c>/ParentTree</c> a reader cannot get
            /// from a page back into the structure, and calling such a document tagged would be
            /// false. This flag gates whether the auto-tagger may <i>destroy</i> the tree, and there
            /// the question is not "is it good" but "did a human make it".</para>
            ///
            /// <para><b>An element naming an MCID is authoring work even with no
            /// <c>/ParentTree</c>.</b> The missing parent tree is a repairable omission; the
            /// element-to-content links are not recoverable once overwritten. Using the stricter
            /// flag for both decisions would have replaced a real, merely incomplete tree with a
            /// generated one — trading a fixable defect for an unfixable loss.</para>
            /// </remarks>
            internal bool StructTreeNamesContent;

            /// <summary>
            /// The document's <c>/Info /Title</c> when the metadata pass read the packet — after
            /// that pass, whose own title write is the caller's, which the identifier pass writes
            /// to <c>dc:title</c> as well.
            /// </summary>
            internal string InfoTitle;

            internal int  FigureCount;
            internal int  FiguresMissingAlt;

            /// <summary>
            /// Numbered headings (<c>H1</c>–<c>H6</c>) only — the elements
            /// <see cref="PdfStructureTreeEditor.FixHeadingHierarchy"/> renumbers. Unnumbered
            /// <c>&lt;H&gt;</c> elements are counted in <see cref="StrongHeadingCount"/> instead,
            /// because the fixer leaves them alone.
            /// </summary>
            internal int  HeadingCount;

            /// <summary>
            /// Unnumbered PDF/UA <c>&lt;H&gt;</c> headings, whose level comes from <c>/Sect</c>
            /// nesting. Counted so the skip reason can say "there are headings, but none this step
            /// renumbers" rather than the false "the structure tree contains no headings".
            /// </summary>
            internal int  StrongHeadingCount;

            internal bool HeadingHierarchyNeedsFixing;

            /// <summary>Pages whose /Annots array resolves and is non-empty — the UA09 scope.</summary>
            internal int AnnotatedPageCount;

            /// <summary>
            /// Of those, the ones that do not already declare <c>/Tabs /S</c>. Counted with the
            /// same test <see cref="PdfStructureTreeEditor.SetStructureTabOrder"/> writes on, so
            /// a zero here really does mean "that step would append an empty revision".
            /// </summary>
            internal int PagesMissingStructureTabOrder;

            /// <summary>
            /// Distinct fonts reachable from the page resources whose descriptor carries no
            /// <c>/FontFile</c>, <c>/FontFile2</c> or <c>/FontFile3</c> — the fonts UA21 and UA25
            /// are open against. Counted with the same walk
            /// <see cref="PdfFontEmbeddingRemediator.EmbedMissingFonts"/> performs, so a zero here
            /// really does mean "that step would append an empty revision".
            ///
            /// <para>
            /// <b>Zero is not the only "nothing to do"-looking answer.</b>
            /// <see cref="PdfFontEmbeddingRemediator.UnknownFontCount"/> (-1) means the walk did not
            /// complete — an encrypted document, a page tree that failed, resources that could not
            /// be read, or an exhausted budget — so the count is unknown rather than zero, and the
            /// two must not be conflated. The call sites keep them apart on purpose:
            /// <c>&gt; 0</c> gates the residual warning, so -1 invents no count; <c>== 0</c> gates
            /// the "already satisfied" skip, so -1 lets the step run and report for itself rather
            /// than claiming every font is embedded on the strength of a walk that never finished.
            /// </para>
            /// </summary>
            internal int NonEmbeddedFontCount;

            /// <summary>The catalog's XMP packet, or null when there is none / it is unreadable.</summary>
            internal string XmpPacket;

            /// <summary>Whether <see cref="XmpPacket"/> was actually looked up (null is a real answer).</summary>
            internal bool XmpPacketRead;

            /// <summary>Whether a PDF/UA-2 declaration needs the catalog's <c>/Version</c> written beside it (<c>PdfUaXmpWriter.VersionToDeclare</c>).</summary>
            internal bool NeedsPdf20Version;
        }

        private static DocumentSnapshot Inspect(byte[] pdfBytes)
        {
            var snapshot = new DocumentSnapshot();
            try
            {
                using var ms  = new MemoryStream(pdfBytes, writable: false);
                using var doc = PdfDocument.Open(ms);

                snapshot.Parsed = true;

                if (doc.IsEncrypted)
                {
                    snapshot.IsEncrypted = true;
                    return snapshot;
                }

                InspectOpenDocument(doc, snapshot);
            }
            catch (Exception ex)
            {
                snapshot.Parsed = false;
                snapshot.Error  = ex.Message;
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".Inspect", "The document could not be inspected.", ex);
            }
            return snapshot;
        }

        /// <summary>
        /// Fills a snapshot from an <b>open, readable</b> document — the half of
        /// <see cref="Inspect"/> that does not care how the bytes were obtained.
        /// </summary>
        /// <remarks>
        /// Extracted so the encrypted route can reach it. <c>PdfDocument.Open</c> loads no catalog
        /// for an encrypted file, so every gate in the pipeline — has a structure tree, how many
        /// figures lack alt text, do the headings need renumbering — would read as false/0 and the
        /// pipeline would take a different branch at every step. The snapshot must be built from the
        /// <i>decrypted</i> document or it is not a snapshot of anything.
        /// </remarks>
        private static void InspectOpenDocument(PdfDocument doc, DocumentSnapshot snapshot)
        {
            PdfDictionary catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                snapshot.Error = "the document has no catalog dictionary";
                return;
            }
            snapshot.NeedsPdf20Version = PdfUaXmpWriter.VersionToDeclare(doc, 2) != null;

            snapshot.HasCatalog = true;

            InspectPages(doc, snapshot);
            snapshot.NonEmbeddedFontCount = PdfFontEmbeddingRemediator.CountNonEmbeddedFonts(doc);

            var structTreeRoot = ResolveDictionary(doc, catalog[PdfName.StructTreeRoot]);
            snapshot.HasStructTree = structTreeRoot != null;

            if (structTreeRoot != null)
            {
                var index = PdfObjectIndex.Build(doc);
                IReadOnlyList<StructNode> nodes =
                    StructTreeWalker.Collect(structTreeRoot, doc, index);

                AnalyzeStructure(nodes, snapshot, RoleMapResolver.Build(doc), doc);

                // The nodes are already collected, so the reachability test reuses them rather
                // than walking the tree a second time.
                snapshot.StructTreeReachesContent =
                    StructTreeReachesContent(doc, structTreeRoot, index, nodes);
                snapshot.StructTreeNamesContent = AnyElementReferencesContent(nodes, doc);
            }
        }

        /// <summary>
        /// Inspects an <b>encrypted</b> document by borrowing the caller's writer: it is run with a
        /// plan that stages nothing, which leaves the bytes untouched and yields the decrypted
        /// document the inspection needs.
        /// </summary>
        /// <remarks>
        /// <para>This assembly cannot decrypt, and must not learn how — but it already holds a
        /// delegate that can, and <c>PdfEncryptedIncrementalUpdate.Run</c> documents that a plan
        /// returning no objects writes the original bytes unchanged. So the read-only probe costs
        /// one decrypt, is expressible entirely within the existing contract, and adds no
        /// dependency; no password ever enters this assembly.</para>
        /// <para>The scratch stream is discarded: the probe writes nowhere the caller can see.</para>
        /// </remarks>
        private static DocumentSnapshot InspectThroughWriter(
            byte[] encrypted, EncryptedRevisionWriter writer)
        {
            var snapshot = new DocumentSnapshot();
            try
            {
                using var input   = new MemoryStream(encrypted, writable: false);
                using var scratch = new MemoryStream(encrypted.Length + 16);

                writer(input, scratch, (doc, firstFree) =>
                {
                    snapshot.Parsed      = true;
                    snapshot.IsEncrypted = true;
                    InspectOpenDocument(doc, snapshot);
                    return PlannedRevision.Nothing;
                });

                // The writer never invoked the plan — a wrong password, an unsupported handler, or a
                // document that is not encrypted after all. Parsed=false is the honest report:
                // nothing was examined.
                if (!snapshot.Parsed)
                    snapshot.Error = "the encrypted-revision writer did not yield a decrypted document";
            }
            catch (Exception ex)
            {
                snapshot.Parsed = false;
                snapshot.Error  = ex.Message;
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".InspectThroughWriter",
                    "The encrypted document could not be inspected.", ex);
            }
            return snapshot;
        }

        /// <summary>
        /// Counts the pages that carry annotations and, among those, the ones that do not already
        /// declare <c>/Tabs /S</c>.
        ///
        /// <para>
        /// The two tests — a resolvable, non-empty <c>/Annots</c>, and a <c>/Tabs</c> whose value
        /// is the name <c>S</c> — are exactly the ones
        /// <see cref="PdfStructureTreeEditor.SetStructureTabOrder"/> applies, and the annotation
        /// test is exactly the one <c>PdfUaChecker</c>'s UA09 check applies. A legacy
        /// <c>/TabOrder /S</c> does not count as satisfied here, matching the writer: it is not a
        /// PDF key, so a page carrying only it still needs the real one.
        /// </para>
        ///
        /// <para>
        /// Failures are contained: this method never lets a page-tree problem turn the whole
        /// snapshot into "could not be parsed", which would skip every step in the pipeline. It
        /// reports and returns the counts it managed to gather, and an under-count only costs a
        /// skipped step that would have changed nothing.
        /// </para>
        /// </summary>
        private static void InspectPages(PdfDocument doc, DocumentSnapshot snapshot)
        {
            try
            {
                IReadOnlyList<PdfPage> pages = doc.Pages;
                if (pages == null) return;

                for (int i = 0; i < pages.Count; i++)
                {
                    PdfDictionary pageDict;
                    try
                    {
                        pageDict = pages[i].Dictionary;
                    }
                    catch (Exception ex)
                    {
                        AccessibilityDiagnostics.Report(
                            DiagnosticOperation + ".InspectPages",
                            "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                            " could not be read; it is not counted for the tab-order step", ex);
                        continue;
                    }

                    if (pageDict == null) continue;

                    var annots = ResolveArray(doc, pageDict[PdfName.Annots]);
                    if (annots == null || annots.Count == 0) continue;

                    snapshot.AnnotatedPageCount++;

                    // /Tabs /S = "structure order" (ISO 32000-1 Table 30). The /S here is a /Tabs
                    // *value*, not the structure-element /S key that PdfName.S denotes elsewhere.
                    var tabs = ResolveName(doc, pageDict[PdfName.Tabs]);
                    if (tabs == null || tabs.Value != StructureTabOrderValue)
                        snapshot.PagesMissingStructureTabOrder++;
                }
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".InspectPages",
                    "the page tree could not be walked; the tab-order step sees no annotated pages", ex);
            }
        }

        /// <summary>
        /// Counts the Figure elements that would be given alternate text and replays the heading
        /// renumbering to find out whether it would change anything.
        ///
        /// <para>
        /// The heading half is <b>not</b> replayed here: it is delegated to
        /// <see cref="HeadingHierarchyPlanner"/>, the same function
        /// <see cref="PdfStructureTreeEditor.FixHeadingHierarchy"/> writes from. A second,
        /// hand-synchronised copy of that traversal would drift: the pipeline reporting "already
        /// satisfied: the N heading(s) already form an unbroken level sequence" over documents the
        /// step it gates would renumber — and, in the mirror case, appending a revision that changes
        /// nothing. A gate and the step it gates computed from one
        /// function cannot disagree.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <c>doc</c> is a parameter because <c>StructNode.Dict</c> holds the element dictionary
        /// with RAW entries, so an <c>/Alt 12 0 R</c> read as absent and the figure was counted as
        /// missing its alt text. The sole call site has had the document in hand on the same line
        /// all along (<c>RoleMapResolver.Build(doc)</c>).
        /// </remarks>
        private static void AnalyzeStructure(IReadOnlyList<StructNode> nodes, DocumentSnapshot snapshot,
                                             Dictionary<string, string> roleMap = null,
                                             PdfDocument doc = null)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                // Through /RoleMap, exactly as PdfStructureTreeEditor.SetFigureAltText does. This
                // gate decides whether the figure step runs at all, so comparing raw here means a
                // role-mapped document is reported as having no figures and the step is skipped —
                // a gate and the step it gates computed two different ways cannot agree.
                if (!RoleMapResolver.Means(node.Role, roleMap, StructureTypes.Figure)) continue;

                snapshot.FigureCount++;
                // Matches PdfStructureTreeEditor's own test: an /Alt that is absent, not a
                // string, or an empty string all count as "missing".
                string alt = ((doc != null ? doc.ResolveObject(node.Dict?[PdfName.Alt])
                                           : node.Dict?[PdfName.Alt]) as PdfString)?.GetText();
                if (string.IsNullOrEmpty(alt)) snapshot.FiguresMissingAlt++;
            }

            // Through /RoleMap too, as FixHeadingHierarchy and PlanHeadingHierarchy plan: the gate and
            // the step it gates read one function with one map, or a role-mapped heading is "no heading"
            // here and renumbered there.
            HeadingHierarchyPlan plan = HeadingHierarchyPlanner.Plan(nodes, roleMap);

            snapshot.HeadingCount                = plan.NumberedHeadingCount;
            snapshot.StrongHeadingCount          = plan.StrongHeadingCount;
            snapshot.HeadingHierarchyNeedsFixing = plan.NeedsRenumbering;
        }

        /// <summary>
        /// Whether <paramref name="structTreeRoot"/> is a structure tree that is actually connected
        /// to the document's content: a resolvable <c>/ParentTree</c> <b>and</b> at least one
        /// element that names content.
        ///
        /// <para>
        /// This is the test the two conformance-claiming writes gate on — see
        /// <see cref="DocumentSnapshot.StructTreeReachesContent"/> for why presence of
        /// <c>/StructTreeRoot</c> is the wrong question. Both halves are required: a tree full of
        /// MCIDs with no <c>/ParentTree</c> cannot be navigated from a page back into the structure
        /// (UA14), and a <c>/ParentTree</c> above elements that name nothing describes nothing
        /// (UA16/UA30).
        /// </para>
        ///
        /// <para>
        /// Deliberately permissive about <i>how much</i> content is reached: one linked element is
        /// enough. The point is to separate "tagged, possibly imperfectly" from "not tagged at
        /// all"; grading quality is the audit's job, and a stricter rule here would refuse the
        /// identifier on real, merely-incomplete documents.
        /// </para>
        /// </summary>
        /// <param name="doc">The document the tree belongs to.</param>
        /// <param name="structTreeRoot">The <c>/StructTreeRoot</c> dictionary; null answers false.</param>
        /// <param name="index">Object index used by the walk; may be null.</param>
        /// <param name="collected">
        /// An already-collected walk of the same tree, to avoid walking it twice. Null collects.
        /// </param>
        /// <remarks>
        /// <c>internal</c> rather than private because <see cref="Artifacts.PdfArtifactMarker"/>
        /// gates the very same <c>/MarkInfo /Marked true</c> claim and must ask the identical
        /// question. Asking only whether a <c>/StructTreeRoot</c> resolves, the artifacts pass would
        /// stamp the claim onto a hollow tree this class refuses to stamp it onto — two writers, one
        /// flag, opposite answers.
        /// </remarks>
        internal static bool StructTreeReachesContent(
            PdfDocument doc, PdfDictionary structTreeRoot, PdfObjectIndex index,
            IReadOnlyList<StructNode> collected = null)
        {
            if (doc == null || structTreeRoot == null) return false;
            if (ResolveDictionary(doc, structTreeRoot[PdfName.ParentTree]) == null) return false;

            return AnyElementReferencesContent(
                collected ?? StructTreeWalker.Collect(structTreeRoot, doc, index), doc);
        }

        /// <summary><c>/Obj</c> — the required entry of an object reference (ISO 32000-1 Table 325).</summary>

        /// <summary>
        /// Whether any collected structure element has a <c>/K</c> child that names page content:
        /// a bare MCID integer, an <c>/MCR</c> marked-content reference, or an <c>/OBJR</c> object
        /// reference. <see cref="StructNode.RawKids"/> is unresolved by design, so a kid that is an
        /// indirect reference is resolved here — one level, and only for the dictionaries the walk
        /// did not already descend into.
        ///
        /// <para>
        /// <b><c>/Type</c> is not the test.</b> ISO 32000-1 Table 324 makes <c>/MCID</c> the only
        /// <i>required</i> entry of a marked-content reference, and Table 325 makes <c>/Obj</c> the
        /// only required entry of an object reference; <c>/Type</c> is Optional in both. Demanding
        /// it would tell a correctly tagged document written in the typeless form that its
        /// "structure tree reaches no page content", refusing the <c>pdfuaid:part</c> declaration
        /// (<see cref="ApplyPdfUaIdentifier"/>) and <c>/MarkInfo &lt;&lt;/Marked true&gt;&gt;</c>
        /// (<see cref="Artifacts.PdfArtifactMarker"/>) while the audit in the same run reports the
        /// tree as perfectly connected. Both checkers apply the rule used here —
        /// <c>PdfDeepAccessibilityChecker.ClassifyKid</c>/<c>IsObjectReference</c> and
        /// <c>PdfUaChecker.ClassifyStructKid</c> — so the three sites agree.
        /// <c>/S</c> is what disambiguates: it is Required on every structure element (Table 323),
        /// so a dictionary carrying it is an element and not a reference, whatever else it holds.
        /// </para>
        ///
        /// <para><c>/Type</c> is resolved rather than cast, because any value may be indirect
        /// (§7.3.10) and an <c>/MCR</c> whose <c>/Type</c> is stored indirectly would otherwise
        /// read as typeless.</para>
        /// </summary>
        internal static bool AnyElementReferencesContent(
            IReadOnlyList<StructNode> nodes, PdfDocument doc)
        {
            if (nodes == null) return false;

            for (int i = 0; i < nodes.Count; i++)
            {
                IReadOnlyList<PdfObject> kids = nodes[i].RawKids;
                if (kids == null) continue;

                for (int k = 0; k < kids.Count; k++)
                {
                    PdfObject kid = kids[k];
                    if (kid == null) continue;

                    // The commonest shape by far: /K 0 or /K [0 1 2] — bare MCIDs on the page the
                    // element's /Pg names.
                    if (kid is PdfNumber) return true;

                    var kidDict = kid as PdfDictionary;
                    if (kidDict == null && kid is PdfReference)
                    {
                        try
                        {
                            kidDict = doc?.ResolveObject(kid) as PdfDictionary;
                        }
                        catch (Exception ex)
                        {
                            AccessibilityDiagnostics.Report(
                                DiagnosticOperation + ".AnyElementReferencesContent",
                                "a /K child could not be resolved; it is not counted as content " +
                                "the structure tree reaches", ex);
                            continue;
                        }
                    }

                    if (kidDict == null) continue;

                    // One ladder, Base.Operations.StructKidClassifier's — shared with both checkers
                    // rather than agreeing with them by inspection. A copy returning early on ANY /Type
                    // it recognises as a name would count << /Type /StructElem /Obj 12 0 R >> as no
                    // content here while the plugin calls it an object reference.
                    StructKidKind kind = StructKidClassifier.Classify(kidDict, doc);
                    if (kind == StructKidKind.MarkedContentReference ||
                        kind == StructKidKind.ObjectReference) return true;
                }
            }

            return false;
        }

        // ================================================================
        //  Private helpers
        // ================================================================

        // One implementation, in Core.IO.StreamBytes. It rewinds a seekable stream before
        // reading; this copy read from wherever the position happened to be.
        private static byte[] ReadAll(Stream s) => StreamBytes.ReadAll(s);

        /// <summary>
        /// Audits an in-memory document. Unlike <see cref="Audit(Stream)"/> this never throws:
        /// the remediation pipeline has to write an output document even for input it cannot
        /// read, so a parse failure is turned into a UA00 error instead.
        /// </summary>
        private static AccessibilityAuditReport AuditBytes(byte[] pdfBytes)
            => AuditBytes(pdfBytes,
                          PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes,
                          PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes);

        /// <summary>
        /// Audits an in-memory document under explicit content ceilings, against
        /// <paramref name="target"/>. Never throws, for the same reason the parameterless form does
        /// not.
        /// </summary>
        private static AccessibilityAuditReport AuditBytes(
            byte[] pdfBytes, int maxPageContentBytes, long maxDocumentContentBytes,
            PdfUaVersion target = PdfUaVersion.Ua1)
        {
            var issues = new List<AccessibilityIssue>();

            // Auto is a request, not a part. Settle it here, from the document's own declaration,
            // so that everything below — and the report — deals in Ua1 or Ua2 only.
            PdfUaVersion applied = target == PdfUaVersion.Auto ? DetectDeclaredTarget(pdfBytes) : target;

            try
            {
                if (applied == PdfUaVersion.Ua2)
                    AuditUa2(pdfBytes, issues);
                else
                    AuditUa1(pdfBytes, maxPageContentBytes, maxDocumentContentBytes, issues);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".Audit", "The document could not be audited.", ex);

                issues.Add(new AccessibilityIssue(
                    IssueSeverity.Error, "UA00",
                    "The document could not be audited: " + ex.Message,
                    "Repair the file (see Tetitulz.Pdf.Plugins.Maintenance) before running an " +
                    "accessibility audit; no accessibility conclusion can be drawn from a document " +
                    "that cannot be parsed.",
                    IssueCategory.Structure));
            }

            // Total order: severity, then page (document-level first), then code, then object,
            // then message — two audits of the same bytes always produce the same sequence.
            issues.Sort((a, b) =>
            {
                int severity = a.Severity.CompareTo(b.Severity);
                if (severity != 0) return severity;

                int page = (a.PageNumber ?? 0).CompareTo(b.PageNumber ?? 0);
                if (page != 0) return page;

                int code = string.Compare(a.Code, b.Code, StringComparison.Ordinal);
                if (code != 0) return code;

                int obj = (a.ObjectNumber ?? 0).CompareTo(b.ObjectNumber ?? 0);
                return obj != 0 ? obj : string.Compare(a.Message, b.Message, StringComparison.Ordinal);
            });

            return new AccessibilityAuditReport(issues, applied);
        }

        /// <summary>
        /// The PDF/UA-1 audit: the Conformance plugin's dictionary-level checks and the deep
        /// checks, merged. Throws on a document it cannot read; the caller turns that into UA00.
        /// </summary>
        private static void AuditUa1(byte[] pdfBytes, int maxPageContentBytes,
            long maxDocumentContentBytes, List<AccessibilityIssue> issues)
        {
            // 1. Base PDF/UA-1 checks (UA00–UA09, UA25–UA29, UA99).
            //
            // These go into `issues` immediately and are deliberately NOT held back until the
            // deep half has run. PdfDeepAccessibilityChecker.Check opens its own PdfDocument
            // and can throw after this half has already succeeded; a buffer flushed only on the
            // success path would then take every plugin finding down with it, leaving a report
            // consisting of one UA00 — strictly less than the audit actually established, and
            // the opposite of what AuditBytes' catch exists to do.
            var baseResult = PdfUaChecker.Validate(new MemoryStream(pdfBytes));
            foreach (var violation in baseResult.Violations)
            {
                if (SupersededByDeepCheck(violation.Code)) continue;
                issues.Add(MapBaseViolation(violation));
            }

            // 2. Extended deep checks (UA00, UA06, UA10–UA24, UA30, UA99), collected into a
            //    local list so the UA06 collapse below can ask whether the deep half really
            //    produced one before dropping the plugin's copy.
            IReadOnlyList<AccessibilityIssue> deepIssues = PdfDeepAccessibilityChecker.Check(
                new MemoryStream(pdfBytes), maxPageContentBytes, maxDocumentContentBytes);

            // UA06 is the single code both halves emit, by design: they apply the same /P bit 10
            // rule in the same order (ISO 32000-1 Table 22) precisely so a combined report cannot
            // contradict itself about one document — but that made it the one code the merged
            // report double-counted, showing a remediation UI two entries for one permission bit.
            //
            // The drop is CONDITIONAL, and keyed on a deep UA06 actually existing rather than on
            // the document being encrypted or on a blanket SupersededByDeepCheck entry. The deep
            // half can end without reaching CheckEncryption's verdict, and an unconditional rule
            // would then delete the only UA06 in the report — losing the finding entirely, which
            // is worse than showing it twice. SupersededByDeepCheck stays an unconditional
            // predicate, as its doc comment promises; this case needs the deep result in hand and
            // so cannot live there.
            bool deepReportedEncryptionPermission = false;
            for (int i = 0; i < deepIssues.Count; i++)
            {
                if (!string.Equals(deepIssues[i].Code, EncryptionPermissionCode, StringComparison.Ordinal))
                    continue;
                deepReportedEncryptionPermission = true;
                break;
            }

            // `issues` holds nothing but the plugin half at this point — the deep findings are
            // appended below — so this removes the plugin's copy and never the deep one. Keeping
            // the deep entry is the deliberate direction: it carries the WCAG criterion and the
            // remediation naming the permission to grant, which the mapped plugin violation does
            // not.
            if (deepReportedEncryptionPermission)
                issues.RemoveAll(i => string.Equals(i.Code, EncryptionPermissionCode, StringComparison.Ordinal));

            issues.AddRange(deepIssues);
        }

        /// <summary>
        /// The PDF/UA-2 audit: Clause 5's version identification from the Conformance plugin
        /// (<c>UA2-5</c>, <c>UA2-99</c>) and the deep structure and content clauses
        /// (<c>UA2-8.x</c>, <c>UA2-6.2</c>). None of the PDF/UA-1 checkers run — a UA-2 document
        /// judged against UA-1 is told its <c>pdfuaid:part</c> must be 1, which is the wrong
        /// standard's finding, not a finding.
        /// </summary>
        /// <remarks>
        /// The plugin's findings go into <paramref name="issues"/> before the deep half runs, for
        /// the reason <see cref="AuditUa1"/> gives: the deep half can throw after this half has
        /// succeeded, and the caller's UA00 must not replace what was already established.
        /// </remarks>
        private static void AuditUa2(byte[] pdfBytes, List<AccessibilityIssue> issues)
        {
            var identifier = PdfUa2Checker.Validate(new MemoryStream(pdfBytes));
            foreach (var violation in identifier.Violations)
                issues.Add(MapUa2Violation(violation.Code, violation.Message));

            var structure = PdfUa2StructureChecker.Validate(new MemoryStream(pdfBytes));
            foreach (var violation in structure.Violations)
                issues.Add(MapUa2Violation(violation.Code, violation.Message));
        }

        /// <summary>
        /// The part the document declares in its XMP identification schema: <c>pdfuaid:part</c> of
        /// <c>2</c> selects <see cref="PdfUaVersion.Ua2"/>; anything else — <c>1</c>, no packet, a
        /// packet that cannot be read, a document that cannot be opened — selects
        /// <see cref="PdfUaVersion.Ua1"/>, which is also the default a caller naming no target gets.
        /// Never throws: a document that cannot be opened is reported by the audit it is then
        /// handed to, as UA00.
        /// </summary>
        private static PdfUaVersion DetectDeclaredTarget(byte[] pdfBytes)
        {
            try
            {
                using var doc = PdfDocument.Open(new MemoryStream(pdfBytes));

                // PdfDocument skips catalog loading for an encrypted document; the catalog is
                // almost always a top-level object, so resolve it directly rather than give up.
                PdfDictionary catalog = doc.Catalog?.Dictionary;
                if (catalog == null && doc.Trailer?[PdfName.Root] is PdfObject rootRef)
                    catalog = ResolveDictionary(doc, rootRef);
                if (catalog == null) return PdfUaVersion.Ua1;

                byte[] xmp = ReadXmpPacketBytes(doc, catalog);
                if (xmp == null) return PdfUaVersion.Ua1;

                // One reader for the identification schema, shared with the two checkers.
                PdfUaXmpIdentifier.Read(xmp, out string part, out _);
                return string.Equals(part, "2", StringComparison.Ordinal) ? PdfUaVersion.Ua2 : PdfUaVersion.Ua1;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".DetectDeclaredTarget",
                    "the document's pdfuaid:part declaration could not be read, so the audit runs " +
                    "against PDF/UA-1", ex);
                return PdfUaVersion.Ua1;
            }
        }

        /// <summary>
        /// The PDF/UA-2 "a check could not be completed" code, <c>PdfUa2Checker</c>'s counterpart
        /// of <see cref="IncompleteCheckCode"/>, and Info for the same reason: it says the report is
        /// incomplete, never that the document is wrong.
        /// </summary>
        private const string IncompleteUa2CheckCode = "UA2-99";

        /// <summary>
        /// Maps a PDF/UA-2 violation — from either UA-2 checker, both of which report a code and a
        /// message only — onto the issue model. The location is recovered from the message the
        /// way <see cref="MapBaseViolation"/> recovers it, the category follows the clause, and
        /// every code is an <see cref="IssueSeverity.Error"/> except <see cref="IncompleteUa2CheckCode"/>.
        /// </summary>
        private static AccessibilityIssue MapUa2Violation(string code, string message)
        {
            code    ??= string.Empty;
            message ??= string.Empty;

            bool incomplete = string.Equals(code, IncompleteUa2CheckCode, StringComparison.Ordinal);
            string clause = code.StartsWith("UA2-", StringComparison.Ordinal) ? code.Substring(4) : code;

            string remediation = incomplete
                ? "This is not a document defect: one of the PDF/UA-2 checks could not be " +
                  "completed, so whatever it would have found is missing. Treat the report as " +
                  "incomplete — repair the file (see Tetitulz.Pdf.Plugins.Maintenance), decrypt it " +
                  "if it is encrypted, and re-run PdfAccessibility.Audit() until the check runs cleanly."
                : "ISO 14289-2:2024 clause " + clause + ". Correct the document in an authoring " +
                  "tool and re-run PdfAccessibility.Audit() with AuditOptions.Target = " +
                  "PdfUaVersion.Ua2; this library's auto-tagging and remediation produce PDF/UA-1 " +
                  "output and do not close PDF/UA-2 findings.";

            return new AccessibilityIssue(
                incomplete ? IssueSeverity.Info : IssueSeverity.Error,
                code, message, remediation, Ua2Category(code),
                pageNumber: ExtractPageNumber(message),
                wcagLevel: WcagLevel.None,
                objectNumber: ExtractObjectNumber(message),
                structPath: null,
                matterhornCheckpoint: null,
                wcagCriterion: null,
                instanceCount: ExtractInstanceCount(message) ?? ExtractUa2RemainderCount(message));
        }

        /// <summary>
        /// The PDF/UA-2 structure checker's summary after the occurrences it lists, "…and 5 more
        /// …": that many instances, so <see cref="AccessibilityAuditReport.TotalInstanceCount"/>
        /// counts them rather than one. A check that stopped looking says "…and at least one more"
        /// and is counted as the one it knows of.
        /// </summary>
        private static readonly Regex Ua2RemainderPattern =
            new Regex(@"^…and (\d{1,7}) more\b", RegexOptions.CultureInvariant, MessagePatternTimeout);

        private static int? ExtractUa2RemainderCount(string message) => MessageNumber(Ua2RemainderPattern, message);

        /// <summary>
        /// The category a PDF/UA-2 clause belongs to, from its number: Clause 5 and §8.11/§8.14 are
        /// metadata, §8.4.4 is the language, §8.4.5 the fonts, §8.8 the destinations, §8.9 the
        /// annotations, §8.10 the forms, and everything else — §6.2, §8.2, §8.7 — the structure tree.
        /// </summary>
        private static IssueCategory Ua2Category(string code) => code switch
        {
            "UA2-5"                                                 => IssueCategory.Metadata,
            "UA2-8.4.4"                                             => IssueCategory.Language,
            _ when code.StartsWith("UA2-8.11", StringComparison.Ordinal)  => IssueCategory.Metadata,
            _ when code.StartsWith("UA2-8.14", StringComparison.Ordinal)  => IssueCategory.Metadata,
            _ when code.StartsWith("UA2-8.4.5", StringComparison.Ordinal) => IssueCategory.Fonts,
            _ when code.StartsWith("UA2-8.8", StringComparison.Ordinal)   => IssueCategory.Navigation,
            _ when code.StartsWith("UA2-8.9", StringComparison.Ordinal)   => IssueCategory.Annotations,
            _ when code.StartsWith("UA2-8.10", StringComparison.Ordinal)  => IssueCategory.Forms,
            _                                                       => IssueCategory.Structure,
        };

        /// <summary>
        /// The security-handler permission check (ISO 32000-1 Table 22, <c>/P</c> bit 10). Both
        /// halves of the audit own it — see the collapse in
        /// <see cref="AuditUa1"/> — which is why it is named once rather than
        /// spelled as a literal at both sites that compare against it.
        /// </summary>
        private const string EncryptionPermissionCode = "UA06";

        /// <summary>
        /// Whether a violation code from the Conformance plugin is fully covered by one of the
        /// deep checks, and so must not be added to a combined report.
        ///
        /// <para><b>Unconditional by design, and <c>UA06</c> is deliberately not in it.</b> This
        /// predicate sees one code and nothing else, so it can only express "the deep half always
        /// reports this better". UA06 is a conditional case — the plugin's copy may only be dropped
        /// when the deep half actually produced one — and it is handled where the deep result is in
        /// hand, in <see cref="AuditUa1"/>. Adding it here would drop the
        /// finding outright on any run where the deep half did not reach its encryption verdict.</para>
        ///
        /// <para>
        /// The plugin ships and is validated standalone, so it carries its own dictionary-level
        /// hollow-tree rules (UA28 = no /ParentTree, UA29 = elements referencing no content).
        /// The deep checker reports the same two defects as UA14 and UA16 with a page number, an
        /// object number and a structure path attached. Emitting both would double-count every
        /// occurrence and show a remediation UI two entries for one problem, one of which cannot
        /// be navigated to.
        /// </para>
        /// </summary>
        private static bool SupersededByDeepCheck(string code)
            => string.Equals(code, "UA28", StringComparison.Ordinal)    // → UA14 (ParentTree coverage)
            || string.Equals(code, "UA29", StringComparison.Ordinal)    // → UA16 (empty structure elements)
            // → UA16–UA18. UA50 is the plugin's standalone MCID-existence check, added so a
            // consumer of the Conformance package alone is not told a hollow-tagged file is
            // clean. It reads the page's own content stream only and skips /Stm references; the
            // deep checker follows form XObjects and reports the same defect with a page number
            // and a structure path, so in the merged report the deep finding is strictly better.
            || string.Equals(code, "UA50", StringComparison.Ordinal);

        /// <summary>
        /// Clones <paramref name="source"/>, or starts a new dictionary when it is <c>null</c>.
        ///
        /// <para><b>The null case is load-bearing.</b> The callers pass
        /// <c>ResolveDictionary(doc, …)</c> for <c>/MarkInfo</c> and <c>/ViewerPreferences</c>,
        /// which returns <c>null</c> when the document has neither — and building one from nothing
        /// is exactly what remediation is for. The shared
        /// <see cref="PdfDictionary.CloneShallow"/> throws on <c>null</c> deliberately, so the
        /// tolerance is stated here rather than inherited by accident.</para>
        /// </summary>
        private static PdfDictionary CloneDictionary(PdfDictionary source)
            => source == null ? new PdfDictionary() : PdfDictionary.CloneShallow(source);

        // One implementation, in Internal.Resolve, so a damaged file is reported and skipped rather
        // than thrown out of a public entry point.
        private static PdfDictionary ResolveDictionary(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Dict(doc, obj, "PdfAccessibility.ResolveDictionary");

        private static PdfArray ResolveArray(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Array(doc, obj, "PdfAccessibility.ResolveArray");

        private static PdfName ResolveName(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            return obj as PdfName ?? doc.ResolveObject(obj) as PdfName;
        }

        private static string GetTextValue(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            var str = obj as PdfString ?? doc.ResolveObject(obj) as PdfString;
            return str?.GetText();
        }

        /// <summary>
        /// A possibly-indirect boolean, resolved. The sibling of
        /// <see cref="ResolveDictionary"/> / <see cref="ResolveArray"/> / <see cref="ResolveName"/>,
        /// and it exists because the two callers below were the only reads in this file that were
        /// not resolving.
        /// </summary>
        /// <remarks>
        /// <c>PdfDictionary</c>'s indexer does not follow an indirect reference, so a producer
        /// writing <c>/Marked 9 0 R</c> — legal under §7.3.10 — read as absent, indistinguishable
        /// from a document that never set the flag. The consequence was not a missing finding but a
        /// pointless <em>write</em>: the metadata pass cloned <c>/MarkInfo</c>, set <c>/Marked
        /// true</c>, incremented its changed count and appended a revision that altered nothing,
        /// reported to the caller as "1 item changed" — and the same input run twice grew by a
        /// revision each time, contradicting <c>RemediateWithReport</c>'s own promise that a step
        /// changing nothing costs no revision at all.
        /// </remarks>
        private static bool ResolveBooleanTrue(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return false;
            var boolean = obj as PdfBoolean ?? doc.ResolveObject(obj) as PdfBoolean;
            return boolean != null && boolean.Value;
        }

        private static bool IsMarkedTrue(PdfDocument doc, PdfDictionary catalog)
        {
            var markInfo = ResolveDictionary(doc, catalog[PdfName.MarkInfo]);
            return ResolveBooleanTrue(doc, markInfo?[PdfName.Marked]);
        }

        /// <summary>
        /// <c>/ViewerPreferences /DisplayDocTitle</c>, through
        /// <see cref="PdfViewerPreferences"/> — the one reader, which takes a resolver rather than
        /// a <see cref="PdfDocument"/> so an encrypted document (which loads no catalog object) can
        /// still be read. This was open-coded here, in the UA-2 structure checker and in the
        /// Conformance plugin's UA04.
        /// </summary>
        private static bool IsDisplayDocTitleTrue(PdfDocument doc, PdfDictionary catalog)
            => PdfViewerPreferences.Read(catalog, doc.ResolveObject).DisplayDocTitle == true;

        internal static string GetDocumentTitle(PdfDocument doc)
        {
            var infoObj = doc.Trailer?[PdfName.Info];
            var info    = infoObj != null ? ResolveDictionary(doc, infoObj) : null;
            return info == null ? null : GetTextValue(doc, info[PdfName.Title]);
        }

        /// <summary>
        /// The catalog's <c>/Lang</c>, or null. Needed by the encrypted metadata pass, which has to
        /// decide "is this already set?" from inside the plan callback.
        /// </summary>
        private static string ReadCatalogLanguage(PdfDocument doc)
        {
            PdfDictionary catalog = doc?.Catalog?.Dictionary;
            return catalog == null ? null : GetTextValue(doc, catalog[PdfName.Lang]);
        }

        /// <summary>
        /// Reads the catalog's XMP packet as text, or returns null when there is none or it
        /// cannot be decoded. A decode failure is reported, never swallowed: the only cost of a
        /// null here is one redundant appended revision in the PDF/UA-identifier pass.
        /// </summary>
        private static string ReadXmpPacket(PdfDocument doc, PdfDictionary catalog)
        {
            try
            {
                byte[] bytes = ReadXmpPacketBytes(doc, catalog);
                if (bytes == null) return null;

                // One decoder, in Core.Metadata.XmpPacket. This copy was correct; two others —
                // PdfXValidator and PdfUaChecker — assumed UTF-8, and Encoding.UTF8.GetString does
                // not throw on UTF-16, it substitutes, so a conforming file with a UTF-16 packet was
                // reported as non-conforming.
                return Tetitulz.Pdf.Core.Metadata.XmpPacket.DecodeText(bytes);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    DiagnosticOperation + ".ReadXmpPacket",
                    "The catalog /Metadata stream could not be read; the PDF/UA identifier will be " +
                    "written unconditionally.", ex);
                return null;
            }
        }

        /// <summary>
        /// The catalog's XMP packet, decoded from its stream filters but not yet as text, or null
        /// when the catalog has none or it is empty. Throws when the stream cannot be read, or
        /// decodes past the XMP packet ceiling (<c>XmpPacketDom.MaxExistingPacketBytes</c>); the two
        /// callers report that in their own terms.
        /// </summary>
        private static byte[] ReadXmpPacketBytes(PdfDocument doc, PdfDictionary catalog)
        {
            var metadataObj = catalog[PdfName.Metadata];
            if (metadataObj == null) return null;

            var metadata = metadataObj as PdfStream ?? doc.ResolveObject(metadataObj) as PdfStream;
            if (metadata == null) return null;

            if (!Tetitulz.Pdf.Core.Metadata.XmpPacketDom.TryReadPacketBytes(metadata, out byte[] bytes, out string reason))
                throw new Tetitulz.Pdf.Core.PdfException("the catalog /Metadata stream could not be read: " + reason.TrimEnd('.') + ".");
            return bytes == null || bytes.Length == 0 ? null : bytes;
        }

        // ================================================================
        //  Base-violation mapping
        // ================================================================

        /// <summary>Aggregated counts open the message: "27 Figure element(s) ... are missing".</summary>
        private static readonly Regex LeadingCountPattern =
            new Regex(@"^(\d{1,7})\s", RegexOptions.CultureInvariant, MessagePatternTimeout);

        /// <summary>"... on page 3 ..." — deliberately not matching "3 page(s) with annotations".</summary>
        private static readonly Regex PageNumberPattern =
            new Regex(@"\bpage\s+(\d{1,7})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MessagePatternTimeout);

        /// <summary>"... object 12 ..." / "... obj 12 ...".</summary>
        private static readonly Regex ObjectNumberPattern =
            new Regex(@"\bobj(?:ect)?\s+(\d{1,9})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MessagePatternTimeout);

        /// <summary>
        /// The longest prefix of a checker's message the number patterns read. A message embeds text the
        /// file chose (names, a list of twenty and a count), so it is a field the file controls; the
        /// patterns are linear, and this cap and <see cref="MessagePatternTimeout"/> are what bound them
        /// (07's DoS hunt, F4). A number past the cap is not read.
        /// </summary>
        internal const int MaxMessageCharsMatched = 16 * 1024;

        // A property, not a field: the patterns above it are initialised first, in textual order.
        private static TimeSpan MessagePatternTimeout => TimeSpan.FromSeconds(1);

        /// <summary>
        /// The positive number a pattern's first group captures in a message's first
        /// <see cref="MaxMessageCharsMatched"/> characters, or <c>null</c> — none, not positive, or the match
        /// timed out.
        /// </summary>
        internal static int? MessageNumber(Regex pattern, string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            Match match;
            try { match = pattern.Match(message, 0, Math.Min(message.Length, MaxMessageCharsMatched)); }
            catch (RegexMatchTimeoutException) { return null; }
            return match.Success && int.TryParse(
                       match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                       && number >= 1
                ? number
                : (int?)null;
        }

        /// <summary>
        /// Maps a base <see cref="PdfUaChecker"/> violation onto the richer issue model: category,
        /// concrete remediation advice naming the API that fixes it, WCAG level and criterion, and
        /// the Matterhorn checkpoint where one is unambiguous.
        ///
        /// <para>The base checker reports a code and a message only, so the location is recovered
        /// from the message text: a leading count becomes
        /// <see cref="AccessibilityIssue.InstanceCount"/>, "page N" becomes
        /// <see cref="AccessibilityIssue.PageNumber"/>, and "object N" / "obj N" becomes
        /// <see cref="AccessibilityIssue.ObjectNumber"/>. Messages that carry no location stay
        /// document-level, which is the honest answer.</para>
        ///
        /// <para>An unrecognised code still gets a real category and real remediation text rather
        /// than an empty string — a new check must never render as a blank instruction in a UI.</para>
        /// </summary>
        private static AccessibilityIssue MapBaseViolation(PdfUaViolation violation)
        {
            string code    = violation.Code ?? string.Empty;
            string message = violation.Message ?? string.Empty;

            var (category, remediation, wcagLevel, matterhorn, criterion) = code switch
            {
                "UA00" => (IssueCategory.Structure,
                           "The document has no readable catalog dictionary. Repair the file " +
                           "(Tetitulz.Pdf.Plugins.Maintenance) before attempting accessibility remediation.",
                           WcagLevel.None, (string)null, (string)null),

                "UA01" => (IssueCategory.Tagging,
                           "Add /MarkInfo << /Marked true >> to the document catalog. " +
                           "PdfAccessibility.AutoTag() writes it together with a real structure tree — " +
                           "setting the flag on its own only hides the problem.",
                           WcagLevel.A, (string)null, "1.3.1"),

                "UA02" => (IssueCategory.Language,
                           "Add /Lang \"en-US\" (or the appropriate BCP-47 tag) to the catalog. " +
                           "Use PdfAccessibility.SetDocumentLanguage(), or RemediationOptions.Language.",
                           WcagLevel.A, (string)null, "3.1.1"),

                "UA03" => (IssueCategory.Metadata,
                           "Add an XMP metadata stream declaring pdfuaid:part=\"1\". Use " +
                           "PdfAccessibility.SetPdfUaIdentifier() (or PdfUaXmpWriter.SetPdfUaIdentifier), " +
                           "which merges into any existing packet. This is the only fix for UA03 — " +
                           "no structure-tree change can close it.",
                           WcagLevel.None, "06-002", (string)null),

                "UA04" => (IssueCategory.Metadata,
                           "Write the title into the XMP metadata stream's dc:title (an /Info /Title alone does " +
                           "not satisfy ISO 14289-1 7.1) and set /ViewerPreferences /DisplayDocTitle true. " +
                           "PdfAccessibility.SetDocumentTitle() and RemediationOptions.Title write both; with " +
                           "no title given, Remediate carries the document's own /Info /Title to dc:title.",
                           WcagLevel.A, "06-003", "2.4.2"),

                "UA05" => (IssueCategory.Structure,
                           "Add a /StructTreeRoot to the catalog. Use PdfAccessibility.AutoTag() to " +
                           "generate a structure tree from the page content, then review it — " +
                           "a generated tree is a starting point, not a finished one.",
                           WcagLevel.A, (string)null, "1.3.1"),

                "UA06" => (IssueCategory.Tagging,
                           "The security handler denies content extraction for accessibility. Re-encrypt " +
                           "the document with the accessibility-extraction permission granted (PDF 2.0 " +
                           "removed the separate bit: bit 5, /Copy, now governs it), or supply an " +
                           "unencrypted copy. Every remediation step in this library writes clear-text " +
                           "incremental updates and cannot run on an encrypted document.",
                           WcagLevel.None, (string)null, (string)null),

                "UA07" => (IssueCategory.Images,
                           "Add /Alt to every Figure structure element. Use " +
                           "PdfAccessibility.SetFigureAltText() with a callback that writes a real " +
                           "description — generated placeholder text satisfies the checker but not a reader.",
                           WcagLevel.A, "13-004", "1.1.1"),

                "UA08" => (IssueCategory.Forms,
                           "Add /TU (tooltip / accessible name) to every interactive form field. " +
                           "Use PdfAccessibility.SetFieldTooltip() per field.",
                           WcagLevel.A, (string)null, "4.1.2"),

                "UA09" => (IssueCategory.Navigation,
                           "Set /Tabs /S on every page that has annotations, so tab order follows the " +
                           "structure tree. (/Tabs is the page-dictionary key; /TabOrder is not a PDF key.)",
                           WcagLevel.A, (string)null, "2.4.3"),

                "UA25" => (IssueCategory.Fonts,
                           "Embed every font used by the document, including the standard 14. A " +
                           "non-embedded font is substituted by the reader, which changes glyph " +
                           "identities and breaks text extraction for assistive technology.",
                           WcagLevel.None, "31-001", (string)null),

                "UA26" => (IssueCategory.Fonts,
                           "Give every glyph a Unicode value: add a /ToUnicode CMap to the font, or use " +
                           "an encoding whose glyph names map to Unicode. Without it, extracted text is " +
                           "meaningless to a screen reader even though the page looks correct.",
                           WcagLevel.A, (string)null, "1.3.1"),

                "UA27" => (IssueCategory.Annotations,
                           "Give every annotation an accessible description: /Contents (or /Alt on its " +
                           "structure element) for non-Widget annotations, and /TU on form field widgets. " +
                           "Link annotations additionally need an /Alt on the enclosing Link structure element.",
                           WcagLevel.A, (string)null, "1.1.1"),

                "UA58" => (IssueCategory.Structure,
                           "Give every optional content configuration — /OCProperties /D and each /Configs " +
                           "entry — a non-empty /Name, and remove /AS from each (ISO 14289-1 7.10).",
                           WcagLevel.None, (string)null, (string)null),

                "UA59" => (IssueCategory.Structure,
                           "Give each embedded file's specification both /F and /UF — the file's name, /UF as " +
                           "a Unicode text string — and preferably a /Desc saying what the file is (ISO 14289-1 7.11).",
                           WcagLevel.None, (string)null, (string)null),

                "UA60" => (IssueCategory.Forms,
                           "Dynamic XFA forms are not permitted (ISO 14289-1 7.15). Re-author the form as a " +
                           "static XFA form or as AcroForm fields, and re-run the audit.",
                           WcagLevel.None, (string)null, (string)null),

                "UA61" => (IssueCategory.Annotations,
                           "Give each media clip data dictionary a /CT (the media's MIME type) and an /Alt " +
                           "multi-language text array describing the media (ISO 14289-1 7.18.6.2).",
                           WcagLevel.A, (string)null, "1.1.1"),

                // Not an ISO 14289-1 requirement: UA99 is the plugin's own "a check could not be
                // completed" code. The default arm below sent it out as a hard violation whose
                // remediation told the reader to look up a clause that does not exist, and the
                // Error severity flipped AccessibilityAuditReport.IsConforming — so a report that
                // was merely *incomplete* condemned the document. PdfDeepAccessibilityChecker emits
                // the same code as Info for the same reason, and one combined report cannot apply
                // two rules to the same construct.
                IncompleteCheckCode => (IssueCategory.Structure,
                           "This is not a document defect: one of the conformance checks could not " +
                           "be completed, so whatever it would have found is missing. Treat the " +
                           "report as incomplete — repair the file (see " +
                           "Tetitulz.Pdf.Plugins.Maintenance), decrypt it if it is encrypted, and " +
                           "re-run PdfAccessibility.Audit() until the check runs cleanly.",
                           WcagLevel.None, (string)null, (string)null),

                _      => (IssueCategory.Structure,
                           "This PDF/UA-1 requirement has no automated fix in this library. Consult " +
                           "ISO 14289-1 for check " + (code.Length > 0 ? code : "(unnamed)") +
                           ", correct the document in an authoring tool, and re-run PdfAccessibility.Audit().",
                           WcagLevel.None, (string)null, (string)null),
            };

            // Severity is a function of the code, not a constant. Everything the plugin reports is
            // a violation of a requirement — an Error — except UA99, which reports that a check did
            // not run. That is a coverage notice: the document has not been shown to be wrong, so
            // it must not flip AccessibilityAuditReport.IsConforming on its own, exactly as
            // PdfDeepAccessibilityChecker's identical notices do not.
            IssueSeverity severity = string.Equals(code, IncompleteCheckCode, StringComparison.Ordinal)
                ? IssueSeverity.Info
                : IssueSeverity.Error;

            var issue = new AccessibilityIssue(
                severity, code, message, remediation, category,
                pageNumber: ExtractPageNumber(message),
                wcagLevel: wcagLevel,
                objectNumber: ExtractObjectNumber(message),
                structPath: null,
                matterhornCheckpoint: matterhorn,
                wcagCriterion: criterion,
                instanceCount: ExtractInstanceCount(message));

            return issue;
        }

        private static int? ExtractInstanceCount(string message) => MessageNumber(LeadingCountPattern, message);

        internal static int? ExtractPageNumber(string message) => MessageNumber(PageNumberPattern, message);

        private static int? ExtractObjectNumber(string message) => MessageNumber(ObjectNumberPattern, message);
    }
}


