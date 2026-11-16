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
    /// CSS <c>opacity</c> in the managed engine — mapped onto <c>Paragraph.Opacity</c> /
    /// <c>TextRun.Opacity</c> and emitted by Builder's exporter as PDF constant alpha.
    ///
    /// <para><b>The value asserted is the product, not the presence.</b> A test that only checked
    /// "some <c>/ca</c> was written" would pass with the accumulation deleted, with the ancestor
    /// factor dropped, and with the paragraph's factor applied twice — which are three different
    /// wrong answers that all look alike. Each case below picks numbers whose product cannot be
    /// reached by any of the plausible mistakes.</para>
    /// </summary>
    public class HtmlOpacityTests
    {
        private static readonly Regex GsOp = new Regex(@"/(GS\d+)\s+gs", RegexOptions.Compiled);

        private static List<string> GsNames(string content)
        {
            var names = new List<string>();
            foreach (Match m in GsOp.Matches(content)) names.Add(m.Groups[1].Value);
            return names;
        }

        /// <summary>Every <c>/ca</c> value the file's ExtGState objects declare, in file order.</summary>
        private static List<string> CaValues(byte[] pdf)
        {
            var values = new List<string>();
            foreach (Match m in Regex.Matches(Raw(pdf), @"/Type\s*/ExtGState\s*/ca\s+([\d.]+)"))
                values.Add(m.Groups[1].Value);
            return values;
        }

        // ------------------------------------------------------------------ the product

        /// <summary>
        /// 0.5 on the wrapper and 0.5 on the paragraph is 0.25 in CSS: the property does not inherit,
        /// but its EFFECT applies to the subtree and compounds. Since batch 106 the wrapper, a box of
        /// blocks, is a transparency group painted once at its own 0.5, and the paragraph inside it
        /// carries only the 0.5 it adds — two alphas of 0.5, one on the page's <c>Do</c> and one inside
        /// the form, and no 0.25 anywhere. Before batch 106 the paragraph carried the product, 0.25,
        /// and the wrapper's background (had it one) was drawn opaque.
        /// </summary>
        [Fact]
        public void NestedOpacity_IsTheWrappersGroupAtItsOwn_AndTheParagraphAtWhatItAdds()
        {
            byte[] pdf = ToPdf("<div style=\"opacity:0.5\"><p style=\"opacity:0.5\">Dim</p></div>");
            string content = DecodeStreams(pdf);

            Assert.Contains("0.5", CaValues(pdf));
            Assert.DoesNotContain("0.25", CaValues(pdf));
            // The page paints the wrapper's form at a state; inside the form the paragraph sets its
            // own before its text (and resets to 1 after it, which is the third state the file holds).
            Assert.Matches(@"/GS\d+ gs\s+/Fm\d+ Do", content);
            Assert.Matches(@"/GS\d+ gs[^(]*\(Dim\) Tj", content);
        }

        [Fact]
        public void SpanOpacityInsideParagraphOpacity_CompoundsWithoutSquaringTheParagraphs()
        {
            // The paragraph carries 0.5 and the span's run carries the 0.5 it added, so the
            // exporter's multiply lands on 0.25 for the span and 0.5 for the bare text.
            //
            // The failure this discriminates: handing the ACCUMULATED value to both the paragraph
            // and its runs. That squares it — the bare text would draw at 0.25 and the span at
            // 0.0625, and no 0.5 would appear anywhere.
            byte[] pdf = ToPdf("<p style=\"opacity:0.5\">out<span style=\"opacity:0.5\">in</span></p>");

            var ca = CaValues(pdf);
            Assert.Contains("0.5", ca);
            Assert.Contains("0.25", ca);
            Assert.DoesNotContain("0.0625", ca);
        }

        /// <summary>
        /// A boxed paragraph's opacity composites its text and its background as one layer (batch
        /// 90, a transparency group): black text on a blue background at 0.5 over white is grey where
        /// the glyphs are — (128, 128, 128) — and (128, 128, 255) beside them. Constant alpha on each
        /// object drew the text at 0.5 over the half-faded blue, (64, 64, 160): the background showed
        /// through the letters. Asserted on the rendered page's darkest pixel, since the letters'
        /// positions are the layout's; the default, tagged, HTML export is what is rendered.
        /// </summary>
        [Fact]
        public void ATranslucentBoxedParagraph_CompositesTextAndBackgroundAsOneLayer()
        {
            byte[] pdf = ToPdf("<p style=\"background:#0000ff;opacity:0.5;font-size:120pt;font-family:Helvetica\">IIIIIIIIII</p>");
            var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new System.IO.MemoryStream(pdf), 0, 72);

            (int R, int G, int B) darkest = (255, 255, 255);
            int blueBackground = 0;
            for (int y = 0; y < page.Height; y++)
                for (int x = 0; x < page.Width; x++)
                {
                    int i = y * page.Stride + x * 4;
                    var p = (R: (int)page.Pixels[i], G: (int)page.Pixels[i + 1], B: (int)page.Pixels[i + 2]);
                    if (p.R + p.G + p.B < darkest.R + darkest.G + darkest.B) darkest = p;
                    if (p.B >= 240 && p.R is >= 110 and <= 145) blueBackground++;
                }

            Assert.True(blueBackground > 100, $"no half-strength blue background was rendered ({blueBackground} pixels)");
            Assert.InRange(darkest.R, 105, 150);
            Assert.InRange(darkest.G, 105, 150);
            Assert.InRange(darkest.B, 105, 150);
            Assert.True(Math.Abs(darkest.R - darkest.B) <= 12, $"the darkest pixel {darkest} is not grey: the background shows through the text");
        }

        [Fact]
        public void PercentageOpacity_IsReadAsAFraction()
        {
            byte[] pdf = ToPdf("<p style=\"opacity:40%\">Dim</p>");

            Assert.Contains("0.4", CaValues(pdf));
        }

        [Fact]
        public void OpacityFromAStyleBlock_ReachesTheParagraph()
        {
            // The cascade flattener folds <style> rules into the element's style attribute, so
            // this is the path a real page uses far more often than an inline declaration.
            byte[] pdf = ToPdf("<style>p.faint { opacity: 0.3 }</style><p class=\"faint\">Dim</p>");

            Assert.Contains("0.3", CaValues(pdf));
        }

        [Fact]
        public void RepeatedOpacityInOneDeclarationBlock_IsLastWins_NotMultiplied()
        {
            // Within one element CSS is last-wins. Multiplying inside the declaration loop would
            // give 0.16 here — and the cascade flattener routinely produces exactly this shape,
            // appending the element's own style= after a stylesheet rule's declarations.
            byte[] pdf = ToPdf("<p style=\"opacity:0.4;opacity:0.4\">Dim</p>");

            Assert.Contains("0.4", CaValues(pdf));
            Assert.DoesNotContain("0.16", CaValues(pdf));
        }

        // ------------------------------------------------------------------ the negatives

        [Fact]
        public void HtmlThatNeverMentionsOpacity_EmitsNoGraphicsStateOperator()
        {
            // The whole feature must be invisible in the bytes of an ordinary page, or every
            // existing caller's output moved.
            byte[] pdf = ToPdf("<p>Plain</p><p style=\"background:#eee\">Shaded</p>");

            Assert.Empty(GsNames(DecodeStreams(pdf)));
            Assert.DoesNotContain("/ExtGState", Raw(pdf), StringComparison.Ordinal);
        }

        [Fact]
        public void FullyOpaqueDeclaration_EmitsNoGraphicsStateOperator()
        {
            byte[] pdf = ToPdf("<p style=\"opacity:1\">Plain</p>");

            Assert.Empty(GsNames(DecodeStreams(pdf)));
        }

        [Fact]
        public void UnparseableOpacity_IsDeclined_AndLeavesTheInheritedValueStanding()
        {
            // Declining rather than defaulting is the rule the colour properties beside it follow:
            // an unreadable declaration must not overrule the ancestor that WAS readable.
            byte[] pdf = ToPdf("<div style=\"opacity:0.5\"><p style=\"opacity:banana\">Dim</p></div>");

            Assert.Contains("0.5", CaValues(pdf));
        }

        [Fact]
        public void OpacityOnAListItem_ReachesItsRunsAndItsMarker()
        {
            // A ListItem is not a Paragraph and has no Opacity property, so the accumulated value
            // has to land on the runs instead — including the marker, which the exporter clones
            // from TextFormat. Left to the ordinary block rule it landed nowhere and the item drew
            // fully opaque.
            byte[] pdf = ToPdf("<ul><li style=\"opacity:0.5\">item</li></ul>");

            Assert.Contains("0.5", CaValues(pdf));

            // The claim is that the marker and the text AGREE about the alpha, so what has to be
            // pinned is the set of alphas the page can draw at — not how many times a state is
            // entered. This once counted `gs` invocations and expected exactly two, which was the
            // same claim only while an item was drawn as one paragraph: the list container draws
            // the label and the body separately (that is what makes /Lbl and /LBody separable, and
            // what gives the item its hanging indent), so each sets and resets and the count is
            // four. A third alpha would still fail, which is the case the test is for.
            Assert.All(CaValues(pdf), v => Assert.True(v == "0.5" || v == "1",
                $"an alpha of {v} means part of the item disagreed with the rest"));

            var names = GsNames(DecodeStreams(pdf));
            Assert.Equal(2, names.Distinct().Count());   // set 0.5, reset to 1 — and nothing else
        }

        [Fact]
        public void ZeroOpacity_IsCarried_RatherThanTreatedAsUnset()
        {
            // 0 is a legitimate value and falsy in every language that has the concept, so a
            // `if (opacity)` style guard anywhere on the path would silently drop it.
            byte[] pdf = ToPdf("<p style=\"opacity:0\">Hidden</p>");

            Assert.Contains("0", CaValues(pdf));
        }
    }
}
