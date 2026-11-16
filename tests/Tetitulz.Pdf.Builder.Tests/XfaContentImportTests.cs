// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Import;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Reading a <b>dynamic</b> XFA form's content out of a PDF and building a <see cref="Document"/>
/// from it — the half of dynamic XFA that needs no template grammar.
/// </summary>
/// <remarks>
/// <para>The fixtures are §12.7.8's array shape of <c>/AcroForm /XFA</c>, with a <c>datasets</c>
/// packet that holds the form's values. A dynamic form's AcroForm carries nothing worth reading,
/// which is why these assert on the datasets and never on a widget.</para>
/// <para><b>Every assertion is about the model that came out</b>, not about a call having returned
/// something: an importer that produced an empty document would return a perfectly valid
/// <see cref="Document"/>, and the last test here puts the values through the PDF exporter so the
/// claim "this is a real document" is checked rather than asserted.</para>
/// </remarks>
public sealed class XfaContentImportTests
{
    // ---- fixtures ----

    private static string Datasets(string body) =>
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
        + "<xfa:data>" + body + "</xfa:data></xfa:datasets>";

    private const string InvoiceData =
        "<invoice><number>INV-2026-014</number><date>2026-09-20</date>"
        + "<customer><name>Ada Lovelace</name><city>London</city></customer>"
        + "<total>1234.50</total><note></note></invoice>";

    private static byte[] XfaPdf(string datasetsXml, bool packaged = true) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        Contents("BT ET"),
        packaged
            ? "<< /Fields [] /XFA [(template) 6 0 R (datasets) 7 0 R] >>"
            : "<< /Fields [] /XFA 6 0 R >>",
        packaged
            ? Contents("<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/>")
            : Contents("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" + datasetsXml + "</xdp:xdp>"),
        Contents(datasetsXml),
    });

    private static byte[] PlainPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        Contents("BT ET"),
    });

    private static string Contents(string body) =>
        "<< /Length " + body.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + body + "\nendstream";

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static Document Read(byte[] pdf, XfaContentOptions? options = null)
    {
        using var input = new MemoryStream(pdf, writable: false);
        Document? doc = XfaContent.Read(input, options);
        Assert.True(doc != null, "no document was produced: " + XfaContent.LastError);
        return doc!;
    }

    /// <summary>Every cell of every table in the document, row by row.</summary>
    private static List<(string Label, string Value)> Rows(Document doc) =>
        doc.Sections[0].Children.OfType<Table>()
           .SelectMany(t => t.Rows)
           .Select(r => (Text(r.Cells[0]), Text(r.Cells[1])))
           .ToList();

    private static List<string> Headings(Document doc) =>
        doc.Sections[0].Children.OfType<Paragraph>().Select(TextOf).ToList();

    // Content(), not Blocks: AddCell(string) fills the simple Paragraphs list, and Blocks is the
    // richer form that supersedes it only when a caller uses one.
    private static string Text(TableCell cell) =>
        string.Concat(cell.Content().OfType<Paragraph>().Select(TextOf));

    private static string TextOf(Paragraph p) =>
        string.Concat(p.Runs.OfType<TextRun>().Select(r => r.Text));

    // ---- the content ----

    /// <summary>
    /// The values are what the datasets packet holds, under the leaf names the form's designer
    /// gave them.
    /// </summary>
    [Fact]
    public void TheFormsValuesBecomeRowsUnderTheirOwnFieldNames()
    {
        var rows = Rows(Read(XfaPdf(Datasets(InvoiceData))));

        Assert.Contains(("number", "INV-2026-014"), rows);
        Assert.Contains(("name", "Ada Lovelace"), rows);
        Assert.Contains(("total", "1234.50"), rows);
    }

    /// <summary>
    /// A subform path becomes a heading, so a nested group reads as a group rather than as a flat
    /// list of names that repeat across sections.
    /// </summary>
    [Fact]
    public void ASubformPathBecomesAHeadingAboveItsOwnRows()
    {
        Document doc = Read(XfaPdf(Datasets(InvoiceData)));

        Assert.Contains("invoice.customer", Headings(doc));
        Assert.Contains(("city", "London"), Rows(doc));
    }

    /// <summary>
    /// A field the form left empty is not content. It is in the packet because the template
    /// declared it, and a mostly-blank form would otherwise read as pages of empty rows.
    /// </summary>
    [Fact]
    public void AnEmptyFieldIsOmitted()
    {
        Assert.DoesNotContain(Rows(Read(XfaPdf(Datasets(InvoiceData)))), r => r.Label == "note");
    }

    /// <summary>The acceptance control: the same field is kept when the caller asks for it.</summary>
    [Fact]
    public void AnEmptyFieldIsKeptWhenAsked()
    {
        var rows = Rows(Read(XfaPdf(Datasets(InvoiceData)),
                             new XfaContentOptions { IncludeEmptyValues = true }));

        Assert.Contains(("note", string.Empty), rows);
    }

    /// <summary>
    /// The order is the paths', not the dictionary's. A document whose sections shuffle between
    /// runs is worse than one in name order, and a dictionary's enumeration order is an
    /// implementation detail.
    /// </summary>
    [Fact]
    public void TheOrderIsByPathAndIsTheSameEveryRun()
    {
        var first = Rows(Read(XfaPdf(Datasets(InvoiceData))));
        var second = Rows(Read(XfaPdf(Datasets(InvoiceData))));

        Assert.Equal(first, second);
        // Five non-empty leaves, and `invoice.customer.city` sorts before every `invoice.<leaf>`.
        Assert.Equal(5, first.Count);
        Assert.Equal("city", first[0].Label);
    }

    /// <summary>The title is written when there is one, and nothing is written when there is not.</summary>
    [Fact]
    public void TheTitleIsWrittenOnlyWhenTheCallerSuppliesOne()
    {
        Assert.Contains("Invoice", Headings(Read(XfaPdf(Datasets(InvoiceData)),
                                                 new XfaContentOptions { Title = "Invoice" })));
        Assert.DoesNotContain("Invoice", Headings(Read(XfaPdf(Datasets(InvoiceData)))));
    }

    // ---- what it refuses, and why ----

    /// <summary>A document with no <c>/XFA</c> entry is refused by name rather than returning nothing.</summary>
    [Fact]
    public void ADocumentWithNoXfaIsRefusedByName()
    {
        using var input = new MemoryStream(PlainPdf(), writable: false);

        Assert.Null(XfaContent.Read(input));
        Assert.Contains("no /AcroForm /XFA entry", XfaContent.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// An unpackaged single-stream XDP is reported rather than quietly skipped: its datasets are in
    /// there, and splitting an XDP is an XML problem this does not take on.
    /// </summary>
    [Fact]
    public void AnUnpackagedXdpStreamIsReportedRatherThanQuietlySkipped()
    {
        using var input = new MemoryStream(XfaPdf(Datasets(InvoiceData), packaged: false), writable: false);

        Assert.Null(XfaContent.Read(input));
        Assert.Contains("single XDP stream", XfaContent.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>Malformed XML is reported, not thrown, and not read as an empty form.</summary>
    [Fact]
    public void MalformedDatasetsXmlIsReportedRatherThanReadAsEmpty()
    {
        using var input = new MemoryStream(XfaPdf("<xfa:datasets><unclosed>"), writable: false);

        Assert.Null(XfaContent.Read(input));
        Assert.Contains("not well-formed", XfaContent.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A packet declaring an external entity is refused rather than expanded — the reader prohibits
    /// DTDs, which closes XXE and entity expansion together. This is attacker-controlled XML: it
    /// arrives inside a PDF, from whoever produced the PDF.
    /// </summary>
    [Fact]
    public void AdversarialExternalEntityIsRefusedAndFetchesNothing()
    {
        string hostile =
            "<!DOCTYPE datasets [ <!ENTITY leak SYSTEM \"file:///C:/Windows/win.ini\"> ]>"
            + Datasets("<invoice><total>&leak;</total></invoice>");

        using var input = new MemoryStream(XfaPdf(hostile), writable: false);

        Assert.Null(XfaContent.Read(input));
        Assert.Contains("not well-formed", XfaContent.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream the caller has already read to the end still reads. A caller who just wrote or
    /// inspected the bytes hands one over at its end.
    /// </summary>
    /// <remarks>
    /// This holds because a PDF is opened by seeking, not by reading forward from the cursor — a
    /// mutation is what established that, by proving an explicit rewind here was dead code.
    /// </remarks>
    [Fact]
    public void AStreamLeftAtItsEndStillReads()
    {
        using var input = new MemoryStream(XfaPdf(Datasets(InvoiceData)), writable: false);
        input.Position = input.Length;

        Document? doc = XfaContent.Read(input);

        Assert.True(doc != null, "no document was produced: " + XfaContent.LastError);
        Assert.Contains(("total", "1234.50"), Rows(doc!));
    }

    /// <summary>
    /// A forward-only stream — an HTTP body, a pipe — is copied into memory first, because a PDF is
    /// read from its tail and cannot be opened over a one-pass stream at all.
    /// </summary>
    [Fact]
    public void AForwardOnlyStreamIsBufferedRatherThanRefused()
    {
        using var input = new ForwardOnlyStream(XfaPdf(Datasets(InvoiceData)));

        Document? doc = XfaContent.Read(input);

        Assert.True(doc != null, "no document was produced: " + XfaContent.LastError);
        Assert.Contains(("number", "INV-2026-014"), Rows(doc!));
    }

    /// <summary>
    /// A form whose every field is empty has no content, and says so rather than producing a
    /// document with a heading and nothing under it.
    /// </summary>
    [Fact]
    public void AFormWithNothingFilledInIsReportedRatherThanReturnedEmpty()
    {
        using var input = new MemoryStream(
            XfaPdf(Datasets("<invoice><number></number><total></total></invoice>")), writable: false);

        Assert.Null(XfaContent.Read(input, new XfaContentOptions { Title = "Invoice" }));
        Assert.Contains("carries no values", XfaContent.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A stream that cannot seek and cannot report its length.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        internal ForwardOnlyStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary><see cref="XfaContent.IsPresent"/> answers for the entry, not for the packet's contents.</summary>
    [Fact]
    public void IsPresentAnswersForTheEntryItself()
    {
        using var withXfa = new MemoryStream(XfaPdf(Datasets(InvoiceData)), writable: false);
        using var without = new MemoryStream(PlainPdf(), writable: false);

        Assert.True(XfaContent.IsPresent(withXfa));
        Assert.False(XfaContent.IsPresent(without));
    }

    // ---- it is a real document ----

    /// <summary>
    /// The content survives the PDF exporter, which is what makes "a <see cref="Document"/>" a
    /// claim rather than a shape: the values are drawn on a page a reader can extract.
    /// </summary>
    [Fact]
    public void TheContentReachesThePdfExporter()
    {
        byte[] pdf = Read(XfaPdf(Datasets(InvoiceData)),
                          new XfaContentOptions { Title = "Invoice" }).ToPdf();

        // Per drawn string, not as a substring of the whole stream: a cell's text may be laid down
        // in more than one show operator, and a raw search would then pass or fail on the wrap.
        List<string> drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf))
                                            .Select(d => d.Text).ToList();
        string all = string.Join(" | ", drawn);

        Assert.True(drawn.Any(t => t.Contains("INV-2026-014", StringComparison.Ordinal)), all);
        Assert.True(drawn.Any(t => t.Contains("Lovelace", StringComparison.Ordinal)), all);
        Assert.True(drawn.Any(t => t.Contains("Invoice", StringComparison.Ordinal)), all);
    }
}
