// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Tests.Constants;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// What a parser learns failed is remembered for as long as the table it was judged against: an
/// object stream whose decode fails is decoded once, not again, uncharged, for every member; and a
/// re-index that replaces the table forgets what failed under the old one.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class ObjectStreamFailureMemoTests
{
    private const int Members = 300;

    /// <summary>
    /// A container of <see cref="Members"/> members whose body inflates past the ceiling, behind a
    /// cross-reference stream naming each member as a type-2 entry.
    /// </summary>
    private static byte[] FailingContainer()
    {
        byte[] body;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(new byte[128 * 1024]);
            body = ms.ToArray();
        }

        var bytes = new List<byte>();
        void Text(string s) => bytes.AddRange(Encoding.Latin1.GetBytes(s));
        var offsets = new Dictionary<int, int>();
        Text("%PDF-1.5\n");
        offsets[1] = bytes.Count; Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = bytes.Count; Text("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        offsets[3] = bytes.Count;
        Text($"3 0 obj\n<< /Type /ObjStm /N {Members} /First 0 /Filter /FlateDecode /Length {body.Length} >>\nstream\n");
        bytes.AddRange(body);
        Text("\nendstream\nendobj\n");

        int size = 10 + Members;
        var rows = new List<byte>();
        void Row(byte type, int f2, int f3)
        {
            rows.Add(type);
            rows.Add((byte)(f2 >> 24)); rows.Add((byte)(f2 >> 16)); rows.Add((byte)(f2 >> 8)); rows.Add((byte)f2);
            rows.Add((byte)(f3 >> 8)); rows.Add((byte)f3);
        }
        offsets[4] = bytes.Count;
        for (int n = 0; n < size; n++)
        {
            if (n >= 1 && n <= 4) Row(1, n == 4 ? offsets[4] : offsets[n], 0);
            else if (n >= 10) Row(2, 3, n - 10);
            else Row(0, 0, n == 0 ? 65535 : 0);
        }
        Text($"4 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] /Root 1 0 R /Length {rows.Count} >>\nstream\n");
        bytes.AddRange(rows);
        Text($"\nendstream\nendobj\nstartxref\n{offsets[4]}\n%%EOF\n");
        return bytes.ToArray();
    }

    [Fact]
    public void AdversarialFailingObjectStream_IsDecodedOnce_NotOncePerMember()
    {
        byte[] pdf = FailingContainer();
        try
        {
            PdfLimits.MaxDecompressedBytes = 64 * 1024;
            var parser = new PdfParser(new MemoryStream(pdf));
            parser.ParseCrossReferences();

            parser.GetObject(10);   // the first member pays the one decode
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int n = 11; n < 10 + Members; n++)
            {
                var obj = parser.GetObject(n);
                Assert.True(obj == null || obj is PdfNull, $"member {n} was read from a stream that does not decode");
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // One decode reaches the 64 KiB ceiling; 299 of them were some 19 MB.
            Assert.True(allocated < 4L * 1024 * 1024, $"the other members allocated {allocated} bytes");
            Assert.NotNull(PdfParser.LastObjectParseError);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The cross-reference entry for object 1 points inside object 2's dictionary, so the first read
    /// of the catalog fails; the re-index that follows finds it by its own header and opens the file.
    /// Before, the failure memo survived the rebuild and the catalog was never found.
    /// </summary>
    [Fact]
    public void ACatalogWhoseFirstReadFailed_IsFound_ByTheReindex()
    {
        string head = "%PDF-1.4\n";
        string obj1 = "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n";
        string obj2 = "2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\n";
        // The xref is placed first; compute the offsets the objects will have after it.
        string Xref(int o1, int o2) =>
            "xref\n0 3\n0000000000 65535 f \n" + o1.ToString("D10") + " 00000 n \n" + o2.ToString("D10") + " 00000 n \n" +
            "trailer<</Size 3/Root 1 0 R>>\n";
        int xrefLength = Xref(0, 0).Length;
        int at1 = head.Length + xrefLength, at2 = at1 + obj1.Length;
        int inside2 = at2 + 12;   // inside object 2's dictionary
        string pdf = head + Xref(inside2, at2) + obj1 + obj2 + "startxref\n" + head.Length + "\n%%EOF\n";

        using var doc = PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(pdf)));
        Assert.NotNull(doc.Catalog);
        Assert.NotNull(PdfParser.LastRootRecovery);
        Assert.Contains("1", PdfParser.LastRootRecovery, StringComparison.Ordinal);
    }
}
