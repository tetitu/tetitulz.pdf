// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// Core holds Adobe StandardEncoding (ISO 32000-1 Annex D.2) TWICE, in two shapes, for two
    /// consumers that never meet:
    ///
    /// <list type="bullet">
    /// <item><see cref="StandardEncodings.AdobeStandardEncoding"/> is code to UNICODE, and is what
    /// text extraction reads to turn a byte in a simple font into a character.</item>
    /// <item><see cref="CffParser.TryGetStandardEncodingGlyphName"/> is code to GLYPH NAME via the
    /// CFF SID table, and is what <c>seac</c> reads to find the base and accent of a composed
    /// glyph -- always through StandardEncoding, never through the font's own /Encoding.</item>
    /// </list>
    ///
    /// <para><b>They are not merged, and this test is the price of that.</b> A merge needs one of
    /// two things, and both cost more than they buy: deriving the Unicode map from the SID map adds
    /// a type-initializer edge from a leaf encoding table into the CFF parser, which is the exact
    /// shape that crashed the Base test host once (see CLAUDE.md, "share downward into a leaf, never
    /// sideways between peers"); deriving the SID map from the Unicode map means SIDs stop being a
    /// CFF-local concept. So they stay separate and this asserts they agree -- which is a real
    /// assertion precisely BECAUSE they are separate. Post-merge it would be a tautology.</para>
    ///
    /// <para><b>A drift here is silent in both directions.</b> A wrong Unicode extracts the wrong
    /// character from a correctly-rendered page; a wrong glyph name draws the wrong accent on a
    /// composed glyph, or drops it. Neither table's own consumer can see the other's answer.</para>
    ///
    /// <para><b>Two further copies exist.</b>
    /// <c>Plugins.PostScript.Interpreter.PsEncodings.StandardEncodingNames</c> (PLRM Appendix E.1,
    /// code-to-name shape) is deliberately out of reach: a plugin may not see Core internals, and
    /// widening <c>CffParser</c> to public for a test would put a CFF implementation detail in the
    /// package's permanent API surface. <see cref="AdobeStandardEncodingNames"/> is the plainly
    /// indexed code-to-name table Core gained in 2026-08, when the two byte-identical hand-written
    /// copies in <c>Tetitulz.Pdf.Base</c> were folded into it; it is pinned against BOTH tables
    /// compared here by <see cref="AdobeStandardEncodingNamesTests"/>, so the shapes cannot drift
    /// pairwise.</para>
    /// </summary>
    public class StandardEncodingTableAgreementTests
    {
        /// <summary>SIDs 1-149 are contiguous over StandardEncoding, so every defined code has one.</summary>
        private const int DefinedCodeCount = 149;

        [Fact]
        public void TheUnicodeTableAndTheCffSidTableDefineTheSameCodes()
        {
            var unicodeTable = StandardEncodings.AdobeStandardEncoding;
            var onlyUnicode = new List<int>();
            var onlySid = new List<int>();

            for (int code = 0; code < 256; code++)
            {
                bool hasName = CffParser.TryGetStandardEncodingGlyphName(code, out _);
                bool hasChar = unicodeTable.ContainsKey(code);
                if (hasChar && !hasName) onlyUnicode.Add(code);
                if (hasName && !hasChar) onlySid.Add(code);
            }

            Assert.Empty(onlyUnicode);
            Assert.Empty(onlySid);

            // Without this, emptying BOTH tables would agree perfectly and pass.
            Assert.Equal(DefinedCodeCount, unicodeTable.Count);
        }

        [Fact]
        public void EveryCodeResolvesToTheSameCharacterThroughEitherTable()
        {
            var unicodeTable = StandardEncodings.AdobeStandardEncoding;
            var disagreements = new List<string>();
            int compared = 0;

            for (int code = 0; code < 256; code++)
            {
                if (!CffParser.TryGetStandardEncodingGlyphName(code, out string glyphName)) continue;
                if (!unicodeTable.TryGetValue(code, out char direct)) continue;

                // A name the AGL table cannot resolve is a defect in its own right: it is a name
                // seac can hand to a glyph lookup that will never match.
                Assert.True(PdfGlyphNames.TryGetUnicode(glyphName, out char viaName),
                    $"code 0x{code:X2} maps to glyph name '{glyphName}', which has no Unicode value.");

                compared++;
                if (viaName != direct)
                    disagreements.Add($"0x{code:X2}: '{glyphName}' is U+{(int)viaName:X4} but the Unicode table says U+{(int)direct:X4}");
            }

            Assert.Empty(disagreements);
            Assert.Equal(DefinedCodeCount, compared);
        }

        /// <summary>
        /// The two slots that make this StandardEncoding rather than ASCII, pinned by value in both
        /// tables. They are the ones a "the ASCII range is just ASCII" simplification silently
        /// breaks, and the only place the two encodings PsEncodings builds differ below 0x80.
        /// </summary>
        [Theory]
        [InlineData(0x27, "quoteright", '\u2019')]
        [InlineData(0x60, "quoteleft", '\u2018')]
        [InlineData(0xA4, "fraction", '\u2044')]   // NOT currency, which sits at 0xA8
        [InlineData(0xA8, "currency", '\u00A4')]
        [InlineData(0xA9, "quotesingle", '\u0027')]
        public void TheTypographicSlotsAreTheSameGlyphInBothTables(int code, string expectedName, char expectedChar)
        {
            Assert.True(CffParser.TryGetStandardEncodingGlyphName(code, out string glyphName));
            Assert.Equal(expectedName, glyphName);
            Assert.Equal(expectedChar, StandardEncodings.AdobeStandardEncoding[code]);
        }
    }
}
