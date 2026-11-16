// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// The PDF exporter's adapter onto <see cref="TextRunComposer"/>: deciding when a run needs an
    /// embedded Unicode font, collecting the code points a candidate face has to cover, and turning
    /// one logical-order token into visual-order glyph ids for the exporter's
    /// <see cref="PdfDocumentBuilder.EmbeddedFont"/> handle.
    /// </summary>
    /// <remarks>
    /// <para><b>The shape → bidi → glyph-id pipeline itself lives in Core</b>
    /// (<see cref="TextRunComposer.Compose"/>), shared with <c>DocxToPdf.Rendering.UnicodeText</c>,
    /// so both have the caller-supplied <see cref="BaseDirection"/> (a Latin word inside a Hebrew
    /// paragraph is not an LTR paragraph of its own) and the <b>code-point</b> walk (a
    /// supplementary-plane character is one glyph rather than two lone surrogates).</para>
    /// <para><b>Why <see cref="MissingGlyphPolicy.SubstituteSpace"/>.</b> It reproduces this
    /// exporter's historical pixels for an uncovered code point — a space, silently — while making
    /// the event visible on <see cref="TextRunComposer.LastCoverageError"/>. The exporter's own
    /// <c>UnrenderableText</c> policy ladder is what decides whether that matters; it runs before
    /// this method, off <see cref="NeedsUnicode"/> and font resolution.</para>
    /// </remarks>
    internal static class RtlText
    {
        /// <summary>True when the text has a character the standard-14 WinAnsi fonts
        /// cannot encode (code point &gt; 0xFF — e.g. Hebrew, Arabic, CJK).</summary>
        /// <remarks>
        /// Deliberately <b>not</b> <see cref="TextRunComposer.NeedsCompositeFont"/>, which also
        /// treats U+0080–U+009F as needing a composite font. That is the stricter and more correct
        /// threshold, but it is one half of a pair: this predicate and
        /// <see cref="CollectCodePoints"/> must agree, or a run enters the embedding path with an
        /// empty required-code-point set, resolution declines for want of anything to cover, and
        /// the exporter reports unrenderable text for a character WinAnsi can draw.
        /// </remarks>
        internal static bool NeedsUnicode(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text!) if (c > 0xFF) return true;
            return false;
        }

        /// <summary>Adds every &gt;0xFF code point in <paramref name="text"/> to <paramref name="sink"/>.</summary>
        /// <remarks>
        /// Real code points, not UTF-16 code units. Walking by code unit added the two halves of a
        /// surrogate pair — U+D835 and U+DC65 for 𝑥 — as separate values, and no font's cmap maps a
        /// lone surrogate, so <c>Ctx.GetUniFont</c>'s <c>Covers</c> loop could never succeed and
        /// every supplementary-plane run fell through to the standard-14 path as '?'. That is the
        /// same defect <c>TextRunComposer</c> fixes on the drawing side; the font is chosen here, so
        /// fixing only one end leaves the character undrawable for a different reason.
        /// </remarks>
        internal static void CollectCodePoints(string? text, HashSet<int> sink)
            => Collect(text, sink, above: 0xFF);

        /// <summary>
        /// Adds <b>every</b> code point in <paramref name="text"/> to <paramref name="sink"/> — the
        /// question a run that NAMED its own family asks, since the candidate face has to cover the
        /// run's Latin as well, and for pure-Latin text the &gt;0xFF set would be empty (which the
        /// resolver reads as "nothing to cover" and declines).
        /// </summary>
        internal static void CollectAllCodePoints(string? text, HashSet<int> sink)
            => Collect(text, sink, above: -1);

        /// <remarks>
        /// Through <see cref="TextRunComposer.CodePointsToCover"/>: the font has to cover what the
        /// run will be <b>drawn</b> as, and shaping substitutes Arabic presentation forms
        /// before a glyph is chosen. Collected from the source text, a face covering the letters and
        /// not the forms was accepted here and then drew none of them. It keeps the code-point walk
        /// this method exists for — an unpaired surrogate falls through as itself, so malformed input
        /// still terminates and is still reported under the value the document contains.
        /// </remarks>
        private static void Collect(string? text, HashSet<int> sink, int above)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (int cp in TextRunComposer.CodePointsToCover(text!))
                if (cp > above) sink.Add(cp);
        }

        internal sealed class ShapedRun
        {
            public ushort[] Gids = Array.Empty<ushort>();
            /// <summary>The run's mark attachment (<c>ShapedGlyphRun.Placements</c>), or <c>null</c>.</summary>
            public IReadOnlyList<GlyphPlacement>? Placements;
            /// <summary>How many glyphs are marks, which letter spacing does not follow.</summary>
            public int MarkCount;
            public double WidthPt;
        }

        /// <summary>
        /// Shapes + bidi-reorders <paramref name="text"/> into visual order, maps each glyph
        /// to <paramref name="font"/>'s ids, records the used glyphs on <paramref name="handle"/>
        /// (so the subset / /W / /ToUnicode stay minimal), and returns the visual gids plus
        /// the run's advance width in points. Uncovered characters fall back to the space glyph.
        /// </summary>
        internal static ShapedRun Shape(string? text, TrueTypeFontProgram font, double sizePt,
            PdfDocumentBuilder.EmbeddedFont handle)
            => Shape(text, font, sizePt, handle, BaseDirection.Auto);

        /// <summary>
        /// As <see cref="Shape(string, TrueTypeFontProgram, double, PdfDocumentBuilder.EmbeddedFont)"/>,
        /// with the paragraph's base direction supplied rather than auto-detected.
        ///
        /// <para><b>Why the direction has to come from the caller.</b> This is invoked once per
        /// whitespace-delimited token, and P2/P3 take the base direction from the first strong
        /// character — so every token resolved its <i>own</i> direction, in isolation. A Latin
        /// word inside a Hebrew paragraph therefore became an LTR paragraph of its own, and the
        /// paragraph's actual direction — which the caller knows and the token cannot — was
        /// discarded. <c>UnicodeBidi.Reorder(string, BaseDirection)</c> exists for exactly this.</para>
        /// <para>With <c>directionalOverride</c> the token lies inside an LRO or RLO and the direction
        /// is the override's: its characters are held in that direction whatever their own
        /// (<c>TextRunOptions.DirectionalOverride</c>).</para>
        /// </summary>
        internal static ShapedRun Shape(string? text, TrueTypeFontProgram font, double sizePt,
            PdfDocumentBuilder.EmbeddedFont handle, BaseDirection baseDirection, bool kerning = false,
            bool contextualAlternates = false, bool ligatures = false,
            bool discretionaryLigatures = false, bool historicalLigatures = false,
            bool directionalOverride = false)
        {
            var run = new ShapedRun();
            if (string.IsNullOrEmpty(text) || font == null) return run;

            var composed = TextRunComposer.Compose(text!, font, new TextRunOptions
            {
                BaseDirection = baseDirection,
                DirectionalOverride = directionalOverride,
                MissingGlyphPolicy = MissingGlyphPolicy.SubstituteSpace,
                Kerning = kerning,
                ContextualAlternates = contextualAlternates,
                Ligatures = ligatures,
                DiscretionaryLigatures = discretionaryLigatures,
                HistoricalLigatures = historicalLigatures,
            });

            // Note() takes the BASE letter, which is what Compose already put on the mapping — not
            // the presentation form that was drawn. Recording U+FE91 in /ToUnicode would make a
            // search for بيت miss the word visibly on the page and make copy-paste yield code
            // points no keyboard produces. A ligature glyph's Text is the letters it joined.
            foreach (var glyph in composed.Glyphs) handle?.Note(glyph.GlyphId, glyph.CodePoint, glyph.Text);

            run.Gids = composed.GlyphIds;
            run.Placements = composed.Placements;
            run.MarkCount = composed.MarkCount;
            run.WidthPt = composed.WidthAt(sizePt);
            return run;
        }
    }
}
