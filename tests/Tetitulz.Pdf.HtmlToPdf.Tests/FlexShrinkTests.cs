// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>flex-shrink</c> (CSS Flexbox §7.1.4, §9.7, batch 105): when a line's bases overflow it,
    /// the negative free space is shared by each item's shrink factor weighted by its basis, so
    /// <c>flex-shrink: 0</c> keeps its basis and a factor of 3 gives up three times a factor of 1's
    /// share. Before batch 105 no factor was read and every overflowing line shrank in proportion to
    /// its bases — which is what every factor 1 means, so the one-factor case was never wrong. Each
    /// item's width is read off its top rule against a full-width reference rule.
    /// </summary>
    public class FlexShrinkTests
    {
        private const string Rule = "border-top:1px solid #000";

        /// <summary>Three 300px (225pt) items overflow the row; the first keeps its 225pt with <c>flex-shrink: 0</c>, the other two share what is left — where without it all three are equal.</summary>
        [Fact]
        public void ShrinkZero_KeepsTheBasis()
        {
            var reports = new List<string>();
            var r = Rules($"<div style='width:300px;flex-shrink:0;{Rule}'>A</div><div style='width:300px;{Rule}'>B</div><div style='width:300px;{Rule}'>C</div>", reports);
            var control = Rules($"<div style='width:300px;{Rule}'>A</div><div style='width:300px;{Rule}'>B</div><div style='width:300px;{Rule}'>C</div>");

            Assert.True(r[0] < 3 * 225, "the fixture's row does not overflow");
            Assert.Equal(225, r[1], 0);
            Assert.Equal((r[0] - 225) / 2, r[2], 0);
            Assert.Equal((r[0] - 225) / 2, r[3], 0);
            Assert.Equal(control[0] / 3, control[1], 0);
            Assert.Equal(control[0] / 3, control[2], 0);
            Assert.DoesNotContain(reports, m => m.Contains("flex-shrink", System.StringComparison.Ordinal));
        }

        /// <summary>Factors weight the shrink: of two 400px (300pt) items, the one with <c>flex-shrink: 3</c> gives up three quarters of the overflow.</summary>
        [Fact]
        public void Factors_WeightTheOverflow()
        {
            var r = Rules($"<div style='width:400px;flex-shrink:3;{Rule}'>A</div><div style='width:400px;{Rule}'>B</div>");

            double overflow = 2 * 300 - r[0];
            Assert.True(overflow > 20, "the fixture's row does not overflow");
            Assert.Equal(300 - overflow * 3 / 4, r[1], 0);
            Assert.Equal(300 - overflow / 4, r[2], 0);
        }

        /// <summary>The weight is the factor times the basis: a 300px item at factor 1 gives up half of what a 600px item at the same factor does — proportional, as the table always did.</summary>
        [Fact]
        public void TheWeight_IsTheFactorTimesTheBasis()
        {
            var r = Rules($"<div style='width:300px;{Rule}'>A</div><div style='width:600px;{Rule}'>B</div>");

            double overflow = 675 - r[0];
            Assert.True(overflow > 20, "the fixture's row does not overflow");
            Assert.Equal(225 - overflow / 3, r[1], 0);
            Assert.Equal(450 - overflow * 2 / 3, r[2], 0);
        }

        /// <summary>The <c>flex</c> shorthand's second number is the shrink factor, and <c>flex: none</c> is <c>0 0 auto</c>: both keep a 400px item at 300pt beside one that shrinks; <c>flex: 0 1 400px</c> shrinks with it.</summary>
        [Fact]
        public void TheShorthand_ReadsShrink()
        {
            var two = Rules($"<div style='flex:0 0 400px;{Rule}'>A</div><div style='width:400px;{Rule}'>B</div>");
            var none = Rules($"<div style='flex:none;width:400px;{Rule}'>A</div><div style='width:400px;{Rule}'>B</div>");
            var one = Rules($"<div style='flex:0 1 400px;{Rule}'>A</div><div style='width:400px;{Rule}'>B</div>");

            Assert.True(two[0] < 600, "the fixture's row does not overflow");
            Assert.Equal(300, two[1], 0);
            Assert.Equal(two[0] - 300, two[2], 0);
            Assert.Equal(300, none[1], 0);
            Assert.Equal(one[0] / 2, one[1], 0);
        }

        /// <summary>The gap counts against the line: two 400px items with a 40px gap, the first unshrinkable, leave the second the width less 300pt and the 30pt gap.</summary>
        [Fact]
        public void TheGap_CountsAgainstTheLine()
        {
            var r = Rules($"<div style='width:400px;flex-shrink:0;{Rule}'>A</div><div style='width:400px;{Rule}'>B</div>", container: "display:flex;column-gap:40px");

            Assert.True(r[0] < 600, "the fixture's row does not overflow");
            Assert.Equal(300, r[1], 0);
            Assert.Equal(r[0] - 300 - 30, r[2], 0);
        }

        /// <summary>Every factor 0 on an overflowing line cannot be honoured in a table: the items shrink in proportion, and the container says so once.</summary>
        [Fact]
        public void EveryFactorZero_IsShrunkProportionally_Reported()
        {
            var reports = new List<string>();
            var r = Rules($"<div style='width:400px;flex-shrink:0;{Rule}'>A</div><div style='width:400px;flex-shrink:0;{Rule}'>B</div>", reports);

            Assert.Equal(r[0] / 2, r[1], 0);
            Assert.Equal(r[0] / 2, r[2], 0);
            Assert.Single(reports, m => m.StartsWith("flex-shrink: 0 on every item", System.StringComparison.Ordinal));
        }

        /// <summary>A line that fits shrinks nothing: two 100px items keep 75pt each whatever their factors.</summary>
        [Fact]
        public void ALineThatFits_ShrinksNothing()
        {
            var r = Rules($"<div style='width:100px;flex-shrink:0;{Rule}'>A</div><div style='width:100px;flex-shrink:5;{Rule}'>B</div>");

            Assert.Equal(75, r[1], 0);
            Assert.Equal(75, r[2], 0);
        }

        /// <summary>The top rules' lengths, longest (the full-width reference) first, then the items' left to right.</summary>
        private static double[] Rules(string items, List<string>? reports = null, string container = "display:flex")
        {
            var options = Managed();
            if (reports != null) options.OnDiagnostic = d => { lock (reports) reports.Add(d.Detail ?? ""); };
            string c = DecodeStreams(ToPdf("<html><body><div style='border-top:1px solid #000'>REF</div>" +
                                           "<div style='" + container + "'>" + items + "</div></body></html>", options));
            var rules = StrokedSegments(c).Where(s => s.IsHorizontal).OrderByDescending(s => s.Y0).ToList();
            return new[] { System.Math.Abs(rules[0].X1 - rules[0].X0) }
                .Concat(rules.Skip(1).OrderBy(s => System.Math.Min(s.X0, s.X1)).Select(s => System.Math.Abs(s.X1 - s.X0)))
                .ToArray();
        }
    }
}
