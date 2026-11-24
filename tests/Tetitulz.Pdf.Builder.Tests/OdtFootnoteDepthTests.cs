// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The ODT exporter writes a footnote's body inline, recursing into its runs. A footnote that is
/// numbered (referenced from the body) and whose own runs reference it — directly, or through a chain of
/// body-referenced notes that closes into a cycle — would recurse forever: the mark gate does not stop it,
/// because the note is numbered, and the stack would overflow uncatchably, taking the host down. (The HTML
/// exporter has no such path — there a note's mark is only a link and each body renders once.) The
/// recursion is bounded at the exporter's shared nesting ceiling and the truncation reported.
/// </summary>
public class OdtFootnoteDepthTests
{
    private static (byte[] bytes, List<BuilderDiagnostic> reports) ExportOdt(Document doc)
    {
        var reports = new List<BuilderDiagnostic>();
        int tid = Environment.CurrentManagedThreadId;
        void Handler(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId == tid) lock (reports) reports.Add(d);
        }
        BuilderDiagnostics.OnEvent += Handler;
        try
        {
            using var ms = new MemoryStream();
            new OdtExporter().Export(doc, ms);   // must RETURN — pre-fix this overflowed the stack
            return (ms.ToArray(), reports);
        }
        finally { BuilderDiagnostics.OnEvent -= Handler; }
    }

    [Fact]
    public void ASelfReferencingNumberedFootnote_IsDepthCapped_NotOverflowed()
    {
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = "NoteText" });
        note.Runs.Add(note);                       // the note's body contains the note itself

        var para = new Paragraph();
        para.Runs.Add(new TextRun { Text = "Body" });
        para.Runs.Add(note);                       // referenced from the body, so it is numbered

        var doc = new Document();
        var section = new Section();
        section.Children.Add(para);
        doc.Sections.Add(section);

        var (bytes, reports) = ExportOdt(doc);

        Assert.NotEmpty(bytes);
        Assert.Contains(reports, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
            && d.Detail.Contains("Footnotes nested more than", StringComparison.Ordinal));
    }

    /// <summary>The control: an ordinary footnote exports its body once and reports no truncation.</summary>
    [Fact]
    public void AnOrdinaryFootnote_IsExportedWithoutTruncation()
    {
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = "OrdinaryNoteText" });

        var para = new Paragraph();
        para.Runs.Add(new TextRun { Text = "Body" });
        para.Runs.Add(note);

        var doc = new Document();
        var section = new Section();
        section.Children.Add(para);
        doc.Sections.Add(section);

        var (bytes, reports) = ExportOdt(doc);
        string contentXml = ContentXml(bytes);

        Assert.Contains("OrdinaryNoteText", contentXml, StringComparison.Ordinal);  // the note body was written
        Assert.DoesNotContain(reports, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
            && d.Detail.Contains("Footnotes nested more than", StringComparison.Ordinal));
    }

    /// <summary>The ODT package's <c>content.xml</c> — where the document's text lives.</summary>
    private static string ContentXml(byte[] odt)
    {
        using var zip = new ZipArchive(new MemoryStream(odt), ZipArchiveMode.Read);
        ZipArchiveEntry entry = zip.GetEntry("content.xml") ?? throw new Xunit.Sdk.XunitException("no content.xml");
        using var r = new StreamReader(entry.Open());
        return r.ReadToEnd();
    }
}
