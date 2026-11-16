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
/// A paragraph's tab stops, through its style chain (<see cref="StyleResolver.ResolveTabs"/>). Every paragraph of
/// a style collected the chain's stops again, and each stop was compared with every earlier one — quadratic in a
/// style's stops, once a paragraph (Wave 22, DocxToPdf-3:5). A paragraph with no tabs of its own now shares its
/// style's resolved stops, and positions are compared as the twips the file states.
/// </summary>
public sealed class StyleTabsTests
{
    [Fact]
    public void ParagraphsOfOneStyle_ShareItsStops_AndADirectTabResolvesItsOwn()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(new Style(
                new StyleName { Val = "Tabbed" },
                new StyleParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Left, Position = 1440 },
                    new TabStop { Val = TabStopValues.Right, Position = 2880, Leader = TabStopLeaderCharValues.Dot },
                    new TabStop { Val = TabStopValues.Center, Position = 1440 })))   // the same place: the first wins
            { Type = StyleValues.Paragraph, StyleId = "Tabbed" });

            Paragraph Styled(params TabStop[] direct)
            {
                var pPr = new ParagraphProperties(new ParagraphStyleId { Val = "Tabbed" });
                if (direct.Length > 0) pPr.Append(new Tabs(direct));
                return new Paragraph(pPr, new Run(new Text("x")));
            }

            main.Document = new Document(new Body(
                Styled(),
                Styled(),
                Styled(new TabStop { Val = TabStopValues.Clear, Position = 1440 },
                       new TabStop { Val = TabStopValues.Center, Position = 4320 })));
            main.Document.Save();
        }

        ms.Position = 0;
        using var read = WordprocessingDocument.Open(ms, false);
        var resolver = new StyleResolver(read);
        var paragraphs = read.MainDocumentPart!.Document.Body!.Elements<Paragraph>().ToArray();

        var (first, _) = resolver.ResolveTabs(paragraphs[0]);
        var (second, _) = resolver.ResolveTabs(paragraphs[1]);
        var (own, _) = resolver.ResolveTabs(paragraphs[2]);

        Assert.Same(first, second);
        Assert.Equal(new[] { (72.0, 0, '\0'), (144.0, 2, '.') },
            first.Select(t => (t.PosPt, t.Align, t.Leader)).ToArray());
        Assert.Equal(new[] { (144.0, 2, '.'), (216.0, 1, '\0') },
            own.Select(t => (t.PosPt, t.Align, t.Leader)).ToArray());
    }
}
