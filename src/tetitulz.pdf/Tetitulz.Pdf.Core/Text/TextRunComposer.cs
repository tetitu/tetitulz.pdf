// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// What to do with a code point the font has no glyph for.
    /// </summary>
    public enum MissingGlyphPolicy
    {
        /// <summary>
        /// Emit nothing for it, and report it on <see cref="ShapedGlyphRun.MissingCodePoint"/>.
        /// The default: a caller that silently drops a character from a name or an amount has
        /// produced a wrong document, and the only honest options are to report or to refuse.
        /// </summary>
        Refuse = 0,

        /// <summary>
        /// Emit the space glyph in its place — <b>and still</b> set
        /// <see cref="ShapedGlyphRun.MissingCodePoint"/>, so the substitution is visible.
        ///
        /// <para>This exists as a migration bridge for callers that already substituted a space
        /// silently (<c>Builder.Export.RtlText</c>, <c>DocxToPdf.Rendering.UnicodeText</c>): it
        /// reproduces their pixels byte-for-byte while making the event reportable for the first
        /// time. New code should not choose it.</para>
        /// </summary>
        SubstituteSpace = 1,
    }

    /// <summary>Options for <see cref="TextRunComposer.Compose"/>.</summary>
    public sealed class TextRunOptions
    {
        /// <summary>
        /// The paragraph direction. Defaults to <see cref="BaseDirection.Auto"/>, which reproduces
        /// the historical behaviour of <see cref="UnicodeBidi.Reorder(string)"/>.
        /// </summary>
        public BaseDirection BaseDirection { get; set; } = BaseDirection.Auto;

        /// <summary>What to do about a code point the font cannot render. Defaults to
        /// <see cref="MissingGlyphPolicy.Refuse"/>.</summary>
        public MissingGlyphPolicy MissingGlyphPolicy { get; set; } = MissingGlyphPolicy.Refuse;

        /// <summary>
        /// Apply the font's kerning — its <c>GPOS</c> <c>kern</c> feature, else its legacy <c>kern</c>
        /// table — so a pair like "AV" or "To" sits as the font designed it. Off by default: it changes
        /// a run's width, so each producer decides (HTML on, as browsers kern; DOCX where the run asks;
        /// the Builder when the caller does).
        /// </summary>
        public bool Kerning { get; set; }

        /// <summary>
        /// Apply the font's contextual alternates — its <c>GSUB</c> <c>calt</c> feature, one glyph for
        /// another by its neighbours — on top of the required ones (<c>rclt</c>), which are always
        /// applied. Off by default, as Word leaves it; a browser applies it, so the HTML converter
        /// turns it on, and a DOCX run that asks (<c>w14:cntxtAlts</c>) gets it.
        /// </summary>
        public bool ContextualAlternates { get; set; }

        /// <summary>
        /// Form the font's standard ligatures (<c>GSUB</c> <c>liga</c>: fi, fl and the rest), several
        /// glyphs becoming one whose <see cref="GlyphMapping.Text"/> is the letters it stands for.
        /// Off by default, as Word leaves it; a browser forms them. The required ligatures
        /// (<c>rlig</c>) are formed either way.
        /// </summary>
        public bool Ligatures { get; set; }

        /// <summary>
        /// Form the font's discretionary ligatures (<c>GSUB</c> <c>dlig</c>: ct, st and the rest a
        /// designer offers for display use), as <see cref="Ligatures"/> forms the standard ones. Off by
        /// default; CSS's <c>discretionary-ligatures</c> and a DOCX run's discretional
        /// <c>w14:ligatures</c> ask for them.
        /// </summary>
        public bool DiscretionaryLigatures { get; set; }

        /// <summary>
        /// Form the font's historical ligatures (<c>GSUB</c> <c>hlig</c>: the long-s ligatures and the
        /// rest), as <see cref="Ligatures"/> forms the standard ones. Off by default; CSS's
        /// <c>historical-ligatures</c> and a DOCX run's historical <c>w14:ligatures</c> ask for them.
        /// </summary>
        public bool HistoricalLigatures { get; set; }

        /// <summary>
        /// Whether the run is drawn under a directional override in <see cref="BaseDirection"/>
        /// rather than ordered by the bidirectional algorithm: left to right, the characters in their
        /// logical order whatever their class; right to left, reversed (and mirrored where the
        /// algorithm mirrors). What CSS's and SVG's <c>unicode-bidi: bidi-override</c> asks of a run.
        /// Shaping is unaffected: joining and ligatures are decided in logical order, as always.
        /// </summary>
        /// <remarks>Read only with a definite direction; under <c>BaseDirection.Auto</c> it is ignored.</remarks>
        public bool DirectionalOverride { get; set; }
    }

    /// <summary>One emitted glyph and the code point it should map back to.</summary>
    public readonly struct GlyphMapping
    {
        /// <summary>The glyph id emitted into the content stream.</summary>
        public ushort GlyphId { get; }

        /// <summary>
        /// The <b>base</b> code point — the letter the author typed, not the presentation form that
        /// was drawn. This is what belongs in <c>/ToUnicode</c>: mapping to the shaped form instead
        /// makes searching the document for the word visibly on the page fail, and makes copy-paste
        /// yield code points no keyboard produces.
        /// </summary>
        /// <remarks>
        /// One exception: a Hebrew presentation form drawn for a letter and its point (U+FB31 for bet
        /// with dagesh) stands for two code points, and is given as the form. The one
        /// <c>/ToUnicode</c> writer, <c>Core.Writing.ToUnicodeCMapWriter</c>, writes it as its
        /// canonical decomposition, so the text is still what was typed.
        /// </remarks>
        public int CodePoint { get; }

        /// <summary>
        /// The typed text a <b>ligature</b> glyph stands for — the letters its components were, in
        /// logical order ("fi" for the fi ligature) — or <c>null</c> for a glyph that stands for
        /// <see cref="CodePoint"/> alone, which is every glyph of a run that formed no ligature. A
        /// producer records it beside the code point, and the <c>/ToUnicode</c> writer writes the
        /// text, so a search for the word finds it and copy-paste yields the letters.
        /// </summary>
        public string Text { get; }

        internal GlyphMapping(ushort glyphId, int codePoint, string text = null)
        {
            GlyphId = glyphId;
            CodePoint = codePoint;
            Text = text;
        }
    }

    /// <summary>
    /// Where one glyph is drawn relative to where the font's own advances put it, in glyph space
    /// (1000 units per em): the mark attachment of the font's <c>GPOS</c>, and its kerning when asked.
    /// </summary>
    public readonly struct GlyphPlacement
    {
        internal GlyphPlacement(double xOffset, double yOffset, double advanceAdjustment, bool isMark)
        {
            XOffset = xOffset;
            YOffset = yOffset;
            AdvanceAdjustment = advanceAdjustment;
            IsMark = isMark;
        }

        /// <summary>
        /// True for a mark — a glyph the font's <c>GDEF</c> classes as one. It belongs to the letter
        /// before it, so letter spacing (CSS's, applied per grapheme cluster) goes after the letter's
        /// last mark and not after each glyph, as a PDF's <c>Tc</c> would put it.
        /// </summary>
        public bool IsMark { get; }

        /// <summary>How far right of its pen position the glyph is drawn.</summary>
        public double XOffset { get; }

        /// <summary>How far above the baseline the glyph is drawn.</summary>
        public double YOffset { get; }

        /// <summary>
        /// What the glyph's advance differs from the font's (<c>/W</c>) by — the negative of a
        /// mark's own advance, since an attached mark takes no room, or a kerning pair's change.
        /// </summary>
        public double AdvanceAdjustment { get; }

        /// <summary>True when the glyph is drawn exactly where the font's advances put it.</summary>
        public bool IsIdentity => XOffset == 0 && YOffset == 0 && AdvanceAdjustment == 0;
    }

    /// <summary>The result of composing one run of text against one font.</summary>
    public sealed class ShapedGlyphRun
    {
        internal ShapedGlyphRun(ushort[] glyphIds, GlyphMapping[] glyphs, double advance,
                                bool isRightToLeft, int? missingCodePoint, int missingCount,
                                GlyphPlacement[] placements = null)
        {
            GlyphIds = glyphIds;
            Glyphs = glyphs;
            AdvanceGlyphSpaceUnits = advance;
            IsRightToLeft = isRightToLeft;
            MissingCodePoint = missingCodePoint;
            MissingCodePointCount = missingCount;
            Placements = placements;
        }

        /// <summary>
        /// Each glyph's placement, in the order of <see cref="GlyphIds"/>, or <c>null</c> when the run
        /// has no mark — the common case, and every run whose font has no <c>GDEF</c>. A content
        /// stream must honour it (<see cref="GlyphRunShowText"/>) or the run's marks land beside
        /// their letters.
        /// </summary>
        public IReadOnlyList<GlyphPlacement> Placements { get; }

        /// <summary>
        /// How many of the glyphs are marks (<see cref="GlyphPlacement.IsMark"/>): the glyphs letter
        /// spacing does not follow, so a producer measuring a spaced run counts
        /// <c>GlyphIds.Length - MarkCount</c> gaps.
        /// </summary>
        public int MarkCount
        {
            get
            {
                int n = 0;
                if (Placements != null) foreach (GlyphPlacement p in Placements) if (p.IsMark) n++;
                return n;
            }
        }

        /// <summary>Glyph ids in <b>visual</b> order — the order to write into the content stream.</summary>
        public ushort[] GlyphIds { get; }

        /// <summary>The same glyphs, paired with the base code point each maps back to.</summary>
        public IReadOnlyList<GlyphMapping> Glyphs { get; }

        /// <summary>
        /// Total advance in <b>glyph space</b> (1000 units per em), <b>not</b> points.
        ///
        /// <para>Returning points would force every existing call site's arithmetic to change, and
        /// the three that exist today each apply the font size differently. Keeping the raw sum
        /// here and supplying <see cref="WidthAt"/> lets each one stay byte-identical.</para>
        /// </summary>
        public double AdvanceGlyphSpaceUnits { get; }

        /// <summary>The resolved paragraph level was odd — the run reads right-to-left.</summary>
        public bool IsRightToLeft { get; }

        /// <summary>
        /// The first code point the font had no glyph for, or <c>null</c> when every code point was
        /// covered. Branch on this (or on <see cref="IsComplete"/>) rather than on a diagnostic
        /// string, which cannot be branched on.
        /// </summary>
        public int? MissingCodePoint { get; }

        /// <summary>How many code points were uncovered in total.</summary>
        public int MissingCodePointCount { get; }

        /// <summary>Every code point in the source text was rendered.</summary>
        public bool IsComplete => MissingCodePoint == null;

        /// <summary>The run's width in points at a given font size.</summary>
        public double WidthAt(double fontSizePoints)
            => AdvanceGlyphSpaceUnits / PdfUnits.GlyphSpaceUnitsPerEm * fontSizePoints;

        /// <summary>
        /// Records every glyph of the run for a composite font's <c>/W</c> and <c>/ToUnicode</c>:
        /// each glyph id to its code point in <paramref name="gidToUnicode"/>, and a ligature glyph's
        /// to the text it stands for in <paramref name="gidToText"/> (<see cref="GlyphMapping.Text"/>),
        /// which the font writer takes beside the code points. Every producer that draws a composed
        /// run records it this way, so a ligature is written back as its letters everywhere.
        /// </summary>
        public void RecordUsedGlyphs(IDictionary<ushort, int> gidToUnicode, IDictionary<ushort, string> gidToText)
        {
            if (gidToUnicode == null) throw new ArgumentNullException(nameof(gidToUnicode));
            foreach (GlyphMapping g in Glyphs)
            {
                gidToUnicode[g.GlyphId] = g.CodePoint;
                if (g.Text != null && gidToText != null) gidToText[g.GlyphId] = g.Text;
            }
        }
    }

    /// <summary>
    /// Turns a logical-order string into the glyphs to draw, in visual order: shape, then bidi
    /// reorder, then map to glyph ids.
    ///
    /// <para><b>The order is the whole point, and getting it backwards is silent.</b> Arabic
    /// joining is decided by a letter's <i>logical</i> neighbours, so a form chosen after the run
    /// has been reversed is the mirror of the right one. Hebrew is unaffected (it has no contextual
    /// forms — final letters are separate code points), and Latin passes through untouched.</para>
    ///
    /// <para><b>This is producer-side.</b> Composing a run makes the caller the producer, which is
    /// the role shaping belongs to. A PDF <i>reader</i> must not shape — §9.4 defines showing text
    /// as character codes → glyphs, with the producer having already chosen them, so a reader that
    /// shaped again would double-shape the common case.</para>
    ///
    /// <para>The one pipeline behind <c>Base.Operations.FormAppearance.ForEachGlyph</c>,
    /// <c>Builder.Export.RtlText.Shape</c> and <c>DocxToPdf.Rendering.UnicodeText.Shape</c>; separate
    /// pipelines drift on missing-glyph policy and on shaping granularity. Do not start
    /// another.</para>
    /// </summary>
    public static class TextRunComposer
    {
        private static readonly TextRunOptions Defaults = new TextRunOptions();

        [ThreadStatic] private static string _lastCoverageError;

        /// <summary>
        /// Why the most recent <see cref="Compose"/> <b>on this thread</b> could not render every
        /// code point — <c>null</c> when it could, and <c>null</c> on a thread that has not composed
        /// anything.
        ///
        /// <para>Cleared at the start of every <see cref="Compose"/> on every path, so reading it
        /// straight after a call describes that call. (Per-thread storage is not per-operation
        /// scope: a slot written only on the failing path keeps a previous document's message and
        /// reports it against a clean run.)</para>
        /// </summary>
        public static string LastCoverageError => _lastCoverageError;

        /// <summary>
        /// True when <paramref name="text"/> cannot be carried by the byte-oriented simple-font
        /// path and needs a composite (Type0 / Identity-H) font.
        ///
        /// <para>The threshold is Latin-1, not WinAnsi, because that is what
        /// <c>PdfContentStreamBuilder.ShowText</c> actually writes — one byte per char. U+0080–U+009F
        /// are included because WinAnsi puts printable characters at those positions while Latin-1
        /// puts control codes there, so writing the raw byte draws a different glyph.</para>
        /// </summary>
        public static bool NeedsCompositeFont(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (c > 0xFF || (c >= 0x80 && c <= 0x9F)) return true;
            return false;
        }

        /// <summary>
        /// The code points a font must cover to draw <paramref name="text"/> — the code points of the
        /// <b>shaped</b> run, not of the text as it was typed.
        /// </summary>
        /// <remarks>
        /// <para><b>Ask this before choosing a font, not after.</b> <see cref="Compose"/> shapes
        /// before it looks a glyph up, and Arabic shaping substitutes presentation forms
        /// (U+FE70–U+FEFF). A font that covers the base letters and not the forms is therefore a font
        /// that cannot draw the run — and a resolver asked about the base letters picks exactly that
        /// font. Every producer in this tree asked the base-letter question and then shaped: the form
        /// appearance generator and the template engine refused the run afterwards, the signature
        /// appearance generator did the same, and the DOCX renderer drew spaces.</para>
        /// <para>For Hebrew, Latin and CJK this is the identity — none of them is substituted — so
        /// it changes which font is chosen only where it had to.</para>
        /// </remarks>
        public static IReadOnlyCollection<int> CodePointsToCover(string text)
            => FontResolutionRequest.CodePointsOf(OpenTypeShaper.Shape(text) ?? text);

        /// <summary>
        /// Composes one run of logical-order text into visual-order glyphs.
        /// </summary>
        /// <param name="text">Logical-order text. <c>null</c> or empty yields an empty run.</param>
        /// <param name="font">The font the glyphs are taken from.</param>
        /// <param name="options">Null uses the defaults (auto direction, refuse missing glyphs).</param>
        /// <exception cref="ArgumentNullException"><paramref name="font"/> is null.</exception>
        public static ShapedGlyphRun Compose(string text, TrueTypeFontProgram font,
                                             TextRunOptions options)
        {
            if (font == null) throw new ArgumentNullException(nameof(font));
            options = options ?? Defaults;

            // Cleared on EVERY path, including the empty-input and fully-successful ones.
            _lastCoverageError = null;

            if (string.IsNullOrEmpty(text))
            {
                return new ShapedGlyphRun(Array.Empty<ushort>(), Array.Empty<GlyphMapping>(),
                                          0d, options.BaseDirection == BaseDirection.RightToLeft,
                                          null, 0);
            }

            // Shape BEFORE reordering — see the type remarks. A Hebrew letter and its point are first
            // drawn as their presentation form where the font has its glyph (HebrewPresentationForms):
            // a drawing decision, which /ToUnicode undoes.
            Func<int, bool> drawable = cp => font.TryGetGlyph(cp, out ushort g) && g != 0;
            string drawn = HebrewPresentationForms.Compose(text, drawable);
            // A lam joined to a following alef is one glyph (ArabicLigatures) — after joining, which
            // decides the lam's form, and only where the font has the ligature's glyph; where the
            // font's own rules redraw the two forms — as a ligature, or as two contextual pieces —
            // they are left for Substitute, whose glyphs carry the font's mark anchors.
            string shaped = ArabicLigatures.Compose(OpenTypeShaper.Shape(drawn) ?? drawn, drawable,
                                                    (lam, alef) => FontRedrawsLamAlef(font, lam, alef));
            int[] logicalOf;
            string visual = (options.DirectionalOverride && options.BaseDirection != BaseDirection.Auto
                    ? UnicodeBidi.ReorderOverriddenWithMap(shaped, options.BaseDirection == BaseDirection.RightToLeft, out logicalOf)
                    : UnicodeBidi.ReorderWithMap(shaped, options.BaseDirection, out logicalOf)) ?? shaped;

            var gids = new List<ushort>(visual.Length);
            var maps = new List<GlyphMapping>(visual.Length);
            var logical = new List<int>(visual.Length);   // each glyph's logical index in `shaped`
            List<string> joining = null;                  // each glyph's GSUB joining feature, or null
            double advance = 0;
            int? firstMissing = null;
            int missingCount = 0;

            for (int i = 0; i < visual.Length; i++)
            {
                int cp = visual[i];
                int at = logicalOf != null ? logicalOf[i] : i;
                if (char.IsHighSurrogate(visual[i]) && i + 1 < visual.Length &&
                    char.IsLowSurrogate(visual[i + 1]))
                {
                    cp = char.ConvertToUtf32(visual[i], visual[i + 1]);
                    i++;
                }

                // A presentation form the font does not map, but reaches from its letter through its
                // own GSUB joining feature: the letter's glyph now, the feature at substitution.
                string feature = null;
                bool mapped = font.TryGetGlyph(cp, out ushort gid) && gid != 0;
                if (!mapped && font.TryGetJoiningForm(cp, out gid, out feature)) mapped = true;
                // A joining letter Unicode gives no presentation forms (U+0750 onwards and the rest)
                // is drawn as itself, and only the font's own joining feature can join it.
                if (mapped && feature == null && cp == visual[i] &&
                    OpenTypeShaper.TryGetUnformedJoiningFeature(shaped, at, out string unformed))
                    feature = unformed;
                if (feature != null)
                {
                    joining ??= new List<string>(new string[gids.Count]);
                }

                if (!mapped)
                {
                    if (firstMissing == null) firstMissing = cp;
                    missingCount++;

                    if (options.MissingGlyphPolicy != MissingGlyphPolicy.SubstituteSpace)
                        continue;

                    // A font with no space glyph must emit NOTHING rather than glyph 0. Emitting
                    // .notdef here is worse than dropping the character: the box is drawn, and the glyph is
                    // then either omitted from /W and /ToUnicode or — worse — recorded as U+0020,
                    // so the document claims a space it did not draw.
                    if (!font.TryGetGlyph(' ', out gid) || gid == 0)
                        continue;

                    cp = ' ';
                }

                // /ToUnicode gets the BASE letter, not the presentation form drawn. The inverse
                // exists only because OpenTypeShaper is strictly 1:1.
                int baseCp = OpenTypeShaper.TryGetBasePresentationForm(cp, out int bc) ? bc : cp;

                gids.Add(gid);
                maps.Add(new GlyphMapping(gid, baseCp));
                logical.Add(at);
                joining?.Add(feature);
                advance += font.GetAdvance(gid);
            }

            // The font's own substitutions, before positioning — the joining forms it reaches only
            // through GSUB, its required contextual alternates, its required ligatures, and its
            // optional alternates and ligatures when asked: a glyph may change, or several become one,
            // never the letters they stand for, so the run's mapping keeps its letters (a ligature's as
            // its Text) and takes its widths from the glyphs now drawn.
            if (Substitute(font, gids, maps, logical, ref joining, options.ContextualAlternates, options.Ligatures,
                           options.DiscretionaryLigatures, options.HistoricalLigatures))
            {
                advance = 0;
                for (int k = 0; k < gids.Count; k++) advance += font.GetAdvance(gids[k]);
            }

            GlyphPlacement[] placements = Position(font, gids, maps, logical, options.Kerning);
            if (placements != null)
                foreach (GlyphPlacement p in placements) advance += p.AdvanceAdjustment;

            if (firstMissing != null)
            {
                _lastCoverageError =
                    $"font '{font.PostScriptName ?? "(unnamed)"}' has no glyph for U+{firstMissing.Value:X4}" +
                    (missingCount > 1 ? $" (and {missingCount - 1} more)" : string.Empty) +
                    (options.MissingGlyphPolicy == MissingGlyphPolicy.SubstituteSpace
                        ? "; a space was drawn in its place"
                        : "; it was not drawn");
            }

            return new ShapedGlyphRun(gids.ToArray(), maps.ToArray(), advance,
                                      UnicodeBidi.IsRightToLeftParagraph(shaped, options.BaseDirection),
                                      firstMissing, missingCount, placements);
        }

        /// <summary>
        /// The font's glyph positioning applied to a composed run (<see cref="OpenTypeMarkPositioning"/>):
        /// its <c>curs</c>, <c>mark</c> and <c>mkmk</c> lookups — and with <paramref name="kerning"/> its
        /// <c>kern</c> lookups, or its legacy <c>kern</c> table where it has none — run over the glyphs
        /// in <b>logical</b> order, as a shaper runs them, and each mark takes no room. <c>null</c> when
        /// nothing moves: a run with no mark that kerning leaves as it was writes as it always did.
        /// </summary>
        /// <remarks>
        /// <para><b>Attachment is logical, position is visual.</b> Reordering reverses a
        /// right-to-left run, so a Hebrew point comes before its letter in the glyph order; which
        /// letter it belongs to is a question about the text, and where it is drawn a question about
        /// the line. Each glyph's pen position is the sum of the advances before it in visual
        /// order — every mark's removed, a single adjustment's added — and an attached mark is drawn
        /// at its parent's drawn position plus the anchor offset, plus any placement a single
        /// adjustment gave it. A mark the font gives no anchor for stays at its pen position, taking
        /// no room.</para>
        /// <para><b>An advance change belongs to the logically first glyph, and widens the gap after
        /// it in reading order.</b> The run is laid out left to right, so in a right-to-left run that
        /// gap is on the glyph's left: the change goes to the advance of the glyph drawn left of it.
        /// The legacy <c>kern</c> table is a left-to-right list of pairs as drawn, and is applied as
        /// drawn.</para>
        /// <para><b>A cursive join decides the advance it sets</b> — the left glyph's, as shapers set
        /// it — over any kerning on that glyph, and raises or lowers the glyph it hangs, a mark on that
        /// glyph moving with it.</para>
        /// <para>The lookups are taken script by script (<see cref="ScriptSegments"/>): a Latin word
        /// beside Hebrew is kerned under <c>latn</c> and the Hebrew letters pointed under <c>hebr</c>,
        /// each segment falling back to the font's default script as a shaper does. A run of one
        /// script, the common case, is one segment.</para>
        /// </remarks>
        private static GlyphPlacement[] Position(TrueTypeFontProgram font, List<ushort> gids, List<GlyphMapping> maps, List<int> logical, bool kerning)
        {
            if (gids.Count < 2) return null;
            OpenTypeMarkPositioning gpos = font.MarkPositioning;
            if (gpos == null) return null;
            kerning &= gpos.CanKern;

            int n = gids.Count;
            var isMark = new bool[n];
            bool any = false;
            for (int i = 0; i < n; i++) any |= isMark[i] = gpos.IsMark(gids[i]);

            // The glyphs in logical order, which is the order the lookups run in.
            int[] byLogical = LogicalOrder(logical);
            var inLogical = new ushort[n];
            for (int k = 0; k < n; k++) inLogical[k] = gids[byLogical[k]];
            List<ScriptSegment> segments = ScriptSegments(maps, byLogical);

            bool anyFeature = kerning;
            foreach (ScriptSegment s in segments)
                anyFeature |= gpos.HasCursive(s.Script) || gpos.HasDistance(s.Script);
            if (!any && !anyFeature) return null;

            OpenTypeMarkPositioning.Positions applied = ApplySegments(gpos, segments, inLogical, kerning);

            // Which glyphs (in visual order) take the legacy kern table: those of a segment whose
            // script has no GPOS kerning, as the whole run did when it had one script.
            bool[] legacyAt = null;
            if (kerning)
            {
                foreach (ScriptSegment s in segments)
                {
                    if (gpos.HasGposKerning(s.Script)) continue;
                    legacyAt ??= new bool[n];
                    for (int k = s.Start; k < s.Start + s.Count; k++) legacyAt[byLogical[k]] = true;
                }
            }

            double scale = PdfUnits.GlyphSpaceUnitsPerEm / gpos.UnitsPerEm;
            var logicalOfVisual = new int[n];
            for (int k = 0; k < n; k++) logicalOfVisual[byLogical[k]] = k;

            // Advance changes, in visual order: each to the glyph whose advance spans the gap after the
            // changed glyph in reading order — itself, or in a right-to-left run the first letter on
            // its left: a mark there takes no room and is drawn from its own letter, so the gap is the
            // letter's.
            var delta = new double[n];
            bool moved = false;
            for (int k = 0; k < n; k++)
            {
                if (applied.XAdvance[k] == 0) continue;
                int v = byLogical[k];
                int target = v;
                if (v > 0 && k + 1 < n && byLogical[k + 1] == v - 1)
                {
                    target = v - 1;
                    while (target > 0 && isMark[target]) target--;
                }
                delta[target] += applied.XAdvance[k] * scale;
                moved = true;
            }
            if (legacyAt != null)
            {
                for (int i = 0; i < n; i++)
                {
                    if (isMark[i] || !legacyAt[i]) continue;
                    int j = i + 1;
                    while (j < n && isMark[j]) j++;
                    if (j >= n) break;
                    if (!legacyAt[j]) continue;
                    int pair = gpos.LegacyKerning(gids[i], gids[j]);
                    if (pair != 0) { delta[i] += pair * scale; moved = true; }
                }
            }

            // A cursive join sets the advance of the pair's glyph drawn on the left, so that its anchor
            // on the right meets the other's anchor on the left: in a right-to-left run the later glyph's
            // entry and the earlier's exit, left to right the earlier's exit and the later's entry. A
            // mark between them takes no room, so the advance spans to the other glyph.
            var joined = new double?[n];
            for (int k = 0; k < n; k++)
            {
                int j = applied.JoinedTo[k];
                if (j < 0) continue;
                int vi = byLogical[k], vj = byLogical[j];
                if (vj < vi) joined[vj] = (applied.EntryX[j] - applied.ExitX[k]) * scale;
                else joined[vi] = (applied.ExitX[k] - applied.EntryX[j]) * scale;
            }

            // Pen positions in visual order: every mark's advance removed, an adjustment's added; a
            // joined glyph's advance is the join's.
            var pen = new double[n];
            var adjust = new double[n];
            double x = 0;
            for (int i = 0; i < n; i++)
            {
                pen[i] = x;
                double own = font.GetAdvance(gids[i]);
                adjust[i] = isMark[i] ? -own : joined[i] is double set ? set - own : delta[i];
                x += own + adjust[i];
            }

            // Each glyph's height from cursive joins: its own offset plus the glyph it hangs from's.
            double[] lifted = CursiveHeights(applied, n, scale);

            // Drawn positions, resolved in logical order: a parent precedes its mark there.
            var drawnX = new double[n];
            var drawnY = new double[n];
            var placements = new GlyphPlacement[n];
            for (int k = 0; k < n; k++)
            {
                int i = byLogical[k];
                int parent = applied.Parent[k];
                double px = applied.XPlacement[k] * scale, py = applied.YPlacement[k] * scale;
                if (parent >= 0)
                {
                    int pv = byLogical[parent];
                    drawnX[i] = drawnX[pv] + applied.AnchorX[k] * scale + px;
                    drawnY[i] = drawnY[pv] + applied.AnchorY[k] * scale + py;
                }
                else
                {
                    drawnX[i] = pen[i] + px;
                    drawnY[i] = py + lifted[k];
                }
                placements[i] = new GlyphPlacement(drawnX[i] - pen[i], drawnY[i], adjust[i], isMark[i]);
                moved |= !placements[i].IsIdentity;
            }
            return any || moved ? placements : null;
        }

        /// <summary>The visual indices of a run's glyphs in logical order — the order a font's lookups run in.</summary>
        private static int[] LogicalOrder(List<int> logical)
        {
            var byLogical = new int[logical.Count];
            for (int i = 0; i < byLogical.Length; i++) byLogical[i] = i;
            Array.Sort(byLogical, (a, b) => logical[a] != logical[b] ? logical[a].CompareTo(logical[b]) : a.CompareTo(b));
            return byLogical;
        }

        /// <summary>
        /// The font's <c>GSUB</c> <c>rclt</c> and <c>rlig</c> substitutions — its <c>calt</c> ones with
        /// <paramref name="contextualAlternates"/>, its <c>liga</c> ones with <paramref name="ligatures"/>
        /// — applied to the run in logical order (<see cref="OpenTypeMarkPositioning.Substitute"/>),
        /// written back into <paramref name="gids"/> and <paramref name="maps"/> in visual order; true
        /// when a glyph changed.
        /// </summary>
        /// <remarks>
        /// <para><b>A ligature takes the place of the first of its components the page reaches.</b>
        /// Its components are consecutive in logical order and so consecutive in visual order too,
        /// reversed in a right-to-left run; the run is rebuilt by walking the visual order and
        /// writing the ligature where its first component was, dropping the rest. The ligature's
        /// logical index is its first component's, its mapping's code point that component's, and
        /// its <see cref="GlyphMapping.Text"/> the components' typed letters in logical order — each
        /// component's base letter, or the letter and point a Hebrew presentation form or the lam and
        /// alef a lam-alef ligature stand for. A ligature takes no joining feature: the joining was
        /// decided before it formed.</para>
        /// </remarks>
        private static bool Substitute(TrueTypeFontProgram font, List<ushort> gids, List<GlyphMapping> maps, List<int> logical,
                                       ref List<string> joining, bool contextualAlternates, bool ligatures,
                                       bool discretionary = false, bool historical = false)
        {
            OpenTypeMarkPositioning gsub = font.Substitution;
            if (gsub == null || gids.Count == 0) return false;
            int[] byLogical = LogicalOrder(logical);
            var inLogical = new ushort[gids.Count];
            for (int k = 0; k < inLogical.Length; k++) inLogical[k] = gids[byLogical[k]];
            string[] joiningInLogical = null;
            if (joining != null)
            {
                joiningInLogical = new string[gids.Count];
                for (int k = 0; k < joiningInLogical.Length; k++) joiningInLogical[k] = joining[byLogical[k]];
            }
            OpenTypeMarkPositioning.Substituted result = SubstituteSegments(
                gsub, ScriptSegments(maps, byLogical), inLogical, contextualAlternates, joiningInLogical, ligatures,
                discretionary, historical);
            ushort[] after = result.Glyphs;
            if (result.Sources == null)
            {
                bool changed = false;
                for (int k = 0; k < after.Length; k++)
                {
                    if (after[k] == inLogical[k]) continue;
                    gids[byLogical[k]] = after[k];
                    maps[byLogical[k]] = new GlyphMapping(after[k], maps[byLogical[k]].CodePoint);
                    changed = true;
                }
                return changed;
            }

            // A ligature formed: which output glyph each input (logical) glyph went into.
            int n = gids.Count;
            var outputOf = new int[n];
            for (int k = 0; k < after.Length; k++)
                foreach (int source in result.Sources[k]) outputOf[source] = k;
            var logicalOfVisual = new int[n];
            for (int k = 0; k < n; k++) logicalOfVisual[byLogical[k]] = k;

            var newGids = new List<ushort>(after.Length);
            var newMaps = new List<GlyphMapping>(after.Length);
            var newLogical = new List<int>(after.Length);
            List<string> newJoining = joining == null ? null : new List<string>(after.Length);
            var emitted = new bool[after.Length];
            for (int v = 0; v < n; v++)
            {
                int k = outputOf[logicalOfVisual[v]];
                if (emitted[k]) continue;
                emitted[k] = true;
                int[] sources = result.Sources[k];
                int firstVisual = byLogical[sources[0]];
                newGids.Add(after[k]);
                newLogical.Add(logical[firstVisual]);
                newJoining?.Add(sources.Length == 1 ? joining[firstVisual] : null);
                if (sources.Length == 1)
                {
                    newMaps.Add(new GlyphMapping(after[k], maps[firstVisual].CodePoint));
                    continue;
                }
                var typed = new StringBuilder();
                foreach (int source in sources) typed.Append(TypedText(maps[byLogical[source]].CodePoint));
                newMaps.Add(new GlyphMapping(after[k], maps[firstVisual].CodePoint, typed.ToString()));
            }
            gids.Clear(); gids.AddRange(newGids);
            maps.Clear(); maps.AddRange(newMaps);
            logical.Clear(); logical.AddRange(newLogical);
            joining = newJoining;
            return true;
        }

        /// <summary>
        /// The text a glyph's base code point stands for as typed: the letter and point of a Hebrew
        /// presentation form, the lam and alef of a lam-alef ligature, else the code point itself —
        /// what <c>ToUnicodeCMapWriter</c> writes for it.
        /// </summary>
        private static string TypedText(int codePoint)
            => HebrewPresentationForms.Decomposition(codePoint) ?? ArabicLigatures.Decomposition(codePoint)
               ?? char.ConvertFromUtf32(codePoint);

        /// <summary>
        /// Each glyph's height, in logical order and glyph space, from the cursive joins that hang it
        /// from another: its offset plus its parent's height, down the chain. A chain that comes back on
        /// itself (lookups of both orientations on one pair) adds nothing past the repeat.
        /// </summary>
        private static double[] CursiveHeights(OpenTypeMarkPositioning.Positions applied, int n, double scale)
        {
            var height = new double[n];
            var resolved = new bool[n];
            var onPath = new bool[n];
            var path = new List<int>();
            for (int start = 0; start < n; start++)
            {
                int k = start;
                while (k >= 0 && !resolved[k] && !onPath[k]) { onPath[k] = true; path.Add(k); k = applied.CursiveParent[k]; }
                double above = k >= 0 && resolved[k] ? height[k] : 0;
                for (int p = path.Count - 1; p >= 0; p--)
                {
                    int g = path[p];
                    height[g] = above + applied.CursiveY[g] * scale;
                    above = height[g];
                    resolved[g] = true;
                    onPath[g] = false;
                }
                path.Clear();
            }
            return height;
        }

        /// <summary>
        /// Whether the font's required substitutions (<c>GSUB</c> <c>rlig</c> and <c>rclt</c>, under the
        /// forms' script) redraw the lam form <paramref name="lamForm"/> and the alef form
        /// <paramref name="alefForm"/> — as one ligature glyph, or as two contextual glyphs (Segoe UI
        /// draws lam-alef as a lam piece and an alef piece). Those are the glyphs the font's mark
        /// anchors are on, which the encoded presentation form's glyph need not be
        /// (<see cref="ArabicLigatures.Compose"/>).
        /// </summary>
        private static bool FontRedrawsLamAlef(TrueTypeFontProgram font, int lamForm, int alefForm)
        {
            OpenTypeMarkPositioning gsub = font.Substitution;
            if (gsub == null) return false;
            if (!font.TryGetGlyph(lamForm, out ushort lam) || lam == 0) return false;
            if (!font.TryGetGlyph(alefForm, out ushort alef) || alef == 0) return false;

            OpenTypeMarkPositioning.Substituted formed =
                gsub.Substitute(UnicodeScripts.TagOf(lamForm) ?? UnicodeScripts.LatinTag, new[] { lam, alef });
            return formed.Glyphs.Length != 2 || formed.Glyphs[0] != lam || formed.Glyphs[1] != alef;
        }

        /// <summary>A maximal run of glyphs of one script, in logical order, and the tag its lookups are taken under.</summary>
        private readonly struct ScriptSegment
        {
            internal ScriptSegment(int start, int count, string script) { Start = start; Count = count; Script = script; }
            internal int Start { get; }
            internal int Count { get; }
            internal string Script { get; }
        }

        /// <summary>
        /// The run's glyphs in logical order cut into maximal segments of one script, each tagged by
        /// its characters' script (<see cref="UnicodeScripts.TagOf(int)"/>). A glyph of no script — a
        /// space, a digit, a point, punctuation — takes the script of the glyph before it, or of the
        /// first scripted glyph after it at the start, as a shaper's itemizer attaches it; a run with
        /// no scripted glyph at all is one <see cref="UnicodeScripts.LatinTag"/> segment.
        /// </summary>
        /// <remarks>
        /// <b>Why segments and not one tag.</b> The lookups a font registers under <c>hebr</c> are
        /// not the ones it registers under <c>latn</c>: a Latin word beside Hebrew taken under
        /// <c>hebr</c> lost its kerning, and its letters took whatever the Hebrew lookups did with
        /// them. A font with no entry for a segment's script still falls back through
        /// <see cref="UnicodeScripts.Fallbacks"/>, so a face registering Latin alone kerns a Cyrillic
        /// segment as before. The cut is by script alone — a context rule spanning two scripts is a
        /// case no font in the suite has.
        /// </remarks>
        private static List<ScriptSegment> ScriptSegments(List<GlyphMapping> maps, int[] byLogical)
        {
            int n = byLogical.Length;
            var tags = new string[n];
            string current = null;
            for (int k = 0; k < n; k++)
            {
                string tag = UnicodeScripts.TagOf(maps[byLogical[k]].CodePoint);
                if (tag != null) current = tag;
                tags[k] = current;
            }
            // Leading glyphs of no script take the first script after them.
            string first = null;
            for (int k = 0; k < n && first == null; k++) first = tags[k];
            for (int k = 0; k < n && tags[k] == null; k++) tags[k] = first;

            var segments = new List<ScriptSegment>(1);
            int start = 0;
            for (int k = 1; k <= n; k++)
            {
                if (k < n && string.Equals(tags[k], tags[start], StringComparison.Ordinal)) continue;
                segments.Add(new ScriptSegment(start, k - start, tags[start] ?? UnicodeScripts.LatinTag));
                start = k;
            }
            return segments;
        }

        /// <summary>
        /// <see cref="OpenTypeMarkPositioning.Apply"/> over each segment under its own script, the
        /// results joined: every index a part reports (a mark's parent, a cursive join) moved by
        /// the segment's start. One segment is the engine's answer as it is.
        /// </summary>
        private static OpenTypeMarkPositioning.Positions ApplySegments(
            OpenTypeMarkPositioning gpos, List<ScriptSegment> segments, ushort[] inLogical, bool kerning)
        {
            if (segments.Count == 1) return gpos.Apply(segments[0].Script, inLogical, kerning);

            var merged = new OpenTypeMarkPositioning.Positions(inLogical.Length);
            foreach (ScriptSegment s in segments)
            {
                var slice = new ushort[s.Count];
                Array.Copy(inLogical, s.Start, slice, 0, s.Count);
                OpenTypeMarkPositioning.Positions part = gpos.Apply(s.Script, slice, kerning);
                for (int i = 0; i < s.Count; i++)
                {
                    int k = s.Start + i;
                    merged.Parent[k] = part.Parent[i] < 0 ? -1 : part.Parent[i] + s.Start;
                    merged.JoinedTo[k] = part.JoinedTo[i] < 0 ? -1 : part.JoinedTo[i] + s.Start;
                    merged.CursiveParent[k] = part.CursiveParent[i] < 0 ? -1 : part.CursiveParent[i] + s.Start;
                    merged.AnchorX[k] = part.AnchorX[i];
                    merged.AnchorY[k] = part.AnchorY[i];
                    merged.XPlacement[k] = part.XPlacement[i];
                    merged.YPlacement[k] = part.YPlacement[i];
                    merged.XAdvance[k] = part.XAdvance[i];
                    merged.ExitX[k] = part.ExitX[i];
                    merged.EntryX[k] = part.EntryX[i];
                    merged.CursiveY[k] = part.CursiveY[i];
                }
            }
            return merged;
        }

        /// <summary>
        /// <see cref="OpenTypeMarkPositioning.Substitute"/> over each segment under its own script,
        /// the output glyphs concatenated and each ligature's sources moved by its segment's start;
        /// a run in which no segment formed a ligature answers with no sources, as one segment does.
        /// </summary>
        private static OpenTypeMarkPositioning.Substituted SubstituteSegments(
            OpenTypeMarkPositioning gsub, List<ScriptSegment> segments, ushort[] inLogical,
            bool contextualAlternates, string[] joiningInLogical, bool ligatures,
            bool discretionary = false, bool historical = false)
        {
            if (segments.Count == 1)
                return gsub.Substitute(segments[0].Script, inLogical, contextualAlternates, joiningInLogical, ligatures,
                                       discretionary, historical);

            var glyphs = new List<ushort>(inLogical.Length);
            var sources = new List<int[]>(inLogical.Length);
            bool anyLigature = false;
            foreach (ScriptSegment s in segments)
            {
                var slice = new ushort[s.Count];
                Array.Copy(inLogical, s.Start, slice, 0, s.Count);
                string[] joiningSlice = null;
                if (joiningInLogical != null)
                {
                    joiningSlice = new string[s.Count];
                    Array.Copy(joiningInLogical, s.Start, joiningSlice, 0, s.Count);
                }

                OpenTypeMarkPositioning.Substituted part =
                    gsub.Substitute(s.Script, slice, contextualAlternates, joiningSlice, ligatures, discretionary, historical);
                for (int k = 0; k < part.Glyphs.Length; k++)
                {
                    glyphs.Add(part.Glyphs[k]);
                    if (part.Sources != null)
                    {
                        anyLigature = true;
                        var moved = new int[part.Sources[k].Length];
                        for (int j = 0; j < moved.Length; j++) moved[j] = part.Sources[k][j] + s.Start;
                        sources.Add(moved);
                    }
                    else
                    {
                        sources.Add(new[] { s.Start + k });
                    }
                }
            }
            return new OpenTypeMarkPositioning.Substituted(glyphs.ToArray(), anyLigature ? sources.ToArray() : null);
        }
    }
}
