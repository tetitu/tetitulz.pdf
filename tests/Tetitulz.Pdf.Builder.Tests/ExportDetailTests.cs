// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Low-severity export details where the output could be wrong or silent: duplicate ODF
/// identifiers, a colour that cannot be parsed painted black without saying so, and a font file
/// that is present but broken looking exactly like one that is absent.
/// </summary>
public class ExportDetailTests
{
    private static Document OneSection(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (var c in children) section.Children.Add(c);
        doc.Sections.Add(section);
        return doc;
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static Table OneCellTable(string text, Table? nested = null)
    {
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(Para(text));
        if (nested != null) cell.Blocks.Add(nested);
        row.Cells.Add(cell);
        table.Rows.Add(row);
        return table;
    }

    private static XDocument OdtContent(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsOdt(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("content.xml");
        Assert.NotNull(entry);
        using var s = entry!.Open();
        return XDocument.Load(s);
    }

    private static readonly XNamespace TableNs =
        "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    // ================================================================ ODF identifiers

    /// <summary>
    /// Every <c>table:name</c> in one document is distinct.
    /// </summary>
    /// <remarks>
    /// It is an ODF identifier that must be unique in the document, and every table — nested ones
    /// included — was written as the literal "Table1". LibreOffice silently renames the duplicates
    /// on load, so a round trip does not reproduce the file, and a validating consumer rejects it.
    /// </remarks>
    [Fact]
    public void OdtTableNames_AreUniqueAcrossTheDocument()
    {
        var doc = OneSection(
            OneCellTable("first"),
            OneCellTable("second", nested: OneCellTable("inner")),
            OneCellTable("third"));

        var names = OdtContent(doc).Descendants(TableNs + "table")
            .Select(t => t.Attribute(TableNs + "name")!.Value)
            .ToList();

        Assert.Equal(4, names.Count);                    // three top-level plus the nested one
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OdtSingleTable_IsStillNamedTable1()
    {
        var names = OdtContent(OneSection(OneCellTable("only"))).Descendants(TableNs + "table")
            .Select(t => t.Attribute(TableNs + "name")!.Value)
            .ToList();

        Assert.Equal(new[] { "Table1" }, names);
    }

    // ================================================================ colour parsing

    /// <summary>
    /// The CSS <c>#RGB</c> shorthand is honoured rather than painted black.
    /// </summary>
    /// <remarks>
    /// Anything shorter than six hex digits fell through to (0,0,0), so <c>#f00</c> — a plausible
    /// value for a hand-written caller, and the model documents these properties only as "(hex)" —
    /// drew black instead of red.
    /// </remarks>
    [Theory]
    [InlineData("#f00", 1.0, 0.0, 0.0)]
    [InlineData("#0f0", 0.0, 1.0, 0.0)]
    [InlineData("0AF", 0.0, 0.666, 1.0)]
    public void ShorthandHexColour_IsExpanded(string value, double r, double g, double b)
    {
        var (pr, pg, pb) = PdfExporter.ParseColorD(value);

        Assert.Equal(r, pr, 2);
        Assert.Equal(g, pg, 2);
        Assert.Equal(b, pb, 2);
    }

    [Theory]
    [InlineData("#FF0000", 1.0, 0.0, 0.0)]
    [InlineData("#000000", 0.0, 0.0, 0.0)]
    public void FullHexColour_IsUnchanged(string value, double r, double g, double b)
    {
        var (pr, pg, pb) = PdfExporter.ParseColorD(value);

        Assert.Equal(r, pr, 3);
        Assert.Equal(g, pg, 3);
        Assert.Equal(b, pb, 3);
    }

    /// <summary>
    /// A colour the parser cannot read is REPORTED, not silently turned into black.
    /// </summary>
    /// <remarks>
    /// <c>Shading = "white"</c> painted a solid black rectangle behind the paragraph's black text,
    /// making it unreadable, and nothing anywhere said so: "the caller asked for black" and "the
    /// caller's colour was unparseable" produced identical output.
    /// </remarks>
    [Fact]
    public void UnparseableColour_IsReported()
    {
        var p = Para("readable?");
        p.Shading = "white";

        var seen = Capture(() => OneSection(p).ToPdf());

        Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
                                   && d.Detail.Contains("white"));
    }

    [Fact]
    public void HexColour_IsNotReported()
    {
        var p = Para("fine");
        p.Shading = "#EEEEEE";

        var seen = Capture(() => OneSection(p).ToPdf());

        Assert.DoesNotContain(seen, d => d.Detail.Contains("not a #RGB"));
    }

    /// <summary>
    /// The report is capped, because it runs once per drawn piece.
    /// </summary>
    /// <remarks>
    /// A document that sets one bad colour on every run would otherwise mint one message and one
    /// <c>OnEvent</c> invocation per piece — the flood shape the table diagnostics are guarded
    /// against for the same reason.
    /// </remarks>
    [Fact]
    public void ManyUnparseableColours_ReportAtMostTheCap()
    {
        var p = new Paragraph();
        for (int i = 0; i < 60; i++)
            p.Runs.Add(new TextRun { Text = "w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ", Color = "colour" + i });

        var seen = Capture(() => OneSection(p).ToPdf());

        int reports = seen.Count(d => d.Detail.Contains("not a #RGB"));
        Assert.InRange(reports, 1, PdfExporter.MaxColorReportsPerExport);
    }

    // ================================================================ font files

    /// <summary>
    /// A font file that exists but cannot be read or parsed is reported, naming the file.
    /// </summary>
    /// <remarks>
    /// The bare <c>catch</c> reported nothing and memoised the null, so on a container where a
    /// system font is truncated by a partial layer copy every non-Latin run fell back to
    /// <c>'?'</c> and the only diagnostic named the code points, never the file that was tried —
    /// the exact distinction <see cref="BuilderDiagnosticKind"/> exists to preserve.
    /// </remarks>
    [Fact]
    public void BrokenFontFile_IsReportedByPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-font-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // A unique path per run, so the resolver's process-lifetime file cache cannot answer
        // from a previous test's attempt.
        string path = Path.Combine(dir, "Broken.ttf");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00 });

        try
        {
            // Held open exclusively: File.Exists succeeds and File.ReadAllBytes throws, which is
            // the "present but unreadable" case the finding names (permissions, in production).
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            var resolver = new BuilderFontResolver(PdfExportOptions.Default);
            var seen = Capture(() => Assert.Null(resolver.LoadFontFileForTest(path)));

            Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected
                                       && d.Detail.Contains("Broken.ttf"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>A file that is simply absent is not a rejection — it is the ordinary miss.</summary>
    [Fact]
    public void AbsentFontFile_IsNotReported()
    {
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-absent-" + Guid.NewGuid().ToString("N") + ".ttf");
        var resolver = new BuilderFontResolver(PdfExportOptions.Default);

        var seen = Capture(() => Assert.Null(resolver.LoadFontFileForTest(path)));

        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
    }

    // ================================================================ helper

    /// <summary>Diagnostics raised during <paramref name="export"/>, filtered to this thread's.</summary>
    private static List<BuilderDiagnostic> Capture(Action export)
    {
        var seen = new List<BuilderDiagnostic>();
        int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        void Handler(BuilderDiagnostic d)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == thread) seen.Add(d);
        }
        BuilderDiagnostics.OnEvent += Handler;
        try { export(); }
        finally { BuilderDiagnostics.OnEvent -= Handler; }
        return seen;
    }
}
