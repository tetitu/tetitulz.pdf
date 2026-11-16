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
    /// CSS <c>opacity</c> on a box of blocks (batch 106): the box — its background, border and its
    /// blocks — is a transparency group the page paints once at the box's opacity, as a boxed
    /// paragraph has been since batch 90. Before batch 106 the box's background was drawn opaque and
    /// each block inside carried the accumulated alpha, so black text at 0.5 over the box's
    /// half-strength blue showed the blue through the letters.
    /// </summary>
    public class HtmlBoxOpacityTests
    {
        private static readonly Regex GsOp = new Regex(@"/(GS\d+)\s+gs", RegexOptions.Compiled);

        /// <summary>Every <c>/ca</c> value the file's ExtGState objects declare, in file order.</summary>
        private static List<string> CaValues(byte[] pdf)
            => Regex.Matches(Raw(pdf), @"/Type\s*/ExtGState\s*/ca\s+([\d.]+)").Cast<Match>().Select(m => m.Groups[1].Value).ToList();

        /// <summary>
        /// Black text in two paragraphs inside a blue box at 0.5, rendered: the darkest pixel is grey
        /// — (128, 128, 128) — not the bluish (64, 64, 160) that constant alpha on each object gave,
        /// and the box's background is half-strength blue, not the full blue an opaque fill left.
        /// </summary>
        [Fact]
        public void ATranslucentBoxOfBlocks_CompositesBackgroundAndTextAsOneLayer()
        {
            byte[] pdf = ToPdf("<div style=\"background:#0000ff;opacity:0.5\"><p style=\"font-size:120pt;font-family:Helvetica\">IIIIIIIIII</p><p>second block</p></div>");
            var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new System.IO.MemoryStream(pdf), 0, 72);

            (int R, int G, int B) darkest = (255, 255, 255);
            int halfBlue = 0, fullBlue = 0;
            for (int y = 0; y < page.Height; y++)
                for (int x = 0; x < page.Width; x++)
                {
                    int i = y * page.Stride + x * 4;
                    var p = (R: (int)page.Pixels[i], G: (int)page.Pixels[i + 1], B: (int)page.Pixels[i + 2]);
                    if (p.R + p.G + p.B < darkest.R + darkest.G + darkest.B) darkest = p;
                    if (p.B >= 240 && p.R is >= 110 and <= 145) halfBlue++;
                    if (p.B >= 240 && p.R <= 20) fullBlue++;
                }

            Assert.True(halfBlue > 100, $"no half-strength blue background was rendered ({halfBlue} pixels)");
            Assert.Equal(0, fullBlue);
            Assert.InRange(darkest.R, 105, 150);
            Assert.True(Math.Abs(darkest.R - darkest.B) <= 12, $"the darkest pixel {darkest} is not grey: the background shows through the text");
        }

        /// <summary>The box's group is painted once at its alpha on the page, and the blocks inside write no alpha of their own.</summary>
        [Fact]
        public void ATranslucentBox_IsOneGroupPaintedOnce_ItsBlocksOpaqueInside()
        {
            byte[] pdf = ToPdf("<div style=\"background:#0000ff;opacity:0.5\"><p>one</p><p>two</p></div>");
            string content = DecodeStreams(pdf);

            Assert.Equal(new[] { "0.5" }, CaValues(pdf));
            Assert.Single(GsOp.Matches(content).Cast<Match>());
            Assert.Single(Regex.Matches(content, @"/Fm\d+ Do").Cast<Match>());
        }

        /// <summary>A block inside the box carries only what it adds: a 0.5 paragraph in a 0.5 box is a second 0.5, not 0.25.</summary>
        [Fact]
        public void ABlockInsideTheBox_CarriesOnlyWhatItAdds()
        {
            byte[] pdf = ToPdf("<div style=\"background:#0000ff;opacity:0.5\"><p>plain</p><p style=\"opacity:0.5\">dimmer</p></div>");
            string content = DecodeStreams(pdf);

            Assert.Contains("0.5", CaValues(pdf));
            Assert.DoesNotContain("0.25", CaValues(pdf));
            // The page paints the box's form at a state, and inside the form the dimmer paragraph
            // sets its own before its text — the plain one none.
            Assert.Matches(@"/GS\d+ gs\s+/Fm\d+ Do", content);
            Assert.Matches(@"/GS\d+ gs[^(]*\(dimmer\) Tj", content);
            Assert.DoesNotMatch(@"/GS\d+ gs[^(]*\(plain\) Tj", content);
        }

        /// <summary>A list item's runs inside the box carry only what they add too — no alpha when the item adds none.</summary>
        [Fact]
        public void ListRunsInsideTheBox_CarryOnlyWhatTheyAdd()
        {
            byte[] pdf = ToPdf("<div style=\"opacity:0.5\"><ul><li>item</li></ul></div>");
            string content = DecodeStreams(pdf);

            Assert.Equal(new[] { "0.5" }, CaValues(pdf));
            Assert.Single(GsOp.Matches(content).Cast<Match>());
        }

        /// <summary>A box inside a translucent box is a group of its own inside the outer's, each at what it adds: two alphas of 0.5 and no 0.25.</summary>
        [Fact]
        public void ABoxInsideATranslucentBox_IsANestedGroupAtWhatItAdds()
        {
            byte[] pdf = ToPdf("<div style=\"opacity:0.5\"><div style=\"background:#0000ff;opacity:0.5\"><p>one</p><p>two</p></div></div>");
            string content = DecodeStreams(pdf);

            // Each content stream — the page's and the outer form's — holds its own state, both 0.5.
            Assert.Equal(2, CaValues(pdf).Count);
            Assert.All(CaValues(pdf), ca => Assert.Equal("0.5", ca));
            Assert.Equal(2, GsOp.Matches(content).Count);
            Assert.Equal(2, Regex.Matches(content, @"/Fm\d+ Do").Count);
        }

        /// <summary>In the default tagged export the group's <c>Do</c> is one <c>Div</c> sequence and nothing inside the form is marked.</summary>
        [Fact]
        public void TheTaggedBoxsDo_IsOneDivSequence()
        {
            byte[] pdf = ToPdf("<div style=\"background:#0000ff;opacity:0.5\"><p>one</p><p>two</p></div>");
            string content = DecodeStreams(pdf);

            Assert.Matches(@"/Div\s*<<[^>]*/MCID\s+\d+[^>]*>>\s*BDC\s*q\s*/GS\d+ gs\s*/Fm\d+ Do\s*Q\s*EMC", content);
            Assert.DoesNotMatch(@"/P\s*<<[^>]*/MCID", content);
        }
    }
}
