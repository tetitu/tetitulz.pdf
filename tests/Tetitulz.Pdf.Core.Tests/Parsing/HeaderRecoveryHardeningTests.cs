// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The header recovery's bounds: a header of
/// another generation is another object; a header inside a stream's data is not this file's object;
/// a recovery is reported and fills the table's gaps; the object fetch is bounded in depth and does
/// not parse a failing object once per reference.
/// </summary>
public class HeaderRecoveryHardeningTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>";
    private const string Benign = "the-content-the-table-meant";
    private const string Decoy = "the-attachments-own-object-four";

    /// <summary>Object 5's entry (generation 1) is misaimed; the file holds a <c>5 0 obj</c>. It is another object, so 5 is refused; with a <c>5 1 obj</c> header it is read.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void AHeaderOfAnotherGeneration_IsNotTheObject_WhereTheSameGenerationIs(int headerGeneration, bool recovered)
    {
        var b = new FileBuilder();
        b.Obj(1, Catalog); b.Obj(2, Pages); b.Obj(3, Page); b.Stream(4, Benign);
        b.Raw($"5 {headerGeneration} obj\n<< /Marker 5 >>\nendobj\n", 5);
        b.Xref(new Dictionary<int, (long Offset, int Gen)> { [1] = (b.Offsets[1], 0), [2] = (b.Offsets[2], 0), [3] = (b.Offsets[3], 0), [4] = (b.Offsets[4], 0), [5] = (b.Offsets[3], 1) }, size: 6);

        PdfParser parser = Parse(b.ToArray());
        PdfObject five = parser.GetObject(5);

        if (recovered) Assert.Equal(5, Assert.IsType<PdfDictionary>(five).GetInteger(new PdfName("Marker")));
        else
        {
            Assert.IsType<PdfNull>(five);
            Assert.Contains("holds obj 3", PdfParser.LastObjectParseError, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Object 4's entry is misaimed. Its own header comes first; later, object 7 — an uncompressed
    /// embedded file — holds a <c>4 0 obj</c> inside its stream data. The last header in the file won,
    /// so the page drew the attachment's content stream; the data is stepped over now.
    /// </summary>
    [Fact]
    public void AHeaderInsideAStreamsData_IsNotTheObject()
    {
        var b = new FileBuilder();
        b.Obj(1, Catalog); b.Obj(2, Pages); b.Obj(3, Page); b.Stream(4, Benign);
        string embedded = $"%PDF-1.4\n4 0 obj\n<< /Length {Decoy.Length} >>\nstream\n{Decoy}\nendstream\nendobj\n";
        b.Stream(7, embedded);
        b.Xref(new Dictionary<int, (long Offset, int Gen)> { [1] = (b.Offsets[1], 0), [2] = (b.Offsets[2], 0), [3] = (b.Offsets[3], 0), [4] = (b.Offsets[3], 0), [7] = (b.Offsets[7], 0) }, size: 8);

        PdfParser parser = Parse(b.ToArray());
        var content = Assert.IsType<PdfStream>(parser.GetObject(4));

        Assert.Equal(Benign, Encoding.ASCII.GetString(content.GetDecodedData()));
    }

    /// <summary>A recovery says which object, why and from where, and is counted; the next clean read leaves the slot null.</summary>
    [Fact]
    public void ARecovery_IsReported_AndCounted()
    {
        var b = new FileBuilder();
        b.Obj(1, Catalog); b.Obj(2, Pages); b.Obj(3, Page); b.Stream(4, Benign);
        b.Xref(new Dictionary<int, (long Offset, int Gen)> { [1] = (b.Offsets[1], 0), [2] = (b.Offsets[2], 0), [3] = (b.Offsets[3], 0), [4] = (b.Offsets[3], 0) }, size: 5);

        PdfParser parser = Parse(b.ToArray());
        Assert.IsType<PdfStream>(parser.GetObject(4));

        Assert.Null(PdfParser.LastObjectParseError);
        Assert.Contains("obj 4", PdfParser.LastObjectHeaderRecovery, StringComparison.Ordinal);
        Assert.Contains("read from its own header", PdfParser.LastObjectHeaderRecovery, StringComparison.Ordinal);
        Assert.Equal(1, parser.HeaderRecoveries);

        Assert.IsType<PdfDictionary>(parser.GetObject(1));
        Assert.Null(PdfParser.LastObjectHeaderRecovery);
    }

    /// <summary>
    /// Object 5's entry aims at stream 9, whose <c>/Length</c> is object 10 — an entry outside the
    /// file with no header anywhere. Parsing 9 writes object 10's failure into the slot; object 5 is
    /// then read from its own header, a success, and the slot must say so: it kept object 10's message.
    /// </summary>
    [Fact]
    public void ANestedFailureDuringTheFirstAttempt_DoesNotOutliveTheRecovery()
    {
        var b = new FileBuilder();
        b.Obj(1, Catalog); b.Obj(2, Pages); b.Obj(3, Page); b.Stream(4, Benign);
        b.Obj(5, "<< /Marker 5 >>");
        b.Raw("9 0 obj\n<< /Length 10 0 R >>\nstream\nxx\nendstream\nendobj\n", 9);
        b.Xref(new Dictionary<int, (long Offset, int Gen)>
        {
            [1] = (b.Offsets[1], 0), [2] = (b.Offsets[2], 0), [3] = (b.Offsets[3], 0), [4] = (b.Offsets[4], 0),
            [5] = (b.Offsets[9], 0), [9] = (b.Offsets[9], 0), [10] = (9_000_000_000L, 0),
        }, size: 11);

        PdfParser parser = Parse(b.ToArray());
        Assert.Equal(5, Assert.IsType<PdfDictionary>(parser.GetObject(5)).GetInteger(new PdfName("Marker")));

        Assert.Null(PdfParser.LastObjectParseError);
        Assert.NotNull(PdfParser.LastObjectHeaderRecovery);
    }

    /// <summary>
    /// A table numbered one too low with no free head: objects 1 and 2 are misplaced and read from
    /// their headers, and object 3 has no entry at all. The first recovery fills the table's gaps from
    /// the headers, so object 3 reads rather than being absent with every slot null.
    /// </summary>
    [Fact]
    public void AShiftedTable_ReadsTheObjectItHasNoEntryFor_OnceARecoveryShowedItDamaged()
    {
        var b = new FileBuilder();
        b.Obj(1, Catalog); b.Obj(2, Pages); b.Obj(3, Page);
        b.Xref(new Dictionary<int, (long Offset, int Gen)> { [0] = (b.Offsets[1], 0), [1] = (b.Offsets[2], 0), [2] = (b.Offsets[3], 0) }, size: 4, freeHead: false);

        PdfParser parser = Parse(b.ToArray());
        Assert.Equal(PdfName.Catalog, Assert.IsType<PdfDictionary>(parser.GetObject(1)).GetName(PdfName.Type));

        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetName(PdfName.Type));
        Assert.True(parser.HeaderRecoveries >= 1);
    }

    /// <summary>
    /// Every object's <c>/Length</c> is the next object, a stream: one fetch parses the whole chain,
    /// four frames an object, and a long enough chain ran the stack out. The nesting stops at
    /// <see cref="PdfLimits.MaxObjectGraphDepth"/> with the reason, and the first object still reads.
    /// </summary>
    [Fact]
    public void AChainOfObjectsEachNeedingTheNext_IsBoundedInDepth()
    {
        const int Count = 2000;
        var b = new FileBuilder();
        var entries = new Dictionary<int, (long Offset, int Gen)>();
        for (int n = 1; n <= Count; n++)
        {
            b.Raw(n == Count
                ? $"{n} 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n"
                : $"{n} 0 obj\n<< /Length {n + 1} 0 R >>\nstream\n\nendstream\nendobj\n", n);
            entries[n] = (b.Offsets[n], 0);
        }
        b.Xref(entries, size: Count + 1);

        PdfParser parser = Parse(b.ToArray());
        PdfObject first = parser.GetObject(1);

        Assert.IsType<PdfStream>(first);
        Assert.True(parser.ObjectParses <= PdfLimits.MaxObjectGraphDepth + 2, $"{parser.ObjectParses} parses");
        Assert.Equal(1, parser.GetObject(Count - 1) is PdfStream ? 1 : 0);
    }

    /// <summary>
    /// Twelve objects each misaimed at a stream whose <c>/Filter</c> names the next failing object
    /// twice: without a memory of failures that is 2^12 parses of a few kilobytes. The failure is
    /// remembered with its reason, so each is parsed once.
    /// </summary>
    [Fact]
    public void AFailingObjectReferencedTwicePerLevel_IsParsedOnce_NotExponentially()
    {
        const int Levels = 12;
        var b = new FileBuilder();
        var entries = new Dictionary<int, (long Offset, int Gen)>();
        // Objects 100.. are the failing ones (no header anywhere); 200.. are the streams their entries aim at.
        for (int i = 0; i < Levels; i++)
        {
            int next = 100 + i + 1;
            string filter = i == Levels - 1 ? "" : $" /Filter [{next} 0 R {next} 0 R]";
            b.Raw($"{200 + i} 0 obj\n<< /Length 0{filter} >>\nstream\n\nendstream\nendobj\n", 200 + i);
            entries[200 + i] = (b.Offsets[200 + i], 0);
            entries[100 + i] = (b.Offsets[200 + i], 0);
        }
        b.Xref(entries, size: 300);

        PdfParser parser = Parse(b.ToArray());
        Assert.IsType<PdfNull>(parser.GetObject(100));

        Assert.Contains("holds obj 200", PdfParser.LastObjectParseError, StringComparison.Ordinal);
        Assert.True(parser.ObjectParses < 100, $"{parser.ObjectParses} parses");
        Assert.IsType<PdfNull>(parser.GetObject(100));
        Assert.Contains("holds obj 200", PdfParser.LastObjectParseError, StringComparison.Ordinal);
    }

    private static PdfParser Parse(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    private sealed class FileBuilder
    {
        private readonly MemoryStream _file = new MemoryStream();
        public readonly Dictionary<int, long> Offsets = new Dictionary<int, long>();

        public FileBuilder() { Write("%PDF-1.7\n"); }

        private void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); _file.Write(b, 0, b.Length); }

        public void Raw(string text, int objectNumber) { Offsets[objectNumber] = _file.Position; Write(text); }

        public void Obj(int n, string body) => Raw($"{n} 0 obj\n{body}\nendobj\n", n);

        public void Stream(int n, string data) => Raw($"{n} 0 obj\n<< /Length {data.Length} >>\nstream\n{data}\nendstream\nendobj\n", n);

        public void Xref(Dictionary<int, (long Offset, int Gen)> entries, int size, bool freeHead = true)
        {
            long xref = _file.Position;
            var sb = new StringBuilder("xref\n");
            var numbers = new List<int>(entries.Keys);
            numbers.Sort();
            if (freeHead && !entries.ContainsKey(0)) { sb.Append("0 1\n0000000000 65535 f \n"); }
            int i = 0;
            while (i < numbers.Count)
            {
                int start = numbers[i], run = 1;
                while (i + run < numbers.Count && numbers[i + run] == start + run) run++;
                sb.Append(start).Append(' ').Append(run).Append('\n');
                for (int k = 0; k < run; k++)
                {
                    var e = entries[start + k];
                    sb.AppendFormat("{0:D10} {1:D5} n \n", e.Offset, e.Gen);
                }
                i += run;
            }
            sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            Write(sb.ToString());
        }

        public byte[] ToArray() => _file.ToArray();
    }
}
