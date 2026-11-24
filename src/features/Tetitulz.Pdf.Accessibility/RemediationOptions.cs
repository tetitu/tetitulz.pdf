// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Models;

namespace Tetitulz.Pdf.Accessibility
{
    /// <summary>
    /// Options for <see cref="PdfAccessibility.Remediate"/> and
    /// <see cref="PdfAccessibility.RemediateWithReport"/>.
    /// <para>Every property has a safe default: <c>new RemediationOptions()</c> sets the language
    /// to "en-US", auto-tags untagged documents, describes undescribed figures, repairs the
    /// heading hierarchy, marks running headers/footers as artifacts, fixes the reading order,
    /// sets <c>/Tabs /S</c> on annotated pages and declares the PDF/UA-1 identifier.</para>
    /// </summary>
    public sealed class RemediationOptions
    {
        /// <summary>Language tag to set (e.g. "en-US"). Null or empty = do not change.</summary>
        public string Language { get; set; } = "en-US";

        /// <summary>
        /// Document title to set in /Info /Title and in XMP dc:title. Null or empty = do not
        /// change. A title is what makes /ViewerPreferences /DisplayDocTitle meaningful, so the
        /// pipeline sets that flag only once the document has one.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Auto-tag the document when nothing in its structure tree reaches any content. Default
        /// true.
        /// </summary>
        /// <remarks>
        /// <para><b>"Untagged" is about reaching content, not about a <c>/StructTreeRoot</c>
        /// existing.</b> A tree whose elements have no <c>/K</c>, no <c>/ParentTree</c> entry and no
        /// marked content is the input class where "the document already has a structure tree" reads
        /// as "nothing to do" while the audit in the same run reports UA16/UA17/UA18/UA30 against
        /// it. This pipeline runs <c>PdfAccessibility.AutoTag</c> on that document itself rather
        /// than telling the caller to.</para>
        ///
        /// <para><b>A tree whose elements name any content is never replaced</b>, and the test is
        /// deliberately permissive: one element naming one MCID is enough to make the tree the
        /// author's rather than this library's. It is also deliberately weaker than the test that
        /// gates the conformance claims — a missing <c>/ParentTree</c> does not license destroying
        /// the element-to-content links, because the first is repairable and the second is not.
        /// Replacing a partially built tree would discard work a human did, which is a different
        /// and much worse trade than replacing one that does nothing.</para>
        ///
        /// <para>When it does replace one, it says so — <see cref="AutoTagResult.Warnings"/> carries
        /// the supersession, and the remediation report carries the step.</para>
        /// </remarks>
        public bool AutoTagIfUntagged { get; set; } = true;

        /// <summary>Options for the auto-tagger.</summary>
        public AutoTagOptions AutoTagOptions { get; set; } = new AutoTagOptions();

        /// <summary>Set /Alt on figures that are missing it. Default true.</summary>
        public bool SetFigureAltText { get; set; } = true;

        /// <summary>
        /// Factory for figure alt text. Receives a <see cref="FigureContext"/> and returns
        /// the alt text to set, or null to skip.
        /// <para>When a factory is supplied the pipeline runs the alt-text step for <i>every</i>
        /// Figure, including ones that already have text, because only the factory can decide
        /// whether an existing description should be replaced.</para>
        ///
        /// <para><b>Null (the default) writes "Figure 1", "Figure 2", … and warns.</b> That
        /// placeholder closes UA07 structurally while describing nothing to a screen-reader user,
        /// so the step attaches a warning naming exactly that to
        /// <see cref="RemediationReport.AllWarnings"/>. Treat a report carrying it as
        /// <i>not finished</i>: supply real descriptions here and have a human review them.</para>
        /// </summary>
        public Func<FigureContext, string> AltTextFactory { get; set; }

        /// <summary>
        /// Supplies each link's alternate description — its <c>/Contents</c>, which closes
        /// <b>UA22</b>. Receives a <see cref="LinkContext"/> for every link annotation a reader is
        /// presented, in page order, and returns the description or <c>null</c> to leave the link.
        /// <para><b>Null (the default) skips the step, and nothing is invented.</b> A link's
        /// description says where it goes in words a listener understands, which neither its URI
        /// nor the text under it is; a link left without one stays a UA22 finding in the
        /// after-audit.</para>
        /// </summary>
        public Func<LinkContext, string> LinkDescriptionFactory { get; set; }

        /// <summary>Fix heading hierarchy (renumber H1–H6). Default true.</summary>
        public bool FixHeadingHierarchy { get; set; } = true;

        /// <summary>Mark page headers/footers as artifacts. Default true.</summary>
        public bool MarkArtifacts { get; set; } = true;

        /// <summary>Header margin (points from top). Default 50.</summary>
        public float HeaderMarginPt { get; set; } = 50f;

        /// <summary>Footer margin (points from bottom). Default 50.</summary>
        public float FooterMarginPt { get; set; } = 50f;

        /// <summary>Fix reading order in structure tree. Default true.</summary>
        public bool FixReadingOrder { get; set; } = true;

        /// <summary>
        /// Write <c>/Tabs /S</c> ("tab order follows the structure tree", ISO 32000-1 Table 30) on
        /// every page that carries annotations and does not already declare it. Default true.
        ///
        /// <para>This is the step that closes <b>UA09</b>; with it off, an annotated document
        /// keeps failing that check however much else the pipeline repairs. Pages without
        /// annotations are never touched — PDF/UA-1 (ISO 14289-1, 7.18.3) requires the key only
        /// where there is a tab order to define — so the step costs no appended revision on a
        /// document that has no annotations or is already tabbed.</para>
        /// </summary>
        public bool FixTabOrder { get; set; } = true;

        /// <summary>
        /// Write the PDF/UA identification schema (<c>pdfuaid:part</c>) into the document's XMP
        /// metadata as the pipeline's last pass. Default true.
        /// <para>This is the only step that can close <b>UA03</b>; with it disabled the pipeline
        /// can never produce a PDF/UA-conformant file, however much structure it repairs. Turn it
        /// off only when the document is not meant to claim PDF/UA conformance — for instance when
        /// the structure tree is machine-generated and has not been reviewed, since declaring
        /// conformance you have not verified is a stronger misstatement than omitting the
        /// identifier.</para>
        ///
        /// <para><b>The pipeline refuses it in two cases regardless of this flag</b>, and says so
        /// in the step's skip reason and in <see cref="RemediationReport.AllWarnings"/>: when the
        /// document has no structure tree, and when it has one that reaches no page content (no
        /// <c>/ParentTree</c>, or no element holding an MCID, <c>/MCR</c> or <c>/OBJR</c>). A
        /// hollow tree is untagged as far as assistive technology is concerned, and stamping
        /// <c>pdfuaid:part</c> onto it would bake a self-asserted conformance claim into a file the
        /// same call audits as non-conformant. <c>/MarkInfo /Marked</c> is gated identically.</para>
        /// </summary>
        public bool SetPdfUaIdentifier { get; set; } = true;

        /// <summary>
        /// Embed a font program into every font the document uses but does not carry.
        /// <b>Default false.</b>
        ///
        /// <para>This is the only step that can close <b>UA21</b> / <b>UA25</b>: PDF/UA-1 §7.21.4.1
        /// requires every font to be embedded and grants the Standard 14 no exemption, so a document
        /// drawing with Helvetica, Times or Courier reports those codes until a font program is put
        /// into it — and a font program cannot be recovered from a document that does not contain
        /// one. Supply one through <see cref="FontEmbedding"/>.</para>
        ///
        /// <para>Off by default, unlike every other step, because it cannot run without a font
        /// source the caller supplies, it changes the document materially (a font program usually
        /// dwarfs everything else the pipeline writes) and it <i>redistributes a font</i> — see the
        /// licensing remark on <see cref="FontEmbeddingOptions"/>. When it is off, or on with no
        /// font source, the step is skipped with a stated reason and a warning naming the
        /// violations left open.</para>
        /// </summary>
        public bool EmbedMissingFonts { get; set; }

        /// <summary>
        /// Where the font programs come from, and whether to subset them. Null (the default) means
        /// there is no font source, so <see cref="EmbedMissingFonts"/> has nothing to work with and
        /// the step skips with that reason.
        ///
        /// <para><b>Licensing:</b> embedding a font redistributes it. The caller is responsible for
        /// holding redistribution rights to whatever it supplies through
        /// <see cref="FontEmbeddingOptions.FontProvider"/> or authorises through
        /// <see cref="FontEmbeddingOptions.AllowSystemFontSubstitution"/>.</para>
        /// </summary>
        public FontEmbeddingOptions FontEmbedding { get; set; }

        /// <summary>
        /// PDF/UA part to declare: 1 for PDF/UA-1 (ISO 14289-1), 2 for PDF/UA-2 (ISO 14289-2).
        /// Default 1. Any other value fails the identifier step rather than writing a meaningless
        /// declaration.
        /// </summary>
        /// <remarks>
        /// This is what the document <i>claims</i>. What the pipeline's before/after audits
        /// <i>check</i> is <see cref="Target"/>, which follows this by default so the two cannot
        /// silently disagree.
        /// </remarks>
        public int PdfUaPart { get; set; } = 1;

        /// <summary>
        /// The part of ISO 14289 the pipeline's before/after audits judge the document against —
        /// the same knob <see cref="AuditOptions.Target"/> is. <c>null</c> (the default) follows
        /// <see cref="PdfUaPart"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Without this the pipeline could stamp a claim it had not checked.</b> The audits
        /// ran PDF/UA-1 unconditionally while <see cref="PdfUaPart"/> could be set to 2, so a caller
        /// asking for a PDF/UA-2 declaration got one — over a document judged only against PDF/UA-1,
        /// and a report whose "after" audit said it conformed. Declaring conformance that was never
        /// verified is the misstatement <see cref="SetPdfUaIdentifier"/> already refuses to make for
        /// a hollow structure tree; this is the same rule for the part number.</para>
        ///
        /// <para>Defaulting to <see cref="PdfUaPart"/> rather than to
        /// <see cref="PdfUaVersion.Ua1"/> keeps the two aligned for a caller who sets only one, and
        /// makes setting them to different values a deliberate act — which is legitimate: auditing
        /// against PDF/UA-2 while declaring nothing, or declaring 1 while checking the stricter
        /// part, are both reasonable. It is only the unnoticed mismatch that was wrong.</para>
        ///
        /// <para><see cref="PdfUaVersion.Auto"/> is resolved per audit from the document's own
        /// declaration, exactly as it is for <see cref="PdfAccessibility.Audit(System.IO.Stream, AuditOptions)"/>
        /// — which for the <i>after</i> audit means the identifier this same run just wrote.</para>
        /// </remarks>
        public PdfUaVersion? Target { get; set; }

        /// <summary>
        /// The part the audits actually run against: <see cref="Target"/> when set, otherwise the
        /// one <see cref="PdfUaPart"/> declares.
        /// </summary>
        internal PdfUaVersion EffectiveTarget =>
            Target ?? (PdfUaPart == 2 ? PdfUaVersion.Ua2 : PdfUaVersion.Ua1);

        /// <summary>
        /// Timestamp recorded as <c>xmp:ModifyDate</c> / <c>xmp:MetadataDate</c>. Null (the
        /// default) writes no timestamp, which keeps remediation output byte-for-byte reproducible
        /// — pass <c>DateTimeOffset.UtcNow</c> when an audit trail matters more than determinism.
        /// </summary>
        public DateTimeOffset? XmpModifyDate { get; set; }

        /// <summary>
        /// Limit the pipeline to a single full text-extraction pass. Default true.
        ///
        /// <para>Three steps need the page text laid out — <see cref="AutoTagIfUntagged"/>,
        /// <see cref="MarkArtifacts"/> and <see cref="FixReadingOrder"/> — and extraction is by far
        /// the most expensive thing the pipeline does. With this set, they are tried in that order
        /// and only the first one that is both enabled and applicable runs; the others are skipped
        /// with a reason recorded in <see cref="RemediationReport.SkippedSteps"/>.</para>
        ///
        /// <para>On an untagged document that costs nothing: AutoTag already emits Artifact
        /// elements for running headers/footers and builds the tree in visual reading order. On an
        /// already-tagged document it means artifact marking wins over reading-order repair. Set it
        /// to false to run every enabled step, at the price of one extra text extraction and one
        /// extra appended revision each.</para>
        /// </summary>
        public bool SingleTextExtraction { get; set; } = true;

        /// <summary>
        /// Bridge to the Encryption assembly, enabling remediation of an <b>encrypted</b> document
        /// in place. Null (the default) leaves the pipeline clear-text only, and an encrypted
        /// document is skipped with a reason exactly as before.
        /// </summary>
        /// <remarks>
        /// <para>Wiring is one lambda; the password never enters this assembly:</para>
        /// <code>
        /// options.EncryptedRevisionWriter = (input, output, plan) =&gt;
        ///     PdfEncryptedIncrementalUpdate.Run(input, output, password, plan);
        /// </code>
        /// <para><b>Each step becomes its own decrypt/append cycle.</b> The pipeline's steps read
        /// each other's output — reading-order repair must see the tree the tagger built — so the
        /// alternative, planning all ten against one decrypted copy, would need plan
        /// <i>composition</i>: steps 2..10 would have to see changes that exist only as staged
        /// objects, and several revise the same catalog. That is a research problem rather than an
        /// optimisation, and the cost bought is N decrypt/encrypt cycles on an in-memory copy.</para>
        /// <para><b>A step with no encrypted route is skipped, never forced.</b> Running a
        /// clear-text writer against an encrypted document would append a revision the encryption
        /// dictionary does not describe, producing a file no reader can open — so the pipeline
        /// fails closed and records why, rather than risking that.</para>
        /// <para>Setting this on a document that is <i>not</i> encrypted changes nothing: the
        /// clear-text path is used, because it preserves more (see
        /// <c>PdfEncryptedIncrementalUpdate</c> on what an appended encrypted revision costs).</para>
        /// </remarks>
        public EncryptedRevisionWriter EncryptedRevisionWriter { get; set; }

        /// <summary>
        /// Per-page decoded-content ceiling, in bytes, applied to the <b>before</b> and
        /// <b>after</b> audits <see cref="PdfAccessibility.RemediateWithReport"/> runs. Default
        /// <see cref="PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes"/> (8 MiB).
        /// <c>0</c> or less disables it — see that field for why an unbounded audit is only for a
        /// caller that has already bounded its own input.
        ///
        /// <para>This is the same ceiling <see cref="PdfAccessibility.Audit(System.IO.Stream,int,long)"/>
        /// takes as a parameter, and it exists here for the same reason: without it the pipeline's
        /// two audits ran at the default however the caller had configured the rest of the run, so a
        /// host that legitimately processes 20 MiB pages got a report in which those pages were
        /// <i>refused</i> — named in a UA99 Info issue, never silently, but never examined either —
        /// while the identical document audited through <c>Audit(stream, 32 MiB, …)</c> was checked
        /// in full. The pipeline could not be configured to match its own audit.</para>
        ///
        /// <para><b>Not the same ceiling as <see cref="Models.AutoTagOptions.MaxPageContentBytes"/>,
        /// and the two must not be collapsed.</b> That one bounds a pass that decodes, splices and
        /// <i>rewrites</i> a page's content stream, so its cost is a large multiple of the stream
        /// and its refusal means "this page was not tagged". This one bounds a read-only scan, and
        /// its refusal means "this page's content-level checks did not run". A document can
        /// legitimately want one raised and the other left alone.</para>
        /// </summary>
        public int MaxPageContentBytes { get; set; }
            = PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes;

        /// <summary>
        /// Document-wide decoded-content budget, in bytes, applied to the <b>before</b> and
        /// <b>after</b> audits <see cref="PdfAccessibility.RemediateWithReport"/> runs. Default
        /// <see cref="PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes"/> (128 MiB).
        /// <c>0</c> or less disables it.
        ///
        /// <para><see cref="MaxPageContentBytes"/> alone bounds one page and therefore nothing at
        /// all — a thousand pages just under it is the same cost with an extra loop — so raising
        /// that one for a large document usually means raising this one too. The budget is spent in
        /// page order; once it is gone the remaining pages keep every dictionary-level check and
        /// lose only the content-level ones, each named per page in the report.</para>
        /// </summary>
        public long MaxDocumentContentBytes { get; set; }
            = PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes;
    }
}
