// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="TextRun.IsInvisible"/>: the run is shown in text render mode 3 (ISO 32000-1 §9.3.6),
/// so its glyphs take their room and paint nothing — CSS <c>color: transparent</c>. Its rules, which
/// take the text's colour, are not drawn; the mode is put back for the text after it.
/// </summary>
/// <remarks>
/// Such a run was drawn in its colour: the HTML mapper kept the inherited colour for a transparent
/// one, having no way to say "invisible", and before that composited it to white.
/// </remarks>
public class InvisibleTextTests
{
    private static string Content(bool invisible, bool underline = false, string next = null)
    {
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "A " });
        p.Runs.Add(new TextRun { Text = "gone", IsInvisible = invisible, IsUnderline = underline });
        p.Runs.Add(new TextRun { Text = " C" });
        section.Children.Add(p);
        if (next != null)
        {
            var q = new Paragraph();
            q.Runs.Add(new TextRun { Text = next });
            section.Children.Add(q);
        }
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions()));
    }

    private static int IndexOf(string content, string token)
    {
        int at = content.IndexOf(token, StringComparison.Ordinal);
        Assert.True(at >= 0, token + " is not in the content");
        return at;
    }

    /// <summary>
    /// The invisible run is shown between <c>3 Tr</c> and <c>0 Tr</c>, and the text after it starts
    /// where it starts when the run is visible: the glyphs are laid out, not dropped.
    /// </summary>
    [Fact]
    public void AnInvisibleRunIsShownInRenderMode3AndTakesItsRoom()
    {
        string content = Content(invisible: true);
        string control = Content(invisible: false);

        int on = IndexOf(content, "3 Tr"), gone = IndexOf(content, "(gone) Tj"), off = IndexOf(content, "0 Tr"), c = IndexOf(content, "(C) Tj");
        Assert.True(on < gone && gone < off && off < c, $"order: 3 Tr at {on}, gone at {gone}, 0 Tr at {off}, C at {c}");
        Assert.Equal(PdfContentProbe.XOf(control, "C"), PdfContentProbe.XOf(content, "C"), 3);
        Assert.DoesNotContain(" Tr", control, StringComparison.Ordinal);
    }

    /// <summary>The rules take the text's colour, so an invisible run's underline is not drawn; a visible one's is.</summary>
    [Fact]
    public void AnInvisibleRunsUnderlineIsNotDrawn()
    {
        int Rules(string content) => Regex.Matches(content, @"\bre\s+f\b").Count;

        int plain = Rules(Content(invisible: false));
        Assert.Equal(plain + 1, Rules(Content(invisible: false, underline: true)));
        Assert.Equal(plain, Rules(Content(invisible: true, underline: true)));
    }

    /// <summary>
    /// A line ending in an invisible run puts the mode back before the next line, whose text is seen:
    /// the render mode is text state and survives <c>ET</c>.
    /// </summary>
    [Fact]
    public void TheModeIsPutBackForTheNextLine()
    {
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "gone", IsInvisible = true });
        section.Children.Add(p);
        var q = new Paragraph();
        q.Runs.Add(new TextRun { Text = "Next" });
        section.Children.Add(q);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions()));

        int gone = IndexOf(content, "(gone) Tj"), off = IndexOf(content, "0 Tr"), next = IndexOf(content, "(Next) Tj");
        Assert.True(gone < off && off < next, $"order: gone at {gone}, 0 Tr at {off}, Next at {next}");
        Assert.Equal(1, Regex.Matches(content, @"\b3 Tr\b").Count);
    }

    /// <summary>The HTML exporter says the same thing in CSS.</summary>
    [Fact]
    public void TheHtmlExporterWritesColorTransparent()
    {
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "gone", IsInvisible = true, Color = "#FF0000" });
        p.Runs.Add(new TextRun { Text = "seen", Color = "#FF0000" });
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string html = System.Text.Encoding.UTF8.GetString(doc.ToHtml());

        Assert.Contains("color:transparent;", html, StringComparison.Ordinal);
        Assert.Contains("color:#FF0000;", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("color:transparent;", StringComparison.Ordinal) < html.IndexOf("color:#FF0000;", StringComparison.Ordinal));
    }
}
