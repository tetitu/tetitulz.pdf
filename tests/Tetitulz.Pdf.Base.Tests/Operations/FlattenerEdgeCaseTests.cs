// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Decisions in <see cref="PdfFormFlattener"/> that a mutation sweep of <c>Base/Operations/Forms</c>
/// found no test could see.
/// </summary>
/// <remarks>
/// <para>Each was deleted or inverted and the whole Base suite stayed green (4469, 7 corpus skips).
/// The one that mattered most: <b>the page's own XObjects</b>. The flattener rebuilds the page's
/// <c>/XObject</c> resource to add the stamped appearances, and the loop copying the existing entries
/// into it could be removed without a failure — which would drop every image and form the page
/// already drew. No fixture had a page with an XObject of its own.</para>
///
/// <para>The rest are the geometry of an unusual <c>/Rect</c>, an <c>/AP</c> written as a reference,
/// a document with no form at all, and a page the flattener cannot replace.</para>
/// </remarks>
public class FlattenerEdgeCaseTests
{
    [Fact]
    public void ThePagesOwnXObjectsSurviveFlattening()
    {
        byte[] flattened = Flatten(FormPdf(pageResources: "<< /XObject << /Im0 8 0 R >> >>",
            extra: "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray "
                 + "/BitsPerComponent 8 /Length 1 >>\nstream\n\nendstream"));

        var xobjects = PageXObjects(flattened);
        Assert.Contains(xobjects, name => name == "Im0");
        Assert.Contains(xobjects, name => name.StartsWith("FlatXO", StringComparison.Ordinal));
    }

    [Fact]
    public void AWidgetWhoseAppearanceDictionaryIsIndirectIsStampedAndRemoved()
    {
        // §7.3.10 permits /AP 8 0 R. Unresolved, the widget was not stamped — but /AcroForm was still
        // removed, leaving an interactive widget with no form behind it.
        byte[] flattened = Flatten(FormPdf(ap: "8 0 R", extra: "<< /N 6 0 R >>"));

        Assert.Empty(PageAnnotations(flattened));
        Assert.Contains(PageXObjects(flattened), name => name.StartsWith("FlatXO", StringComparison.Ordinal));
    }

    [Fact]
    public void AWidgetWithAShortRectIsLeftInPlaceNotStampedAtTheOrigin()
    {
        // Two numbers are not a rectangle. Read past its end, the missing corners come back as zero
        // and the appearance is stamped from the page origin — somewhere the widget never was.
        byte[] flattened = Flatten(FormPdf(rect: "[20 20]"));

        Assert.Single(PageAnnotations(flattened));
        Assert.DoesNotContain(PageXObjects(flattened), name => name.StartsWith("FlatXO", StringComparison.Ordinal));
    }

    [Fact]
    public void AReversedRectIsStampedAtItsLowerLeftCornerWithAPositiveSize()
    {
        // §12.5.2: /Rect's corners may come in either order; readers normalise. Written upper-right
        // first, the stamp must still clip to (20, 20) 160 × 40 — not start at (180, 60) and extend
        // by a negative size.
        byte[] flattened = Flatten(FormPdf(rect: "[180 60 20 20]"));

        var clip = Regex.Match(StampWrapper(flattened), @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re W n");
        Assert.True(clip.Success, "no clip in the stamp's wrapper stream");
        Assert.Equal(new[] { 20.0, 20.0, 160.0, 40.0 },
            Enumerable.Range(1, 4).Select(i => double.Parse(clip.Groups[i].Value, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void FlatteningADocumentWithNoFormReturnsItByteForByte()
    {
        // Nothing to flatten is not a reason to write a revision.
        byte[] input = FormPdf(acroForm: null);

        Assert.Equal(input, Flatten(input));
    }

    [Fact]
    public void AWidgetOnAPageWrittenDirectlyInKidsIsReportedNotSkippedInSilence()
    {
        // §7.7.3.2 makes /Kids an array of indirect references, so a page written inline breaks the
        // rule — and an incremental update cannot replace an object with no number. The widget on it
        // cannot be stamped, and skipping the whole page without a word while still removing
        // /AcroForm is the one outcome a caller must be told about.
        int thread = Environment.CurrentManagedThreadId;
        var seen = new List<string>();
        void Handler(string detail)
        {
            if (Environment.CurrentManagedThreadId == thread) lock (seen) seen.Add(detail);
        }

        DocumentEditDiagnostics.OnEvent += Handler;
        try { Flatten(FormPdf(directPage: true)); }
        finally { DocumentEditDiagnostics.OnEvent -= Handler; }

        Assert.Contains(seen, d => d != null && d.Contains("written directly", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static byte[] Flatten(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFlattener.Flatten(input, output);
        return output.ToArray();
    }

    private static PdfDictionary FirstPage(PdfDocument doc) => doc.Pages[0].Dictionary;

    private static List<string> PageXObjects(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var resources = doc.ResolveObject(FirstPage(doc)[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
        return xobjects == null ? new List<string>() : xobjects.Select(kv => kv.Key.Value).ToList();
    }

    private static List<PdfObject> PageAnnotations(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var annots = doc.ResolveObject(FirstPage(doc)[PdfName.Annots]) as PdfArray;
        var result = new List<PdfObject>();
        if (annots != null) for (int i = 0; i < annots.Count; i++) result.Add(annots[i]);
        return result;
    }

    /// <summary>The last stream of the page's <c>/Contents</c> — the wrapper the flattener appended.</summary>
    private static string StampWrapper(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var contents = doc.ResolveObject(FirstPage(doc)[PdfName.Contents]) as PdfArray;
        Assert.NotNull(contents);
        var last = doc.ResolveObject(contents![contents.Count - 1]) as PdfStream;
        Assert.NotNull(last);
        return Encoding.Latin1.GetString(last!.GetDecodedData());
    }

    /// <summary>
    /// One text widget on one page, with an appearance that draws its value. Object 8 is free for
    /// <paramref name="extra"/>.
    /// </summary>
    private static byte[] FormPdf(
        string rect = "[20 20 180 60]",
        string ap = "<< /N 6 0 R >>",
        string pageResources = "<< >>",
        string extra = "null",
        string acroForm = "<< /Fields [5 0 R] >>",
        bool directPage = false)
    {
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                    + " /Resources " + pageResources + " /Annots [5 0 R] >>";
        const string appearance = "BT /Helv 12 Tf 2 12 Td (Hello) Tj ET";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R" + (acroForm == null ? "" : " /AcroForm " + acroForm) + " >>",
            "<< /Type /Pages /Kids [" + (directPage ? page : "3 0 R") + "] /Count 1 >>",
            directPage ? "null" : page,
            "<< /Length 7 >>\nstream\n0 0 m S\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (f1) /V (Hello) /Rect " + rect + " /AP " + ap + " >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 160 40] /Resources << /Font << /Helv 7 0 R >> >>"
                + " /Length " + appearance.Length + " >>\nstream\n" + appearance + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            extra);
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
