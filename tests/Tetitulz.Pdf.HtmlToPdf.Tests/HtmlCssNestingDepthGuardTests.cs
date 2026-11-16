// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// The managed engine's CSS at-rule nesting cap (<c>HtmlToBuilderMapper.MaxCssNestingDepth</c>).
    ///
    /// <para><c>ExtractRules</c> flattens <c>@media</c>/<c>@supports</c> by recursing into the
    /// block body, so nesting depth in the source <c>&lt;style&gt;</c> is recursion depth in the
    /// converter. <c>MaxCssRules</c> cannot bound it — a nesting-only payload adds zero rules —
    /// and a <c>StackOverflowException</c> is uncatchable, so an unguarded recursion kills a host
    /// converting untrusted HTML rather than failing its conversion.</para>
    ///
    /// <para>These assert the guard's own evidence — <b>which rules reached the page</b> and the
    /// reported diagnostic — never a wall clock. The over-deep payload is only 40 levels precisely
    /// so that the drop is attributable to the cap and not to a stack that ran out anyway; the
    /// 20&#160;000-level case is the separate adversarial test.</para>
    /// </summary>
    public class HtmlCssNestingDepthGuardTests
    {
        private const string RedFill = "1 0 0 rg";   // p{color:#ff0000} as the exporter emits it

        private static HtmlToPdfOptions Managed() =>
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed };

        /// <summary><paramref name="levels"/> nested <c>@media all{…}</c> wrappers around one red rule.</summary>
        private static string NestedRedRule(int levels)
        {
            var sb = new StringBuilder("<style>");
            for (int i = 0; i < levels; i++) sb.Append("@media all{");
            sb.Append("p{color:#ff0000}");
            for (int i = 0; i < levels; i++) sb.Append('}');
            sb.Append("</style><p>GUARDED</p>");
            return sb.ToString();
        }

        private static (byte[] Pdf, List<BuilderDiagnostic> Seen) Convert(string html)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = seen.Add;
            return (HtmlToPdfConverter.ConvertHtmlToBytes(html, o), seen);
        }

        // ------------------------------------------------------------------ the cap's effect

        /// <summary>
        /// Nesting a browser would ever see still cascades. Without this the guard could be
        /// satisfied by a cap of 0, which would silently delete every real <c>@media print</c>
        /// stylesheet.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(16)]   // the cap itself: the last depth that must still apply
        public void AtRuleNesting_WithinCap_StillCascades(int levels)
        {
            var (pdf, _) = Convert(NestedRedRule(levels));
            string dec = ManagedTestSupport.DecodeStreams(pdf);

            Assert.Contains(ManagedTestSupport.DrawnTexts(dec), t => t.Text == "GUARDED");
            Assert.Contains(RedFill, dec);
        }

        /// <summary>
        /// Past the cap the block is not flattened, so the rule inside it never reaches the page —
        /// the paragraph is still drawn, in the default colour. This is the guard's evidence; a
        /// conversion that produced a blank page would fail the first assertion.
        /// </summary>
        [Theory]
        [InlineData(17)]   // one past the cap
        [InlineData(40)]
        public void AtRuleNesting_BeyondCap_IsNotFlattened(int levels)
        {
            var (pdf, seen) = Convert(NestedRedRule(levels));
            string dec = ManagedTestSupport.DecodeStreams(pdf);

            Assert.Contains(ManagedTestSupport.DrawnTexts(dec), t => t.Text == "GUARDED");
            Assert.DoesNotContain(RedFill, dec);
            Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
                                       && d.Detail.Contains("nesting deeper than 16"));
        }

        /// <summary>
        /// The refusal is reported once per conversion, not once per refused block: a payload with
        /// thousands of over-deep siblings must not flood a host's telemetry sink.
        /// </summary>
        [Fact]
        public void AtRuleNesting_BeyondCap_ReportsOncePerConversion()
        {
            var sb = new StringBuilder("<style>");
            for (int i = 0; i < 5; i++)
            {
                for (int j = 0; j < 20; j++) sb.Append("@media all{");
                sb.Append("p{color:#ff0000}");
                for (int j = 0; j < 20; j++) sb.Append('}');
            }
            sb.Append("</style><p>GUARDED</p>");

            var (_, seen) = Convert(sb.ToString());

            Assert.Equal(1, seen.Count(d => d.Kind == BuilderDiagnosticKind.ContentApproximated
                                            && d.Detail.Contains("nesting deeper than 16")));
        }

        // ------------------------------------------------------------------ adversarial input

        /// <summary>
        /// The payload the cap exists for: 20&#160;000 nested at-rules in one <c>&lt;style&gt;</c>,
        /// which unguarded recurses 20&#160;000 frames deep and takes the process down with an
        /// uncatchable <c>StackOverflowException</c>.
        ///
        /// <para>Asserted on the output, not on elapsed time: a PDF comes back, the body text is
        /// on the page, and the buried rule is absent.</para>
        /// </summary>
        [Fact]
        public void AdversarialAtRuleNesting_DoesNotStackOverflow()
        {
            var (pdf, seen) = Convert(NestedRedRule(20_000));
            string dec = ManagedTestSupport.DecodeStreams(pdf);

            Assert.Equal((byte)'%', pdf[0]);
            Assert.Contains(ManagedTestSupport.DrawnTexts(dec), t => t.Text == "GUARDED");
            Assert.DoesNotContain(RedFill, dec);
            Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated);
        }

        /// <summary>
        /// Same shape with the braces never closed, which is what an attacker sends when the
        /// closing half would double the payload. The block scanner runs to end-of-input and the
        /// substring it hands the recursion is still one level shallower, so depth still tracks
        /// the <c>@</c> count — the cap has to hold here too.
        /// </summary>
        [Fact]
        public void AdversarialUnclosedAtRuleNesting_DoesNotStackOverflow()
        {
            var sb = new StringBuilder("<style>");
            for (int i = 0; i < 20_000; i++) sb.Append("@media all{");
            sb.Append("p{color:#ff0000}</style><p>GUARDED</p>");

            var (pdf, _) = Convert(sb.ToString());
            string dec = ManagedTestSupport.DecodeStreams(pdf);

            Assert.Equal((byte)'%', pdf[0]);
            Assert.Contains(ManagedTestSupport.DrawnTexts(dec), t => t.Text == "GUARDED");
            Assert.DoesNotContain(RedFill, dec);
        }
    }
}
