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
/// The DOCX exporter writes a note's content through the run writer, which may meet a note: a chain of
/// distinct notes recursed once a level until the stack overflowed, uncatchably — the note map stops only
/// a note re-entering itself. The chain is bounded at the exporters' shared nesting ceiling and the
/// truncation reported, as the ODT exporter does; an ordinary note is written whole (the control).
/// </summary>
public class DocxFootnoteDepthTests
{
    private static (byte[] Bytes, List<BuilderDiagnostic> Reports) Export(Document doc)
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
            new DocxExporter().Export(doc, ms);
            return (ms.ToArray(), reports);
        }
        finally { BuilderDiagnostics.OnEvent -= Handler; }
    }

    private static Document WithBody(Footnote note)
    {
        var para = new Paragraph();
        para.Runs.Add(new TextRun { Text = "Body" });
        para.Runs.Add(note);
        var section = new Section();
        section.Children.Add(para);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc;
    }

    [Fact]
    public void AdversarialChainOfDistinctNotes_IsDepthCapped_NotOverflowed()
    {
        var first = new Footnote();
        var current = first;
        for (int i = 0; i < 100_000; i++)
        {
            current.Runs.Add(new TextRun { Text = "n" + i });
            var next = new Footnote();
            current.Runs.Add(next);
            current = next;
        }

        var (bytes, reports) = Export(WithBody(first));

        Assert.NotEmpty(bytes);
        Assert.Contains(reports, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
            && d.Detail.Contains("Footnotes nested more than", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOrdinaryNote_IsWrittenWithoutTruncation()
    {
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = "OrdinaryNoteText" });

        var (bytes, reports) = Export(WithBody(note));

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry("word/footnotes.xml")!.Open());
        Assert.Contains("OrdinaryNoteText", r.ReadToEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain(reports, d => d.Detail.Contains("Footnotes nested more than", StringComparison.Ordinal));
    }
}
