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
/// A source document's <c>/Outlines /Count</c> is a number the file chooses, and merge used to
/// convert it with <c>Math.Abs((long)cn.DoubleValue)</c>.
///
/// <para>Both halves of that expression are unsafe on hostile input: a <c>double</c> outside
/// <c>long</c>'s range has an <b>unspecified</b> conversion result, which on x64 is
/// <c>long.MinValue</c>, and <c>Math.Abs(long.MinValue)</c> raises
/// <see cref="System.OverflowException"/>. One crafted number in one merged document therefore
/// aborted the whole merge with a message that named nothing about the input — a denial of service
/// on a merge service that accepts uploads.</para>
///
/// <para>The guard's own evidence is asserted, not a wall clock: the merge completes AND the
/// emitted <c>/Count</c> is a finite value inside <c>int</c>'s range.</para>
/// </summary>
public class OutlineCountOverflowTests
{
    /// <summary>long.MinValue written out, the value whose Math.Abs throws.</summary>
    private const string LongMinValue = "-9223372036854775808";

    [Theory]
    [InlineData(LongMinValue)]                     // integer path — PdfToken.LongValue
    [InlineData("-9223372036854775809.0")]         // real path — PdfToken.DoubleValue
    public void Merge_WithAnOverflowingOutlineCount_CompletesAndEmitsAClampedCount(string hostileCount)
    {
        byte[] hostile = OnePagePdfWithOutline(hostileCount);
        byte[] plain   = OnePagePdfWithOutline("1");

        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(hostile), new MemoryStream(plain));

        using var merged = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        Assert.NotNull(outlines);

        double count = ((PdfNumber)merged.ResolveObject(outlines![PdfName.Count])!).DoubleValue;
        Assert.False(double.IsNaN(count) || double.IsInfinity(count));
        // Exactly the clamp. Unclamped, the hostile count wraps negative and the writer falls back
        // to the number of top-level items, which is also "in range" — a range assertion cannot
        // tell the two apart.
        Assert.Equal((double)int.MaxValue, count);

        // The clamp must not have eaten the outline itself: both bookmarks are still chained.
        Assert.NotNull(outlines[PdfName.First]);
        Assert.NotNull(outlines[PdfName.Last]);
    }

    /// <summary>
    /// The control: an ordinary pair of counts still sums, so the clamp above cannot pass by the
    /// merger having started to discard every <c>/Count</c>.
    /// </summary>
    [Fact]
    public void Merge_WithOrdinaryOutlineCounts_SumsThem()
    {
        byte[] a = OnePagePdfWithOutline("1");
        byte[] b = OnePagePdfWithOutline("1");

        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(a), new MemoryStream(b));

        using var merged = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var outlines = (PdfDictionary)merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines])!;
        Assert.Equal(2d, ((PdfNumber)merged.ResolveObject(outlines[PdfName.Count])!).DoubleValue);
    }

    // One page, one top-level bookmark, and an /Outlines root whose /Count is written verbatim.
    private static byte[] OnePagePdfWithOutline(string countLiteral)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }
        const string cs = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");
        Obj($"5 0 obj\n<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count {countLiteral} >>\nendobj\n");
        Obj("6 0 obj\n<< /Title (Bookmark) /Parent 5 0 R /Dest [3 0 R /Fit] >>\nendobj\n");

        int size = offsets.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
