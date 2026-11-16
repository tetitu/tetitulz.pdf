// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Docx;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// The DOCX export and import plugins.
///
/// <para>The export test opens the result <b>as a zip</b> and looks for the part every DOCX must
/// have. A DOCX is an OPC package, so "bytes came back" and even "it starts with PK" are both
/// satisfied by an empty archive — the failure mode a converter actually has.</para>
/// </summary>
public class DocxPluginTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static byte[] Export(byte[] pdf)
    {
        using var doc = Open(pdf);
        using var output = new MemoryStream();
        new DocxExportPlugin().Export(doc, output, null);
        return output.ToArray();
    }

    /// <summary>
    /// <c>word/document.xml</c> is the main document part — an OPC package without it is not a
    /// DOCX, whatever else it contains.
    /// </summary>
    /// <remarks>
    /// Asserts on the parsed part, not merely that the entry exists: a zip carrying an empty or
    /// non-XML <c>document.xml</c> would satisfy a presence check and open in nothing. The root
    /// element name is the specific claim — WordprocessingML's document root is <c>w:document</c>.
    /// </remarks>
    [Fact]
    public void Export_ProducesAnOpcPackageWithTheMainDocumentPart()
    {
        byte[] docx = Export(MinimalPdfBuilder.CreateMinimalPdf("Content"));

        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);

        ZipArchiveEntry? main = archive.GetEntry("word/document.xml");
        Assert.True(main is not null, "the package has no word/document.xml");
        Assert.Equal("document", XDocument.Load(main!.Open()).Root!.Name.LocalName);

        ZipArchiveEntry? contentTypes = archive.GetEntry("[Content_Types].xml");
        Assert.True(contentTypes is not null, "the package has no [Content_Types].xml");
        using var reader = new StreamReader(contentTypes!.Open());
        Assert.Contains("wordprocessingml.document.main+xml", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_CarriesThePagesTextIntoTheDocumentPart()
    {
        byte[] docx = Export(MinimalPdfBuilder.CreateMinimalPdf("UniqueMarkerText"));

        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        string xml = reader.ReadToEnd();

        Assert.Contains("UniqueMarkerText", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plugin rewinds the caller's stream to read the document; it does not own it, so the
    /// position must be where the caller left it.
    /// </summary>
    [Fact]
    public void Export_RestoresTheDocumentStreamPosition()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        const int position = 23;
        doc.BaseStream.Position = position;

        using var output = new MemoryStream();
        new DocxExportPlugin().Export(doc, output, null);

        Assert.Equal(position, doc.BaseStream.Position);
    }

    [Fact]
    public void ExportAndImport_ShareTheFormatKey()
    {
        // Both directions of one format must answer to the same key, or a caller can export "docx"
        // and find nothing able to import it.
        Assert.Equal("docx", new DocxExportPlugin().FormatKey);
        Assert.Equal("docx", new DocxImportPlugin().FormatKey);
    }

    [Fact]
    public void ExportPlugin_DeclaresTheOoxmlMimeType()
    {
        Assert.Contains("wordprocessingml", new DocxExportPlugin().TargetMimeType, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_WithNullArguments_Throws()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        var plugin = new DocxExportPlugin();

        Assert.Throws<ArgumentNullException>(() => plugin.Export(null!, output, null));
        Assert.Throws<ArgumentNullException>(() => plugin.Export(doc, null!, null));
    }

    /// <summary>
    /// Import of something that is not a zip must fail loudly rather than produce an empty PDF that
    /// looks like a successful conversion.
    /// </summary>
    [Fact]
    public void Import_OfMalformedInput_Throws()
    {
        using var garbage = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        using var output = new MemoryStream();

        Assert.ThrowsAny<Exception>(() => new DocxImportPlugin().Import(garbage, output, null));
    }
}
