// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// §9.6.2.2: a font that names one of the Standard 14 may omit <c>/Widths</c>, and its
    /// advances then come from the built-in AFM metrics rather than from <c>/MissingWidth</c>.
    ///
    /// <para><b>This tier existed only on the Type 1 side.</b> §9.6.3 Table 113 carries the same
    /// "Required except for the standard 14 fonts" note as Table 111, so
    /// <c>/Subtype /TrueType /BaseFont /Arial</c> with no <c>/Widths</c> is legal and is
    /// something real producers emit — and it measured <c>/MissingWidth</c>, which defaults to
    /// <b>0</b>. Every glyph then draws at zero advance and the whole run piles up at one x.</para>
    ///
    /// <para>The second half is the alias problem: the Type 1 copy gated on an exact-name set, so
    /// <c>ArialMT</c>, <c>Arial,Bold</c> and a subset-tagged <c>ABCDEF+Helvetica</c> all missed
    /// the tier that exists for them and fell through to zero as well.</para>
    /// </summary>
    public class Standard14WidthFallbackTests
    {
        /// <summary>Helvetica's AFM advance for 'A' — the value the fallback must produce.</summary>
        private const double HelveticaA = 667;

        private static PdfFont LoadSimpleFont(string subtype, string baseFont)
        {
            // A one-page document whose only font is the dictionary under test. No /Widths,
            // no /FirstChar, no /FontDescriptor - so /MissingWidth is absent and defaults to 0,
            // which is exactly the shape that measured zero.
            var sb = new StringBuilder();
            sb.Append("%PDF-1.7\n");
            var bodies = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
                "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> >>",
                $"<< /Type /Font /Subtype /{subtype} /BaseFont /{baseFont} /Encoding /WinAnsiEncoding >>",
            };

            var offsets = new int[bodies.Length + 1];
            for (int i = 0; i < bodies.Length; i++)
            {
                offsets[i + 1] = sb.Length;
                sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
            }
            int xref = sb.Length;
            sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n');
            sb.Append("0000000000 65535 f \n");
            for (int i = 1; i <= bodies.Length; i++)
                sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
            sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
            sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

            var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
            var page = doc.Pages[0];
            var fonts = page.Resources.GetDictionary(PdfName.Font);
            var fontDict = doc.ResolveObject(fonts[new PdfName("F1")]) as PdfDictionary;
            return PdfFont.Create(fontDict, doc);
        }

        /// <summary>
        /// The defect itself. A TrueType dictionary naming a Standard-14 font with no
        /// <c>/Widths</c> measured 0 for every code.
        /// </summary>
        [Fact]
        public void ATrueTypeStandard14FontWithNoWidthsMeasuresItsAfmAdvanceNotZero()
        {
            var font = LoadSimpleFont("TrueType", "Helvetica");

            Assert.Equal(HelveticaA, font.GetWidth('A'), 3);
        }

        /// <summary>
        /// The Type 1 side, which already worked — asserted alongside so the two subtypes are
        /// pinned to the same answer and cannot drift apart again.
        /// </summary>
        [Fact]
        public void AType1Standard14FontWithNoWidthsMeasuresTheSameAfmAdvance()
        {
            var font = LoadSimpleFont("Type1", "Helvetica");

            Assert.Equal(HelveticaA, font.GetWidth('A'), 3);
        }

        [Fact]
        public void BothSimpleSubtypesAgreeOnEveryAsciiAdvance()
        {
            var trueType = LoadSimpleFont("TrueType", "Helvetica");
            var type1 = LoadSimpleFont("Type1", "Helvetica");

            for (int code = 32; code < 127; code++)
                Assert.Equal(type1.GetWidth(code), trueType.GetWidth(code), 3);
        }

        /// <summary>
        /// The alias half. These are the names real producers write, and an exact-name gate on this tier
        /// would miss every one of them — on <i>both</i> subtypes.
        /// </summary>
        [Theory]
        [InlineData("Type1", "ArialMT")]
        [InlineData("TrueType", "ArialMT")]
        [InlineData("Type1", "Arial")]
        [InlineData("TrueType", "Arial")]
        [InlineData("TrueType", "ABCDEF+Helvetica")]   // subset-tagged
        public void AStandard14AliasResolvesToTheSameAfmAdvance(string subtype, string baseFont)
        {
            var font = LoadSimpleFont(subtype, baseFont);

            Assert.Equal(HelveticaA, font.GetWidth('A'), 3);
        }

        /// <summary>
        /// Stops the vacuous fix. Returning an AFM width for <i>everything</i> would satisfy every
        /// assertion above, so this pins that a font naming none of the fourteen still falls
        /// through to <c>/MissingWidth</c> — 0 here, because the fixture has no descriptor.
        /// </summary>
        [Theory]
        [InlineData("Type1")]
        [InlineData("TrueType")]
        public void AFontThatNamesNoStandard14FontStillFallsThroughToMissingWidth(string subtype)
        {
            var font = LoadSimpleFont(subtype, "Wingdings-Regular");

            Assert.Equal(0d, font.GetWidth('A'), 3);
        }

        /// <summary>
        /// A width recovered from the AFM tables is not an explicit <c>/Widths</c> entry, and
        /// space detection and combining-mark placement need to tell the two apart. The fallback
        /// must not have leaked into <c>HasExplicitWidth</c>.
        /// </summary>
        [Theory]
        [InlineData("Type1")]
        [InlineData("TrueType")]
        public void TheAfmFallbackIsNotReportedAsAnExplicitWidth(string subtype)
        {
            var font = LoadSimpleFont(subtype, "Helvetica");

            Assert.True(font.GetWidth('A') > 0);
            Assert.False(font.HasExplicitWidth('A'));
        }
    }
}
