// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Found against Chrome on the second Bootstrap page: <c>תוכן בתוך well.</c> in a right-to-left
    /// block printed <c>well.</c>, the period between the word and the Hebrew; Chrome prints
    /// <c>.well</c>, the period at the line's left end.
    /// </summary>
    public class StandardPieceBidiTests
    {
        [Fact]
        public void ALatinWordsTrailingPeriodIsDisplayedAsChromeDisplaysIt()
        {
            // The period is a piece of its own since a word is cut where its levels differ: it is
            // drawn left of the word, never glued to its right.
            var drawn = DrawnTexts(DecodeStreams(ToPdf("<html dir='rtl'><body><div>תוכן בתוך well.</div></body></html>")));
            Assert.DoesNotContain(drawn, t => t.Text == "well.");
            double word = drawn.Single(t => t.Text == "well").X, period = drawn.Single(t => t.Text == ".").X;
            Assert.True(period < word, $"the period at {period} is not left of the word at {word}");
        }
    }
}
