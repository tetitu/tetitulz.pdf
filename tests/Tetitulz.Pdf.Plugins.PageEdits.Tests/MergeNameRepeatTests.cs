// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The merger's renaming of colliding names spends one search a name a source holds, and drops
/// nothing. A key or a field a source repeats is one entry, rather than each repeat searching the
/// suffixes it had itself minted, a thousand attempts a copy; and a key whose thousand suffixes are
/// all taken is not dropped, which would leave its own document's links naming it — so they would
/// open the earlier document's entry — under a warning the next line overwrites.
/// </summary>
public sealed class MergeNameRepeatTests
{
    [Fact]
    public void AdversarialKeyRepeatedBySource_IsOneEntry_AndSaid()
    {
        const int copies = 3000;
        var leaf = new StringBuilder("[");
        for (int i = 0; i < copies; i++) leaf.Append("(a) [3 0 R /Fit] ");
        leaf.Append(']');

        using var merged = Merge(WithDests("[(a) [3 0 R /Fit]]"), WithDests(leaf.ToString()));

        var keys = DestKeys(merged);
        Assert.Equal(new[] { "a", "a.1" }, keys);
        Assert.Contains((copies - 1) + " name-tree key(s) a source repeats", PdfMerger.LastNameTreeWarning ?? "",
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The first source holds <c>K</c> and all thousand of the second source's suffixes for it; the
    /// second source's <c>K</c> is suffixed past them, and its bookmark follows it to its own page.
    /// </summary>
    [Fact]
    public void AKeyWhoseBoundedSuffixesAreAllTaken_IsStillSuffixed_AndItsBookmarkFollows()
    {
        var first = new StringBuilder("[(K) [3 0 R /Fit] (K.1) [3 0 R /Fit] ");
        for (int attempt = 2; attempt <= 1000; attempt++) first.Append("(K.1.").Append(attempt).Append(") [3 0 R /Fit] ");
        first.Append(']');

        using var merged = Merge(WithDests(first.ToString()), WithDests("[(K) [3 0 R /Fit]]", outlineDest: "K"));

        Assert.Contains("K.1.1001", DestKeys(merged));
        var outlines = (PdfDictionary)merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]);
        var item = (PdfDictionary)merged.ResolveObject(outlines[PdfName.First]);
        Assert.Equal("K.1.1001", Encoding.ASCII.GetString(((PdfString)item[PdfName.Dest]).RawBytes));
        Assert.Contains("collided", PdfMerger.LastNameTreeWarning ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A field the second source's <c>/Fields</c> lists three thousand times is one field of the
    /// merged form, renamed once.
    /// </summary>
    [Fact]
    public void AdversarialFieldListedRepeatedly_IsOneField_RenamedOnce()
    {
        var refs = string.Concat(Enumerable.Repeat("4 0 R ", 3000));
        using var merged = Merge(WithField("[4 0 R]"), WithField("[" + refs + "]"));

        var form = (PdfDictionary)merged.ResolveObject(merged.Catalog.Dictionary[PdfName.AcroForm]);
        var fields = (PdfArray)merged.ResolveObject(form[PdfName.Fields]);
        Assert.Equal(2, fields.Count);
        var names = fields.Select(f => ((PdfString)((PdfDictionary)merged.ResolveObject(f))[PdfName.T]).GetText()).ToList();
        Assert.Equal(new[] { "Name", "Name.1" }, names);
    }

    // ----------------------------------------------------------------

    private static List<string> DestKeys(PdfDocument doc)
    {
        var names = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]);
        var dests = (PdfDictionary)doc.ResolveObject(names[PdfName.Dests]);
        var pairs = (PdfArray)doc.ResolveObject(dests[PdfName.Names]);
        var keys = new List<string>();
        for (int i = 0; i < pairs.Count; i += 2) keys.Add(Encoding.ASCII.GetString(((PdfString)pairs[i]).RawBytes));
        return keys;
    }

    // One page; its /Dests leaf is `leaf`, and an outline item naming `outlineDest` when given.
    private static byte[] WithDests(string leaf, string outlineDest = null)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 4 0 R >>" + (outlineDest != null ? " /Outlines 5 0 R" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Names " + leaf + " >>",
        };
        if (outlineDest != null)
        {
            bodies.Add("<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>");
            bodies.Add($"<< /Title (Go) /Parent 5 0 R /Dest ({outlineDest}) >>");
        }
        return Assemble(bodies);
    }

    // One page with one text field, /T (Name), object 4; the form's /Fields is `fields`.
    private static byte[] WithField(string fields) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields " + fields + " >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [10 10 60 30] /P 3 0 R >>",
    });

    private static PdfDocument Merge(byte[] a, byte[] b)
    {
        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(a), new MemoryStream(b));
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
