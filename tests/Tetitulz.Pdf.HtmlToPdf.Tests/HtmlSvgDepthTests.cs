// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// An inline <c>&lt;svg&gt;</c> is measured before AngleSharp serialises it for the rasterizer: the
    /// serialiser recurses, the mapper's own depth bound never enters an <c>&lt;svg&gt;</c>, and the
    /// markup scan admits 25 000 levels — a stack overflow, which ends the process. Past the bound the
    /// image is dropped and said; a shallow one is still rasterized (the control).
    /// </summary>
    public class HtmlSvgDepthTests
    {
        [Fact]
        public void AdversarialDeeplyNestedInlineSvg_IsReported_NotSerialised()
        {
            int calls = 0;
            var diagnostics = new List<BuilderDiagnostic>();
            var o = Managed();
            o.SvgRasterizer = _ => { calls++; return MakeRgbPng(4, 4); };
            o.OnDiagnostic = diagnostics.Add;

            byte[] pdf = ToPdf("<p>x</p><svg>" + string.Concat(Enumerable.Repeat("<g>", 24_000)) + "</svg>", o);

            Assert.Equal(0, calls);
            Assert.Contains(diagnostics, d => d.Detail.Contains("nests", StringComparison.Ordinal));
            Assert.DoesNotContain("/Subtype /Image", Raw(pdf));

            byte[] shallow = ToPdf("<p>x</p><svg viewBox=\"0 0 10 10\"><g><g></g></g></svg>", o);
            Assert.Equal(1, calls);
            Assert.Contains("/Subtype /Image", Raw(shallow));
        }
    }
}
