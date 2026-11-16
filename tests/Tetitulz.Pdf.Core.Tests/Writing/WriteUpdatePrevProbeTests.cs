// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// The six-argument <see cref="IncrementalUpdateWriter.WriteUpdate(Stream, Stream, List{IndirectObject}, PdfDictionary, long, int, IPdfObjectEncryptor)"/>
/// probes the offset it is handed for a section. Where none is, it writes a complete standalone table —
/// and wrote a <c>/Prev</c> to that offset beside it, a chain to bytes that are not a section. It writes
/// <c>/Prev</c> only where it chained; the real section's offset still chains (the control).
/// </summary>
public sealed class WriteUpdatePrevProbeTests
{
    private static (byte[] Pdf, int Xref, int Obj1) Original()
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int x = sb.Length;
        sb.Append("xref\n0 3\n0000000000 65535 f \n").Append(o1.ToString("D10")).Append(" 00000 n \n")
          .Append(o2.ToString("D10")).Append(" 00000 n \ntrailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n")
          .Append(x).Append("\n%%EOF\n");
        return (Encoding.Latin1.GetBytes(sb.ToString()), x, o1);
    }

    private static string Appended(byte[] original, long previous)
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(3));
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        var info = new PdfDictionary();
        info.Set(new PdfName("Title"), new PdfString("x"));
        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(new MemoryStream(original), output,
            new List<IndirectObject> { new IndirectObject(3, 0, info) }, trailer, previous, 4);
        return Encoding.Latin1.GetString(output.ToArray())[original.Length..];
    }

    [Fact]
    public void AnOffsetHoldingNoSection_GetsNoPrev_AndTheRealSectionDoes()
    {
        var (pdf, xref, obj1) = Original();

        string unchained = Appended(pdf, obj1);   // in range, but an object, not a section
        Assert.DoesNotMatch(new Regex(@"/Prev\b"), unchained);

        string chained = Appended(pdf, xref);
        Assert.Matches(new Regex(@"/Prev " + xref + @"\b"), chained);
    }
}
