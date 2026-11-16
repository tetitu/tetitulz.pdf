// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A run drawn in a standard-14 face, kerned when asked (2026-10-20): Helvetica's <c>KPX A V -70</c> is
/// written as <c>[(A) 70 (V)] TJ</c> and counted in the run's width; unasked, the same <c>(AV) Tj</c>.
/// </summary>
public class Standard14KerningExportTests
{
    [Fact]
    public void AKernedStandardRun_IsDrawnAsTJ_TheUnkernedAsTj()
    {
        Assert.Contains("[(A) 70 (V)] TJ", Content("AV", kerning: true), System.StringComparison.Ordinal);
        string plain = Content("AV", kerning: false);
        Assert.Contains("(AV) Tj", plain, System.StringComparison.Ordinal);
        Assert.DoesNotContain("TJ", plain, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Laid out at its kerned width: right-aligned, the kerned word starts 70/1000 of its 20pt size —
    /// 1.4pt — further right, so measuring and drawing agree.
    /// </summary>
    [Fact]
    public void AKernedStandardRun_IsLaidOutAtItsKernedWidth()
    {
        double kerned = TmX(Content("AV", kerning: true, TextAlignment.Right));
        double plain = TmX(Content("AV", kerning: false, TextAlignment.Right));
        Assert.Equal(1.4, kerned - plain, 2);
    }

    /// <summary>
    /// A page-number field is not kerned: NUMPAGES is found in the drawn content and replaced after
    /// layout, and "PA" is a Helvetica pair (<c>KPX P A -120</c>), so a kerned placeholder was never
    /// replaced. Both fields come out as the number.
    /// </summary>
    [Theory]
    [InlineData(PdfExporter.NumPagesPlaceholder)]
    [InlineData(PdfExporter.PagePlaceholder)]
    public void AFieldPlaceholder_IsNotKerned_AndIsReplaced(string field)
    {
        string content = Content(field, kerning: true);
        Assert.Contains("(1) Tj", content, System.StringComparison.Ordinal);
        Assert.DoesNotContain("{{", content, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A piece a bidi pass reorders keeps its width and is drawn unkerned: under a right-to-left
    /// override "AV" is displayed "VA", and the pairs of its logical text no longer stand beside each
    /// other. The control: the same word without the override, kerned.
    /// </summary>
    [Fact]
    public void AReorderedPiece_IsDrawnUnkerned()
    {
        string content = Content("‮AV‬", kerning: true);
        Assert.Contains("(VA) Tj", content, System.StringComparison.Ordinal);
        Assert.DoesNotContain("TJ", content, System.StringComparison.Ordinal);
        Assert.Contains("[(A) 70 (V)] TJ", Content("AV", kerning: true), System.StringComparison.Ordinal);
    }

    private static double TmX(string content)
    {
        var m = Regex.Match(content, @"1 0 0 1 (\S+) \S+ Tm");
        Assert.True(m.Success, "no text matrix was written");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string Content(string text, bool kerning, TextAlignment alignment = TextAlignment.Left,
        TextDirection? direction = null)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Alignment = alignment, Direction = direction };
        p.Runs.Add(new TextRun { Text = text, FontSize = 20 });
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions { AllowSystemFontSubstitution = false, Kerning = kerning }));
    }
}
