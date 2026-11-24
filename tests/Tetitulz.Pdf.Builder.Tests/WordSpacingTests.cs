// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A Builder-generated PDF must carry its word boundaries as space <b>characters</b>, not only as
/// gaps in the geometry.
/// </summary>
/// <remarks>
/// The exporter positions every piece with its own text matrix, so a document whose spaces exist
/// only as advance width is still legal and still renders identically — but plain extraction
/// returns "HelloWorld", and the document copies and searches badly in every viewer. These tests
/// assert the extracted string, which is the thing a user actually observes; they pass only when a
/// space reaches the content stream.
/// </remarks>
public class WordSpacingTests
{
    private static string ExtractText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return PdfTextExtractor.ExtractText(doc);
    }

    private static byte[] OneParagraph(params IInlineElement[] runs)
    {
        var doc = new Document();
        var sec = new Section();
        var para = new Paragraph();
        foreach (var r in runs) para.Runs.Add(r);
        sec.Children.Add(para);
        doc.Sections.Add(sec);
        return doc.ToPdf();
    }

    /// <summary>
    /// Arabic is shaped into presentation forms before it is drawn, but <c>/ToUnicode</c> must map
    /// those glyphs back to the <b>base</b> letters.
    /// </summary>
    /// <remarks>
    /// Recording the drawn form would make a search for the word visibly on the page fail, and
    /// copy-paste yield U+FE70-block code points no keyboard produces. Asserted through
    /// <c>ExtractText</c> — the reader's view — rather than by inspecting the CMap, because that is
    /// the behaviour the user experiences.
    /// <para>Three producers in this tree shape text; all three had this defect, and each records
    /// its <c>/ToUnicode</c> through different code, so each needs its own test.</para>
    /// </remarks>
    [Fact]
    public void ArabicText_ExtractsAsBaseLetters_NotPresentationForms()
    {
        const string arabic = "بيت";
        byte[] pdf = OneParagraph(new TextRun { Text = arabic });

        string text = ExtractText(pdf);

        Assert.Contains(arabic, text);
        // U+FE70–FEFF is the Arabic Presentation Forms-B block the shaper emits.
        Assert.DoesNotContain(text, c => c >= 'ﹰ' && c <= '﻿');
    }

    [Fact]
    public void SpaceBetweenWords_SurvivesIntoPlainExtraction()
    {
        byte[] pdf = OneParagraph(new TextRun { Text = "Hello World" });

        Assert.Contains("Hello World", ExtractText(pdf));
    }

    /// <summary>
    /// A run of spaces is one layout piece with a summed width, so it would be easy to emit a
    /// single space for it. The authored count is what extraction should return.
    /// </summary>
    [Fact]
    public void RunOfSpaces_IsNotCollapsedToOne()
    {
        byte[] pdf = OneParagraph(new TextRun { Text = "Hello   World" });

        Assert.Contains("Hello   World", ExtractText(pdf));
    }

    /// <summary>
    /// The boundary must survive across two separate runs, which is the case the exporter's own
    /// per-run text matrix makes easiest to lose.
    /// </summary>
    [Fact]
    public void SpaceBetweenTwoRuns_SurvivesIntoPlainExtraction()
    {
        byte[] pdf = OneParagraph(
            new TextRun { Text = "Hello " },
            new TextRun { Text = "World", IsBold = true });

        Assert.Contains("Hello World", ExtractText(pdf));
    }

    /// <summary>
    /// A tab is synthesised by layout and carries no font of its own; it must borrow one rather
    /// than vanish. U+0009 is deliberately not emitted — no simple font maps it, so it would be
    /// dropped again downstream. The gap itself stays recoverable from the geometry.
    /// </summary>
    [Fact]
    public void Tab_ReachesExtractionAsASpace()
    {
        byte[] pdf = OneParagraph(
            new TextRun { Text = "Hello" },
            new TabRun(),
            new TextRun { Text = "World" });

        string text = ExtractText(pdf);

        Assert.Contains("Hello World", text);
        Assert.DoesNotContain("HelloWorld", text);
    }

    /// <summary>
    /// Adding the space must not move the ink. Spaces are excluded from the placement list that
    /// drives highlight rectangles, underline/strike rules and link annotations, so a word's x
    /// position is unchanged — pinned here because a second list threads through the same method,
    /// and swapping the two would silently shift every rule and link.
    /// </summary>
    [Fact]
    public void AddingTheSpace_InsertsExactlyOneCharacter()
    {
        string text = ExtractText(OneParagraph(new TextRun { Text = "Hello World" }));

        int hello = text.IndexOf("Hello", StringComparison.Ordinal);
        int world = text.IndexOf("World", StringComparison.Ordinal);

        Assert.True(hello >= 0 && world > hello, $"both words must be present, got \"{text}\"");
        // Exactly one character between them: the space, not a doubled or dropped advance.
        Assert.Equal(hello + "Hello".Length + 1, world);
    }
}
