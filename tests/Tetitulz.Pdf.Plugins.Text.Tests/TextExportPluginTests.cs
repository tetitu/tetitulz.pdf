// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Text.Tests;

/// <summary>
/// <see cref="TextExportPlugin"/> — the <c>txt</c> export plugin.
///
/// <para>Thin adapters like this get skipped because "it just calls the extractor". The two things
/// that are genuinely its own are the two tested hardest here: the <b>format key</b>, which is the
/// only way <c>PdfOperations.Export</c> can find it — a typo makes the plugin unreachable while
/// every unit test of the extractor still passes — and the <b>stream position restore</b>, which
/// exists because the plugin rewinds a stream it does not own.</para>
/// </summary>
public class TextExportPluginTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Export(PdfDocument doc)
    {
        using var output = new MemoryStream();
        new TextExportPlugin().Export(doc, output, null);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <remarks>
    /// Markers are single tokens on purpose. The structured extractor ends a word at a gap or a
    /// drawn space glyph (GAPS.md §4a); asserting on a spaced phrase would be testing that rule,
    /// not the export.
    /// </remarks>
    [Fact]
    public void Export_WritesThePagesText()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf("ExportableContent"));

        Assert.Contains("ExportableContent", Export(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_OfAMultiPageDocument_IncludesEveryPage()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMultiPagePdf(3));

        string text = Export(doc);

        // Each page draws "Page N" with a space glyph. This asserted "Page1" until extraction ended
        // a word at the drawn space.
        Assert.Contains("Page 1", text, StringComparison.Ordinal);
        Assert.Contains("Page 2", text, StringComparison.Ordinal);
        Assert.Contains("Page 3", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plugin seeks the caller's stream to 0 to run the extractor. It does not own that stream,
    /// so it must put the position back — a caller mid-read through the same document would
    /// otherwise silently resume from the wrong offset.
    /// </summary>
    [Fact]
    public void Export_RestoresTheDocumentStreamPosition()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        const int position = 42;
        doc.BaseStream.Position = position;

        using var output = new MemoryStream();
        new TextExportPlugin().Export(doc, output, null);

        Assert.Equal(position, doc.BaseStream.Position);
    }

    /// <summary>
    /// The format key is the plugin's address: <c>PdfOperations.Export(…, "txt")</c> resolves by
    /// this string, case-insensitively. Changing it silently unregisters the plugin from callers.
    /// </summary>
    [Fact]
    public void FormatKey_AndMimeType_AreTheDocumentedContract()
    {
        var plugin = new TextExportPlugin();

        Assert.Equal("txt", plugin.FormatKey);
        Assert.Equal("text/plain", plugin.TargetMimeType);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.NotNull(plugin.Version);
    }

    [Fact]
    public void Export_WithNullArguments_Throws()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        var plugin = new TextExportPlugin();

        Assert.Throws<ArgumentNullException>(() => plugin.Export(null!, output, null));
        Assert.Throws<ArgumentNullException>(() => plugin.Export(doc, null!, null));
    }

    /// <summary>Initialize/Shutdown are lifecycle no-ops, but the host calls them unconditionally.</summary>
    [Fact]
    public void Lifecycle_IsReentrant()
    {
        var plugin = new TextExportPlugin();

        plugin.Initialize();
        plugin.Initialize();
        plugin.Shutdown();
        plugin.Shutdown();

        // Still usable after a shutdown/initialize cycle — the host may reuse an instance.
        plugin.Initialize();
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf("StillWorks"));
        Assert.Contains("StillWorks", Export(doc), StringComparison.Ordinal);
    }
}
