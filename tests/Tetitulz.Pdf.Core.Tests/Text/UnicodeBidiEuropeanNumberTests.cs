// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text
{
    /// <summary>
    /// UAX #9 W7 — a European number whose last preceding strong type is <c>L</c> becomes <c>L</c> —
    /// run per isolating run sequence, starting from the sequence's own direction (sos).
    /// </summary>
    /// <remarks>
    /// <para><b>What was wrong:</b> the scan started from no direction at all and ran through
    /// isolates. Digits with no letter before them therefore stayed <c>EN</c>; N1 counts <c>EN</c>
    /// as right-to-left, so the space between two numbers resolved right-to-left and the numbers
    /// swapped places. HTML's <c>&lt;span dir="ltr"&gt;24/09/2026 07:28&lt;/span&gt;</c> — an LTR
    /// isolate — rendered as "07:28 24/09/2026", in right-to-left and left-to-right paragraphs
    /// alike.</para>
    /// <para>The fixtures are digits and spaces only, because that is the shape that exposes it: a
    /// Latin letter anywhere before the digits would make W7 fire from the letter and hide the
    /// missing sos.</para>
    /// </remarks>
    public class UnicodeBidiEuropeanNumberTests
    {
        private const char LRI = '⁦';
        private const char PDI = '⁩';

        /// <summary>The visual string, with the isolate controls (which are never drawn) removed.</summary>
        private static string Visual(string s, BaseDirection dir)
        {
            var p = UnicodeBidi.ResolveParagraph(s, dir);
            return new string(p.VisualOrder(0, s.Length)
                .Select(i => s[i])
                .Where(c => c != LRI && c != PDI)
                .ToArray());
        }

        [Fact]
        public void NumbersInsideAnLtrIsolate_KeepTheirOrder_InAnRtlParagraph()
        {
            Assert.Equal("12 34", Visual(LRI + "12 34" + PDI, BaseDirection.RightToLeft));
        }

        [Fact]
        public void NumbersInsideAnLtrIsolate_KeepTheirOrder_InAnLtrParagraph()
        {
            Assert.Equal("12 34", Visual(LRI + "12 34" + PDI, BaseDirection.LeftToRight));
        }

        /// <summary>
        /// The control that gives the two above their meaning: with no isolate, a right-to-left
        /// paragraph DOES order the numbers right to left — UAX #9 says so, and a browser shows
        /// <c>&lt;p dir="rtl"&gt;12 34&lt;/p&gt;</c> as "34 12". The isolate is what changes it.
        /// </summary>
        [Fact]
        public void WithoutAnIsolate_AnRtlParagraphOrdersTheNumbersRightToLeft()
        {
            Assert.Equal("34 12", Visual("12 34", BaseDirection.RightToLeft));
        }

        /// <summary>
        /// A left-to-right paragraph that opens with numbers keeps them first: sos is <c>L</c>, so
        /// W7 makes them <c>L</c> before any letter is seen. They used to swap with the Hebrew after
        /// them.
        /// </summary>
        [Fact]
        public void AnLtrParagraphThatOpensWithNumbers_KeepsThemBeforeTheHebrew()
        {
            const string Hebrew = "אב";          // אב
            const string Reversed = "בא";        // בא — Hebrew reads right to left

            Assert.Equal("12 34 " + Reversed, Visual("12 34 " + Hebrew, BaseDirection.LeftToRight));
        }

        /// <summary>
        /// The W7 pass recurses once per matched isolate, and is bounded only by the pairing cap in
        /// <c>MatchIsolates</c> (125, UAX #9's max_depth). Fifty thousand nested initiators must
        /// resolve — an uncapped pairing would recurse fifty thousand frames deep and take the
        /// process down with an uncatchable stack overflow — and the digits in the innermost
        /// matched isolate must still resolve left-to-right.
        /// </summary>
        [Fact]
        public void DeeplyNestedIsolates_TheWeakTypeRecursionIsBoundedByTheDepthCap()
        {
            const int Depth = 50_000;
            string s = new string(LRI, Depth) + "12 34" + new string(PDI, Depth);

            Assert.Equal("12 34", Visual(s, BaseDirection.LeftToRight));
        }
    }
}
