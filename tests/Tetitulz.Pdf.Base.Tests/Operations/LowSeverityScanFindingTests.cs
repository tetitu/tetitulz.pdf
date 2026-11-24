// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Regression tests for the LOW-severity findings of the 2026-08 assembly scan.
/// </summary>
public class LowSeverityScanFindingTests
{
    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string dictExtras, string content)
        => $"{n} 0 obj\n<< /Length {Latin1(content).Length}{dictExtras} >>\nstream\n{content}\nendstream\nendobj\n";

    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        using var ms = new MemoryStream();
        var head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            var b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    // ==================================================================
    //  PdfLinkExtractor — the 50 000-annotation cap was silent
    // ==================================================================

    /// <summary>
    /// The cap bounds volume, and volume is the legitimate case: an index or a bulk-linked
    /// catalogue really can carry more links than this. A truncated result was byte-identical in
    /// shape to a complete one, so a link checker treated the dropped links as non-existent.
    /// </summary>
    [Fact]
    public void ExtractLinks_OversizedAnnotsArray_ReportsThatTheResultIsTruncated()
    {
        // 50 001 link annotations: one past the cap, which is the cheapest way to reach it.
        const int total = 50_001;
        var annotRefs = new StringBuilder();
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };

        for (int i = 0; i < total; i++) annotRefs.Append(4 + i).Append(" 0 R ");
        bodies.Add(Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [" +
                          annotRefs.ToString().TrimEnd() + "] >>"));
        for (int i = 0; i < total; i++)
            bodies.Add(Obj(4 + i,
                "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] " +
                "/A << /S /URI /URI (https://example.com/) >> >>"));

        byte[] pdf = Assemble(bodies);

        var links = PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf));

        Assert.Equal(50_000, links.Count);
        Assert.NotNull(PdfLinkExtractor.LastError);
        Assert.Contains("50000", PdfLinkExtractor.LastError.Replace(",", ""), StringComparison.Ordinal);
    }

    /// <summary>A document under the cap reports nothing — the diagnostic must not cry wolf.</summary>
    [Fact]
    public void ExtractLinks_OrdinaryDocument_ReportsNoError()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>"),
            Obj(4, "<< /Type /Annot /Subtype /Link /Rect [10 20 110 40] " +
                   "/A << /S /URI /URI (https://example.com/) >> >>"),
        };
        byte[] pdf = Assemble(bodies);

        var links = PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf));

        var only = Assert.Single(links);
        Assert.Equal("https://example.com/", only.Uri);
        Assert.Equal(10f, only.X, 2);
        Assert.Equal(20f, only.Y, 2);
        Assert.Null(PdfLinkExtractor.LastError);
    }

    // ==================================================================
    //  PdfBookmarksManager — sibling links after the O(n^2) removal
    // ==================================================================

    /// <summary>
    /// The sibling position is recorded during the grouping pass, not found by
    /// <c>siblings.IndexOf(i)</c> — a linear scan per item — so this pins that /Prev, /Next, /First
    /// and /Last chain in document order.
    /// </summary>
    [Fact]
    public void WriteBookmarks_LinksSiblingsInOrderWithPrevAndNext()
    {
        byte[] pdf = Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
        });

        var bookmarks = new List<PdfBookmark>
        {
            new PdfBookmark("One",   0, 0),
            new PdfBookmark("Two",   0, 0),
            new PdfBookmark("Three", 0, 0),
        };

        using var outMs = new MemoryStream();
        PdfBookmarksManager.WriteBookmarks(new MemoryStream(pdf), outMs, bookmarks);

        using var doc = PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        var outlines = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        Assert.NotNull(outlines);

        // Walk /First then /Next and assert the titles come back in order.
        var titles = new List<string>();
        var node = doc.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        var prevSeen = new List<bool>();
        while (node != null)
        {
            titles.Add((doc.ResolveObject(node[PdfName.Title]) as PdfString)?.GetText());
            prevSeen.Add(node[PdfName.Prev] != null);
            node = doc.ResolveObject(node[PdfName.Next]) as PdfDictionary;
        }

        Assert.Equal(new[] { "One", "Two", "Three" }, titles);
        // The first sibling has no /Prev; the others do. That is what the position index encodes.
        Assert.Equal(new[] { false, true, true }, prevSeen);
    }

    // ==================================================================
    //  PdfColorSpaceConverter — the GCR factor now travels with the call
    // ==================================================================

    /// <summary>
    /// GcrFactor is process-wide mutable state that changes pixel values. The per-call overload
    /// must actually drive the conversion, or it is decoration: a factor of 0 puts nothing in K,
    /// a factor of 1 puts the full grey component there.
    /// </summary>
    [Fact]
    public void ConvertRgbToCmyk_PerCallGcrFactor_ChangesTheKChannel()
    {
        byte[] pdf = MidGreyRgbImagePdf();

        byte[] noGcr = ConvertAndReadSamples(pdf, gcrFactor: 0.0);
        byte[] fullGcr = ConvertAndReadSamples(pdf, gcrFactor: 1.0);

        // K is the 4th component of the first pixel.
        byte kNone = noGcr[3];
        byte kFull = fullGcr[3];

        Assert.Equal(0, kNone);
        Assert.True(kFull > 0,
            $"full GCR should pull the grey component into K, but K was {kFull}.");
    }

    /// <summary>The per-call factor must not disturb the process-wide default for later callers.</summary>
    [Fact]
    public void ConvertRgbToCmyk_PerCallOverload_DoesNotMutateTheStaticDefault()
    {
#pragma warning disable CS0618 // the obsolete process-wide factor is what this test is about
        double before = PdfColorSpaceConverter.GcrFactor;

        ConvertAndReadSamples(MidGreyRgbImagePdf(), gcrFactor: 0.0);

        Assert.Equal(before, PdfColorSpaceConverter.GcrFactor);
#pragma warning restore CS0618
    }

    /// <summary>
    /// The <b>facade</b> overload must carry the factor through, not merely accept it.
    ///
    /// <para>Plan 11 §5d listed RGB→CMYK as unstarted work blocked on lut-based ICC. The conversion
    /// was in fact already shipping and already covered — what was missing was reachability: only the
    /// factor-less <c>PdfOperations.ConvertRgbToCmyk(Stream, Stream)</c> existed, while
    /// <see cref="PdfColorSpaceConverter"/>'s own doc comment names the factor overload as the one to
    /// prefer. A prepress caller could not set GCR without bypassing the facade.</para>
    ///
    /// <para>The failure this guards is a facade overload that takes a parameter and drops it on the
    /// floor — which compiles, returns a correct-looking count, and produces the default separation.
    /// Asserting on the K channel is what separates "carried through" from "accepted".</para>
    /// </summary>
    [Fact]
    public void FacadeConvertRgbToCmyk_CarriesTheGcrFactorThrough()
    {
        byte[] pdf = MidGreyRgbImagePdf();

        byte[] noGcr = FacadeConvertAndReadSamples(pdf, gcrFactor: 0.0);
        byte[] fullGcr = FacadeConvertAndReadSamples(pdf, gcrFactor: 1.0);

        Assert.Equal(0, noGcr[3]);
        Assert.True(fullGcr[3] > 0,
            $"the facade overload ignored its gcrFactor: K was {fullGcr[3]} at full GCR, so the " +
            "parameter is decoration and prepress callers still cannot set the separation.");
    }

    private static byte[] FacadeConvertAndReadSamples(byte[] pdf, double gcrFactor)
    {
        using var outMs = new MemoryStream();
        int converted = Tetitulz.Pdf.Base.PdfOperations.ConvertRgbToCmyk(
            new MemoryStream(pdf), outMs, gcrFactor);
        Assert.Equal(1, converted);

        using var doc = PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        var img = doc.GetObject(4) as PdfStream;
        Assert.NotNull(img);
        return img!.GetDecodedData();
    }

    private static byte[] ConvertAndReadSamples(byte[] pdf, double gcrFactor)
    {
        using var outMs = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToCmyk(
            new MemoryStream(pdf), outMs, gcrFactor);
        Assert.Equal(1, converted);

        using var doc = PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        var img = doc.GetObject(4) as PdfStream;
        Assert.NotNull(img);
        Assert.Equal("DeviceCMYK", (img[PdfName.ColorSpace] as PdfName)?.Value);
        return img.GetDecodedData();
    }

    /// <summary>A 1x1 mid-grey DeviceRGB image — grey is where GCR is visible.</summary>
    private static byte[] MidGreyRgbImagePdf()
    {
        string samples = "\x80\x80\x80";
        return Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Resources << /XObject << /Im0 4 0 R >> >> >>"),
            StreamObj(4,
                " /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 " +
                "/ColorSpace /DeviceRGB",
                samples),
        });
    }
}
