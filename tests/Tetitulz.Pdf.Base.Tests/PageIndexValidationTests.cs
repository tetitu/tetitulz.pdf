// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Every page-editing entry point must refuse a page index the document does not have.
///
/// <para><b>Nothing asserted this, anywhere.</b> The guard existed three times — byte-identical,
/// down to the interpolated message — in <c>PdfPageContentAppender</c>, <c>PdfPageManipulator</c>
/// and, inlined rather than extracted, <c>PdfAnnotationWriter</c>. Replacing its condition with
/// <c>false</c> left the entire Base suite green, so merging the three into one shared helper would
/// have been a deletion wearing a merge's clothes: <i>a merge whose survivor no test reaches</i>.
/// These tests are what make the merge safe rather than merely tidy.</para>
///
/// <para><b>The ParamName is asserted, not just the exception type.</b> A caller that catches
/// <see cref="ArgumentOutOfRangeException"/> and branches on
/// <see cref="ArgumentException.ParamName"/> is relying on the literal <c>"pageIndex"</c>;
/// <c>nameof</c> resolved to it in all three copies and resolves to it in the shared helper, so the
/// move is invisible to such a caller — which is a claim, and this is the claim's test.</para>
/// </summary>
public class PageIndexValidationTests
{
    /// <summary>A one-page document, so index 0 is valid and 1 is one past the end.</summary>
    private const int PageCount = 1;

    public static IEnumerable<object[]> OutOfRangeIndices => new[]
    {
        new object[] { -1 },          // below
        new object[] { PageCount },   // one past the end — the off-by-one a `>` instead of `>=` admits
        new object[] { 9999 },
    };

    [Theory]
    [MemberData(nameof(OutOfRangeIndices))]
    public void AppendContentRefusesAPageTheDocumentDoesNotHave(int pageIndex)
    {
        var ex = Record.Exception(() => PdfPageContentAppender.AppendContent(
            new MemoryStream(OnePagePdf()), new MemoryStream(), pageIndex, b => { }));

        AssertRefused(ex);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeIndices))]
    public void RotatePageRefusesAPageTheDocumentDoesNotHave(int pageIndex)
    {
        var ex = Record.Exception(() => PdfPageManipulator.RotatePage(
            new MemoryStream(OnePagePdf()), new MemoryStream(), pageIndex, 90));

        AssertRefused(ex);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeIndices))]
    public void CropPageRefusesAPageTheDocumentDoesNotHave(int pageIndex)
    {
        var ex = Record.Exception(() => PdfPageManipulator.CropPage(
            new MemoryStream(OnePagePdf()), new MemoryStream(), pageIndex, 0, 0, 100, 100));

        AssertRefused(ex);
    }

    [Theory]
    [MemberData(nameof(OutOfRangeIndices))]
    public void AddUriLinkRefusesAPageTheDocumentDoesNotHave(int pageIndex)
    {
        var ex = Record.Exception(() => PdfAnnotationWriter.AddUriLink(
            new MemoryStream(OnePagePdf()), new MemoryStream(), pageIndex,
            0, 0, 10, 10, "https://example.invalid/"));

        AssertRefused(ex);
    }

    /// <summary>
    /// The control every case above needs: index 0 is a real page, so these entry points are
    /// reachable and the refusals are refusals rather than some earlier failure.
    /// </summary>
    /// <remarks>
    /// It asserts the ROTATION, not merely that nothing was thrown. "It did not throw" is
    /// satisfied by an entry point that quietly did nothing, and then the four refusal theories
    /// above would be passing without evidence that the valid path ever runs. (The repo's own
    /// unfalsifiable-assertion guard caught the first version of this, which was exactly that.)
    /// </remarks>
    [Fact]
    public void TheValidPageIndexIsAcceptedAndTheEditIsApplied()
    {
        using var output = new MemoryStream();
        PdfPageManipulator.RotatePage(new MemoryStream(OnePagePdf()), output, 0, 90);

        output.Position = 0;
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(output);
        var rotate = doc.Pages[0].Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.Rotate]
                     as Tetitulz.Pdf.Core.Objects.PdfNumber;

        Assert.NotNull(rotate);
        Assert.Equal(90, (int)rotate!.LongValue);
    }

    private static void AssertRefused(Exception ex)
    {
        var range = Assert.IsType<ArgumentOutOfRangeException>(ex);
        Assert.Equal("pageIndex", range.ParamName);
    }

    private static byte[] OnePagePdf()
    {
        const string content = "0 0 1 rg 10 10 50 50 re f";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        var acc = new List<byte>();
        var off = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        Emit("%PDF-1.4\n");
        for (int i = 0; i < objs.Length; i++) { off.Add(acc.Count); Emit($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = acc.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in off) sb.Append($"{o:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        Emit(sb.ToString());
        return acc.ToArray();
    }
}
