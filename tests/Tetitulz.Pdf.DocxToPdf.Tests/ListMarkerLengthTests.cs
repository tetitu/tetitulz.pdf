// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A list level's marker text (<c>w:lvlText</c>) is laid out for every numbered paragraph, so its
/// length multiplies by the paragraph count, two counts the file chooses: a 20 000-character marker
/// over two hundred paragraphs ran to some 1500 pages. Past <see cref="ListRenderer.MaxListMarkerChars"/>
/// it is cut; an ordinary marker is drawn as written (the control).
/// </summary>
public class ListMarkerLengthTests
{
    private static int PagesFor(string lvlText, int paragraphs)
    {
        string numbering = $"<w:numbering xmlns:w=\"{HostileDocxInputTests.WordNamespace}\">"
            + "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/>"
            + $"<w:lvlText w:val=\"{lvlText}\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";
        string para = "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>Item</w:t></w:r></w:p>";
        string document = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\">"
            + "<w:body>" + string.Concat(Enumerable.Repeat(para, paragraphs)) + "</w:body></w:document>";

        byte[] pdf = DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(document, numbering),
            new DocxToPdfOptions { UseLegacyRenderer = true });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.Pages.Count;
    }

    [Fact]
    public void AdversarialLongMarkerText_IsCut_SoThePagesDoNotMultiply()
    {
        int pages = PagesFor(new string('A', 20_000) + "%1.", 200);
        Assert.True(pages < 100, $"{pages} pages");
    }

    [Fact]
    public void AnOrdinaryMarker_IsLaidOutAsWritten()
        => Assert.Equal(1, PagesFor("%1.", 5));
}
