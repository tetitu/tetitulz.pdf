// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Stated bases beside growing items (CSS Flexbox §9.7): the growers share what the bases leave.
    /// Found against Chrome on a CV — a <c>width: 32%</c> sidebar beside a <c>flex: 1</c> column took
    /// three quarters of the row, because a grow factor (a ratio) was carried as 1% of the width a
    /// unit and normalised with the sidebar's width (a length). Each item's width is read off its
    /// top rule against a full-width reference rule.
    /// </summary>
    public class FlexGrowBasisTests
    {
        /// <summary>The top rules' lengths, longest (the full-width reference) first, then the items' left to right.</summary>
        private static double[] Rules(string items, string container = "display:flex")
        {
            string c = DecodeStreams(ToPdf("<html><body><div style='border-top:1px solid #000'>REF</div>" +
                                           "<div style='" + container + "'>" + items + "</div></body></html>"));
            // The reference is the topmost rule (PDF y grows upward); the items' share a lower line.
            var rules = StrokedSegments(c).Where(s => s.IsHorizontal).OrderByDescending(s => s.Y0).ToList();
            return new[] { System.Math.Abs(rules[0].X1 - rules[0].X0) }
                .Concat(rules.Skip(1).OrderBy(s => System.Math.Min(s.X0, s.X1)).Select(s => System.Math.Abs(s.X1 - s.X0)))
                .ToArray();
        }

        private const string Rule = "border-top:1px solid #000";

        [Fact]
        public void AGrowerTakesWhatAFixedItemLeaves()
        {
            var r = Rules($"<div style='width:32%;{Rule}'>SIDE</div><div style='flex:1;{Rule}'>MAIN</div>");
            Assert.Equal(0.32 * r[0], r[1], 0);
            Assert.Equal(0.68 * r[0], r[2], 0);
        }

        /// <summary>Two growers, 1 and 2, share the 75% a 25% item leaves as 25% and 50%.</summary>
        [Fact]
        public void GrowersShareByTheirFactors()
        {
            var r = Rules($"<div style='width:25%;{Rule}'>A</div><div style='flex-grow:1;{Rule}'>B</div><div style='flex-grow:2;{Rule}'>C</div>");
            Assert.Equal(0.25 * r[0], r[1], 0);
            Assert.Equal(0.25 * r[0], r[2], 0);
            Assert.Equal(0.50 * r[0], r[3], 0);
        }

        /// <summary>A grower with a basis of its own keeps it and grows from there: 100px (75pt) each, the rest to the grower.</summary>
        [Fact]
        public void AGrowerGrowsFromItsBasis()
        {
            var r = Rules($"<div style='width:100px;flex-grow:1;{Rule}'>A</div><div style='width:100px;{Rule}'>B</div>");
            Assert.Equal(75, r[2], 0);
            Assert.Equal(r[0] - 75, r[1], 0);
        }

        /// <summary>
        /// An auto item's basis is its content's width: beside it a 30% item keeps 30%, and the
        /// grower takes the rest. The auto item had taken most of the row and squeezed the grower
        /// to a sliver.
        /// </summary>
        [Fact]
        public void AnAutoItemTakesItsContentsWidth()
        {
            string c = DecodeStreams(ToPdf("<html><body><div style='border-top:1px solid #000'>REF</div><div style='display:flex'>" +
                $"<div>AUTO</div><div style='width:30%;{Rule}'>BBB</div><div style='flex:1;{Rule}'>CCC</div></div></body></html>"));
            var r = Rules($"<div>AUTO</div><div style='width:30%;{Rule}'>BBB</div><div style='flex:1;{Rule}'>CCC</div>");
            Assert.Equal(0.30 * r[0], r[1], 0);
            double auto = XOf(c, "BBB") - XOf(c, "AUTO");
            Assert.InRange(auto, 20, 50);                  // "AUTO", not a share of the row
            Assert.Equal(r[0] - 0.30 * r[0] - auto, r[2], 0);
        }

        /// <summary>
        /// An auto item of more than one block is not measured: it keeps the width of its longest
        /// word, and the stated item and the grower still get theirs — the grower no sliver.
        /// </summary>
        [Fact]
        public void AnAutoItemOfSeveralBlocksKeepsItsLongestWord()
        {
            var r = Rules($"<div><p style='margin:0'>AUTO</p><p style='margin:0'>TWO</p></div><div style='width:30%;{Rule}'>BBB</div><div style='flex:1;{Rule}'>CCC</div>");
            Assert.InRange(r[1], 0.25 * r[0], 0.30 * r[0] + 1);   // the stated item, less what the auto item's word took from it
            Assert.True(r[2] > 0.5 * r[0], $"the grower is {r[2]:F1} of {r[0]:F1}");
        }

        /// <summary>
        /// Bases that overflow the row leave nothing to grow into: the grower keeps no more than its
        /// text (CCC, about 26pt) while the bases shrink in proportion, 80% to 40% staying 2 : 1.
        /// </summary>
        [Fact]
        public void OverflowingBasesLeaveTheGrowerNothing()
        {
            var r = Rules($"<div style='width:80%;{Rule}'>AAA</div><div style='width:40%;{Rule}'>BBB</div><div style='flex:1;{Rule}'>CCC</div>");
            Assert.Equal(2, r[1] / r[2], 1);
            Assert.True(r[3] < 40, $"the grower is {r[3]:F1}pt");
        }

        /// <summary>
        /// <c>flex: 1</c> is <c>1 1 0</c> (§7.1.1): its basis is 0, so the item's <c>width</c> is not
        /// one — two such growers split the row in halves though one says 200px. The width used to be
        /// read as the basis, so that item took 150pt more than its neighbour. <c>flex: 1 1 0</c>,
        /// whose third token is a unitless zero basis rather than a third factor, is the same.
        /// </summary>
        [Theory]
        [InlineData("flex:1")]
        [InlineData("flex:1 1 0")]
        public void AGrowFactorAloneSetsABasisOfZero(string flex)
        {
            var r = Rules($"<div style='{flex};width:200px;{Rule}'>A</div><div style='{flex};{Rule}'>B</div>");
            Assert.Equal(r[0] / 2, r[1], 0);
            Assert.Equal(r[0] / 2, r[2], 0);
        }

        /// <summary>
        /// <c>flex: auto</c> is <c>1 1 auto</c>: an auto basis is the item's <c>width</c>, so beside a
        /// <c>flex: 1</c> item it grows from 75pt and the two split what is left.
        /// </summary>
        [Fact]
        public void FlexAutoKeepsTheWidthAsItsBasis()
        {
            var r = Rules($"<div style='flex:auto;width:100px;{Rule}'>A</div><div style='flex:1;{Rule}'>B</div>");
            Assert.Equal(75 + (r[0] - 75) / 2, r[1], 0);
            Assert.Equal((r[0] - 75) / 2, r[2], 0);
        }

        /// <summary>
        /// <c>flex: none</c> (<c>0 0 auto</c>) and <c>flex: initial</c> (<c>0 1 auto</c>) do not grow:
        /// the item keeps its 75pt width beside a grower that takes the rest. Read as a basis named
        /// "none" with a default grow factor, the item would grow and split the row.
        /// </summary>
        [Theory]
        [InlineData("flex:none")]
        [InlineData("flex:initial")]
        public void NoneAndInitialKeepTheWidthAndDoNotGrow(string flex)
        {
            var r = Rules($"<div style='{flex};width:100px;{Rule}'>A</div><div style='flex:1;{Rule}'>B</div>");
            Assert.Equal(75, r[1], 0);
            Assert.Equal(r[0] - 75, r[2], 0);
        }

        /// <summary>
        /// A basis alone is <c>1 1 &lt;basis&gt;</c> (§7.1.1): <c>flex: 200px</c> grows from 150pt into
        /// what a 25% item leaves, so it takes 75% of the row. It read as no grow factor at all, and
        /// stayed at 150pt.
        /// </summary>
        [Fact]
        public void ABasisAloneGrows()
        {
            var r = Rules($"<div style='flex:200px;{Rule}'>A</div><div style='width:25%;{Rule}'>B</div>");
            Assert.Equal(0.75 * r[0], r[1], 0);
            Assert.Equal(0.25 * r[0], r[2], 0);
        }

        /// <summary>
        /// The full shorthand's basis is the item's: <c>flex: 2 1 100px</c> beside
        /// <c>flex: 1 1 100px</c> each start at 75pt and share the rest 2 : 1. The basis used to be
        /// lost, so the two split the whole row 2 : 1.
        /// </summary>
        [Fact]
        public void TheShorthandsBasisIsTheItems()
        {
            var r = Rules($"<div style='flex:2 1 100px;{Rule}'>A</div><div style='flex:1 1 100px;{Rule}'>B</div>");
            Assert.Equal(75 + (r[0] - 150) * 2 / 3, r[1], 0);
            Assert.Equal(75 + (r[0] - 150) / 3, r[2], 0);
        }

        /// <summary>With a gap the grower takes what the basis and the gap leave: the sidebar keeps its 32%.</summary>
        [Fact]
        public void TheGapComesOutOfTheGrower()
        {
            var r = Rules($"<div style='width:32%;{Rule}'>SIDE</div><div style='flex:1;{Rule}'>MAIN</div>", "display:flex;gap:24px");
            Assert.Equal(0.32 * r[0], r[1], 0);
            Assert.Equal(0.68 * r[0] - 18, r[2], 0);
        }
    }
}
