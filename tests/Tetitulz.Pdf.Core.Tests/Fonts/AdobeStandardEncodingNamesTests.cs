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
    /// <see cref="AdobeStandardEncodingNames"/> is the code → glyph-name shape of Adobe
    /// StandardEncoding (ISO 32000-1 Annex D.2). It exists because <c>Tetitulz.Pdf.Base</c> had
    /// written that table out by hand <b>twice</b> — once in <c>FontCache</c> for <c>/Encoding</c>
    /// and <c>/BaseEncoding</c> resolution, once in <c>Type1CharstringEvaluator</c> for
    /// <c>seac</c> — with no Core table of that shape to call. The two copies were identical at all
    /// 149 codes when they were deleted, so this consolidation fixed no defect; it removed the
    /// opportunity for one.
    ///
    /// <para><b>The Core census is now four, in four shapes, and that is deliberate.</b>
    /// <c>StandardEncodings.AdobeStandardEncoding</c> (code → Unicode, for extraction),
    /// <c>CffParser</c>'s code → SID map composed with the CFF standard strings (which is what its
    /// <c>encodingVal == 0</c> charset walk needs, so it cannot be rewired to read a name table),
    /// the CFF standard-string array itself (indexed by SID, not by character code), and this one.
    /// <see cref="StandardEncodingTableAgreementTests"/> pins the first two against each other;
    /// this file pins the new table against <b>both</b> of them, so no shape can drift alone.</para>
    ///
    /// <para><b>The count assertions are not padding.</b> Emptying every table makes them agree
    /// perfectly at every code, and every cross-check below would then pass vacuously — the trap
    /// the sibling file caught in itself.</para>
    /// </summary>
    public class AdobeStandardEncodingNamesTests
    {
        /// <summary>
        /// Annex D.2 defines 149 codes: the 95 printable ASCII positions 0x20–0x7E, plus 54 in the
        /// high range. Written out rather than derived, so a table edit has to disagree with a
        /// number a reader can check against the spec.
        /// </summary>
        private const int DefinedCodeCount = 149;

        private static List<int> DefinedCodes()
        {
            var codes = new List<int>();
            for (int code = 0; code < 256; code++)
                if (AdobeStandardEncodingNames.Get(code) != null)
                    codes.Add(code);
            return codes;
        }

        [Fact]
        public void TheTableDefinesTheHundredAndFortyNineCodesAnnexD2Defines()
        {
            var codes = DefinedCodes();

            Assert.Equal(DefinedCodeCount, codes.Count);
            Assert.Equal(256, AdobeStandardEncodingNames.Table.Count);

            // 0x20–0x7E contiguous, then nothing until 0xA1.
            for (int code = 0x20; code <= 0x7E; code++)
                Assert.NotNull(AdobeStandardEncodingNames.Get(code));
            for (int code = 0x7F; code <= 0xA0; code++)
                Assert.Null(AdobeStandardEncodingNames.Get(code));

            Assert.Equal(0x20, codes[0]);
            Assert.Equal(0xFB, codes[codes.Count - 1]);   // germandbls, the last defined code
        }

        /// <summary>
        /// The gaps inside the high range. These are the positions an "everything from 0xA1 up is
        /// defined" simplification would fill with a neighbour's glyph, which draws a wrong
        /// character rather than nothing.
        /// </summary>
        [Theory]
        [InlineData(0xB0)]   // between fl (0xAF) and endash (0xB1)
        [InlineData(0xB5)]
        [InlineData(0xBE)]
        [InlineData(0xC0)]
        [InlineData(0xC9)]
        [InlineData(0xCC)]
        [InlineData(0xE0)]
        [InlineData(0xE2)]
        [InlineData(0xFC)]
        public void AnUndefinedHighCodeIsNullRatherThanANeighboursGlyph(int code)
        {
            Assert.Null(AdobeStandardEncodingNames.Get(code));
        }

        /// <summary>
        /// The slots that make this StandardEncoding rather than ASCII or WinAnsi, pinned by value.
        /// 0x27 and 0x60 are where StandardEncoding and WinAnsi disagree below 0x80, and
        /// fraction/currency are the pair that sit at each other's "obvious" code.
        /// </summary>
        [Theory]
        [InlineData(0x20, "space")]
        [InlineData(0x27, "quoteright")]      // WinAnsi has quotesingle here
        [InlineData(0x41, "A")]
        [InlineData(0x60, "quoteleft")]       // WinAnsi has grave here
        [InlineData(0x7E, "asciitilde")]
        [InlineData(0xA4, "fraction")]        // NOT currency, which sits at 0xA8
        [InlineData(0xA8, "currency")]
        [InlineData(0xA9, "quotesingle")]
        [InlineData(0xD0, "emdash")]
        [InlineData(0xE8, "Lslash")]
        [InlineData(0xFB, "germandbls")]
        public void TheTableGivesTheAnnexD2GlyphName(int code, string expected)
        {
            Assert.Equal(expected, AdobeStandardEncodingNames.Get(code));
        }

        [Fact]
        public void GetRefusesACodeOutsideTheTableRatherThanThrowing()
        {
            Assert.Null(AdobeStandardEncodingNames.Get(-1));
            Assert.Null(AdobeStandardEncodingNames.Get(256));
            Assert.Null(AdobeStandardEncodingNames.Get(int.MinValue));
            Assert.Null(AdobeStandardEncodingNames.Get(int.MaxValue));
        }

        /// <summary>
        /// The cross-check against the direction Core already had. A glyph name is only useful if it
        /// denotes the character the code stands for, and the two tables were typed out
        /// independently from the same annex, so this is a real second opinion.
        /// </summary>
        [Fact]
        public void EveryGlyphNameDenotesTheCharacterTheUnicodeTableGivesTheSameCode()
        {
            var unicodeTable = StandardEncodings.AdobeStandardEncoding;
            var disagreements = new List<string>();
            var unresolvable = new List<string>();
            int compared = 0;

            for (int code = 0; code < 256; code++)
            {
                string glyphName = AdobeStandardEncodingNames.Get(code);
                if (glyphName == null) continue;

                if (!StandardEncodings.TryGetGlyphUnicode(glyphName, out char viaName))
                {
                    // A name the AGL cannot resolve is a defect on its own: it is a name a font
                    // lookup can never match.
                    unresolvable.Add($"0x{code:X2}: '{glyphName}'");
                    continue;
                }

                Assert.True(unicodeTable.TryGetValue(code, out char direct),
                    $"code 0x{code:X2} has glyph name '{glyphName}' but no entry in the Unicode table.");

                compared++;
                if (viaName != direct)
                    disagreements.Add(
                        $"0x{code:X2}: '{glyphName}' is U+{(int)viaName:X4} but the Unicode table says U+{(int)direct:X4}");
            }

            Assert.Empty(unresolvable);
            Assert.Empty(disagreements);
            Assert.Equal(DefinedCodeCount, compared);
        }

        /// <summary>
        /// The cross-check against <see cref="CffParser.TryGetStandardEncodingGlyphName"/>, which
        /// reaches the same answer the long way round — code → SID → CFF standard string. It stays a
        /// separate implementation because the code → SID half is what the CFF charset walk needs,
        /// so this comparison is not a tautology and does not become one.
        /// </summary>
        [Fact]
        public void TheTableAgreesWithTheCffSidDerivationAtEveryCode()
        {
            var disagreements = new List<string>();
            var onlyHere = new List<int>();
            var onlyCff = new List<int>();
            int compared = 0;

            for (int code = 0; code < 256; code++)
            {
                string here = AdobeStandardEncodingNames.Get(code);
                bool viaCff = CffParser.TryGetStandardEncodingGlyphName(code, out string cffName);

                if (here != null && !viaCff) { onlyHere.Add(code); continue; }
                if (here == null && viaCff) { onlyCff.Add(code); continue; }
                if (here == null) continue;

                compared++;
                if (here != cffName)
                    disagreements.Add($"0x{code:X2}: '{here}' here, '{cffName}' via the CFF SID table");
            }

            Assert.Empty(onlyHere);
            Assert.Empty(onlyCff);
            Assert.Empty(disagreements);
            Assert.Equal(DefinedCodeCount, compared);
        }
    }
}
