// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text
{
    /// <summary>
    /// The single five-entity XML escaper, which replaced three byte-identical copies:
    /// <c>PdfAConverter.XmlEscape</c>, <c>PdfXConverter.XmlEscape</c> and Base's
    /// <c>SvgSyntax.XmlEsc</c>.
    ///
    /// <para><b>Nothing asserted any of the three.</b> The two converters' copies had no test at
    /// all — a title containing <c>&amp;</c> or <c>&lt;</c> would have produced a malformed XMP
    /// packet with nothing to notice — so consolidating them without covering the survivor would
    /// have been a deletion wearing a merge's clothes.</para>
    /// </summary>
    public class XmlEscapeTests
    {
        /// <summary>
        /// The ampersand must be replaced FIRST, or every other entity it emits gets escaped again.
        /// </summary>
        /// <remarks>
        /// Swapping the order gives <c>a&amp;amp;lt;b&amp;amp;c</c> where the correct order gives
        /// <c>a&amp;lt;b&amp;amp;c</c> — the entity's own ampersand escaped a second time.
        /// <para><b>Mutating the order reddens the single-character theory rows too</b>, which is
        /// worth recording because the first draft of this comment claimed the opposite: that only
        /// a mixed input could see it. Escaping <c>"&lt;"</c> alone is <i>not</i> order-independent
        /// — under the swap it comes out <c>&amp;amp;lt;</c>. This case is kept anyway because it
        /// states the rule directly and shows the corruption in text a caller would really pass.</para>
        /// </remarks>
        [Fact]
        public void TheAmpersandIsEscapedFirstSoNothingIsDoubleEscaped()
        {
            Assert.Equal("a&lt;b&amp;c", XmlEscape.EscapeText("a<b&c"));
        }

        [Theory]
        [InlineData("&", "&amp;")]
        [InlineData("<", "&lt;")]
        [InlineData(">", "&gt;")]
        [InlineData("\"", "&quot;")]
        [InlineData("'", "&apos;")]
        public void EachOfTheFiveEntitiesIsEscaped(string input, string expected)
        {
            Assert.Equal(expected, XmlEscape.EscapeText(input));
        }

        /// <summary>
        /// The apostrophe is escaped as <c>&amp;apos;</c>, which is what makes the result safe in a
        /// single-quoted attribute — and what makes this dialect wrong for HTML 4, where
        /// <c>&amp;apos;</c> is not a defined entity. Pinned so a future "unification" of the tree's
        /// seven escapers across four dialects has to argue with a test.
        /// </summary>
        [Fact]
        public void TheApostropheUsesTheNamedEntityNotTheNumericOne()
        {
            Assert.Equal("&apos;", XmlEscape.EscapeText("'"));
            Assert.DoesNotContain("&#39;", XmlEscape.EscapeText("'"));
        }

        [Fact]
        public void TextWithNothingToEscapeIsReturnedUnchanged()
        {
            Assert.Equal("plain text 123", XmlEscape.EscapeText("plain text 123"));
        }

        /// <summary>
        /// Null yields the empty string. <c>SvgSyntax</c> guarded; the two converters would have
        /// thrown <see cref="System.NullReferenceException"/> — never a contract. The merge took the
        /// guarded behaviour, which no caller can currently observe either way.
        /// </summary>
        [Fact]
        public void NullYieldsTheEmptyStringRatherThanThrowing()
        {
            Assert.Equal("", XmlEscape.EscapeText(null));
        }
    }
}
