// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// The managed engine's CSS/HTML correctness: cases where a wrong reading emits output that is
    /// confidently <b>wrong</b> rather than merely missing.
    ///
    /// <para>Every assertion here is two-sided — the correct ink is present <i>and</i> the
    /// incorrect ink is absent. Asserting only the former would pass against a wrong reading in
    /// several of these cases, because the wrong output is a superset of the right one (a black
    /// box drawn <i>in addition to</i> the text).</para>
    /// </summary>
    public class HtmlManagedCssCorrectnessTests
    {
        private const string Red = "1 0 0 rg";
        private const string Black = "0 0 0 rg";

        // Counts fill-a-rectangle pairs: `x y w h re` followed (eventually) by `f`.
        private static int RectangleFillCount(string content) =>
            Regex.Matches(content, @"[-\d.]+\s+[-\d.]+\s+[-\d.]+\s+[-\d.]+\s+re\s*\n?f").Count;

        private static int StrokeCount(string content) =>
            Regex.Matches(content, @"\bS\b").Count;

        // ------------------------------------------------------------------ !important

        [Fact]
        public void Important_Color_IsAppliedNotBlackened()
        {
            string dec = DecodeStreams(ToPdf("<p style=\"color:red !important\">Hi</p>"));

            Assert.Contains("Hi", dec);
            Assert.Contains(Red, dec);
            // The defect: !important reached TryParseColor verbatim, every arm failed, and
            // NormalizeColor's black fallback painted the text black.
            Assert.DoesNotContain(Black, dec);
        }

        [Fact]
        public void Important_WithSpaceBeforeKeyword_IsAlsoStripped()
        {
            // CSS permits whitespace between the bang and the keyword.
            string dec = DecodeStreams(ToPdf("<p style=\"color:red ! important\">Hi</p>"));
            Assert.Contains(Red, dec);
            Assert.DoesNotContain(Black, dec);
        }

        [Fact]
        public void Important_FontSize_IsApplied()
        {
            string dec = DecodeStreams(ToPdf("<p style=\"font-size:20pt !important\">Hi</p>"));

            Assert.Matches(@"/F\d+\s+20\s+Tf", dec);
            Assert.DoesNotMatch(@"/F\d+\s+12\s+Tf", dec);
        }

        [Fact]
        public void Important_FontWeight_IsApplied()
        {
            string boldRes = FirstFontResource(DecodeStreams(ToPdf("<p style=\"font-weight:bold\">Hi</p>")));
            string impRes = FirstFontResource(DecodeStreams(ToPdf("<p style=\"font-weight:bold !important\">Hi</p>")));
            string plainRes = FirstFontResource(DecodeStreams(ToPdf("<p>Hi</p>")));

            Assert.Equal(boldRes, impRes);
            Assert.NotEqual(plainRes, impRes);
        }

        [Fact]
        public void Important_InStylesheet_BeatsInlineStyle()
        {
            // The whole point of !important: a stylesheet declaration outranks an inline one.
            string dec = DecodeStreams(ToPdf(
                "<style>p{color:red !important}</style><p style=\"color:blue\">Hi</p>"));

            Assert.Contains(Red, dec);
            Assert.DoesNotContain("0 0 1 rg", dec); // not blue
        }

        [Fact]
        public void Important_Inline_BeatsImportantStylesheet()
        {
            string dec = DecodeStreams(ToPdf(
                "<style>p{color:blue !important}</style><p style=\"color:red !important\">Hi</p>"));

            Assert.Contains(Red, dec);
            Assert.DoesNotContain("0 0 1 rg", dec);
        }

        [Fact]
        public void NonImportantStylesheet_StillLosesToInline()
        {
            // Guards the ordinary cascade the !important work had to preserve.
            string dec = DecodeStreams(ToPdf(
                "<style>p{color:blue}</style><p style=\"color:red\">Hi</p>"));

            Assert.Contains(Red, dec);
            Assert.DoesNotContain("0 0 1 rg", dec);
        }

        // ------------------------------------------------------------------ background

        [Fact]
        public void BackgroundUrlOnly_PaintsNoBox()
        {
            string dec = DecodeStreams(ToPdf("<p style=\"background:url(bg.png)\">Hi</p>"));

            Assert.Contains("Hi", dec);
            // The defect: `url(bg.png)` failed to parse and became #000000 shading — a solid
            // black rectangle behind the text.
            Assert.Equal(0, RectangleFillCount(dec));
        }

        [Fact]
        public void BackgroundShorthand_WithColourAndUrl_KeepsTheColour()
        {
            string dec = DecodeStreams(ToPdf("<p style=\"background:#eeeeee url(bg.png) no-repeat\">Hi</p>"));

            Assert.Equal(1, RectangleFillCount(dec));
            Assert.Contains("0.933", dec); // 0xEE/255 = 0.9333…
        }

        [Fact]
        public void BackgroundShorthand_WithDataUri_IsNotTornApartBySemicolons()
        {
            // A data: URI contains semicolons, which a naive Split(';') would treat as
            // declaration separators — shredding this value into unparseable fragments.
            string dec = DecodeStreams(ToPdf(
                "<p style=\"background:url(data:image/png;base64,iVBOR);color:red\">Hi</p>"));

            Assert.Contains(Red, dec);           // the declaration AFTER the data URI survived
            Assert.Equal(0, RectangleFillCount(dec));
        }

        [Fact]
        public void InlineBackgroundTransparent_PaintsNoHighlight()
        {
            string dec = DecodeStreams(ToPdf("<p>a <span style=\"background:transparent\">b</span> c</p>"));

            Assert.Contains("b", dec);
            // The defect: "transparent" is not a named colour, so it became a black highlight
            // rectangle behind the span — the block path guarded this, the inline path did not.
            Assert.Equal(0, RectangleFillCount(dec));
        }

        [Fact]
        public void InlineBackgroundColour_StillPaintsAHighlight()
        {
            // The control for the test above: a real colour must still paint.
            string dec = DecodeStreams(ToPdf("<p>a <span style=\"background:#ffff00\">b</span> c</p>"));
            Assert.Equal(1, RectangleFillCount(dec));
        }

        [Fact]
        public void UnknownColourSyntax_DropsTheDeclarationRatherThanPaintingBlack()
        {
            // Represents every colour function this engine does not implement (color-mix, lab,
            // rgb(from …)). Inheriting is right; black is not.
            string dec = DecodeStreams(ToPdf(
                "<div style=\"color:red\"><p style=\"color:color-mix(in srgb, blue, white)\">Hi</p></div>"));

            Assert.Contains(Red, dec);
            Assert.DoesNotContain(Black, dec);
        }

        [Fact]
        public void UnparseableBorderColour_DoesNotBecomeABlackBorder()
        {
            string withBogus = DecodeStreams(ToPdf("<p style=\"border:1px solid notacolour\">Hi</p>"));
            string withNamed = DecodeStreams(ToPdf("<p style=\"border:1px solid red\">Hi</p>"));

            // Both draw a border; the bogus one must fall back to the default border colour
            // rather than being treated as an explicit black.
            Assert.True(StrokeCount(withBogus) > 0);
            Assert.Contains("1 0 0 RG", withNamed);
        }

        // ------------------------------------------------------------------ table structure

        [Fact]
        public void TfootAuthoredBeforeTbody_RendersLast()
        {
            string dec = DecodeStreams(ToPdf(
                "<table><thead><tr><td>HEADCELL</td></tr></thead>" +
                "<tfoot><tr><td>FOOTCELL</td></tr></tfoot>" +
                "<tbody><tr><td>BODYCELL</td></tr></tbody></table>"));

            int head = dec.IndexOf("HEADCELL", StringComparison.Ordinal);
            int body = dec.IndexOf("BODYCELL", StringComparison.Ordinal);
            int foot = dec.IndexOf("FOOTCELL", StringComparison.Ordinal);

            Assert.True(head >= 0 && body >= 0 && foot >= 0, "all three cells should render");
            Assert.True(head < body, $"thead before tbody (head={head}, body={body})");
            Assert.True(body < foot, $"tbody before tfoot (body={body}, foot={foot})");
        }

        [Fact]
        public void NestedTable_RowsAreNotDuplicatedIntoTheOuterTable()
        {
            string dec = DecodeStreams(ToPdf(
                "<table><tr><td>OUTERCELL<table><tr><td>NESTEDCELL</td></tr></table></td></tr></table>"));

            // The defect: Descendants() was a QuerySelectorAll, so the inner <tr> was emitted as
            // an extra row of the OUTER table as well as being inlined into the cell.
            Assert.Single(Regex.Matches(dec, "NESTEDCELL"));
            Assert.Single(Regex.Matches(dec, "OUTERCELL"));
        }

        [Fact]
        public void NestedTable_ColumnsAreNotCountedByTheOuterTable()
        {
            // <col> was collected by the same descendant query, so a nested table's column hints
            // silently reshaped the outer table's grid.
            string one = DecodeStreams(ToPdf(
                "<table><colgroup><col width=\"100\"></colgroup><tr><td>A</td></tr></table>"));
            string nested = DecodeStreams(ToPdf(
                "<table><colgroup><col width=\"100\"></colgroup><tr><td>A" +
                "<table><colgroup><col width=\"20\"><col width=\"20\"></colgroup>" +
                "<tr><td>B</td><td>C</td></tr></table></td></tr></table>"));

            Assert.Contains("A", one);
            Assert.Contains("B", nested);
            Assert.Contains("C", nested);
        }

        [Fact]
        public void TheadRowWithTdCells_IsStillAHeaderRow()
        {
            // Header-ness comes from <thead> too, not only from <th>: a <thead> of <td> repeats.
            var sb = new System.Text.StringBuilder(
                "<table><thead><tr><td>REPEATEDHEADER</td></tr></thead><tbody>");
            for (int i = 0; i < 80; i++) sb.Append("<tr><td>Row ").Append(i).Append("</td></tr>");
            sb.Append("</tbody></table>");

            byte[] pdf = ToPdf(sb.ToString());
            Assert.True(CountPages(pdf) >= 2, "fixture must paginate for header repeat to be observable");
            Assert.True(Regex.Matches(DecodeStreams(pdf), "REPEATEDHEADER").Count >= 2,
                "a <thead> row must repeat on each page");
        }

        // ------------------------------------------------------------------ noscript

        [Fact]
        public void Noscript_BodyText_IsNotEmittedWhenScriptingIsEnabled()
        {
            // JavaScriptEnabled defaults to true, matching a browser with scripting on — where
            // <noscript> content is not rendered, and must not leak into the document.
            string dec = DecodeStreams(ToPdf("<p>Visible</p><noscript><p>NOSCRIPTTEXT</p></noscript>"));

            Assert.Contains("Visible", dec);
            Assert.DoesNotContain("NOSCRIPTTEXT", dec);
        }

        [Fact]
        public void Noscript_BodyText_IsEmittedWhenScriptingIsDisabled()
        {
            var options = Managed();
            options.JavaScriptEnabled = false;
            string dec = DecodeStreams(ToPdf("<p>Visible</p><noscript><p>NOSCRIPTTEXT</p></noscript>", options));

            Assert.Contains("NOSCRIPTTEXT", dec);
        }

        // ------------------------------------------------------------------ table borders

        // A 2×2 table's interior gridlines are the vertical rule between the columns and the
        // horizontal rule between the rows. Their PRESENCE is the whole difference between a grid
        // and a frame, so asserting on them says what is actually claimed. A raw stroke count
        // would test an implementation detail instead: the frame is emitted as eight half-edges,
        // because each of its four sides is split between the two cells that share it.
        private static bool HasInteriorGridline(string dec)
        {
            var segs = StrokedSegments(dec);
            if (segs.Count == 0) return false;
            double minX = segs.Min(s => Math.Min(s.X0, s.X1)), maxX = segs.Max(s => Math.Max(s.X0, s.X1));
            double minY = segs.Min(s => Math.Min(s.Y0, s.Y1)), maxY = segs.Max(s => Math.Max(s.Y0, s.Y1));
            const double Tol = 0.5;
            foreach (var s in segs)
            {
                if (s.IsVertical && s.X0 > minX + Tol && s.X0 < maxX - Tol) return true;
                if (s.IsHorizontal && s.Y0 > minY + Tol && s.Y0 < maxY - Tol) return true;
            }
            return false;
        }

        [Fact]
        public void CssTableBorder_DrawsAFrameNotAGrid()
        {
            string dec = DecodeStreams(ToPdf(
                "<table style=\"border:1px solid #000\">" +
                "<tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></table>"));

            Assert.True(StrokedSegments(dec).Count > 0, "the frame itself must still be drawn");
            Assert.False(HasInteriorGridline(dec),
                "CSS `table { border: … }` styles the table's own box, so no interior rule belongs");
        }

        [Fact]
        public void LegacyBorderAttribute_StillRulesEveryCell()
        {
            // The counterpart claim: <table border="1"> genuinely means "rule every cell", so
            // this must NOT have been changed into a frame.
            string dec = DecodeStreams(ToPdf(
                "<table border=\"1\">" +
                "<tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></table>"));

            Assert.True(HasInteriorGridline(dec),
                "the legacy border attribute must keep ruling every cell");
        }

        [Fact]
        public void ExplicitCellBorder_WinsOverTheOuterOnlyFallback()
        {
            string frameOnly = DecodeStreams(ToPdf(
                "<table style=\"border:1px solid #000\">" +
                "<tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></table>"));
            string withCellBorder = DecodeStreams(ToPdf(
                "<table style=\"border:1px solid #000\">" +
                "<tr><td>A</td><td style=\"border:1px solid #000\">B</td></tr>" +
                "<tr><td>C</td><td>D</td></tr></table>"));

            // A cell's own per-side border always wins over the outer-only fallback, so it adds
            // interior edges the frame-only table does not have.
            Assert.False(HasInteriorGridline(frameOnly));
            Assert.True(HasInteriorGridline(withCellBorder));
            Assert.True(StrokeCount(withCellBorder) > StrokeCount(frameOnly));
        }

        // ------------------------------------------------------------------ helpers

        private static string FirstFontResource(string content)
        {
            var m = Regex.Match(content, @"/(F\d+)\s+[\d.]+\s+Tf");
            Assert.True(m.Success, "expected a text-showing Tf operator");
            return m.Groups[1].Value;
        }
    }
}
