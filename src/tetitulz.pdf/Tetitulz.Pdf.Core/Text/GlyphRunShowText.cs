// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The content-stream operators that show a composed run's glyphs (Identity-H, two-byte codes)
    /// where its placements put them — <b>the one writer</b> of a <see cref="ShapedGlyphRun"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Without placements it is a plain <c>&lt;…&gt; Tj</c></b>, byte for byte.</para>
    /// <para><b>With them</b> a horizontal offset is a <c>TJ</c> adjustment either side of the glyph
    /// — before it to move it, after it to move back and to take off the advance a mark does not
    /// take — and a vertical one is a text rise (<c>Ts</c>) round the glyph alone, restored to
    /// <c>rise</c> after it. A <c>TJ</c> number is in thousandths of text space, which is glyph
    /// space, so the horizontal part needs no font size; a rise is in unscaled text space, so the
    /// vertical part does.</para>
    /// <para><b>Letter spacing</b> — a <c>Tc</c> the producer has set — is taken back after each
    /// mark, by <c>Tc</c>·1000 / size thousandths (horizontal scaling multiplies both alike), so
    /// it falls once after a letter and its marks as CSS spaces a grapheme cluster.</para>
    /// </remarks>
    public static class GlyphRunShowText
    {
        /// <summary>
        /// The operators showing <paramref name="run"/> at <paramref name="fontSize"/> (the <c>Tf</c>
        /// size), the text rise otherwise being <paramref name="rise"/> and the character spacing
        /// <paramref name="charSpacing"/> (the <c>Tc</c> in force).
        /// </summary>
        public static string Operators(ShapedGlyphRun run, double fontSize, double rise = 0, double charSpacing = 0)
            => run == null ? string.Empty : Operators(run.GlyphIds, run.Placements, fontSize, rise, null, charSpacing);

        /// <summary>
        /// The operators showing <paramref name="glyphIds"/> with <paramref name="placements"/> (one
        /// per glyph, or <c>null</c> for none) at <paramref name="fontSize"/>; empty for no glyphs.
        /// No trailing line break.
        /// </summary>
        /// <param name="glyphIds">The glyphs, in visual order.</param>
        /// <param name="placements">One per glyph, or <c>null</c>.</param>
        /// <param name="fontSize">The <c>Tf</c> size.</param>
        /// <param name="rise">The text rise otherwise in force.</param>
        /// <param name="adjustmentAfter">
        /// A <c>TJ</c> adjustment to write after each glyph, one per glyph or <c>null</c> — the way a
        /// producer spaces words in a two-byte font, whose codes <c>Tw</c> never reaches.
        /// </param>
        /// <param name="charSpacing">The <c>Tc</c> in force, taken back after each mark.</param>
        public static string Operators(IReadOnlyList<ushort> glyphIds, IReadOnlyList<GlyphPlacement> placements,
                                       double fontSize, double rise = 0, IReadOnlyList<double> adjustmentAfter = null,
                                       double charSpacing = 0)
        {
            if (glyphIds == null || glyphIds.Count == 0) return string.Empty;
            var sb = new StringBuilder(glyphIds.Count * 4 + 8);
            if (adjustmentAfter != null && adjustmentAfter.Count != glyphIds.Count) adjustmentAfter = null;
            if ((placements == null || placements.Count != glyphIds.Count) && adjustmentAfter == null)
            {
                sb.Append('<');
                foreach (ushort g in glyphIds) Hex(sb, g);
                return sb.Append("> ").Append(PdfOperators.ShowText).ToString();
            }

            var array = new StringBuilder();
            bool open = false;        // a hex string is open in `array`
            double pending = 0;       // an adjustment not yet written

            for (int i = 0; i < glyphIds.Count; i++)
            {
                GlyphPlacement p = placements != null && placements.Count == glyphIds.Count ? placements[i] : default;
                double extra = adjustmentAfter?[i] ?? 0;
                if (p.IsMark && charSpacing != 0 && fontSize > 0)
                    extra += charSpacing / fontSize * PdfUnits.GlyphSpaceUnitsPerEm;
                if (p.YOffset != 0)
                {
                    Flush(sb, array, ref open, ref pending);
                    sb.Append(Num(rise + p.YOffset / PdfUnits.GlyphSpaceUnitsPerEm * fontSize)).Append(' ')
                      .Append(PdfOperators.SetTextRise).Append('\n');
                    var single = new StringBuilder();
                    bool singleOpen = false;
                    double before = -p.XOffset;
                    Glyph(single, ref singleOpen, ref before, glyphIds[i]);
                    double after = p.XOffset - p.AdvanceAdjustment + extra;
                    Flush(sb, single, ref singleOpen, ref after);
                    sb.Append(Num(rise)).Append(' ').Append(PdfOperators.SetTextRise).Append('\n');
                    continue;
                }
                pending -= p.XOffset;
                Glyph(array, ref open, ref pending, glyphIds[i]);
                pending += p.XOffset - p.AdvanceAdjustment + extra;
            }
            Flush(sb, array, ref open, ref pending);
            if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
            return sb.ToString();
        }

        /// <summary>Adds one glyph to an array being built, writing any pending adjustment before it.</summary>
        private static void Glyph(StringBuilder array, ref bool open, ref double pending, ushort glyph)
        {
            if (pending != 0)
            {
                if (open) { array.Append('>'); open = false; }
                if (array.Length > 0) array.Append(' ');
                array.Append(Num(pending)).Append(' ');
                pending = 0;
            }
            if (!open) { array.Append('<'); open = true; }
            Hex(array, glyph);
        }

        /// <summary>Writes the array built so far — as <c>Tj</c> when it holds one string and no adjustment — and empties it.</summary>
        private static void Flush(StringBuilder sb, StringBuilder array, ref bool open, ref double pending)
        {
            if (open) { array.Append('>'); open = false; }
            if (pending != 0)
            {
                if (array.Length > 0) array.Append(' ');
                array.Append(Num(pending));
                pending = 0;
            }
            if (array.Length == 0) return;
            string body = array.ToString();
            bool plain = body[0] == '<' && body.IndexOf('>') == body.Length - 1;
            if (plain) sb.Append(body).Append(' ').Append(PdfOperators.ShowText).Append('\n');
            else sb.Append('[').Append(body).Append("] ").Append(PdfOperators.ShowTextArray).Append('\n');
            array.Clear();
        }

        private static void Hex(StringBuilder sb, ushort g) => sb.Append(g.ToString("X4", CultureInfo.InvariantCulture));

        private static string Num(double v) => PdfRealFormat.Format(System.Math.Round(v, 3));
    }
}
