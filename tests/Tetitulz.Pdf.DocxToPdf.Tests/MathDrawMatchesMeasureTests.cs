// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// An equation's glyphs are drawn in the face, colour and encoding they were measured in — not a glyph box
/// measured in the standard-14 face its family maps to drawn by registering the family's raw name, nor a
/// stretched fence written apart from its box, as WinAnsi text with no colour.
/// </summary>
public sealed class MathDrawMatchesMeasureTests
{
    private const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" "
                              + "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"";

    /// <summary>One paragraph of <paramref name="omath"/>, in a document whose default font is Times New Roman.</summary>
    private static string Convert(string omath)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }))));
            main.Document = new Document(new Body(new Paragraph($"<w:p {Ns}><m:oMath>{omath}</m:oMath></w:p>")));
            main.Document.Save();
        }
        return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
            new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = true }));
    }

    private static string R(string text) => $"<m:r><m:t>{text}</m:t></m:r>";

    /// <summary>
    /// A variable is italic in OMML by default, and is measured in the italic face; it was drawn upright, in the
    /// face the family's name alone registered.
    /// </summary>
    [Fact]
    public void AnEquationsGlyphs_AreDrawnInTheFaceTheyWereMeasuredIn()
    {
        string pdf = Convert(R("x"));

        Assert.Contains("/BaseFont /Times-Italic", pdf);
        Assert.DoesNotContain("/BaseFont /Helvetica", pdf);
    }

    /// <summary>
    /// A parenthesis stretched around a fraction is drawn through a text matrix inside a text object that sets
    /// its own fill colour, as every other glyph box does.
    /// </summary>
    [Fact]
    public void AStretchedFence_SetsItsColour_AndIsDrawnInTheMeasuredFace()
    {
        string pdf = Convert($"<m:d><m:e><m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f></m:e></m:d>");

        // The parenthesis is written escaped, "(\()", in a PDF literal string.
        Assert.Matches(new Regex(@"BT\n[^\n]* rg\n/\w+ [\d.]+ Tf\n1 0 0 [\d.]+ -?[\d.]+ -?[\d.]+ Tm\n\(\\\(\) Tj"), pdf);
        Assert.DoesNotContain("/BaseFont /Helvetica", pdf);
    }
}
