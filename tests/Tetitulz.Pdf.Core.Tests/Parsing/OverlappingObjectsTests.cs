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
/// Objects whose bytes overlap other objects' are read once each, never once per object that
/// claims them. Top-level streams whose data all runs to one shared <c>endstream</c> (each header
/// inside the previous stream's data) are bounded by the raw stream bytes one parser reads
/// (<see cref="PdfParser.RawStreamBytesRead"/>); object-stream members whose offsets fall inside one
/// another (each at one more <c>(</c> of a nested string) are each parsed within their own span
/// (<see cref="PdfParser.MemberBytesParsed"/>). The counters are the evidence, never a clock.
/// </summary>
public sealed class OverlappingObjectsTests
{
    private const int Mebibyte = 1024 * 1024;

    [Fact]
    public void Adversarial_nested_streams_sharing_one_endstream_are_read_within_twice_the_file()
    {
        const int n = 100;
        byte[] file = NestedStreams(n, filler: 400_000);   // each stream copies ~400 kB: 40 MB unbounded
        var parser = new PdfParser(new MemoryStream(file));
        parser.ParseCrossReferences();

        int refused = 0;
        for (int i = 1; i <= n; i++)
        {
            PdfObject o = parser.GetObject(i);
            if (o is PdfNull)
            {
                refused++;
                Assert.Contains("overlap", PdfParser.LastObjectParseError);
            }
        }

        Assert.InRange(parser.RawStreamBytesRead, 16 * Mebibyte - 400_000, 16 * Mebibyte);
        Assert.InRange(refused, n / 2, n - 1);
    }

    /// <summary>The control: as many streams each in its own bytes are all read, each once.</summary>
    [Fact]
    public void Streams_each_in_their_own_bytes_are_all_read()
    {
        const int n = 100;
        byte[] file = SeparateStreams(n, size: 4_000);
        var parser = new PdfParser(new MemoryStream(file));
        parser.ParseCrossReferences();

        for (int i = 1; i <= n; i++)
            Assert.Equal(4_000, Assert.IsType<PdfStream>(parser.GetObject(i)).RawData.Length);
        Assert.Equal(n * 4_000L, parser.RawStreamBytesRead);
    }

    [Fact]
    public void Adversarial_object_stream_members_inside_one_another_are_each_parsed_within_their_own_bytes()
    {
        const int n = 500;
        byte[] file = ObjStmOverlap(n, payload: 100_000, out int bodyLength);   // each member the rest of the body: 50 MB unbounded
        var parser = new PdfParser(new MemoryStream(file));
        parser.ParseCrossReferences();

        for (int i = 1; i <= n; i++) parser.GetObject(i);

        Assert.InRange(parser.MemberBytesParsed, n, bodyLength);
    }

    /// <summary>The control: members that abut, as §7.5.7 lays them out, parse whole.</summary>
    [Fact]
    public void Object_stream_members_laid_out_one_after_another_parse_whole()
    {
        const int n = 50;
        byte[] file = ObjStmAbutting(n);
        var parser = new PdfParser(new MemoryStream(file));
        parser.ParseCrossReferences();

        for (int i = 1; i <= n; i++)
        {
            var d = Assert.IsType<PdfDictionary>(parser.GetObject(i));
            Assert.Equal(i, Assert.IsType<PdfNumber>(d[new PdfName("A")]).IntValue);
            Assert.Equal("x" + i, Assert.IsType<PdfString>(d[new PdfName("B")]).GetText());
        }
    }

    // ---------------------------------------------------------------- builders

    private static void Write(MemoryStream ms, string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s);
        ms.Write(b, 0, b.Length);
    }

    /// <summary>Objects 1..n, each a stream whose data runs to one shared <c>endstream</c> at the end, every /Length correct.</summary>
    private static byte[] NestedStreams(int n, int filler)
    {
        var ms = new MemoryStream();
        Write(ms, "%PDF-1.4\n");
        var offsets = new long[n + 1];
        var dataStarts = new long[n + 1];
        for (int i = 1; i <= n; i++)
        {
            offsets[i] = ms.Position;
            Write(ms, $"{i} 0 obj\n<</Length LLLLLLLLLL>>\nstream\n");
            dataStarts[i] = ms.Position;
        }
        ms.Write(new byte[filler], 0, filler);
        long end = ms.Position;
        Write(ms, "\nendstream\nendobj\n");
        byte[] bytes = ms.ToArray();
        byte[] placeholder = Encoding.ASCII.GetBytes("LLLLLLLLLL");
        for (int i = 1; i <= n; i++)
        {
            int at = IndexOf(bytes, placeholder, (int)offsets[i]);
            Encoding.ASCII.GetBytes((end - dataStarts[i]).ToString().PadLeft(10, '0')).CopyTo(bytes, at);
        }
        var output = new MemoryStream();
        output.Write(bytes, 0, bytes.Length);
        long xref = output.Position;
        Write(output, $"xref\n0 {n + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= n; i++) Write(output, $"{offsets[i]:D10} 00000 n \n");
        Write(output, $"trailer\n<</Size {n + 1}>>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>Objects 1..n, each a stream of <paramref name="size"/> zero bytes in its own bytes.</summary>
    private static byte[] SeparateStreams(int n, int size)
    {
        var ms = new MemoryStream();
        Write(ms, "%PDF-1.4\n");
        var offsets = new long[n + 1];
        for (int i = 1; i <= n; i++)
        {
            offsets[i] = ms.Position;
            Write(ms, $"{i} 0 obj\n<</Length {size}>>\nstream\n");
            ms.Write(new byte[size], 0, size);
            Write(ms, "\nendstream\nendobj\n");
        }
        long xref = ms.Position;
        Write(ms, $"xref\n0 {n + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= n; i++) Write(ms, $"{offsets[i]:D10} 00000 n \n");
        Write(ms, $"trailer\n<</Size {n + 1}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// Object n+1 an uncompressed ObjStm; members 1..n at offsets 0..n−1, each a <c>(</c> of one string
    /// nested n deep around <paramref name="payload"/> bytes, so member i, unbounded, parses to its match.
    /// </summary>
    private static byte[] ObjStmOverlap(int n, int payload, out int bodyLength)
    {
        var body = new StringBuilder();
        for (int i = 1; i <= n; i++) body.Append(i).Append(' ').Append(i - 1).Append(' ');
        body.Append('\n');
        int first = body.Length;
        body.Append('(', n).Append('x', payload).Append(')', n);
        bodyLength = body.Length;
        return WithObjectStream(n, first, body.ToString());
    }

    /// <summary>Object n+1 an uncompressed ObjStm whose members are <c>&lt;&lt;/A i /B (xi)&gt;&gt;</c>, one after another.</summary>
    private static byte[] ObjStmAbutting(int n)
    {
        var objects = new StringBuilder();
        var index = new StringBuilder();
        for (int i = 1; i <= n; i++)
        {
            index.Append(i).Append(' ').Append(objects.Length).Append(' ');
            objects.Append("<</A ").Append(i).Append(" /B (x").Append(i).Append(")>>\n");
        }
        index.Append('\n');
        return WithObjectStream(n, index.Length, index.ToString() + objects);
    }

    private static byte[] WithObjectStream(int n, int first, string objstm)
    {
        int stmNum = n + 1;
        var ms = new MemoryStream();
        Write(ms, "%PDF-1.5\n");
        long stmOff = ms.Position;
        Write(ms, $"{stmNum} 0 obj\n<</Type/ObjStm/N {n}/First {first}/Length {objstm.Length}>>\nstream\n{objstm}\nendstream\nendobj\n");
        long xrefOff = ms.Position;
        var rows = new List<byte>();
        void Row(int t, long f1, int f2)
        {
            rows.Add((byte)t);
            rows.Add((byte)(f1 >> 24)); rows.Add((byte)(f1 >> 16)); rows.Add((byte)(f1 >> 8)); rows.Add((byte)f1);
            rows.Add((byte)(f2 >> 8)); rows.Add((byte)f2);
        }
        Row(0, 0, 0xFFFF);
        for (int i = 1; i <= n; i++) Row(2, stmNum, i - 1);
        Row(1, stmOff, 0);
        Row(1, xrefOff, 0);
        Write(ms, $"{n + 2} 0 obj\n<</Type/XRef/Size {n + 3}/W [1 4 2]/Length {rows.Count}>>\nstream\n");
        ms.Write(rows.ToArray(), 0, rows.Count);
        Write(ms, $"\nendstream\nendobj\nstartxref\n{xrefOff}\n%%EOF\n");
        return ms.ToArray();
    }

    private static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        for (int i = from; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
