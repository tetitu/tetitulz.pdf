// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting
{
    /// <summary>
    /// The single answer to "how wide, how tall and how deep is this text in this style".
    ///
    /// <para>
    /// One place, because the WinAnsi and embedded-glyph paths would otherwise drift on what
    /// "height" means — and a fraction laid out with one definition and drawn with the other is
    /// wrong in a way that looks like a rounding error.
    /// </para>
    /// </summary>
    internal static class MathMeasure
    {
        /// <summary>
        /// Builds a glyph box for <paramref name="text"/>, choosing between the Standard-14 and the
        /// embedded-font paths per the characters actually present.
        ///
        /// <para>
        /// When nothing available covers the text, returns a <b>phantom of the estimated advance</b>
        /// and reports: the surrounding layout stays put, and the loss is visible to the caller
        /// rather than being a silent hole. Never a blank, never a substituted "?".
        /// </para>
        /// </summary>
        internal static MathBox Glyphs(string text, MathStyle style, DocxRenderContext context)
        {
            if (string.IsNullOrEmpty(text)) return MathHBox.Phantom(0, 0, 0);

            if (!UnicodeText.NeedsUnicode(text))
                return Standard14(text, style, context);

            return Embedded(text, style, context);
        }

        private static MathBox Standard14(string text, MathStyle style, DocxRenderContext context)
        {
            string font = context.StyleResolver.MapToPdfFontName(
                style.FontName, style.Bold, style.Italic);

            double width = context.StyleResolver.MeasureTextWidth(text, font, style.SizePt);
            var (height, depth) = VerticalOf(font, style.SizePt);

            return new MathGlyphBox
            {
                Text = text,
                Style = style.WithSize(style.SizePt),
                PdfFontName = font,
                Width = width,
                Height = height,
                Depth = depth,
            };
        }

        private static MathBox Embedded(string text, MathStyle style, DocxRenderContext context)
        {
            // Real code points, not UTF-16 code units. `text.Select(c => (int)c)` would turn every
            // supplementary-plane maths glyph — 𝑥 U+1D465, 𝔸 U+1D538, which Word's equation editor
            // writes directly into m:t — into two lone surrogates. No cmap maps a surrogate, so
            // `missing` would never be 0 even when the font covered the character: the term would
            // become a phantom of estimated width and the diagnostic name "U+D835, U+DC65", code
            // points that do not exist as characters.
            //
            // Through FontResolver.RequiredCodePoints, not a bare .Distinct(): this method re-checks
            // coverage itself after resolving, so filtering only inside ResolveEmbeddedFont would
            // have left the equation path answering the same question a second, stricter way — the
            // resolver would hand back a font and `missing` would still reject it. Whitespace is not
            // evidence of coverage; that helper's remarks say why, and why the failure is invisible.
            var codePoints = FontResolver.RequiredCodePoints(UnicodeText.CodePointsToCover(text));
            // A font embedded in the DOCX first, then the host's — the order body runs and list marks use. Without
            // the host's, an equation in a document embedding no font would draw its non-WinAnsi symbols
            // as reserved space on a machine that has a covering face.
            var program = context.ResolveEmbeddedFont(style.FontName, style.Bold, style.Italic, codePoints)
                          ?? FontResolver.Resolve(style.FontName, style.Bold, style.Italic, codePoints,
                                 context.Options.AllowRestrictedSystemFonts);

            if (program != null)
            {
                // A resolved font is not a covering font: FontResolver's cache key is lossy, so
                // verify each code point rather than trusting the lookup. Without this a missing
                // glyph silently becomes a space and the operators of an equation vanish.
                int missing = codePoints.Count(cp => !program.TryGetGlyph(cp, out _));
                if (missing == 0)
                {
                    var entry = context.RegisterEmbeddedFont(program);
                    var run = UnicodeText.Shape(text, program, style.SizePt);
                    run.RecordInto(entry.UsedGidToUnicode, entry.UsedGidToText);

                    double scale = style.SizePt / (double)PdfUnits.GlyphSpaceUnitsPerEm;
                    return new MathGlyphBox
                    {
                        Text = text,
                        Style = style,
                        Glyphs = run,
                        EmbeddedResName = entry.ResName,
                        Width = run.WidthPts,
                        Height = Math.Max(0, program.Ascent * scale),
                        Depth = Math.Max(0, -program.Descent * scale),
                    };
                }
            }

            // Nothing covers it. Reserve the space it would have taken and say which code point
            // went, so a caller can tell "the document had no such symbol" from "we could not
            // draw it".
            string missingList = string.Join(", ", codePoints
                .Where(cp => program == null || !program.TryGetGlyph(cp, out _))
                .Take(4)
                .Select(cp => "U+" + cp.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

            DocxDiagnostics.Report("MathMeasure.Glyphs",
                "no available font covers " + missingList
                    + " in an equation; the space was reserved and the glyphs omitted");

            double estimated = text.Length * style.SizePt * 0.5;
            var (h, d) = VerticalOf(StandardFonts.Helvetica, style.SizePt);
            return MathHBox.Phantom(estimated, h, d);
        }

        /// <summary>
        /// Height above and depth below the baseline for a Standard-14 font at a size. Falls back
        /// to Helvetica's proportions for anything not in the table, which is the closest thing to
        /// a neutral answer.
        /// </summary>
        internal static (double height, double depth) VerticalOf(string pdfFontName, double sizePt)
        {
            if (!Standard14Metrics.TryGetVerticalMetrics(pdfFontName,
                    out double ascender, out double descender, out _, out _))
                Standard14Metrics.TryGetVerticalMetrics(StandardFonts.Helvetica,
                    out ascender, out descender, out _, out _);

            double scale = sizePt / (double)PdfUnits.GlyphSpaceUnitsPerEm;
            return (Math.Max(0, ascender * scale), Math.Max(0, -descender * scale));
        }

        /// <summary>
        /// The maths axis at a size — the height a fraction bar and the centre of a delimiter sit
        /// at. Half the x-height when the font reports one.
        /// </summary>
        internal static double AxisHeight(string pdfFontName, double sizePt)
        {
            if (Standard14Metrics.TryGetVerticalMetrics(pdfFontName, out _, out _, out _, out double xHeight)
                && xHeight > 0)
                return xHeight / PdfUnits.GlyphSpaceUnitsPerEm * sizePt / 2.0;

            return sizePt * MathLayoutMetrics.AxisHeightFallbackEm;
        }
    }
}


