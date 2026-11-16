// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>&lt;pre&gt;</c> with the UA stylesheet's defaults, which the page replaces: monospace unless
    /// the page names a family for it — the engine forced monospace over a declared one — and no
    /// background, which browsers do not give it and the engine painted grey.
    /// </summary>
    public class PreDefaultsTests
    {
        private static byte[] Pdf(string html) => ToPdf("<html><body>" + html + "</body></html>");

        /// <summary>No background of its own: nothing filled round a plain <c>pre</c>; one the page sets is (the control).</summary>
        [Fact]
        public void APreHasNoBackgroundOfItsOwn()
        {
            Assert.Equal(0, PdfContentProbe.RectangleFillCount(DecodeStreams(Pdf("<pre>code</pre>"))));
            Assert.True(PdfContentProbe.RectangleFillCount(DecodeStreams(Pdf("<pre style='background-color:#eee'>code</pre>"))) >= 1);
        }

        /// <summary>The font resource the word <c>code</c> is set in.</summary>
        private static string FontOf(string html)
        {
            string c = DecodeStreams(Pdf(html));
            var m = System.Text.RegularExpressions.Regex.Match(c, @"/(\w+) [\d.]+ Tf(?:(?!Tf)[\s\S])*?\(code\)");
            Assert.True(m.Success, "the word was not drawn");
            return m.Groups[1].Value;
        }

        /// <summary>
        /// Monospace by default — a font other than a paragraph's — and the page's family where it
        /// names one: a <c>pre</c> in the generic <c>serif</c> — no name for the font lookup to go
        /// by, only the family's kind — is set in the paragraph's serif font, by the longhand and by
        /// the <c>font</c> shorthand alike. A named family (Arial) resolved by its name all along.
        /// </summary>
        [Fact]
        public void APagesFamilyReplacesMonospace()
        {
            string serif = FontOf("<p style='font-family:serif'>code</p>");
            Assert.NotEqual(serif, FontOf("<pre>code</pre>"));
            Assert.Equal(serif, FontOf("<pre style='font-family:serif'>code</pre>"));
            Assert.Equal(serif, FontOf("<pre style='font:10pt serif'>code</pre>"));
        }
    }
}
