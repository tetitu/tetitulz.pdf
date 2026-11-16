// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Tests for the managed-engine enhancements: <c>&lt;style&gt;</c> CSS cascade
    /// (selectors / specificity / inheritance / inline-wins), serif font-family → Times,
    /// em-relative + percentage lengths, block margins / padding / line-height / text-indent,
    /// &amp;nbsp; preservation, hsl()/rgba() colours, table colgroup widths + cellpadding,
    /// opt-in local-file images with aspect-ratio, and the recursion (DoS) guards.
    /// Assertions read the PDF back: font dicts are uncompressed and content-stream operators
    /// are recovered by inflating the FlateDecode streams.
    /// </summary>
    public class HtmlManagedEnhancementsTests
    {
        private static HtmlToPdfOptions Managed() => new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed };
        private static byte[] Pdf(string html) => HtmlToPdfConverter.ConvertHtmlToBytes(html, Managed());
        private static string Raw(byte[] pdf) => Encoding.Latin1.GetString(pdf);

        private static int CountPages(byte[] pdf)
        {
            string raw = Encoding.Latin1.GetString(pdf);
            int n = 0, i = 0;
            while ((i = raw.IndexOf("/MediaBox", i, StringComparison.Ordinal)) >= 0) { n++; i += 9; }
            return n;
        }

        // The shared inflater (ManagedTestSupport), kerned standard-14 words joined back into their Tj.
        private static string DecodeStreams(byte[] pdf) => ManagedTestSupport.DecodeStreams(pdf);

        // The font resource (e.g. "F9") referenced by the first text-showing Tf operator.
        private static string? FirstFontRes(string content)
        {
            var m = Regex.Match(content, @"/(F\d+)\s+[\d.]+\s+Tf");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------------------------------------------------------------- CSS cascade

        [Fact]
        public void StyleBlock_ClassSelector_AppliesColor()
        {
            string dec = DecodeStreams(Pdf("<style>.r{color:#ff0000}</style><p class=\"r\">Red</p>"));
            Assert.Contains("Red", dec);
            Assert.Contains("1 0 0 rg", dec); // red fill
        }

        [Fact]
        public void StyleBlock_TagSelector_InheritsToParagraph()
        {
            // color set on <body> inherits to the contained <p> (no rule targets <p> directly).
            string dec = DecodeStreams(Pdf(
                "<html><head><style>body{color:#0000ff}</style></head><body><p>Blue</p></body></html>"));
            Assert.Contains("0 0 1 rg", dec); // blue fill, inherited
        }

        [Fact]
        public void Css_Specificity_ClassBeatsTag()
        {
            // .r (specificity 0,1,0) must win over p (0,0,1) regardless of source order.
            string dec = DecodeStreams(Pdf("<style>p{color:#0000ff}.r{color:#ff0000}</style><p class=\"r\">X</p>"));
            Assert.Contains("1 0 0 rg", dec);
            Assert.DoesNotContain("0 0 1 rg", dec);
        }

        [Fact]
        public void Css_InlineStyle_BeatsStylesheet()
        {
            string dec = DecodeStreams(Pdf("<style>p{color:#ff0000}</style><p style=\"color:#0000ff\">X</p>"));
            Assert.Contains("0 0 1 rg", dec);
            Assert.DoesNotContain("1 0 0 rg", dec);
        }

        [Fact]
        public void Css_AtMedia_IsFlattenedAndApplied()
        {
            string dec = DecodeStreams(Pdf(
                "<style>@media print { .g { color:#008000 } }</style><p class=\"g\">G</p>"));
            Assert.Contains("0 0.501961 0 rg", dec); // #008000 → 0, 128/255, 0
        }

        [Fact]
        public void MalformedCss_DoesNotThrow()
        {
            byte[] pdf = Pdf("<style>p{color:red;;;} @media screen{.x{color:blue}} " +
                             "@font-face{src:url(x)} ::bogus{} /* c */</style><p class=\"x\">X</p>");
            Assert.Equal((byte)'%', pdf[0]);
        }

        // ---------------------------------------------------------------- fonts

        [Fact]
        public void FontFamily_Serif_UsesDifferentFontResourceThanSans()
        {
            string sans = FirstFontRes(DecodeStreams(Pdf("<p>Word</p>")))!;
            string serif = FirstFontRes(DecodeStreams(Pdf("<p style=\"font-family:Georgia,serif\">Word</p>")))!;
            Assert.NotNull(sans);
            Assert.NotNull(serif);
            Assert.NotEqual(sans, serif); // serif text drawn with the Times family resource
            // The face it is set in, not a name somewhere in the file: every page carries all twelve.
            Assert.Equal("Times-Roman", Tetitulz.Pdf.TestHelpers.PdfContentProbe.BaseFontOf(Pdf("<p style=\"font-family:serif\">Word</p>"), "Word"));
        }

        [Fact]
        public void FontFamily_SansSerif_StaysHelvetica()
        {
            // "sans-serif" must not be misread as "serif".
            string sans = FirstFontRes(DecodeStreams(Pdf("<p>Word</p>")))!;
            string sansSerif = FirstFontRes(DecodeStreams(Pdf("<p style=\"font-family:Arial,sans-serif\">Word</p>")))!;
            Assert.Equal(sans, sansSerif);
        }

        [Fact]
        public void FontSize_Em_RelativeToInheritedSize()
        {
            // div 20px (=15pt) → p 1.5em (=22.5pt).
            string dec = DecodeStreams(Pdf("<div style=\"font-size:20px\"><p style=\"font-size:1.5em\">X</p></div>"));
            Assert.Contains("22.5 Tf", dec);
        }

        // ---------------------------------------------------------------- block spacing

        [Fact]
        public void LineHeight_IncreasesVerticalSpace()
        {
            string Build(string style)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 40; i++) sb.Append($"<p{style}>Paragraph {i}</p>");
                return sb.ToString();
            }
            int tall = CountPages(Pdf(Build(" style=\"line-height:50px\"")));
            int normal = CountPages(Pdf(Build("")));
            Assert.True(tall > normal, $"line-height should add pages: tall={tall} normal={normal}");
        }

        [Fact]
        public void TextIndent_ShiftsFirstLine()
        {
            string plain = DecodeStreams(Pdf("<p>Hello world this is some text</p>"));
            string indented = DecodeStreams(Pdf("<p style=\"text-indent:40px\">Hello world this is some text</p>"));
            Assert.NotEqual(plain, indented); // first-line x offset differs
        }

        [Fact]
        public void MarginTop_AddsSpaceBeforeParagraph()
        {
            string plain = DecodeStreams(Pdf("<p>X</p>"));
            string spaced = DecodeStreams(Pdf("<p style=\"margin-top:200px\">X</p>"));
            Assert.NotEqual(plain, spaced); // text baseline pushed lower
        }

        // ---------------------------------------------------------------- whitespace

        [Fact]
        public void NonBreakingSpace_IsPreservedNotCollapsed()
        {
            // Three regular spaces collapse to one; three &nbsp; do not (and do not break) —
            // so the two render differently.
            string collapsed = DecodeStreams(Pdf("<p>a   b</p>"));
            string nbsp = DecodeStreams(Pdf("<p>a&nbsp;&nbsp;&nbsp;b</p>"));
            Assert.NotEqual(collapsed, nbsp);
        }

        // ---------------------------------------------------------------- colours

        [Fact]
        public void HslColor_ResolvesToFill()
        {
            // hsl(0,100%,50%) = pure red.
            string dec = DecodeStreams(Pdf("<p style=\"color:hsl(0,100%,50%)\">X</p>"));
            Assert.Contains("1 0 0 rg", dec);
        }

        [Fact]
        public void RgbaColor_CompositesAlphaOverWhite()
        {
            string semi = DecodeStreams(Pdf("<p style=\"color:rgba(0,0,0,0.5)\">X</p>"));
            string opaque = DecodeStreams(Pdf("<p style=\"color:rgb(0,0,0)\">X</p>"));
            Assert.Contains("0 0 0 rg", opaque);      // opaque black
            Assert.DoesNotContain("0 0 0 rg", semi);  // 50% alpha → lightened to grey
        }

        // ---------------------------------------------------------------- tables

        [Fact]
        public void Table_ColGroupWidths_ChangeColumnLayout()
        {
            string withCols = DecodeStreams(Pdf(
                "<table border=\"1\"><colgroup><col style=\"width:20%\"><col style=\"width:80%\"></colgroup>" +
                "<tr><td>A</td><td>B</td></tr></table>"));
            string uniform = DecodeStreams(Pdf("<table border=\"1\"><tr><td>A</td><td>B</td></tr></table>"));
            Assert.Contains("A", withCols);
            Assert.Contains("B", withCols);
            Assert.NotEqual(uniform, withCols); // 20/80 split vs 50/50
        }

        [Fact]
        public void Table_CellPadding_ChangesLayout()
        {
            string padded = DecodeStreams(Pdf("<table cellpadding=\"20\"><tr><td>A</td></tr></table>"));
            string plain = DecodeStreams(Pdf("<table><tr><td>A</td></tr></table>"));
            Assert.NotEqual(plain, padded);
        }

        // ---------------------------------------------------------------- images

        [Fact]
        public void LocalImage_WithBasePath_Embeds()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tetitulz_img_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "logo.png"), MakeRgbPng(4, 4));
                var opt = Managed();
                opt.BasePath = dir;
                byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                    "<img src=\"logo.png\" width=\"40\" height=\"40\">", opt);
                Assert.Contains("/Image", Raw(pdf));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void LocalImage_WithoutBasePath_Skipped()
        {
            byte[] pdf = Pdf("<img src=\"logo.png\" width=\"40\" height=\"40\">");
            Assert.Equal((byte)'%', pdf[0]);
            Assert.DoesNotContain("/Image", Raw(pdf));
        }

        [Fact]
        public void LocalImage_PathTraversalOutsideBase_Refused()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tetitulz_img_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var opt = Managed();
                opt.BasePath = dir;
                // Escape attempt must be refused (no image embedded), not read.
                byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                    "<img src=\"../../../../Windows/win.ini\" width=\"40\" height=\"40\">", opt);
                Assert.DoesNotContain("/Image", Raw(pdf));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Image_WidthOnly_StillEmbedsWithDerivedHeight()
        {
            // 8x2 (wide) PNG with only width set → height derived from the intrinsic ratio.
            string dataUri = "data:image/png;base64," + System.Convert.ToBase64String(MakeRgbPng(8, 2));
            byte[] pdf = Pdf($"<img src=\"{dataUri}\" width=\"40\">");
            Assert.Contains("/Image", Raw(pdf));
        }

        // ---------------------------------------------------------------- DoS guards

        [Fact]
        public void DeeplyNestedMarkup_DoesNotStackOverflow()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++) sb.Append("<div>");
            sb.Append("deep");
            for (int i = 0; i < 20000; i++) sb.Append("</div>");
            byte[] pdf = Pdf(sb.ToString()); // would StackOverflow without the depth guard
            Assert.Equal((byte)'%', pdf[0]);
        }

        [Fact]
        public void DeeplyNestedInline_DoesNotStackOverflow()
        {
            var sb = new StringBuilder("<p>");
            for (int i = 0; i < 20000; i++) sb.Append("<span>");
            sb.Append("x");
            for (int i = 0; i < 20000; i++) sb.Append("</span>");
            sb.Append("</p>");
            byte[] pdf = Pdf(sb.ToString());
            Assert.Equal((byte)'%', pdf[0]);
        }

        // ---------------------------------------------------------------- html inheritance + @media + <link>

        [Fact]
        public void HtmlElement_Style_InheritsToBodyContent()
        {
            // A rule on <html> inherits all the way to a <p> with no rule of its own.
            string dec = DecodeStreams(Pdf("<style>html{color:#ff0000}</style><body><p>X</p></body>"));
            Assert.Contains("1 0 0 rg", dec);
        }

        [Fact]
        public void AtMedia_ScreenOnly_IsSkippedForPrint()
        {
            // A screen-only rule must NOT apply to the (print) PDF output.
            string dec = DecodeStreams(Pdf("<style>@media screen{.s{color:#0000ff}}</style><p class=\"s\">X</p>"));
            Assert.DoesNotContain("0 0 1 rg", dec);
        }

        [Fact]
        public void AtMedia_Print_Applies()
        {
            string dec = DecodeStreams(Pdf("<style>@media print{.p{color:#ff0000}}</style><p class=\"p\">X</p>"));
            Assert.Contains("1 0 0 rg", dec);
        }

        [Fact]
        public void AtMedia_FeatureOnlyQuery_Applies()
        {
            string dec = DecodeStreams(Pdf("<style>@media (min-width:1px){.f{color:#ff0000}}</style><p class=\"f\">X</p>"));
            Assert.Contains("1 0 0 rg", dec);
        }

        /// <summary>
        /// <c>not</c> negates the whole query (Media Queries 4 §2.2.1), so <c>not screen</c> is print's
        /// and <c>not print</c> is not. Substring tests had both backwards: "not screen" contains
        /// "screen" and was skipped, "not print" contains "print" and applied.
        /// </summary>
        [Fact]
        public void AtMedia_NotNegatesTheWholeQuery()
        {
            string notScreen = DecodeStreams(Pdf("<style>@media not screen{.n{color:#ff0000}}</style><p class=\"n\">X</p>"));
            Assert.Contains("1 0 0 rg", notScreen);

            string notPrint = DecodeStreams(Pdf("<style>@media not print{.n{color:#0000ff}}</style><p class=\"n\">X</p>"));
            Assert.DoesNotContain("0 0 1 rg", notPrint);
        }

        [Fact]
        public void ExternalLinkCss_WithBasePath_Applies()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tetitulz_css_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "site.css"), "p { color: #ff0000 }");
                var opt = Managed();
                opt.BasePath = dir;
                byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                    "<link rel=\"stylesheet\" href=\"site.css\"><p>X</p>", opt);
                Assert.Contains("1 0 0 rg", DecodeStreams(pdf));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ExternalLinkCss_WithoutBasePath_Ignored()
        {
            // No BasePath → no file I/O → the <link> is ignored, text stays default black.
            string dec = DecodeStreams(Pdf("<link rel=\"stylesheet\" href=\"site.css\"><p>X</p>"));
            Assert.DoesNotContain("1 0 0 rg", dec);
        }

        [Fact]
        public void ExternalLinkCss_TraversalOutsideBase_Refused()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tetitulz_css_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var opt = Managed();
                opt.BasePath = dir;
                byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                    "<link rel=\"stylesheet\" href=\"../../escape.css\"><p>X</p>", opt);
                // Nothing loaded → no crash, text stays black (the escape file is never read).
                Assert.Equal((byte)'%', pdf[0]);
                Assert.DoesNotContain("1 0 0 rg", DecodeStreams(pdf));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---------------------------------------------------------------- display:none / font shorthand / links

        [Fact]
        public void DisplayNone_Inline_HidesParagraph()
        {
            string dec = DecodeStreams(Pdf("<p>Visible</p><p style=\"display:none\">Secret</p>"));
            Assert.Contains("Visible", dec);
            Assert.DoesNotContain("Secret", dec);
        }

        [Fact]
        public void DisplayNone_ViaStylesheetClass_HidesElement()
        {
            string dec = DecodeStreams(Pdf("<style>.h{display:none}</style><p class=\"h\">Secret</p><p>Shown</p>"));
            Assert.DoesNotContain("Secret", dec);
            Assert.Contains("Shown", dec);
        }

        [Fact]
        public void HiddenAttribute_HidesElement()
        {
            string dec = DecodeStreams(Pdf("<div hidden>Nope</div><p>Yes</p>"));
            Assert.DoesNotContain("Nope", dec);
            Assert.Contains("Yes", dec);
        }

        [Fact]
        public void DisplayNone_InlineSpan_HidesText()
        {
            string dec = DecodeStreams(Pdf("<p>a<span style=\"display:none\">SECRET</span>b</p>"));
            Assert.DoesNotContain("SECRET", dec);
            Assert.Contains("a", dec);
            Assert.Contains("b", dec);
        }

        [Fact]
        public void VisibilityHidden_HidesText()
        {
            string dec = DecodeStreams(Pdf("<p style=\"visibility:hidden\">Invisible</p><p>OK</p>"));
            Assert.DoesNotContain("Invisible", dec);
            Assert.Contains("OK", dec);
        }

        [Fact]
        public void DisplayNone_TableRow_Skipped()
        {
            string dec = DecodeStreams(Pdf(
                "<table><tr><td>Keep</td></tr><tr style=\"display:none\"><td>Drop</td></tr></table>"));
            Assert.Contains("Keep", dec);
            Assert.DoesNotContain("Drop", dec);
        }

        [Fact]
        public void FontShorthand_ParsesSize()
        {
            // font: <size> <family> — 24px → 18pt.
            string dec = DecodeStreams(Pdf("<p style=\"font:24px sans-serif\">Big</p>"));
            Assert.Contains("18 Tf", dec);
        }

        [Fact]
        public void FontShorthand_ParsesStyleAndSerifFamily()
        {
            // font: italic bold 12px/1.5 Georgia, serif → serif family + italic + bold.
            string serif = FirstFontRes(DecodeStreams(Pdf("<p style=\"font:italic bold 12px Georgia,serif\">W</p>")))!;
            string sans = FirstFontRes(DecodeStreams(Pdf("<p>W</p>")))!;
            Assert.NotEqual(sans, serif); // serif (Times) resource differs from default Helvetica
        }

        [Fact]
        public void Anchor_GetsDefaultBlueUnderlineStyling()
        {
            // #0000EE → 0, 0, 238/255 = 0.933333.
            string dec = DecodeStreams(Pdf("<p><a href=\"http://x\">Link</a></p>"));
            Assert.Contains("0 0 0.933333 rg", dec);
        }

        [Fact]
        public void Anchor_ExplicitColorOverridesDefault()
        {
            string dec = DecodeStreams(Pdf("<p><a href=\"http://x\" style=\"color:#ff0000\">Link</a></p>"));
            Assert.Contains("1 0 0 rg", dec);
            Assert.DoesNotContain("0 0 0.933333 rg", dec);
        }

        // ---------------------------------------------------------------- page breaks + var()

        [Fact]
        public void PageBreakBefore_ForcesNewPage()
        {
            byte[] pdf = Pdf("<p>Page1</p><div style=\"page-break-before:always\"><p>Page2</p></div>");
            Assert.True(CountPages(pdf) >= 2, $"expected a forced page break, got {CountPages(pdf)} page(s)");
            string dec = DecodeStreams(pdf);
            Assert.Contains("Page1", dec);
            Assert.Contains("Page2", dec);
        }

        [Fact]
        public void BreakAfter_Page_ForcesNewPage()
        {
            byte[] pdf = Pdf("<p style=\"break-after:page\">First</p><p>Second</p>");
            Assert.True(CountPages(pdf) >= 2, $"expected a forced page break, got {CountPages(pdf)} page(s)");
        }

        [Fact]
        public void NoPageBreak_SingleParagraphsStayOnOnePage()
        {
            byte[] pdf = Pdf("<p>One</p><p>Two</p>");
            Assert.Equal(1, CountPages(pdf));
        }

        [Fact]
        public void CssVariable_FromRoot_ResolvesColor()
        {
            string dec = DecodeStreams(Pdf("<style>:root{--c:#ff0000} p{color:var(--c)}</style><p>X</p>"));
            Assert.Contains("1 0 0 rg", dec);
        }

        [Fact]
        public void CssVariable_Fallback_UsedWhenUndefined()
        {
            // #00ff00 → 0 1 0.
            string dec = DecodeStreams(Pdf("<p style=\"color:var(--missing,#00ff00)\">X</p>"));
            Assert.Contains("0 1 0 rg", dec);
        }

        [Fact]
        public void CssVariable_InlineDefinition_ResolvesForDescendant()
        {
            string dec = DecodeStreams(Pdf("<div style=\"--brand:#0000ff\"><p style=\"color:var(--brand)\">X</p></div>"));
            Assert.Contains("0 0 1 rg", dec);
        }

        // ---------------------------------------------------------------- inline <svg>

        [Fact]
        public void Svg_NoRasterizer_Skipped()
        {
            // Default options (no rasterizer) → <svg> is dropped, no image, valid PDF.
            byte[] pdf = Pdf("<p>Chart:</p><svg width=\"40\" height=\"40\"><rect width=\"40\" height=\"40\"/></svg>");
            Assert.Equal((byte)'%', pdf[0]);
            Assert.DoesNotContain("/Image", Raw(pdf));
        }

        [Fact]
        public void Svg_FakeRasterizer_EmbedsAndReceivesMarkup()
        {
            string captured = null;
            var opt = Managed();
            opt.SvgRasterizer = markup => { captured = markup; return MakeRgbPng(4, 4); };
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<svg width=\"40\" height=\"40\"><rect x=\"5\" y=\"5\" width=\"30\" height=\"30\"/></svg>", opt);
            Assert.Contains("/Image", Raw(pdf));
            Assert.NotNull(captured);
            Assert.Contains("<svg", captured);
            Assert.Contains("rect", captured); // the element's children survive serialization
        }

        [Fact]
        public void Svg_InlineInParagraph_EmbedsAndKeepsText()
        {
            var opt = Managed();
            opt.SvgRasterizer = _ => MakeRgbPng(4, 4);
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<p>before<svg width=\"20\" height=\"20\"><circle cx=\"10\" cy=\"10\" r=\"8\"/></svg>after</p>", opt);
            Assert.Contains("/Image", Raw(pdf));
            string dec = DecodeStreams(pdf);
            Assert.Contains("before", dec);
            Assert.Contains("after", dec);
        }

        [Fact]
        public void Svg_NullFromRasterizer_Skipped()
        {
            var opt = Managed();
            opt.SvgRasterizer = _ => null; // rasterizer declines (e.g. unsupported SVG)
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<p>x</p><svg width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>", opt);
            Assert.Equal((byte)'%', pdf[0]);
            Assert.DoesNotContain("/Image", Raw(pdf));
        }

        [Fact]
        public void Svg_RealBaseRasterizer_Embeds()
        {
            // End-to-end: SvgToPdfConverter → PdfPageRenderer → PNG → ImageRun via the Base helper.
            var opt = Managed();
            opt.SvgRasterizer = Tetitulz.Pdf.Base.Operations.Images.SvgRasterizer.RasterizeToPng;
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<svg width=\"40\" height=\"40\"><rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" fill=\"blue\"/></svg>", opt);
            Assert.Contains("/Image", Raw(pdf));
        }

        // ---- minimal RGB PNG synthesis (self-contained, mirrors HtmlManagedEngineTests) ----

        private static byte[] MakeRgbPng(int w, int h)
        {
            byte[] raw = new byte[h * (1 + w * 3)];
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < w; x++) { raw[p++] = 200; raw[p++] = 50; raw[p++] = 50; }
            }
            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }
            using var outMs = new MemoryStream();
            outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 2;
            WriteChunk(outMs, "IHDR", ihdr);
            WriteChunk(outMs, "IDAT", idat);
            WriteChunk(outMs, "IEND", Array.Empty<byte>());
            return outMs.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
            byte[] t = Encoding.ASCII.GetBytes(type);
            var crcIn = new byte[4 + data.Length];
            Array.Copy(t, 0, crcIn, 0, 4); Array.Copy(data, 0, crcIn, 4, data.Length);
            s.Write(t, 0, 4); s.Write(data, 0, data.Length);
            var crcb = new byte[4]; WriteBE(crcb, 0, (int)Crc32(crcIn)); s.Write(crcb, 0, 4);
        }

        private static void WriteBE(byte[] b, int o, int v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1); }
            return c ^ 0xFFFFFFFF;
        }
    }
}
