// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    // ====================================================================
    //  Request
    // ====================================================================

    /// <summary>
    /// Describes one non-embedded font the document uses, handed to
    /// <see cref="FontEmbeddingOptions.FontProvider"/> so the caller can decide which font
    /// program (if any) to supply for it.
    /// </summary>
    public sealed class FontEmbeddingRequest
    {
        private static readonly IReadOnlyList<int> NoCodePoints = new int[0];

        /// <summary>
        /// The font dictionary's <c>/BaseFont</c> exactly as the document spells it, including any
        /// <c>ABCDEF+</c> subset prefix. Null when the dictionary carries none.
        /// </summary>
        public string BaseFont { get; internal set; }

        /// <summary>
        /// The canonical Standard-14 name this font resolves to (<c>"Helvetica-Bold"</c>,
        /// <c>"Times-Roman"</c>, <c>"Courier"</c>, <c>"Symbol"</c>, <c>"ZapfDingbats"</c>, …), or
        /// <c>null</c> when <see cref="BaseFont"/> is not one of the Standard 14 under any of its
        /// common aliases. <c>"Arial-BoldMT"</c> resolves to <c>"Helvetica-Bold"</c>, which is what
        /// makes the built-in AFM advance widths applicable to it.
        /// </summary>
        public string Standard14Name { get; internal set; }

        /// <summary>The font dictionary's <c>/Subtype</c> — <c>Type1</c>, <c>TrueType</c>, <c>Type0</c>, …</summary>
        public string Subtype { get; internal set; }

        /// <summary>True when <see cref="Standard14Name"/> is non-null.</summary>
        public bool IsStandard14 => Standard14Name != null;

        /// <summary>
        /// True when the font descriptor declares the Symbolic flag (bit 3), or when the base font
        /// is Symbol / ZapfDingbats. A symbolic font's character codes address a built-in encoding
        /// that no substitute reproduces.
        /// </summary>
        public bool IsSymbolic { get; internal set; }

        /// <summary>The resource name the font is registered under on the page, e.g. <c>"F1"</c>.</summary>
        public string ResourceName { get; internal set; }

        /// <summary>Zero-based index of the first page the font was reached from.</summary>
        public int PageIndex { get; internal set; }

        /// <summary>
        /// The distinct characters this font actually draws, in ascending code-point order. Useful
        /// for a provider that wants to supply a pre-subsetted program.
        ///
        /// <para>
        /// Empty is <b>not</b> proof that the font draws nothing: it is also empty for a composite
        /// (Type0) font, whose codes are CIDs rather than characters, and for a font whose encoding
        /// could not be resolved to Unicode. <see cref="UsedCharacterCodes"/> is the raw answer and
        /// <see cref="UsedTextIsComplete"/> says whether the scan behind it was clean.
        /// </para>
        /// </summary>
        public string UsedText { get; internal set; } = string.Empty;

        /// <summary>The character codes seen for this font, in ascending order (may be empty).</summary>
        public IReadOnlyList<int> UsedCharacterCodes { get; internal set; } = NoCodePoints;

        /// <summary>
        /// True when every content stream that could reach this font was scanned successfully, so
        /// <see cref="UsedText"/> is the complete set of characters it draws. "Every" means the page
        /// content, the Form XObjects it invokes, the appearance streams of its annotations, the
        /// tiling patterns and soft-mask groups its resource dictionaries name, and any Type 3 glyph
        /// procedure. False when at least one of those was unreadable, could not be tokenised safely,
        /// or lay beyond the scan's traversal budget — in which case the step refuses to subset,
        /// because a subset built from an incomplete character set drops glyphs the document really
        /// shows.
        /// </summary>
        public bool UsedTextIsComplete { get; internal set; }

        /// <inheritdoc/>
        public override string ToString()
        {
            var sb = new StringBuilder(64);
            sb.Append('/').Append(ResourceName ?? "?");
            if (!string.IsNullOrEmpty(BaseFont)) sb.Append(" (").Append(BaseFont).Append(')');
            if (!string.IsNullOrEmpty(Subtype)) sb.Append(" [").Append(Subtype).Append(']');
            sb.Append(" on page ").Append((PageIndex + 1).ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    // ====================================================================
    //  Options
    // ====================================================================

    /// <summary>
    /// Options for <see cref="PdfFontEmbeddingRemediator.EmbedMissingFonts"/>.
    ///
    /// <para><b>Licensing — read this before enabling
    /// <see cref="AllowSystemFontSubstitution"/>.</b> Embedding a font <i>redistributes</i> it:
    /// the program travels inside every copy of the PDF. Many fonts installed on a machine —
    /// Arial and Times New Roman among them — are licensed under terms that do not permit that.
    /// This library cannot read a font's licence, so it cannot decide for you. The caller is
    /// responsible for holding redistribution rights to whatever it supplies through
    /// <see cref="FontProvider"/> or authorises through
    /// <see cref="AllowSystemFontSubstitution"/>.</para>
    /// </summary>
    public sealed class FontEmbeddingOptions
    {
        /// <summary>
        /// Supplies a font program (TrueType or OpenType bytes) for a font the document does not
        /// embed. Return <c>null</c> to skip that font. This is the primary path: it is the only
        /// one where the caller knowingly chooses the program that gets redistributed.
        ///
        /// <para><b>Licensing:</b> whatever this returns is written into the output document. The
        /// caller is responsible for holding redistribution rights to it.</para>
        ///
        /// <para>
        /// Accepted formats: sfnt TrueType (<c>glyf</c> outlines → <c>/FontFile2</c>), sfnt
        /// OpenType with CFF outlines (<c>OTTO</c> → <c>/FontFile3 /OpenType</c>) and a bare CFF
        /// program (<c>/FontFile3 /Type1C</c>). TrueType Collections (<c>ttcf</c>) and Type 1
        /// PFA/PFB programs are refused with a stated reason rather than embedded incorrectly.
        /// </para>
        /// </summary>
        public Func<FontEmbeddingRequest, byte[]> FontProvider { get; set; }

        /// <summary>
        /// When <see cref="FontProvider"/> returns null, resolve a metrically compatible substitute
        /// from the host's installed fonts. <b>Default false.</b>
        ///
        /// <para><b>Licensing:</b> turning this on authorises this library to embed — and therefore
        /// redistribute — a font installed on the machine running it. Most desktop font licences,
        /// including those of the fonts this maps the Standard 14 onto (Arial, Times New Roman,
        /// Courier New), restrict that. Enable it only when you hold redistribution rights for the
        /// fonts on the host.</para>
        ///
        /// <para>
        /// Substitution is limited to what is metrically safe. The Standard-14 text families map
        /// onto their metrically compatible system equivalents (Helvetica → Arial, Times → Times
        /// New Roman, Courier → Courier New) and the document's own advance widths are always
        /// preserved, so no glyph moves. <b>Symbol and ZapfDingbats are never substituted</b>:
        /// they are symbolic fonts whose character codes address a built-in encoding no text font
        /// reproduces, so substituting one would change every glyph on the page. They are skipped
        /// with an explicit reason instead.
        /// </para>
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>
        /// Reduce each embedded program to the glyphs the document actually draws. Default true.
        ///
        /// <para>
        /// Subsetting is attempted only when the character set is provably complete — every content
        /// stream that can reach the font was decoded and tokenised without incident. If it is not,
        /// or if subsetting fails (CFF programs cannot be subsetted here), the full program is
        /// embedded and a warning is recorded. A subset that is missing a glyph the document shows
        /// is never emitted.
        /// </para>
        /// </summary>
        public bool SubsetEmbeddedFonts { get; set; } = true;
    }

    // ====================================================================
    //  Result
    // ====================================================================

    /// <summary>One font that was left non-embedded, and why.</summary>
    public sealed class FontEmbeddingSkip
    {
        internal FontEmbeddingSkip(string baseFont, string resourceName, int pageIndex, string reason)
        {
            BaseFont     = baseFont;
            ResourceName = resourceName;
            PageIndex    = pageIndex;
            Reason       = reason ?? "(no reason recorded)";
        }

        /// <summary>The font's <c>/BaseFont</c>, or null when it has none.</summary>
        public string BaseFont { get; }

        /// <summary>The resource name the font is registered under.</summary>
        public string ResourceName { get; }

        /// <summary>Zero-based index of the first page the font was reached from.</summary>
        public int PageIndex { get; }

        /// <summary>Why the font was not embedded. Never null, never vague.</summary>
        public string Reason { get; }

        /// <inheritdoc/>
        public override string ToString()
            => "/" + (ResourceName ?? "?")
             + (string.IsNullOrEmpty(BaseFont) ? string.Empty : " (" + BaseFont + ")")
             + " on page " + (PageIndex + 1).ToString(CultureInfo.InvariantCulture)
             + ": " + Reason;
    }

    /// <summary>Outcome of <see cref="PdfFontEmbeddingRemediator.EmbedMissingFonts"/>.</summary>
    public sealed class FontEmbeddingResult
    {
        private static readonly IReadOnlyList<string> NoStrings = new string[0];
        private static readonly IReadOnlyList<FontEmbeddingSkip> NoSkips = new FontEmbeddingSkip[0];

        internal FontEmbeddingResult(
            int fontsExamined,
            int fontsAlreadyEmbedded,
            int fontsEmbedded,
            int fontsSubsetted,
            IReadOnlyList<string> embeddedFonts,
            IReadOnlyList<FontEmbeddingSkip> skippedFonts,
            IReadOnlyList<string> warnings)
        {
            FontsExamined        = fontsExamined;
            FontsAlreadyEmbedded = fontsAlreadyEmbedded;
            FontsEmbedded        = fontsEmbedded;
            FontsSubsetted       = fontsSubsetted;
            EmbeddedFonts        = embeddedFonts ?? NoStrings;
            SkippedFonts         = skippedFonts  ?? NoSkips;
            Warnings             = warnings      ?? NoStrings;
        }

        /// <summary>Distinct font dictionaries reached from the page resources.</summary>
        public int FontsExamined { get; }

        /// <summary>Of those, the ones that already carried a font program and were left untouched.</summary>
        public int FontsAlreadyEmbedded { get; }

        /// <summary>Fonts this call embedded a program into.</summary>
        public int FontsEmbedded { get; }

        /// <summary>Of the embedded ones, how many were reduced to a subset of their glyphs.</summary>
        public int FontsSubsetted { get; }

        /// <summary>
        /// Fonts that were not embedded and are still open UA21 / UA25 violations. Equal to
        /// <see cref="SkippedFonts"/>.Count.
        /// </summary>
        public int FontsSkipped => SkippedFonts.Count;

        /// <summary>The <c>/BaseFont</c> of every font that received a program.</summary>
        public IReadOnlyList<string> EmbeddedFonts { get; }

        /// <summary>Every font left non-embedded, each with a stated reason. Never null.</summary>
        public IReadOnlyList<FontEmbeddingSkip> SkippedFonts { get; }

        /// <summary>
        /// Anything the caller should know: the licensing note when a system font was substituted,
        /// a font whose program the step had to embed whole because subsetting was unsafe, an
        /// encoding that had to be made explicit, an encrypted or unreadable document. Never null.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>True when at least one font received a program, i.e. the output is a new revision.</summary>
        public bool Changed => FontsEmbedded > 0;

        /// <inheritdoc/>
        public override string ToString()
        {
            var c = CultureInfo.InvariantCulture;
            return FontsEmbedded.ToString(c) + " font(s) embedded ("
                 + FontsSubsetted.ToString(c) + " subsetted), "
                 + FontsAlreadyEmbedded.ToString(c) + " already embedded, "
                 + FontsSkipped.ToString(c) + " still not embedded, of "
                 + FontsExamined.ToString(c) + " examined";
        }
    }

    // ====================================================================
    //  The remediator
    // ====================================================================

    /// <summary>
    /// Embeds a font program into every font the document uses but does not carry — the one
    /// PDF/UA-1 violation no other remediation step can close.
    ///
    /// <para>
    /// <b>The gap this exists for.</b> PDF/UA-1 §7.21.4.1 requires <i>every</i> font to be embedded
    /// and grants no exemption for the Standard 14. A document that draws text with Helvetica,
    /// Times or Courier therefore reports <b>UA21</b> (deep checker) and <b>UA25</b> (conformance
    /// plugin) for ever, and no structural fix can touch it: closing it means putting a font
    /// program into the file, which needs a font program from outside the file. This type is where
    /// the caller supplies one.
    /// </para>
    ///
    /// <para><b>Licensing.</b> Embedding a font redistributes it, and most fonts installed on a
    /// machine are licensed in ways that may not permit that. Hence
    /// <see cref="FontEmbeddingOptions.FontProvider"/> is the primary path and
    /// <see cref="FontEmbeddingOptions.AllowSystemFontSubstitution"/> defaults to <c>false</c>.
    /// <b>The caller is responsible for holding redistribution rights to whatever it supplies or
    /// authorises.</b></para>
    ///
    /// <para><b>What is preserved.</b> Embedding must not move a single glyph, so:</para>
    /// <list type="bullet">
    ///   <item><description>an existing <c>/Widths</c> array is never touched — the document's own
    ///     advance widths win over the substitute's;</description></item>
    ///   <item><description>when there is no <c>/Widths</c> (a Standard-14 font relying on the
    ///     built-in AFM metrics) the array is synthesised from the <b>original</b> Standard-14
    ///     metrics, never from the substitute, because the text positions in the content stream
    ///     were laid out against those numbers;</description></item>
    ///   <item><description>the resource name and every object identity stay as they are, so
    ///     content streams keep working untouched;</description></item>
    ///   <item><description>an existing <c>/Encoding</c> or <c>/ToUnicode</c> is never
    ///     disturbed.</description></item>
    /// </list>
    ///
    /// <para><b>Composite (Type0) fonts.</b> A CID selects a glyph by <i>index</i> into one specific
    /// program's glyph order, so the program is not interchangeable the way a simple font's is:
    /// binding the wrong one replaces every glyph on every page that uses the font. This step
    /// therefore never substitutes an installed font for a composite one, refuses a program whose
    /// outline flavour the descendant's <c>/Subtype</c> forbids (ISO 32000-1 Table 126), and warns
    /// — every time — that a caller-supplied program is embedded on the assumption that it is the
    /// one the document was laid out against, which nothing in the file can confirm.</para>
    ///
    /// <para>All edits are written as a single appended revision through
    /// <see cref="DocumentRevisionWriter"/>; the original bytes are never rewritten. An
    /// encrypted document is refused loudly (diagnostic plus warning) and copied through unchanged,
    /// consistently with every other editor in this assembly.</para>
    /// </summary>
    public static partial class PdfFontEmbeddingRemediator
    {
        private const string Op = "PdfFontEmbeddingRemediator.EmbedMissingFonts";

        /// <summary>
        /// Depth cap for the resource walk. A Form XObject's <c>/Resources</c> may name another
        /// Form XObject, and nothing in the format stops it naming <i>itself</i>; the identity set
        /// catches the cycle and this catches the merely absurd.
        ///
        /// <para>Intentionally not <see cref="PdfLimits.MaxResourceDepth"/> (50): that bounds the
        /// <c>/Resources</c> inheritance chain a renderer must follow to draw correctly, while this
        /// only has to find the fonts a real document declares.</para>
        /// </summary>
        private const int MaxResourceDepth = 8;

        /// <summary>Depth cap for the nested content-stream scan (same reasoning).</summary>
        private const int MaxContentDepth = 8;

        /// <summary>
        /// Node budget for the resource walk. A depth cap and an identity set stop a <i>cycle</i>,
        /// but neither bounds a shared <b>acyclic</b> graph: N pages may all name one
        /// <c>/Resources</c> whose <c>/XObject</c> dictionary holds N forms that each name their
        /// own <c>/Resources</c>, and the walk is then quadratic in a file that costs bytes to
        /// build. Object numbers make that sharing free, so the walk needs a budget of its own.
        /// </summary>
        private const int MaxResourceWalkSteps = 2000000;

        /// <summary>
        /// Budget for the whole glyph-usage scan, spent on both the bytes tokenised and the tokens
        /// examined. A Form XObject invoked from two places may inherit different resources, so the
        /// visited set is a <i>path</i> guard, which bounds recursion but not fan-out: a chain of
        /// forms that each invoke the next one twice is exponential. Exhausting this marks the scan
        /// truncated, and a truncated scan never subsets — it embeds whole programs instead.
        ///
        /// <para>Fan-out is attacked from two sides. <see cref="UsageScan.MarkStreamScanned"/>
        /// memoises the (stream, resources) pairs already scanned, so a form invoked N times under
        /// the same resource dictionary is tokenised once; and every scan is charged
        /// <see cref="ContentScanBytesPerStep"/>-proportionally to its length <i>before</i> the work
        /// is done, so what remains is bounded in bytes and not merely in tokens.</para>
        /// </summary>
        private const int MaxContentScanSteps = 20000000;

        /// <summary>
        /// Bytes of content stream one unit of <see cref="MaxContentScanSteps"/> buys. The product
        /// of the two caps the total volume this scan will ever tokenise (~160 MB), which is far
        /// more decompressed content than a real document carries and far less than the 512 MB
        /// <see cref="PdfLimits.MaxDecompressedBytes"/> allows a <i>single</i> stream to reach.
        /// </summary>
        private const int ContentScanBytesPerStep = 8;

        /// <summary><c>/CharProcs</c> — a Type 3 font's glyph procedures.</summary>

        /// <summary>
        /// Why a composite (Type0) font is never given a program this library chose for itself.
        /// Stated once so the early refusal and <see cref="CheckCompositeBinding"/> cannot drift.
        /// </summary>
        private const string CompositeSubstitutionRefusal =
            "the font is a Type0 composite: its character codes are CIDs that index the glyph order " +
            "of the program the document was laid out against, so a font resolved from this " +
            "machine's installed fonts would select a different, unrelated glyph for every code — " +
            "the text would still be drawn, but it would no longer be the document's text. Supply " +
            "the document's own program through FontEmbeddingOptions.FontProvider instead";

        /// <summary>
        /// A shared, monotonically decreasing step count for one walk. Handed down the recursion so
        /// the whole traversal is bounded, not just each individual branch of it.
        /// </summary>

        private static HashSet<PdfObject> NewIdentitySet()
            => new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Embeds a font program into every font the document does not already carry one for, and
        /// writes the result to <paramref name="output"/> as one appended revision.
        ///
        /// <para>
        /// The whole input is always written to <paramref name="output"/>, even when nothing could
        /// be embedded — a document that cannot be improved is still a document. When no font
        /// changed, the output is byte-identical to the input (no empty revision is appended).
        /// </para>
        ///
        /// <para>
        /// <b>Invariant: <see cref="FontEmbeddingResult.FontsEmbedded"/> &gt; 0 implies the output
        /// differs from the input.</b> The font programs are staged in memory and handed to the
        /// incremental writer, which is documented never to throw: it reports the failure and
        /// leaves the output holding just the original bytes. Returning the staged tally
        /// unconditionally therefore claimed embedded fonts for a file that is byte-for-byte the
        /// input — and UA21/UA25 would have been reported closed on a document that still violates
        /// them. When the revision does not land, <see cref="FontEmbeddingResult.FontsEmbedded"/>
        /// and <see cref="FontEmbeddingResult.FontsSubsetted"/> are <c>0</c>, every font whose
        /// program was lost appears in <see cref="FontEmbeddingResult.SkippedFonts"/> with that as
        /// its stated reason, and <see cref="FontEmbeddingResult.Warnings"/> names the cause.
        /// </para>
        /// </summary>
        /// <param name="input">Source PDF. Read in full; never modified.</param>
        /// <param name="output">Receives the document. Always written.</param>
        /// <param name="options">
        /// Embedding options; null uses <see cref="FontEmbeddingOptions"/> defaults, under which no
        /// font source is available and every non-embedded font is skipped with that reason.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        public static FontEmbeddingResult EmbedMissingFonts(
            Stream input, Stream output, FontEmbeddingOptions options = null)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            options = options ?? new FontEmbeddingOptions();
            AccessibilityDiagnostics.Clear();

            byte[] bytes = ReadAll(input);
            var warnings = new List<string>();
            var skips    = new List<FontEmbeddingSkip>();

            PdfDocument doc = null;
            try
            {
                doc = PdfDocument.Open(new MemoryStream(bytes, writable: false));
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the document could not be parsed; it was copied to the output unchanged and no " +
                    "font was embedded", ex);
                warnings.Add("The document could not be parsed (" + ex.Message +
                             "), so no font could be embedded and UA21/UA25 remain open.");
                output.Write(bytes, 0, bytes.Length);
                return new FontEmbeddingResult(0, 0, 0, 0, null, null, warnings);
            }

            // Every edit is computed before a single byte is written, so a failure in the analysis
            // leaves `output` untouched rather than half-written; `written` then guarantees the
            // recovery path cannot append the document a second time.
            bool written = false;

            try
            {
                if (doc.IsEncrypted)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "the document is encrypted and re-encrypting an appended revision is out of " +
                        "scope; the input was copied to the output unchanged and no font was embedded");
                    warnings.Add("The document is encrypted. Every edit this library writes is a " +
                                 "clear-text incremental update, which would not match the encryption " +
                                 "dictionary, so no font was embedded and UA21/UA25 remain open. " +
                                 "Decrypt the document first.");
                    written = true;
                    output.Write(bytes, 0, bytes.Length);
                    return new FontEmbeddingResult(0, 0, 0, 0, null, null, warnings);
                }

                PdfObjectIndex index = PdfObjectIndex.Build(doc);
                // No ceiling to honour here: the clear-text path plans against the very file it
                // writes, so the index's own ceiling IS the target's. Plan passes a real one.
                List<FontSite> sites = CollectFontSites(doc, index, out string incompleteness, warnings);

                // A walk that could not finish has already put its own sentence into `warnings`
                // (CollectFontSites does that for the budget case); this one covers the rest, so an
                // empty result is never silently equivalent to "this document has no font to fix".
                if (incompleteness != null && sites.Count == 0)
                    warnings.Add("No font could be examined because " + incompleteness +
                                 "; UA21/UA25 cannot be ruled out for this document.");

                if (sites.Count == 0)
                {
                    written = true;
                    output.Write(bytes, 0, bytes.Length);
                    return new FontEmbeddingResult(0, 0, 0, 0, null, null, warnings);
                }

                UsageScan usage = CollectUsedCharacterCodes(doc);

                var modified     = new Dictionary<int, PdfObject>();
                var newObjects   = new Dictionary<int, PdfObject>();
                var embedded     = new List<string>();
                // Kept alongside `embedded` so that, if the revision carrying these programs never
                // reaches the output, each one can be turned back into a stated skip rather than
                // vanishing from the accounting.
                var embeddedSites = new List<FontSite>();

                int alreadyEmbedded = 0;
                int subsetted       = 0;

                StageEmbedding(
                    doc, index, sites, usage, options, modified, newObjects,
                    embedded, embeddedSites, skips, warnings,
                    ref alreadyEmbedded, ref subsetted);

                // With no edits staged this writes the original bytes and nothing else, so a
                // document whose fonts are all embedded (or all unfixable) comes out byte-identical
                // — no empty revision is appended. The write is verified because the writer falls
                // back to exactly that byte-identical output when it cannot build the revision, and
                // "N fonts embedded" about an unchanged file would close UA21/UA25 on a document
                // that still violates them.
                written = true;
                bool landed = WriteVerifiedEmbedding(output, bytes, doc, index, modified, newObjects);

                if (!landed)
                {
                    string lost =
                        "the font program was staged but the incremental revision carrying it never "
                        + "reached the output stream, so the produced document is the original and "
                        + "this font is still not embedded";

                    foreach (FontSite lostSite in embeddedSites)
                    {
                        skips.Add(new FontEmbeddingSkip(
                            lostSite.BaseFont, lostSite.ResourceName, lostSite.PageIndex, lost));
                    }

                    warnings.Add(
                        "The incremental revision carrying the embedded font programs never reached "
                        + "the output stream, so the output holds the original document and no font "
                        + "was embedded. UA21/UA25 remain open. See PdfAccessibility.DiagnosticReported "
                        + "for the underlying cause.");

                    // The residual warning is deliberately built after the write outcome is known:
                    // its count has to be the number of fonts that really are still open.
                    if (skips.Count > 0)
                        warnings.Add(BuildResidualWarning(skips.Count));

                    return new FontEmbeddingResult(
                        sites.Count, alreadyEmbedded, 0, 0, null, skips, warnings);
                }

                if (skips.Count > 0)
                    warnings.Add(BuildResidualWarning(skips.Count));

                return new FontEmbeddingResult(
                    sites.Count, alreadyEmbedded, embedded.Count, subsetted, embedded, skips, warnings);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the font-embedding pass failed; the input was copied to the output unchanged", ex);
                warnings.Add("The font-embedding pass failed (" + ex.Message +
                             "); the document was written " +
                             (written ? "before the failure" : "unchanged") +
                             " and UA21/UA25 remain open.");

                if (!written) output.Write(bytes, 0, bytes.Length);
                return new FontEmbeddingResult(0, 0, 0, 0, null, skips, warnings);
            }
            finally
            {
                doc.Dispose();
            }
        }

        /// <summary>
        /// Computes the font-embedding revision <b>without writing it</b>, so a caller that owns
        /// the write can append it to bytes this method never sees — which is how an
        /// <b>encrypted</b> document gets its fonts embedded, closing UA21/UA25 on a file the
        /// clear-text path can only decline.
        /// </summary>
        /// <remarks>
        /// <para><see cref="EmbedMissingFonts(Stream, Stream, FontEmbeddingOptions)"/> appends a
        /// clear-text revision and therefore refuses an encrypted document. This assembly cannot
        /// decrypt (it does not reference <c>Tetitulz.Pdf.Encryption</c>, and must not), so the
        /// operation travels to the decryptor:</para>
        /// <code>
        /// PdfEncryptedIncrementalUpdate.Run(input, output, password,
        ///     (decrypted, firstFree) =&gt; PdfFontEmbeddingRemediator.Plan(decrypted, firstFree, options).Objects);
        /// </code>
        /// <para><b>This pass DOES mint object numbers</b> — a font program is a new stream, and a
        /// synthesised descriptor is another — so <paramref name="firstFreeObjectNumber"/> is
        /// load-bearing here, unlike in <c>PdfReadingOrderAnalyzer.Plan</c>, which only re-deals
        /// existing arrays. It is reserved immediately after the index is built and <b>before</b>
        /// any staging, because <c>PdfArtifactMarker</c> reserved after minting instead, handed out
        /// a number that was live in the target file, and produced a document that would not
        /// decrypt at all.</para>
        /// <para><b>Report through <see cref="FontEmbeddingPlan.Describe"/></b>: whether the
        /// revision reached the file is known only to the caller, and fonts that were staged and
        /// then lost must come back as <i>skips</i> with a stated reason — never as embedded — or
        /// the result closes UA21/UA25 on a document that still violates them.</para>
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate — the object-number ceiling of the file
        /// the revision will be appended to, plus one.
        /// </param>
        /// <param name="options">Embedding options; defaults apply when null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="firstFreeObjectNumber"/> is below 1.
        /// </exception>
        public static FontEmbeddingPlan Plan(
            PdfDocument doc, int firstFreeObjectNumber, FontEmbeddingOptions options = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (firstFreeObjectNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(firstFreeObjectNumber),
                    "Object numbering starts at 1; 0 is the free-list head and can never be written.");

            options = options ?? new FontEmbeddingOptions();
            var warnings = new List<string>();
            var skips = new List<FontEmbeddingSkip>();

            PdfObjectIndex index = PdfObjectIndex.Build(doc);
            // BEFORE staging — see the remarks. EmbedOne mints, so a late reservation hands out a
            // number that is live in the target file.
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            List<FontSite> sites = CollectFontSites(doc, index, out string incompleteness, warnings);
            if (incompleteness != null && sites.Count == 0)
                warnings.Add("No font could be examined because " + incompleteness +
                             "; UA21/UA25 cannot be ruled out for this document.");

            if (sites.Count == 0)
                return FontEmbeddingPlan.Nothing(warnings);

            UsageScan usage = CollectUsedCharacterCodes(doc);

            var modified = new Dictionary<int, PdfObject>();
            var newObjects = new Dictionary<int, PdfObject>();
            var embedded = new List<string>();
            var embeddedSites = new List<FontSite>();
            int alreadyEmbedded = 0;
            int subsetted = 0;

            StageEmbedding(
                doc, index, sites, usage, options, modified, newObjects,
                embedded, embeddedSites, skips, warnings,
                ref alreadyEmbedded, ref subsetted);

            var objects = RevisionObjects.From(
                newObjects, modified, index, "PdfFontEmbeddingRemediator.Plan");

            var siteRefs = new List<FontEmbeddingPlan.FontSiteRef>(embeddedSites.Count);
            foreach (FontSite site in embeddedSites)
                siteRefs.Add(new FontEmbeddingPlan.FontSiteRef(
                    site.BaseFont, site.ResourceName, site.PageIndex));

            return new FontEmbeddingPlan(
                objects, sites.Count, alreadyEmbedded, embedded, subsetted,
                siteRefs, skips, warnings);
        }

        /// <summary>
        /// Stages every embeddable font into <paramref name="modified"/> / <paramref name="newObjects"/>.
        /// Shared by the clear-text writer and by <see cref="Plan"/>, so the two cannot disagree
        /// about what a font-embedding revision contains.
        /// </summary>
        private static void StageEmbedding(
            PdfDocument doc, PdfObjectIndex index, List<FontSite> sites, UsageScan usage,
            FontEmbeddingOptions options,
            Dictionary<int, PdfObject> modified, Dictionary<int, PdfObject> newObjects,
            List<string> embedded, List<FontSite> embeddedSites, List<FontEmbeddingSkip> skips,
            List<string> warnings, ref int alreadyEmbedded, ref int subsetted)
        {
            foreach (FontSite site in sites)
            {
                if (site.AlreadyEmbedded) { alreadyEmbedded++; continue; }

                EmbedOutcome outcome = EmbedOne(
                    doc, index, site, usage, options, modified, newObjects, warnings);

                if (outcome.Embedded)
                {
                    embedded.Add(site.BaseFont ?? site.ResourceName ?? "(unnamed font)");
                    embeddedSites.Add(site);
                    if (outcome.Subsetted) subsetted++;
                }
                else
                {
                    skips.Add(new FontEmbeddingSkip(
                        site.BaseFont, site.ResourceName, site.PageIndex, outcome.SkipReason));
                }
            }
        }

        /// <summary>
        /// Writes the staged font programs as one appended revision and reports whether that
        /// revision actually reached <paramref name="output"/>.
        ///
        /// <para>
        /// Delegates to <see cref="PdfStructureTreeEditor.WriteVerifiedRevision"/> — the assembly's
        /// single write-verification mechanism, shared with <c>PdfAutoTagger</c>,
        /// <c>PdfArtifactMarker</c>, the structure-tree editor and the reading-order fixer — rather
        /// than growing a fifth copy of the same byte-count check here.
        /// </para>
        /// </summary>
        private static bool WriteVerifiedEmbedding(
            Stream output, byte[] bytes, PdfDocument doc, PdfObjectIndex index,
            Dictionary<int, PdfObject> modified, Dictionary<int, PdfObject> newObjects)
            => VerifiedWrite.WriteOrExplain(
                output, bytes, doc, index, modified, newObjects,
                newInfoObjectNumber: -1,
                operation: Op,
                whatWasPlanned: "the embedded font programs",
                failureDetail: out _);

        /// <summary>
        /// What <see cref="CountNonEmbeddedFonts(PdfDocument)"/> returns when the answer could not
        /// be established — an encrypted document, a walk that threw, a page whose resources could
        /// not be read, or a resource graph that outran the traversal budget.
        ///
        /// <para><b>It is deliberately negative rather than 0.</b> Zero is a positive claim — "every
        /// font in this document already carries a program" — and the pipeline turns it into exactly
        /// that sentence when it skips the embedding step. A failed walk is not entitled to that
        /// claim, and a caller comparing <c>== 0</c> or <c>&gt; 0</c> must fall through to running
        /// the step rather than announcing that there is nothing to do.</para>
        /// </summary>
        internal const int UnknownFontCount = -1;

        /// <summary>
        /// Counts the distinct fonts reachable from the page resources whose descriptor carries no
        /// <c>/FontFile</c>, <c>/FontFile2</c> or <c>/FontFile3</c> — i.e. the fonts UA21 and UA25
        /// are open against — or <see cref="UnknownFontCount"/> when the walk could not complete.
        ///
        /// <para>Used by the remediation pipeline to decide whether the embedding step could change
        /// anything, and to say how many fonts remain open when it is not run.</para>
        /// </summary>
        internal static int CountNonEmbeddedFonts(PdfDocument doc)
            => CountNonEmbeddedFonts(doc, out _);

        /// <summary>
        /// As <see cref="CountNonEmbeddedFonts(PdfDocument)"/>, additionally reporting <b>why</b> the
        /// answer is <see cref="UnknownFontCount"/> so a caller can say so instead of inventing a
        /// reassurance. <paramref name="incompleteness"/> is null exactly when the returned count is
        /// the whole truth.
        /// </summary>
        internal static int CountNonEmbeddedFonts(
            PdfDocument doc, out string incompleteness, List<string> warnings = null)
        {
            if (doc == null)
            {
                incompleteness = "no document was supplied";
                return UnknownFontCount;
            }

            if (doc.IsEncrypted)
            {
                incompleteness = "the document is encrypted, so its font resources cannot be walked";
                return UnknownFontCount;
            }

            try
            {
                // No index: counting needs no object numbers, and building one resolves every
                // object in the file — a cost the pipeline would pay on every inspection pass.
                List<FontSite> sites = CollectFontSites(doc, index: null, out incompleteness, warnings);
                if (incompleteness != null) return UnknownFontCount;

                int count = 0;
                for (int i = 0; i < sites.Count; i++)
                    if (!sites[i].AlreadyEmbedded) count++;
                return count;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(
                    "PdfFontEmbeddingRemediator.CountNonEmbeddedFonts",
                    "the font resources could not be walked; the count is reported as unknown, not " +
                    "as zero, so no caller can read it as 'every font is already embedded'", ex);
                incompleteness = "the font walk threw (" + ex.Message + ")";
                return UnknownFontCount;
            }
        }

        /// <summary>
        /// The sentence a skipped embedding step adds to its warnings, naming the consequence in the
        /// vocabulary the rest of the pipeline uses. Shared so the step and this type cannot drift.
        /// </summary>
        internal static string BuildResidualWarning(int fontCount)
            => fontCount.ToString(CultureInfo.InvariantCulture) +
               " font(s) are not embedded, so UA21/UA25 remain open; supply " +
               "RemediationOptions.FontEmbedding.FontProvider or set AllowSystemFontSubstitution " +
               "to close them.";

    }
}
