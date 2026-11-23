// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// A cross-reference stream's fields may be up to eight bytes wide (<c>/W</c>) and are judged whole:
/// narrowed to an <c>int</c> first, a type of 2^32 + 1 read as type 1 and an object-stream number of
/// 2^32 + 5 as stream 5. §7.5.8.3 reads any type but 0 to 2 as a reference to the null object.
/// </summary>
public sealed class XrefStreamWideFieldTests
{
    [Fact]
    public void Adversarial_type_past_an_int_is_a_reference_to_the_null_object()
    {
        PdfParser parser = Parser(type1: 0x0000_0001_0000_0001L, out _);

        Assert.IsType<PdfNull>(parser.GetObject(1));
    }

    /// <summary>The control: the same entry with type 1 names the object.</summary>
    [Fact]
    public void Type_one_names_the_object()
    {
        PdfParser parser = Parser(type1: 1, out _);

        var dict = Assert.IsType<PdfDictionary>(parser.GetObject(1));
        Assert.Equal(7, dict.GetInteger(new PdfName("Marker")));
    }

    [Fact]
    public void Adversarial_object_stream_number_past_an_int_is_dropped_and_said()
    {
        PdfParser parser = Parser(type1: 2, out _, field1Override: 0x0000_0001_0000_0005L);

        Assert.IsType<PdfNull>(parser.GetObject(1));
        Assert.Contains("past any object number or index", PdfParser.LastXrefSectionError);
    }

    /// <summary>
    /// Object 1 a dictionary at a known offset; a cross-reference stream (object 2) with /W [8 8 2] whose
    /// entry for object 1 has type <paramref name="type1"/> and field 1 the object's offset, or
    /// <paramref name="field1Override"/>.
    /// </summary>
    private static PdfParser Parser(long type1, out byte[] file, long? field1Override = null)
    {
        var ms = new MemoryStream();
        void W(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.5\n");
        long obj1 = ms.Position;
        W("1 0 obj\n<< /Marker 7 >>\nendobj\n");
        long xref = ms.Position;

        var rows = new List<byte>();
        void Field(long v, int width) { for (int k = width - 1; k >= 0; k--) rows.Add((byte)(v >> (8 * k))); }
        void Row(long t, long f1, long f2) { Field(t, 8); Field(f1, 8); Field(f2, 2); }
        Row(0, 0, 0xFFFF);
        Row(type1, field1Override ?? obj1, 0);
        Row(1, xref, 0);

        W($"2 0 obj\n<< /Type /XRef /Size 3 /W [8 8 2] /Root 1 0 R /Length {rows.Count} >>\nstream\n");
        ms.Write(rows.ToArray(), 0, rows.Count);
        W($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        file = ms.ToArray();

        var parser = new PdfParser(new MemoryStream(file));
        parser.ParseCrossReferences();
        return parser;
    }
}
