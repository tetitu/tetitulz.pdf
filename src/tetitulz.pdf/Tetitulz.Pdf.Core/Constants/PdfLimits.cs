// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Hard bounds applied when processing UNTRUSTED PDF input, so a malformed or adversarial file
    /// cannot exhaust the stack, the heap or the CPU.
    ///
    /// <para><b>Why these are centralised.</b> Caps kept as private constants across the tree
    /// contradict each other under shared names — one <c>MaxDepth</c> meaning 24, another 200, a
    /// third 1000; two table renderers implementing the same feature capping nesting at 24 and 8 —
    /// and a reader has no way to tell which value is deliberate and which a copy-paste
    /// accident.</para>
    ///
    /// <para><b>What this type is not.</b> It is a table of values, not a mechanism. It does not
    /// replace <see cref="Util.WorkBudget"/>, which exists for the different problem of bounding
    /// <i>aggregate</i> work across nested loops that individually look bounded. Guards keep their
    /// current shape — including the compute-the-maximum-up-front shape that
    /// <see cref="Util.WorkBudget"/>'s documentation specifically recommends for CCITT rows, JBIG2
    /// instance counts and xref entry counts — and read only their value from here.</para>
    ///
    /// <para><b>Changing a value here changes DoS-guard behaviour.</b> Each has an adversarial
    /// regression test; raising one weakens a real protection.</para>
    ///
    /// <para><b>Every limit is the caller's to set.</b> A library cannot pick a consumer's threat
    /// model — only they know whether the next file is a hostile upload or a 400-page press proof —
    /// so each limit is a settable property with its built-in value in a matching
    /// <c>Default…</c> constant.</para>
    ///
    /// <para><b>Read the property; use the constant only where the language demands one.</b> About
    /// thirty-five sites in this tree need a compile-time constant — another <c>const</c>, a default
    /// parameter value, an <c>[InlineData]</c> argument — and that is what <c>Default…</c> is for.
    /// Anywhere else it silently pins the built-in value and an override never arrives. That is not
    /// hypothetical: these were all <c>const</c>, and thirty-three private aliases of the form
    /// <c>private const X = PdfLimits.Y;</c> had baked their values in at compile time.</para>
    ///
    /// <para><b>They are process-global, so set them once at startup, before any parsing.</b> There
    /// is deliberately no per-document or per-thread scoping: these are read in the hottest paths in
    /// the library — every reference resolve, every glyph — and an ambient lookup on each read would
    /// cost more than the guard it feeds. Where a ceiling genuinely needs to differ per call, the
    /// entry point takes it as a parameter instead (<c>PdfXValidator.Validate</c>,
    /// <c>PdfUaChecker.Validate</c>, <c>PdfRichMediaExtractor.ListRichMedia</c>,
    /// <c>Pdf3DExtractor.List3DAnnotations</c>, <c>Woff2Decoder.Decode</c>).</para>
    ///
    /// <para><b>Values are not validated on assignment.</b> The guards fail closed: a ceiling of
    /// zero or below refuses everything rather than admitting it, which is the safe direction, and
    /// clamping a caller's number would be this class deciding the threat model after all.
    /// <see cref="ResetToDefaults"/> puts everything back.</para>
    /// </summary>
    public static class PdfLimits
    {
        // ------------------------------------------------------------------
        //  Object-graph traversal
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum depth when walking or cloning an arbitrary PDF object graph — parsing nested
        /// arrays/dictionaries, deep-copying, remapping object numbers, collecting references.
        /// Deep enough for any legitimate document; shallow enough that a self-nesting structure
        /// cannot overflow the stack.
        /// </summary>
        public static int MaxObjectGraphDepth { get; set; } = DefaultMaxObjectGraphDepth;

        /// <summary>Built-in value of <see cref="MaxObjectGraphDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxObjectGraphDepth = 256;

        /// <summary>
        /// Maximum depth of a name tree, number tree, outline tree or field tree. All four are
        /// <c>/Kids</c>-linked balanced trees whose legitimate depth is logarithmic in entry count,
        /// so 100 is far beyond any real file.
        /// </summary>
        public static int MaxTreeDepth { get; set; } = DefaultMaxTreeDepth;

        /// <summary>Built-in value of <see cref="MaxTreeDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxTreeDepth = 100;

        /// <summary>
        /// Maximum <c>/Parent</c> hops when resolving an <b>inheritable AcroForm field
        /// attribute</b> — ISO 32000-1 Table 220 (<c>/FT</c>, <c>/Ff</c>, <c>/V</c>, <c>/DV</c>)
        /// and Table 222 §12.7.3.3 (<c>/DA</c>, <c>/Q</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Deliberately not <see cref="MaxTreeDepth"/>, and deliberately not
        /// <c>Plugins.Conformance.AcroFormFieldWalk.MaxFieldDepth</c>.</b> Those two bound a
        /// <c>/Kids</c> DESCENT, whose legitimate depth is logarithmic in field count; this bounds
        /// a <c>/Parent</c> CLIMB, which is a linked list an adversary writes directly and can
        /// point in a ring. Centralising a value is not the same as unifying it.</para>
        ///
        /// <para><b>The distinction is load-bearing, not tidiness.</b>
        /// <c>AcroFormFieldWalk.MaxFieldDepth</c>'s descent half is pinned by a cross-plugin invariant
        /// that <c>PdfSplitter</c> depends on through <c>PageEdits.AcroFormPageFilter</c>. Moving the
        /// climb through the shared symbol would drag that descent with it — one-sided and silent,
        /// since nothing in PageEdits changes to signal it. <c>CrossPluginConstantInvariantTests</c>
        /// holds that pin.</para>
        ///
        /// <para><b>Only the two <c>/Parent</c>-KEY climbers take this.</b> Core's climbs walk the
        /// <i>arrival</i> chain — the nodes a <c>/Kids</c> descent actually came through — whose
        /// length is already bounded by the same walkers' own descent caps, so those keep
        /// <see cref="MaxTreeDepth"/> and nothing is lowered.</para>
        /// </remarks>
        public static int MaxFieldInheritanceDepth { get; set; } = DefaultMaxFieldInheritanceDepth;

        /// <summary>Built-in value of <see cref="MaxFieldInheritanceDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFieldInheritanceDepth = 50;

        /// <summary>
        /// The longest fully qualified field name (§12.7.4.2), in UTF-16 code units, that a join of
        /// partial names answers as it is. A longer one is answered as a stand-in of a fixed length:
        /// its first characters, a <c>#</c> and the SHA-256 of the whole name in hexadecimal
        /// (<c>Core.Forms.FieldNames.Qualify</c>), so two different names stay two names.
        /// </summary>
        /// <remarks>
        /// <para><b>A file chooses the product.</b> A field's name is its ancestors' partial names
        /// joined, so without a ceiling one parent named by a string of a megabyte, over ten thousand
        /// fields, makes ten thousand names of a megabyte each — some 20 GB from a file of about one — in every reader that
        /// lists the fields. Under this ceiling a name costs at most this much, and a field count the
        /// file's own bytes bound.</para>
        /// <para>No form a person fills has a name near it: a designer's deepest generated names run to
        /// a few hundred characters. A caller addresses a field past it by the stand-in the readers
        /// list, never by the full name.</para>
        /// </remarks>
        public static int MaxQualifiedFieldNameLength { get; set; } = DefaultMaxQualifiedFieldNameLength;

        /// <summary>Built-in value of <see cref="MaxQualifiedFieldNameLength"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxQualifiedFieldNameLength = 1024;

        /// <summary>
        /// Largest decoded widget appearance stream the renderers will parse purely to decide
        /// whether it is <i>stale</i> under §12.7.3.3 <c>/NeedAppearances</c>.
        ///
        /// <para><b>This bounds a read that is not the drawing read.</b> When the form sets
        /// <c>/NeedAppearances</c> the renderer asks whether the existing appearance already shows
        /// the field's value; if it does, the appearance is drawn as before and the stream is parsed
        /// twice. Past this ceiling the question is answered <c>"it draws"</c> without parsing — the
        /// conservative answer, which keeps the file's own appearance — so a hostile stream can cost
        /// at most the one parse the drawing path was always going to do.</para>
        ///
        /// <para>A real widget appearance is tens of bytes; 256&#160;KiB is four orders of magnitude
        /// of headroom and still far below <see cref="MaxPluginContentStreamBytes"/>.</para>
        /// </summary>
        public static int MaxWidgetAppearanceScanBytes { get; set; } = DefaultMaxWidgetAppearanceScanBytes;

        /// <summary>Built-in value of <see cref="MaxWidgetAppearanceScanBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxWidgetAppearanceScanBytes = 256 * 1024;

        /// <summary>
        /// Maximum number of name-tree entries flattened in one operation, counted across every
        /// category and every source document.
        ///
        /// <para><b>Depth is not enough here, because a name tree is unbounded in BREADTH.</b>
        /// <see cref="MaxTreeDepth"/> bounds the <c>/Kids</c> chain and a visited set bounds its
        /// cycles, but neither bounds a single leaf holding millions of <c>/Names</c> pairs — and
        /// flattening one costs far more than the file did, since each entry becomes a live key
        /// array plus a cloned value. Merging N documents multiplies that again, which is why the
        /// budget is shared by all of them rather than applied per tree. Past the cap the walk
        /// stops and the truncation is reported, never silently dropped.</para>
        /// </summary>
        public static int MaxNameTreeEntries { get; set; } = DefaultMaxNameTreeEntries;

        /// <summary>Built-in value of <see cref="MaxNameTreeEntries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxNameTreeEntries = 500_000;

        /// <summary>
        /// Maximum number of annotations examined on one page by the annotation inventory and the
        /// link extractor. Past it the rest of the page is skipped and the truncation is reported on
        /// each one's <c>LastError</c>.
        ///
        /// <para><b>One value</b>, settable, for the annotation and link extractors alike — two
        /// constants that say they match are two values. It bounds volume on
        /// an already-parsed array, so a page that reaches it is more likely a generated index or a
        /// map overlay than an attack.</para>
        /// </summary>
        public static int MaxAnnotationsPerPage { get; set; } = DefaultMaxAnnotationsPerPage;

        /// <summary>Built-in value of <see cref="MaxAnnotationsPerPage"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxAnnotationsPerPage = 50_000;

        /// <summary>
        /// The annotation entries one whole-document read examines — the link and annotation
        /// extractors and the annotation editor; past it the rest are not examined, and the caller
        /// is told.
        /// </summary>
        /// <remarks>
        /// <see cref="MaxAnnotationsPerPage"/> bounds a page. Pages may name one <c>/Annots</c>
        /// array, which §12.5.2 forbids and nothing prevents, so a document's entries are pages ×
        /// entries, two counts the file chooses: 800 pages naming one array of 800 links took the
        /// link extractor 1.8 s against 99 ms for 200, about 3 µs an entry,
        /// and a megabyte of such a file names tens of millions. The default is about three seconds.
        /// </remarks>
        public static int MaxAnnotationsPerDocument { get; set; } = DefaultMaxAnnotationsPerDocument;

        /// <summary>Built-in value of <see cref="MaxAnnotationsPerDocument"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxAnnotationsPerDocument = 1_000_000;

        /// <summary>
        /// The most floats one page of a Builder export keeps as live exclusions, and the point past
        /// which placing a float stops stepping down the page to find room beside the others.
        /// </summary>
        /// <remarks>
        /// A float does not advance the flow cursor, so a page of nothing but floats accumulates them
        /// all as exclusions, and placing each one scanned every exclusion already on the page (twice)
        /// once per float bottom it stepped past — <c>O(n³)</c> in the floats on a page, which a column
        /// of near-full-width floats reaches. Past this cap a page stops registering new
        /// exclusions and placing a float no longer steps, so its remaining floats are laid out beside
        /// the capped set at their own line or fall to a block below it; the work a page spends on
        /// float placement is then bounded by this cap, not by its float count. A page with no more
        /// floats than the cap is laid out uncapped. The default admits any real page — a
        /// dense gallery or grid is far below it — while bounding a hostile one.
        /// </remarks>
        public static int MaxBuilderFloatsPerPage { get; set; } = DefaultMaxBuilderFloatsPerPage;

        /// <summary>Built-in value of <see cref="MaxBuilderFloatsPerPage"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxBuilderFloatsPerPage = 1_000;

        /// <summary>
        /// Maximum number of fields — and, in an FDF, of indirect objects — read from one
        /// FDF / XFDF form-data file (§12.7.7, ISO 19444-1).
        ///
        /// <para><b>These files arrive from outside</b>, which is the whole point of an interchange
        /// format: a caller importing one is running a stranger's bytes through a parser. Depth is
        /// bounded by <see cref="MaxTreeDepth"/>, but a flat file listing a hundred million fields
        /// is neither deep nor cyclic, and every entry becomes a live string pair. Past the cap the
        /// read is refused with a reason rather than truncated — a partially-imported form is
        /// indistinguishable from a fully-imported one at the call site, and would overwrite the
        /// document's values with an incomplete set.</para>
        /// </summary>
        public static int MaxFormDataFields { get; set; } = DefaultMaxFormDataFields;

        /// <summary>Built-in value of <see cref="MaxFormDataFields"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFormDataFields = 100_000;

        /// <summary>
        /// How many times over a document's length its signatures together may cover before the
        /// rest are not verified: the bytes each signature's <c>/ByteRange</c> names are read and
        /// digested, and the verification refuses, with the reason, every signature past the budget.
        /// </summary>
        /// <remarks>
        /// <para><b>Signatures × bytes.</b> Each signature may cover the whole file, and a signature
        /// dictionary is a few kilobytes, so the work grew as the square of the file: one genuine
        /// signature copied 200 times over a 4 MB revision verified 201 times in 10.8 s, about 13 ms
        /// a covered megabyte; a 40 MB revision with a thousand copies is nine
        /// minutes. Bounded by the file's own length, it is linear in it.</para>
        /// <para>A document of 64 signatures each covering it whole stays inside the default, as
        /// does one of about 128 whose signatures cover growing prefixes, the usual shape.</para>
        /// </remarks>
        public static int MaxSignatureCoverageMultiple { get; set; } = DefaultMaxSignatureCoverageMultiple;

        /// <summary>Built-in value of <see cref="MaxSignatureCoverageMultiple"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSignatureCoverageMultiple = 64;

        /// <summary>
        /// How many signatures and document timestamps one verification reads; every one past it is
        /// refused, with the reason.
        /// </summary>
        /// <remarks>
        /// <para>A signature costs about 16 ms however little it covers — BouncyCastle validates the
        /// RSA modulus of each certificate it parses with Miller–Rabin rounds (measured with
        /// 2 048-bit keys) — and a signature dictionary is a couple of kilobytes, so a file of copies
        /// verified for about ten seconds a megabyte. <see cref="MaxSignatureCoverageMultiple"/>
        /// bounds the bytes they digest, not their number. The default bounds that work at about
        /// eight seconds; a document signed by a few dozen people is far inside it.</para>
        /// </remarks>
        public static int MaxSignaturesPerDocument { get; set; } = DefaultMaxSignaturesPerDocument;

        /// <summary>Built-in value of <see cref="MaxSignaturesPerDocument"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSignaturesPerDocument = 500;

        /// <summary>
        /// Maximum size, in bytes, of an FDF form-data file read in one call.
        ///
        /// <para>FDF alone needs this and XFDF does not: XFDF streams through an
        /// <c>XmlReader</c> and is bounded by <see cref="MaxFormDataFields"/> as it goes, while
        /// FDF's cross-reference table cannot be trusted, so the objects are found by scanning the
        /// whole file — which means holding it. 64 MiB is far past any real form's data and far
        /// short of a memory hazard.</para>
        /// </summary>
        public static long MaxFormDataBytes { get; set; } = DefaultMaxFormDataBytes;

        /// <summary>Built-in value of <see cref="MaxFormDataBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxFormDataBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Maximum number of <c>/Kids</c> <b>slots</b> examined while walking one name or number
        /// tree (§7.9.6, §7.9.7) — the third guard those walks need, beside
        /// <see cref="MaxTreeDepth"/> and a visited set.
        ///
        /// <para><b>A visited set bounds how many times a node is EXPANDED. It does not bound the
        /// cost of expanding one.</b> Give every node a <c>/Kids</c> pointing at the <i>same</i>
        /// shared array of N references and each of the N nodes iterates and resolves all N
        /// entries — O(N²) — while the visited set is perfectly happy, because every node is
        /// expanded exactly once and every kid after the first pass is correctly skipped. Nothing
        /// counted the iterations. At N = 100 000 that is 10<sup>10</sup> resolutions from a small
        /// compressed file.</para>
        ///
        /// <para>It is the same distinction <see cref="MaxNameTreeEntries"/> draws for breadth and
        /// the one <c>Accessibility.StructTreeWalker.DefaultMaxEdges</c> already draws in its own
        /// doc comment for the structure tree — this is that guard, for the two trees Core owns.
        /// A real tree has about one edge per node, so a ceiling four times
        /// <see cref="MaxNameTreeEntries"/> cannot be reached legitimately.</para>
        ///
        /// <para><b>Charged separately from <see cref="MaxNameTreeEntries"/> on purpose.</b>
        /// Sharing one counter would make a legitimate 500 000-entry tree — which necessarily has
        /// about 500 000 kid slots too — truncate at roughly half its real size.</para>
        /// </summary>
        public static int MaxTreeKidSlots { get; set; } = DefaultMaxTreeKidSlots;

        /// <summary>Built-in value of <see cref="MaxTreeKidSlots"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxTreeKidSlots = 2_000_000;

        /// <summary>
        /// Maximum depth of the page tree. Higher than <see cref="MaxTreeDepth"/> because
        /// generators exist that emit one <c>/Pages</c> node per page, producing a legitimately
        /// deep but degenerate tree.
        /// </summary>
        public static int MaxPageTreeDepth { get; set; } = DefaultMaxPageTreeDepth;

        /// <summary>Built-in value of <see cref="MaxPageTreeDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPageTreeDepth = 1024;

        /// <summary>
        /// Maximum depth of the logical structure tree (<c>/StructTreeRoot</c>). Tagged documents
        /// nest deeply in tables and nested lists, so this is deliberately generous.
        /// </summary>
        public static int MaxStructTreeDepth { get; set; } = DefaultMaxStructTreeDepth;

        /// <summary>Built-in value of <see cref="MaxStructTreeDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxStructTreeDepth = 1000;

        /// <summary>
        /// Hard ceiling on <c>/K</c> child <em>slots</em> examined across one walk of a logical
        /// structure tree.
        /// </summary>
        /// <remarks>
        /// <para><b>A depth cap and a visited set do not bound total work, and this is the constant
        /// that does.</b> A visited set bounds how many times a node is <i>expanded</i>; it does not
        /// bound the cost of expanding one. A document whose structure elements all point <c>/K</c>
        /// at a single shared array of N references costs N per node over N nodes — O(N²) — while
        /// the visited set is satisfied and every node is expanded exactly once. At N = 100 000
        /// that is 10<sup>10</sup> probes from a ~200 KB compressed file.</para>
        ///
        /// <para>A real tree has about one edge per node, so this is far above anything legitimate:
        /// a 10 000-page report's tree lands in the low hundreds of thousands of nodes.</para>
        ///
        /// <para><b>It is the ceiling of the one walk every reader of the tree uses</b>
        /// (`Base.Operations.Structure.StructureTreeWalk`), and it was found by comparing walks:
        /// while `Accessibility.Internal.StructTreeWalker`, `PdfTaggedTextExtractor` and
        /// `PdfStructureTreeReader` each read the same tree from untrusted input themselves, the
        /// extractor, and then the reader, turned out to have the depth cap and the visited set but
        /// not this one.</para>
        /// </remarks>
        public static int MaxStructTreeEdges { get; set; } = DefaultMaxStructTreeEdges;

        /// <summary>Built-in value of <see cref="MaxStructTreeEdges"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxStructTreeEdges = 2_000_000;

        /// <summary>
        /// Hard ceiling on the marked-content identifier (<c>/MCID</c>) a page's content may carry for
        /// the auto-tagger to extend its marked content. Default 1 000 000.
        /// </summary>
        /// <remarks>
        /// The identifiers the tagger mints count up from the largest one the page carries, and the
        /// parent tree's array for a page holds one slot per identifier up to the last (§14.7.4.4):
        /// an <c>/MCID</c> of two billion in one sequence is a two-billion-slot array, and one past
        /// <c>int.MaxValue</c> wraps. A page past this ceiling is left as it is, and said.
        /// </remarks>
        public static int MaxMarkedContentIdentifier { get; set; } = DefaultMaxMarkedContentIdentifier;

        /// <summary>Built-in value of <see cref="MaxMarkedContentIdentifier"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxMarkedContentIdentifier = 1_000_000;

        /// <summary>
        /// Hard ceiling on the steps the OpenType layout reader takes over a <c>GPOS</c> or
        /// <c>GSUB</c> script list: one per script record, per feature index of a language system
        /// and per lookup index of a feature.
        /// </summary>
        /// <remarks>
        /// The three counts are all 16-bit fields the font chooses, and the walk is their product: a
        /// 660 KB font declaring 65 535 scripts that share one language system of 65 535 feature
        /// indices, all naming one feature of 65 535 lookup indices, asked for 2.8 × 10¹⁴ reads —
        /// days — from every producer that embeds it. A real font has a few scripts, a few dozen
        /// features and a few hundred lookups: a few thousand steps.
        /// </remarks>
        public static int MaxOpenTypeScriptSteps { get; set; } = DefaultMaxOpenTypeScriptSteps;

        /// <summary>Built-in value of <see cref="MaxOpenTypeScriptSteps"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxOpenTypeScriptSteps = 1_000_000;

        /// <summary>
        /// Hard ceiling on the number of structure-tree nodes one walk visits. Well past any real
        /// tagged document and low enough that a hostile tree cannot exhaust memory.
        /// </summary>
        public static int MaxStructTreeNodes { get; set; } = DefaultMaxStructTreeNodes;

        /// <summary>Built-in value of <see cref="MaxStructTreeNodes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxStructTreeNodes = 500_000;

        /// <summary>Maximum length of an annotation or action <c>/Next</c> chain.</summary>
        public static int MaxActionChainDepth { get; set; } = DefaultMaxActionChainDepth;

        /// <summary>Built-in value of <see cref="MaxActionChainDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxActionChainDepth = 64;

        // ------------------------------------------------------------------
        //  Rendering and content streams
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum nesting of form XObjects, tiling patterns and Type 3 glyph procedures during
        /// rendering. Shared by the raster and SVG renderers so they cannot disagree about how
        /// deep a legal document may nest.
        /// </summary>
        public static int MaxRenderRecursionDepth { get; set; } = DefaultMaxRenderRecursionDepth;

        /// <summary>Built-in value of <see cref="MaxRenderRecursionDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxRenderRecursionDepth = 24;

        /// <summary>
        /// Maximum nesting of form XObjects during text extraction. Deliberately HIGHER than
        /// <see cref="MaxRenderRecursionDepth"/>: extraction recurses only through form XObjects —
        /// never through tiling patterns or Type 3 glyph procedures — so a level costs far less
        /// stack than a render level, and the two caps are not interchangeable.
        /// </summary>
        public static int MaxFormXObjectDepth { get; set; } = DefaultMaxFormXObjectDepth;

        /// <summary>Built-in value of <see cref="MaxFormXObjectDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFormXObjectDepth = 32;

        /// <summary>
        /// Aggregate work budget for one <c>PdfTextExtractor.ExtractText</c> call, in units of
        /// decoded content-stream bytes (see <see cref="FormXObjectInvocationCost"/> for the fixed
        /// per-invocation charge). Roughly 64&#160;MB of interpreted form content per page — orders
        /// of magnitude above any real document, and it does not bound the page's own stream, only
        /// the forms reached from it.
        /// </summary>
        /// <remarks>
        /// <para><see cref="MaxFormXObjectDepth"/> bounds depth and <b>never total work</b>. The
        /// extractor's cycle guard is a per-branch visited set copied at every <c>Do</c>, so a name
        /// already on the current branch is refused while the same name is re-entered freely on
        /// every sibling branch: with N mutually-referencing forms the traversal visits
        /// N!/(N−D)! nodes, each of them re-parsing a content stream and re-loading a font
        /// dictionary. Twelve forms of one line each — about 3&#160;KB of PDF — is
        /// 12! ≈ 4.8×10<sup>8</sup> invocations and the call never returns.</para>
        /// <para>This is the "per-loop caps do not compose" case
        /// <c>Core.Util.WorkBudget</c> exists for; the depth cap is the per-loop cap.</para>
        /// </remarks>
        public static long MaxTextExtractionWorkUnits { get; set; } = DefaultMaxTextExtractionWorkUnits;

        /// <summary>Built-in value of <see cref="MaxTextExtractionWorkUnits"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxTextExtractionWorkUnits = 64L * 1024 * 1024;

        /// <summary>
        /// Type 4 (PostScript calculator) operations one page's rendering may spend across every
        /// function it evaluates — a tint transform a pixel, a shading a sample, a spot function a cell
        /// pixel — and the same figure for one colour conversion and one halftone screen. Past it each
        /// further evaluation is refused at its first operation, and the renderer reports the image,
        /// shading or screen it could not draw.
        /// </summary>
        /// <remarks>
        /// One evaluation is bounded on its own (two million operations, its loops included), but a
        /// page runs a function once a pixel: a fifty-byte tint transform looping two million times
        /// over a thousand-pixel-square DeviceN image was 2 × 10¹² operations. A real tint transform is
        /// tens of operations, so a 4 000² DeviceN image spends under a billion.
        /// </remarks>
        public static long MaxFunctionWorkPerPage { get; set; } = DefaultMaxFunctionWorkPerPage;

        /// <summary>Built-in value of <see cref="MaxFunctionWorkPerPage"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxFunctionWorkPerPage = 1L << 30;

        /// <summary>
        /// Earlier drawings one page's duplicate-text rule (<c>Text.OverlappingTextIndex</c>, both
        /// extractors) may compare a glyph with, over the whole page and every form it reaches. Past
        /// it the rule stops — later copies are extracted as they are drawn — and the extractor says so.
        /// </summary>
        /// <remarks>
        /// A query reads the earlier drawings of its text in the cells its X window covers, and a
        /// column of one text at one X — a rotated line, or the same glyph shown down the page — puts
        /// them all in one cell: each glyph read every one before it, so a few hundred kilobytes of
        /// content were 10<sup>11</sup> comparisons. Sixty-four million is some tens of milliseconds, and
        /// thousands of times what a dense real page spends.
        /// </remarks>
        public static long MaxOverlappingTextComparisons { get; set; } = DefaultMaxOverlappingTextComparisons;

        /// <summary>Built-in value of <see cref="MaxOverlappingTextComparisons"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxOverlappingTextComparisons = 64L * 1024 * 1024;

        /// <summary>
        /// Aggregate work budget for one label-to-value field extraction call
        /// (<c>PdfFieldExtractor.Extract</c>, and the invoice extractor built on it), in words and
        /// label characters visited. Sixty-four million — a one-page invoice spends a few thousand
        /// per query, a hundred-page statement a few million in all.
        /// </summary>
        /// <remarks>
        /// Every occurrence of a label is a candidate, and a candidate with no value on its own line
        /// searches the whole page for a neighbour. A page of thirty thousand one-word labels is a few
        /// kilobytes deflated and was about 10<sup>9</sup> word visits; a call that runs out returns
        /// what it has found and says so in <c>PdfFieldExtractor.LastError</c>.
        /// </remarks>
        public static int MaxFieldExtractionWork { get; set; } = DefaultMaxFieldExtractionWork;

        /// <summary>Built-in value of <see cref="MaxFieldExtractionWork"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFieldExtractionWork = 64 * 1024 * 1024;

        /// <summary>
        /// Fixed cost charged against <see cref="MaxTextExtractionWorkUnits"/> for each Form XObject
        /// invocation, on top of the form's decoded byte count.
        /// </summary>
        /// <remarks>
        /// Without it a fan-out of empty or near-empty forms would be free, and it is precisely the
        /// tiny form that makes the factorial cheap to write. Every invocation costs a dictionary
        /// resolve, a stream decode, a visited-set copy, a font-dictionary load and a parse
        /// regardless of how few bytes it contains, so charging ~1&#160;KB of "equivalent content"
        /// for the fixed part is closer to the truth than charging nothing.
        /// </remarks>
        public static int FormXObjectInvocationCost { get; set; } = DefaultFormXObjectInvocationCost;

        /// <summary>Built-in value of <see cref="FormXObjectInvocationCost"/>; for a compile-time constant context.</summary>
        public const int DefaultFormXObjectInvocationCost = 1024;

        /// <summary>
        /// Maximum total form-XObject invocations while extracting text from one page.
        /// </summary>
        /// <remarks>
        /// <para><b>A depth cap and a branch-scoped visited set do not bound total work, and this
        /// is the constant that does.</b> Take 32 forms where form <i>i</i>'s content is
        /// <c>/F{i+1} Do /F{i+1} Do</c>: no form ever draws itself, so the visited set never
        /// fires; no path exceeds <see cref="MaxFormXObjectDepth"/>, so the depth cap never fires;
        /// and the walk still performs 2<sup>31</sup> invocations, each re-parsing a content stream
        /// and building a font. A few kilobytes of PDF, and extraction never returns.</para>
        ///
        /// <para>The bound is on the <i>aggregate</i>, so it composes where the per-branch guards
        /// do not. 100&#160;000 leaves ample room for genuinely form-heavy output (a page of
        /// repeated letterhead and signature stamps runs to hundreds, not tens of thousands).</para>
        /// </remarks>
        public static int MaxFormXObjectInvocationsPerPage { get; set; } = DefaultMaxFormXObjectInvocationsPerPage;

        /// <summary>Built-in value of <see cref="MaxFormXObjectInvocationsPerPage"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFormXObjectInvocationsPerPage = 100_000;

        /// <summary>
        /// Aggregate decoded content a page's form invocations may run through while rendering, in
        /// bytes — form XObjects, pattern cells, soft-mask groups and Type 3 glyph procedures, each
        /// charged its decoded length as it is invoked. Roughly 64 MB of interpreted form content per
        /// page, the extraction budget's figure (<see cref="MaxTextExtractionWorkUnits"/>); it does not
        /// bound the page's own stream.
        /// </summary>
        /// <remarks>
        /// <para><see cref="MaxFormXObjectInvocationsPerPage"/> bounds how many times a form runs and
        /// <b>never how large each run is</b>. A form of two thousand <c>Do</c> operators drawing
        /// itself is a 16 KB stream run whole at every invocation: a hundred thousand invocations is
        /// 1.6 GB of content from a 20 KB file, and the SVG writer, which caches no parse, ran for
        /// the whole of the structure fuzzer's two-minute window on four such files while the
        /// invocation count was still inside its ceiling. Counts a file chooses
        /// multiply; this is the bound on the product.</para>
        ///
        /// <para>Redaction charges the same figure for each form walk on a page it redacts
        /// (<c>PdfRedactionEngine</c>), and refuses past it: a walk may also write a clone of the form,
        /// so the product is the output's size as well as the work. At the default, a page whose forms
        /// show 64 MB of glyphs costs about sixteen seconds before the refusal.</para>
        /// </remarks>
        public static long MaxFormContentBytesPerPage { get; set; } = DefaultMaxFormContentBytesPerPage;

        /// <summary>Built-in value of <see cref="MaxFormContentBytesPerPage"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxFormContentBytesPerPage = 64L * 1024 * 1024;

        /// <summary>
        /// Aggregate pixels a page's offscreen renders may allocate while rendering — a tiling
        /// pattern's cell and the tile composed from its copies, a soft-mask group — each charged its
        /// bitmap's area as it is rendered, in the raster renderer (the SVG writer renders nothing
        /// offscreen). 2³⁰ pixels: a second or two of bitmap work per page.
        /// </summary>
        /// <remarks>
        /// <para>A pattern cell's invocation is not priced as a form's. Before its content runs it
        /// allocates and clears a bitmap of the cell's size and composes it into the tile, so one
        /// invocation can be sixteen million pixels where a form's is a parse. The coverage-guided
        /// fuzzer wrote a pattern whose cell fills with itself twice, a
        /// thousand points square at a one-point step: the lattice bounded its copies, the depth cap
        /// its nesting and <see cref="MaxFormXObjectInvocationsPerPage"/> its doubling at a hundred
        /// thousand — and every one of those invocations was a four-megabyte bitmap, so a two-kilobyte
        /// file rendered for minutes inside every ceiling. Measured: clearing and composing a pixel
        /// costs one to two nanoseconds, so this ceiling is a second or two of bitmap work. A tiling
        /// cell is a few hundred pixels square at 300 dpi and a soft-mask group a page, so a page of
        /// thousands of pattern fills, or of a hundred soft masks, stays inside it.</para>
        /// </remarks>
        public static long MaxOffscreenPixelsPerPage { get; set; } = DefaultMaxOffscreenPixelsPerPage;

        /// <summary>Built-in value of <see cref="MaxOffscreenPixelsPerPage"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxOffscreenPixelsPerPage = 1L << 30;

        /// <summary>
        /// Aggregate paint work one page may do in the raster renderer, in units of one device pixel
        /// of a solid fill: every fill, stroke, glyph, image and shading is charged the device area it
        /// covers inside the clip, times its kind's measured price (<c>RenderBudget</c>'s weights),
        /// whether it is drawn by the page's own stream or by nested content. 2³⁵ units: about
        /// fourteen seconds of painting. The SVG writer paints no pixels and never spends it.
        /// </summary>
        /// <remarks>
        /// <para>Every other render ceiling prices an operation as if it covered a few pixels, and a
        /// paint's cost is the area it covers. The page box chooses the bitmap up to
        /// <see cref="MaxImagePixelArea"/> whatever resolution the caller asks for, so a page a million
        /// points square renders to a hundred megapixels even as a thumbnail, and each page-sized
        /// fill then costs 42 ms on the shipped build. Two thousand such fills are 85 s from a 50 KB
        /// page; a form drawing itself with one fill reaches as many inside every form ceiling.
        /// Measured at that size, a pixel of a stroke costs 2.4 fill pixels, of a shading 15, of an
        /// image drawn with nearest sampling 13 and with <c>/Interpolate</c> (Mitchell) 88.</para>
        /// <para>The ceiling is absolute rather than a multiple of the page's area, so a thumbnail of
        /// a dense page is never refused; at 1200 dpi it still admits three page-sized interpolated
        /// images. Past it, the page's remaining paints are skipped and reported once.</para>
        /// </remarks>
        public static long MaxPaintWorkPerPage { get; set; } = DefaultMaxPaintWorkPerPage;

        /// <summary>Built-in value of <see cref="MaxPaintWorkPerPage"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxPaintWorkPerPage = 1L << 35;

        /// <summary>
        /// The most render diagnostics of one kind delivered for one page while its operators run;
        /// the last one delivered says the rest are suppressed. 100.
        /// </summary>
        /// <remarks>
        /// <para>A report site reached once an operator, a colour sample or a nested invocation
        /// reports as often as the file repeats it, and the form ceilings admit millions of each: a
        /// shading in a Separation space whose alternate is <c>/Pattern</c>, clipped to one unit and
        /// drawn a million times through three levels of forms, is 2 KB and evaluated 22 million
        /// colours, each a report. The structure fuzzer's render corpus held pages of 111 000 pattern
        /// reports and 25 000 image reports. Every one is a call into the host's handler — its
        /// logging, its telemetry — so the count is a cost the file chooses.</para>
        /// <para>The cap is per kind, so a page's distinct problems are each still reported; reports
        /// made outside a page's operators (opening the page, the page group) are not counted.</para>
        /// </remarks>
        public static int MaxRenderReportsPerKindPerPage { get; set; } = DefaultMaxRenderReportsPerKindPerPage;

        /// <summary>Built-in value of <see cref="MaxRenderReportsPerKindPerPage"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxRenderReportsPerKindPerPage = 100;

        /// <summary>
        /// Aggregate operations a page's nested content may run while rendering — form XObjects,
        /// pattern cells, soft-mask groups and Type 3 glyph procedures, each charged its parsed
        /// operation count as it runs, in both renderers; the page's own stream is not counted.
        /// Two million: a few seconds of the dearest operators per page.
        /// </summary>
        /// <remarks>
        /// <para><see cref="MaxFormContentBytesPerPage"/> prices an operation by its bytes, and an
        /// operation's cost is not its length. Measured on the shipped build, run inside a form: a fill or a stroke costs 1.2 to 1.8 microseconds an operation in either
        /// renderer, two to six bytes of content; a <c>q</c> in a fresh offscreen context clones the
        /// state, 330 bytes, for two. So the content ceiling alone lets a page run some ten million fills —
        /// eighteen seconds — and the coverage-guided fuzzer's tiling cell of 4 556 <c>q</c>
        /// operators, drawing itself, ran 39 million clones inside every other ceiling: 21 seconds
        /// and 12.9 GB allocated raster, 57 seconds and 24 GB as SVG, from 8 KB. A page of thousands
        /// of Type 3 glyphs or pattern fills runs tens or hundreds of thousands.</para>
        /// </remarks>
        public static long MaxFormOperationsPerPage { get; set; } = DefaultMaxFormOperationsPerPage;

        /// <summary>Built-in value of <see cref="MaxFormOperationsPerPage"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxFormOperationsPerPage = 2_000_000;

        /// <summary>
        /// Aggregate work the image decodes of one page may spend while rendering, in the codecs'
        /// own units — the JBIG2 composition budget's pixels, which is the one codec whose per-stream
        /// budget is sized by the stream's parameters rather than its bytes. Four streams' worth of
        /// <see cref="MaxJbig2CompositionPixels"/>: about a dozen seconds of decoding on one core.
        /// </summary>
        /// <remarks>
        /// <para>A JBIG2 stream of a few dozen bytes can spend its whole composition budget (a region
        /// declared 8192 pixels square, a symbol dictionary of millions of symbols) — three seconds
        /// on the shipped build after the generic region decoder's rewrite — and the budget is per
        /// stream, so a page of a hundred such images was five minutes inside every ceiling (the
        /// coverage-guided fuzzer's residual, GAPS.md §7c). Each stream's budget now also spends this
        /// one, through <see cref="Util.WorkBudget.Parent"/>, and the renderer stops decoding the
        /// page's images once it is spent, reporting once. Three 600-dpi A3 bilevel scans — 70 Mi
        /// pixels each decoded and composed — fit; the JPEG 2000 decoder's budget is sized by its
        /// codestream's bytes and is not counted here.</para>
        /// </remarks>
        public static long MaxImageDecodeWorkPerPage { get; set; } = DefaultMaxImageDecodeWorkPerPage;

        /// <summary>Built-in value of <see cref="MaxImageDecodeWorkPerPage"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxImageDecodeWorkPerPage = 4 * DefaultMaxJbig2CompositionPixels;

        /// <summary>
        /// Aggregate codec work one document may spend decoding the streams its syntax is read from —
        /// object streams, cross-reference streams and page content
        /// (<see cref="Document.PdfPage.GetContentStreamData"/>), over the document's whole life — in
        /// the units of <see cref="MaxImageDecodeWorkPerPage"/>. An eighth of one JBIG2 stream's
        /// composition budget: 16 Mi pixels, about half a second.
        /// </summary>
        /// <remarks>
        /// <para>These streams hold syntax. The parser decodes the first two at open, before any
        /// caller can decline, and on a damaged file once for every recovered header that reaches
        /// one and again in each recovery scan; every reader of a page decodes its content. A page
        /// whose <c>/Contents</c> named JBIG2-filtered streams spent a whole stream budget on each
        /// (the fuzzer's second campaign, 5 s at open). A filter whose work is counted here is one whose own budget
        /// is sized by the stream's parameters (JBIG2); Flate, the filter these streams are written
        /// with, charges nothing, so an ordinary document never spends this. The coverage-guided
        /// fuzzer's 1 108-byte file decoded one JBIG2-filtered object stream eight times, 27 s at
        /// open; under this ceiling the first decode is refused within it and the rest at their
        /// first charge. An object stream or cross-reference stream whose <c>/Filter</c> names an
        /// image codec (JBIG2, JPX, DCT, CCITT) is refused by name before any decode
        /// (<c>Parsing.SyntaxStreamFilters</c>) — samples are never syntax, and the ceiling was
        /// spent once for every open of such a document — so this bounds the page content such a
        /// stream may filter through a codec, and any codec a syntax stream may carry.</para>
        /// </remarks>
        public static long MaxSyntaxStreamDecodeWork { get; set; } = DefaultMaxSyntaxStreamDecodeWork;

        /// <summary>Built-in value of <see cref="MaxSyntaxStreamDecodeWork"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxSyntaxStreamDecodeWork = DefaultMaxJbig2CompositionPixels / 8;

        /// <summary>
        /// How many visible glyphs structured text extraction compares one invisible glyph with,
        /// when deciding whether that glyph copies visible text drawn at the same place.
        /// </summary>
        /// <remarks>
        /// <para>The check indexes visible glyphs in a grid of 16-point cells. A cell of ordinary
        /// text holds a handful of glyphs. Nothing stops a page from crowding thousands into one,
        /// and without a cap every invisible glyph landing there would be compared with all of
        /// them. The same cap applies to the glyphs too large for the grid, which are kept in a
        /// list of their own.</para>
        ///
        /// <para><b>Past the cap the invisible glyph is kept.</b> Stopping early can extract a
        /// character twice. It never loses one, and for search-driven redaction a lost character is
        /// the failure that leaks.</para>
        /// </remarks>
        public static int MaxInvisibleGlyphCoverCandidates { get; set; } = DefaultMaxInvisibleGlyphCoverCandidates;

        /// <summary>Built-in value of <see cref="MaxInvisibleGlyphCoverCandidates"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxInvisibleGlyphCoverCandidates = 256;

        // MaxColumnDetectionCellMarks lived here until the column detector stopped needing it.
        // It capped the O(chars x cells) coverage-grid FILL in PdfLayoutAnalyzer.DetectColumns —
        // 50 000 full-page glyphs cost ~148 s for one page, measured — and it bounded that by
        // truncating the analysis and reporting the truncation. The fill is now a 2-D difference
        // array plus one prefix-sum pass, which is O(chars + cells) for the identical coverage, so
        // there is nothing left to charge and no truncated answer to report. Removed rather than
        // left in place: an unreachable cap reads as a live bound to the next person tuning one.

        /// <summary>Maximum depth of a <c>/Resources</c> inheritance chain.</summary>
        public static int MaxResourceDepth { get; set; } = DefaultMaxResourceDepth;

        /// <summary>Built-in value of <see cref="MaxResourceDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxResourceDepth = 50;

        /// <summary>Maximum depth of an optional-content visibility expression (<c>/VE</c>).</summary>
        public static int MaxVisibilityExpressionDepth { get; set; } = DefaultMaxVisibilityExpressionDepth;

        /// <summary>Built-in value of <see cref="MaxVisibilityExpressionDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxVisibilityExpressionDepth = 50;

        /// <summary>Maximum size of the graphics-state (<c>q</c>/<c>Q</c>) stack.</summary>
        public static int MaxGraphicsStateStackDepth { get; set; } = DefaultMaxGraphicsStateStackDepth;

        /// <summary>Built-in value of <see cref="MaxGraphicsStateStackDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxGraphicsStateStackDepth = 4096;

        /// <summary>Maximum nesting of tables when laying out generated documents.</summary>
        public static int MaxTableNestingDepth { get; set; } = DefaultMaxTableNestingDepth;

        /// <summary>Built-in value of <see cref="MaxTableNestingDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxTableNestingDepth = 24;

        /// <summary>
        /// Maximum nesting of DrawingML group shapes (<c>wpg:wgp</c> / <c>a:grpSp</c> /
        /// <c>dsp:grpSp</c>) walked when converting a DOCX drawing. Word's own UI cannot produce
        /// anything close; a deeper tree is a crafted file.
        /// </summary>
        public static int MaxShapeTreeDepth { get; set; } = DefaultMaxShapeTreeDepth;

        /// <summary>Built-in value of <see cref="MaxShapeTreeDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxShapeTreeDepth = 16;

        /// <summary>
        /// Maximum number of shapes painted from one DrawingML drawing, counted across the whole
        /// tree. Depth alone does not bound a <b>wide</b> tree, and each shape emits its own block
        /// of content-stream operators — a crafted <c>diagrams/drawing1.xml</c> with hundreds of
        /// thousands of siblings would otherwise expand into hundreds of megabytes of output.
        /// Past the cap the remaining shapes are skipped and the count is reported.
        /// </summary>
        public static int MaxDrawingShapes { get; set; } = DefaultMaxDrawingShapes;

        /// <summary>Built-in value of <see cref="MaxDrawingShapes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxDrawingShapes = 5000;

        /// <summary>
        /// Maximum number of cells a DOCX table row lays out; the cells past it are dropped and reported.
        /// </summary>
        /// <remarks>
        /// A row taller than a page is drawn a page segment at a time, and each segment strokes the borders of
        /// every cell in the row: the output is the row's cells times the pages it spans, and the file chooses both
        /// — a row of a hundred thousand one-twip cells, one of them a hundred thousand lines deep, wrote some
        /// 55 GB of borders. Bounding the cells bounds the product to the pages. 63 is the most columns Word
        /// itself lets a table have, so no table Word made or shows is cut.
        /// <para>The PDF-to-DOCX converter (<c>Plugins.Docx</c>) writes a detected table wider than this as
        /// lines rather than a Word table: it pads each row to the table's columns, and the detector's
        /// columns times its rows is the file's to choose.</para>
        /// </remarks>
        public static int MaxDocxTableColumns { get; set; } = DefaultMaxDocxTableColumns;

        /// <summary>Built-in value of <see cref="MaxDocxTableColumns"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxDocxTableColumns = 63;

        /// <summary>
        /// Most columns a worksheet holds — SpreadsheetML's own figure, column <c>XFD</c> — which the PDF-to-XLSX
        /// converter (<c>Plugins.Docx</c>) writes a detected table wider than as lines of text and says so
        /// (<c>PdfToExcelConverter.LastError</c>): a cell past it is one a spreadsheet application repairs or
        /// refuses the workbook over, and the detector's columns are the file's to choose.
        /// </summary>
        public static int MaxSpreadsheetColumns { get; set; } = DefaultMaxSpreadsheetColumns;

        /// <summary>Built-in value of <see cref="MaxSpreadsheetColumns"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSpreadsheetColumns = 16_384;

        /// <summary>
        /// Most rows a worksheet holds — SpreadsheetML's own figure — past which the PDF-to-XLSX converter writes
        /// no more of a page and says so (<c>PdfToExcelConverter.LastError</c>).
        /// </summary>
        public static int MaxSpreadsheetRows { get; set; } = DefaultMaxSpreadsheetRows;

        /// <summary>Built-in value of <see cref="MaxSpreadsheetRows"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSpreadsheetRows = 1_048_576;

        /// <summary>
        /// Maximum hits one full-text search keeps (<c>PdfTextSearch.Search</c>); past it the search stops and
        /// says so (<c>PdfTextSearch.LastTruncation</c>).
        /// </summary>
        /// <remarks>
        /// Every hit is kept with its snippet and a copy of its match, and the hits are the page's to choose: a
        /// 143 KB file of 300 pages sharing one run of 100 000 <c>a</c>s answered a search for <c>a</c> with
        /// 30 000 000 hits — 26 GB allocated, a 7.8 GB working set, 42 s.
        /// </remarks>
        public static int MaxTextSearchHits { get; set; } = DefaultMaxTextSearchHits;

        /// <summary>Built-in value of <see cref="MaxTextSearchHits"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxTextSearchHits = 100_000;

        /// <summary>
        /// Maximum characters of context one full-text search writes across all its hits' snippets; past it a
        /// hit's snippet is its match alone, and the search says so (<c>PdfTextSearch.LastTruncation</c>).
        /// </summary>
        /// <remarks>
        /// A snippet is the caller's context on each side of a hit, so the hits times the context is what a
        /// search writes — a cap on the hits alone bounds neither factor's product with the other.
        /// </remarks>
        public static long MaxTextSearchContextChars { get; set; } = DefaultMaxTextSearchContextChars;

        /// <summary>Built-in value of <see cref="MaxTextSearchContextChars"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxTextSearchContextChars = 16L * 1024 * 1024;

        /// <summary>
        /// Maximum line-table cells one text comparison fills across all its pages
        /// (<c>PdfComparer.CompareText</c>): each page costs (left lines + 1) × (right lines + 1), and a
        /// page past what is left is compared line by line in order, the fallback said on
        /// <c>PdfComparer.LastApproximation</c>. A page's own cap bounds one page, not the page count.
        /// </summary>
        public static long MaxTextCompareCells { get; set; } = DefaultMaxTextCompareCells;

        /// <summary>Built-in value of <see cref="MaxTextCompareCells"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxTextCompareCells = 250_000_000L;

        /// <summary>
        /// Maximum colour stops carried into one shading. A gradient's stops become a stitching
        /// function with one sub-function per interval, so an unbounded list expands the written
        /// dictionary without limit. Far above any authored gradient; past it the extra stops are
        /// dropped and the truncation is reported.
        /// </summary>
        public static int MaxShadingStops { get; set; } = DefaultMaxShadingStops;

        /// <summary>Built-in value of <see cref="MaxShadingStops"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxShadingStops = 32;

        /// <summary>
        /// Maximum data points read from one chart series cache.
        ///
        /// <para><b>This bounds an allocation whose size the FILE chooses.</b> A chart cache
        /// declares <c>&lt;c:ptCount val="…"/&gt;</c> and each point carries an <c>idx</c>
        /// attribute; sizing an array from either lets a crafted DOCX ask for billions of elements
        /// while supplying no data at all. Excel's own per-series ceiling for a 2-D chart is 32,000,
        /// so this is generous for anything authored. Past the cap the extra points are dropped and
        /// the truncation is reported.</para>
        /// </summary>
        public static int MaxChartDataPoints { get; set; } = DefaultMaxChartDataPoints;

        /// <summary>Built-in value of <see cref="MaxChartDataPoints"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxChartDataPoints = 100_000;

        /// <summary>
        /// Maximum inflated size, in bytes, of one part of a DOCX package the converter will open.
        ///
        /// <para><b>This bounds an allocation whose size the FILE chooses.</b> A DOCX is a ZIP, and
        /// the Open XML SDK reads a part — <c>document.xml</c>, a style sheet, an image — into memory
        /// whole; a part deflated from gigabytes to kilobytes (a zip bomb) exhausts the process
        /// before a single paragraph is laid out. Every entry's declared inflated length is checked
        /// against this before the package is opened (ZIP enforces the declared length as the
        /// entry is read, so a lie fails the read rather than passing the check), and the two
        /// image readers stream their parts under the same figure. 256 MB is far past any authored
        /// document — a 300-page illustrated manual's largest part is tens of megabytes.</para>
        /// </summary>
        public static long MaxDocxPartBytes { get; set; } = DefaultMaxDocxPartBytes;

        /// <summary>Built-in value of <see cref="MaxDocxPartBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxDocxPartBytes = 256L * 1024 * 1024;

        /// <summary>
        /// The deepest an XML part of a DOCX may nest its elements before the document is refused unopened.
        /// <para>The Open XML SDK loads a part's elements by recursion, a call or two per level and about
        /// 700 bytes of stack each: content controls nested 800 deep — 1600 elements —
        /// overflowed a 1 MB stack, and 200 a 256 KB one, which no handler can catch. Every part is
        /// checked, by a streaming read, before the package is opened. Word's own documents nest a few
        /// dozen levels; tables nested to <see cref="MaxTableNestingDepth"/> with a paragraph in each are
        /// under a hundred. 256 is a sixth of what a 1 MB stack holds, leaving the converter's own walk
        /// room on top.</para>
        /// </summary>
        public static int MaxDocxXmlDepth { get; set; } = DefaultMaxDocxXmlDepth;

        /// <summary>Built-in value of <see cref="MaxDocxXmlDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxDocxXmlDepth = 256;

        /// <summary>
        /// The most device pixels one halftone cell may hold (ISO 32000-2 §10.6): a Type 1 screen's square at the
        /// render's resolution, or a threshold array's samples. A screen past it is not built and the device's default
        /// halftone serves instead, reported. A cell's size is the file's to choose — a frequency of 0.01 at 600 dpi is
        /// a cell of 3.6 billion pixels, each ranked — and a real screen is hundreds or a few thousand.
        /// </summary>
        public static int MaxHalftoneCellPixels { get; set; } = DefaultMaxHalftoneCellPixels;

        /// <summary>Built-in value of <see cref="MaxHalftoneCellPixels"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxHalftoneCellPixels = 1 << 20;

        /// <summary>
        /// Maximum data points plotted per chart series. Each becomes a segment of a path, so a
        /// crafted <c>c:numCache</c> of a million points would expand into a multi-megabyte content
        /// stream. Far above any readable chart — past it the series is decimated with a stride and
        /// the decimation is reported, never silently truncated.
        /// </summary>
        public static int MaxChartPointsPerSeries { get; set; } = DefaultMaxChartPointsPerSeries;

        /// <summary>Built-in value of <see cref="MaxChartPointsPerSeries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxChartPointsPerSeries = 20000;

        /// <summary>
        /// Maximum series read from one chart. Each series allocates its own point array up to
        /// <see cref="MaxChartDataPoints"/>, so the series count multiplies that per-series cap: twenty
        /// thousand empty series each declaring a hundred-thousand-point cache — under two megabytes of
        /// markup — would allocate tens of gigabytes before any decimation runs. Past this the remaining
        /// series are dropped and the drop reported.
        /// </summary>
        public static int MaxChartSeries { get; set; } = DefaultMaxChartSeries;

        /// <summary>Built-in value of <see cref="MaxChartSeries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxChartSeries = 1000;

        // ------------------------------------------------------------------
        //  Plugin-supplied content
        // ------------------------------------------------------------------
        //  Unlike the rest of this table these bound PLUGIN OUTPUT, not file input. A plugin runs
        //  in-process and is as trusted as the host that registered it, but its output is derived
        //  from an untrusted file (an embedded PostScript program), so a crafted input must not be
        //  able to steer a plugin into producing unbounded work for the renderer.

        /// <summary>
        /// Maximum length of a PDF content stream returned by an
        /// <c>IPostScriptPlugin</c>. Generous for a converted EPS page, which is authored artwork
        /// rather than a machine-amplified expansion; a plugin that needs more is emitting a
        /// rasterised image, which belongs in its returned <c>/Resources</c> where the image caps
        /// apply. Output over this is rejected and the built-in placeholder is drawn.
        /// </summary>
        public static int MaxPluginContentStreamBytes { get; set; } = DefaultMaxPluginContentStreamBytes;

        /// <summary>Built-in value of <see cref="MaxPluginContentStreamBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPluginContentStreamBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Maximum number of DIRECT entries in the resource dictionary an
        /// <c>IPostScriptPlugin</c> returns, counted across the whole graph. Indirect references
        /// are not followed — those belong to the host document's already-validated object graph.
        /// Depth is bounded separately by <see cref="MaxObjectGraphDepth"/>, which also makes the
        /// walk safe against a self-nesting direct dictionary.
        /// </summary>
        public static int MaxPluginResourceEntries { get; set; } = DefaultMaxPluginResourceEntries;

        /// <summary>Built-in value of <see cref="MaxPluginResourceEntries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPluginResourceEntries = 4096;

        // ------------------------------------------------------------------
        //  Fonts
        // ------------------------------------------------------------------

        /// <summary>Maximum depth of nested charstring subroutine calls (Type 1 and Type 2).</summary>
        public static int MaxCharstringCallDepth { get; set; } = DefaultMaxCharstringCallDepth;

        /// <summary>Built-in value of <see cref="MaxCharstringCallDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCharstringCallDepth = 10;

        /// <summary>
        /// Maximum font files parsed during an unnamed fallback scan for one that covers a set of
        /// code points (<c>Fonts.SystemFontResolver</c>). A font server can expose thousands of
        /// files and each candidate costs a read plus a parse, so the search has to be bounded —
        /// but note that a cap in raw directory order is a lottery, which is why the resolver ranks
        /// named-family candidates ahead of reaching this scan at all.
        /// </summary>
        public static int MaxScannedFontFiles { get; set; } = DefaultMaxScannedFontFiles;

        /// <summary>Built-in value of <see cref="MaxScannedFontFiles"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxScannedFontFiles = 256;

        /// <summary>
        /// Maximum entries retained in <c>Fonts.FontFileLocator</c>'s family → font-file cache.
        /// </summary>
        /// <remarks>
        /// The cache key is built from a PDF <c>/BaseFont</c> name, so the FILE chooses how many
        /// distinct keys exist. Without a cap a document declaring 100 000 font resources with
        /// distinct names left 100 000 entries alive for the process lifetime — a monotonic leak
        /// keyed on untrusted strings in a long-lived server. Far above the few dozen families any
        /// real document uses; past it, lookups still resolve correctly, just uncached.
        /// </remarks>
        public static int MaxCachedFontPathLookups { get; set; } = DefaultMaxCachedFontPathLookups;

        /// <summary>Built-in value of <see cref="MaxCachedFontPathLookups"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCachedFontPathLookups = 4096;

        /// <summary>
        /// Maximum size, in bytes, of one embedded font program re-emitted as a <c>data:</c> URI in
        /// an SVG export (<c>Rendering.SvgFontEmbedder</c>).
        /// </summary>
        /// <remarks>
        /// <b>The base64 expansion is the reason this is not just "whatever the stream holds".</b>
        /// A data URI costs 4/3 of the bytes plus the CSS around it, and the export builds the whole
        /// document in a <see cref="System.Text.StringBuilder"/> — as UTF-16, so a 10 MB CJK font
        /// becomes roughly 27 MB of live characters before a single glyph is drawn, and a document
        /// may declare many. Past the cap the run still renders: it falls back to naming a system
        /// family, which is what the export did for every font before embedding existed.
        /// </remarks>
        public static int MaxSvgEmbeddedFontBytes { get; set; } = DefaultMaxSvgEmbeddedFontBytes;

        /// <summary>Built-in value of <see cref="MaxSvgEmbeddedFontBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSvgEmbeddedFontBytes = 8 * 1024 * 1024;

        /// <summary>Maximum charstring interpreter steps, bounding a self-recursive subroutine.</summary>
        public static int MaxCharstringSteps { get; set; } = DefaultMaxCharstringSteps;

        /// <summary>Built-in value of <see cref="MaxCharstringSteps"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCharstringSteps = 5_000_000;

        /// <summary>
        /// Maximum charstring steps one page's rendering spends across all the glyphs it evaluates —
        /// both renderers, Type 1 and Type 2 alike; a glyph past it draws nothing, reported once a page
        /// (<c>RenderDiagnosticKind.GlyphOutlineWorkExceeded</c>). A step costs about 7 ns on the shipped
        /// build, so this is about 0.7 s; a real page's distinct glyphs run a few thousand steps each.
        /// </summary>
        public static long MaxCharstringStepsPerPage { get; set; } = DefaultMaxCharstringStepsPerPage;

        /// <summary>Built-in value of <see cref="MaxCharstringStepsPerPage"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCharstringStepsPerPage = 100_000_000L;

        /// <summary>
        /// Maximum charstring steps the CFF width and seac scans spend over one font program — the
        /// whole-font passes (a width table, the subsetter) that run outside any page. A real glyph's
        /// width is read in a handful of steps, so this is about 0.35 s of a font that runs every glyph
        /// to <see cref="MaxCharstringSteps"/>; past it a glyph's width is unread and its seac
        /// components uncollected, as for a charstring the scan refuses.
        /// </summary>
        public static long MaxCharstringStepsPerFont { get; set; } = DefaultMaxCharstringStepsPerFont;

        /// <summary>Built-in value of <see cref="MaxCharstringStepsPerFont"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCharstringStepsPerFont = 50_000_000L;

        /// <summary>
        /// Maximum charstring steps a glyph of an embedded CFF program may run, by the library's own
        /// evaluator, for the program still to be drawn through Skia (wrapped as OpenType, for its
        /// hinting); past it the font is drawn from the library's outlines
        /// (<c>RenderDiagnosticKind.FontHintingSkipped</c>). Skia runs charstrings under no budget of the
        /// library's — a subroutine bomb cost it about 300 ms a glyph. A real glyph runs a few thousand
        /// steps.
        /// </summary>
        public static int MaxCharstringStepsForHinting { get; set; } = DefaultMaxCharstringStepsForHinting;

        /// <summary>Built-in value of <see cref="MaxCharstringStepsForHinting"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCharstringStepsForHinting = 100_000;

        /// <summary>
        /// Maximum nesting of TrueType composite glyphs read for an outline (<c>Fonts.TrueTypeOutlines</c>):
        /// a composite naming a composite, and so on. The program chooses the chain, so without it a
        /// glyph naming itself recursed until the stack overflowed.
        /// </summary>
        public static int MaxCompositeGlyphDepth { get; set; } = DefaultMaxCompositeGlyphDepth;

        /// <summary>Built-in value of <see cref="MaxCompositeGlyphDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCompositeGlyphDepth = 16;

        /// <summary>
        /// Maximum points one TrueType glyph's outline expands to (<c>Fonts.TrueTypeOutlines</c>), and
        /// the most glyphs it reads to get them. A composite may name the same component many times at
        /// every level, so the count multiplies with the nesting; this bounds the product, which a cap
        /// on either factor does not — the visits as well, since a component that is an empty glyph
        /// places no point.
        /// </summary>
        public static int MaxGlyphOutlinePoints { get; set; } = DefaultMaxGlyphOutlinePoints;

        /// <summary>Built-in value of <see cref="MaxGlyphOutlinePoints"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxGlyphOutlinePoints = 1 << 17;

        /// <summary>Maximum depth of CMap <c>usecmap</c> chaining.</summary>
        public static int MaxCMapUsecmapDepth { get; set; } = DefaultMaxCMapUsecmapDepth;

        /// <summary>Built-in value of <see cref="MaxCMapUsecmapDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCMapUsecmapDepth = 16;

        /// <summary>
        /// Maximum span of a single <c>cidrange</c> line. A 4-byte codespace lets a malformed
        /// CMap declare a range covering the whole 32-bit space; the ranges are stored
        /// unexpanded, but this keeps an absurd declaration from being treated as meaningful.
        /// </summary>
        public static int MaxCMapRangeSpan { get; set; } = DefaultMaxCMapRangeSpan;

        /// <summary>Built-in value of <see cref="MaxCMapRangeSpan"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCMapRangeSpan = 0x10FFFF;

        /// <summary>
        /// Aggregate budget, in mapping writes, for one <c>CMapParser.Parse</c> call and every
        /// <c>usecmap</c> it chains into. About 100× the largest Adobe predefined CMap.
        /// </summary>
        /// <remarks>
        /// The 0x10000 clamp on a single <c>bfrange</c>/<c>cidrange</c> line bounds one line; the
        /// NUMBER of lines is bounded only by the decoded stream length, and the two multiply.
        /// A <c>/ToUnicode</c> holding one <c>beginbfrange</c> section of 50&#160;000 identical
        /// <c>&lt;0000&gt; &lt;FFFF&gt; &lt;0041&gt;</c> lines is ~1&#160;MB of extremely repetitive
        /// text — a couple of kilobytes once Flate-compressed — and expands to ~3.3×10<sup>9</sup>
        /// iterations, each allocating a string. The dictionary stays small because the keys
        /// collide, so no <c>Count</c>-based guard can fire; only a tick-style budget sees it.
        /// </remarks>
        public static long MaxCMapMappingWrites { get; set; } = DefaultMaxCMapMappingWrites;

        /// <summary>Built-in value of <see cref="MaxCMapMappingWrites"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCMapMappingWrites = 8L * 1024 * 1024;

        /// <summary>
        /// Aggregate budget, in dictionary entries produced, for parsing one CIDFont <c>/W</c>
        /// array (§9.7.4.3). Sized like <see cref="MaxCMapMappingWrites"/> and for the same
        /// reason — a legitimate <c>/W</c> covers at most the 64K CID space many times over,
        /// so 8M entries is far above any real font and far below an amplification.
        /// </summary>
        /// <remarks>
        /// The per-entry clamp (a single <c>c1 c2 w</c> span is capped at 0x10000 CIDs) bounds one
        /// entry; the NUMBER of entries is bounded only by the array length, and the two multiply.
        /// A <c>/W</c> of 100&#160;000 <c>[0 65536 1000]</c> triples — a few hundred kilobytes of
        /// source, a couple of kilobytes Flate-compressed — asks for 6.5×10<sup>9</sup> dictionary
        /// writes. The dictionary itself stays small because the keys collide, so no
        /// <c>Count</c>-based guard can fire; only a tick-style budget sees it.
        /// </remarks>
        public static long MaxCidWidthEntries { get; set; } = DefaultMaxCidWidthEntries;

        /// <summary>Built-in value of <see cref="MaxCidWidthEntries"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCidWidthEntries = 8L * 1024 * 1024;

        // ------------------------------------------------------------------
        //  Images and decompression
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum width or height, in pixels, of a decoded image or rendered page. Bounds a
        /// declared-but-absurd <c>/Width</c> or <c>/Height</c> before any allocation.
        /// </summary>
        public static int MaxImageDimension { get; set; } = DefaultMaxImageDimension;

        /// <summary>Built-in value of <see cref="MaxImageDimension"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxImageDimension = 16384;

        /// <summary>
        /// Maximum total pixel count of a decoded image or rendered page. Necessary in addition to
        /// <see cref="MaxImageDimension"/>, since two individually-legal dimensions still multiply
        /// to an illegal area.
        /// </summary>
        public static long MaxImagePixelArea { get; set; } = DefaultMaxImagePixelArea;

        /// <summary>Built-in value of <see cref="MaxImagePixelArea"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxImagePixelArea = 100_000_000L;

        // ------------------------------------------------------------------
        //  CCITT Group 3/4 (T.4 / T.6)
        // ------------------------------------------------------------------
        // One set of numbers for the read side and the write side, so the two cannot disagree. The
        // encoder's public constants are forwarders, so a caller that wants to skip an oversized image rather than catch an
        // exception still has them.
        //
        // They are deliberately NOT the same question as MaxImageDimension / MaxImagePixelArea
        // above, which bound a decoded raster or a rendered page. These bound a CCITT decode's own
        // amplification, which is why the row cap exists at all.

        /// <summary>
        /// Largest <c>/Columns</c> a CCITT stream may declare. The decoder allocates a
        /// <c>byte[columns]</c> per scanline — reference and current — so an adversarial value near
        /// <see cref="int.MaxValue"/> drives huge per-line allocations before a single row decodes.
        /// </summary>
        public static int MaxCcittColumns { get; set; } = DefaultMaxCcittColumns;

        /// <summary>Built-in value of <see cref="MaxCcittColumns"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCcittColumns = 1_000_000;

        /// <summary>
        /// Largest row count a CCITT decode may produce.
        /// </summary>
        /// <remarks>
        /// <b><see cref="MaxCcittPixels"/> alone is not enough.</b> The decoder allocates one
        /// <c>byte[columns]</c> heap <i>object</i> per row, so with <c>/Columns 1</c> the pixel
        /// budget would permit ~64M rows — ~64M tiny objects, multi-gigabyte object overhead and a
        /// GC stall — from a few megabytes of all-V0 input. Capping the row count bounds the object
        /// count whatever columns is. 200 000 is far above any real page (A4 at 200 dpi ≈ 2 200
        /// rows; a 100-inch banner ≈ 20 000).
        /// </remarks>
        public static int MaxCcittRows { get; set; } = DefaultMaxCcittRows;

        /// <summary>Built-in value of <see cref="MaxCcittRows"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCcittRows = 200_000;

        /// <summary>
        /// Total decoded-pixel budget for a CCITT stream. A crafted G4 stream of all-white rows
        /// expands roughly one input bit into a whole scanline, so without a cap a few kilobytes
        /// balloon into tens of gigabytes.
        /// </summary>
        public static long MaxCcittPixels { get; set; } = DefaultMaxCcittPixels;

        /// <summary>Built-in value of <see cref="MaxCcittPixels"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCcittPixels = 64L * 1024 * 1024;

        /// <summary>Maximum width or height of a shading or mesh bitmap.</summary>
        public static int MaxShadingDimension { get; set; } = DefaultMaxShadingDimension;

        /// <summary>Built-in value of <see cref="MaxShadingDimension"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxShadingDimension = 4096;

        /// <summary>
        /// The most spot-ink plates one page's separations render beside the four process plates: each is a
        /// whole render of the page and a page-sized plate kept in the result, and a page names as many inks
        /// as it holds colour spaces, a few dozen bytes each. The inks past it are reported, not rendered.
        /// </summary>
        public static int MaxSpotPlatesPerPage { get; set; } = DefaultMaxSpotPlatesPerPage;

        /// <summary>Built-in value of <see cref="MaxSpotPlatesPerPage"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSpotPlatesPerPage = 64;

        /// <summary>
        /// Most image pixels one Builder export decodes, every picture together — the Builder's own documents and
        /// the HTML and DOCX converters that export through it. Each picture is decoded once an export and held to
        /// the authored-image size, and neither bounds how many there are: a page of distinct pictures of a few
        /// kilobytes each, every one declaring forty megapixels, decodes and compresses each one whole. Past this the
        /// pictures not yet decoded are not drawn, their alt text standing in, and the cut is reported once.
        /// </summary>
        /// <remarks>
        /// Measured on the shipped build at about 5 ns a pixel, decoded and compressed (a 16-megapixel GIF in
        /// 80 ms): the default is some ten seconds of decoding, five hundred twelve-megapixel photographs.
        /// </remarks>
        public static long MaxImagePixelsPerExport { get; set; } = DefaultMaxImagePixelsPerExport;

        /// <summary>Built-in value of <see cref="MaxImagePixelsPerExport"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxImagePixelsPerExport = 2_000_000_000;

        /// <summary>Maximum vertex count in a mesh shading.</summary>
        public static int MaxMeshVertices { get; set; } = DefaultMaxMeshVertices;

        /// <summary>Built-in value of <see cref="MaxMeshVertices"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxMeshVertices = 4_000_000;

        /// <summary>
        /// Maximum triangles accumulated into one <c>DrawVertices</c> call when rasterising a
        /// type&#160;4 or type&#160;5 mesh shading. Past this the accumulated batch is drawn and a
        /// fresh one started; nothing is discarded.
        /// </summary>
        /// <remarks>
        /// <para>This exists because batching is itself a DoS surface. Issued one draw call at a
        /// time, exactly three points and three colours are live; accumulating the whole mesh
        /// makes the peak allocation a function of the
        /// <em>file's</em> vertex count. <see cref="MaxMeshVertices"/> is 4&#160;000&#160;000, and a
        /// legal type&#160;5 lattice of that size is ~8&#160;M triangles — 24&#160;M
        /// <c>SKPoint</c> (192&#160;MB) plus 24&#160;M <c>SKColor</c> (96&#160;MB) held at once.
        /// One batch of this size is ~1.5&#160;MB + 768&#160;KB.</para>
        /// <para>Deliberately far above any plausible mesh: a batch boundary is the one place where
        /// two triangles sharing an edge are composited as separate draws, so lowering it to a
        /// "tidier" number would trade a bounded allocation for visible seams in real files.</para>
        /// </remarks>
        public static int MaxMeshTrianglesPerBatch { get; set; } = DefaultMaxMeshTrianglesPerBatch;

        /// <summary>Built-in value of <see cref="MaxMeshTrianglesPerBatch"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxMeshTrianglesPerBatch = 65_536;

        /// <summary>
        /// Aggregate budget, in composited pixels, for one JBIG2 <c>Decode</c> call — every
        /// <c>DrawAt</c>, generic-region and refinement-region pixel across every segment.
        /// Twice the 64&#160;Mi cap on a single JBIG2 bitmap: room for a maximal full-page generic
        /// region plus another full-page composition on top of it, and far above any real scan
        /// (600&#160;dpi A4 is ~35&#160;M pixels).
        /// </summary>
        /// <remarks>
        /// The per-bitmap dimension check bounds one <b>allocation</b>, not the composition.
        /// A pattern dictionary of 255×255 cells (65&#160;025&#160;px, legal) followed by a halftone
        /// region with an 8000×8000 grid (64&#160;Mi cells, exactly at the cap, also legal) composes
        /// ~4.2×10<sup>12</sup> pixel operations from about a hundred bytes of segment bodies — and
        /// the MQ decoder pads past end-of-data with 0xFF rather than reporting EOF, so short input
        /// does not stop it. The text-region path amplifies identically: instance count × symbol
        /// area. Only a budget spanning the whole decode sees either.
        /// </remarks>
        public static long MaxJbig2CompositionPixels { get; set; } = DefaultMaxJbig2CompositionPixels;

        /// <summary>Built-in value of <see cref="MaxJbig2CompositionPixels"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxJbig2CompositionPixels = 2L * 64 * 1024 * 1024;

        /// <summary>
        /// Maximum number of tiles a JPEG 2000 codestream may declare. The tile count comes from the
        /// SIZ marker segment's grid and tile size, so <c>XTsiz</c> = <c>YTsiz</c> = 1 on a large grid
        /// declares one tile per pixel in 40 bytes, each with its own header state and packets.
        /// </summary>
        /// <remarks>
        /// The default is the most a codestream can use anyway: a tile-part names its tile in a 16-bit
        /// <c>Isot</c> of 0 to 65 534 (T.800 Table A.5), so a grid of more tiles than that leaves some
        /// unaddressable. The ceiling is therefore settable downwards only in practice.
        /// </remarks>
        public static int MaxJpxTiles { get; set; } = DefaultMaxJpxTiles;

        /// <summary>Built-in value of <see cref="MaxJpxTiles"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxJpxTiles = 65535;

        /// <summary>
        /// The code-blocks and precinct-bands one JPEG 2000 tile may hold — the objects the packet
        /// decoder allocates, a tag tree per precinct-band, before it reads a packet. A header can
        /// declare 2x2 precincts at every resolution level, or 4x4 code-blocks: one object per few
        /// samples of a tile whose area is otherwise legal.
        /// </summary>
        public static int MaxJpxCodeBlocks { get; set; } = DefaultMaxJpxCodeBlocks;

        /// <summary>Built-in value of <see cref="MaxJpxCodeBlocks"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxJpxCodeBlocks = 4_194_304;

        /// <summary>
        /// The work one JPEG 2000 decode may do, in <see cref="Util.WorkBudget"/> units: one per
        /// packet, one per code-block contribution, one per coding pass, one per grid position a
        /// position-driven progression visits. Layers (up to 65 535) times resolution levels,
        /// components and precincts is the packet count a few header bytes declare, each packet
        /// possibly one bit long.
        /// </summary>
        public static long MaxJpxDecodeWork { get; set; } = DefaultMaxJpxDecodeWork;

        /// <summary>Built-in value of <see cref="MaxJpxDecodeWork"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxJpxDecodeWork = 100_000_000L;

        /// <summary>
        /// The work one JPEG 2000 encode may do, in the decode's units (<see cref="MaxJpxDecodeWork"/>):
        /// a coding pass over a code-block, a packet written, a layer judged. The encoder's own
        /// ceiling, named by its message. Default the decode's.
        /// </summary>
        public static long MaxJpxEncodeWork { get; set; } = DefaultMaxJpxEncodeWork;

        /// <summary>Built-in value of <see cref="MaxJpxEncodeWork"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxJpxEncodeWork = DefaultMaxJpxDecodeWork;

        /// <summary>
        /// Work units (see <see cref="MaxJpxDecodeWork"/>) a JPEG 2000 decode may spend per byte of
        /// its codestream, above a floor the decoder keeps for small files. A hostile file is small
        /// and declares much: a block of a few bytes with 88 coding passes over 4 096 samples, a
        /// progression order restated in every tile-part. A legitimate file's work is a fraction of
        /// a unit per byte (a 5-3 lossless 8-bit image ≈ 0.01, a heavily compressed lossy one ≈ 0.3),
        /// so 16 leaves fifty times the headroom while a declaration of work far past its bytes is
        /// refused with the reason. Default 16.
        /// </summary>
        public static long MaxJpxDecodeWorkPerByte { get; set; } = DefaultMaxJpxDecodeWorkPerByte;

        /// <summary>Built-in value of <see cref="MaxJpxDecodeWorkPerByte"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxJpxDecodeWorkPerByte = 16;

        /// <summary>
        /// How many code-block problems one JPEG 2000 decode reports by text in its deviations before
        /// counting the rest: a crafted codestream of 65 535 tiles can have a problem in every block.
        /// Default 64.
        /// </summary>
        public static int MaxJpxReportedProblems { get; set; } = DefaultMaxJpxReportedProblems;

        /// <summary>Built-in value of <see cref="MaxJpxReportedProblems"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxJpxReportedProblems = 64;

        /// <summary>
        /// The memory one JPEG 2000 decode may hold at its peak, in bytes: the samples it will place —
        /// the image area at the resolution decoded, times the planes — at the codec's measured price
        /// of <see cref="Imaging.Jpx.JpxDecodedImage.WorkingBytesPerSample"/> each, judged after the
        /// tile headers are read and before any tile's samples are allocated. Default 5 GiB (the
        /// owner's, for print hosts): a 600-dpi A3 CMYK scan — 7 016 × 9 933 × 4, 280 million
        /// samples, 4.8 GB at the peak — decodes; a 100-million-pixel image with more than three
        /// components does not. A 312-byte file declaring 9 000 × 9 000 × 3 took 4.1 GB
        /// inside every other ceiling — <see cref="MaxImagePixelArea"/> counts pixels, not samples, and
        /// <see cref="MaxDecompressedBytes"/> the 8-bit output, a seventeenth of the peak.
        /// </summary>
        public static long MaxJpxWorkingSetBytes { get; set; } = DefaultMaxJpxWorkingSetBytes;

        /// <summary>Built-in value of <see cref="MaxJpxWorkingSetBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxJpxWorkingSetBytes = 5L * 1024 * 1024 * 1024;

        /// <summary>Maximum output of a single decompression filter stage.</summary>
        public static long MaxDecompressedBytes { get; set; } = DefaultMaxDecompressedBytes;

        /// <summary>Built-in value of <see cref="MaxDecompressedBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxDecompressedBytes = 512L * 1024 * 1024;

        /// <summary>
        /// The most a CMap stream — a font's <c>/ToUnicode</c>, an embedded <c>/Encoding</c> — decodes
        /// to (<c>Core.Fonts.FontStreamBytes.CMap</c>). Default 16 MiB: a CMap mapping every code of a
        /// two-byte font one by one is about a megabyte, and Adobe's largest predefined CMaps a few
        /// hundred kilobytes. Under the general <see cref="MaxDecompressedBytes"/> a CMap bomb of half
        /// a megabyte costs some 3 GB in passing — its bytes, then its text.
        /// </summary>
        public static long MaxCMapStreamBytes { get; set; } = DefaultMaxCMapStreamBytes;

        /// <summary>Built-in value of <see cref="MaxCMapStreamBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCMapStreamBytes = 16L * 1024 * 1024;

        /// <summary>
        /// The most an embedded font program (<c>/FontFile</c>, <c>/FontFile2</c>, <c>/FontFile3</c>)
        /// decodes to (<c>Core.Fonts.FontStreamBytes.Program</c>). Default 64 MiB: the largest
        /// complete CJK and pan-Unicode faces a producer embeds run to 20–30 MB, and a font program is
        /// read by every reader of the font — the model, the renderers, the checkers.
        /// </summary>
        public static long MaxFontProgramBytes { get; set; } = DefaultMaxFontProgramBytes;

        /// <summary>Built-in value of <see cref="MaxFontProgramBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxFontProgramBytes = 64L * 1024 * 1024;

        /// <summary>
        /// The decoded bytes one parsed document keeps memoized across all its streams. A stream
        /// caches its decoded data for the document's life, so a whole-document pass — rendering every
        /// page, extracting all text, a validator — held the sum of every stream's decoded content,
        /// not the largest. Past this ceiling a stream decodes without caching, so the retained total
        /// is bounded while decodes under it stay cached. Generous by default: a real document stays
        /// fully cached and byte-for-byte unchanged, and only one that retains this much decoded
        /// content begins to re-decode on a repeated read.
        /// </summary>
        public static long MaxMemoizedDecodedBytesPerDocument { get; set; } = DefaultMaxMemoizedDecodedBytesPerDocument;

        /// <summary>Built-in value of <see cref="MaxMemoizedDecodedBytesPerDocument"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxMemoizedDecodedBytesPerDocument = 256L * 1024 * 1024;

        /// <summary>
        /// Maximum number of <c>/CIDToGIDMap</c> stream bytes consulted: 2&#160;bytes × 64&#160;K CIDs,
        /// the entire space addressable by a 2-byte CID.
        /// </summary>
        /// <remarks>
        /// The stream is untrusted and compressed, so a small PDF can decompress to hundreds of
        /// megabytes here. Without this bound every non-zero 2-byte pair became a
        /// <c>Dictionary&lt;int,int&gt;</c> entry, and the dictionary — roughly 40 bytes per entry,
        /// not the byte array — was what exhausted memory. Bytes past this point map CIDs that a
        /// 2-byte CID can never reference, so nothing usable is discarded.
        /// </remarks>
        public static int MaxCidToGidMapBytes { get; set; } = DefaultMaxCidToGidMapBytes;

        /// <summary>Built-in value of <see cref="MaxCidToGidMapBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCidToGidMapBytes = 0x20000;

        /// <summary>
        /// Maximum cumulative output across every stage of a filter chain. Per-stage caps do not
        /// compose: a 16-stage chain each capped at 512&#160;MB could otherwise allocate 8&#160;GB.
        /// </summary>
        public static long MaxCumulativeOutputBytes { get; set; } = DefaultMaxCumulativeOutputBytes;

        /// <summary>Built-in value of <see cref="MaxCumulativeOutputBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCumulativeOutputBytes = 1024L * 1024 * 1024;

        /// <summary>Maximum number of filters in a single <c>/Filter</c> chain.</summary>
        public static int MaxFilterChainLength { get; set; } = DefaultMaxFilterChainLength;

        /// <summary>Built-in value of <see cref="MaxFilterChainLength"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFilterChainLength = 16;

        /// <summary>
        /// Maximum <c>/N</c> (contained-object count) accepted from an object stream. The index is
        /// read entry by entry, so an advertised count of billions would allocate and loop long
        /// before the stream ran out of bytes. A real file does approach 65&#160;536: a public corpus's
        /// <c>bug1978317</c> keeps 65&#160;542 objects, the catalog among them, in one object stream;
        /// the default is twice that.
        /// </summary>
        /// <remarks>
        /// <b>This bound alone is not enough, and the reason is the whole point of
        /// <see cref="MinObjStmIndexBytesPerEntry"/>.</b> A cap on the count is a cap on the
        /// <i>ratio</i> only when the file has to pay for what it declares. Declaring
        /// <c>/N 65536</c> over a three-object body costs ~67 file bytes and buys a 524&#160;288-byte
        /// entry array — a ~7&#160;800x amplification that sits entirely inside this limit.
        /// </remarks>
        public static int MaxObjStmEntries { get; set; } = DefaultMaxObjStmEntries;

        /// <summary>Built-in value of <see cref="MaxObjStmEntries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxObjStmEntries = 131072;

        /// <summary>
        /// The smallest number of decoded bytes one object-stream index entry can occupy, used to
        /// reject a <c>/N</c> the stream body cannot possibly contain.
        /// </summary>
        /// <remarks>
        /// An entry is two integers separated by whitespace (§7.5.7), so the shortest legal form is
        /// <c>"0 0 "</c> — four bytes. Checking <c>4 x N</c> against the decoded length makes the
        /// declared count answerable by the file's own size instead of by a constant, which is what
        /// removes the amplification: an attacker must now supply the bytes it is charged for.
        /// </remarks>
        public static int MinObjStmIndexBytesPerEntry { get; set; } = DefaultMinObjStmIndexBytesPerEntry;

        /// <summary>Built-in value of <see cref="MinObjStmIndexBytesPerEntry"/>; for a compile-time constant context.</summary>
        public const int DefaultMinObjStmIndexBytesPerEntry = 4;

        /// <summary>
        /// Maximum number of cross-reference entries admitted while opening ONE document, counted
        /// across every section of the <c>/Prev</c> and <c>/XRefStm</c> walk.
        /// </summary>
        /// <remarks>
        /// <b>Per document, not per section — and that distinction is the whole limit.</b> With the
        /// minimum legal <c>/W</c> entry size of 1 byte a small, highly-compressible
        /// cross-reference stream decodes toward <see cref="MaxDecompressedBytes"/> and mints one
        /// <c>CrossReferenceEntry</c> per byte. Bounding a single section leaves the chain free to
        /// multiply it: each section gets a fresh budget, and <c>/Index [start count]</c> lets each
        /// claim a disjoint object-number range, so the first-wins table cannot deduplicate them.
        /// Measured with a budget per section: 4 290 bytes of file produce 1 200 000 entries and
        /// 97 MB of heap.
        /// </remarks>
        public static int MaxXrefEntries { get; set; } = DefaultMaxXrefEntries;

        /// <summary>Built-in value of <see cref="MaxXrefEntries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxXrefEntries = 16_000_000;

        /// <summary>
        /// Maximum memory retained across all cached object-stream readers, in bytes: each reader's
        /// index array, sized from a file-chosen <c>/N</c>, and its decoded body when the stream did
        /// not keep that body itself.
        /// </summary>
        /// <remarks>
        /// Under <see cref="MaxMemoizedDecodedBytesPerDocument"/> the body is aliased from the
        /// <c>PdfStream</c>'s own memo and costs the reader nothing new; past that ceiling the stream
        /// decodes without caching and the reader is the body's only owner, so the body is counted
        /// here. The reader cache was unbounded in count, so a document made of many small object
        /// streams retained one array per stream with nothing to stop it.
        /// </remarks>
        public static long MaxObjStmIndexCacheBytes { get; set; } = DefaultMaxObjStmIndexCacheBytes;

        /// <summary>Built-in value of <see cref="MaxObjStmIndexCacheBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxObjStmIndexCacheBytes = 8L * 1024 * 1024;

        /// <summary>
        /// Maximum number of object-stream index entries ONE parser reads over its lifetime, counted
        /// on every index read — the first and each re-read after an eviction.
        /// </summary>
        /// <remarks>
        /// <see cref="MaxObjStmIndexCacheBytes"/> bounds what is retained, not what is done: members
        /// of nine streams of <see cref="MaxObjStmEntries"/> entries each, taken round-robin under a
        /// cache that holds eight, miss every time, and each miss inflates and reads one whole index
        /// again — one index read per member, bounded only by the cross-reference table's
        /// <see cref="MaxXrefEntries"/>. A legitimate document reads each index once, so its total is
        /// its compressed object count; the default is four times the largest table admitted.
        /// </remarks>
        public static long MaxObjStmIndexEntriesRead { get; set; } = DefaultMaxObjStmIndexEntriesRead;

        /// <summary>Built-in value of <see cref="MaxObjStmIndexEntriesRead"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxObjStmIndexEntriesRead = 4L * DefaultMaxXrefEntries;

        /// <summary>
        /// How many bytes of a damaged file the cross-reference recovery scan holds at a time while it
        /// looks for <c>N G obj</c> headers to rebuild the table from: the window. The whole file is
        /// scanned, one window after another.
        /// </summary>
        /// <remarks>
        /// Recovery materialises a window twice — the bytes plus an ASCII transcription of the last
        /// one, for the trailer and catalog searches — so an unbounded read of a multi-gigabyte file
        /// is a trivial memory-exhaustion vector; the work stays linear in the file. Every window is read: reading only the first, a newer
        /// copy of an object appended past it would lose to an older one inside it, and the trailer at
        /// the end of such a file would never be found.
        /// </remarks>
        public static long MaxXrefRecoveryScanBytes { get; set; } = DefaultMaxXrefRecoveryScanBytes;

        /// <summary>Built-in value of <see cref="MaxXrefRecoveryScanBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxXrefRecoveryScanBytes = 64L * 1024 * 1024;

        /// <summary>
        /// How many <c>trailer</c> keywords the cross-reference recovery scan reads a dictionary
        /// after, newest first, when it rebuilds a damaged file's trailer.
        /// </summary>
        /// <remarks>
        /// Each keyword costs a dictionary parse, and a dictionary that never closes is parsed to the
        /// end of the scanned region — so a hostile file holding the seven-byte keyword every seven
        /// bytes would buy a quadratic amount of work from a linear amount of file. §7.5.6 has each
        /// added trailer carry every entry of the previous one, so a conforming file needs only the
        /// last; the earlier ones serve a file whose last trailer is cut off or incomplete, and a
        /// handful of revisions back is as far as that reaches.
        /// </remarks>
        public static int MaxRecoveredTrailerDictionaries { get; set; } = DefaultMaxRecoveredTrailerDictionaries;

        /// <summary>Built-in value of <see cref="MaxRecoveredTrailerDictionaries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxRecoveredTrailerDictionaries = 16;

        /// <summary>
        /// ISO 32000-2 Annex F.3.3: "The linearization parameter dictionary shall be entirely
        /// contained within the first 1024 bytes of the PDF file. This limits the amount of data a
        /// PDF processor will have to read before deciding whether the file is linearized." The
        /// bound the first-page-terminator recognition in <c>Base.Operations.Documents.PdfRevisions</c>
        /// reads under.
        /// </summary>
        /// <remarks>
        /// The same number as the <c>%PDF-</c> header search window, and deliberately not the same
        /// constant: that window is this library's tolerance for bytes before the header, this is the
        /// specification's placement rule for a dictionary, and either could change without the other.
        /// </remarks>
        public static int LinearizationDictionaryWindowBytes { get; set; } = DefaultLinearizationDictionaryWindowBytes;

        /// <summary>Built-in value of <see cref="LinearizationDictionaryWindowBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultLinearizationDictionaryWindowBytes = 1024;

        /// <summary>
        /// The widest field the Annex F hint tables may declare, in bits.
        /// </summary>
        /// <remarks>
        /// <para><b>The file chooses these widths, and it chooses them in 16 bits.</b> Tables F.3
        /// and F.5 give each item group a bit width read from the stream, so a hostile file can ask
        /// for a 65 535-bit field per item and multiply every per-item loop by four thousand. None
        /// of the values those fields carry is wider than a 32-bit integer — object counts, lengths
        /// and offsets — so a declared width above 32 describes nothing the format can hold.</para>
        /// <para>Refusing is right rather than clamping: a width this reader cannot honour means the
        /// hints are not the hints the producer wrote, and hints that are silently wrong are worse
        /// than no hints at all.</para>
        /// </remarks>
        public static int MaxLinearizationHintFieldBits { get; set; } = DefaultMaxLinearizationHintFieldBits;

        /// <summary>Built-in value of <see cref="MaxLinearizationHintFieldBits"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxLinearizationHintFieldBits = 32;

        /// <summary>
        /// Total per-page shared-object references an Annex F page offset table may declare.
        /// </summary>
        /// <remarks>
        /// Table F.3's per-page "number of shared objects" is itself a file-chosen field, so the
        /// nested loop that reads the identifiers is <c>pages × count</c> — two attacker-chosen
        /// numbers multiplied, out of a stream that need not be that large once deflated. This is
        /// the bound on their product, in the shape <c>PdfLimits.MaxStructTreeEdges</c> has: a cap
        /// on one factor bounds neither.
        /// </remarks>
        public static int MaxLinearizationSharedReferences { get; set; } = DefaultMaxLinearizationSharedReferences;

        /// <summary>Built-in value of <see cref="MaxLinearizationSharedReferences"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxLinearizationSharedReferences = 2_000_000;

        /// <summary>
        /// References the linearizer may examine, in all, while it divides a document into Annex F's parts.
        /// </summary>
        /// <remarks>
        /// Each page's closure is its own walk, and what a walk examines is the references of every object it
        /// reaches, not the objects it collects: pages sharing an object that names every page examine
        /// pages² references and collect nothing for it. <see cref="MaxLinearizationSharedReferences"/> bounds
        /// what the walks collect; this bounds the work. Each object's references are read once and
        /// de-duplicated, so a repeated reference costs nothing after the first. Measured: 4 000 pages
        /// examining 16 million linearized in 0.26 s, so the default is a couple of seconds' work.
        /// </remarks>
        public static int MaxLinearizationEdges { get; set; } = DefaultMaxLinearizationEdges;

        /// <summary>Built-in value of <see cref="MaxLinearizationEdges"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxLinearizationEdges = 100_000_000;

        /// <summary>
        /// Objects the progressive page reader examines while checking that a page's <c>/Resources</c>
        /// are in hand — the dictionary, its categories, their entries and what those reference, to a
        /// fixed depth of reference hops.
        /// </summary>
        /// <remarks>
        /// The walk is over a file-chosen graph read from a partial download: a <c>/Font</c> dictionary
        /// of a million entries, or an array of a million references, costs the file nothing. Past the
        /// ceiling the rest is not checked and the reader says so through its <c>LastError</c>; the
        /// page is still answered from what was fetched.
        /// </remarks>
        public static int MaxProgressiveResourceObjects { get; set; } = DefaultMaxProgressiveResourceObjects;

        /// <summary>Built-in value of <see cref="MaxProgressiveResourceObjects"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxProgressiveResourceObjects = 4096;

        /// <summary>
        /// How far past the start of a stream's data the parser scans for the <c>endstream</c>
        /// keyword when the declared <c>/Length</c> is missing, unresolvable or demonstrably wrong.
        /// </summary>
        /// <remarks>
        /// Past this the stream is read at the fallback length and the refusal is recorded on
        /// <c>PdfParser.LastObjectParseError</c> — a zero-byte stream that was never reported was
        /// indistinguishable from one that was always empty.
        /// </remarks>
        public static long MaxEndStreamSearchBytes { get; set; } = DefaultMaxEndStreamSearchBytes;

        /// <summary>Built-in value of <see cref="MaxEndStreamSearchBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxEndStreamSearchBytes = 8L * 1024 * 1024;

        /// <summary>
        /// Maximum number of hops followed while resolving a chain of indirect references
        /// (<c>1 0 R</c> → <c>2 0 R</c> → …) before the chain is abandoned as <c>PdfNull</c>.
        /// </summary>
        /// <remarks>
        /// Deliberately its own name rather than a reuse of <see cref="MaxActionChainDepth"/>,
        /// which happens to hold the same number today: one bounds a <c>/Next</c> chain between
        /// annotations, the other the reference indirection a parser follows, and tuning either for
        /// its own reason must not move the other. Centralising a value is not the same as
        /// unifying it.
        /// </remarks>
        public static int MaxIndirectReferenceChain { get; set; } = DefaultMaxIndirectReferenceChain;

        /// <summary>Built-in value of <see cref="MaxIndirectReferenceChain"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxIndirectReferenceChain = 64;

        // ------------------------------------------------------------------
        //  Functions
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum number of inputs <i>m</i> for which a Type 0 (sampled) function is evaluated by
        /// multilinear interpolation. The interpolation visits the 2<sup>m</sup> corners of the
        /// sample hypercube surrounding the point, so the cost is exponential in <i>m</i> — and
        /// <i>m</i> is the colorant count of a DeviceN space, which the file chooses (up to 32 by
        /// §8.6.6.5). 8 admits every real transform, CMYK included, at 256 corners; a wider
        /// function falls back to the nearest sample and says so through
        /// <c>PdfFunctionEvaluator.LastType0Failure</c> rather than allocating 2<sup>32</sup>.
        /// </summary>
        public static int MaxSampledFunctionInterpolationInputs { get; set; } = DefaultMaxSampledFunctionInterpolationInputs;

        /// <summary>Built-in value of <see cref="MaxSampledFunctionInterpolationInputs"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSampledFunctionInterpolationInputs = 8;

        // ------------------------------------------------------------------
        //  Colour
        // ------------------------------------------------------------------

        /// <summary>
        /// Largest colorant count a <c>/DeviceN</c> colour space may declare (ISO 32000-1
        /// §8.6.6.5 — "the number of colorants shall not exceed 32").
        ///
        /// <para>The count comes from the length of the space's colorant-name array, which is
        /// file-supplied, so anything sized from it needs this bound. It is a <b>spec</b> limit
        /// rather than a defensive one, which is why it lives here as a named value rather than as
        /// a locally chosen cap.</para>
        /// </summary>
        public static int MaxColorantCount { get; set; } = DefaultMaxColorantCount;

        /// <summary>Built-in value of <see cref="MaxColorantCount"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxColorantCount = 32;

        /// <summary>
        /// Largest decoded ICC profile stream the renderer will parse. An <c>/ICCBased</c> profile
        /// arrives as an ordinary (compressible) PDF stream, so it is untrusted input that a small
        /// file can inflate; the largest real-world profiles — big CMYK output profiles with full
        /// A2B/B2A luts — are a few megabytes.
        /// </summary>
        public static int MaxIccProfileBytes { get; set; } = DefaultMaxIccProfileBytes;

        /// <summary>Built-in value of <see cref="MaxIccProfileBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxIccProfileBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Largest tag count accepted from an ICC profile's tag table. The count is a file-supplied
        /// <c>uInt32</c> that sizes a 12-byte-per-entry table, so it is bounded before the table is
        /// walked. Real profiles carry well under a hundred tags.
        /// </summary>
        public static int MaxIccTagCount { get; set; } = DefaultMaxIccTagCount;

        /// <summary>Built-in value of <see cref="MaxIccTagCount"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxIccTagCount = 1024;

        /// <summary>
        /// Largest sample count accepted from a sampled ICC <c>curveType</c> tone curve. Also a
        /// file-supplied <c>uInt32</c>, and it sizes an allocation; 4096 is the largest value real
        /// profiles use, and 65536 is the whole range a 16-bit sample can address.
        /// </summary>
        public static int MaxIccCurvePoints { get; set; } = DefaultMaxIccCurvePoints;

        /// <summary>Built-in value of <see cref="MaxIccCurvePoints"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxIccCurvePoints = 65536;

        // ------------------------------------------------------------------
        //  Rendering quality (not security)
        // ------------------------------------------------------------------

        /// <summary>
        /// Segments used when flattening a Bézier or a Coons/tensor patch edge for a <b>raster</b>
        /// target. 16 is ample when the result is immediately sampled to pixels.
        ///
        /// <para>Observed by <c>Base.Tests</c>' <c>GradientResolutionTests</c>, which measures a
        /// rendered patch boundary against the true cubic: coarsening this shows up there as ink
        /// spilling past the curve.</para>
        /// </summary>
        public static int BezierFlattenSteps { get; set; } = DefaultBezierFlattenSteps;

        /// <summary>Built-in value of <see cref="BezierFlattenSteps"/>; for a compile-time constant context.</summary>
        public const int DefaultBezierFlattenSteps = 16;

        /// <summary>
        /// Number of colour stops emitted when approximating a PDF shading function as an SVG
        /// gradient.
        ///
        /// <para>Unrelated to <see cref="BezierFlattenSteps"/> despite both once being a bare
        /// <c>N</c>: this is colour resolution, not geometry. A PDF shading function is an
        /// arbitrary continuous function, while an SVG gradient can only carry discrete stops, so
        /// this is the sampling rate of that approximation — and SVG output is
        /// resolution-independent, so banding it introduces stays visible at any zoom.</para>
        ///
        /// <para>Prose is not enforcement: <c>Base.Tests</c>' <c>GradientResolutionTests</c> counts the <c>&lt;stop&gt;</c>
        /// elements an export actually emits, and asserts the two constants differ.</para>
        /// </summary>
        public static int SvgGradientStopCount { get; set; } = DefaultSvgGradientStopCount;

        /// <summary>Built-in value of <see cref="SvgGradientStopCount"/>; for a compile-time constant context.</summary>
        public const int DefaultSvgGradientStopCount = 32;

        /// <summary>
        /// Most <c>&lt;use&gt;</c> references one SVG-to-PDF conversion expands. A reference draws
        /// its target again, and a target may itself hold references, so a few kilobytes of SVG —
        /// each level referencing the next twice — ask for 2^n copies: the "billion laughs" of
        /// SVG. Past this, a reference is skipped and reported. Depth alone cannot bound it; the
        /// count does. A marker drawn at a vertex and an SVG document drawn as an <c>&lt;image&gt;</c>
        /// count as references do: a chain of markers, each marking with the next, multiplies by the
        /// vertex count at every level.
        /// </summary>
        public static int MaxSvgUseExpansions { get; set; } = DefaultMaxSvgUseExpansions;

        /// <summary>Built-in value of <see cref="MaxSvgUseExpansions"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSvgUseExpansions = 10_000;

        /// <summary>
        /// Most elements one SVG-to-PDF conversion copies through <c>&lt;use&gt;</c>, all references
        /// together: each reference clones its target whole, so <see cref="MaxSvgUseExpansions"/> bounds
        /// the references and not what each one costs. Past it a reference is skipped and reported. A
        /// marker's content at each vertex it marks and an SVG image's document count too.
        /// </summary>
        /// <remarks>
        /// Measured on the shipped build: a group of 2 000 rectangles referenced 2 000 times — 86 KB
        /// of SVG, both counts inside every other ceiling — would convert in 21 s, allocate 8 GB and
        /// write a 238 MB PDF; at a million copied elements it stops at 5 s and 2 GB. A
        /// quarter of a million is a sprite sheet of fifty-element icons used five thousand times.
        /// </remarks>
        public static long MaxSvgUseExpandedElements { get; set; } = DefaultMaxSvgUseExpandedElements;

        /// <summary>Built-in value of <see cref="MaxSvgUseExpandedElements"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxSvgUseExpandedElements = 250_000;

        /// <summary>
        /// Most cycles one SVG gradient's <c>spreadMethod</c> <c>repeat</c> or <c>reflect</c> is
        /// written with in a PDF: each cycle is a piece of the shading's function, and the file sets
        /// the count — a gradient a thousandth of a unit long over a shape two hundred wide asks for
        /// two hundred thousand. Past this the cycles stop, the rest padded, and that is reported.
        /// </summary>
        public static int MaxSvgGradientCycles { get; set; } = DefaultMaxSvgGradientCycles;

        /// <summary>Built-in value of <see cref="MaxSvgGradientCycles"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSvgGradientCycles = 256;

        /// <summary>
        /// Most pixels one SVG filter works on: its region's pixels times the images its primitives
        /// read. A filter is applied to pixels, so its region is rendered at two pixels a point, fewer
        /// where this asks; each image is sixteen bytes a pixel. Past it at one dot an inch the element
        /// is drawn without its filter, and that is reported.
        /// </summary>
        public static int MaxSvgFilterPixels { get; set; } = DefaultMaxSvgFilterPixels;

        /// <summary>Built-in value of <see cref="MaxSvgFilterPixels"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSvgFilterPixels = 8_388_608;

        /// <summary>
        /// Most elements one SVG conversion filters. Each is rendered and filtered as pixels, so a
        /// few kilobytes of filtered shapes ask for as many renders; past this the rest are drawn
        /// without their filters, and that is reported.
        /// </summary>
        public static int MaxSvgFilteredElements { get; set; } = DefaultMaxSvgFilteredElements;

        /// <summary>Built-in value of <see cref="MaxSvgFilteredElements"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSvgFilteredElements = 256;

        /// <summary>
        /// Most fonts one SVG-to-PDF conversion resolves for text outside WinAnsi: one per family,
        /// weight and style, and the text it must cover. A resolution may read every font file the
        /// host has (<see cref="MaxScannedFontFiles"/>), and a file sets how many families it names,
        /// so a few kilobytes of text elements each in a family of their own ask for as many scans.
        /// Past this the rest are set as <c>?</c> in the standard face, and that is reported.
        /// </summary>
        public static int MaxSvgFontResolutions { get; set; } = DefaultMaxSvgFontResolutions;

        /// <summary>Built-in value of <see cref="MaxSvgFontResolutions"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSvgFontResolutions = 32;

        /// <summary>
        /// Restores every limit in this class to its built-in default.
        /// </summary>
        /// <remarks>
        /// <b>Provided because these are process-global.</b> A test that raises a ceiling and does
        /// not put it back changes the meaning of every later test in the same process, which is the
        /// failure mode this whole class would otherwise introduce.
        /// </remarks>
        public static void ResetToDefaults()
        {
            MaxObjectGraphDepth = DefaultMaxObjectGraphDepth;
            MaxTreeDepth = DefaultMaxTreeDepth;
            MaxFieldInheritanceDepth = DefaultMaxFieldInheritanceDepth;
            MaxQualifiedFieldNameLength = DefaultMaxQualifiedFieldNameLength;
            MaxWidgetAppearanceScanBytes = DefaultMaxWidgetAppearanceScanBytes;
            MaxNameTreeEntries = DefaultMaxNameTreeEntries;
            MaxAnnotationsPerPage = DefaultMaxAnnotationsPerPage;
            MaxAnnotationsPerDocument = DefaultMaxAnnotationsPerDocument;
            MaxBuilderFloatsPerPage = DefaultMaxBuilderFloatsPerPage;
            MaxFormDataFields = DefaultMaxFormDataFields;
            MaxSignatureCoverageMultiple = DefaultMaxSignatureCoverageMultiple;
            MaxSignaturesPerDocument = DefaultMaxSignaturesPerDocument;
            MaxFormDataBytes = DefaultMaxFormDataBytes;
            MaxTreeKidSlots = DefaultMaxTreeKidSlots;
            MaxPageTreeDepth = DefaultMaxPageTreeDepth;
            MaxStructTreeDepth = DefaultMaxStructTreeDepth;
            MaxStructTreeEdges = DefaultMaxStructTreeEdges;
            MaxMarkedContentIdentifier = DefaultMaxMarkedContentIdentifier;
            MaxOpenTypeScriptSteps = DefaultMaxOpenTypeScriptSteps;
            MaxStructTreeNodes = DefaultMaxStructTreeNodes;
            MaxActionChainDepth = DefaultMaxActionChainDepth;
            MaxRenderRecursionDepth = DefaultMaxRenderRecursionDepth;
            MaxFormXObjectDepth = DefaultMaxFormXObjectDepth;
            MaxTextExtractionWorkUnits = DefaultMaxTextExtractionWorkUnits;
            MaxOverlappingTextComparisons = DefaultMaxOverlappingTextComparisons;
            MaxFunctionWorkPerPage = DefaultMaxFunctionWorkPerPage;
            MaxFieldExtractionWork = DefaultMaxFieldExtractionWork;
            FormXObjectInvocationCost = DefaultFormXObjectInvocationCost;
            MaxFormXObjectInvocationsPerPage = DefaultMaxFormXObjectInvocationsPerPage;
            MaxFormContentBytesPerPage = DefaultMaxFormContentBytesPerPage;
            MaxFormOperationsPerPage = DefaultMaxFormOperationsPerPage;
            MaxOffscreenPixelsPerPage = DefaultMaxOffscreenPixelsPerPage;
            MaxPaintWorkPerPage = DefaultMaxPaintWorkPerPage;
            MaxRenderReportsPerKindPerPage = DefaultMaxRenderReportsPerKindPerPage;
            MaxImageDecodeWorkPerPage = DefaultMaxImageDecodeWorkPerPage;
            MaxSyntaxStreamDecodeWork = DefaultMaxSyntaxStreamDecodeWork;
            MaxInvisibleGlyphCoverCandidates = DefaultMaxInvisibleGlyphCoverCandidates;
            MaxResourceDepth = DefaultMaxResourceDepth;
            MaxVisibilityExpressionDepth = DefaultMaxVisibilityExpressionDepth;
            MaxGraphicsStateStackDepth = DefaultMaxGraphicsStateStackDepth;
            MaxTableNestingDepth = DefaultMaxTableNestingDepth;
            MaxShapeTreeDepth = DefaultMaxShapeTreeDepth;
            MaxDrawingShapes = DefaultMaxDrawingShapes;
            MaxDocxTableColumns = DefaultMaxDocxTableColumns;
            MaxSpreadsheetColumns = DefaultMaxSpreadsheetColumns;
            MaxSpreadsheetRows = DefaultMaxSpreadsheetRows;
            MaxTextSearchHits = DefaultMaxTextSearchHits;
            MaxTextSearchContextChars = DefaultMaxTextSearchContextChars;
            MaxTextCompareCells = DefaultMaxTextCompareCells;
            MaxShadingStops = DefaultMaxShadingStops;
            MaxChartDataPoints = DefaultMaxChartDataPoints;
            MaxDocxPartBytes = DefaultMaxDocxPartBytes;
            MaxDocxXmlDepth = DefaultMaxDocxXmlDepth;
            MaxHalftoneCellPixels = DefaultMaxHalftoneCellPixels;
            MaxChartPointsPerSeries = DefaultMaxChartPointsPerSeries;
            MaxChartSeries = DefaultMaxChartSeries;
            MaxPluginContentStreamBytes = DefaultMaxPluginContentStreamBytes;
            MaxPluginResourceEntries = DefaultMaxPluginResourceEntries;
            MaxCharstringCallDepth = DefaultMaxCharstringCallDepth;
            MaxScannedFontFiles = DefaultMaxScannedFontFiles;
            MaxCachedFontPathLookups = DefaultMaxCachedFontPathLookups;
            MaxSvgEmbeddedFontBytes = DefaultMaxSvgEmbeddedFontBytes;
            MaxCharstringSteps = DefaultMaxCharstringSteps;
            MaxCharstringStepsPerPage = DefaultMaxCharstringStepsPerPage;
            MaxCharstringStepsPerFont = DefaultMaxCharstringStepsPerFont;
            MaxCharstringStepsForHinting = DefaultMaxCharstringStepsForHinting;
            MaxCompositeGlyphDepth = DefaultMaxCompositeGlyphDepth;
            MaxGlyphOutlinePoints = DefaultMaxGlyphOutlinePoints;
            MaxCMapUsecmapDepth = DefaultMaxCMapUsecmapDepth;
            MaxCMapRangeSpan = DefaultMaxCMapRangeSpan;
            MaxCMapMappingWrites = DefaultMaxCMapMappingWrites;
            MaxCidWidthEntries = DefaultMaxCidWidthEntries;
            MaxImageDimension = DefaultMaxImageDimension;
            MaxImagePixelArea = DefaultMaxImagePixelArea;
            MaxCcittColumns = DefaultMaxCcittColumns;
            MaxCcittRows = DefaultMaxCcittRows;
            MaxCcittPixels = DefaultMaxCcittPixels;
            MaxShadingDimension = DefaultMaxShadingDimension;
            MaxSpotPlatesPerPage = DefaultMaxSpotPlatesPerPage;
            MaxImagePixelsPerExport = DefaultMaxImagePixelsPerExport;
            MaxMeshVertices = DefaultMaxMeshVertices;
            MaxMeshTrianglesPerBatch = DefaultMaxMeshTrianglesPerBatch;
            MaxJbig2CompositionPixels = DefaultMaxJbig2CompositionPixels;
            MaxJpxTiles = DefaultMaxJpxTiles;
            MaxJpxCodeBlocks = DefaultMaxJpxCodeBlocks;
            MaxJpxDecodeWork = DefaultMaxJpxDecodeWork;
            MaxJpxEncodeWork = DefaultMaxJpxEncodeWork;
            MaxJpxDecodeWorkPerByte = DefaultMaxJpxDecodeWorkPerByte;
            MaxJpxReportedProblems = DefaultMaxJpxReportedProblems;
            MaxJpxWorkingSetBytes = DefaultMaxJpxWorkingSetBytes;
            MaxDecompressedBytes = DefaultMaxDecompressedBytes;
            MaxCMapStreamBytes = DefaultMaxCMapStreamBytes;
            MaxFontProgramBytes = DefaultMaxFontProgramBytes;
            MaxMemoizedDecodedBytesPerDocument = DefaultMaxMemoizedDecodedBytesPerDocument;
            MaxCidToGidMapBytes = DefaultMaxCidToGidMapBytes;
            MaxCumulativeOutputBytes = DefaultMaxCumulativeOutputBytes;
            MaxFilterChainLength = DefaultMaxFilterChainLength;
            MaxObjStmEntries = DefaultMaxObjStmEntries;
            MinObjStmIndexBytesPerEntry = DefaultMinObjStmIndexBytesPerEntry;
            MaxXrefEntries = DefaultMaxXrefEntries;
            MaxObjStmIndexCacheBytes = DefaultMaxObjStmIndexCacheBytes;
            MaxObjStmIndexEntriesRead = DefaultMaxObjStmIndexEntriesRead;
            MaxXrefRecoveryScanBytes = DefaultMaxXrefRecoveryScanBytes;
            MaxRecoveredTrailerDictionaries = DefaultMaxRecoveredTrailerDictionaries;
            LinearizationDictionaryWindowBytes = DefaultLinearizationDictionaryWindowBytes;
            MaxLinearizationHintFieldBits = DefaultMaxLinearizationHintFieldBits;
            MaxLinearizationSharedReferences = DefaultMaxLinearizationSharedReferences;
            MaxLinearizationEdges = DefaultMaxLinearizationEdges;
            MaxProgressiveResourceObjects = DefaultMaxProgressiveResourceObjects;
            MaxEndStreamSearchBytes = DefaultMaxEndStreamSearchBytes;
            MaxIndirectReferenceChain = DefaultMaxIndirectReferenceChain;
            MaxSampledFunctionInterpolationInputs = DefaultMaxSampledFunctionInterpolationInputs;
            MaxColorantCount = DefaultMaxColorantCount;
            MaxIccProfileBytes = DefaultMaxIccProfileBytes;
            MaxIccTagCount = DefaultMaxIccTagCount;
            MaxIccCurvePoints = DefaultMaxIccCurvePoints;
            BezierFlattenSteps = DefaultBezierFlattenSteps;
            SvgGradientStopCount = DefaultSvgGradientStopCount;
            MaxSvgUseExpansions = DefaultMaxSvgUseExpansions;
            MaxSvgUseExpandedElements = DefaultMaxSvgUseExpandedElements;
            MaxSvgGradientCycles = DefaultMaxSvgGradientCycles;
            MaxSvgFilterPixels = DefaultMaxSvgFilterPixels;
            MaxSvgFilteredElements = DefaultMaxSvgFilteredElements;
            MaxSvgFontResolutions = DefaultMaxSvgFontResolutions;
            MaxPageLabelLength = DefaultMaxPageLabelLength;
        }

        /// <summary>
        /// Longest roman-numeral or alphabetic page label formatted as such; a longer one is
        /// formatted as a decimal number instead.
        /// </summary>
        /// <remarks>
        /// <para><b>The file chooses the length.</b> A label's number is the range's <c>/St</c> plus
        /// the page's offset, and §12.4.2 bounds <c>/St</c> below (at 1) but not above. An
        /// alphabetic label repeats one letter once per 26, so <c>/St 2147483647</c> is a label of
        /// 82 million characters — per page — and a roman one writes an <c>m</c> per thousand, two
        /// million of them. The default is far past any label a person reads (26 624 pages of
        /// letters, a million of roman numerals) and far short of what a hostile document asks
        /// for.</para>
        /// </remarks>
        public static int MaxPageLabelLength { get; set; } = DefaultMaxPageLabelLength;

        /// <summary>Built-in value of <see cref="MaxPageLabelLength"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPageLabelLength = 1024;
    }
}
