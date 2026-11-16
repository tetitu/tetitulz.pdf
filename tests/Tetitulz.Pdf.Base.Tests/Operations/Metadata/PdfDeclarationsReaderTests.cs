// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Metadata;

/// <summary>
/// <see cref="PdfDeclarationsReader"/> — the read side of <see cref="PdfDeclarationsWriter"/>. The
/// round trip through the writer is the primary check; the hand-written packets pin the two XMP
/// spellings of a property and the difference between "no claim" (empty) and "unreadable" (null).
/// </summary>
public class PdfDeclarationsReaderTests
{
    private const string First = "http://pdfa.org/declarations/wtpdf/#reuse1.0";
    private const string Second = "https://example.org/standards/privacy-v1";

    [Fact]
    public void ReadConformsTo_ReadsWhatTheWriterWrote()
    {
        byte[] pdf = Declare(MinimalPdfBuilder.CreateMinimalPdf(), First);

        var uris = PdfDeclarationsReader.ReadConformsTo(new MemoryStream(pdf));

        Assert.NotNull(uris);
        Assert.Equal(new[] { First }, uris);
    }

    [Fact]
    public void ReadConformsTo_TwoDeclarations_ReturnsBothInOrder()
    {
        byte[] pdf = Declare(Declare(MinimalPdfBuilder.CreateMinimalPdf(), First), Second);

        var uris = PdfDeclarationsReader.ReadConformsTo(new MemoryStream(pdf));

        Assert.Equal(new[] { First, Second }, uris);
    }

    [Fact]
    public void ReadConformsTo_AttributeShorthandOnADescription_IsRead()
    {
        // XMP lets a simple property be written as an attribute of rdf:Description; whitespace
        // around a URI (as in WTPDF's own example) is trimmed.
        string xmp =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:pdfd=\"http://pdfa.org/declarations/\"><pdfd:declarations><rdf:Bag>" +
            "<rdf:li><rdf:Description pdfd:conformsTo=\"  " + First + "  \"/></rdf:li>" +
            "</rdf:Bag></pdfd:declarations></rdf:Description></rdf:RDF></x:xmpmeta>";

        var uris = PdfDeclarationsReader.ReadConformsTo(
            new MemoryStream(PdfDeclarationsWriterTests.BuildPdfWithMetadata(xmp)));

        Assert.Equal(new[] { First }, uris);
    }

    [Fact]
    public void ReadConformsTo_NoMetadata_IsEmptyNotNull()
    {
        var uris = PdfDeclarationsReader.ReadConformsTo(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()));

        Assert.NotNull(uris);
        Assert.Empty(uris);
    }

    [Fact]
    public void ReadConformsTo_PacketWithNoDeclarations_IsEmptyNotNull()
    {
        string xmp =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:format>application/pdf</dc:format>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>";

        var uris = PdfDeclarationsReader.ReadConformsTo(
            new MemoryStream(PdfDeclarationsWriterTests.BuildPdfWithMetadata(xmp)));

        Assert.NotNull(uris);
        Assert.Empty(uris);
    }

    [Fact]
    public void ReadConformsTo_EncryptedDocument_IsNullWithAReason()
    {
        var uris = PdfDeclarationsReader.ReadConformsTo(
            new MemoryStream(PdfDeclarationsWriterTests.EncryptedLookingPdf()));

        Assert.Null(uris);
        Assert.Contains("encrypted", PdfDeclarationsReader.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadConformsTo_PacketThatIsNotXml_IsNullWithAReason()
    {
        var uris = PdfDeclarationsReader.ReadConformsTo(
            new MemoryStream(PdfDeclarationsWriterTests.BuildPdfWithMetadata("<x:xmpmeta><not closed")));

        Assert.Null(uris);
        Assert.Contains("not well-formed", PdfDeclarationsReader.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadConformsTo_DeeplyNestedAdversarialPacket_TerminatesWithoutReadingPastTheDepthCap()
    {
        // A conformsTo buried deeper than MaxSearchDepth is not reached — the walk is bounded, which
        // is what keeps a hostile packet from recursing without limit.
        var sb = new StringBuilder();
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfd=\"http://pdfa.org/declarations/\"><pdfd:declarations>");
        for (int i = 0; i < PdfDeclarationsWriter.MaxSearchDepth + 10; i++) sb.Append("<pdfd:n>");
        sb.Append("<pdfd:conformsTo>").Append(First).Append("</pdfd:conformsTo>");
        for (int i = 0; i < PdfDeclarationsWriter.MaxSearchDepth + 10; i++) sb.Append("</pdfd:n>");
        sb.Append("</pdfd:declarations></rdf:Description></rdf:RDF></x:xmpmeta>");

        var uris = PdfDeclarationsReader.ReadConformsTo(
            new MemoryStream(PdfDeclarationsWriterTests.BuildPdfWithMetadata(sb.ToString())));

        Assert.NotNull(uris);
        Assert.Empty(uris);
    }

    private static byte[] Declare(byte[] pdf, string conformsTo)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        Assert.True(PdfDeclarationsWriter.AddDeclaration(input, output, conformsTo), PdfDeclarationsWriter.LastError);
        return output.ToArray();
    }
}
