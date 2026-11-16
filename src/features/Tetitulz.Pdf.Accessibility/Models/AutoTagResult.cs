// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Accessibility.Models
{
    /// <summary>Options for the PDF auto-tagger.</summary>
    public sealed class AutoTagOptions
    {
        /// <summary>Default document language tag to inject (e.g. "en-US").</summary>
        public string Language { get; set; } = "en-US";

        /// <summary>Document title to set in /Info. If null, leaves existing title untouched.</summary>
        public string Title { get; set; }

        /// <summary>
        /// The part of ISO 14289 the emitted structure is authored for. Default
        /// <see cref="PdfUaVersion.Ua1"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>It changes one thing today: the PDF 2.0 standard structure namespace.</b> Under
        /// <see cref="PdfUaVersion.Ua2"/> the tagger declares
        /// <c>http://iso.org/pdf2/ssn</c> in the structure tree root's <c>/Namespaces</c> and points
        /// every element's <c>/NS</c> at it. ISO 32000-2 §14.8.6 defaults an absent <c>/NS</c> to the
        /// PDF 1.7 namespace rather than leaving it unspecified, so a tree of correctly named PDF 2.0
        /// elements without one declares itself to be PDF 1.7 structure — which ISO 14289-2 §8.2.5.2
        /// rejects.</para>
        ///
        /// <para><b>It does not make the output PDF/UA-2 conformant</b>, and nothing here claims it
        /// does. The roles PDF/UA-2 adds — <c>Aside</c>, <c>Title</c>, <c>FENote</c> — are semantic
        /// judgements about content, and this tagger infers structure from geometry and type size.
        /// Emitting them on a guess would produce confidently mislabelled structure, which is worse
        /// for a screen-reader user than an honest <c>P</c>.</para>
        ///
        /// <para><see cref="PdfUaVersion.Auto"/> is treated as <see cref="PdfUaVersion.Ua1"/> here:
        /// authoring has no document to detect a declaration from, unlike auditing.</para>
        /// </remarks>
        public PdfUaVersion Target { get; set; } = PdfUaVersion.Ua1;

        /// <summary>
        /// These options with <see cref="Target"/> set to <paramref name="target"/>: a copy, so the
        /// remediation pipeline tags for the part it declares without changing the caller's object.
        /// </summary>
        internal AutoTagOptions ForTarget(PdfUaVersion target)
        {
            if (Target == target) return this;
            var copy = (AutoTagOptions)MemberwiseClone();
            copy.Target = target;
            return copy;
        }

        /// <summary>
        /// Ratio of a line's height to the median body-text height that classifies it as H1.
        /// Default 1.8 — lines 1.8× the median height become Heading 1.
        /// </summary>
        public float H1Ratio { get; set; } = 1.8f;

        /// <summary>Ratio threshold for H2. Default 1.4.</summary>
        public float H2Ratio { get; set; } = 1.4f;

        /// <summary>Ratio threshold for H3. Default 1.15.</summary>
        public float H3Ratio { get; set; } = 1.15f;

        /// <summary>
        /// Level headings by the rank of their size rather than by the three fixed ratios. Every
        /// distinct size at or above <see cref="H3Ratio"/> × the median body size — rounded to half a
        /// point — is ranked largest first: the largest is H1, the next H2, and so down to H6, which
        /// every smaller heading size shares. A document with five heading sizes gets H1–H5, where the
        /// ratios put everything between 1.15 and 1.4 × the median at H3; and no level is skipped, since
        /// the ranks are consecutive. Off by default, so existing output keeps its levels.
        /// </summary>
        public bool HeadingLevelsBySize { get; set; }

        /// <summary>
        /// Maximum gap (in PDF user units) between consecutive lines that still belong
        /// to the same paragraph. Lines further apart start a new paragraph. Default 6.
        /// </summary>
        public float ParagraphGapThreshold { get; set; } = 6f;

        /// <summary>
        /// If true, lines in the top <see cref="HeaderFooterMargin"/> points of a page
        /// are tagged as Artifacts rather than body content.
        /// </summary>
        public bool TagHeadersFootersAsArtifacts { get; set; } = true;

        /// <summary>
        /// Margin (in points from top or bottom of page) used to detect running
        /// headers and footers. Default 50 pt.
        /// </summary>
        public float HeaderFooterMargin { get; set; } = 50f;

        /// <summary>
        /// Rewrite each page's content stream so every logical run is wrapped in
        /// <c>BDC</c>/<c>EMC</c> marked content with a real MCID. <b>Default true — this is what
        /// tagging a PDF means.</b>
        ///
        /// <para>
        /// A structure tree whose elements carry no MCIDs is invisible to assistive technology:
        /// screen readers reach content through marked-content sequences, not through element
        /// attributes, and independent checkers fail such a file outright. Setting this to false produces
        /// "structure-only" output — object graph without content marking — which is
        /// occasionally useful for inspection but is <em>not</em> a tagged PDF.
        /// </para>
        /// </summary>
        public bool RewriteContentStreams { get; set; } = true;

        /// <summary>
        /// Also write <c>/ActualText</c> onto every generated structure element. <b>Default false.</b>
        ///
        /// <para>
        /// Now that elements reference real marked content, <c>/ActualText</c> is redundant, and it
        /// is not harmless: it <em>overrides</em> the text a reader would otherwise extract from
        /// the content stream. The text this tagger can recover from raw string operands is only
        /// correct for simple Latin-encoded fonts (it has no access to the font's encoding or
        /// <c>ToUnicode</c> CMap), so blanket <c>/ActualText</c> risks replacing correct text with
        /// mojibake. Enable it only when the source is known to be simply encoded.
        /// </para>
        /// </summary>
        public bool EmitActualText { get; set; }

        /// <summary>
        /// Flate-compress the rewritten content streams. Default true. Set false to leave them
        /// readable when inspecting the tagger's output.
        /// </summary>
        public bool CompressRewrittenStreams { get; set; } = true;

        /// <summary>
        /// Give each page that draws a shared Form XObject its own copy of it, so the text inside
        /// can be marked. <b>Default true.</b>
        ///
        /// <para>
        /// A Form XObject has one stream dictionary and therefore one <c>/StructParents</c>, so its
        /// marked content can belong to exactly one page's parent-tree entry. Without duplication a
        /// form drawn by several pages cannot be tagged at all, and every page drawing it is
        /// reported through <see cref="AutoTagResult.PagesPartiallyTagged"/> — which for a
        /// letterhead, a stamp or a set of flattened fields means the whole document. Those are
        /// precisely the things a document shares, so this is the common case rather than an
        /// exotic one.
        /// </para>
        ///
        /// <para>
        /// <b>What it costs.</b> The document's object graph changes: <em>n</em> pages drawing one
        /// form end up with <em>n</em> copies of it (the first page keeps the original), each page
        /// after the first gains its own <c>/Resources</c>, and each copy gets its own set of
        /// structure elements. The output is correspondingly larger.
        /// <see cref="AutoTagResult.SharedFormsDuplicated"/> reports how many forms this happened
        /// to. Set false to leave the object graph exactly as it was found and accept the partial
        /// tagging, reported the same way.
        /// </para>
        /// </summary>
        public bool DuplicateSharedFormXObjects { get; set; } = true;

        /// <summary>
        /// Infer lists: a run of consecutive lines that each begin with a marker and share a left
        /// edge is tagged <c>/L</c> → <c>/LI</c> → <c>/LBody</c> instead of a run of
        /// <c>/P</c>. Off by default.
        /// </summary>
        /// <remarks>
        /// <para><b>Off by default because a wrongly-inferred list is worse than none.</b> It puts
        /// an <c>/L</c> into the tree that a screen reader announces as a list of the wrong length,
        /// and this library's own deep checker then reports the shape — remediation
        /// manufacturing the violation it exists to remove. Nothing about an untagged document says
        /// which of its indented runs are lists, so this is a judgement a caller makes about their
        /// own corpus.</para>
        ///
        /// <para><b>What it does not claim.</b> No <c>/Lbl</c>: separating the marker from the body
        /// means splitting one line's marked content inside a show operator, and §7.6 is
        /// satisfied by an <c>/LI</c> carrying a body alone, so the marker stays in the body text.
        /// No nesting: a deeper run has a different left edge, so it becomes a second list beside
        /// the first rather than one inside it — a shallow list is not a wrong list. And a run
        /// is broken at a page boundary, because a heuristic reading each page alone cannot tell a
        /// continuation from a new list.</para>
        /// </remarks>
        public bool InferLists { get; set; }

        /// <summary>
        /// Infer tables: a run of consecutive rows whose text starts at the same set of X positions
        /// is tagged <c>/Table</c> → <c>/TR</c> → <c>/TH</c>|<c>/TD</c> instead of a run of
        /// <c>/P</c>. Off by default.
        /// </summary>
        /// <remarks>
        /// <para><b>Off by default, for a sharper version of the reason <see cref="InferLists"/>
        /// is.</b> A table has a required internal shape — a <c>/TR</c> may contain only <c>/TD</c>
        /// and <c>/TH</c>, and every row must span the same number of columns — and this library's
        /// own deep checker reports a table that fails it as an <b>Error</b>, not a warning. So an
        /// inferred table that is wrong is a finding this pass manufactured.</para>
        ///
        /// <para><b>What it detects.</b> Two or more consecutive rows, each of two or more cells,
        /// whose cells' left edges line up within a quarter of the type size. A row is assembled
        /// from every text-showing operator on one baseline, so it is found whether the producer
        /// drew the row as one <c>BT…ET</c> with a <c>Td</c> per cell or as one <c>BT…ET</c> per
        /// cell.</para>
        ///
        /// <para><b>What it refuses.</b> A cell drawn with two show operators — its second one
        /// lands at a different X on every row, so the column set stops matching and the run
        /// breaks. A row with a missing cell, because padding it would be inventing content and not
        /// padding it makes the table ragged, which is the Error above. And any run shorter than
        /// two rows.</para>
        ///
        /// <para><b>What it claims that it cannot measure.</b> The first row becomes <c>/TH</c>
        /// with <c>/Scope /Column</c>. Nothing recorded here distinguishes a header row from a data
        /// row — weight is not recorded, and a rule drawn above the row is not text — but a table
        /// with no <c>/TH</c> at all is itself a UA12 finding, and a first row that heads its
        /// columns is far commoner than one that does not. A corpus where it is not has no override
        /// today: the run classifier is <c>internal</c>, and so are the <c>RunCandidate</c> and
        /// <c>RunClassification</c> types it would have to speak in. Turn <see cref="InferTables"/>
        /// off and tag those documents by hand.</para>
        ///
        /// <para><b>A two-column table of contents is a known false positive</b> — a caption and a
        /// page number repeated down the page is a grid by every measure available here, and
        /// becomes a table rather than a <c>/TOC</c>. The structure is valid and navigable; the
        /// semantics are poorer.</para>
        /// </remarks>
        public bool InferTables { get; set; }

        /// <summary>
        /// Asked for the alternate text of each image on the page. Returning null or empty means
        /// <b>do not tag that image</b>. Null (the default) means figures are not inferred at all.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a callback rather than a flag because nothing here can describe a
        /// picture.</b> ISO 14289-1 §7.3 requires a <c>/Figure</c> to carry an <c>/Alt</c>, and
        /// this library's own UA07 check reports one that does not — so a tagger that emitted the
        /// element and left the text to a later pass would manufacture, on every image, exactly the
        /// finding it exists to remove. The caller has the picture, or a caption, or a filename, or
        /// a vision model, or nothing; only the caller knows which.</para>
        ///
        /// <para><b>Declining is a first-class answer.</b> An image the callback returns null for
        /// is left exactly as it was — untagged content, which is the state every image on an
        /// untagged page is already in. That is a real loss for a screen reader and it is
        /// <em>visible</em> to the deep checker as unreached content, where a described-as-nothing
        /// <c>/Figure</c> would have looked tagged and been useless.</para>
        ///
        /// <para><b>Asked once per occurrence, not once per resource.</b> The same logo drawn in a
        /// header and again in a footer arrives twice, because those two occurrences may want
        /// different text — or one of them may want to be declined. Deduplicating here would take
        /// that choice away; a caller that wants one answer per resource caches on
        /// <see cref="FigureCandidate.XObjectName"/>.</para>
        ///
        /// <para><b>An image inside a <c>BT…ET</c>, or already inside somebody's marked content, is
        /// never offered.</b> The first cannot be wrapped without risking improper nesting against
        /// the text runs being spliced into the same bytes; the second is already tagged.</para>
        /// </remarks>
        public Func<FigureCandidate, string> FigureAlternateText { get; set; }

        /// <summary>
        /// Extend an existing structure tree instead of superseding it. Off by default.
        /// </summary>
        /// <remarks>
        /// <para><b>The default supersedes, and that has to stay the default.</b> This pass writes
        /// a tree it can vouch for end to end; merging produces one whose two halves were built by
        /// different things, and only the caller knows whether the half already there is worth
        /// keeping. So the choice is theirs, and the failure mode of choosing wrongly is
        /// recoverable either way — supersede loses the old tree, merge keeps a bad one.</para>
        ///
        /// <para><b>What merging actually does.</b> The existing <c>/StructTreeRoot</c> is kept,
        /// along with its <c>/RoleMap</c>, <c>/ClassMap</c> and every element it holds; the new
        /// <c>/Document</c> element is appended to its <c>/K</c>; and the new parent-tree entries
        /// are added to the existing ones rather than replacing them. Pages the existing tree
        /// already reaches are <b>left entirely alone</b> — not re-marked, not adopted — and
        /// named in <see cref="AutoTagResult.SkippedPageReasons"/>.</para>
        ///
        /// <para><b>It refuses rather than truncates.</b> A parent tree this pass cannot read to
        /// the end (a budget, or the depth cap) would produce a merged tree missing the entries it
        /// could not see, which is worse than not merging: the links were there and would not be
        /// afterwards. That case is reported and the document is left untouched.</para>
        /// </remarks>
        public bool MergeIntoExistingTree { get; set; }

        /// <summary>
        /// Ceiling, in <b>bytes</b>, on the <em>decoded</em> content stream of a single page that
        /// the tagging pass will analyse. <b>Default 4194304 (4 MiB).</b>
        ///
        /// <para>
        /// <b>What happens on exceed.</b> The page is <em>skipped whole</em>: its content stream is
        /// left byte-for-byte as it was found, no marked content is spliced into it, no MCID is
        /// minted for it, and one entry naming the page, its actual size and this limit is added to
        /// <see cref="AutoTagResult.SkippedPageReasons"/>. A page is never partially tagged, and a
        /// document whose only page was refused reports
        /// <see cref="AutoTagResult.ContentMarked"/> <c>false</c> rather than claiming to be tagged.
        /// </para>
        ///
        /// <para>
        /// <b>Why the ceiling exists.</b> Tagging builds three parallel representations of the same
        /// bytes — the byte scanner's token list, the production parser's operation list used for
        /// the cross-check, and the re-scan of the spliced copy — so peak cost is a large multiple
        /// of the stream (measured at roughly 120× in peak working set and ~460× in total
        /// allocation) and it is linear. A page's <c>/Contents</c> is attacker-chosen and inflates
        /// from a few kilobytes of Flate to as much as 512 MiB, so without a ceiling a small hostile
        /// PDF drives one page to tens of gigabytes. 4 MiB is far above any real text page — a dense
        /// multi-column A4 page decodes to well under a megabyte — so the default costs no realistic
        /// document its tags.
        /// </para>
        ///
        /// <para>
        /// <b>Adjusting it.</b> Raise it for a host that legitimately processes very large pages and
        /// can afford the multiple; lower it to be stricter on untrusted upload. <c>0</c> or less
        /// disables the ceiling entirely, which is only for a caller that has already bounded its
        /// own input.
        /// </para>
        /// </summary>
        public int MaxPageContentBytes { get; set; }
            = Internal.ContentStreamTagger.DefaultMaxContentBytes;

        /// <summary>
        /// Ceiling, in <b>bytes</b>, on the total <em>decoded</em> content assembled for one page
        /// before any of it is analysed. <b>Default 536870912 (512 MiB), matching the most a single
        /// stream is allowed to inflate to.</b>
        ///
        /// <para>
        /// This is the earlier of the two ceilings and bounds a different thing from
        /// <see cref="MaxPageContentBytes"/>: that one bounds the <em>analysis</em> of a page after
        /// its bytes exist, this one bounds <em>materialising the bytes at all</em>. A page's
        /// <c>/Contents</c> may be an array of arbitrarily many streams, each of which is separately
        /// allowed to inflate to 512 MiB, so the concatenation is otherwise unbounded from a tiny
        /// file — forty parts ask for a 20 GiB array.
        /// </para>
        ///
        /// <para>
        /// <b>What happens on exceed.</b> The page's content is treated as unreadable: nothing
        /// partial is ever returned, the page is skipped with a reason naming this limit, and its
        /// bytes are left untouched — the same all-or-nothing outcome as
        /// <see cref="MaxPageContentBytes"/>.
        /// </para>
        ///
        /// <para>
        /// <b>Set it below <see cref="MaxPageContentBytes"/> to cap what is decoded, not just what
        /// is analysed.</b> Whenever a stream declares its decoded size (<c>/DL</c>) or carries no
        /// filter, a page over this limit is refused <em>before</em> it is decompressed. A filtered
        /// stream that declares nothing cannot be predicted — that is what a compression filter is —
        /// and is still inflated once, up to 512 MiB, before being refused. <c>0</c> or less means
        /// "use the 512 MiB default"; there is deliberately no way to ask for an unbounded
        /// concatenation.
        /// </para>
        /// </summary>
        public long MaxConcatenatedContentBytes { get; set; }
            = Internal.ContentStreamTagger.MaxConcatenatedContentBytes;
    }

    /// <summary>Result of an auto-tagging operation.</summary>
    public sealed class AutoTagResult
    {
        private static readonly IReadOnlyList<string> NoStrings = new string[0];
        private static readonly IReadOnlyDictionary<int, int> NoHeadings = new Dictionary<int, int>();

        /// <summary>
        /// Number of structure elements written into the structure tree — the <c>/Document</c>
        /// root plus one element per logical block (paragraph or heading). Zero when no page
        /// could be tagged, in which case no structure tree is written at all.
        /// </summary>
        public int ElementsAdded { get; }

        /// <summary>Number of pages examined.</summary>
        public int PagesProcessed { get; }

        /// <summary>Headings detected, keyed by level (1–6).</summary>
        public IReadOnlyDictionary<int, int> HeadingsByLevel { get; }

        /// <summary>Number of paragraphs detected.</summary>
        public int ParagraphCount { get; }

        /// <summary>Number of artifact regions detected (headers/footers).</summary>
        public int ArtifactCount { get; }

        /// <summary>Warnings generated during tagging.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// Total number of MCIDs assigned across all pages — i.e. how many marked-content
        /// sequences the structure tree actually points at. <b>Zero means the document was not
        /// really tagged</b>, whatever <see cref="ElementsAdded"/> says.
        /// </summary>
        public int McidCount { get; }

        /// <summary>Number of pages left untouched. See <see cref="SkippedPageReasons"/>.</summary>
        public int PagesSkipped { get; }

        /// <summary>
        /// One entry per skipped page, naming the page and why it was skipped (already-marked
        /// content, an undecodable stream, an unsafe scan, no text). Never null.
        /// </summary>
        public IReadOnlyList<string> SkippedPageReasons { get; }

        /// <summary>
        /// True when at least one page's content stream was rewritten with real marked content.
        /// This — not the presence of a structure tree — is what makes the document readable by
        /// assistive technology.
        /// </summary>
        public bool ContentMarked { get; }

        /// <summary>Pages whose content stream was rewritten.</summary>
        public int PagesTagged => Math.Max(0, PagesProcessed - PagesSkipped);

        /// <summary>
        /// Number of annotations that were made reachable from the structure tree: each got a
        /// wrapper element (<c>/Link</c>, <c>/Form</c> or <c>/Annot</c>) whose <c>/K</c> is an
        /// <c>/OBJR</c> naming it, a <c>/StructParent</c> of its own, and a matching
        /// <c>/ParentTree</c> entry (ISO 32000-1 §14.7.4.3 / §14.8.4.4). Annotations PDF/UA exempts
        /// — <c>/Popup</c>, <c>/PrinterMark</c>, and anything flagged Hidden or NoView — are not
        /// counted because they are not meant to be in the tree.
        ///
        /// <para>
        /// <b>Exposed as a settable property rather than a constructor parameter on purpose.</b>
        /// Widening either constructor — even with an optional parameter — would be a binary break
        /// for an assembly already compiled against this type (see the constructor remarks below),
        /// so the value is assigned by the producing pass, which lives in this assembly.
        /// </para>
        /// </summary>
        public int AnnotationsTagged { get; internal set; }

        /// <summary>
        /// Lists this pass inferred, or 0 when <c>AutoTagOptions.InferLists</c> was not set.
        /// </summary>
        /// <remarks>
        /// <b>The number a caller checks before trusting the option on their corpus.</b> A run that
        /// is inferred is one a screen reader will announce as a list, so seeing how many a
        /// document produced — against how many it has — is the only way to tell the
        /// setting is doing what it should. A getter with an internal setter rather than a widened
        /// constructor, which C# would bake into every existing call site as a binary break.
        /// </remarks>
        public int ListsInferred { get; internal set; }

        /// <summary>
        /// Tables this pass inferred, or 0 when <c>AutoTagOptions.InferTables</c> was not set.
        /// </summary>
        /// <remarks>
        /// <b>The number a caller checks before trusting the option on their corpus</b>, exactly as
        /// <see cref="ListsInferred"/> is: a table that is inferred is one a screen reader will
        /// navigate cell by cell, so comparing how many a document produced against how many it has
        /// is the only way to tell the setting apart from a setting that is guessing.
        /// </remarks>
        public int TablesInferred { get; internal set; }

        /// <summary>
        /// <c>/Figure</c> elements this pass wrote, or 0 when
        /// <c>AutoTagOptions.FigureAlternateText</c> was not supplied.
        /// </summary>
        /// <remarks>
        /// <b>Elements written, not decisions taken — and the difference is real.</b> A Form
        /// XObject drawn on several pages is tagged once and its bytes shared by every copy, so
        /// one call to the callback produces one <c>/Figure</c> per copy. This number therefore
        /// counts what is in the document; <see cref="FiguresDeclined"/> counts what the caller was
        /// asked about and refused. <b>They do not sum to "images seen"</b>, and neither is
        /// derivable from the other.
        /// </remarks>
        public int FiguresTagged { get; internal set; }

        /// <summary>
        /// Images offered to <c>AutoTagOptions.FigureAlternateText</c> that it returned no text
        /// for, and which were therefore left untagged.
        /// </summary>
        /// <remarks>
        /// <para><b>A number worth reading, because it is silent damage otherwise.</b> Every count
        /// here is a picture a screen reader will not reach — the honest outcome when nobody can
        /// describe it, and still an outcome a caller should see rather than discover from an audit
        /// later. A warning is raised as well, so it also reaches
        /// <c>PdfAccessibility.RemediateWithReport</c>, which surfaces a step's warnings rather
        /// than its result.</para>
        ///
        /// <para><b>Decisions refused, not elements missing</b> — see <see cref="FiguresTagged"/>
        /// for why the two count different populations.</para>
        /// </remarks>
        public int FiguresDeclined { get; internal set; }

        /// <summary>
        /// The <c>/XObject</c> dictionaries whose images the figure seam read — each once a run,
        /// however many pages and forms share it: the evidence the catalogue is live.
        /// </summary>
        internal int ImageCatalogsRead { get; set; }

        /// <summary>
        /// Whether this pass extended an existing structure tree rather than writing a fresh one.
        /// </summary>
        /// <remarks>
        /// <b>Distinct from "the option was set".</b> A caller can ask to merge against a document
        /// that has no tree, in which case this pass writes a fresh one and there is nothing to
        /// report — so the option says what was wanted and this says what happened. A getter with
        /// an internal setter rather than a widened constructor, which C# would bake into every
        /// existing call site as a binary break.
        /// </remarks>
        public bool MergedIntoExistingTree { get; internal set; }

        /// <summary>
        /// Pages left alone because the existing structure tree already reaches their content.
        /// </summary>
        /// <remarks>
        /// Only ever non-zero under <c>AutoTagOptions.MergeIntoExistingTree</c>: without it the
        /// existing tree is superseded and no page is anyone else's. Each is also named in
        /// <see cref="SkippedPageReasons"/>, so a caller sees which as well as how many.
        /// </remarks>
        public int PagesAlreadyTagged { get; internal set; }

        /// <summary>
        /// Number of Form XObjects whose text was marked — each with its own <c>/StructParents</c>,
        /// its own MCID space, and structure elements whose <c>/K</c> is an <c>/MCR</c> naming the
        /// form stream through <c>/Stm</c>.
        ///
        /// <para>
        /// Counted per <em>form</em>, not per copy: a form shared by three pages and duplicated for
        /// each of them counts once here and once in <see cref="SharedFormsDuplicated"/>.
        /// </para>
        /// </summary>
        public int FormsTagged { get; internal set; }

        /// <summary>
        /// Number of Form XObjects that were drawn by more than one page and were given a separate
        /// copy per page so each could carry its own structure.
        ///
        /// <para>
        /// Non-zero means <b>the output's object graph differs from the input's</b> beyond the
        /// tagging itself: new form objects exist, and the pages after the first carry their
        /// own <c>/Resources</c>. Set <see cref="AutoTagOptions.DuplicateSharedFormXObjects"/> to
        /// false to forbid that, at the cost of leaving those pages partially tagged.
        /// </para>
        /// </summary>
        public int SharedFormsDuplicated { get; internal set; }

        /// <summary>
        /// Number of pages that were tagged but only <b>partially</b>: the page-level content
        /// stream was marked, and the page also invokes a Form XObject whose text lives in a
        /// separate stream this pass does not rewrite.
        ///
        /// <para>
        /// A non-zero value means the document carries unmarked real content despite declaring
        /// itself tagged (ISO 14289-1 §7.1), so <see cref="ContentMarked"/> being true does not
        /// mean <em>all</em> content is marked. Each such page also contributes one entry to
        /// <see cref="Warnings"/>.
        /// </para>
        /// </summary>
        public int PagesPartiallyTagged { get; }

        /// <summary>
        /// Creates a result with the original six-parameter surface.
        ///
        /// <para>
        /// <b>Kept as a distinct overload deliberately.</b> C# bakes optional-argument defaults in
        /// at the call site, so widening this constructor with optional parameters instead of
        /// adding one would be a <em>binary</em> break: an assembly already compiled against the
        /// six-parameter form emits a <c>newobj</c> for a six-parameter member and would fail with
        /// <see cref="MissingMethodException"/> at run time even though it still compiles from
        /// source. This library ships as NuGet packages, so that is a real consumer break.
        /// </para>
        /// </summary>
        public AutoTagResult(
            int elementsAdded,
            int pagesProcessed,
            IReadOnlyDictionary<int, int> headingsByLevel,
            int paragraphCount,
            int artifactCount,
            IReadOnlyList<string> warnings)
            : this(elementsAdded, pagesProcessed, headingsByLevel, paragraphCount, artifactCount,
                   warnings, 0, 0, null, false, 0)
        {
        }

        /// <summary>
        /// Creates a result. The first six parameters are the original surface and keep their
        /// order and names; everything after them is optional.
        /// </summary>
        public AutoTagResult(
            int elementsAdded,
            int pagesProcessed,
            IReadOnlyDictionary<int, int> headingsByLevel,
            int paragraphCount,
            int artifactCount,
            IReadOnlyList<string> warnings,
            int mcidCount = 0,
            int pagesSkipped = 0,
            IReadOnlyList<string> skippedPageReasons = null,
            bool contentMarked = false,
            int pagesPartiallyTagged = 0)
        {
            PagesPartiallyTagged = pagesPartiallyTagged;
            ElementsAdded      = elementsAdded;
            PagesProcessed     = pagesProcessed;
            HeadingsByLevel    = headingsByLevel ?? NoHeadings;
            ParagraphCount     = paragraphCount;
            ArtifactCount      = artifactCount;
            Warnings           = warnings ?? NoStrings;
            McidCount          = mcidCount;
            PagesSkipped       = pagesSkipped;
            SkippedPageReasons = skippedPageReasons ?? NoStrings;
            ContentMarked      = contentMarked;
        }

        public override string ToString()
        {
            var sb = new StringBuilder(160);
            sb.Append("AutoTag: ")
              .Append(ElementsAdded.ToString(CultureInfo.InvariantCulture))
              .Append(" elements on ")
              .Append(PagesProcessed.ToString(CultureInfo.InvariantCulture))
              .Append(" pages (")
              .Append(ParagraphCount.ToString(CultureInfo.InvariantCulture))
              .Append(" paragraphs, ")
              .Append(ArtifactCount.ToString(CultureInfo.InvariantCulture))
              .Append(" artifacts, ")
              .Append(McidCount.ToString(CultureInfo.InvariantCulture))
              .Append(" MCIDs");

            if (PagesSkipped > 0)
                sb.Append("; ").Append(PagesSkipped.ToString(CultureInfo.InvariantCulture)).Append(" pages skipped");

            if (PagesPartiallyTagged > 0)
                sb.Append("; ").Append(PagesPartiallyTagged.ToString(CultureInfo.InvariantCulture))
                  .Append(" pages only PARTIALLY tagged (text inside Form XObjects is not marked)");

            if (!ContentMarked)
                sb.Append("; NO CONTENT MARKED");

            sb.Append(')');
            return sb.ToString();
        }
    }

    /// <summary>Result of setting alt text on figures.</summary>
    public sealed class SetAltTextResult
    {
        /// <summary>Number of figures that received new alt text.</summary>
        public int Updated { get; }

        /// <summary>Number of figures that already had alt text (skipped).</summary>
        public int Skipped { get; }

        /// <summary>Number of figures where alt text could not be set.</summary>
        public int Failed { get; }

        public SetAltTextResult(int updated, int skipped, int failed)
        {
            Updated = updated;
            Skipped = skipped;
            Failed  = failed;
        }
    }

    /// <summary>Context passed to the user-supplied alt-text provider callback.</summary>
    public sealed class FigureContext
    {
        /// <summary>Zero-based index among all Figure elements in the structure tree.</summary>
        public int FigureIndex { get; }

        /// <summary>Zero-based page index the figure appears on (-1 if unknown).</summary>
        public int PageIndex { get; }

        /// <summary>Existing alt text, or null if none.</summary>
        public string ExistingAltText { get; }

        public FigureContext(int figureIndex, int pageIndex, string existingAltText)
        {
            FigureIndex     = figureIndex;
            PageIndex       = pageIndex;
            ExistingAltText = existingAltText;
        }
    }

    /// <summary>
    /// One image the auto-tagger is offering to wrap in a <c>/Figure</c>, handed to
    /// <c>AutoTagOptions.FigureAlternateText</c> so it can supply the <c>/Alt</c>.
    /// </summary>
    /// <remarks>
    /// <b>It carries no <c>PdfDictionary</c>, and that is deliberate.</b> This assembly's public
    /// surface names no type from the object model, and adding one here would make every consumer
    /// of a callback compile against <c>Tetitulz.Pdf.Core</c>'s parsing types to answer a question
    /// about a picture. The name, the page and the pixel dimensions are what a lookup table, a
    /// filename convention or a vision service needs; a caller that wants the samples themselves
    /// already has the input document and <c>PdfImageReplacer.FindImages</c>.
    /// </remarks>
    public sealed class FigureCandidate
    {
        /// <summary>
        /// Zero-based index of the page the image is drawn on, or <c>-1</c> when it is drawn on
        /// <em>several</em> pages.
        /// </summary>
        /// <remarks>
        /// <b><c>-1</c> means a Form XObject that more than one page draws.</b> The form is tagged
        /// once and its bytes shared by every copy, so there is no single page to name — and
        /// naming the first invoking page would hand a caller keying a cache on (page, name) the
        /// wrong page for all but one of them. The picture is still offered and still described;
        /// only the page is unavailable, and saying so is the difference between an absent value
        /// and a false one. <see cref="PageIndexes"/> names every page that draws it.
        /// </remarks>
        public int PageIndex { get; }

        /// <summary>
        /// Zero-based indexes of every page that draws the image, in page order: one for an image a
        /// page draws itself or through a form only it draws, several for a form several pages draw.
        /// </summary>
        /// <remarks>
        /// <b>The answer <see cref="PageIndex"/>'s <c>-1</c> leaves out.</b> A shared form's picture is
        /// one picture on several pages, so the list is the truthful form of "which page": a caller
        /// keying a cache on (page, name) can enter it under each. Empty only when a candidate was
        /// made with the four-argument constructor and <c>-1</c>.
        /// </remarks>
        public IReadOnlyList<int> PageIndexes { get; }

        /// <summary>
        /// The resource name the content stream invokes it by — the <c>Im0</c> of <c>/Im0 Do</c>.
        /// </summary>
        /// <remarks>
        /// <b>Unique within a page's resources, not within the document.</b> Two pages may both
        /// call their image <c>/Im0</c> and mean different pictures, so a cache keyed on this alone
        /// is wrong across pages; key on the pair with <see cref="PageIndex"/>.
        /// </remarks>
        public string XObjectName { get; }

        /// <summary>
        /// Whether the candidate is an inline image (<c>BI…EI</c>), drawn in the content stream
        /// itself rather than through a resource; then <see cref="XObjectName"/> is <c>null</c> and
        /// <see cref="InlineImageIndex"/> names it.
        /// </summary>
        public bool IsInlineImage { get; }

        /// <summary>
        /// For an inline image, its zero-based position among the inline images of the content
        /// stream that draws it — the page's own, or a form's; <c>-1</c> for an image XObject. Key a
        /// cache on (<see cref="PageIndex"/>, this), as on (page, name) for an XObject.
        /// </summary>
        public int InlineImageIndex { get; } = -1;

        /// <summary>The image's <c>/Width</c> in samples, or 0 when it could not be read.</summary>
        public int PixelWidth { get; }

        /// <summary>The image's <c>/Height</c> in samples, or 0 when it could not be read.</summary>
        public int PixelHeight { get; }

        public FigureCandidate(int pageIndex, string xObjectName, int pixelWidth, int pixelHeight)
            : this(pageIndex, pageIndex >= 0 ? new[] { pageIndex } : new int[0], xObjectName, pixelWidth, pixelHeight)
        {
        }

        /// <summary>A candidate drawn on <paramref name="pageIndexes"/>; <paramref name="pageIndex"/> is <c>-1</c> when they are several.</summary>
        public FigureCandidate(int pageIndex, IReadOnlyList<int> pageIndexes, string xObjectName, int pixelWidth, int pixelHeight)
        {
            PageIndex   = pageIndex;
            PageIndexes = pageIndexes ?? new int[0];
            XObjectName = xObjectName;
            PixelWidth  = pixelWidth;
            PixelHeight = pixelHeight;
        }

        /// <summary>An inline image drawn on <paramref name="pageIndexes"/>, the <paramref name="inlineImageIndex"/>th of its content stream's.</summary>
        public FigureCandidate(int pageIndex, IReadOnlyList<int> pageIndexes, int inlineImageIndex, int pixelWidth, int pixelHeight)
            : this(pageIndex, pageIndexes, (string)null, pixelWidth, pixelHeight)
        {
            if (inlineImageIndex < 0) throw new ArgumentOutOfRangeException(nameof(inlineImageIndex), inlineImageIndex, "an inline image's index is zero or more");
            IsInlineImage = true;
            InlineImageIndex = inlineImageIndex;
        }
    }

    /// <summary>Result of a reading-order fix operation.</summary>
    public sealed class ReadingOrderFixResult
    {
        /// <summary>Number of elements whose order was changed.</summary>
        public int ElementsReordered { get; }

        /// <summary>True if the structure tree was modified.</summary>
        public bool Modified => ElementsReordered > 0;

        public ReadingOrderFixResult(int reordered) => ElementsReordered = reordered;
    }

    /// <summary>A single element whose position in the structure tree appears out of visual order.</summary>
    public sealed class ReadingOrderIssue
    {
        /// <summary>Structure element type (e.g. "P", "H1").</summary>
        public string ElementType { get; }

        /// <summary>Zero-based page index.</summary>
        public int PageIndex { get; }

        /// <summary>Position in the structure tree (0-based).</summary>
        public int TreePosition { get; }

        /// <summary>Expected visual position (0-based) based on bounding-box order.</summary>
        public int ExpectedVisualPosition { get; }

        public ReadingOrderIssue(string elementType, int pageIndex, int treePos, int expectedPos)
        {
            ElementType           = elementType;
            PageIndex             = pageIndex;
            TreePosition          = treePos;
            ExpectedVisualPosition = expectedPos;
        }

        public override string ToString() =>
            $"[{ElementType}] page {PageIndex + 1}: tree pos {TreePosition}, expected visual pos {ExpectedVisualPosition}";
    }

    /// <summary>
    /// Result of a reading-order analysis.
    ///
    /// <para>
    /// <b>Check <see cref="Analyzed"/> before trusting <see cref="IsOrdered"/>.</b> A document that
    /// could not be examined at all — unparseable, encrypted, or carrying no structure tree — has
    /// no issues to report, which is not the same claim as "its reading order is correct".
    /// <see cref="NotAnalyzed"/> produces exactly that distinction.
    /// </para>
    /// </summary>
    public sealed class ReadingOrderAnalysis
    {
        private static readonly IReadOnlyList<ReadingOrderIssue> NoIssues = new ReadingOrderIssue[0];

        /// <summary>Issues where structure tree order differs from visual reading order. Never null.</summary>
        public IReadOnlyList<ReadingOrderIssue> Issues { get; }

        /// <summary>True if no ordering issues were found.</summary>
        public bool IsOrdered => Issues.Count == 0;

        /// <summary>
        /// True when the document was actually examined. False when the analysis could not run,
        /// in which case <see cref="Issues"/> is empty because nothing was checked — not because
        /// the document is clean — and <see cref="NotAnalyzedReason"/> says why.
        /// </summary>
        public bool Analyzed { get; }

        /// <summary>
        /// Why the analysis could not run, or null when <see cref="Analyzed"/> is true.
        /// </summary>
        public string NotAnalyzedReason { get; }

        /// <summary>
        /// Number of structure elements that could be located on a page and therefore took part
        /// in the ordering comparison. Zero when <see cref="Analyzed"/> is false.
        /// </summary>
        public int ElementsPlaced { get; }

        /// <summary>
        /// Number of structure elements found in the tree, whether or not each could be located.
        /// Zero when <see cref="Analyzed"/> is false.
        /// </summary>
        public int ElementsTotal { get; }

        /// <summary>
        /// True when the analysis ran but could position only some of the tree.
        ///
        /// <para>
        /// <b>Check this before trusting <see cref="IsOrdered"/>.</b> An element with no
        /// resolvable marked-content identifier cannot be placed and is silently absent from the
        /// comparison, and pages left with fewer than two placed elements contribute no findings
        /// at all. A document where 2 of 100 000 elements resolved therefore reports
        /// <c>Analyzed = true, IsOrdered = true</c> — "I examined 0.002% of your document and it
        /// looked fine", which is indistinguishable from a real pass without this flag.
        /// </para>
        /// </summary>
        public bool IsPartial => Analyzed && ElementsPlaced < ElementsTotal;

        public ReadingOrderAnalysis(IReadOnlyList<ReadingOrderIssue> issues)
            : this(issues, true, null)
        {
        }

        public ReadingOrderAnalysis(IReadOnlyList<ReadingOrderIssue> issues, bool analyzed, string notAnalyzedReason)
            : this(issues, analyzed, notAnalyzedReason, 0, 0)
        {
        }

        public ReadingOrderAnalysis(
            IReadOnlyList<ReadingOrderIssue> issues,
            bool analyzed,
            string notAnalyzedReason,
            int elementsPlaced,
            int elementsTotal)
        {
            Issues            = issues ?? NoIssues;
            Analyzed          = analyzed;
            NotAnalyzedReason = analyzed
                ? null
                : (string.IsNullOrEmpty(notAnalyzedReason) ? "the reading order was not analyzed" : notAnalyzedReason);
            ElementsPlaced    = analyzed ? elementsPlaced : 0;
            ElementsTotal     = analyzed ? elementsTotal  : 0;
        }

        /// <summary>
        /// Creates a result that reports "not analyzed" rather than an empty (and therefore
        /// falsely reassuring) issue list.
        /// </summary>
        public static ReadingOrderAnalysis NotAnalyzed(string reason)
            => new ReadingOrderAnalysis(NoIssues, false, reason);

        public override string ToString()
            => Analyzed
                ? "ReadingOrder: " + Issues.Count + " issue(s)"
                  + (IsPartial
                        ? " (PARTIAL: only " + ElementsPlaced + " of " + ElementsTotal +
                          " structure elements could be located on a page)"
                        : string.Empty)
                : "ReadingOrder: not analyzed — " + NotAnalyzedReason;
    }

    /// <summary>Result of marking headers/footers as artifacts.</summary>
    public sealed class ArtifactMarkResult
    {
        /// <summary>Number of structure elements added or updated as artifacts.</summary>
        public int ArtifactsMarked { get; }

        public ArtifactMarkResult(int marked) => ArtifactsMarked = marked;
    }
}
