// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Export;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A block pseudo-element that paints nothing — Bootstrap's clearfix, <c>display: table;
    /// content: " "</c> on every row and panel — is not reported as skipped: there is nothing to
    /// skip, and the report on every Bootstrap page buried the ones that mattered.
    /// </summary>
    public class BlankPseudoElementTests
    {
        private static List<string> Reports(string css)
        {
            var seen = new List<string>();
            var o = Managed();
            o.OnDiagnostic = d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) seen.Add(d.Detail); };
            ToPdf("<html><head><style>" + css + "</style></head><body><div class='x'>A</div></body></html>", o);
            return seen;
        }

        private static bool Skipped(List<string> reports) => reports.Exists(r => r.Contains("pseudo-element", StringComparison.Ordinal));

        /// <summary>The clearfix is quiet, with a blank string of either quote.</summary>
        [Fact]
        public void AClearfixIsNotReported()
        {
            Assert.False(Skipped(Reports(".x:before{display:table;content:\" \"}")));
            Assert.False(Skipped(Reports(".x:after{display:table;content:''}")));
        }

        /// <summary>Content, or anything that paints, is still reported: text, a background, a border, a size.</summary>
        [Theory]
        [InlineData(".x:before{display:table;content:\"X\"}")]
        [InlineData(".x:before{display:table;content:\" \";background:red}")]
        [InlineData(".x:before{display:block;content:\" \";border-top:1px solid red}")]
        [InlineData(".x:before{display:block;content:\"\";height:4px}")]
        public void APseudoElementThatPaintsIsReported(string css)
            => Assert.True(Skipped(Reports(css)));
    }
}
