// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.TestHelpers;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Monospace under a page that names a family — a report's <c>body { font-family: Georgia }</c>:
    /// the inherited name won over the UA stylesheet's <c>pre</c>/<c>code</c> monospace, and over an
    /// author's generic <c>font-family: monospace</c>, because the font was chosen by name first and
    /// by kind only without one.
    /// </summary>
    /// <remarks>
    /// Read as the base font of the resource the word is SET in (<see cref="PdfContentProbe.BaseFontOf"/>):
    /// every page carries all twelve standard faces, so a <c>/Courier</c> anywhere in the file proves nothing.
    /// </remarks>
    public class MonospaceInheritanceTests
    {
        private static string UnderGeorgia(string body, string word)
            => PdfContentProbe.BaseFontOf(ToPdf("<html><body style='font-family:Georgia'>" + body + "</body></html>"), word);

        /// <summary>The control: under Georgia, a paragraph is not set in Courier.</summary>
        [Fact]
        public void AParagraphUnderANamedFamilyIsNotMonospace()
            => Assert.DoesNotContain("Courier", UnderGeorgia("<p>word</p>", "word"));

        [Fact]
        public void APreUnderANamedFamilyIsMonospace()
            => Assert.Equal("Courier", UnderGeorgia("<pre>word</pre>", "word"));

        [Fact]
        public void InlineCodeUnderANamedFamilyIsMonospace()
            => Assert.Equal("Courier", UnderGeorgia("<p>call <code>word</code></p>", "word"));

        [Fact]
        public void AGenericMonospaceDeclarationUnderANamedFamilyIsMonospace()
            => Assert.Equal("Courier", UnderGeorgia("<p>call <span style='font-family:monospace'>word</span></p>", "word"));

        /// <summary>The page's own family on the element still wins over the UA monospace.</summary>
        [Fact]
        public void AFamilyDeclaredOnThePreStillWins()
            => Assert.DoesNotContain("Courier", UnderGeorgia("<pre style='font-family:Georgia'>word</pre>", "word"));
    }
}
