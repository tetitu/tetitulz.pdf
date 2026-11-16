// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A paragraph's spacing and indentation resolved per attribute (<see cref="StyleResolver.ResolveParagraphSpacing"/>,
/// <see cref="StyleResolver.ResolveIndentation"/>). The element was taken whole from the first level that had one,
/// so a direct w:spacing stating only w:before set the space after to zero, and a direct w:ind stating only
/// w:firstLine set the left indent to zero (Wave 22, DocxToPdf-3:7).
/// </summary>
public sealed class ParagraphSpacingResolutionTests
{
    [Fact]
    public void ADirectAttribute_ReplacesItsOwn_AndTheStyleKeepsTheRest()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(new Style(
                new StyleName { Val = "Spaced" },
                new StyleParagraphProperties(
                    new SpacingBetweenLines { Before = "240", After = "480" },
                    new Indentation { Left = "720", Hanging = "360" }))
            { Type = StyleValues.Paragraph, StyleId = "Spaced" });

            main.Document = new Document(new Body(
                new Paragraph(new ParagraphProperties(
                    new ParagraphStyleId { Val = "Spaced" },
                    new SpacingBetweenLines { Before = "120" },
                    new Indentation { FirstLine = "200" })),
                new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Spaced" }))));
            main.Document.Save();
        }

        ms.Position = 0;
        using var read = WordprocessingDocument.Open(ms, false);
        var resolver = new StyleResolver(read);
        var paragraphs = read.MainDocumentPart!.Document.Body!.Elements<Paragraph>().ToArray();

        // The direct w:before and w:firstLine replace the style's; its w:after and w:left stay.
        Assert.Equal((6.0, 24.0), resolver.ResolveParagraphSpacing(paragraphs[0]));
        Assert.Equal((36.0, 0.0, 10.0), resolver.ResolveIndentation(paragraphs[0]));

        // The control: nothing direct, the style whole — its hanging indent outdenting the first line.
        Assert.Equal((12.0, 24.0), resolver.ResolveParagraphSpacing(paragraphs[1]));
        Assert.Equal((36.0, 0.0, -18.0), resolver.ResolveIndentation(paragraphs[1]));
    }
}
