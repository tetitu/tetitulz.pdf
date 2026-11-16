// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Svg;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Svg.Tests;

/// <summary>
/// <see cref="SvgExportPlugin"/> reached the way a host reaches it: <b>registered</b> with
/// <see cref="PdfPluginHost"/> and invoked through <c>PdfOperations.Export</c>.
/// </summary>
/// <remarks>
/// <para>
/// Since GAPS.md §15e this is the only plugin that exposes SVG — the renderer-plugin route was
/// removed, because it returned SVG from <c>IRendererPlugin</c>, whose contract is PNG. That makes
/// the dispatch path here the one that has to work.
/// </para>
/// <para>
/// The output is parsed as XML rather than substring-matched: CLAUDE.md records that the suite
/// substring-matched SVG for its entire history, which is how an export that was not even
/// well-formed XML went unnoticed.
/// </para>
/// </remarks>
public class SvgExportPluginDispatchTests
{
    [Fact]
    public void Export_ThroughTheFacade_ProducesWellFormedSvgForEveryPage()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            PdfPluginHost.Instance.Register(new SvgExportPlugin());

            // THREE pages, deliberately. The fixture used to be a single page, where a stream of
            // concatenated SVG documents and one well-formed document are indistinguishable — so
            // the assertion this test's own name makes was never actually made, and the plugin
            // shipped for its whole life emitting output no XML parser can open.
            using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));
            using var output = new MemoryStream();

            PdfOperations.Export(input, output, "svg");

            string svg = Encoding.UTF8.GetString(output.ToArray()).Trim();
            Assert.NotEqual(0, svg.Length);

            // One document, one nested <svg> per page; parse it rather than trusting a substring.
            var doc = XDocument.Parse(svg);
            XNamespace ns = "http://www.w3.org/2000/svg";
            Assert.Equal("svg", doc.Root!.Name.LocalName);
            Assert.Equal(3, doc.Root.Elements(ns + "svg").Count());
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }
}
