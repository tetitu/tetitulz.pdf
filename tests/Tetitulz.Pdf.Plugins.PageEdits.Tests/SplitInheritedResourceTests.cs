// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A page that inherits <c>/Resources</c> from a <c>/Pages</c> ancestor (§7.7.3.4) still names
/// resolvable fonts and XObjects after extraction.
///
/// <para><b>Why this exists.</b> The splitter collected only the objects reachable from the page
/// dictionary, and that walk deliberately skips <c>/Parent</c> — so the font objects an inherited
/// <c>/Resources</c> names were never collected and never landed in the object map.
/// <c>MaterializeInheritedPageAttributes</c> then deep-cloned that resources dictionary, and
/// <c>PdfObjectHelper.DeepClone</c> maps an unmapped reference to <c>PdfNull</c>: the extracted page
/// carried the literal <c>/Resources &lt;&lt; /Font &lt;&lt; /F1 null &gt;&gt; &gt;&gt;</c> and every
/// <c>Tj</c> on it drew nothing. Producers that put shared resources on the root <c>/Pages</c> node
/// are common, so this was "the extracted page renders blank" on ordinary files.</para>
///
/// <para>The assertion is on the resolved font dictionary's <c>/BaseFont</c>, not on
/// <c>/Resources</c> merely being non-null — the broken output had a perfectly non-null
/// <c>/Resources</c>.</para>
/// </summary>
public class SplitInheritedResourceTests
{
    [Fact]
    public void SplitRange_WithResourcesInheritedFromThePagesNode_KeepsTheFontResolvable()
    {
        using var input  = new MemoryStream(TwoPagePdfWithInheritedResources());
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 1, 1);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Single(doc.Pages);

        var resources = doc.Pages[0].Resources;
        Assert.NotNull(resources);

        var fonts = doc.ResolveObject(resources![PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);

        var font = doc.ResolveObject(fonts![new PdfName("F1")]) as PdfDictionary;
        Assert.NotNull(font);
        Assert.Equal("Helvetica", (doc.ResolveObject(font![PdfName.BaseFont]) as PdfName)?.Value);
    }

    /// <summary>
    /// The inherited <c>/XObject</c> is the same defect wearing a different key, and it is the one
    /// that makes images vanish rather than text.
    /// </summary>
    [Fact]
    public void SplitRange_WithAnInheritedXObject_KeepsItResolvable()
    {
        using var input  = new MemoryStream(TwoPagePdfWithInheritedResources());
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 2, 2);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var resources = doc.Pages[0].Resources;
        var xobjects  = doc.ResolveObject(resources![PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);

        var form = doc.ResolveObject(xobjects![new PdfName("Fm0")]) as PdfStream;
        Assert.NotNull(form);
        Assert.Equal("Form", (doc.ResolveObject(form![PdfName.Subtype]) as PdfName)?.Value);
    }

    // /Resources lives on the root /Pages node only; neither page dictionary defines its own.
    private static byte[] TwoPagePdfWithInheritedResources()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }
        // The page draws its form: an extract carries the resource entries its pages draw.
        const string cs   = "BT /F1 12 Tf 72 700 Td (hi) Tj ET /Fm0 Do";
        const string form = "1 0 0 RG 0 0 m 10 10 l S";

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 /Resources 7 0 R >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj("7 0 obj\n<< /Font << /F1 5 0 R >> /XObject << /Fm0 8 0 R >> >>\nendobj\n");
        Obj($"8 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length {form.Length} >>\n"
            + $"stream\n{form}\nendstream\nendobj\n");

        int size = offsets.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
