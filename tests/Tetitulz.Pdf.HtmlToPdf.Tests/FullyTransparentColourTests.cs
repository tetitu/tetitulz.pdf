// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A fully transparent colour paints nothing, whichever way it is written: the
    /// <c>transparent</c> keyword, <c>rgba(…, 0)</c>, <c>#rrggbb00</c>.
    /// </summary>
    /// <remarks>
    /// A translucent colour is composited over white to approximate how it looks on the page, and an
    /// alpha of 0 composited that way is <b>white</b>. So <c>rgba(0, 0, 0, 0)</c> — which reset
    /// stylesheets write for "no background" — painted a white box over its parent's colour, and a
    /// border in it drew a white line where the <c>transparent</c> keyword drew none. The keyword was
    /// recognised by name at two sites; it is now a colour <c>CssColor</c> reads, and an alpha of 0
    /// is absent at every site, as the keyword always was.
    /// </remarks>
    public class FullyTransparentColourTests
    {
        private const string WhiteFill = "1 1 1 rg";

        private static string Content(string html) => DecodeStreams(ToPdf(html));

        /// <summary>
        /// A background in a fully transparent colour paints nothing; the same colour opaque paints
        /// white (the control).
        /// </summary>
        [Theory]
        [InlineData("background-color:rgba(255, 255, 255, 0)")]
        [InlineData("background:rgba(255, 255, 255, 0)")]
        [InlineData("background-color:#ffffff00")]
        public void AFullyTransparentBackgroundPaintsNothing(string declaration)
        {
            string clear = Content($"<div style='{declaration}'>A</div>");
            string opaque = Content("<div style='background-color:rgba(255, 255, 255, 1)'>A</div>");

            Assert.DoesNotContain(WhiteFill, clear);
            Assert.Contains(WhiteFill, opaque);
        }

        /// <summary>
        /// A cell edge in a fully transparent colour is suppressed, as a <c>transparent</c> edge is:
        /// no stroke, and no fallback to the table's grid.
        /// </summary>
        [Theory]
        [InlineData("rgba(0, 0, 0, 0)")]
        [InlineData("transparent")]
        public void AFullyTransparentCellBorderDrawsNothing(string colour)
        {
            string grid = Content("<table border='1' cellspacing='0'><tr><td>A</td></tr></table>");
            string clear = Content($"<table border='1' cellspacing='0'><tr><td style='border:1px solid {colour}'>A</td></tr></table>");

            Assert.True(StrokeCount(grid) > 0, "the control table drew no grid, so this proves nothing");
            Assert.Equal(0, StrokeCount(clear));
        }

        /// <summary>A transparent background token in the shorthand, beside an image, paints no colour.</summary>
        [Fact]
        public void ATransparentTokenBesideAnImagePaintsNoColour()
        {
            string png = System.Convert.ToBase64String(MakeRgbPng(4, 4));
            string content = Content($"<div style='background:rgba(255,255,255,0) url(data:image/png;base64,{png});height:20px'>A</div>");

            Assert.DoesNotContain(WhiteFill, content);
        }

        /// <summary>
        /// A fully transparent shadow draws nothing: as many fills as the same box with no shadow.
        /// The keyword drew the grey default and <c>rgba(…, 0)</c> a white shadow. A red shadow is
        /// the control.
        /// </summary>
        [Theory]
        [InlineData("2px 2px transparent")]
        [InlineData("2px 2px rgba(255, 0, 0, 0)")]
        public void AFullyTransparentShadowDrawsNothing(string shadow)
        {
            static int Fills(string c) => System.Text.RegularExpressions.Regex.Matches(c, @"\brg\b").Count;
            string none = Content("<div style='background:#0000ff'>A</div>");
            string clear = Content($"<div style='background:#0000ff;box-shadow:{shadow}'>A</div>");
            string red = Content("<div style='background:#0000ff;box-shadow:2px 2px #ff0000'>A</div>");

            Assert.True(Fills(red) > Fills(none), "the control shadow drew no fill, so this proves nothing");
            Assert.Equal(Fills(none), Fills(clear));
        }

        /// <summary>
        /// A shadow colour written with spaces, <c>rgba(255, 0, 0, 1)</c>, is read whole: the shadow
        /// is red, not the grey default. Splitting at every comma and space tore it apart.
        /// </summary>
        [Fact]
        public void ASpacedRgbaShadowColourIsRead()
        {
            string content = Content("<div style='background:#0000ff;box-shadow:2px 2px rgba(255, 0, 0, 1)'>A</div>");

            Assert.Contains("1 0 0 rg", content);
        }

        private static int StrokeCount(string content)
            => System.Text.RegularExpressions.Regex.Matches(content, @"\bS\b").Count;
    }
}
