// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.GsubJoiningFont;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A caller's font that maps the Arabic letters but reaches their joining forms only through its
/// <c>GSUB</c> (see <see cref="F"/>): the Builder draws it joined — the final yeh then the initial
/// beh, <c>&lt;00040003&gt; Tj</c> — where its coverage check refused the font for the forms it
/// does not map, and the run was drawn unrendered.
/// </summary>
public class GsubJoiningFormsTests
{
    [Fact]
    public void AFontReachingItsFormsThroughGsub_DrawsTheJoinedForms()
    {
        string content = Content(F.Build());
        Assert.Contains("<00040003> Tj", content);
    }

    /// <summary>The same font without its <c>GSUB</c> cannot draw the joined forms, and does not.</summary>
    [Fact]
    public void WithoutItsGsub_TheJoinedFormsAreNotDrawn()
    {
        string content = Content(F.Build(gsub: false));
        Assert.DoesNotContain("<00040003> Tj", content);
        Assert.DoesNotContain("<00020001> Tj", content);
    }

    /// <summary>
    /// A second run in the same font reuses the font already embedded: the exporter's check of the
    /// fonts it holds asks the same coverage question. Asked the narrower one, it found the font
    /// lacking for the second run and embedded it again.
    /// </summary>
    [Fact]
    public void ASecondRun_ReusesTheEmbeddedFont()
    {
        var doc = new Document();
        var section = new Section();
        foreach (string text in new[] { "بي", "بب" })
        {
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = text, FontName = "GsubJoiningFixture", FontSize = 20 });
            section.Children.Add(p);
        }
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf(new PdfExportOptions { AllowSystemFontSubstitution = false, FontProvider = _ => F.Build() });

        string content = PdfContentProbe.DecodeStreams(pdf);
        Assert.Contains("<00040003> Tj", content);
        Assert.Contains("<00050003> Tj", content);
        int fontFiles = System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), "/FontFile2").Count;
        Assert.Equal(1, fontFiles);
    }

    private static string Content(byte[] font)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "بي", FontName = "GsubJoiningFixture", FontSize = 20 });
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => font,
        }));
    }
}
