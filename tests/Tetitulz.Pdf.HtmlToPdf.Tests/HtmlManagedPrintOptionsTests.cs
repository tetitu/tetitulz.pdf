// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// The two print options that were read only on the Playwright path and were silently inert
    /// under <see cref="HtmlRenderEngine.Managed"/>: <see cref="HtmlToPdfOptions.Scale"/> and
    /// <see cref="HtmlToPdfOptions.PrintBackground"/>.
    ///
    /// <para>Every <c>PrintBackground</c> case here comes in a pair — the option off and the same
    /// markup with it on. Without the positive control the "no background was painted" assertion
    /// passes with the whole shading feature deleted, which is the failure
    /// <c>Builder.Tests.ParagraphBoxTests</c> was written to document.</para>
    /// </summary>
    public class HtmlManagedPrintOptionsTests
    {
        private const double NominalWidthPt = 612;   // 8.5in, the default PageWidth
        private const double NominalHeightPt = 792;  // 11in

        private static readonly Regex MediaBoxArray =
            new(@"/MediaBox\s*\[([^\]]*)\]", RegexOptions.Compiled);

        private static double[] FirstMediaBox(byte[] pdf)
        {
            var m = MediaBoxArray.Match(Encoding.Latin1.GetString(pdf));
            Assert.True(m.Success, "no /MediaBox in the output");
            return m.Groups[1].Value
                .Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                .ToArray();
        }

        private static HtmlToPdfOptions Scaled(double scale)
        {
            var o = Managed();
            o.Scale = scale;
            return o;
        }

        private static HtmlToPdfOptions Backgrounds(bool on)
        {
            var o = Managed();
            o.PrintBackground = on;
            return o;
        }

        // ------------------------------------------------------------------ Scale

        [Fact]
        public void Managed_Scale_KeepsThePaperNominalAndShrinksTheContent()
        {
            byte[] pdf = ToPdf("<p>Hi</p>", Scaled(0.5));

            // Chromium's semantics, and the whole point of dividing the Section by the same
            // factor the exporter multiplies the paper by: the sheet is still Letter.
            double[] box = FirstMediaBox(pdf);
            Assert.Equal(NominalWidthPt, box[2], 3);
            Assert.Equal(NominalHeightPt, box[3], 3);

            Assert.Contains("0.5 0 0 0.5 0 0 cm", DecodeStreams(pdf));
        }

        [Fact]
        public void Managed_ScaleDefault_EmitsNoScalingMatrix()
        {
            // The control for the test above. Text alone never emits `cm` (only images and the
            // page scale do), so its absence here is a real statement about the default path.
            byte[] pdf = ToPdf("<p>Hi</p>");

            double[] box = FirstMediaBox(pdf);
            Assert.Equal(NominalWidthPt, box[2], 3);
            Assert.DoesNotContain(" cm", DecodeStreams(pdf));
        }

        [Fact]
        public void Managed_Scale_LeavesTheMarginsWhereTheyAreOnThePaper()
        {
            // The exporter's `cm` scales the margins too, so the mapper has to divide them along
            // with the page box. Miss that and a 0.4in margin prints at 0.2in.
            byte[] plainPdf = ToPdf("<p>Hi</p>");
            byte[] halfPdf = ToPdf("<p>Hi</p>", Scaled(0.5));
            string plain = DecodeStreams(plainPdf), half = DecodeStreams(halfPdf);

            // The device factor is read back out of the file rather than assumed to be 0.5:
            // hard-coding it would make this pass even if the export never scaled at all, since
            // the mapper's division alone would then account for the difference.
            double plainOnPaper = XOf(plain, "Hi") * PageScaleFactor(plain);
            double halfOnPaper = XOf(half, "Hi") * PageScaleFactor(half);

            Assert.Equal(NominalWidthPt, FirstMediaBox(halfPdf)[2], 3);
            Assert.Equal(plainOnPaper, halfOnPaper, 3);
            Assert.True(plainOnPaper > 1, "the fixture has no left margin, so the assertion above is vacuous");
        }

        private static readonly Regex LeadingScaleMatrix =
            new(@"(-?[\d.]+)\s+0\s+0\s+(-?[\d.]+)\s+0\s+0\s+cm", RegexOptions.Compiled);

        /// <summary>The page's own device scale, taken from the matrix it wrote; 1 when it wrote none.</summary>
        private static double PageScaleFactor(string decodedContent)
        {
            var m = LeadingScaleMatrix.Match(decodedContent);
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1.0;
        }

        [Fact]
        public void Managed_ScaleBelowOne_FitsMoreContentPerPage()
        {
            string html = "<body>" + string.Concat(Enumerable.Repeat("<p>Line of body text.</p>", 90)) + "</body>";

            int plainPages = CountPages(ToPdf(html));
            int scaledPages = CountPages(ToPdf(html, Scaled(0.5)));

            Assert.True(plainPages > 1, $"the fixture must overflow one page, got {plainPages}");
            Assert.True(scaledPages < plainPages,
                $"scaling to 0.5 should fit more per page: {scaledPages} pages vs {plainPages}");
        }

        // ------------------------------------------------------------------ PrintBackground

        private const string ShadedMarkup =
            "<div style=\"background:#ff0000\">block</div>" +
            "<p>a <mark>marked</mark> b</p>" +
            "<table><tr><td bgcolor=\"#00ff00\">cell</td></tr></table>";

        [Fact]
        public void Managed_PrintBackgroundFalse_PaintsNoBackgroundBoxes()
        {
            string dec = DecodeStreams(ToPdf(ShadedMarkup, Backgrounds(false)));

            Assert.Contains("block", dec);   // the content itself is untouched
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(dec));
        }

        [Fact]
        public void Managed_PrintBackgroundTrue_PaintsThem()
        {
            string dec = DecodeStreams(ToPdf(ShadedMarkup, Backgrounds(true)));

            // Block shading, the <mark> highlight and the cell fill — three distinct sources, so
            // the test above cannot pass just because one of them was never wired up.
            Assert.True(PdfContentProbe.RectangleFillCount(dec) >= 3,
                $"expected three background fills, got {PdfContentProbe.RectangleFillCount(dec)}");
        }

        [Fact]
        public void Managed_PrintBackgroundFalse_KeepsBordersAndRules()
        {
            // Borders are not backgrounds: Chromium keeps them under printBackground:false, and a
            // scrub that reached them would silently strip every table grid and paragraph rule.
            string dec = DecodeStreams(ToPdf(
                "<div style=\"background:#ff0000;border:1px solid #0000ff\">boxed</div><hr>",
                Backgrounds(false)));

            Assert.Equal(0, PdfContentProbe.RectangleFillCount(dec));
            Assert.True(PdfContentProbe.StrokedSegments(dec).Count >= 5,
                "the four div borders and the <hr> rule must survive, got " +
                PdfContentProbe.StrokedSegments(dec).Count);
        }

        [Fact]
        public void Managed_PrintBackgroundFalse_ScrubsTheRunningHeadAndFoot()
        {
            var off = Backgrounds(false);
            off.DisplayHeaderFooter = true;
            off.HeaderTemplate = "<div style=\"background:#ff0000\">head</div>";
            off.FooterTemplate = "<div style=\"background:#00ff00\">foot</div>";

            // Header and footer paragraphs are rendered through the exporter's own paginate:false
            // path, so a body-only scrub leaves them shaded and no body-only test sees it.
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf("<p>body</p>", off))));
        }

        [Fact]
        public void Managed_PrintBackgroundTrue_PaintsTheRunningHeadAndFoot()
        {
            var on = Backgrounds(true);
            on.DisplayHeaderFooter = true;
            on.HeaderTemplate = "<div style=\"background:#ff0000\">head</div>";
            on.FooterTemplate = "<div style=\"background:#00ff00\">foot</div>";

            Assert.True(PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf("<p>body</p>", on))) >= 2,
                "the control must paint both running blocks' shading");
        }

        [Fact]
        public void Managed_PrintBackgroundFalse_ScrubsNestedTableCells()
        {
            const string nested =
                "<table><tr><td><table><tr><td bgcolor=\"#ff0000\">deep</td></tr></table></td></tr></table>";

            Assert.True(PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(nested, Backgrounds(true)))) >= 1,
                "the control must paint the inner cell's fill");
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(nested, Backgrounds(false)))));
        }

        [Fact]
        public void Managed_PrintBackgroundFalse_ScrubsListItemHighlights()
        {
            const string list = "<ul><li><mark>item</mark></li></ul>";

            Assert.True(PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(list, Backgrounds(true)))) >= 1,
                "the control must paint the <mark> highlight inside the list item");
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(list, Backgrounds(false)))));
        }

        [Fact]
        public void Managed_PrintBackgroundFalse_ScrubsFormFieldShading()
        {
            // A form control's grey box is a UA-stylesheet background, exactly like <pre>'s and
            // <mark>'s. Chromium drops it, so leaving it would make "backgrounds off" a
            // half-truth.
            const string form = "<form><input type=\"text\" value=\"typed\"></form>";

            Assert.True(PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(form, Backgrounds(true)))) >= 1,
                "the control must paint the field shading");
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(form, Backgrounds(false)))));
        }

        /// <summary>
        /// A <c>&lt;pre&gt;</c>'s background, which the page sets (browsers give it none — the grey
        /// box this scrubbed was the engine's own, and is gone), is scrubbed with the rest.
        /// </summary>
        [Fact]
        public void Managed_PrintBackgroundFalse_ScrubsAPreBlocksBackground()
        {
            const string pre = "<pre style=\"background-color:#eeeeee\">code</pre>";

            Assert.True(PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(pre, Backgrounds(true)))) >= 1,
                "the control must paint the pre's background");
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(pre, Backgrounds(false)))));
        }

        [Fact]
        public void Managed_PrintBackgroundFalse_DeeplyNestedTables_ScrubsEveryLevel()
        {
            // The scrub recurses through table cells, so nesting is its stack depth. 60 levels is
            // well inside the mapper's own DOM cap and well past anything a real document nests.
            var sb = new StringBuilder();
            for (int i = 0; i < 60; i++) sb.Append("<table><tr><td bgcolor=\"#ff0000\">");
            sb.Append('x');
            for (int i = 0; i < 60; i++) sb.Append("</td></tr></table>");
            string deep = sb.ToString();

            Assert.True(PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(deep, Backgrounds(true)))) >= 2,
                "the control must paint the nested cells' fills");
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(DecodeStreams(ToPdf(deep, Backgrounds(false)))));
        }
    }
}
