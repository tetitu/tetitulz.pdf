// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// An inline <see cref="PageBreak"/> — what <c>DocumentBuilder.AddPageBreak()</c> adds — ends the
/// PDF page.
/// </summary>
/// <remarks>
/// The PDF exporter treated it as a line break: layout closed the line and nothing ended the page,
/// so the same model came out as one page in PDF and two in DOCX, HTML and ODT, whose exporters
/// all honour it. Found while placing a form field after a break, which landed on page one.
/// </remarks>
public class InlinePageBreakTests
{
    private static byte[] Pdf(bool withBreak)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "before" });
        if (withBreak) p.Runs.Add(new PageBreak());
        else p.Runs.Add(new LineBreak());
        p.Runs.Add(new TextRun { Text = "after" });

        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc.ToPdf();
    }

    [Fact]
    public void AnInlinePageBreakStartsANewPage()
    {
        byte[] pdf = Pdf(withBreak: true);

        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
        // Both halves are still drawn — the break moved "after", it did not drop it.
        string content = PdfContentProbe.DecodeStreams(pdf);
        Assert.Contains("before", content, System.StringComparison.Ordinal);
        Assert.Contains("after", content, System.StringComparison.Ordinal);
    }

    /// <summary>The control: a line break in the same place stays on one page.</summary>
    [Fact]
    public void ALineBreakInTheSamePlaceStaysOnOnePage()
    {
        Assert.Equal(1, PdfContentProbe.CountPages(Pdf(withBreak: false)));
    }
}
