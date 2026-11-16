// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A search-and-redact removes a word from the text the document holds outside its pages as well:
/// the information dictionary, the outline's titles and the catalog's XMP packet. A word left in a
/// title is a word the caller was told was removed.
/// </summary>
public sealed class TextRedactionDocumentTextTests
{
    private const string Marker = "QZXSECRET";

    private static string S(string body, string dict = "") => $"<< {dict} /Length {Encoding.UTF8.GetByteCount(body)} >>\nstream\n{body}\nendstream";

    private static string Xmp(string title) =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
        + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" pdf:Keywords=\"" + title + "\">"
        + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">" + title + "</rdf:li></rdf:Alt></dc:title>"
        + "<pdf:Producer>kept producer</pdf:Producer><!-- note " + title + " --></rdf:Description></rdf:RDF></x:xmpmeta>"
        + "<?xpacket end=\"w\"?>";

    /// <summary>
    /// One page showing <paramref name="pageText"/>, an outline of an item and its child (the child's
    /// title an indirect string, object 11), an information dictionary and, when given, an XMP packet.
    /// </summary>
    private static byte[] Pdf(string pageText, string info, string outlineTitle = "(Chapter)", string childTitle = "(Section)", string xmp = null)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R" + (xmp != null ? " /Metadata 9 0 R" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            S($"BT /F1 12 Tf 20 100 Td ({pageText}) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 2 >>",
            $"<< /Title {outlineTitle} /Parent 6 0 R /First 8 0 R /Last 8 0 R /Count 1 /Dest [3 0 R /Fit] >>",
            "<< /Title 11 0 R /Parent 7 0 R /Dest [3 0 R /Fit] >>",
            S(xmp ?? "", "/Type /Metadata /Subtype /XML"),
            $"<< {info} >>",
            childTitle,
        };
        var offsets = new List<int>();
        var bytes = new List<byte>();
        void Append(string s) { bytes.AddRange(Encoding.UTF8.GetBytes(s)); }
        Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(bytes.Count);
            Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = bytes.Count;
        Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) Append(offset.ToString("D10") + " 00000 n \n");
        Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R /Info 10 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return bytes.ToArray();
    }

    /// <summary>The file's bytes and every stream's decoded bytes, for a marker anywhere in them.</summary>
    private static string Everything(byte[] pdf)
    {
        var all = new StringBuilder(Encoding.Latin1.GetString(pdf));
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        foreach (CrossReferenceEntry e in doc.CrossReferenceTable.AllEntries)
            if (e.Type != CrossReferenceEntry.EntryType.Free
                && doc.GetObject(e.ObjectNumber) is PdfStream stream
                && stream.TryGetDecodedData(1 << 24, out byte[] decoded, out _))
                all.Append(Encoding.Latin1.GetString(decoded)).Append('\n');
        return all.ToString();
    }

    private static byte[] Redact(byte[] pdf, string pattern, TextRedactionOptions options = null)
    {
        using var output = new MemoryStream();
        PdfTextRedactor.Redact(new MemoryStream(pdf), output, new[] { pattern }, options);
        return output.ToArray();
    }

    private static string InfoText(byte[] pdf, PdfName key)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        return (doc.ResolveObject(info?[key]) as PdfString)?.GetText();
    }

    private static List<string> OutlineTitles(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        var titles = new List<string>();
        OutlineItems.Walk(doc, doc.Catalog.Dictionary, item =>
        {
            titles.Add((doc.ResolveObject(item[PdfName.Title]) as PdfString)?.GetText());
            return true;
        });
        return titles;
    }

    private static string Packet(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata]) is PdfStream s
            && s.TryGetDecodedData(1 << 20, out byte[] data, out _) ? Encoding.UTF8.GetString(data) : null;
    }

    [Fact]
    public void The_information_the_outline_and_the_packet_lose_the_word_with_the_page()
    {
        byte[] pdf = Pdf($"pre {Marker} post", $"/Title (Report {Marker} final) /Author (kept author)",
            outlineTitle: $"(See {Marker})", childTitle: $"({Marker} child)", xmp: Xmp($"T {Marker}"));
        Assert.Contains(Marker, Everything(pdf), StringComparison.Ordinal);

        byte[] output = Redact(pdf, Marker);

        Assert.DoesNotContain(Marker, Everything(output), StringComparison.Ordinal);
        Assert.Equal("Report  final", InfoText(output, PdfName.Title));
        Assert.Equal("kept author", InfoText(output, PdfName.Author));
        Assert.Equal(new[] { "See ", " child" }, OutlineTitles(output));
        string packet = Packet(output);
        Assert.Contains("kept producer", packet, StringComparison.Ordinal);
        Assert.Contains(">T <", packet, StringComparison.Ordinal);
        Assert.Contains("/Info /Title", PdfTextRedactor.LastDocumentTextRemoved, StringComparison.Ordinal);
        Assert.Contains("outline", PdfTextRedactor.LastDocumentTextRemoved, StringComparison.Ordinal);
        Assert.Contains("XMP packet", PdfTextRedactor.LastDocumentTextRemoved, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, PdfTextRedactor.LastDocumentTextRemoved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_word_only_in_the_metadata_is_removed_though_no_page_shows_it()
    {
        byte[] pdf = Pdf("nothing to see", $"/Title (Report {Marker} final)");

        using var output = new MemoryStream();
        var matches = PdfTextRedactor.Redact(new MemoryStream(pdf), output, new[] { Marker });

        Assert.Empty(matches);
        Assert.DoesNotContain(Marker, Everything(output.ToArray()), StringComparison.Ordinal);
        Assert.Equal("Report  final", InfoText(output.ToArray(), PdfName.Title));
        Assert.Equal("/Info /Title", PdfTextRedactor.LastDocumentTextRemoved);
    }

    [Fact]
    public void Turned_off_the_documents_own_text_keeps_the_word_and_the_page_loses_it()
    {
        byte[] pdf = Pdf($"pre {Marker} post", $"/Title (Report {Marker} final)");

        byte[] output = Redact(pdf, Marker, new TextRedactionOptions { IncludeDocumentText = false });

        Assert.Equal($"Report {Marker} final", InfoText(output, PdfName.Title));
        Assert.DoesNotContain(Marker, PdfOperations.ExtractText(new MemoryStream(output)), StringComparison.Ordinal);
        Assert.Null(PdfTextRedactor.LastDocumentTextRemoved);
    }

    [Fact]
    public void A_document_with_the_word_nowhere_is_copied_and_reports_nothing()
    {
        // A redaction that removed something first, so a report left over would show.
        Redact(Pdf("nothing", $"/Title ({Marker})"), Marker);
        Assert.NotNull(PdfTextRedactor.LastDocumentTextRemoved);
        byte[] pdf = Pdf("nothing to see", "/Title (Report final)", xmp: Xmp("clean"));

        byte[] output = Redact(pdf, Marker);

        Assert.Equal(pdf, output);
        Assert.Null(PdfTextRedactor.LastDocumentTextRemoved);
    }

    [Fact]
    public void AdversarialOutline_NestedPastTheWalksDepth_RefusesTheRedaction()
    {
        // Items 7, 8, ... each the only child of the one before: titles below the cap are never read.
        int depth = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTreeDepth + 5;
        string text = Encoding.Latin1.GetString(Pdf("nothing", "/Title (plain)"));
        var items = new StringBuilder();
        int first = 12;
        for (int i = 0; i < depth; i++)
        {
            int n = first + i;
            string child = i + 1 < depth ? $" /First {n + 1} 0 R /Last {n + 1} 0 R /Count 1" : "";
            items.Append($"{n} 0 obj\n<< /Title ({(i + 1 == depth ? Marker : "level")}) /Parent {(i == 0 ? 6 : n - 1)} 0 R{child} >>\nendobj\n");
        }
        text = text.Replace("/Type /Outlines /First 7 0 R /Last 7 0 R", $"/Type /Outlines /First {first} 0 R /Last {first} 0 R", StringComparison.Ordinal);
        text = text.Replace("xref\n", items + "xref\n", StringComparison.Ordinal);   // the table is off now: the reader rebuilds it
        byte[] pdf = Encoding.Latin1.GetBytes(text);

        var refused = Assert.Throws<PdfRedactionException>(() => Redact(pdf, Marker));
        Assert.Contains("outline", refused.Message, StringComparison.Ordinal);

        // Control: under the option that leaves the document's own text alone the same file is redacted,
        // and the deepest title is the file's still.
        using var output = new MemoryStream();
        PdfTextRedactor.Redact(new MemoryStream(pdf), output, new[] { Marker }, new TextRedactionOptions { IncludeDocumentText = false });
        Assert.Contains(Marker, Encoding.Latin1.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialPacket_NestedPastTheMergeDepth_IsRemovedWhole()
    {
        int depth = Tetitulz.Pdf.Core.Metadata.XmpPacketDom.MaxSearchDepth + 10;
        string deep = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" + string.Concat(Enumerable.Repeat("<a>", depth)) + Marker
            + string.Concat(Enumerable.Repeat("</a>", depth)) + "</x:xmpmeta>";

        byte[] output = Redact(Pdf("nothing", "/Title (plain)", xmp: deep), Marker);

        Assert.Null(Packet(output));
        Assert.Contains("whole", PdfTextRedactor.LastDocumentTextRemoved, StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialPacket_PastTheReadCeiling_IsRemovedWhole()
    {
        string large = Xmp(Marker + new string(' ', Tetitulz.Pdf.Core.Metadata.XmpPacketDom.MaxExistingPacketBytes));

        byte[] output = Redact(Pdf("nothing", "/Title (plain)", xmp: large), Marker);

        Assert.Null(Packet(output));
        Assert.Contains("whole", PdfTextRedactor.LastDocumentTextRemoved, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Everything(output), StringComparison.Ordinal);
    }

    /// <summary>The word nested in itself <paramref name="depth"/> times: each removal leaves another whole.</summary>
    private static string Nested(int depth)
    {
        string s = Marker;
        for (int i = 0; i < depth; i++) s = Marker.Substring(0, 4) + s + Marker.Substring(4);
        return s;
    }

    [Fact]
    public void A_removal_that_joins_another_match_is_searched_again()
    {
        byte[] output = Redact(Pdf("nothing", $"/Title (keep {Nested(10)} this)"), Marker);

        Assert.Equal("keep  this", InfoText(output, PdfName.Title));
    }

    [Fact]
    public void AdversarialTitle_NestingPastThePassLimit_IsEmptied()
    {
        // Each pass removes one level, so a nesting past the limit would cost a pass per level.
        byte[] output = Redact(Pdf("nothing", $"/Title (keep {Nested(PdfTextRedactor.MaxCleaningPasses + 10)} this)"), Marker);

        Assert.Equal("", InfoText(output, PdfName.Title));
        Assert.DoesNotContain(Marker, Everything(output), StringComparison.Ordinal);
    }

    [Fact]
    public void A_packet_that_is_not_XML_is_removed_whole_and_a_readable_clean_one_kept()
    {
        byte[] broken = Redact(Pdf("nothing", $"/Title ({Marker})", xmp: $"<x:xmpmeta>{Marker} never closed"), Marker);

        Assert.Null(Packet(broken));
        Assert.Contains("whole", PdfTextRedactor.LastDocumentTextRemoved, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Everything(broken), StringComparison.Ordinal);

        // Control: a packet without the word stays as it was.
        byte[] clean = Redact(Pdf("nothing", $"/Title ({Marker})", xmp: Xmp("clean title")), Marker);
        Assert.Equal(Xmp("clean title"), Packet(clean));
    }

    private static string Utf16(string text) => "<FEFF" + string.Concat(text.Select(c => ((int)c).ToString("X4"))) + ">";

    [Fact]
    public void Under_Hebrew_folding_a_pointed_title_loses_the_whole_pointed_word()
    {
        const string Pointed = "שָׁלוֹם", Plain = "שלום", Other = "עולם";
        byte[] pdf = Pdf("nothing", "/Title " + Utf16(Pointed + " " + Other));

        byte[] output = Redact(pdf, Plain, new TextRedactionOptions { HebrewFolding = HebrewFolding.Default });

        Assert.Equal(" " + Other, InfoText(output, PdfName.Title));
    }

    [Fact]
    public void An_identifier_redaction_removes_the_number_from_the_information_too()
    {
        const string Id = "123456782";
        Assert.True(IsraeliIdentifiers.IsValid(Id));
        byte[] pdf = Pdf($"id {Id}", $"/Subject (holder {Id}) /Title (case 12345)");

        using var output = new MemoryStream();
        PdfTextRedactor.RedactIsraeliIdentifiers(new MemoryStream(pdf), output);

        Assert.Equal("holder ", InfoText(output.ToArray(), PdfName.Subject));
        Assert.Equal("case 12345", InfoText(output.ToArray(), PdfName.Title));   // not an identifier
        Assert.DoesNotContain(Id, Everything(output.ToArray()), StringComparison.Ordinal);
    }
}
