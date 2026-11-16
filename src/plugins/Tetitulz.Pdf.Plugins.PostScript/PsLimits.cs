// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>
    /// Hard bounds applied while interpreting UNTRUSTED PostScript.
    ///
    /// <para><b>Why every one of these is load-bearing.</b> PostScript is Turing-complete, and the
    /// programs reaching this interpreter come out of PDF files the caller did not write. Unlike a
    /// content stream — which is a finite list of operators — a PostScript program can loop, recurse
    /// and allocate without bound. There is no "parse it and see how big it is" step that would let
    /// us decide up front; the only defence is a budget on every resource the program can consume.
    /// </para>
    ///
    /// <para><b>Aggregate budgets, not per-operation caps.</b> The two that matter most —
    /// <see cref="MaxTotalOps"/> and <see cref="MaxCompositeAllocationBytes"/> — are cumulative
    /// across one conversion, because per-operation caps do not compose:
    /// <c>{ 1000000 string pop } loop</c> allocates unboundedly while every single allocation in it
    /// is perfectly legal. This mirrors the reasoning in <c>Core.Util.WorkBudget</c> and in
    /// <c>PdfFunctionEvaluator</c>'s <c>MaxPsTotalOps</c>.</para>
    ///
    /// <para><b>Changing a value here changes DoS-guard behaviour.</b> Each has an adversarial
    /// regression test; raising one weakens a real protection. Where Core already owns the right
    /// number — graphics-state depth, image dimensions — this table deliberately does not redefine
    /// it and the code reads <c>PdfLimits</c> instead.</para>
    /// </summary>
    public static class PsLimits
    {
        // ------------------------------------------------------------------
        //  Execution
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum interpreter steps in one conversion, spent through a
        /// <c>Core.Util.WorkBudget</c>: one tick per executed object, one per loop iteration, and
        /// one per element when a procedure is pushed.
        ///
        /// <para>Ten times <c>PdfFunctionEvaluator.MaxPsTotalOps</c> (2e6) because that budget is
        /// spent <i>per shading sample</i> — dozens to hundreds of evaluations per shading — while
        /// this one is spent once per XObject or page. A dense Illustrator EPS executes in the low
        /// hundreds of thousands of steps, so this leaves two orders of magnitude of headroom
        /// while still terminating <c>{ } loop</c> in bounded time.</para>
        /// </summary>
        public static long MaxTotalOps { get; set; } = DefaultMaxTotalOps;

        /// <summary>Built-in value of <see cref="MaxTotalOps"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxTotalOps = 20_000_000;

        /// <summary>
        /// Maximum depth of the execution stack — nested procedure calls, loops and
        /// <c>stopped</c> frames.
        ///
        /// <para>These are <b>heap</b> frames, not CLR frames: the interpreter's step loop is
        /// iterative precisely so that <c>exit</c> and <c>stop</c> unwind by searching a list
        /// rather than by throwing through CLR frames. So this bounds memory, not stack, and can
        /// sit far above <c>PdfFunctionEvaluator.MaxPsProcedureDepth</c> (100), which is limited by
        /// two CLR frames per level against a 1 MB stack. The PLRM's own <c>execstack</c> limits
        /// are 250 (Level 1) and 10 000 (Level 2). This is what catches <c>/f { f } def f</c>.</para>
        /// </summary>
        public static int MaxExecStackDepth { get; set; } = DefaultMaxExecStackDepth;

        /// <summary>Built-in value of <see cref="MaxExecStackDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxExecStackDepth = 1_000;

        /// <summary>
        /// Maximum operand-stack depth. The PLRM Level 1 limit is 500 and real programs peak in the
        /// low hundreds. Overflow raises a catchable PostScript <c>stackoverflow</c> error — not a
        /// CLR exception — so a program that brackets its work in <c>stopped</c> still behaves the
        /// way its author intended.
        /// </summary>
        public static int MaxOperandStackDepth { get; set; } = DefaultMaxOperandStackDepth;

        /// <summary>Built-in value of <see cref="MaxOperandStackDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxOperandStackDepth = 10_000;

        /// <summary>
        /// Maximum dictionary-stack depth (<c>begin</c> without a matching <c>end</c>). The PLRM
        /// Level 1 limit is 20; past this, <c>begin</c> raises <c>dictstackoverflow</c>.
        /// </summary>
        public static int MaxDictStackDepth { get; set; } = DefaultMaxDictStackDepth;

        /// <summary>Built-in value of <see cref="MaxDictStackDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxDictStackDepth = 64;

        /// <summary>
        /// Maximum nesting of <c>{}</c>, <c>[]</c> and <c>&lt;&lt;&gt;&gt;</c> while parsing. The
        /// parser is iterative, so this bounds a builder list rather than the CLR stack: without it
        /// a file of a million <c>{</c> characters allocates a million builders before anything
        /// executes.
        /// </summary>
        public static int MaxParseNestingDepth { get; set; } = DefaultMaxParseNestingDepth;

        /// <summary>Built-in value of <see cref="MaxParseNestingDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxParseNestingDepth = 100;

        // ------------------------------------------------------------------
        //  Allocation
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum length of a single <c>array</c> or <c>packedarray</c>. Checked <b>before</b>
        /// allocating — the compute-the-maximum-up-front shape <c>WorkBudget</c>'s documentation
        /// recommends — so <c>1000000000 array</c> raises <c>limitcheck</c> instead of throwing
        /// <c>OutOfMemoryException</c>.
        /// </summary>
        public static int MaxSingleArrayLength { get; set; } = DefaultMaxSingleArrayLength;

        /// <summary>Built-in value of <see cref="MaxSingleArrayLength"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSingleArrayLength = 1_000_000;

        /// <summary>
        /// Maximum length of a single <c>string</c>, in bytes. Same before-allocation rule as
        /// <see cref="MaxSingleArrayLength"/>. A legal <c>1000000 string</c> (1 MB) still succeeds:
        /// the cap exists to refuse the absurd, not the merely large.
        /// </summary>
        public static int MaxSingleStringLength { get; set; } = DefaultMaxSingleStringLength;

        /// <summary>Built-in value of <see cref="MaxSingleStringLength"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSingleStringLength = 1_000_000;

        /// <summary>
        /// Maximum cumulative bytes of composite objects (arrays, strings, dictionaries) allocated
        /// across one conversion, charged at 24 bytes per array element, 1 per string byte and 48
        /// per dictionary entry.
        ///
        /// <para>This is the cap that per-allocation limits cannot replace:
        /// <c>{ 1000000 string pop } loop</c> stays under
        /// <see cref="MaxSingleStringLength"/> on every iteration and still exhausts memory.</para>
        /// </summary>
        public static long MaxCompositeAllocationBytes { get; set; } = DefaultMaxCompositeAllocationBytes;

        /// <summary>Built-in value of <see cref="MaxCompositeAllocationBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxCompositeAllocationBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Maximum bytes the scanner will discard when skipping a binary section — an <c>eexec</c>
        /// font program or a <c>%%BeginBinary</c> / <c>%%BeginData</c> payload. Bounds a truncated
        /// section whose terminator never arrives.
        /// </summary>
        public static int MaxBinarySkipBytes { get; set; } = DefaultMaxBinarySkipBytes;

        /// <summary>Built-in value of <see cref="MaxBinarySkipBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxBinarySkipBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Maximum Type 1 font programs captured from <c>eexec</c> sections for embedding.
        ///
        /// <para>A real document embeds a handful. The cap exists because capturing is the one
        /// scanner step that <b>copies</b> input rather than pointing into it: each capture runs from
        /// the clear-text header the DSC scanner last saw to the end of the encrypted section, so a
        /// file with one header and thousands of <c>eexec</c> sections would copy an ever-longer
        /// prefix for each one — quadratic in the input, and retained. The header offset is also
        /// cleared once used (a header belongs to one program), which is what makes the total
        /// linear; this bounds the count regardless.</para>
        /// </summary>
        public static int MaxCapturedFontPrograms { get; set; } = DefaultMaxCapturedFontPrograms;

        /// <summary>Built-in value of <see cref="MaxCapturedFontPrograms"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCapturedFontPrograms = 64;

        /// <summary>
        /// Maximum filters one file may be read through, a <c>ReusableStreamDecode</c>'s <c>/Filter</c>
        /// array counted. Past it <c>filter</c> is a <c>limitcheck</c>. A real data source has a few; each
        /// stage is a pass over the data, so the depth multiplies what the file holds.
        /// </summary>
        public static int MaxFilterChainDepth { get; set; } = DefaultMaxFilterChainDepth;

        /// <summary>Built-in value of <see cref="MaxFilterChainDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFilterChainDepth = 32;

        /// <summary>
        /// Maximum colorants a <c>/DeviceN</c> space may name; past it the space is not carried. Every
        /// <c>setcolorspace</c> and <c>setcolor</c> in such a space walks and allocates this many.
        /// </summary>
        public static int MaxDeviceNColorants { get; set; } = DefaultMaxDeviceNColorants;

        /// <summary>Built-in value of <see cref="MaxDeviceNColorants"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxDeviceNColorants = 64;

        /// <summary>
        /// Maximum characters in a name. <c>cvn</c> of a longer string is a <c>limitcheck</c>, and a longer
        /// token is cut to it, said. A name is hashed whole at each dictionary it is looked up in and read
        /// by operators that charge it nothing, so its length is a multiplier on everything; PLRM's own
        /// limit is 127.
        /// </summary>
        public static int MaxNameChars { get; set; } = DefaultMaxNameChars;

        /// <summary>Built-in value of <see cref="MaxNameChars"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxNameChars = 4096;

        // ------------------------------------------------------------------
        //  Output
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum PDF content stream emitted for one fragment (a <c>/Subtype /PS</c> XObject or an
        /// inline <c>PS</c> operator). Matches
        /// <c>Core.Constants.PdfLimits.MaxPluginContentStreamBytes</c>, the host-side cap that would
        /// otherwise reject the output: emitting past it could only produce something the renderer
        /// discards. On overflow the emitter stops, closes every open <c>q</c> and returns a
        /// <b>valid truncated</b> stream — never a half-written operator.
        /// </summary>
        public static int MaxEmittedContentBytes { get; set; } = DefaultMaxEmittedContentBytes;

        /// <summary>Built-in value of <see cref="MaxEmittedContentBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxEmittedContentBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Maximum PDF content emitted across every page of one document conversion
        /// (the <c>.ps</c> / <c>.eps</c> import path).
        /// </summary>
        public static long MaxEmittedDocumentBytes { get; set; } = DefaultMaxEmittedDocumentBytes;

        /// <summary>Built-in value of <see cref="MaxEmittedDocumentBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxEmittedDocumentBytes = 32L * 1024 * 1024;

        /// <summary>
        /// Maximum image XObjects one conversion may emit. Each image's own dimensions are bounded
        /// by <c>PdfLimits.MaxImageDimension</c> and <c>MaxImagePixelArea</c>, which Core already
        /// owns; this bounds their <i>number</i>.
        /// </summary>
        public static int MaxImageXObjects { get; set; } = DefaultMaxImageXObjects;

        /// <summary>Built-in value of <see cref="MaxImageXObjects"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxImageXObjects = 256;

        /// <summary>
        /// Maximum shadings one conversion's <c>shfill</c>s may register, as
        /// <see cref="MaxImageXObjects"/> bounds images. Each <c>shfill</c> registers its own: a
        /// dictionary is the program's storage and may have changed since it was last painted. A
        /// shading is a few dozen bytes, so the figure is higher than the images'.
        /// </summary>
        public static int MaxShadings { get; set; } = DefaultMaxShadings;

        /// <summary>Built-in value of <see cref="MaxShadings"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxShadings = 4096;

        /// <summary>
        /// Maximum function dictionaries one shading may carry, a stitching function's
        /// <c>/Functions</c> counted with everything they hold: a file chooses both how many and how
        /// deep, so the count bounds the product.
        /// </summary>
        public static int MaxShadingFunctions { get; set; } = DefaultMaxShadingFunctions;

        /// <summary>Built-in value of <see cref="MaxShadingFunctions"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxShadingFunctions = 64;

        /// <summary>
        /// Maximum <c>makepattern</c>s one conversion runs, the refused counted: each tiling pattern
        /// runs its <c>PaintProc</c> into a cell of up to <see cref="MaxPatternCellContentBytes"/>, so
        /// the two together bound what the cells can write.
        /// </summary>
        public static int MaxPatterns { get; set; } = DefaultMaxPatterns;

        /// <summary>Built-in value of <see cref="MaxPatterns"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPatterns = 256;

        /// <summary>Maximum bytes of content one tiling pattern's cell may emit.</summary>
        public static int MaxPatternCellContentBytes { get; set; } = DefaultMaxPatternCellContentBytes;

        /// <summary>Built-in value of <see cref="MaxPatternCellContentBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPatternCellContentBytes = 256 * 1024;

        /// <summary>
        /// Maximum depth of pattern cells built inside cells — a <c>PaintProc</c> that makes a pattern
        /// runs another one, and one that makes its own recurses.
        /// </summary>
        public static int MaxPatternNestingDepth { get; set; } = DefaultMaxPatternNestingDepth;

        /// <summary>Built-in value of <see cref="MaxPatternNestingDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPatternNestingDepth = 4;

        /// <summary>
        /// Maximum points at which one <c>/DeviceN</c> space's tint transform is sampled for the PDF
        /// function that carries it — the grid's side raised to the colorant count, which the file
        /// chooses, so the product is what is bounded; each point runs the program's transform once.
        /// </summary>
        public static int MaxTintTransformSamples { get; set; } = DefaultMaxTintTransformSamples;

        /// <summary>Built-in value of <see cref="MaxTintTransformSamples"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxTintTransformSamples = 4096;

        /// <summary>
        /// Maximum transfer, black-generation and undercolour-removal functions one conversion samples
        /// (<c>settransfer</c>, <c>setcolortransfer</c>, <c>setblackgeneration</c>,
        /// <c>setundercolorremoval</c>); the same procedure set again is not sampled again, and past the
        /// cap such a function is ignored, said once.
        /// </summary>
        public static int MaxTransferFunctions { get; set; } = DefaultMaxTransferFunctions;

        /// <summary>Built-in value of <see cref="MaxTransferFunctions"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxTransferFunctions = 256;

        /// <summary>
        /// Maximum <c>/ExtGState</c>s one content stream's resources name for a change of the device
        /// parameters — overprint, black generation, undercolour removal, flatness, smoothness, stroke
        /// adjustment — each distinct change once; past it a change is not written, said once. A
        /// program computing a fresh <c>setflat</c> each time round a loop would otherwise make one a
        /// paint.
        /// </summary>
        public static int MaxDeviceParameterStates { get; set; } = DefaultMaxDeviceParameterStates;

        /// <summary>Built-in value of <see cref="MaxDeviceParameterStates"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxDeviceParameterStates = 256;

        /// <summary>
        /// Maximum entries — codespace ranges, CID and notdef mappings — one CMap the program builds
        /// with the <c>CIDInit</c> operators may hold; past it the <c>begin…</c> that would exceed it
        /// is a <c>limitcheck</c>. Adobe's largest CMaps hold some tens of thousands.
        /// </summary>
        public static int MaxCMapEntries { get; set; } = DefaultMaxCMapEntries;

        /// <summary>Built-in value of <see cref="MaxCMapEntries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCMapEntries = 65536;

        /// <summary>
        /// Maximum colour spaces one content stream names to paint a colour in (an ICC, Cal or Lab
        /// space); past it a colour is painted in its device approximation, said once. The same
        /// space set again is named once.
        /// </summary>
        public static int MaxColorSpaceResources { get; set; } = DefaultMaxColorSpaceResources;

        /// <summary>Built-in value of <see cref="MaxColorSpaceResources"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxColorSpaceResources = 256;

        /// <summary>
        /// Maximum forms one conversion builds with <c>execform</c>, the refused counted; a form drawn
        /// again is not built again.
        /// </summary>
        public static int MaxForms { get; set; } = DefaultMaxForms;

        /// <summary>Built-in value of <see cref="MaxForms"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxForms = 256;

        /// <summary>Maximum bytes of content one form's <c>/PaintProc</c> may emit.</summary>
        public static int MaxFormContentBytes { get; set; } = DefaultMaxFormContentBytes;

        /// <summary>Built-in value of <see cref="MaxFormContentBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFormContentBytes = 256 * 1024;

        /// <summary>Maximum depth of cells (forms, pattern cells) a form is built inside.</summary>
        public static int MaxFormNestingDepth { get; set; } = DefaultMaxFormNestingDepth;

        /// <summary>Built-in value of <see cref="MaxFormNestingDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFormNestingDepth = 4;

        /// <summary>
        /// Maximum pages one document conversion may produce. <c>{ showpage } loop</c> is a page
        /// bomb: every page costs a content stream, a page dictionary and a resource dictionary.
        /// </summary>
        public static int MaxPages { get; set; } = DefaultMaxPages;

        /// <summary>Built-in value of <see cref="MaxPages"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPages = 5_000;

        /// <summary>
        /// Maximum entries in a <c>setdash</c> array. Beyond this the array is a denial-of-service
        /// vector against the renderer rather than a dash pattern.
        /// </summary>
        public static int MaxDashArrayEntries { get; set; } = DefaultMaxDashArrayEntries;

        /// <summary>Built-in value of <see cref="MaxDashArrayEntries"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxDashArrayEntries = 64;

        // ------------------------------------------------------------------
        //  Geometry
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum sweep, in degrees, of one cubic Bézier when flattening <c>arc</c> / <c>arcn</c>.
        /// At 90° the maximum radial error is about 2.7e-4·r — invisible at any zoom, and
        /// resolution-independent, which is what matters when the emitted stream may be rendered at
        /// any scale or re-exported to SVG.
        ///
        /// <para>Deliberately <b>not</b> <c>PdfLimits.BezierFlattenSteps</c>: that constant is
        /// documented as raster-only ("ample when the result is immediately sampled to pixels") and
        /// counts line segments, which is the opposite of producing curves. CLAUDE.md's
        /// <c>BezierFlattenSteps</c>-vs-<c>SvgGradientStopCount</c> note is exactly this
        /// trap.</para>
        /// </summary>
        public static double MaxArcSegmentDegrees { get; set; } = DefaultMaxArcSegmentDegrees;

        /// <summary>Built-in value of <see cref="MaxArcSegmentDegrees"/>; for a compile-time constant context.</summary>
        public const double DefaultMaxArcSegmentDegrees = 90.0;

        /// <summary>
        /// Maximum Bézier segments one <c>arc</c> may produce. A full circle needs four; the extra
        /// headroom covers a sweep clamped to <see cref="MaxArcSweepDegrees"/> at a smaller
        /// per-segment angle.
        /// </summary>
        public static int MaxArcSegments { get; set; } = DefaultMaxArcSegments;

        /// <summary>Built-in value of <see cref="MaxArcSegments"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxArcSegments = 8;

        /// <summary>
        /// Maximum sweep, in degrees, that one <c>arc</c> draws. PostScript permits any angles at
        /// all — <c>0 100000 arc</c> is legal — but every turn past the first retraces the same
        /// circle, so more than one revolution is wasted output rather than more drawing.
        /// </summary>
        public static double MaxArcSweepDegrees { get; set; } = DefaultMaxArcSweepDegrees;

        /// <summary>Built-in value of <see cref="MaxArcSweepDegrees"/>; for a compile-time constant context.</summary>
        public const double DefaultMaxArcSweepDegrees = 360.0;

        /// <summary>
        /// Determinant below which a matrix is treated as singular. A singular <c>cm</c> collapses
        /// everything drawn after it to a point or a line, which most readers render as nothing at
        /// all — so the operator is refused and reported rather than emitted.
        /// </summary>
        public static double MinMatrixDeterminant { get; set; } = DefaultMinMatrixDeterminant;

        /// <summary>Built-in value of <see cref="MinMatrixDeterminant"/>; for a compile-time constant context.</summary>
        public const double DefaultMinMatrixDeterminant = 1e-9;

        // ------------------------------------------------------------------
        //  Diagnostics
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum segments in one current path.
        ///
        /// <para>The path was the one composite a program could grow with <b>nothing</b> watching:
        /// <c>PsPath</c>'s mutators charge no allocation and tick no budget, so
        /// <c>{ 1 1 rlineto } loop</c> grew it without limit. That is only half the problem — the
        /// operators that <i>re-read</i> the whole path (<c>clip</c>, <c>pathbbox</c>) then cost
        /// O(path) each while the budget charged them O(1), which is where the quadratic blow-ups
        /// came from. This caps the memory; the <c>Tick</c>s at those operators cap the time.</para>
        ///
        /// <para>100 000 is far above any real drawing — a dense map EPS peaks in the low tens of
        /// thousands — and is reached only by a program that is looping on purpose.</para>
        /// </summary>
        public static int MaxPathSegments { get; set; } = DefaultMaxPathSegments;

        /// <summary>Built-in value of <see cref="MaxPathSegments"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxPathSegments = 100_000;

        /// <summary>
        /// Maximum font resources one conversion may mint.
        ///
        /// <para><c>PdfResourceCollector</c> de-duplicates by base font plus encoding, but a program
        /// can generate an unbounded number of <i>distinct</i> encodings — and each one permanently
        /// retains a font dictionary, an encoding dictionary, a differences array and their names.
        /// Measured without this cap: a 172-byte program retains ~226 MB, 3.5x the whole
        /// <see cref="MaxCompositeAllocationBytes"/> ceiling, because none of it is charged to that
        /// budget.</para>
        ///
        /// <para>Past the cap the most recent resource is reused, so text still draws — in the wrong
        /// encoding — rather than vanishing, and it is reported once.</para>
        /// </summary>
        public static int MaxFontResources { get; set; } = DefaultMaxFontResources;

        /// <summary>Built-in value of <see cref="MaxFontResources"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxFontResources = 256;

        /// <summary>
        /// Maximum characters of a font name that can be the CMap half of a composite name.
        ///
        /// <para><c>PsCidFonts.TrySplitCompositeName</c> tested every hyphen's suffix against the
        /// registry, copying that suffix each time — O(n²) in the name length, uncharged, and a
        /// program can build a 500 KB name without a single long token in its source. The longest
        /// registered Adobe predefined CMap name is well under this, so a longer suffix cannot be
        /// one and needs no copy to rule out.</para>
        /// </summary>
        public static int MaxCMapNameChars { get; set; } = DefaultMaxCMapNameChars;

        /// <summary>Built-in value of <see cref="MaxCMapNameChars"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxCMapNameChars = 64;

        /// <summary>
        /// Maximum characters in a token the number parser will examine.
        ///
        /// <para>A bound is needed because the parser copies the token to a stack buffer, but it
        /// must not be the thing that decides a token's <b>type</b>: below this a numeric-looking
        /// token becomes a <i>name</i>, so an over-long literal surfaces as an <c>undefined</c>
        /// somewhere unrelated. 256 is far past any real literal — a <c>double</c> round-trips in
        /// 24 characters and a 32-bit binary radix literal needs 35 — so the refusal is reserved
        /// for input that was never a number, and it is reported when it happens.</para>
        /// </summary>
        public static int MaxNumericTokenChars { get; set; } = DefaultMaxNumericTokenChars;

        /// <summary>Built-in value of <see cref="MaxNumericTokenChars"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxNumericTokenChars = 256;

        /// <summary>
        /// Maximum distinct glyph names for which <c>glyphshow</c> will synthesise a one-entry
        /// <c>/Differences</c> font.
        ///
        /// <para><c>glyphshow</c> names a glyph the current encoding may not contain, and the only
        /// way to reach it in PDF is a font resource whose encoding does. Counted by <b>name</b>
        /// rather than by call, because the resource collector de-duplicates internally and cannot
        /// report whether a font was new. Without the cap,
        /// <c>{ /g1 glyphshow /g2 glyphshow … } loop</c> over generated names makes
        /// <c>/Resources</c> itself the memory attack.</para>
        /// </summary>
        public static int MaxSyntheticGlyphFonts { get; set; } = DefaultMaxSyntheticGlyphFonts;

        /// <summary>Built-in value of <see cref="MaxSyntheticGlyphFonts"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxSyntheticGlyphFonts = 256;

        /// <summary>
        /// Maximum distinct operator names recorded in
        /// <c>PostScriptDiagnostics.UnimplementedOperators</c>. Deduped and capped so a hostile file
        /// cannot turn the diagnostics themselves into the memory attack.
        /// </summary>
        public static int MaxRecordedUnimplemented { get; set; } = DefaultMaxRecordedUnimplemented;

        /// <summary>Built-in value of <see cref="MaxRecordedUnimplemented"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxRecordedUnimplemented = 256;

        /// <summary>
        /// Maximum reports one conversion composes and delivers to <c>PostScriptDiagnostics.OnEvent</c>;
        /// past it one more says the rest were suppressed, and <c>ReportCount</c> goes on counting.
        /// Almost every report site is reachable from a loop — <c>{ erasepage } loop</c> is one
        /// message a step — so without it the diagnostics were the attack on a subscriber.
        /// </summary>
        public static int MaxReportsPerConversion { get; set; } = DefaultMaxReportsPerConversion;

        /// <summary>Built-in value of <see cref="MaxReportsPerConversion"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxReportsPerConversion = 1000;

        /// <summary>
        /// How deep a Type 3 glyph procedure may nest before the build is refused.
        ///
        /// <para>A glyph procedure runs in a <b>child interpreter</b>, and nothing stops it showing
        /// text in its own font — one CLR frame per level. The operation budget would stop it
        /// eventually, but only after the CLR stack had gone; this keeps it a reported refusal. A
        /// composite glyph drawing its accent through another glyph is depth 2.</para>
        /// </summary>
        public static int MaxType3NestingDepth { get; set; } = DefaultMaxType3NestingDepth;

        /// <summary>Built-in value of <see cref="MaxType3NestingDepth"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxType3NestingDepth = 4;

        /// <summary>
        /// How many distinct glyphs one Type 3 font may build. Each is memoised, so this bounds the
        /// retained CharProcs; <see cref="MaxTotalOps"/> bounds the work.
        /// </summary>
        public static int MaxType3Glyphs { get; set; } = DefaultMaxType3Glyphs;

        /// <summary>Built-in value of <see cref="MaxType3Glyphs"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxType3Glyphs = 1024;

        /// <summary>Emitted bytes one glyph's CharProc may reach.</summary>
        public static int MaxType3GlyphContentBytes { get; set; } = DefaultMaxType3GlyphContentBytes;

        /// <summary>Built-in value of <see cref="MaxType3GlyphContentBytes"/>; for a compile-time constant context.</summary>
        public const int DefaultMaxType3GlyphContentBytes = 256 * 1024;

        /// <summary>
        /// Emitted glyph bytes across the whole conversion.
        ///
        /// <para>Aggregate for the reason <see cref="MaxCompositeAllocationBytes"/> records:
        /// per-item caps do not compose. A thousand glyphs each just under the per-glyph cap is
        /// 256 MB of retained CharProcs that no single cap would refuse.</para>
        /// </summary>
        public static long MaxType3TotalGlyphBytes { get; set; } = DefaultMaxType3TotalGlyphBytes;

        /// <summary>Built-in value of <see cref="MaxType3TotalGlyphBytes"/>; for a compile-time constant context.</summary>
        public const long DefaultMaxType3TotalGlyphBytes = 16L * 1024 * 1024;

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
            MaxTotalOps = DefaultMaxTotalOps;
            MaxExecStackDepth = DefaultMaxExecStackDepth;
            MaxOperandStackDepth = DefaultMaxOperandStackDepth;
            MaxDictStackDepth = DefaultMaxDictStackDepth;
            MaxParseNestingDepth = DefaultMaxParseNestingDepth;
            MaxSingleArrayLength = DefaultMaxSingleArrayLength;
            MaxSingleStringLength = DefaultMaxSingleStringLength;
            MaxCompositeAllocationBytes = DefaultMaxCompositeAllocationBytes;
            MaxBinarySkipBytes = DefaultMaxBinarySkipBytes;
            MaxCapturedFontPrograms = DefaultMaxCapturedFontPrograms;
            MaxFilterChainDepth = DefaultMaxFilterChainDepth;
            MaxDeviceNColorants = DefaultMaxDeviceNColorants;
            MaxNameChars = DefaultMaxNameChars;
            MaxEmittedContentBytes = DefaultMaxEmittedContentBytes;
            MaxEmittedDocumentBytes = DefaultMaxEmittedDocumentBytes;
            MaxImageXObjects = DefaultMaxImageXObjects;
            MaxShadings = DefaultMaxShadings;
            MaxShadingFunctions = DefaultMaxShadingFunctions;
            MaxPatterns = DefaultMaxPatterns;
            MaxPatternCellContentBytes = DefaultMaxPatternCellContentBytes;
            MaxPatternNestingDepth = DefaultMaxPatternNestingDepth;
            MaxTintTransformSamples = DefaultMaxTintTransformSamples;
            MaxColorSpaceResources = DefaultMaxColorSpaceResources;
            MaxTransferFunctions = DefaultMaxTransferFunctions;
            MaxDeviceParameterStates = DefaultMaxDeviceParameterStates;
            MaxCMapEntries = DefaultMaxCMapEntries;
            MaxForms = DefaultMaxForms;
            MaxFormContentBytes = DefaultMaxFormContentBytes;
            MaxFormNestingDepth = DefaultMaxFormNestingDepth;
            MaxPages = DefaultMaxPages;
            MaxDashArrayEntries = DefaultMaxDashArrayEntries;
            MaxArcSegmentDegrees = DefaultMaxArcSegmentDegrees;
            MaxArcSegments = DefaultMaxArcSegments;
            MaxArcSweepDegrees = DefaultMaxArcSweepDegrees;
            MinMatrixDeterminant = DefaultMinMatrixDeterminant;
            MaxPathSegments = DefaultMaxPathSegments;
            MaxFontResources = DefaultMaxFontResources;
            MaxCMapNameChars = DefaultMaxCMapNameChars;
            MaxNumericTokenChars = DefaultMaxNumericTokenChars;
            MaxSyntheticGlyphFonts = DefaultMaxSyntheticGlyphFonts;
            MaxRecordedUnimplemented = DefaultMaxRecordedUnimplemented;
            MaxReportsPerConversion = DefaultMaxReportsPerConversion;
            MaxType3NestingDepth = DefaultMaxType3NestingDepth;
            MaxType3Glyphs = DefaultMaxType3Glyphs;
            MaxType3GlyphContentBytes = DefaultMaxType3GlyphContentBytes;
            MaxType3TotalGlyphBytes = DefaultMaxType3TotalGlyphBytes;
        }
    }
}
