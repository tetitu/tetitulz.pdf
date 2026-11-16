// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// When the opt-in <see cref="HtmlToPdfOptions.SvgRasterizer"/> throws (a malformed SVG — an unclosed
    /// tag, a DOCTYPE the hardened reader refuses) or returns nothing, the SVG used to be dropped from the
    /// PDF with nothing on <c>OnDiagnostic</c> — the <c>catch { data = null; }</c> swallowed it. It is now
    /// reported as <see cref="BuilderDiagnosticKind.ImageDecodeFailed"/> (2026-10-23).
    /// </summary>
    public class SvgRasterizerDiagnosticTests
    {
        private const string Svg = "<p><svg width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg></p>";

        [Theory]
        [InlineData(true)]    // rasterizer throws
        [InlineData(false)]   // rasterizer returns null
        public void AnSvgTheRasterizerCannotHandle_IsReported_NotDroppedSilently(bool throws)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.SvgRasterizer = throws
                ? new Func<string, byte[]?>(_ => throw new InvalidOperationException("malformed svg"))
                : new Func<string, byte[]?>(_ => null);
            o.OnDiagnostic = seen.Add;

            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(Svg, o);

            Assert.NotEmpty(pdf);
            Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed
                                       && d.Detail.Contains("rasterized", StringComparison.Ordinal));
        }

        /// <summary>The control: a rasterizer that succeeds reports no failure, so the report above is not a rule that always fires.</summary>
        [Fact]
        public void AnSvgTheRasterizerHandles_ReportsNoFailure()
        {
            byte[] png = PdfRawImageEncoder.EncodePng(new byte[1 * 1 * 3], 1, 1, 3);
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.SvgRasterizer = _ => png;
            o.OnDiagnostic = seen.Add;

            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(Svg, o);

            Assert.NotEmpty(pdf);
            Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
        }
    }
}
