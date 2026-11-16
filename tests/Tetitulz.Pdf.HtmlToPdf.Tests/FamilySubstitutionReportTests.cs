// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// An unavailable named family is reported once per conversion — not per word, and not again
    /// by the shrink-to-fit measure of a float, which lays the same text out in a scratch context
    /// the export then draws (and reports) for real.
    /// </summary>
    public class FamilySubstitutionReportTests
    {
        private static List<BuilderDiagnostic> Convert(string body)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                AllowSystemFontSubstitution = false,
                OnDiagnostic = seen.Add,
            };
            HtmlToPdfConverter.ConvertHtmlToBytes("<html><body style='font-family:NoSuchFamilyA'>" + body + "</body></html>", o);
            return seen.Where(d => d.Kind == BuilderDiagnosticKind.FontNotResolved).ToList();
        }

        [Fact]
        public void APageInAMissingFamilyReportsItOnce()
            => Assert.Single(Convert("<p>one two three</p><p>four five six</p>"));

        /// <summary>An auto-width float is measured before it is drawn: still one report.</summary>
        [Fact]
        public void AMeasuredFloatDoesNotReportItAgain()
            => Assert.Single(Convert("<div style='float:left'>floated words</div><p>beside it</p>"));
    }
}
