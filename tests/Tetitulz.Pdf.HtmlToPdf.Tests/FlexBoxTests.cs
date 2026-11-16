// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A flex row's container box, its <c>gap</c>, and its items' widths — found against Chrome on
    /// an invoice: a flex <c>&lt;header&gt;</c>'s bottom rule was never drawn (only the items' table
    /// reached the page), <c>gap</c> was not read (two boxes touched), and an item's
    /// <c>width: 40%</c> was applied again inside its 40% track (a signature line at 16%).
    /// </summary>
    public class FlexBoxTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        /// <summary>The container's border is drawn round its items; with none, no rule (the control).</summary>
        [Fact]
        public void AFlexContainersBorderIsDrawn()
        {
            string ruled = Content("<div style='display:flex;border-bottom:3px solid #000'><div>AAA</div><div>BBB</div></div>");
            Assert.Contains(StrokedSegments(ruled), s => s.IsHorizontal);
            Assert.DoesNotContain(StrokedSegments(Content("<div style='display:flex'><div>AAA</div><div>BBB</div></div>")), s => s.IsHorizontal);
        }

        /// <summary>The container's padding moves its items in: 20px is 15pt.</summary>
        [Fact]
        public void AFlexContainersPaddingMovesItsItems()
        {
            double plain = XOf(Content("<div style='display:flex'><div>AAA</div><div>BBB</div></div>"), "AAA");
            double padded = XOf(Content("<div style='display:flex;padding-left:20px'><div>AAA</div><div>BBB</div></div>"), "AAA");
            Assert.Equal(plain + 15, padded, 1);
        }

        /// <summary>
        /// An item's width is its track, taken once: a 40% item's top rule is 40% of the width — a
        /// full-width reference rule's — where it was 40% of that again.
        /// </summary>
        [Fact]
        public void AnItemsWidthIsTakenOnce()
        {
            string c = Content("<div style='border-top:1px solid #000'>REF</div>" +
                "<div style='display:flex;justify-content:space-between'>" +
                "<div style='width:40%;border-top:1px solid #000'>AAA</div><div style='width:40%;border-top:1px solid #000'>BBB</div></div>");
            var rules = StrokedSegments(c).Where(s => s.IsHorizontal).Select(s => System.Math.Abs(s.X1 - s.X0)).ToList();
            double full = rules.Max();
            Assert.Equal(3, rules.Count);
            Assert.All(rules.Where(r => r < full), r => Assert.Equal(0.4 * full, r, 0));
        }

        /// <summary>
        /// A 24px (18pt) gap between two equal items: the second item's text moves 9pt — half the
        /// gap, the other half coming off the first item's width — and the first's does not.
        /// </summary>
        [Fact]
        public void AGapSeparatesTheItems()
        {
            string plain = Content("<div style='display:flex'><div style='flex:1'>AAA</div><div style='flex:1'>BBB</div></div>");
            string gapped = Content("<div style='display:flex;gap:24px'><div style='flex:1'>AAA</div><div style='flex:1'>BBB</div></div>");
            Assert.Equal(XOf(plain, "BBB") + 9, XOf(gapped, "BBB"), 1);
            Assert.Equal(XOf(plain, "AAA"), XOf(gapped, "AAA"), 1);
        }

        /// <summary>In a right-to-left row the gap is mirrored: the second item, on the left, ends 9pt further left.</summary>
        [Fact]
        public void AGapIsMirroredRightToLeft()
        {
            string plain = Content("<div dir='rtl' style='display:flex'><div style='flex:1'>AAA</div><div style='flex:1'>BBB</div></div>");
            string gapped = Content("<div dir='rtl' style='display:flex;gap:24px'><div style='flex:1'>AAA</div><div style='flex:1'>BBB</div></div>");
            Assert.Equal(XOf(plain, "BBB") - 9, XOf(gapped, "BBB"), 1);
        }

        /// <summary>
        /// Inside a padded container the items share the space inside its padding: a 40% item's rule
        /// is 40% of a reference rule 40px in from each side.
        /// </summary>
        [Fact]
        public void ItemsShareTheSpaceInsideTheContainersPadding()
        {
            string c = Content("<div style='margin:0 40px;border-top:1px solid #000'>REF</div>" +
                "<div style='display:flex;padding:0 40px;justify-content:space-between'>" +
                "<div style='width:40%;border-top:1px solid #000'>AAA</div><div style='width:40%;border-top:1px solid #000'>BBB</div></div>");
            var rules = StrokedSegments(c).Where(s => s.IsHorizontal).Select(s => System.Math.Abs(s.X1 - s.X0)).ToList();
            double inside = rules.Max();
            Assert.All(rules.Where(r => r < inside), r => Assert.Equal(0.4 * inside, r, 0));
        }

        /// <summary>Three equal items with a gap space their text equally: the middle one's two half-gaps come off its own width, not its neighbours'.</summary>
        [Fact]
        public void ThreeItemsWithAGapAreEqual()
        {
            string c = Content("<div style='display:flex;gap:30px'><div style='flex:1'>AAA</div><div style='flex:1'>BBB</div><div style='flex:1'>CCC</div></div>");
            Assert.Equal(XOf(c, "BBB") - XOf(c, "AAA"), XOf(c, "CCC") - XOf(c, "BBB"), 1);
        }

        /// <summary>A flex COLUMN's item keeps its width — the cross axis — as block flow does: a 40% item's rule is 40%.</summary>
        [Fact]
        public void AColumnsItemKeepsItsWidth()
        {
            string c = Content("<div style='border-top:1px solid #000'>REF</div>" +
                "<div style='display:flex;flex-direction:column'><div style='width:40%;border-top:1px solid #000'>AAA</div></div>");
            var rules = StrokedSegments(c).Where(s => s.IsHorizontal).Select(s => System.Math.Abs(s.X1 - s.X0)).ToList();
            Assert.Equal(0.4 * rules.Max(), rules.Min(), 0);
        }

        /// <summary>A fixed-width item keeps its width inside a padded container: 100px is 75pt, however wide the padding.</summary>
        [Fact]
        public void AFixedItemKeepsItsWidthInsideThePadding()
        {
            string c = Content("<div style='display:flex;padding:0 80px'><div style='width:100px;border-top:1px solid #000'>AAA</div></div>");
            var rule = Assert.Single(StrokedSegments(c), s => s.IsHorizontal);
            Assert.Equal(75, System.Math.Abs(rule.X1 - rule.X0), 1);
        }
    }
}
