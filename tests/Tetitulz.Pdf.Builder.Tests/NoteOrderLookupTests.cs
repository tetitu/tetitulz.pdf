// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Notes are numbered and listed in first-seen order, a note used twice once — kept by a lookup beside the
/// list rather than a search of it (an <c>IndexOf</c> or <c>Contains</c> per mark was n²/2 over n notes: the
/// HTML export took 0.2 s at 8 000 notes and grew twelvefold for each fourfold).
/// </summary>
public class NoteOrderLookupTests
{
    private static (Document Doc, Footnote Shared) Doc(NotePlacement placement)
    {
        var shared = new Footnote { Placement = placement };
        shared.Runs.Add(new TextRun { Text = "SHARED" });
        var section = new Section();
        for (int i = 0; i < 3; i++)
        {
            var p = new Paragraph { Runs = { new TextRun { Text = "P" + i } } };
            var own = new Footnote { Placement = placement };
            own.Runs.Add(new TextRun { Text = "OWN" + i });
            p.Runs.Add(own);
            p.Runs.Add(shared);
            section.Children.Add(p);
        }
        var doc = new Document();
        doc.Sections.Add(section);
        return (doc, shared);
    }

    /// <summary>
    /// The HTML export lists each kind once per note, in first-seen order: OWN0, SHARED, OWN1, OWN2 — the shared
    /// note's three marks all point at its one entry.
    /// </summary>
    [Theory]
    [InlineData(NotePlacement.PageFoot, "fn")]
    [InlineData(NotePlacement.DocumentEnd, "en")]
    public void TheHtmlExport_ListsEachNoteOnce_InFirstSeenOrder(NotePlacement placement, string prefix)
    {
        string html = Encoding.UTF8.GetString(Doc(placement).Doc.ToHtml());

        var items = Regex.Matches(html, "<li id=\"" + prefix + "(\\d+)\"><p>(\\w+)").Select(m => m.Groups[1].Value + ":" + m.Groups[2].Value).ToArray();
        Assert.Equal(new[] { "1:OWN0", "2:SHARED", "3:OWN1", "4:OWN2" }, items);
        Assert.Equal(3, Regex.Matches(html, "href=\"#" + prefix + "2\"").Count);
    }

    /// <summary>The PDF export's endnote list holds the shared note once, in first-seen order.</summary>
    [Fact]
    public void TheEndnoteList_HoldsASharedNoteOnce()
    {
        var (doc, shared) = Doc(NotePlacement.DocumentEnd);
        var endnotes = Export.FootnoteNumbering.For(doc).Endnotes;
        Assert.Equal(4, endnotes.Count);
        Assert.Same(shared, endnotes[1]);
    }
}
