// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §13.6.2 lets a 3D annotation's <c>/3DD</c> be a 3D reference dictionary (<c>/Type /3DRef</c>)
/// naming the stream, so several annotations share one. The lister follows it; one that names no
/// stream is listed with a reason, never as a measured stream of no bytes.
/// </summary>
public sealed class ThreeDReferenceDictionaryTests
{
    [Fact]
    public void AStreamNamedThroughA3DReferenceDictionary_IsListedAndExtracted()
    {
        byte[] pdf = Pdf("/3DD << /Type /3DRef /3DD 5 0 R >>");

        var listed = Pdf3DExtractor.List3DAnnotations(new MemoryStream(pdf));
        var info = Assert.Single(listed);
        Assert.Equal("U3D", info.StreamType);
        Assert.Equal(1000, info.DataLength);
        Assert.Null(info.DataLengthUnavailableReason);

        byte[] bytes = Pdf3DExtractor.Extract3DStream(new MemoryStream(pdf), 0);
        Assert.Equal(1000, bytes.Length);
    }

    /// <summary>The control: a direct stream reads as before.</summary>
    [Fact]
    public void ADirectStream_IsListedAndExtracted()
    {
        byte[] pdf = Pdf("/3DD 5 0 R");

        var info = Assert.Single(Pdf3DExtractor.List3DAnnotations(new MemoryStream(pdf)));
        Assert.Equal(1000, info.DataLength);
        Assert.Null(info.DataLengthUnavailableReason);
        Assert.Equal(1000, Pdf3DExtractor.Extract3DStream(new MemoryStream(pdf), 0).Length);
    }

    [Theory]
    [InlineData("/3DD << /Type /3DRef >>", "names no /3DD stream")]
    [InlineData("/3DD << /Type /Other >>", "neither a stream nor a 3D reference dictionary")]
    [InlineData("", "has no /3DD stream")]
    public void AnAnnotationWithNoStream_IsListedWithTheReason_NotAsAnEmptyStream(string entry, string reason)
    {
        byte[] pdf = Pdf(entry);

        var info = Assert.Single(Pdf3DExtractor.List3DAnnotations(new MemoryStream(pdf)));
        Assert.Equal(0, info.DataLength);
        Assert.NotNull(info.DataLengthUnavailableReason);
        Assert.Contains(reason, info.DataLengthUnavailableReason, System.StringComparison.Ordinal);

        Assert.Empty(Pdf3DExtractor.Extract3DStream(new MemoryStream(pdf), 0));
        Assert.Contains(reason, Pdf3DExtractor.LastError ?? "", System.StringComparison.Ordinal);
    }

    // One page with one 3D annotation carrying `entry`; object 5 is a 1000-byte /U3D stream.
    private static byte[] Pdf(string entry)
    {
        string hex = string.Concat(Enumerable.Repeat("00", 1000)) + ">";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "null",
            $"<< /Type /3D /Subtype /U3D /Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream",
            $"<< /Type /Annot /Subtype /3D /Rect [10 10 50 50] {entry} >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
