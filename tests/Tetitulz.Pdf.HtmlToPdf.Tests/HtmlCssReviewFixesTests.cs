// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>Regressions from the 2026-11-12 review of the managed engine's CSS and image readers.</summary>
    public class HtmlCssReviewFixesTests
    {
        private static string Text(byte[] pdf)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            return PdfTextExtractor.ExtractText(doc);
        }

        /// <summary>
        /// An @layer block holds ordinary rules (CSS Cascade 5): they apply, in source order, and the
        /// approximation is said. Skipped whole and silently, a Tailwind v4 sheet lost every rule.
        /// </summary>
        [Fact]
        public void RulesInsideAnAtLayerBlock_Apply_AndTheApproximationIsSaid()
        {
            var diagnostics = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = diagnostics.Add;
            byte[] pdf = ToPdf("<style>@layer base { p { color: #ff0000 } }</style><p>X</p>", o);

            Assert.Contains("1 0 0 rg", DecodeStreams(pdf), StringComparison.Ordinal);
            Assert.Contains(diagnostics, d => d.Detail.Contains("@layer", StringComparison.Ordinal));
        }

        /// <summary>A @container block is not evaluated, and says so rather than vanishing.</summary>
        [Fact]
        public void AnAtContainerBlock_IsReported()
        {
            var diagnostics = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = diagnostics.Add;
            ToPdf("<style>@container (min-width: 1px) { p { color: #00ff00 } }</style><p>X</p>", o);
            Assert.Contains(diagnostics, d => d.Detail.Contains("@container", StringComparison.Ordinal));
        }

        /// <summary>
        /// A pseudo-element's content is read from declarations cut at their top-level semicolons:
        /// <c>content: "a;b"</c> is one string. Cut at every semicolon, it generated nothing, silently.
        /// </summary>
        [Fact]
        public void PseudoElementContentHoldingASemicolon_IsGenerated()
        {
            Assert.Contains("a;bX", Text(ToPdf("<style>p::before{content:\"a;b\"}</style><p>X</p>")).Replace(" ", ""), StringComparison.Ordinal);
            Assert.Contains("abX", Text(ToPdf("<style>p::before{content:\"ab\"}</style><p>X</p>")).Replace(" ", ""), StringComparison.Ordinal);
        }

        /// <summary>
        /// A srcset candidate's URL runs to the next white space, its commas included (HTML Standard
        /// §4.8.4.3.10): a data: URI candidate is drawn, where cutting at every comma drew nothing and
        /// reported the base64 tail as an unloadable file. Without srcset the same image draws (the control).
        /// </summary>
        [Fact]
        public void ASrcsetCandidateHoldingACommaInItsUrl_IsDrawn()
        {
            string b64 = Convert.ToBase64String(MakeRgbPng(8, 8));
            byte[] withSrcset = ToPdf($"<img src=\"data:image/png;base64,{b64}\" srcset=\"data:image/png;base64,{b64} 2x\" width=\"20\">");
            Assert.Contains("/Subtype /Image", Raw(withSrcset), StringComparison.Ordinal);

            byte[] plain = ToPdf($"<img src=\"data:image/png;base64,{b64}\" width=\"20\">");
            Assert.Contains("/Subtype /Image", Raw(plain), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("a.png 1x, b.png 2x", "a.png|1x;b.png|2x")]
        [InlineData("a.png, b.png 2x", "a.png|;b.png|2x")]
        [InlineData("https://img.example/w_100,h_50/x.jpg 100w, https://img.example/w_200,h_100/x.jpg 200w", "https://img.example/w_100,h_50/x.jpg|100w;https://img.example/w_200,h_100/x.jpg|200w")]
        [InlineData("  , a.png  ,, ", "a.png|")]
        [InlineData("a.png 1x (foo,bar), b.png 2x", "a.png|1x (foo,bar);b.png|2x")]   // a comma inside parentheses is the descriptor's
        public void SrcsetCandidates_AreParsedAsTheStandardSays(string srcset, string expected)
        {
            var parts = new List<string>();
            foreach (var (url, d) in HtmlToBuilderMapper.SrcSetCandidates(srcset)) parts.Add(url + "|" + d);
            Assert.Equal(expected, string.Join(";", parts));
        }
    }
}
