// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Per-side borders on a table <b>cell</b> — the half of the border-suppression defect that
    /// lives on the cell path rather than the block one.
    ///
    /// <para>Two things had to be true at once for it to bite. <c>BuildCell</c> read the
    /// <c>border</c> shorthand and nothing else, so a per-side declaration never reached it; and
    /// even routed through, a suppressed edge cannot be spelled <c>null</c> here, because
    /// <c>TableRenderer</c> draws every edge as <c>Cell.BorderX ?? Fallback(…)</c> — on a cell,
    /// <c>null</c> means "use the table default", so suppressing with it hands the edge straight
    /// back. <c>Border.None</c> is the model's word for it.</para>
    ///
    /// <para>Each test names the <b>geometry</b> it expects rather than a bare stroke count, and
    /// each carries its own control, so it fails if the suppression stops working <i>and</i> if it
    /// starts erasing edges nobody suppressed.</para>
    /// </summary>
    public class HtmlTableCellBorderTests
    {
        private static int StrokeCount(string content) => Regex.Matches(content, @"\bS\b").Count;

        private static List<double> VerticalRuleXs(string content) =>
            StrokedSegments(content).Where(s => s.IsVertical).Select(s => s.X0)
                .Distinct().OrderBy(x => x).ToList();

        /// <summary>
        /// The finding's own example, and the reason it matters: this is how essentially every
        /// styled table in the wild suppresses an interior edge. The cascade fold puts both
        /// declarations on the one element, exactly as here.
        /// </summary>
        [Fact]
        public void CellBorderLeftNone_CancelsTheShorthandForThatEdge()
        {
            string ruled = DecodeStreams(ToPdf(
                "<style>td{border:1px solid #ccc}</style>" +
                "<table style='width:100%;table-layout:fixed;border-spacing:0'><tr><td>A</td><td>B</td></tr></table>"));
            string leftless = DecodeStreams(ToPdf(
                "<style>td{border:1px solid #ccc} td.first{border-left:none}</style>" +
                "<table style='width:100%;table-layout:fixed;border-spacing:0'><tr><td class=\"first\">A</td><td>B</td></tr></table>"));

            var ruledXs = VerticalRuleXs(ruled);
            var leftlessXs = VerticalRuleXs(leftless);

            // A stated width and fixed layout, so the columns do not move (a table with no width is
            // as wide as its content, and would narrow with the border's room): an edge's border takes room in its cell, and
            // under auto layout the leftless cell's content would re-divide the width.
            // Three distinct vertical rules across two cells: the table's left edge, the shared
            // interior edge, and the right edge. Suppressing the first cell's left edge removes
            // the LEFT-MOST of them, which is the claim a stroke count cannot make.
            Assert.Equal(3, ruledXs.Count);
            Assert.Equal(2, leftlessXs.Count);
            Assert.Equal(ruledXs.Skip(1), leftlessXs);
        }

        /// <summary>
        /// The counterpart every suppression test needs: a cell that declares no per-side override
        /// must keep all four edges. Collapsing "absent" into "suppressed" the other way would
        /// erase every cell border in the document.
        /// </summary>
        [Fact]
        public void CellWithNoPerSideDeclaration_KeepsEveryEdge()
        {
            string dec = DecodeStreams(ToPdf(
                "<style>td{border:1px solid #ccc}</style>" +
                "<table style='border-spacing:0'><tr><td>A</td><td>B</td></tr></table>"));

            Assert.Equal(3, VerticalRuleXs(dec).Count);
        }

        /// <summary>
        /// A suppressed edge must not fall back to the <b>table's</b> border either. This is the
        /// case that fails when a cell edge is suppressed with <c>null</c> instead of
        /// <c>Border.None</c>: the renderer's <c>?? Fallback(…)</c> silently restores it, and every
        /// other assertion in this file still passes.
        /// </summary>
        [Fact]
        public void SuppressedCellEdge_DoesNotFallBackToTheTableBorder()
        {
            string ruled = DecodeStreams(ToPdf(
                "<style>td{border:1px solid #ccc}</style>" +
                "<table border=\"1\"><tr><td>A</td><td>B</td></tr></table>"));
            string leftless = DecodeStreams(ToPdf(
                "<style>td{border:1px solid #ccc} td.first{border-left:none}</style>" +
                "<table border=\"1\"><tr><td class=\"first\">A</td><td>B</td></tr></table>"));

            Assert.True(StrokeCount(leftless) < StrokeCount(ruled),
                $"the table's own border must not resurrect the suppressed edge " +
                $"(ruled={StrokeCount(ruled)}, leftless={StrokeCount(leftless)})");
        }

        /// <summary>
        /// <c>border:none</c> on the cell suppresses all four, and — the half that distinguishes
        /// this from "assign null" — it survives the table-level fallback that would otherwise
        /// re-draw every one of them.
        /// </summary>
        [Fact]
        public void CellBorderNone_SuppressesEveryEdgeEvenUnderARuledTable()
        {
            string ruled = DecodeStreams(ToPdf(
                "<table border=\"1\"><tr><td>A</td><td>B</td></tr></table>"));
            string bare = DecodeStreams(ToPdf(
                "<table border=\"1\"><tr><td style=\"border:none\">A</td><td>B</td></tr></table>"));

            Assert.True(StrokeCount(bare) < StrokeCount(ruled),
                $"border:none on a cell must beat the table's default " +
                $"(ruled={StrokeCount(ruled)}, bare={StrokeCount(bare)})");
        }

        /// <summary>
        /// An explicit per-side border still draws — the tri-state must not turn every declaration
        /// into a suppression.
        ///
        /// <para>Asserted on the edge's <b>colour</b>, not on a stroke count, and the reason is a
        /// trap worth recording: <c>Model.Table.BorderWidth</c> defaults to <c>0.5</c>, so a
        /// <c>&lt;table&gt;</c> that declares nothing at all already rules every cell. A count
        /// therefore cannot distinguish "the declaration was honoured" from "the fallback drew the
        /// same edge" — the declaration <i>replaces</i> an edge rather than adding one.</para>
        /// </summary>
        [Fact]
        public void CellPerSideBorder_IsDrawnWhenItNamesAWidth()
        {
            string plain = DecodeStreams(ToPdf("<table><tr><td>A</td><td>B</td></tr></table>"));
            string withEdge = DecodeStreams(ToPdf(
                "<table><tr><td style=\"border-left:3px solid #ff0000\">A</td><td>B</td></tr></table>"));

            Assert.DoesNotContain("1 0 0 RG", plain);
            Assert.Contains("1 0 0 RG", withEdge);
        }
    }
}
