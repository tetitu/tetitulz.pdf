// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects
{
    /// <summary>
    /// <see cref="PdfString"/>'s two halves must be each other's inverse: whatever
    /// <see cref="PdfString(string)"/> writes, <see cref="PdfString.GetText"/> must read back.
    ///
    /// <para><b>They were not.</b> The constructor wrote a single Latin-1 byte for any character
    /// at or below U+00FF; <c>GetText</c> decodes an un-BOM'd string as <b>PDFDocEncoding</b>
    /// (§7.9.2.2), which differs from Latin-1 at 0x18-0x1F and 0x80-0xA0. So a NO-BREAK SPACE went
    /// in and a Euro sign came out, and the whole of those two ranges came back as different
    /// characters - in document titles, alt text, tooltips and annotation <c>/Contents</c>.</para>
    ///
    /// <para>The exhaustive test below is the point: the defect is a <i>disagreement between two
    /// tables</i>, so testing one hand-picked character would have closed one cell of it. Sweeping
    /// the whole single-byte range is what makes the claim "these are inverses" rather than "this
    /// example works".</para>
    /// </summary>
    public class PdfStringRoundTripTests
    {
        [Fact]
        public void EveryCharacterUpToU00FFSurvivesAWriteThenRead()
        {
            var broken = new List<string>();

            for (int c = 0; c <= 0xFF; c++)
            {
                string original = ((char)c).ToString();
                string readBack = new PdfString(original).GetText();
                if (readBack != original)
                    broken.Add($"U+{c:X4} -> U+{(int)readBack[0]:X4}");
            }

            Assert.True(
                broken.Count == 0,
                "PdfString(string) and GetText() are not inverses for: " + string.Join(", ", broken));
        }

        /// <summary>
        /// The characters the old predicate got wrong, named individually so a regression says
        /// which range came back rather than only that something did. 0x9F is deliberately absent:
        /// it is undefined in PDFDocEncoding and the decoder correctly passes it through, which is
        /// why the fix reads the decode table rather than testing the range 0x80..0xA0.
        /// </summary>
        [Theory]
        [InlineData((char)0x18)]   // breve - low end of the first divergent range
        [InlineData((char)0x1F)]   // tilde accent - high end of it
        [InlineData((char)0x80)]   // read back as U+2022 BULLET before the fix
        [InlineData((char)0x9E)]   // read back as U+017E z-caron before the fix
        [InlineData((char)0xA0)]   // NO-BREAK SPACE, read back as U+20AC EURO SIGN before the fix
        public void TheCharactersPdfDocEncodingReassignsAreWrittenAsUtf16(char c)
        {
            var s = new PdfString(c.ToString());

            // It must take the UTF-16BE route - a single Latin-1 byte is what broke it.
            Assert.Equal(0xFE, s.RawBytes[0]);
            Assert.Equal(0xFF, s.RawBytes[1]);
            Assert.Equal(c.ToString(), s.GetText());
        }

        /// <summary>
        /// The fix must not have widened the UTF-16 route to everything: plain ASCII and the
        /// undisturbed part of Latin-1 must still cost one byte per character, because that is
        /// what keeps existing output byte-identical.
        /// </summary>
        [Theory]
        [InlineData("Hello, world")]
        [InlineData("café")]           // U+00E9, agrees between Latin-1 and PDFDocEncoding
        [InlineData("")]              // undefined in PDFDocEncoding, passed through
        [InlineData("¡ÿ")]        // 0xA1..0xFF agree with Latin-1
        public void TextTheTwoEncodingsAgreeOnIsStillWrittenOneBytePerCharacter(string text)
        {
            var s = new PdfString(text);

            Assert.Equal(text.Length, s.RawBytes.Length);
            Assert.NotEqual(0xFE, s.RawBytes[0]);
            Assert.Equal(text, s.GetText());
        }

        [Fact]
        public void TextAboveU00FFStillTakesTheUtf16RouteAndSurvives()
        {
            const string hebrew = "שלום";   // shalom
            var s = new PdfString(hebrew);

            Assert.Equal(0xFE, s.RawBytes[0]);
            Assert.Equal(0xFF, s.RawBytes[1]);
            Assert.Equal(hebrew, s.GetText());
        }

        /// <summary>
        /// Guards the fix against the vacuous form of itself. If <c>RequiresUtf16</c> were changed
        /// to return <c>true</c> unconditionally, every round-trip test above would still pass -
        /// UTF-16 round-trips everything. This pins that the cheap route is still taken wherever it
        /// can be, so "always UTF-16" is not a way to go green.
        /// </summary>
        [Fact]
        public void MostOfLatin1StillUsesTheSingleByteRouteSoAlwaysUtf16CannotPassTheseTests()
        {
            int singleByte = Enumerable.Range(0, 0x100)
                .Count(c => new PdfString(((char)c).ToString()).RawBytes.Length == 1);

            // 256 code points, less the 8 at 0x18-0x1F and the 31 at 0x80-0x9E, less 0xA0.
            // 0x9F is NOT reassigned by PDFDocEncoding, so it stays on the cheap route.
            Assert.Equal(256 - 8 - 31 - 1, singleByte);
        }
    }
}
