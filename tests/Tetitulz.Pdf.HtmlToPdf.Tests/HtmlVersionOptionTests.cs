// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <see cref="HtmlToPdfOptions.Version"/> reaches the managed engine's header: written as asked,
    /// raised to what the content needs and reported through <see cref="HtmlToPdfOptions.OnDiagnostic"/>,
    /// and refused for a version no edition publishes.
    /// </summary>
    public class HtmlVersionOptionTests
    {
        private const string Html = "<html><body><h1>Title</h1><p>Hello.</p></body></html>";

        private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 8);

        private static (byte[] pdf, List<BuilderDiagnostic> seen) Convert(PdfVersion? version, bool tagged)
        {
            var seen = new List<BuilderDiagnostic>();
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(Html, new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                Version = version,
                Tagged = tagged,
                OnDiagnostic = seen.Add,
            });
            return (pdf, seen);
        }

        [Fact]
        public void AskedVersion_IsTheHeader()
        {
            Assert.Equal("%PDF-1.7", Header(Convert(PdfVersion.Pdf17, tagged: true).pdf));
            Assert.Equal("%PDF-2.0", Header(Convert(PdfVersion.Pdf20, tagged: true).pdf));
        }

        [Fact]
        public void TaggedOutput_RaisesAVersionBelow14_AndSaysSo()
        {
            // A tagged document's /MarkInfo is PDF 1.4 (ISO 32000-2 §14.7.1).
            var (pdf, seen) = Convert(PdfVersion.Pdf13, tagged: true);

            Assert.Equal("%PDF-1.4", Header(pdf));
            BuilderDiagnostic raised = Assert.Single(seen, d => d.Kind == BuilderDiagnosticKind.VersionRaised);
            Assert.Contains("PDF 1.3 was asked for", raised.Detail);
        }

        [Fact]
        public void UntaggedOutput_Keeps13_AndReportsNothing()
        {
            var (pdf, seen) = Convert(PdfVersion.Pdf13, tagged: false);

            Assert.Equal("%PDF-1.3", Header(pdf));
            Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.VersionRaised);
        }

        [Fact]
        public void Unset_IsTheLowestTheContentNeeds_NeverBelow14()
        {
            var (pdf, seen) = Convert(null, tagged: false);

            Assert.Equal("%PDF-1.4", Header(pdf));
            Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.VersionRaised);
        }

        [Fact]
        public void AnUnpublishedVersion_IsRefused()
        {
            var options = new HtmlToPdfOptions();
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Version = new PdfVersion(1, 8));
            Assert.Equal("value", ex.ParamName);

            options.Version = PdfVersion.Pdf17;   // control
            Assert.Equal(PdfVersion.Pdf17, options.Version);
        }
    }
}
