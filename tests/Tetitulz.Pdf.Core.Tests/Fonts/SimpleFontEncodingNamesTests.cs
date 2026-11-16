// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// <see cref="SimpleFontEncodingNames.ByCode"/>: the glyph name each code selects as far as a simple
    /// font's dictionary says (ISO 32000-1 §9.6.6) — the remediator's <c>/CharSet</c> and the PDF/A
    /// subset check read it. The named tables themselves are pinned by Base's
    /// <c>NamedEncodingTableSelectionTests</c> and <c>LatinEncodingTableAgreementTests</c>.
    /// </summary>
    public class SimpleFontEncodingNamesTests
    {
        private static PdfObject Same(PdfObject o) => o;

        private static PdfDictionary Font(PdfObject encoding)
        {
            var font = new PdfDictionary();
            if (encoding != null) font.Set(PdfName.Encoding, encoding);
            return font;
        }

        private static PdfDictionary EncodingDictionary(string baseEncoding, params PdfObject[] differences)
        {
            var dictionary = new PdfDictionary();
            if (baseEncoding != null) dictionary.Set(PdfName.BaseEncoding, new PdfName(baseEncoding));
            if (differences.Length > 0)
            {
                var array = new PdfArray();
                foreach (var item in differences) array.Add(item);
                dictionary.Set(PdfName.Differences, array);
            }
            return dictionary;
        }

        [Theory]
        [InlineData("WinAnsiEncoding", 0x27, "quotesingle")]
        [InlineData("StandardEncoding", 0x27, "quoteright")]
        [InlineData("MacRomanEncoding", 0x80, "Adieresis")]
        [InlineData("MacExpertEncoding", 0x27, "Acutesmall")]
        public void ANamedEncoding_NamesEveryCodeItsTableDefines(string encoding, int code, string expected)
            => Assert.Equal(expected, SimpleFontEncodingNames.ByCode(Font(new PdfName(encoding)), Same)[code]);

        [Fact]
        public void ABaseEncodingWithDifferences_TheDifferencesWin()
        {
            var names = SimpleFontEncodingNames.ByCode(Font(EncodingDictionary("WinAnsiEncoding",
                new PdfNumber(65), new PdfName("Alpha"), new PdfName("Beta"), new PdfNumber(200), new PdfName("gamma"))), Same);
            Assert.Equal("Alpha", names[65]);
            Assert.Equal("Beta", names[66]);
            Assert.Equal("C", names[67]);          // the base table's
            Assert.Equal("gamma", names[200]);
        }

        /// <summary>
        /// With no <c>/BaseEncoding</c>, only the <c>/Differences</c> codes are named: the others are the
        /// font program's built-in encoding, which the dictionary does not state. No <c>/Encoding</c> names
        /// nothing, and an unknown name (case counts) is no table.
        /// </summary>
        [Fact]
        public void WhatTheDictionaryLeavesToTheProgram_IsNotNamed()
        {
            var differencesOnly = SimpleFontEncodingNames.ByCode(Font(EncodingDictionary(null, new PdfNumber(72), new PdfName("H"))), Same);
            Assert.Equal("H", Assert.Single(differencesOnly).Value);
            Assert.Empty(SimpleFontEncodingNames.ByCode(Font(null), Same));
            Assert.Empty(SimpleFontEncodingNames.ByCode(Font(new PdfName("winansiencoding")), Same));
            Assert.NotEmpty(SimpleFontEncodingNames.ByCode(Font(new PdfName("WinAnsiEncoding")), Same));
        }

        /// <summary>A <c>/Differences</c> code outside one byte, or not an integer, starts no run.</summary>
        [Fact]
        public void ADifferencesCodeOutsideOneByte_NamesNothing()
        {
            var names = SimpleFontEncodingNames.ByCode(Font(EncodingDictionary(null,
                new PdfNumber(300), new PdfName("far"), new PdfNumber(65.5), new PdfName("half"),
                new PdfNumber(-1), new PdfName("negative"), new PdfNumber(255), new PdfName("last"), new PdfName("past"))), Same);
            Assert.Equal("last", Assert.Single(names).Value);
            Assert.Equal(255, Assert.Single(names).Key);
        }

        /// <summary>
        /// Annex D, Table D.2's note: WinAnsi's unused codes above 40 octal are the bullet — 177, 201, 215,
        /// 217, 220 and 235 octal, as an independent implementation and another reader hold them; the control codes below space stay unnamed,
        /// and MacRoman's table is not touched.
        /// </summary>
        [Fact]
        public void WinAnsisUnusedCodesAboveSpace_AreTheBullet()
        {
            var winAnsi = SimpleFontEncodingNames.ByCode(Font(new PdfName("WinAnsiEncoding")), Same);
            foreach (int code in new[] { 127, 129, 141, 143, 144, 157 })
                Assert.Equal("bullet", winAnsi[code]);
            Assert.Equal("bullet", winAnsi[149]);
            Assert.Equal("quotesinglbase", winAnsi[130]);
            for (int code = 0; code < 32; code++) Assert.False(winAnsi.ContainsKey(code), "code " + code);
            Assert.Equal(6 + 1, System.Linq.Enumerable.Count(winAnsi.Values, n => n == "bullet"));

            var macRoman = SimpleFontEncodingNames.ByCode(Font(new PdfName("MacRomanEncoding")), Same);
            Assert.False(macRoman.ContainsKey(127));
        }
    }
}
