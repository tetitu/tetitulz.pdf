// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using AngleSharp.Dom;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// The atoms shared by every construct that becomes a <b>side-by-side band</b> — a flex row, a
    /// float band, a grid and a multi-column block all map onto a borderless table, the only thing
    /// in the Builder model that places blocks next to each other.
    /// </summary>
    /// <remarks>
    /// The mapping itself is documented and deliberate, and implemented once: separate copies of
    /// the direction test drift, and a real <c>&lt;table&gt;</c> reads the same one.
    /// </remarks>
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>
        /// A band's outer table: no rules between cells, no frame around it, and as tall as its
        /// items — none of the Builder's 16pt row floor or 4pt gap below, which a flex row, a grid,
        /// a float band or a column set does not have (an invoice's flex header drew its rule 7px
        /// lower than Chrome; a one-line flex row was 12px taller).
        /// </summary>
        private static M.Table NewBandTable()
            => new M.Table { BorderWidth = 0, OuterBorderOnly = true, MinRowHeightPt = 0, SpacingAfter = 0 };

        /// <summary>
        /// Mirrors a band's columns when the writing direction is right-to-left.
        /// </summary>
        /// <remarks>
        /// <b>The section fallback is unreachable today, and is kept anyway.</b> It looks like it
        /// matters: <c>Style.Direction</c> is nullable, and testing <c>st.Direction == RightToLeft</c>
        /// alone where a real <c>&lt;table&gt;</c> falls back to <c>sec.Direction</c> reads exactly
        /// like a document that mirrors its tables and not its flex rows. IT IS NOT. The root style is seeded from
        /// <c>HtmlToPdfOptions.DefaultTextDirection</c> and every derived style copies it, so
        /// <c>Direction</c> is never null by the time a band is mapped and the <c>??</c> never
        /// fires. Mutating it away reddens nothing — so it is no fix for a defect, since the defect
        /// does not exist.
        ///
        /// <para>It stays because it is the spelling the table site carries, because it
        /// is the safe reading if the seeding ever changes, and because one rule in one place is
        /// the point of this file. It is NOT covered by a test, and that is stated rather than
        /// implied.</para>
        ///
        /// <para>The inherited direction wins over the section's, which is the rule the table site
        /// states the reason for: a table inside <c>&lt;div dir="rtl"&gt;</c> mirrors even when the
        /// document as a whole is left-to-right.</para>
        /// </remarks>
        private static void ApplyBandDirection(M.Table table, Style containerStyle, M.Section sec)
        {
            if (BandIsRtl(containerStyle, sec))
                table.RtlColumns = true;
        }

        /// <summary>
        /// Whether a band in this container is right-to-left — the one reading of it, for the
        /// mirroring above and for anything that must place cells knowing the mirror will follow.
        /// </summary>
        private static bool BandIsRtl(Style containerStyle, M.Section sec)
            => (containerStyle.Direction ?? sec.Direction) == M.TextDirection.RightToLeft;

        /// <summary>
        /// Gives an empty band cell an empty paragraph.
        /// </summary>
        /// <remarks>
        /// A table cell with no blocks has no height, which collapses the row and takes the other
        /// cells' alignment with it. The placeholder is what keeps an empty grid track or an empty
        /// float column occupying its share.
        /// </remarks>
        private static void EnsureNotEmpty(M.TableCell cell)
        {
            if (cell.Blocks.Count == 0)
                cell.Blocks.Add(new M.Paragraph { Runs = { new M.TextRun { Text = "" } } });
        }

        /// <summary>
        /// A cell that holds a track's width and nothing else — free space in a flex row or a line
        /// of floats, an empty grid track — so it adds nothing to its row's height: no padding, and
        /// no empty paragraph, which the exporter gives a 14pt line. With them a two-float grid
        /// line was 26pt tall for one 8.6pt line of text.
        /// </summary>
        private static M.TableCell NewFillerCell() => new M.TableCell { InnerPaddingPt = 0 };

        /// <summary>The width a band has to divide up: the page less its margins.</summary>
        private static double SectionContentWidth(M.Section sec)
            => System.Math.Max(1, sec.PageWidthPt - sec.MarginLeft - sec.MarginRight);

        /// <summary>
        /// True when <paramref name="el"/>'s <c>display</c> is <paramref name="keyword"/> or its
        /// <c>inline-</c> form — the shape every layout-container predicate here needs.
        /// </summary>
        /// <remarks>
        /// <c>IsFlexContainer</c> and <c>IsGridContainer</c> were the same four lines with one word
        /// changed, and had already drifted apart in spelling (one trimmed twice, the other once).
        /// The <c>inline-</c> pair is the part worth having in one place: both forms establish the
        /// container, and the difference between them is how the box sits in its parent's flow,
        /// which this engine does not model either way.
        /// </remarks>
        private static bool DisplayIs(IElement el, string keyword)
        {
            string? display = StyleValue(el, "display");
            if (display == null) return false;
            string v = display.Trim();
            return Eq(v, keyword) || Eq(v, "inline-" + keyword);
        }
    }
}
