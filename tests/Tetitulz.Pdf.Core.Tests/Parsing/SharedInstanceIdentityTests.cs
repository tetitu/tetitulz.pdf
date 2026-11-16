// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The parser hands out one shared instance each for <c>true</c>, <c>false</c> and <c>null</c>, and
/// those instances belong to every document in the process — so a top-level
/// <c>4 0 obj true endobj</c> must not stamp <c>PdfBoolean.True</c> with object number 4.
/// </summary>
/// <remarks>
/// <para><b>Found by the encryption round.</b> <c>ParseIndirectObject</c> stamped whatever it parsed,
/// so after any document with a bare keyword object, <c>PdfBoolean.True.ObjectNumber</c> read as
/// that object's number for the rest of the process, in every document — and a reader keying a
/// value by the object it sits in, as the encryptor does, read noise. <c>ParserInlinedObjects</c>
/// refuses scalars for exactly this reason; this closes the source.</para>
///
/// <para>Each case first asserts the premise — that the object returned <i>is</i> the shared
/// instance — because an assertion on the instance's number proves nothing if the parser had
/// handed out a fresh one.</para>
/// </remarks>
public class SharedInstanceIdentityTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public void ATopLevelKeywordObject_LeavesTheSharedInstanceUnstamped(string keyword)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            keyword);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        PdfObject obj = doc.ResolveObject(new PdfReference(3, 0));

        Assert.Same(Shared(keyword), obj);
        Assert.Equal(0, obj.ObjectNumber);
        Assert.Equal(0, obj.GenerationNumber);
    }

    /// <summary>The same through an object stream, whose members are stamped by another site.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public void AKeywordObjectInAnObjectStream_LeavesTheSharedInstanceUnstamped(string keyword)
    {
        byte[] pdf = WithObjectStreamMember(keyword);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        PdfObject obj = doc.ResolveObject(new PdfReference(3, 0));

        Assert.Same(Shared(keyword), obj);
        Assert.Equal(0, obj.ObjectNumber);
    }

    /// <summary>The control: an object with an identity of its own is still stamped.</summary>
    [Fact]
    public void ATopLevelDictionary_IsStillStampedWithItsNumber()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Marker 3 >>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        PdfObject obj = doc.ResolveObject(new PdfReference(3, 0));

        Assert.IsType<PdfDictionary>(obj);
        Assert.Equal(3, obj.ObjectNumber);
    }

    // ------------------------------------------------------------------

    private static PdfObject Shared(string keyword) => keyword switch
    {
        "true" => PdfBoolean.True,
        "false" => PdfBoolean.False,
        _ => PdfNull.Instance,
    };

    /// <summary>Objects 1 and 2 top-level; object 3 is <paramref name="keyword"/> inside object stream 4, named by cross-reference stream 5.</summary>
    private static byte[] WithObjectStreamMember(string keyword)
    {
        var sb = new StringBuilder();
        var ms = new MemoryStream();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Ascii("%PDF-1.5\n");
        long o1 = ms.Position; Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = ms.Position; Ascii("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        const string Index = "3 0 ";
        string data = Index + keyword;
        long o4 = ms.Position;
        Ascii("4 0 obj\n<< /Type /ObjStm /N 1 /First " + Index.Length.ToString(CultureInfo.InvariantCulture) +
              " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + data + "\nendstream\nendobj\n");

        long xref = ms.Position;
        var rows = new (int num, byte type, long f2, int f3)[] { (0, 0, 0, 65535), (1, 1, o1, 0), (2, 1, o2, 0), (3, 2, 4, 0), (4, 1, o4, 0), (5, 1, xref, 0) };
        var raw = new byte[rows.Length * 7];
        var index = new StringBuilder();
        for (int i = 0; i < rows.Length; i++)
        {
            var (num, type, f2, f3) = rows[i];
            index.Append(num.ToString(CultureInfo.InvariantCulture)).Append(" 1 ");
            int at = i * 7;
            raw[at] = type;
            raw[at + 1] = (byte)(f2 >> 24); raw[at + 2] = (byte)(f2 >> 16); raw[at + 3] = (byte)(f2 >> 8); raw[at + 4] = (byte)f2;
            raw[at + 5] = (byte)(f3 >> 8); raw[at + 6] = (byte)f3;
        }
        Ascii("5 0 obj\n<< /Type /XRef /Size 6 /Root 1 0 R /W [1 4 2] /Index [" + index.ToString().TrimEnd() +
              "] /Length " + raw.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        ms.Write(raw, 0, raw.Length);
        Ascii("\nendstream\nendobj\n");
        Ascii("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
