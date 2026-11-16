// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The default render engine is <see cref="HtmlRenderEngine.Managed"/>, and a conversion that sets
/// no engine really goes through it.
/// </summary>
/// <remarks>
/// <para><b>Pinned because nothing else could catch it being flipped back.</b> Every other test in
/// this assembly sets <c>RenderEngine</c> explicitly, so the default is exercised by none of them —
/// and the browser path has no coverage at all here, since it needs <c>playwright install</c>. A
/// silent revert to <c>Browser</c> would leave this suite entirely green while every caller that
/// did not set the property started requiring a browser.</para>
/// <para>The second test is the one that matters: asserting the enum value alone would pass if the
/// converter ignored the property. It converts with <b>no options at all</b> and requires real
/// output, which only the managed engine can produce on a machine with no browsers installed —
/// which is the machine this runs on.</para>
/// </remarks>
public class DefaultEngineTests
{
    [Fact]
    public void TheDefaultRenderEngine_IsManaged()
    {
        Assert.Equal(HtmlRenderEngine.Managed, new HtmlToPdfOptions().RenderEngine);
        Assert.Equal(HtmlRenderEngine.Managed, HtmlToPdfOptions.Default.RenderEngine);
    }

    [Fact]
    public void ConvertingWithNoOptions_ProducesTextWithoutABrowser()
    {
        using var output = new MemoryStream();

        // No options: this is the path a caller gets by doing nothing, and it must work on a
        // machine with no Playwright browsers -- which is what CI and a fresh clone look like.
        HtmlToPdfConverter.ConvertHtml("<html><body><p>Defaulted</p></body></html>", output);

        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        string text = string.Concat(PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .Select(w => w.Text));

        Assert.Contains("Defaulted", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The managed engine's headline advantage over the browser path, and the first reason the
    /// default changed: it emits a structure tree. Asserted here so "tagged by default" cannot
    /// quietly stop being true.
    /// </summary>
    [Fact]
    public void ConvertingWithNoOptions_ProducesTaggedOutput()
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml("<html><body><p>Tagged</p></body></html>", output);

        using var input = new MemoryStream(output.ToArray(), writable: false);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(input);

        Assert.True(
            doc.Catalog.Dictionary.ContainsKey(Tetitulz.Pdf.Core.Objects.PdfName.StructTreeRoot),
            "the default conversion produced no structure tree, so it is not tagged");
    }
}
