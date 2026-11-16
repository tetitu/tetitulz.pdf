// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A destination reached through a <b>chain</b> of indirect references is still recognised as
/// dangling when the page it names did not survive the split.
/// </summary>
/// <remarks>
/// <para>§7.3.10 lets an indirect object's body be itself an indirect reference —
/// <c>5 0 obj 6 0 R endobj</c> — and <c>PdfSplitter</c>'s <c>Deref</c> was a <b>single hop</b>. It
/// therefore handed <c>IsDanglingDestination</c> a <c>PdfReference</c> rather than the
/// <c>[null /Fit]</c> array behind it; the <c>resolved is PdfArray</c> test failed, the destination
/// was classified as live, and the annotation survived the split <b>pointing at a page that is not
/// in the output</b>.</para>
///
/// <para>The four equivalent helpers in <c>Plugins.Maintenance</c> all loop. Only the chain is
/// fixed here — this file's <c>Deref</c> keeps its "return what you were handed" contract on a
/// miss, because its callers were written against it and the shared loops return <c>null</c>.</para>
/// </remarks>
public class IndirectDestinationChainTests
{
    /// <summary>
    /// The link annotation on the surviving page points, through two hops, at a dropped page. Its
    /// <c>/Dest</c> must be removed.
    /// </summary>
    [Fact]
    public void ADoublyIndirectDanglingDestinationIsRemoved()
    {
        byte[] split = ExtractFirstPage(TwoPagePdfWithChainedDestination());

        Assert.DoesNotContain("/Dest", AnnotationDictionaryText(split), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The control.</b> The same document with the destination written directly — one hop — was
    /// already handled, so this pins that the case above is about the chain and not about
    /// destinations in general.
    /// </summary>
    [Fact]
    public void ASinglyIndirectDanglingDestinationIsRemovedToo()
    {
        byte[] split = ExtractFirstPage(TwoPagePdfWithChainedDestination(chained: false));

        Assert.DoesNotContain("/Dest", AnnotationDictionaryText(split), StringComparison.Ordinal);
    }

    /// <summary>
    /// The premise: the annotation really does carry a <c>/Dest</c> before the split, so the two
    /// cases above cannot pass on a fixture that never had one.
    /// </summary>
    [Fact]
    public void TheFixtureCarriesADestinationBeforeTheSplit()
    {
        Assert.Contains("/Dest", Encoding.Latin1.GetString(TwoPagePdfWithChainedDestination()),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private static byte[] ExtractFirstPage(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);
        return output.ToArray();
    }

    /// <summary>The output's annotation dictionary, as text.</summary>
    private static string AnnotationDictionaryText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        Assert.True(annots != null && annots.Count > 0, "the split lost the annotation entirely");

        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        Assert.True(annot != null, "the annotation did not resolve");

        var sb = new StringBuilder();
        foreach (var kvp in annot) sb.Append('/').Append(kvp.Key.Value).Append(' ');
        return sb.ToString();
    }

    /// <summary>
    /// Two pages; a link on page 1 whose <c>/Dest</c> names page 2. When
    /// <paramref name="chained"/>, the destination is reached through an extra indirect hop
    /// (<c>8 0 obj 9 0 R</c>), which is the shape a single-hop deref cannot follow.
    /// </summary>
    private static byte[] TwoPagePdfWithChainedDestination(bool chained = true)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        string destRef = chained ? "8 0 R" : "9 0 R";

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R "
          + "/Annots [7 0 R] /Resources << >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R "
          + "/Resources << >> >>\nendobj\n");
        Obj("5 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        Obj("6 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        Obj($"7 0 obj\n<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest {destRef} >>\nendobj\n");
        // 8 is the extra hop: an indirect object whose whole body is another reference.
        Obj("8 0 obj\n9 0 R\nendobj\n");
        // 9 is the destination array naming page 2 — the page the split drops.
        Obj("9 0 obj\n[4 0 R /Fit]\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
