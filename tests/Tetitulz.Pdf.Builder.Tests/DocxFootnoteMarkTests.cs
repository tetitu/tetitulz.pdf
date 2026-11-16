// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// An explicit <see cref="Footnote.Mark"/> in the DOCX export: written as Word's own custom mark,
/// so the dagger a caller asked for is the dagger Word shows.
/// </summary>
/// <remarks>
/// <para><b>The mark used to be dropped and reported</b>, on the grounds that Word numbers its own
/// notes and a custom mark "needs a style this exporter does not write". It needs no style:
/// §17.11.14 gives <c>w:footnoteReference</c> a <c>w:customMarkFollows</c> attribute, and when it is
/// set the run's following text IS the mark, in the body and in the note alike. That is how Word
/// itself writes a note marked with a symbol.</para>
///
/// <para><b>Both places, because either alone is a different document.</b> A custom mark in the body
/// with an automatic number in the note is a dagger pointing at "1"; the reverse is a "1" pointing
/// at a dagger. The two runs are asserted separately for that reason.</para>
/// </remarks>
public sealed class DocxFootnoteMarkTests
{
    /// <summary>The body run says a custom mark follows, and the mark follows it.</summary>
    [Fact]
    public void AnExplicitMark_IsWrittenAsACustomMarkFollowingTheReference()
    {
        XDocument document = DocxPart(Doc(WithNote("†")), "word/document.xml");

        XElement reference = document.Descendants(W + "footnoteReference").Single();

        Assert.True(IsOn(reference.Attribute(W + "customMarkFollows")?.Value),
            "w:customMarkFollows is not set, so Word draws its own number over the caller's mark");

        // The same run, immediately after the reference — that is where §17.11.14 puts the mark.
        XElement? next = reference.ElementsAfterSelf().FirstOrDefault();
        Assert.True(next != null && next.Name == W + "t", "no text follows the reference in its run");
        Assert.Equal("†", next!.Value);
    }

    /// <summary>The note itself carries the mark rather than an automatic number.</summary>
    /// <remarks>
    /// <c>w:footnoteRef</c> is the automatic number inside the note; a custom-marked note writes the
    /// mark's text in its place. Leaving the automatic mark there gives a dagger in the body and a
    /// "1" at the foot of the page.
    /// </remarks>
    [Fact]
    public void TheNoteItself_CarriesTheMarkInsteadOfAnAutomaticNumber()
    {
        Document doc = Doc(WithNote("†"));

        string id = DocxPart(doc, "word/document.xml")
            .Descendants(W + "footnoteReference").Single().Attribute(W + "id")!.Value;

        XElement note = DocxPart(doc, "word/footnotes.xml").Descendants(W + "footnote")
            .Single(f => f.Attribute(W + "id")?.Value == id);

        Assert.Empty(note.Descendants(W + "footnoteRef"));
        Assert.Equal("†", note.Descendants(W + "t").First().Value);
    }

    /// <summary>An explicit mark is no longer reported as dropped, because it is not.</summary>
    [Fact]
    public void AnExplicitMark_RaisesNoDroppedReport()
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(Doc(WithNote("†")), ms);

        Assert.DoesNotContain("explicit mark", BuilderDiagnostics.LastError?.Detail ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>An ordinary note is unchanged: automatic numbering in both places.</summary>
    /// <remarks>
    /// The acceptance control. Setting <c>w:customMarkFollows</c> on every reference would make
    /// Word draw whatever text happens to follow as the mark — for an automatically numbered note,
    /// the next word of the sentence.
    /// </remarks>
    [Fact]
    public void AnOrdinaryNote_KeepsAutomaticNumberingInBothPlaces()
    {
        Document doc = Doc(WithNote(null));

        XElement reference = DocxPart(doc, "word/document.xml")
            .Descendants(W + "footnoteReference").Single();
        Assert.Null(reference.Attribute(W + "customMarkFollows"));

        XElement note = DocxPart(doc, "word/footnotes.xml").Descendants(W + "footnote")
            .Single(f => f.Attribute(W + "id")?.Value == reference.Attribute(W + "id")!.Value);
        Assert.Single(note.Descendants(W + "footnoteRef"));
    }

    // ==================================================================== helpers

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>ST_OnOff: Word writes <c>1</c>/<c>true</c>/<c>on</c> and readers accept all three.</summary>
    private static bool IsOn(string? value)
        => value is "1" or "true" or "on";

    private static Paragraph WithNote(string? mark)
    {
        var note = new Footnote { Mark = mark };
        note.Runs.Add(new TextRun { Text = "the note itself" });

        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "Body" });
        paragraph.Runs.Add(note);
        paragraph.Runs.Add(new TextRun { Text = " continues" });
        return paragraph;
    }

    private static Document Doc(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (IDocElement child in children) section.Children.Add(child);
        doc.Sections.Add(section);
        return doc;
    }

    private static XDocument DocxPart(Document doc, string entryName)
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = zip.GetEntry(entryName);
        Assert.True(entry != null, $"the package has no {entryName}");

        using Stream stream = entry!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return XDocument.Parse(Encoding.UTF8.GetString(copy.ToArray()));
    }
}
