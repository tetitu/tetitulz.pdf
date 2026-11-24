// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A body run holding a page or column break, a drawing or a note reference beside its text keeps the text on
/// both sides: the run is taken a part at a time, in order, rather than each of those taking its own path and
/// returning, losing the run's text before or after it.
/// </summary>
public sealed class RunBlockChildTests
{
    private static byte[] Convert(Action<MainDocumentPart, Body> build)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            build(main, body);
            main.Document = new Document(body);
            main.Document.Save();
        }
        return DocxToPdfConverter.ConvertToBytes(ms.ToArray(), new DocxToPdfOptions { CompressStreams = false });
    }

    private static string[] PageTexts(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var texts = new string[doc.Pages.Count];
        Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc, (i, t) => texts[i] = t);
        return texts;
    }

    [Fact]
    public void APageBreakInARun_KeepsTheTextOnEitherSide_OnItsOwnPage()
    {
        string[] pages = PageTexts(Convert((_, body) => body.Append(new Paragraph(new Run(
            new Text("Before"), new Break { Type = BreakValues.Page }, new Text("After"))))));

        Assert.Equal(2, pages.Length);
        Assert.Contains("Before", pages[0], StringComparison.Ordinal);
        Assert.Contains("After", pages[1], StringComparison.Ordinal);
        Assert.DoesNotContain("After", pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnBreakInARun_KeepsTheTextOnEitherSide()
    {
        string text = string.Concat(PageTexts(Convert((_, body) => body.Append(new Paragraph(new Run(
            new Text("Left"), new Break { Type = BreakValues.Column }, new Text("Right")))))));

        Assert.Contains("Left", text, StringComparison.Ordinal);
        Assert.Contains("Right", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoteReferenceInARun_KeepsTheRunsText_AndItsMark()
    {
        byte[] pdf = Convert((main, body) =>
        {
            var fnPart = main.AddNewPart<FootnotesPart>();
            fnPart.Footnotes = new Footnotes(new Footnote(new Paragraph(new Run(new Text("The note.")))) { Id = 1 });
            body.Append(new Paragraph(new Run(new Text("Noted"), new FootnoteReference { Id = 1 }, new Text("text"))));
        });
        string text = string.Concat(PageTexts(pdf));

        Assert.Contains("Noted", text, StringComparison.Ordinal);
        Assert.Contains("text", text, StringComparison.Ordinal);
        Assert.Contains("The note.", text, StringComparison.Ordinal);
    }

    /// <summary>The drawing keeps the run's caption, and is still painted from the original element.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // the control: a drawing alone in its run, the path that never lost anything
    public void ADrawingInARun_KeepsTheRunsText(bool withText)
    {
        byte[] png = DocxImageBuilder.MakeTruecolorPng(8, 8, 200, 50, 50);
        byte[] pdf = Convert((main, body) =>
        {
            var imagePart = main.AddImagePart("image/png");
            using (var s = new MemoryStream(png)) imagePart.FeedData(s);
            string relId = main.GetIdOfPart(imagePart);
            var run = new Run(DocxImageBuilder.BuildInlineDrawing(relId, 50 * 12700, 50 * 12700));
            if (withText) run.Append(new Text("Caption"));
            body.Append(new Paragraph(run));
        });

        Assert.Matches(@"/Im\d+ Do", System.Text.Encoding.Latin1.GetString(pdf));
        Assert.Equal(withText, string.Concat(PageTexts(pdf)).Contains("Caption", StringComparison.Ordinal));
    }
}
