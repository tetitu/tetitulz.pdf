// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Regression tests for the tagged-text, redaction, templating and AcroForm findings of the
/// 2026-08 assembly scan.
/// </summary>
public class FormsAndContentScanFindingTests
{
    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string dictExtras, string content)
        => $"{n} 0 obj\n<< /Length {Latin1(content).Length}{dictExtras} >>\nstream\n{content}\nendstream\nendobj\n";

    private static byte[] Assemble(IReadOnlyList<string> bodies, string extraTrailerKeys = "")
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
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1)
          .Append(" /Root 1 0 R").Append(extraTrailerKeys).Append(" >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    // ==================================================================
    //  PdfTaggedTextExtractor — MCIDs are scoped to a content stream
    // ==================================================================

    /// <summary>
    /// §14.7.4.2 scopes an /MCID to its content stream, and the structured extractor recurses into
    /// Form XObjects. Keying marked content on (page, mcid) alone merged the page's own
    /// <c>/H1 &lt;&lt;/MCID 0&gt;&gt;</c> with a form's <c>/P &lt;&lt;/MCID 0&gt;&gt;</c>, so both
    /// structure elements received the same concatenated string. Word and LibreOffice routinely put
    /// headers, footers and figures in Form XObjects.
    /// </summary>
    [Fact]
    public void FormXObjectMcid_DoesNotMergeWithThePagesOwnMcid()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                   "/Resources << /Font << /F1 6 0 R >> /XObject << /Fx 5 0 R >> >> >>"),
            // Page content: MCID 0 is the heading. Then it draws the form.
            StreamObj(4, "",
                "/H1 << /MCID 0 >> BDC BT /F1 24 Tf 72 700 Td (Title) Tj ET EMC\n/Fx Do\n"),
            // Form content: its OWN MCID 0 is a paragraph.
            StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 612 792]",
                "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 600 Td (Body) Tj ET EMC\n"),
            Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Obj(7, "<< /Type /StructTreeRoot /K [8 0 R] >>"),
            Obj(8, "<< /Type /StructElem /S /Document /P 7 0 R /K [9 0 R 10 0 R] >>"),
            // The H1 names a bare integer MCID: the page's own content stream.
            Obj(9, "<< /Type /StructElem /S /H1 /P 8 0 R /Pg 3 0 R /K 0 >>"),
            // The P names an /MCR whose /Stm is the form — the entry that disambiguates them.
            Obj(10, "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R " +
                    "/K << /Type /MCR /Pg 3 0 R /Stm 5 0 R /MCID 0 >> >>"),
        };
        byte[] pdf = Assemble(bodies);

        var blocks = PdfTaggedTextExtractor.Extract(new MemoryStream(pdf));

        var h1 = Assert.Single(blocks, b => b.StructureType == "H1");
        var p = Assert.Single(blocks, b => b.StructureType == "P");

        Assert.Equal("Title", h1.Text.Trim());
        Assert.Equal("Body", p.Text.Trim());
    }

    // ==================================================================
    //  PdfFullRewriter — a /Contents stream shared by two pages
    // ==================================================================

    /// <summary>
    /// Two pages may legally reference one <c>/Contents</c> object. Stripping it applied one page's
    /// edit to every page using it: redacting page 2 removed the same glyphs from page 3, which the
    /// caller never named, and painted page 2's cover box across it in page-2 coordinates.
    /// </summary>
    [Fact]
    public void Redaction_OnAPageSharingItsContentStream_LeavesTheOtherPageIntact()
    {
        byte[] pdf = TwoPagesSharingContent();

        // Premise: both pages really do render the same text from the same object.
        var before = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf));
        Assert.Contains("SECRET", string.Join(" ", before[0].Lines.Select(l => l.Text)),
            StringComparison.Ordinal);
        Assert.Contains("SECRET", string.Join(" ", before[1].Lines.Select(l => l.Text)),
            StringComparison.Ordinal);

        using var outMs = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), outMs, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 60f, Y = 690f, Width = 200f, Height = 30f }
        });

        var after = PdfStructuredTextExtractor.Extract(new MemoryStream(outMs.ToArray()));

        // Page 0 was named: its copy of the text is gone.
        Assert.DoesNotContain("SECRET", string.Join(" ", after[0].Lines.Select(l => l.Text)),
            StringComparison.Ordinal);
        // Page 1 was NOT named: it must be untouched.
        Assert.Contains("SECRET", string.Join(" ", after[1].Lines.Select(l => l.Text)),
            StringComparison.Ordinal);
    }

    /// <summary>Two pages whose /Contents is the same indirect object (object 5).</summary>
    private static byte[] TwoPagesSharingContent()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R " +
                   "/Resources << /Font << /F1 6 0 R >> >> >>"),
            Obj(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R " +
                   "/Resources << /Font << /F1 6 0 R >> >> >>"),
            StreamObj(5, "", "BT /F1 24 Tf 72 700 Td (SECRET) Tj ET\n"),
            Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };
        return Assemble(bodies);
    }

    // ==================================================================
    //  PdfFormFiller — /NeedAppearances is document-scoped
    // ==================================================================

    /// <summary>
    /// A form arriving with /NeedAppearances true does so because some field holds a /V with no
    /// /AP. Clearing it as a side effect of filling one unrelated field made every one of those
    /// other fields render blank, with nothing in FormFillResult mentioning them.
    /// </summary>
    [Fact]
    public void Fill_DoesNotLowerAnIncomingNeedAppearancesFlag()
    {
        byte[] pdf = TwoFieldForm(needAppearances: true);

        using var outMs = new MemoryStream();
        var result = PdfFormFiller.Fill(new MemoryStream(pdf), outMs,
            new Dictionary<string, string> { ["FieldA"] = "filled" }, new FormFillOptions());

        Assert.Contains("FieldA", result.FilledFields);
        Assert.True(result.NeedAppearances);

        using var doc = PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        var acro = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var flag = doc.ResolveObject(acro[PdfName.NeedAppearances]) as PdfBoolean;
        Assert.True(flag?.Value, "/NeedAppearances was lowered, so field B now renders blank.");
    }

    /// <summary>
    /// The flag is not simply pinned on: a form that did not ask for viewer-drawn appearances, and
    /// whose every filled value got one, still ends up false.
    /// </summary>
    [Fact]
    public void Fill_StillClearsNeedAppearancesWhenTheDocumentNeverAskedForIt()
    {
        byte[] pdf = TwoFieldForm(needAppearances: false);

        using var outMs = new MemoryStream();
        var result = PdfFormFiller.Fill(new MemoryStream(pdf), outMs,
            new Dictionary<string, string> { ["FieldA"] = "filled" }, new FormFillOptions());

        Assert.False(result.NeedAppearances);
    }

    /// <summary>An AcroForm with two text fields, A and B; B holds a /V and has no /AP.</summary>
    private static byte[] TwoFieldForm(bool needAppearances)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R] >>"),
            Obj(4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (FieldA) /Rect [50 700 250 730] " +
                   "/P 3 0 R /DA (/Helv 12 Tf 0 g) >>"),
            Obj(5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (FieldB) /V (already here) " +
                   "/Rect [50 650 250 680] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>"),
            Obj(6, "<< /Fields [4 0 R 5 0 R] /DA (/Helv 12 Tf 0 g) " +
                   "/DR << /Font << /Helv 7 0 R >> >> " +
                   $"/NeedAppearances {(needAppearances ? "true" : "false")} >>"),
            Obj(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Name /Helv >>"),
        };
        return Assemble(bodies);
    }

    // ==================================================================
    //  PdfFormFlattener — §12.5.5 algorithm A
    // ==================================================================

    /// <summary>
    /// The wrapper emitted the appearance's /Matrix as an explicit <c>cm</c> and then invoked the
    /// same stream with <c>Do</c>, which §8.10.1 makes apply the matrix again; and the bbox-to-rect
    /// scale ignored the /BBox origin, so a non-zero-origin box drew offset and clipped.
    /// </summary>
    [Fact]
    public void Flatten_MapsTheTransformedBBoxOntoTheWidgetRectWithASingleCm()
    {
        // /Rect [100 100 200 140]  ->  rw = 100, rh = 40
        // /BBox [10 10 110 30]     ->  100 x 20, identity /Matrix
        // sx = 1, sy = 2, translate = (100 - 1*10, 100 - 2*10) = (90, 80)
        byte[] pdf = WidgetWithAppearance("[10 10 110 30]", matrix: null);

        string wrapper = FlattenAndReadWrapper(pdf);

        Assert.Contains("1.0000 0 0 2.0000 90.0000 80.0000 cm", wrapper, StringComparison.Ordinal);
        // Exactly one cm: Do applies /Matrix itself, so a second one composed it twice.
        Assert.Equal(1, CountOccurrences(wrapper, " cm"));
    }

    /// <summary>
    /// A 90-degree-rotated appearance. The /Matrix maps /BBox [0 0 100 20] to x in [-20,0],
    /// y in [0,100], so the mapping cm must cancel that origin — and must not re-emit the matrix.
    /// </summary>
    [Fact]
    public void Flatten_RotatedAppearance_DoesNotApplyTheMatrixTwice()
    {
        byte[] pdf = WidgetWithAppearance("[0 0 100 20]", matrix: "[0 1 -1 0 0 0]");

        string wrapper = FlattenAndReadWrapper(pdf);

        // Transformed AABB: x in [-20, 0] (w 20), y in [0, 100] (h 100).
        // sx = 100/20 = 5, sy = 40/100 = 0.4; tx = 100 - 5*(-20) = 200, ty = 100 - 0.4*0 = 100.
        Assert.Contains("5.0000 0 0 0.4000 200.0000 100.0000 cm", wrapper, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(wrapper, " cm"));
        // The matrix itself must not appear in the wrapper any more.
        Assert.DoesNotContain("0.0000 1.0000 -1.0000 0.0000 0.0000 0.0000 cm", wrapper, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// Flattens the fixture and returns the decoded text of the wrapper content stream the
    /// flattener appended — the last /Contents part of page 0.
    /// </summary>
    private static string FlattenAndReadWrapper(byte[] pdf)
    {
        using var outMs = new MemoryStream();
        PdfFormFlattener.Flatten(new MemoryStream(pdf), outMs);

        using var doc = PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        var contents = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]);
        var arr = Assert.IsType<PdfArray>(contents);

        var last = doc.ResolveObject(arr[arr.Count - 1]) as PdfStream;
        Assert.NotNull(last);
        return Encoding.GetEncoding("iso-8859-1").GetString(last.GetDecodedData());
    }

    /// <summary>A single text widget at /Rect [100 100 200 140] with a /AP /N form.</summary>
    private static byte[] WidgetWithAppearance(string bbox, string matrix)
    {
        string matrixKey = matrix == null ? "" : $" /Matrix {matrix}";
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] " +
                   "/Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>"),
            Obj(4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (F) /V (x) " +
                   "/Rect [100 100 200 140] /P 3 0 R /AP << /N 5 0 R >> >>"),
            StreamObj(5,
                $" /Type /XObject /Subtype /Form /BBox {bbox}{matrixKey} " +
                "/Resources << /Font << /F1 8 0 R >> >>",
                "BT /F1 12 Tf 0 0 Td (x) Tj ET\n"),
            Obj(6, "<< /Fields [4 0 R] /DA (/Helv 12 Tf 0 g) >>"),
            StreamObj(7, "", "q Q\n"),
            Obj(8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };
        return Assemble(bodies);
    }
}
