// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A stream the 3D and rich-media listers measure is decoded once a listing, however many
/// annotations name it. A decode refused at the ceiling is not cached by the stream, so each
/// annotation naming an asset past the 64 MiB ceiling paid the whole inflation again.
/// </summary>
public sealed class SharedMediaStreamListingTests
{
    [Fact]
    public void AdversarialStreamNamedByManyAnnotations_IsDecodedOnce_AndEachSaysWhy()
    {
        const int annotations = 300;
        DecodedLengths.Decodes = 0;

        var listed = Pdf3DExtractor.List3DAnnotations(new MemoryStream(Pdf(annotations, "/3D", "/3DD 5 0 R")), maxStreamBytes: 100);

        Assert.Equal(annotations, listed.Count);
        Assert.All(listed, i => Assert.NotNull(i.DataLengthUnavailableReason));
        Assert.Equal(1, DecodedLengths.Decodes);
    }

    [Fact]
    public void AdversarialAssetNamedByManyRichMediaAnnotations_IsDecodedOnce()
    {
        const int annotations = 300;
        DecodedLengths.Decodes = 0;

        var listed = PdfRichMediaExtractor.ListRichMedia(new MemoryStream(Pdf(annotations, "/RichMedia",
            "/RichMediaContent << /Assets << /Names [(a.mp4) 5 0 R] >> >>")), maxStreamBytes: 100);

        Assert.Equal(annotations, listed.Count);
        Assert.All(listed, i => Assert.NotNull(i.DataLengthUnavailableReason));
        Assert.Equal(1, DecodedLengths.Decodes);
    }

    /// <summary>The control: under the ceiling each annotation reports the stream's length.</summary>
    [Fact]
    public void UnderTheCeiling_EachAnnotationHasTheLength()
    {
        var listed = Pdf3DExtractor.List3DAnnotations(new MemoryStream(Pdf(3, "/3D", "/3DD 5 0 R")), maxStreamBytes: 10_000);

        Assert.All(listed, i => Assert.Equal(1000, i.DataLength));
        Assert.All(listed, i => Assert.Null(i.DataLengthUnavailableReason));
    }

    // One page whose /Annots lists `count` annotations of `subtype`, each naming stream 5 — 1000
    // decoded bytes — through `entry`.
    private static byte[] Pdf(int count, string subtype, string entry)
    {
        string hex = string.Concat(Enumerable.Repeat("00", 1000)) + ">";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [" +
            string.Join(" ", Enumerable.Range(0, count).Select(i => (6 + i).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "] >>",
            "null",
            $"<< /Type /3D /Subtype /U3D /Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream",
        };
        for (int i = 0; i < count; i++)
            bodies.Add($"<< /Type /Annot /Subtype {subtype} /Rect [10 10 50 50] {entry} >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
