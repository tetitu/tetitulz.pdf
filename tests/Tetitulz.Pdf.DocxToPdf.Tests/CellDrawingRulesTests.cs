// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A table cell paints the drawings its text walk reads, by the same rules: a picture inside a tracked deletion
/// or a move's source is not shown, and one inside an insertion is. The cell found every drawing below the
/// paragraph, deleted ones included (Wave 22, DocxToPdf-4:4).
/// </summary>
public sealed class CellDrawingRulesTests
{
    private static int ImagesPainted(System.Func<Run, OpenXmlElement> wrap)
    {
        byte[] png = DocxImageBuilder.MakeTruecolorPng(8, 8, 200, 50, 50);
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var imagePart = main.AddImagePart("image/png");
            using (var s = new MemoryStream(png)) imagePart.FeedData(s);
            string relId = main.GetIdOfPart(imagePart);
            var run = new Run(DocxImageBuilder.BuildInlineDrawing(relId, 40 * 12700, 40 * 12700));
            var cell = new TableCell(new Paragraph(new Run(new Text("cell")), wrap(run)));
            main.Document = new Document(new Body(new Table(new TableRow(cell)), new Paragraph()));
            main.Document.Save();
        }
        string pdf = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
            new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = true }));
        return Regex.Matches(pdf, @"/Im\d+ Do").Count;
    }

    [Fact]
    public void APictureInATrackedDeletion_IsNotPainted() => Assert.Equal(0, ImagesPainted(r => new DeletedRun(r) { Id = "1", Author = "a" }));

    [Fact]
    public void APictureAMoveLeft_IsNotPainted() => Assert.Equal(0, ImagesPainted(r => new MoveFromRun(r) { Id = "2", Author = "a" }));

    /// <summary>The controls: a picture in a run of its own, and one inside an insertion, are painted.</summary>
    [Fact]
    public void APictureInARunOrAnInsertion_IsPainted()
    {
        Assert.Equal(1, ImagesPainted(r => r));
        Assert.Equal(1, ImagesPainted(r => new InsertedRun(r) { Id = "3", Author = "a" }));
    }
}
