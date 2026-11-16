// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The sanitizer's output is a reachability rewrite: content stripped from the graph leaves the
/// file instead of being emitted as an orphan, and the object-stream / cross-reference-stream
/// containers a compressed input is built from do not ship their untouched copies.
/// </summary>
/// <remarks>
/// <para><b>Every assertion reads the emitted bytes, including inside deflated streams.</b> The
/// defect this class pins is precisely one an object-graph assertion could not see: the script was
/// gone from the dictionary and present in the file — in the raw bytes, or inflated out of an
/// object stream the rewrite used to copy through verbatim. <see cref="MarkerSurvives"/> looks in
/// both places, so a payload hiding in a compressed container is found.</para>
/// </remarks>
public class SanitizerReachabilityLeakTests
{
    /// <summary>
    /// A JavaScript action authored as an indirect object, in a file whose objects live in an
    /// object stream — the common modern layout. The pre-rewrite sanitizer cleaned the member
    /// objects but re-emitted the <c>/ObjStm</c> container, which still held the action compressed.
    /// </summary>
    [Fact]
    public void JavaScriptInsideAnObjectStreamDoesNotSurvive()
    {
        const string marker = "PWNED_OBJSTM_JS";
        byte[] pdf = ObjectStreamPdfWithJsAction(marker);

        // Premise: the marker really is in the input, compressed inside the object stream.
        Assert.True(MarkerSurvives(pdf, marker), "fixture should carry the marker before sanitizing");

        byte[] cleaned = Sanitize(pdf);

        Assert.False(MarkerSurvives(cleaned, marker),
            "the JavaScript action survived inside the re-emitted object stream");
        // Potency control: the page the file is about is still there, so the sanitizer did not
        // merely empty the document.
        Assert.Equal(1, PageCount(cleaned));
    }

    /// <summary>
    /// The acceptance control: the very same object-stream layout with a benign document keeps its
    /// content, so "the marker is gone" is not satisfied by a sanitizer that drops everything.
    /// </summary>
    [Fact]
    public void AnObjectStreamDocumentWithoutAScriptKeepsItsPage()
    {
        byte[] pdf = ObjectStreamPdfWithJsAction(marker: null);
        byte[] cleaned = Sanitize(pdf);
        Assert.Equal(1, PageCount(cleaned));
    }

    /// <summary>
    /// A <c>/JS</c> authored as a text stream (§12.6.4.16). Stripping the <c>/JS</c> key alone left
    /// the stream in the file, every byte intact, because the object-for-object writer emitted it.
    /// </summary>
    [Fact]
    public void AJavaScriptTextStreamLeavesNoOrphan()
    {
        const string marker = "PWNED_JS_TEXT_STREAM";
        byte[] pdf = JsTextStreamPdf(marker);
        Assert.True(MarkerSurvives(pdf, marker), "fixture should carry the script before sanitizing");

        byte[] cleaned = Sanitize(pdf);

        Assert.False(MarkerSurvives(cleaned, marker), "the /JS text stream was emitted as an orphan");
        Assert.Equal(1, PageCount(cleaned));
    }

    /// <summary>
    /// An attachment reachable only through the catalog's <c>/AF</c> array (ISO 32000-2 associated
    /// files, the PDF/A-3 route). The <c>/Names /EmbeddedFiles</c> sweep never saw it.
    /// </summary>
    [Fact]
    public void AnAssociatedFileAttachmentIsNeutralized()
    {
        const string marker = "MZ_PWNED_AF_PAYLOAD";
        byte[] pdf = AssociatedFilePdf(marker);
        Assert.True(MarkerSurvives(pdf, marker), "fixture should carry the attachment before sanitizing");

        byte[] cleaned = Sanitize(pdf);

        Assert.False(MarkerSurvives(cleaned, marker), "the /AF attachment bytes survived");
        Assert.Equal(1, PageCount(cleaned));
    }

    /// <summary>
    /// The output trailer carries only the structural keys. A hybrid input's <c>/XRefStm</c> and the
    /// cross-reference-stream keys named offsets in a file that no longer exists.
    /// </summary>
    [Fact]
    public void TheOutputTrailerDropsCrossReferenceStreamKeys()
    {
        byte[] cleaned = Sanitize(ObjectStreamPdfWithJsAction(marker: null));

        string trailer = TrailerText(cleaned);
        Assert.Contains("/Root", trailer, StringComparison.Ordinal);
        Assert.DoesNotContain("/XRefStm", trailer, StringComparison.Ordinal);
        Assert.DoesNotContain("/W", trailer, StringComparison.Ordinal);
        Assert.DoesNotContain("/Prev", trailer, StringComparison.Ordinal);
    }

    // ================================================================= helpers

    private static byte[] Sanitize(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions());
        return output.ToArray();
    }

    private static int PageCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return doc.Pages.Count;
    }

    private static string Latin1(byte[] b) => Encoding.GetEncoding("iso-8859-1").GetString(b);

    private static string TrailerText(byte[] pdf)
    {
        string raw = Latin1(pdf);
        int t = raw.LastIndexOf("trailer", StringComparison.Ordinal);
        if (t < 0) return raw; // xref-stream layout — the whole tail counts
        int e = raw.IndexOf("startxref", t, StringComparison.Ordinal);
        return e < 0 ? raw.Substring(t) : raw.Substring(t, e - t);
    }

    /// <summary>True when <paramref name="marker"/> appears in the raw bytes or inside any deflate stream.</summary>
    private static bool MarkerSurvives(byte[] pdf, string marker)
    {
        string raw = Latin1(pdf);
        if (raw.Contains(marker, StringComparison.Ordinal)) return true;

        int p = 0;
        while ((p = raw.IndexOf("stream", p, StringComparison.Ordinal)) >= 0)
        {
            if (p >= 3 && raw.Substring(p - 3, 3) == "end") { p += 6; continue; }
            int start = p + 6;
            if (start < raw.Length && raw[start] == '\r') start++;
            if (start < raw.Length && raw[start] == '\n') start++;
            int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                if (Latin1(ms.ToArray()).Contains(marker, StringComparison.Ordinal)) return true;
            }
            catch { /* not a zlib stream */ }
            p = end + 9;
        }
        return false;
    }

    // ---- fixtures ----

    private static byte[] Assemble(string[] objs, string trailerExtra = "")
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new List<int>();
        for (int i = 0; i < objs.Length; i++)
        {
            offs.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R ").Append(trailerExtra)
          .Append(" >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A one-page document whose catalog, pages, page and (optionally) a JavaScript open-action live
    /// in an object stream. Built with a classic input first, then re-encoded with independently written object
    /// streams by compressing the member objects by hand so the test has no external dependency.
    /// </summary>
    private static byte[] ObjectStreamPdfWithJsAction(string marker)
    {
        // Member objects 1..N that go inside the object stream.
        var members = new List<(int num, string body)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R" + (marker == null ? "" : " /OpenAction 4 0 R") + " >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
        };
        if (marker != null)
            members.Add((4, "<< /S /JavaScript /JS (app.alert\\('" + marker + "'\\)) >>"));

        // Object-stream layout: a header of "num offset" pairs, then the concatenated bodies.
        var header = new StringBuilder();
        var bodies = new StringBuilder();
        var offsets = new List<int>();
        foreach (var (num, body) in members)
        {
            offsets.Add(bodies.Length);
            bodies.Append(body).Append(' ');
        }
        for (int i = 0; i < members.Count; i++)
            header.Append(members[i].num).Append(' ').Append(offsets[i]).Append(' ');

        string first = header.ToString();
        byte[] objStmData = Encoding.Latin1.GetBytes(first + bodies.ToString());
        byte[] compressed = Deflate(objStmData);

        // The physical file: object 10 is the /ObjStm, object 11 the /XRef stream.
        var sb = new StringBuilder("%PDF-1.7\n");
        int objStmOffset = sb.Length;
        sb.Append("10 0 obj\n<< /Type /ObjStm /N ").Append(members.Count).Append(" /First ").Append(first.Length)
          .Append(" /Length ").Append(compressed.Length).Append(" /Filter /FlateDecode >>\nstream\n");
        sb.Append(Encoding.Latin1.GetString(compressed));
        sb.Append("\nendstream\nendobj\n");

        int xrefOffset = sb.Length;
        // Build an xref stream (/Type /XRef) mapping objects 0..N and 10,11.
        // Entry format /W [1 2 1]: type, field2 (offset or obj-stream number), field3 (index or gen).
        var entries = new List<byte[]>();
        entries.Add(new byte[] { 0, 0, 0, 255 });                                   // 0: free
        for (int i = 0; i < members.Count; i++)
            entries.Add(new byte[] { 2, 0, 10, (byte)i });                          // in ObjStm 10, index i
        // objects 4..9 (if unused) are free; we only declared members 1..N (+maybe 4)
        // Pad object numbers up to 9 as free so /Index is simple.
        int maxMember = members[members.Count - 1].num;
        for (int n = maxMember + 1; n <= 9; n++)
            entries.Add(new byte[] { 0, 0, 0, 0 });
        entries.Add(new byte[] { 1, (byte)(objStmOffset >> 8), (byte)(objStmOffset & 0xFF), 0 }); // 10: ObjStm
        // object 11 (this xref stream) points at itself
        entries.Add(new byte[] { 1, (byte)(xrefOffset >> 8), (byte)(xrefOffset & 0xFF), 0 });     // 11: XRef

        var xrefBytes = new MemoryStream();
        foreach (var e in entries) xrefBytes.Write(e, 0, e.Length);
        byte[] xrefData = xrefBytes.ToArray();

        sb.Append("11 0 obj\n<< /Type /XRef /Size 12 /Root 1 0 R /W [1 2 1] /Index [0 12] /Length ")
          .Append(xrefData.Length).Append(" >>\nstream\n");
        sb.Append(Encoding.Latin1.GetString(xrefData));
        sb.Append("\nendstream\nendobj\n");
        sb.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] JsTextStreamPdf(string marker)
    {
        string script = "app.alert('" + marker + "')";
        return Assemble(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /S /JavaScript /JS 5 0 R >>",
            "<< /Length " + script.Length + " >>\nstream\n" + script + "\nendstream",
        });
    }

    private static byte[] AssociatedFilePdf(string marker)
    {
        return Assemble(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AF [4 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Filespec /F (evil.exe) /UF (evil.exe) /AFRelationship /Data /EF << /F 5 0 R >> >>",
            "<< /Type /EmbeddedFile /Length " + marker.Length + " >>\nstream\n" + marker + "\nendstream",
        });
    }
}
